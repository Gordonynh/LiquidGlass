using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using Avalonia.Threading;
using ClassIsland.LiquidGlass.Models;

namespace ClassIsland.LiquidGlass.Rendering;

/// <summary>
/// 液态玻璃控件。摆在状态栏背景那一层。
/// </summary>
/// <remarks>
/// 两条路，优先 GPU：
/// <list type="number">
/// <item><b>GPU</b>：DXGI 桌面复制拿到整屏（含所有窗口）→ D3D11 着色器做折射 →
///       共享纹理 → Avalonia 的 <c>ICompositionGpuInterop</c> 导入 → 合成树。
///       全程不回 CPU，实测 0.3ms 一帧，能折射背后<b>任何</b>内容。</item>
/// <item><b>CPU</b>：<see cref="GlassRenderer"/> 逐像素算好缓存成位图。
///       没有背景可采，只有玻璃外壳，但在软件渲染下也能用。</item>
/// </list>
/// Avalonia 落到软件渲染时（虚拟机、远程桌面、驱动异常）拿不到 GPU 互操作，
/// 自动退到第二条。
/// </remarks>
public class LiquidGlassBorder : Control
{
    private LiquidGlassSettings Settings => LiquidGlassSettings.Current;

    // ---- GPU 路 ----
    private GlassSource? _source;
    private ICompositionGpuInterop? _interop;
    private CompositionDrawingSurface? _surface;
    private CompositionSurfaceVisual? _visual;
    private ICompositionImportedGpuImage? _imported;
    private IntPtr _importedHandle;
    private DispatcherTimer? _timer;
    private bool _busy;
    private bool _gpuFailed;

    // ---- CPU 退路 ----
    private WriteableBitmap? _cache;
    private PixelSize _cacheSize;
    private GlassParams _cacheParams;

    public bool UsingGpu => _surface is not null && !_gpuFailed;

    public LiquidGlassBorder()
    {
        // 投影画在控件边界之外，被裁掉就白做了。
        ClipToBounds = false;
    }

    /// <summary>
    /// 运行状态。排除屏幕捕获之后这个控件在截图里是看不见的，
    /// 出问题时没法靠截屏排查，所以它必须自己说话。
    /// </summary>
    public static string Diagnostics { get; private set; } = "尚未启动";

    private static readonly System.Collections.Generic.Queue<string> Recent = new();

    private static void Report(string s)
    {
        Diagnostics = $"{DateTime.Now:HH:mm:ss} {s}";

        // 只留最近这些行。这个文件常驻整天，不能一直追加。
        lock (Recent)
        {
            Recent.Enqueue(Diagnostics);
            while (Recent.Count > 20)
            {
                Recent.Dequeue();
            }

            try
            {
                if (LiquidGlassSettings.ConfigFolder.Length > 0)
                {
                    System.IO.Directory.CreateDirectory(LiquidGlassSettings.ConfigFolder);
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(LiquidGlassSettings.ConfigFolder, "status.txt"),
                        string.Join(Environment.NewLine, Recent));
                }
            }
            catch (Exception)
            {
            }
        }
    }

    private int _frames;

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Report("控件已挂载");

        if (!Settings.UseGpu)
        {
            Report("折射已关闭，只画外壳");
            return;
        }

        try
        {
            await SetupGpuAsync();
        }
        catch (Exception ex)
        {
            Report("GPU 初始化失败，退回 CPU 外壳：" + ex.Message);
            _gpuFailed = true;
            InvalidateVisual();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
        _timer = null;
        _source?.Dispose();
        _source = null;
    }

    private async Task SetupGpuAsync()
    {
        var self = ElementComposition.GetElementVisual(this);
        if (self is null)
        {
            _gpuFailed = true;
            return;
        }

        var compositor = self.Compositor;
        _interop = await compositor.TryGetCompositionGpuInterop();

        var wanted = KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle;
        if (_interop is null || !_interop.SupportedImageHandleTypes.Contains(wanted))
        {
            // 多半是落到了软件渲染。退回 CPU 外壳。
            _gpuFailed = true;
            Report(_interop is null
                ? "拿不到 GPU 互操作（多半落到了软件渲染），退回 CPU 外壳"
                : "不支持 D3D11 共享句柄，退回 CPU 外壳");
            Dispatcher.UIThread.Post(InvalidateVisual);
            return;
        }

        ExcludeFromCapture();

        // 必须和 Avalonia 用同一块显卡，否则共享纹理导入会失败。
        _source = new GlassSource(_interop.DeviceLuid);
        _surface = compositor.CreateDrawingSurface();
        _visual = compositor.CreateSurfaceVisual();
        _visual.Surface = _surface;
        ElementComposition.SetElementChildVisual(this, _visual);

        // ⚠ 这里的续体跑在合成线程上，不是 UI 线程。
        // 在合成线程上建的 DispatcherTimer 挂在不泵消息的线程上，Tick 永远不来，
        // 而且不报任何错——整条管线会一声不吭地什么都不做。
        Dispatcher.UIThread.Post(() =>
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(Settings.FrameRate, 5, 120))
            };
            _timer.Tick += (_, _) => Frame();
            _timer.Start();
            Report($"GPU 折射已启动，{Settings.FrameRate:F0} Hz");
        });
    }

    private async void Frame()
    {
        if (_busy || _source is null || _surface is null || _interop is null || _visual is null)
        {
            return;
        }

        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        var scaling = top.RenderScaling;
        var w = (int)Math.Ceiling(Bounds.Width * scaling);
        var h = (int)Math.Ceiling(Bounds.Height * scaling);
        if (!_source.EnsureTarget(w, h))
        {
            return;
        }

        // 视觉要覆盖胶囊加投影留边，并往左上偏一个留边，胶囊才落回原位。
        // 留边由 GlassSource 按条高算出来，所以必须先 EnsureTarget 再读。
        var padDip = _source.ShadowPad / scaling;

        // ⚠ 合成视觉的 Size 是<b>逻辑单位（DIP）</b>，纹理是<b>物理像素</b>。
        // 传物理尺寸进去，2× 缩放下整块会被放大一倍：
        // 尺寸不对、圆角比例跟着错、位置看着偏，
        // 看到的还是被放大的纹理——像是「折射了周围的内容并且完全扭曲」。
        // 纹理保持物理分辨率是为了清晰，合成器负责缩到逻辑尺寸。
        _visual.Size = new Vector2(
            (float)(Bounds.Width + padDip * 2), (float)(Bounds.Height + padDip * 2));
        _visual.Offset = new Vector3((float)-padDip, (float)-padDip, 0);

        var origin = this.TranslatePoint(new Point(0, 0), top) ?? new Point(0, 0);
        var screen = top.PointToScreen(origin);

        // ⚠ 排除标志会被冲掉。实测：设完立刻回读是 17，过一会儿再查就变回 0——
        // ClassIsland 自己在管理窗口特性：MainWindow.UpdateWindowFeatures 会按
        // 「阻止窗口捕获」这个设置把 DisplayAffinity 重设成 WDA_NONE，
        // 而它挂在窗口激活事件上——<b>用户点开别的程序就会触发</b>。
        // 一旦失效，折射采到的就是状态栏自己，逐帧套娃，
        // 画面上表现为内容被反复位移放大。
        //
        // 原来一秒补一次，最坏会留下整整一秒的套娃窗口。改成每帧查一次：
        // 先回读、已生效就不动，两次 user32 调用，开销可以忽略。
        ReapplyCaptureExclusion();

        _busy = true;
        try
        {
            _source.Capture(screen.X, screen.Y);

            var radius = (float)Math.Min(Settings.CornerRadius * scaling, h / 2.0);
            if (Settings.RoundToCapsule)
            {
                radius = h / 2f;
            }

            if (!_source.Render(new Vector2(screen.X, screen.Y), radius,
                    (float)(Settings.Thickness * scaling), (float)Settings.Lensing,
                    (float)scaling, Settings.IsDark,
                    (float)Settings.Chromatic, (float)Settings.TintAmount,
                    (float)Settings.ShadowAlpha, (float)Settings.Clarity))
            {
                return;
            }

            if (_imported is null || _importedHandle != _source.SharedHandle)
            {
                _importedHandle = _source.SharedHandle;
                _imported = _interop.ImportImage(
                    new PlatformHandle(_importedHandle,
                        KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle),
                    new PlatformGraphicsExternalImageProperties
                    {
                        Width = w + _source.ShadowPad * 2,
                        Height = h + _source.ShadowPad * 2,
                        Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
                        TopLeftOrigin = true
                    });
            }

            // 键控互斥：渲染侧 Release(1)，这里 Acquire(1)、用完 Release(0) 交回。
            await _surface.UpdateWithKeyedMutexAsync(_imported, 1, 0);




            // 配置目录里放一个 dump.on 就导出一帧，用来在看不到屏幕时排查观感。
            if (_frames == 90 && LiquidGlassSettings.ConfigFolder.Length > 0 &&
                System.IO.File.Exists(
                    System.IO.Path.Combine(LiquidGlassSettings.ConfigFolder, "dump.on")))
            {
                try
                {
                    var fw = w + _source.ShadowPad * 2;
                    var fh = h + _source.ShadowPad * 2;
                    _source.Dump(System.IO.Path.Combine(
                        LiquidGlassSettings.ConfigFolder, $"glass_{fw}x{fh}.bgra"));
                    Report($"已按 dump.on 导出一帧 {fw}×{fh}（含留边 {_source.ShadowPad}）");
                }
                catch (Exception ex)
                {
                    Report("导出失败：" + ex.Message);
                }
            }

            if (++_frames % 900 == 0)
            {
                Report($"GPU 折射运行中，已推 {_frames} 帧"
                       + $"；控件 {Bounds.Width:F0}×{Bounds.Height:F0} 逻辑"
                       + $" → {w}×{h} 物理，缩放 {scaling:F2}"
                       + $"；窗口内偏移 ({origin.X:F0},{origin.Y:F0})"
                       + $"；屏幕位置 ({screen.X},{screen.Y})"
                       + $"；宿主不透明度 {HostOpacity():F2}"
                       + $"；桌面帧：{(_source.HasDesktop ? "有" : "无")}"
                       + $"；{_source.Geometry}"
                       + $"；{_source.Status}");
            }
        }
        catch (Exception ex)
        {
            // GPU 路出问题就整条关掉，退回 CPU 外壳，别把宿主拖下水。
            Report("GPU 折射出错，退回 CPU 外壳：" + ex.Message);
            _gpuFailed = true;
            _timer?.Stop();
            _timer = null;
            Dispatcher.UIThread.Post(InvalidateVisual);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// 这一整条视觉链上实际生效的不透明度。
    /// </summary>
    /// <remarks>
    /// 必须是<b>量出来</b>的，不能假定样式生效了。
    /// ClassIsland 把承载玻璃的那块背景板的 Opacity 绑在「主界面透明度」上（默认 0.5），
    /// 本插件的样式要把它压回 1，否则背后的真实画面会直接透过玻璃，
    /// 屏幕上是清晰原图和折射副本两张叠着——在壁纸上看不出来，一开程序就是重影。
    /// 状态栏被排除在屏幕捕获之外，截屏看不见它，所以这个值只能这样读回来。
    /// </remarks>
    private double HostOpacity()
    {
        var acc = Opacity;
        for (Visual? v = this.GetVisualParent(); v is not null; v = v.GetVisualParent())
        {
            acc *= v.Opacity;
            if (v is TopLevel)
            {
                break;
            }
        }

        return acc;
    }

    /// <summary>把 ClassIsland 的窗口排除在屏幕捕获之外。</summary>
    /// <remarks>
    /// 折射的背景来自 DXGI 桌面复制，抓的是合成后的整屏——<b>里面包含 ClassIsland 自己</b>。
    /// 不排除的话每帧都在折射上一帧的自己，亮度逐帧收敛，最后玻璃里就是一块黑。
    /// 实测排除前后，共享纹理亮度上限从 122 变成 223。
    /// <para/>
    /// ⚠ 代价是状态栏在录屏、会议共享和投屏软件里<b>看不见</b>。需要录课就把折射关掉。
    /// <para/>
    /// 时机很关键：必须等窗口真的存在了才能设。插件 <c>Initialize</c> 阶段主窗口还没建，
    /// 那时候拿到的句柄是 0，设了等于没设。
    /// </remarks>
    private void ExcludeFromCapture()
    {
        try
        {
            var handle = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero)
            {
                Report("拿不到窗口句柄，无法排除屏幕捕获");
                return;
            }

            var ok = SetWindowDisplayAffinity(handle, WdaExcludeFromCapture);
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();

            // 设完回读一次确认真的生效了。设置成功不等于生效——
            // 某些窗口样式和虚拟显卡驱动下这个标志会被忽略。
            GetWindowDisplayAffinity(handle, out var actual);
            Report($"排除屏幕捕获：hwnd=0x{handle.ToInt64():X} 返回 {ok}"
                   + (ok ? "" : $" 错误码 {err}")
                   + $"，回读 affinity={actual}"
                   + (actual == WdaExcludeFromCapture ? "（已生效）" : "（未生效，折射会看到自己）"));
        }
        catch (Exception)
        {
            // 设不上最坏就是折射里出现套娃，用户可以关掉折射。
        }
    }

    /// <summary>补设排除标志。已经是排除状态就不动。</summary>
    private void ReapplyCaptureExclusion()
    {
        try
        {
            var handle = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            if (GetWindowDisplayAffinity(handle, out var actual) && actual == WdaExcludeFromCapture)
            {
                return;
            }

            SetWindowDisplayAffinity(handle, WdaExcludeFromCapture);
        }
        catch (Exception)
        {
        }
    }

    private const uint WdaExcludeFromCapture = 0x00000011;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

    /// <summary>GPU 不可用时的退路：只画外壳，没有折射。</summary>
    public override void Render(DrawingContext context)
    {
        if (UsingGpu)
        {
            return;
        }

        var w = (int)Math.Ceiling(Bounds.Width);
        var h = (int)Math.Ceiling(Bounds.Height);
        if (w < 2 || h < 2)
        {
            return;
        }

        var p = new GlassParams(
            Settings.RoundToCapsule ? h / 2f : (float)Settings.CornerRadius,
            (float)Settings.Thickness,
            (float)(Settings.LightAngle * Math.PI / 180.0),
            (float)Settings.LightIntensity,
            (float)Settings.Ambient,
            0f,
            1f, 1f, 1f,
            (float)Settings.TintAmount,
            true,
            1.5f,
            (float)Settings.Chromatic,
            0.8f);

        var size = new PixelSize(w, h);
        if (_cache is null || _cacheSize != size || !_cacheParams.Equals(p))
        {
            _cache?.Dispose();
            _cache = new WriteableBitmap(size, new Avalonia.Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
            _cacheSize = size;
            _cacheParams = p;

            using var fb = _cache.Lock();
            unsafe
            {
                GlassRenderer.Render(new Span<uint>((void*)fb.Address, w * h), w, h, p);
            }
        }

        context.DrawImage(_cache, new Rect(0, 0, w, h));
    }
}
