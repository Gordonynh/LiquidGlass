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
    [ObservableProperty] private double _thickness = 13;

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
    [ObservableProperty] private double _clarity = 0.88;

    /// <summary>边缘色散。0.03~0.06 之间；再大就是廉价的彩边。</summary>
    [ObservableProperty] private double _chromatic = 0.045;

    [ObservableProperty] private double _tintAmount = 0.05;

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
