using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ClassIsland.LiquidGlass.Rendering;

/// <summary>
/// D3D11 那一侧：抓桌面、跑玻璃着色器、把结果写进一张<b>共享纹理</b>。
/// </summary>
/// <remarks>
/// 共享纹理是和 Avalonia 之间唯一的接触面。要点：
/// <list type="bullet">
/// <item>必须建在<b>和 Avalonia 同一块显卡</b>上。
///       <c>ICompositionGpuInterop.DeviceLuid</c> 给的就是那块卡的 LUID，
///       多显卡机器上挑错卡，导入会直接失败。</item>
/// <item>必须带 <c>SharedKeyedmutex</c>。两个设备轮流写读同一块显存，
///       没有键控互斥就是数据竞争，表现为闪烁或撕裂。</item>
/// </list>
/// </remarks>
internal sealed class GlassSource : IDisposable
{
    private const uint KeyRenderer = 0;   // 轮到我们画
    private const uint KeyConsumer = 1;   // 轮到 Avalonia 读

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11Buffer _cb;

    private IDXGIOutputDuplication? _dupl;

    // 正在复制的那台显示器：序号与它在虚拟桌面里的矩形。
    private int _outIndex = -1;
    private int _outLeft;
    private int _outTop;
    private int _outW;
    private int _outH;

    private ID3D11Texture2D? _desktop;
    private ID3D11ShaderResourceView? _desktopSrv;
    private int _desktopW;
    private int _desktopH;

    private ID3D11ShaderResourceView? _blankSrv;
    // 裁剪 + 两趟模糊的中间纹理。裁到条形附近再模糊，
    // 比在整屏上做便宜两个数量级。
    private ID3D11Texture2D? _crop;
    private ID3D11ShaderResourceView? _cropSrv;
    private ID3D11RenderTargetView? _cropRtv;
    private ID3D11Texture2D? _tmp;
    private ID3D11ShaderResourceView? _tmpSrv;
    private ID3D11RenderTargetView? _tmpRtv;
    private ID3D11Texture2D? _blurred;
    private ID3D11ShaderResourceView? _blurSrv;
    private ID3D11RenderTargetView? _blurRtv;
    private int _cropW;
    private int _cropH;
    private ID3D11Texture2D? _stats;
    private ID3D11ShaderResourceView? _statsSrv;
    private ID3D11RenderTargetView? _statsRtv;
    private ID3D11PixelShader? _statsPs;
    private ID3D11PixelShader? _blurPs;
    private ID3D11Buffer? _blurCb;
    private ID3D11PixelShader? _cropPs;
    private ID3D11Buffer? _cropCb;

    private ID3D11Texture2D? _shared;
    private ID3D11RenderTargetView? _rtv;
    private IDXGIKeyedMutex? _mutex;

    public IntPtr SharedHandle { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public string Status { get; private set; } = "";

    public GlassSource(byte[]? targetLuid)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? chosen = null;

        // 按 LUID 挑到 Avalonia 用的那块卡。
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            if (targetLuid is null)
            {
                chosen = adapter;
                break;
            }

            var luid = adapter.Description1.Luid;
            var bytes = BitConverter.GetBytes(luid);
            if (bytes.AsSpan(0, Math.Min(8, targetLuid.Length))
                .SequenceEqual(targetLuid.AsSpan(0, Math.Min(8, targetLuid.Length))))
            {
                chosen = adapter;
                break;
            }

            adapter.Dispose();
        }

        var hr = D3D11.D3D11CreateDevice(chosen, chosen is null ? DriverType.Hardware : DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out _device!, out _, out _context!);
        chosen?.Dispose();

        if (hr.Failure)
        {
            throw new InvalidOperationException($"创建 D3D11 设备失败 {hr}");
        }

        var vsBlob = Compile("VSMain", "vs_5_0");
        var psBlob = Compile("PSMain", "ps_5_0");
        _vs = _device.CreateVertexShader(vsBlob.AsSpan());
        _ps = _device.CreatePixelShader(psBlob.AsSpan());
        vsBlob.Dispose();
        psBlob.Dispose();

        _sampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxLOD = float.MaxValue
        });

        var statsBlob = Compile("PSStats", "ps_5_0");
        _statsPs = _device.CreatePixelShader(statsBlob.AsSpan());
        statsBlob.Dispose();

        var blurBlob = Compile("PSBlur", "ps_5_0");
        _blurPs = _device.CreatePixelShader(blurBlob.AsSpan());
        blurBlob.Dispose();

        var cropBlob = Compile("PSCrop", "ps_5_0");
        _cropPs = _device.CreatePixelShader(cropBlob.AsSpan());
        cropBlob.Dispose();

        _cropCb = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)((Marshal.SizeOf<CropCb>() + 15) / 16 * 16),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ConstantBuffer
        });

        _blurCb = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)((Marshal.SizeOf<BlurCb>() + 15) / 16 * 16),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ConstantBuffer
        });

        _cb = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)((Marshal.SizeOf<GlassCb>() + 15) / 16 * 16),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ConstantBuffer
        });

        // 抓不到桌面时的占位纹理。没有它，着色器就没有资源可绑，
        // 整个 Render 会提前退出——而提前退出意味着<b>键控互斥没有交还</b>，
        // 消费端会一直等下去。
        using (var blank = _device.CreateTexture2D(new Texture2DDescription
               {
                   Width = 1, Height = 1, MipLevels = 1, ArraySize = 1,
                   Format = Format.B8G8R8A8_UNorm,
                   SampleDescription = new SampleDescription(1, 0),
                   Usage = ResourceUsage.Default,
                   BindFlags = BindFlags.ShaderResource
               }, new SubresourceData(_blankPixel, 4)))
        {
            _blankSrv = _device.CreateShaderResourceView(blank);
        }

        // 桌面复制要等第一帧知道状态栏在哪块屏上才启动，见 Capture。
    }

    private static readonly byte[] _blankPixelData = [40, 40, 46, 255];
    private static readonly System.Runtime.InteropServices.GCHandle _blankPin =
        System.Runtime.InteropServices.GCHandle.Alloc(_blankPixelData,
            System.Runtime.InteropServices.GCHandleType.Pinned);
    private static IntPtr _blankPixel => _blankPin.AddrOfPinnedObject();

    private static Blob Compile(string entry, string profile)
    {
        var res = Compiler.Compile(GlassHlsl.Source, entry, "glass.hlsl", profile,
            out var blob, out var errors);
        if (res.Failure || blob is null)
        {
            throw new InvalidOperationException($"{entry} 编译失败：{errors?.AsString()}");
        }

        return blob;
    }

    /// <summary>在包含指定屏幕点的那台显示器上启动桌面复制。</summary>
    /// <remarks>
    /// ⚠ 桌面复制是<b>按显示器</b>的：交出来的纹理，坐标从<b>那台显示器自己的左上角</b>算起。
    /// 而 Avalonia 的 <c>PointToScreen</c> 给的是<b>整个虚拟桌面</b>的坐标。
    /// 单屏、且这块屏就在虚拟桌面原点时两者恰好相同——所以单屏机器上一直没露馅；
    /// 一旦接了第二块屏，或主屏被摆在别的屏右边，差值就是这块屏
    /// <c>DesktopCoordinates</c> 的左上角。不减掉的话，折射采的是另一处的画面，
    /// 看着就是「折射位置整体偏了」。
    /// <para/>
    /// 教室大屏平时单屏，接投影或笔记本时就会变成多屏，所以不能按单屏写死。
    /// </remarks>
    private void StartDuplication(int screenX, int screenY)
    {
        try
        {
            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();

            // 先只看矩形挑序号，再单独取那一个。
            // 边枚举边留着接口对象，出错路径上很容易漏掉 Dispose。
            var count = 0;
            var chosen = 0;
            var hit = false;
            for (uint i = 0; adapter.EnumOutputs(i, out var probe).Success; i++)
            {
                var r = probe.Description.DesktopCoordinates;
                probe.Dispose();
                count++;
                if (!hit && screenX >= r.Left && screenX < r.Right &&
                    screenY >= r.Top && screenY < r.Bottom)
                {
                    chosen = (int)i;
                    hit = true;
                }
            }

            if (count == 0)
            {
                Status = "没有找到可复制的显示器";
                return;
            }

            adapter.EnumOutputs((uint)chosen, out var output);
            using var target = output;
            var rect = target.Description.DesktopCoordinates;
            _outLeft = rect.Left;
            _outTop = rect.Top;
            _outW = rect.Right - rect.Left;
            _outH = rect.Bottom - rect.Top;
            _outIndex = chosen;

            using var output1 = target.QueryInterface<IDXGIOutput1>();
            _dupl = output1.DuplicateOutput(_device);
            Status = hit
                ? $"桌面复制已启动：{chosen} 号屏 {_outW}×{_outH} @({_outLeft},{_outTop})"
                : $"桌面复制已启动：状态栏不在任何一块屏范围内，退回 {chosen} 号屏";
        }
        catch (Exception ex)
        {
            Status = "桌面复制不可用：" + ex.Message;
        }
    }

    /// <summary>停掉当前这条复制，下一帧重建。</summary>
    private void StopDuplication()
    {
        try
        {
            _dupl?.ReleaseFrame();
        }
        catch (Exception)
        {
        }

        _dupl?.Dispose();
        _dupl = null;
        _outIndex = -1;
    }

    /// <summary>桌面没变化时 AcquireNextFrame 的正常返回，不是故障。</summary>
    private const int DxgiWaitTimeout = unchecked((int)0x887A0027);

    /// <summary>投影用的留边（设备像素）。纹理比胶囊本身大出这一圈。</summary>
    public int ShadowPad { get; private set; }

    /// <summary>
    /// 按胶囊尺寸准备共享纹理。尺寸变了就重建。
    /// </summary>
    /// <remarks>
    /// 纹理是<b>胶囊加一圈留边</b>：投影落在胶囊外面，没有留边就没地方画。
    /// 留边取自规格：软投影偏移 0.10h、σ 0.22h，三倍 σ 覆盖到 99%。
    /// </remarks>
    public bool EnsureTarget(int w, int h)
    {
        if (w < 2 || h < 2)
        {
            return false;
        }

        var pad = (int)Math.Ceiling(0.22 * h * 3 + 0.10 * h) + 2;
        if (_shared is not null && Width == w && Height == h && ShadowPad == pad)
        {
            return true;
        }

        ShadowPad = pad;

        _rtv?.Dispose();
        _mutex?.Dispose();
        _shared?.Dispose();

        _shared = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)(w + pad * 2),
            Height = (uint)(h + pad * 2),
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            // 共享 + 键控互斥，缺一不可。
            MiscFlags = ResourceOptionFlags.SharedKeyedMutex
        });

        _rtv = _device.CreateRenderTargetView(_shared);
        _mutex = _shared.QueryInterface<IDXGIKeyedMutex>();

        using var dxgiRes = _shared.QueryInterface<IDXGIResource>();
        SharedHandle = dxgiRes.SharedHandle;

        Width = w;
        Height = h;
        return true;
    }

    /// <summary>按需建立裁剪与模糊用的中间纹理。</summary>
    private void EnsureCrop(int w, int h)
    {
        if (_crop is not null && _cropW == w && _cropH == h)
        {
            return;
        }

        DisposeCrop();
        _cropW = w;
        _cropH = h;

        var desc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
        };

        _crop = _device.CreateTexture2D(desc);
        _cropSrv = _device.CreateShaderResourceView(_crop);
        _cropRtv = _device.CreateRenderTargetView(_crop);

        _tmp = _device.CreateTexture2D(desc);
        _tmpSrv = _device.CreateShaderResourceView(_tmp);
        _tmpRtv = _device.CreateRenderTargetView(_tmp);

        _blurred = _device.CreateTexture2D(desc);
        _blurSrv = _device.CreateShaderResourceView(_blurred);
        _blurRtv = _device.CreateRenderTargetView(_blurred);

        // 统计用：两通道浮点，带完整 mip 链。
        // 逐级平均下去，最小那一级就是整块的 E[x] 和 E[x²]。
        _stats = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 0,               // 0 = 一直生成到 1x1
            ArraySize = 1,
            Format = Format.R16G16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            MiscFlags = ResourceOptionFlags.GenerateMips
        });
        _statsSrv = _device.CreateShaderResourceView(_stats);
        _statsRtv = _device.CreateRenderTargetView(_stats);
    }

    private void DisposeCrop()
    {
        _cropRtv?.Dispose(); _cropSrv?.Dispose(); _crop?.Dispose();
        _tmpRtv?.Dispose(); _tmpSrv?.Dispose(); _tmp?.Dispose();
        _blurRtv?.Dispose(); _blurSrv?.Dispose(); _blurred?.Dispose();
        _statsRtv?.Dispose(); _statsSrv?.Dispose(); _stats?.Dispose();
        _stats = null; _statsSrv = null; _statsRtv = null;
        _crop = _tmp = _blurred = null;
        _cropSrv = _tmpSrv = _blurSrv = null;
        _cropRtv = _tmpRtv = _blurRtv = null;
    }

    /// <summary>一趟模糊。</summary>
    private void BlurPass(ID3D11ShaderResourceView src, ID3D11RenderTargetView dst,
        Vector2 dir, float sigma)
    {
        _context.UpdateSubresource(new BlurCb
        {
            Texel = new Vector2(1f / _cropW, 1f / _cropH),
            Dir = dir,
            Sigma = sigma
        }, _blurCb!);

        _context.OMSetRenderTargets(dst);
        _context.RSSetViewport(0, 0, _cropW, _cropH);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vs);
        _context.PSSetShader(_blurPs);
        _context.PSSetConstantBuffer(1, _blurCb);
        _context.PSSetShaderResource(0, src);
        _context.PSSetSampler(0, _sampler);
        _context.Draw(3, 0);

        // 解绑，否则下一趟把它同时当输入和输出会被驱动拒绝。
        _context.PSSetShaderResource(0, (ID3D11ShaderResourceView?)null);
    }

    /// <summary>抓一帧桌面。抓不到就沿用上一帧。</summary>
    /// <param name="screenX">状态栏左上角在虚拟桌面里的横坐标。</param>
    /// <param name="screenY">纵坐标。用来判断该复制哪一块屏。</param>
    public void Capture(int screenX, int screenY)
    {
        // 状态栏被挪到另一块屏上了，换一台显示器复制。
        if (_dupl is not null && _outW > 0 &&
            (screenX < _outLeft || screenX >= _outLeft + _outW ||
             screenY < _outTop || screenY >= _outTop + _outH))
        {
            StopDuplication();
        }

        if (_dupl is null)
        {
            StartDuplication(screenX, screenY);
            if (_dupl is null)
            {
                return;
            }
        }

        try
        {
            var res = _dupl.AcquireNextFrame(8, out var info, out var resource);
            if (res.Failure)
            {
                // ⚠ 超时是常态（屏幕没变化），其余一律当作这条复制作废。
                // 切换全屏程序、改分辨率、DWM 重启、锁屏回来都会让复制失效
                // （DXGI_ERROR_ACCESS_LOST），而失效之后每一次抓帧都失败——
                // 只 return 的话画面就<b>永远停在失效前的那一帧</b>：
                // 玻璃里折射的还是几分钟前的桌面，和背后实际显示的内容对不上。
                if (res.Code != DxgiWaitTimeout)
                {
                    StopDuplication();
                }

                return;
            }

            if (info.LastPresentTime == 0)
            {
                resource.Dispose();
                _dupl.ReleaseFrame();
                return;
            }

            using var acquired = resource.QueryInterface<ID3D11Texture2D>();
            resource.Dispose();

            var d = acquired.Description;

            // ⚠ 必须先把这一帧<b>拷进自己的纹理</b>再 ReleaseFrame。
            // 桌面复制交出来的纹理在释放之后就失效了，继续拿它当着色器资源
            // 采到的是已回收的显存——现象就是整块全黑，而且不报任何错。
            if (_desktop is null || _desktopW != (int)d.Width || _desktopH != (int)d.Height)
            {
                _desktopSrv?.Dispose();
                _desktop?.Dispose();
                _desktopW = (int)d.Width;
                _desktopH = (int)d.Height;
                _desktop = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = d.Width,
                    Height = d.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = d.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource
                });
                _desktopSrv = _device.CreateShaderResourceView(_desktop);
            }

            _context.CopyResource(_desktop, acquired);
            _dupl.ReleaseFrame();
        }
        catch (Exception)
        {
            // 抓帧出异常同样按失效处理，下一帧重建；这一帧沿用上一帧，不打断渲染。
            StopDuplication();
        }
    }

    /// <summary>把玻璃画进共享纹理。<paramref name="originPx"/> 是条形在桌面纹理里的物理左上角。</summary>
    public bool Render(Vector2 originPx, float radius, float thickness, float lensing,
        float scaling, bool isDark, float chromatic, float tintAmount, float shadowAlpha,
        float clarity)
    {
        if (_shared is null || _rtv is null || _mutex is null)
        {
            return false;
        }

        // 没抓到桌面就用占位纹理，并把折射关掉——但这一帧照样要画、照样要交还互斥。
        var srv = _desktopSrv is null ? _blankSrv : _blurSrv;
        if (_desktopSrv is null)
        {
            lensing = 0f;
        }

        // ---- 裁剪 + 两趟模糊 ----
        // 留边要盖住位移和模糊两者的取样范围，否则边缘会取到裁剪框外，
        // 出现一圈暗边或透明晕。
        var sigma = Math.Min(0.45f * thickness, 12f);
        var pad = (int)Math.Ceiling(thickness * 1.6f + sigma * 2.5f) + 4;
        EnsureCrop(Width + pad * 2, Height + pad * 2);

        if (_desktop is not null && _cropRtv is not null)
        {
            // 虚拟桌面坐标 → 这块屏的纹理坐标。
            // 比例正常是 1；不是 1 说明纹理尺寸和系统报的屏幕矩形对不上
            // （异常缩放等），按比例换算比直接用原值稳。
            var sx = _outW > 0 ? (float)_desktopW / _outW : 1f;
            var sy = _outH > 0 ? (float)_desktopH / _outH : 1f;
            sx = sx is < 0.25f or > 8f ? 1f : sx;
            sy = sy is < 0.25f or > 8f ? 1f : sy;

            var l = (int)((originPx.X - _outLeft) * sx) - pad;
            var t = (int)((originPx.Y - _outTop) * sy) - pad;
            Geometry = $"{_outIndex} 号屏 {_outW}×{_outH}@({_outLeft},{_outTop})"
                       + $"；纹理 {_desktopW}×{_desktopH}"
                       + (sx == 1f && sy == 1f ? "" : $"；换算 {sx:F2}×{sy:F2}")
                       + $"；裁剪原点 ({l},{t})";

            // ⚠ 裁剪走着色器，不走 CopySubresourceRegion。
            // 状态栏贴在 y=0，裁剪框<b>必然</b>超出屏幕上边界；
            // 「清空 + 只拷重叠区」会在框外留下一圈透明黑，
            // 模糊和边缘折射再把这片黑拖进条子里，上沿就是一道暗带。
            // 用 Clamp 取样器采样等于边缘延拓，框外补的是最近的真实像素。
            _context.UpdateSubresource(new CropCb
            {
                Origin = new Vector2(l, t),
                Size = new Vector2(_cropW, _cropH),
                Desk = new Vector2(_desktopW, _desktopH)
            }, _cropCb!);

            _context.OMSetRenderTargets(_cropRtv);
            _context.RSSetViewport(0, 0, _cropW, _cropH);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.VSSetShader(_vs);
            _context.PSSetShader(_cropPs);
            _context.PSSetConstantBuffer(2, _cropCb);
            _context.PSSetShaderResource(0, _desktopSrv);
            _context.PSSetSampler(0, _sampler);
            _context.Draw(3, 0);
            _context.PSSetShaderResource(0, (ID3D11ShaderResourceView?)null);

            BlurPass(_cropSrv!, _tmpRtv!, new Vector2(1, 0), sigma);
            BlurPass(_tmpSrv!, _blurRtv!, new Vector2(0, 1), sigma);

            // 统计跑在<b>未模糊</b>的裁剪上：模糊会把对比度抹平，
            // 拿模糊图算出来的 rms 会严重偏低，动态范围压缩就等于没做。
            _context.OMSetRenderTargets(_statsRtv);
            _context.RSSetViewport(0, 0, _cropW, _cropH);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.VSSetShader(_vs);
            _context.PSSetShader(_statsPs);
            _context.PSSetShaderResource(0, _cropSrv);
            _context.PSSetSampler(0, _sampler);
            _context.Draw(3, 0);
            _context.PSSetShaderResource(0, (ID3D11ShaderResourceView?)null);
            _context.GenerateMips(_statsSrv);
        }

        var pars = new GlassCb
        {
            BarSize = new Vector2(Width, Height),
            // 主着色器现在在裁剪空间里取样：条形左上角就在 (pad, pad)。
            BarOriginUV = new Vector2((float)pad / _cropW, (float)pad / _cropH),
            DesktopSize = new Vector2(_cropW, _cropH),
            Radius = radius,
            Thickness = thickness,
            LightDir = new Vector2(MathF.Cos(-0.96f), MathF.Sin(-0.96f)),
            LightIntensity = 0.28f,
            Ambient = 0.15f,
            Chromatic = chromatic,
            RefractiveIndex = 1.5f,
            BackgroundDistance = 0.8f,
            TintAmount = tintAmount,
            Tint = new Vector3(1, 1, 1),
            Lensing = lensing,
            Scaling = scaling,
            IsDark = isDark ? 1f : 0f,
            Pad = ShadowPad,
            ShadowAlpha = shadowAlpha,
            Clarity = clarity
        };

        _mutex.AcquireSync(KeyRenderer, 1000);
        try
        {
            _context.UpdateSubresource(pars, _cb);
            _context.OMSetRenderTargets(_rtv);
            _context.RSSetViewport(0, 0, Width + ShadowPad * 2, Height + ShadowPad * 2);
            _context.ClearRenderTargetView(_rtv, new Color4(0, 0, 0, 0));
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.VSSetShader(_vs);
            _context.PSSetShader(_ps);
            _context.PSSetConstantBuffer(0, _cb);
            _context.PSSetShaderResource(0, srv);
            _context.PSSetShaderResource(1, _statsSrv);
            // 未模糊的那张也要给主着色器：散射是<b>随光程变化</b>的，
            // 中心是薄平板、光直穿，本来就该清晰；只有边缘倒角处光程长才该散开。
            // 只给模糊图的话，整块都是毛玻璃。
            _context.PSSetShaderResource(2, _desktopSrv is null ? _blankSrv : _cropSrv);
            _context.PSSetSampler(0, _sampler);
            _context.Draw(3, 0);
            _context.Flush();
        }
        finally
        {
            // 交还给 Avalonia。这一步<b>绝不能跳过</b>，
            // 少交还一次，消费端就永远卡在 Acquire 上。
            _mutex.ReleaseSync(KeyConsumer);
        }

        return true;
    }

    /// <summary>诊断用：这一帧实际从哪块屏、哪个位置取的背景。</summary>
    public string Geometry { get; private set; } = "";

    /// <summary>诊断用：是否已经拿到过桌面帧。</summary>
    public bool HasDesktop => _desktopSrv is not null;

    public int DesktopWidth => _desktopW;

    public int DesktopHeight => _desktopH;

    /// <summary>
    /// 把共享纹理原样导出成 BGRA 裸数据。
    /// </summary>
    /// <remarks>
    /// 状态栏被排除在屏幕捕获之外（否则折射会套娃），所以<b>截屏看不到它</b>。
    /// 排查观感只能从显存直接回读。由配置目录下的 <c>dump.on</c> 标记触发，
    /// 平时不产生任何开销。
    /// </remarks>
    public void Dump(string path)
    {
        if (_shared is null || _mutex is null)
        {
            return;
        }

        using var staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)(Width + ShadowPad * 2), Height = (uint)(Height + ShadowPad * 2),
            MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read
        });

        _mutex.AcquireSync(KeyRenderer, 1000);
        try
        {
            _context.CopyResource(staging, _shared);
        }
        finally
        {
            _mutex.ReleaseSync(KeyRenderer);
        }

        var map = _context.Map(staging, 0, Vortice.Direct3D11.MapMode.Read);
        try
        {
            var fw = Width + ShadowPad * 2;
            var fh = Height + ShadowPad * 2;
            var bytes = new byte[fw * fh * 4];
            unsafe
            {
                var src = (byte*)map.DataPointer;
                for (var y = 0; y < fh; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(
                        (IntPtr)(src + y * (int)map.RowPitch), bytes, y * fw * 4, fw * 4);
                }
            }

            System.IO.File.WriteAllBytes(path, bytes);
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    public void Dispose()
    {
        _rtv?.Dispose();
        _mutex?.Dispose();
        _shared?.Dispose();
        DisposeCrop();
        _blurCb?.Dispose();
        _blurPs?.Dispose();
        _statsPs?.Dispose();
        _blankSrv?.Dispose();
        _desktopSrv?.Dispose();
        _desktop?.Dispose();
        _dupl?.Dispose();
        _cb.Dispose();
        _sampler.Dispose();
        _ps.Dispose();
        _vs.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct BlurCb
{
    public Vector2 Texel;
    public Vector2 Dir;
    public float Sigma;
    public Vector3 Pad;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CropCb
{
    public Vector2 Origin;
    public Vector2 Size;
    public Vector2 Desk;
    public Vector2 Spare;
}

internal struct GlassCb
{
    public Vector2 BarSize;
    public Vector2 BarOriginUV;
    public Vector2 DesktopSize;
    public float Radius;
    public float Thickness;
    public Vector2 LightDir;
    public float LightIntensity;
    public float Ambient;
    public float Chromatic;
    public float RefractiveIndex;
    public float BackgroundDistance;
    public float TintAmount;
    public Vector3 Tint;
    public float Lensing;
    public float Scaling;
    public float IsDark;
    public float Pad;
    public float ShadowAlpha;
    public float Clarity;
}
