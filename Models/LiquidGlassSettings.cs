using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassIsland.LiquidGlass.Models;

/// <summary>
/// 液态玻璃的设置。
/// </summary>
public partial class LiquidGlassSettings : ObservableObject
{
    public static LiquidGlassSettings Current { get; private set; } = new();

    private static string? _configPath;

    /// <summary>插件配置目录。诊断文件写在这里。</summary>
    public static string ConfigFolder { get; private set; } = "";

    /// <summary>
    /// 用 GPU 折射。
    /// </summary>
    /// <remarks>
    /// 关掉就只剩玻璃外壳（亮边、内侧暗边、染色），没有折射。
    /// GPU 不可用时也会自动退到这个状态。
    /// </remarks>
    [ObservableProperty] private bool _useGpu = true;

    /// <summary>折射强度。0 = 不折射，只剩外壳。</summary>
    [ObservableProperty] private double _lensing = 1.0;

    /// <summary>刷新率。背后是静态内容时调低几乎看不出差别，还省电。</summary>
    [ObservableProperty] private double _frameRate = 30;

    /// <summary>玻璃厚度，也就是边缘倒角带有多宽（逻辑像素）。</summary>
    /// <summary>
    /// 玻璃厚度（逻辑像素）。倒角带就是这么宽，折射只发生在这一带里。
    /// </summary>
    /// <remarks>
    /// 原来是 7：40 逻辑像素高的状态栏上，上下各只有 17.5% 在弯，
    /// 实测边缘压缩比只有 1.33，看着几乎是平的。
    /// 苹果那条是大半个高度都在弯，所以给到 12——上下各 30%，中间仍留 40% 的平坦区透光。
    /// </remarks>
    [ObservableProperty] private double _thickness = 12;

    /// <summary>圆角半径。开了胶囊就忽略这项。</summary>
    [ObservableProperty] private double _cornerRadius = 20;

    /// <summary>两端做成半圆。状态栏是横条，胶囊比圆角矩形更像那么回事。</summary>
    [ObservableProperty] private bool _roundToCapsule = true;

    [ObservableProperty] private double _lightAngle = -55;

    [ObservableProperty] private double _lightIntensity = 0.28;

    [ObservableProperty] private double _ambient = 0.15;

    /// <summary>
    /// 深色场景。
    /// </summary>
    /// <remarks>
    /// 只影响高光的目标亮度：深色下取纯白，浅色下压到 0.85，
    /// 免得浅色背景上边缘过曝成一条死白线。
    /// </remarks>
    [ObservableProperty] private bool _isDark = true;

    /// <summary>
    /// 通透度。
    /// </summary>
    /// <remarks>
    /// 平坦内部的散射量：1 是一块干净的平板玻璃，背后原样透过来；0 是毛玻璃。
    /// 边缘的散射由折射强度自己决定，不受这一项影响——那里必须散开，
    /// 否则被透镜压缩的一大条背景采样会闪烁。
    /// <para/>
    /// 玻璃的通透感来自「看得见背后」，不是来自把背后糊掉。
    /// 整块均匀模糊是廉价实现的标志：细于卷积核的空间频率全部归零，
    /// 背后是什么完全认不出，看着就是亚克力。
    /// </remarks>
    [ObservableProperty] private double _clarity = 1.0;

    /// <summary>
    /// 亮背景下的压暗。
    /// </summary>
    /// <remarks>
    /// 只在背景本身够亮时才起作用（白底课件、浅色网页），暗背景下完全不动。
    /// <para/>
    /// 它是一个<b>增益</b>，不是朝灰色插值：等比压暗不改变 Michelson 对比度，
    /// 背后是什么照样看得一样清楚，只是整体暗下来，白字重新有了落脚的地方。
    /// 朝灰压缩两头都要付代价——背景认不出，字也没见得更清楚。
    /// 苹果的说法是「染色量与动态范围<b>平移</b>」，平移正是这个意思。
    /// </remarks>
    [ObservableProperty] private double _dimAmount = 0.42;

    /// <summary>
    /// 边缘扭曲量，以倒角带宽的倍数计。
    /// </summary>
    /// <remarks>
    /// <b>大于 1 是关键。</b>小于 1 时映射保持单调，边缘只是一层软渐变；
    /// 大于 1 才会把胶囊旁边一大条内容挤进这道窄带，也就是苹果那种透镜感。
    /// <para/>
    /// 顺带记一条：真物理的 Snell 折射在倒角上位移峰值只有带宽的 0.23 倍，
    /// 数学上<b>永远</b>到不了 1，所以照物理写必然偏弱——
    /// 苹果这一层本来就不是折射，是屏幕空间的非物理重映射。
    /// </remarks>
    [ObservableProperty] private double _lensAmount = 1.6;

    /// <summary>
    /// 边缘色散。
    /// </summary>
    /// <remarks>
    /// 它乘的是位移量，所以加强边缘扭曲会连带把彩边放大。
    /// 峰值控制在 ±1 物理像素以内才不显廉价：0.025 × 位移峰值 ≈ 0.8 像素。
    /// </remarks>
    [ObservableProperty] private double _chromatic = 0.025;

    [ObservableProperty] private double _tintAmount = 0.05;

    /// <summary>
    /// 文字随背景自动反黑白。
    /// </summary>
    /// <remarks>
    /// 逐像素判定，不是整块判定：判据取自<b>低通之后</b>的背景，
    /// 极性场在大约 10 物理像素的尺度上变化——12 号字、缩放 2 下差不多是半个字，
    /// 所以一个字跨在明暗交界上时，左右两半会各自翻，和苹果的 vibrancy 一样。
    /// <para/>
    /// 实现上绕了一圈：Avalonia 没有可用的混合模式
    /// （<c>RenderOptions.BitmapBlendingMode</c> 只对 <c>DrawImage</c> 生效，
    /// 对 GlyphRun 完全不动；<c>CompositionBlendMode</c> 是个没接线的空枚举），
    /// 所以够不着「让文字和它下面的东西混合」。改成把 ClassIsland 的文字子树
    /// 光栅化成掩膜送进着色器，由玻璃自己把字画出来，再把原文字设成透明。
    /// <para/>
    /// ⚠ 一旦 GPU 路不可用就必须把原文字还回来，否则状态栏会变成一片空白。
    /// </remarks>
    /// ⚠ 默认<b>关</b>。这条链依赖 <see cref="RenderTargetBitmap"/> 能画出字形，
    /// 而在开发用的虚拟 GPU 上实测画不出（形状可以、字形不行），无法验证。
    /// 插件会自己检查掩膜里有没有内容，没有就自动退回原文字，不会把状态栏弄成空白；
    /// 但既然验不了，就不该默认开着。到目标机器上打开看一眼，
    /// 配置目录的 status.txt 里会写明「有效着墨」是多少。
    [ObservableProperty] private bool _adaptiveText = false;

    /// <summary>
    /// 黑白翻转的过渡带半宽（线性光）。
    /// </summary>
    /// <remarks>
    /// 0 是硬阈值，最锐利但背景在阈值附近抖动时字会闪；
    /// 大了过渡柔和，但过渡带内对比度会掉。0.045 对应 sRGB 约 0.40~0.50 那一段。
    /// </remarks>
    [ObservableProperty] private double _polarSoft = 0.045;

    /// <summary>
    /// 投影强度。
    /// </summary>
    /// <remarks>
    /// 分软投影和 1 像素的接触投影两道。少了投影，条子不「落」在任何东西上。
    /// 苹果那边这个值是跟着背景走的（压在文字上加重、在纯色亮背景上减轻），
    /// 这里先给一个固定值。
    /// </remarks>
    [ObservableProperty] private double _shadowAlpha = 0.20;

    #region 读写

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Initialize(string pluginConfigFolder)
    {
        ConfigFolder = pluginConfigFolder;
        _configPath = Path.Combine(pluginConfigFolder, "settings.json");
        try
        {
            if (File.Exists(_configPath) &&
                JsonSerializer.Deserialize<LiquidGlassSettings>(
                    File.ReadAllText(_configPath), JsonOptions) is { } loaded)
            {
                Current = loaded;
            }
        }
        catch (Exception)
        {
            // 配置坏了就用默认值。
        }

        Current.PropertyChanged += (_, _) => Current.Save();
    }

    public void Save()
    {
        if (_configPath is null)
        {
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_configPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception)
        {
            // 存不上下次再存。
        }
    }

    #endregion
}
