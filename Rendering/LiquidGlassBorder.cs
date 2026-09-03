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

    // ---- 文字自适应反色 ----
    private Visual? _gridRoot;       // 模板根。玻璃挂到它上面就画在文字之上
    private Visual? _overlay;        // 通知层。它显形时玻璃必须让位
    private Visual? _attached;       // 当前挂在哪
    // 起始就当作「还没验证过」，未验证成功前玻璃一律待在文字下面。
    // 否则启动瞬间会先盖上去、掩膜判死之后再退回，中间有一秒是空白状态栏。
    private int _maskMiss = 30;

    private Control? _textHost;      // 文字层（ClassIsland 的 GridContentRoot）
    private Visual? _textSource;     // 真正拿去光栅化的那一层（它的子节点）
    private byte[]? _maskBuf;
    private Point _maskOriginDip;
    private bool _maskAlive;
    private float _maskGain = 1f;
    private double _maskInk;

    /// <summary>
    /// 找到状态栏画文字的那棵子树。
    /// </summary>
    /// <remarks>
    /// ⚠ 藏起来的和拿去光栅化的<b>必须是两层</b>：
    /// <c>RenderTargetBitmap.Render(v)</c> 会应用 v <b>自身</b>的 Opacity，
    /// 在同一个控件上设 0 再去渲染，拿到的必然是一张空图。
    /// 祖先的 Opacity 不影响它（渲染时该控件就是根），所以藏父、渲子。
    /// </remarks>
    private void LocateText()
    {
        if (_textHost is not null)
        {
            return;
        }

        Visual? line = this;
        while (line is not null && line.GetType().Name != "MainWindowLine")
        {
            line = line.GetVisualParent();
        }

        var host = line is null ? null : FindNamed(line, "GridContentRoot");
        if (host is null || line is null)
        {
            return;
        }

        _gridRoot = FindNamed(line, "GridRoot") ?? line;
        _overlay = FindNamed(line, "GridOverlay");
        _textHost = host;

        // 渲染子节点，藏父节点。见 HiddenOpacity 的说明。
        _textSource = host.GetVisualChildren().FirstOrDefault() ?? (Visual)host;

        var kids = string.Join("/", host.GetVisualChildren()
            .Take(4).Select(v => v.GetType().Name));
        Report($"已接上文字层 {host.GetType().Name}"
               + $"，子节点 [{kids}]，准备自适应反色");
    }

    private static Control? FindNamed(Visual root, string name)
    {
        if (root is Control c && c.Name == name)
        {
            return c;
        }

        foreach (var child in root.GetVisualChildren())
        {
            var hit = FindNamed(child, name);
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    /// <summary>把文字层光栅化成掩膜交给着色器。</summary>
    /// <remarks>
    /// 内容变化是秒级的，没必要每帧做，所以调用方节流。
    /// 光栅化尺寸必须用<b>物理像素</b>，和纹理坐标同一套单位。
    /// </remarks>
    private void UpdateTextMask(double scaling)
    {
        if (_source is null || _textSource is null || _textHost is null)
        {
            return;
        }

        var b = _textSource.Bounds;
        var w = (int)Math.Ceiling(b.Width * scaling);
        var h = (int)Math.Ceiling(b.Height * scaling);
        if (w < 2 || h < 2 || w > 8192 || h > 8192)
        {
            return;
        }

        var need = w * h * 4;
        if (_maskBuf is null || _maskBuf.Length < need)
        {
            _maskBuf = new byte[need];
        }

        using var rtb = new RenderTargetBitmap(
            new PixelSize(w, h), new Avalonia.Vector(96 * scaling, 96 * scaling));

        // ⚠ 祖先的透明度会<b>算进</b> RenderTargetBitmap 的结果。
        // ClassIsland 的 MainWindowLine 在「淡出」状态下 Opacity=0.05
        // （MainWindowLine.axaml 的 IsLineFaded 样式），于是掩膜里的字只剩 5% 的 alpha，
        // 看上去就是「只有一根进度条，一个字都没有」。
        //
        // 临时把祖先链置 1 再渲染<b>没有用</b>：RTB 取的是已经录制好的绘制数据，
        // 属性改了要等下一轮才重建。所以只能把这个系数量出来，事后补偿。
        //
        // 量化损失可以接受，而且恰好损失在无人看得见的地方：系数不是 1
        // 只发生在状态栏本身已经淡到 5% 的时候，那时整条都快看不见了；
        // 正常显示时系数就是 1，掩膜是全质量的。
        var fade = 1.0;
        for (Visual? a = _textSource; a is not null; a = a.GetVisualParent())
        {
            fade *= a.Opacity;
        }

        _maskGain = (float)Math.Clamp(1.0 / Math.Max(fade, 0.02), 1.0, 50.0);
        rtb.Render(_textSource);

        unsafe
        {
            fixed (byte* q = _maskBuf)
            {
                rtb.CopyPixels(new PixelRect(0, 0, w, h), (IntPtr)q, need, w * 4);
            }
        }

        // 排查用：配置目录里放了 dump.on 就把掩膜本身存一张出来。
        // 掩膜画错了（位置、尺寸、透明度）在最终画面上表现得千奇百怪，
        // 直接看这张图比反推快得多。
        if (_frames % 300 == 0 && LiquidGlassSettings.ConfigFolder.Length > 0 &&
            System.IO.File.Exists(
                System.IO.Path.Combine(LiquidGlassSettings.ConfigFolder, "dump.on")))
        {
            try
            {
                rtb.Save(System.IO.Path.Combine(
                    LiquidGlassSettings.ConfigFolder, $"mask_{w}x{h}.png"));
                var off = _textSource.TranslatePoint(new Point(0, 0), this) ?? default;
                var probe = System.Linq.Enumerable.Range(0, w * h)
                    .Count(k => _maskBuf[k * 4 + 3] > 8);
                Report($"掩膜 {w}×{h}，有效着墨 {_maskInk:F2}%（判活门限 2%），淡出补偿 {_maskGain:F1}×"
                       + $"；文字层 {b.Width:F0}×{b.Height:F0} 逻辑"
                       + $"；相对本控件偏移 ({off.X:F1},{off.Y:F1}) 逻辑"
                       + $"；本控件 {Bounds.Width:F0}×{Bounds.Height:F0} 逻辑");
            }
            catch (Exception)
            {
            }
        }

        // ⚠ 掩膜必须<b>自证有内容</b>再启用。
        // 实测在虚拟 GPU 上 RenderTargetBitmap 能画出形状（进度条画出来了）
        // 却画不出字形，掩膜里一个字都没有。这种情况下若照样把玻璃盖到文字上，
        // 状态栏就是一条空白——比不做自适应严重得多。
        // 所以这里数一遍真正有内容的像素：够多才算这条链活着。
        var ink = 0;
        for (var k = 0; k < w * h; k++)
        {
            var o = k * 4;
            if (_maskBuf[o + 3] > 64 &&
                Math.Max(_maskBuf[o], Math.Max(_maskBuf[o + 1], _maskBuf[o + 2])) > 64)
            {
                ink++;
            }
        }

        _maskInk = ink * 100.0 / (w * h) * _maskGain;
        _maskAlive = _maskInk >= 2.0;

        _source.UpdateTextMask(_maskBuf, w, h);
        _maskOriginDip = _textSource.TranslatePoint(new Point(0, 0), this) ?? default;
    }

    /// <summary>
    /// 原文字该不该藏。
    /// </summary>
    /// <remarks>
    /// ⚠ 只有确认整条链活着才藏。GPU 退回 CPU、掩膜没传上去、控件卸载——
    /// 任何一种情况下都必须把原文字还回来，否则状态栏就是一片空白，
    /// 而这是比「文字对比度不够」严重得多的故障。
    /// <para/>
    /// 用 Opacity 而不是 IsVisible：Opacity=0 仍然参与布局、仍然可命中，
    /// IsVisible=false 会让 Measure 返回 0，状态栏宽度会跟着塌掉。
    /// </remarks>
    /// <summary>
    /// 把玻璃挂到哪一层。
    /// </summary>
    /// <remarks>
    /// 自适应反色要求玻璃画在<b>文字之上</b>——胶囊内部本来就不透明，
    /// 盖上去原文字自然就没了，文字由玻璃自己按背景逐像素定黑白重新画出来。
    /// <para/>
    /// 之所以不是「把原文字藏起来」：任何让子树透明的手段都会同时把它从
    /// <see cref="RenderTargetBitmap"/> 里抹掉——Opacity=0 时绘制数据根本不保留，
    /// 1/255 时祖先透明度又会渗进渲染结果。藏与取是同一棵子树，本质冲突。
    /// <para/>
    /// 挂到 <c>GridRoot</c>：它 <c>ClipToBounds=False</c>，投影留边不会被裁；
    /// 而 <c>ContentClipBorder</c> 带 Clip，挂在它里面会被切掉一圈。
    /// <para/>
    /// ⚠ 掩膜一旦拿不到就必须<b>退回到文字下面</b>。玻璃在上、又没有文字，
    /// 状态栏就是一条空白——这比对比度不够严重得多。
    /// </remarks>
    private void Attach(Visual host)
    {
        if (ReferenceEquals(_attached, host) || _visual is null)
        {
            return;
        }

        if (_attached is not null)
        {
            ElementComposition.SetElementChildVisual(_attached, null);
        }

        ElementComposition.SetElementChildVisual(host, _visual);
        _attached = host;
        Report(ReferenceEquals(host, this) ? "玻璃挂回文字下方" : "玻璃已挂到文字上方");
    }

    /// <summary>
    /// 「藏起来」用的透明度：1/255，不是 0。（已不再使用，保留说明备查。）
    /// </summary>
    /// <remarks>
    /// ⚠ 归零不行。实测：第一张掩膜有 58% 的像素有内容，把文字层设成 Opacity=0 之后
    /// 每一张都是 0.0% —— Avalonia 对完全透明的子树<b>根本不保留绘制数据</b>，
    /// 而且在同一个 tick 里临时改回 1 也来不及重建（改的只是属性，绘制数据要等下一轮）。
    /// 现象是状态栏文字整个消失，且不报任何错。
    /// <para/>
    /// 1/255 让这棵子树在渲染器眼里仍然是「可见」的，绘制数据照常维护，
    /// 而屏幕上它对每个像素的贡献不到一个色阶，看不出来。
    /// </remarks>
    private const double HiddenOpacity = 1.0 / 255.0;

    private void ApplyTextHiding(bool alive)
    {
        if (_textHost is null)
        {
            return;
        }

        var want = alive ? HiddenOpacity : 1.0;
        if (Math.Abs(_textHost.Opacity - want) > 0.001)
        {
            _textHost.Opacity = want;
        }
    }


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
        _maskAlive = false;
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
        Attach(this);

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

        // 偏移是相对<b>挂载目标</b>的，不是相对本控件的。
        // 挂到模板根之后两者不再重合，照旧只减留边会整体错位。
        var rel = _attached is null || ReferenceEquals(_attached, this)
            ? new Point(0, 0)
            : this.TranslatePoint(new Point(0, 0), _attached) ?? new Point(0, 0);
        _visual.Offset = new Vector3(
            (float)(rel.X - padDip), (float)(rel.Y - padDip), 0);

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

            // 文字掩膜：秒级内容，没必要每帧重做。
            if (Settings.AdaptiveText)
            {
                LocateText();
                if (_frames % 6 == 0)
                {
                    try
                    {
                        UpdateTextMask(scaling);
                    }
                    catch (Exception ex)
                    {
                        _maskAlive = false;
                        Report("文字掩膜失败，改用原文字：" + ex.Message);
                    }
                }
            }
            else
            {
                _maskAlive = false;
            }

            // 掩膜连着几轮拿不到就退回文字下方，宁可没有自适应也不能让字消失。
            if (Settings.AdaptiveText && _source.MaskWidth > 0 && _maskAlive)
            {
                _maskMiss = 0;
            }
            else
            {
                _maskMiss++;
            }

            var above = _maskMiss < 30 && _gridRoot is not null;
            Attach(above ? _gridRoot! : this);

            // 通知显形时让位：玻璃在文字之上，不退让会把整套通知盖住。
            // 跟着通知层的透明度做交叉淡入，比硬切干净。
            _visual.Opacity = _attached is null || ReferenceEquals(_attached, this) || _overlay is null
                ? 1f
                : (float)Math.Clamp(1.0 - _overlay.Opacity, 0.0, 1.0);

            var radius = (float)Math.Min(Settings.CornerRadius * scaling, h / 2.0);
            if (Settings.RoundToCapsule)
            {
                radius = h / 2f;
            }

            if (!_source.Render(new Vector2(screen.X, screen.Y), radius,
                    (float)(Settings.Thickness * scaling), (float)Settings.Lensing,
                    (float)scaling, Settings.IsDark,
                    (float)Settings.Chromatic, (float)Settings.TintAmount,
                    (float)Settings.ShadowAlpha, (float)Settings.Clarity,
                    (float)Settings.DimAmount, (float)Settings.LensAmount,
                    new Vector2((float)(_maskOriginDip.X * scaling),
                                (float)(_maskOriginDip.Y * scaling)),
                    Settings.AdaptiveText && _maskAlive, (float)Settings.PolarSoft, _maskGain))
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
            // ⚠ 周期性导出，不是只导第 90 帧。
            // 启动后头几秒状态栏还在调整宽度、桌面复制也可能还在交上一帧，
            // 那时导出的一帧和事后截屏对不上，量出来的数会随轮次跳。
            if (_frames > 0 && _frames % 300 == 0 && LiquidGlassSettings.ConfigFolder.Length > 0 &&
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
            _maskAlive = false;
            Attach(this);
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
