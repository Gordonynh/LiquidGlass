using System;
using System.ComponentModel;
using System.Linq;
using Avalonia.Markup.Xaml;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.LiquidGlass.Models;
using ClassIsland.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace ClassIsland.LiquidGlass.Views;

/// <summary>液态玻璃的设置页。</summary>
[SettingsPageInfo("gordon.liquidglass", "液态玻璃", "", "")]
public partial class LiquidGlassSettingsPage : SettingsPageBase, INotifyPropertyChanged
{
    public LiquidGlassSettings Settings => LiquidGlassSettings.Current;

    public LiquidGlassSettingsPage()
    {
        DataContext = this;
        InitializeComponent();

        Settings.PropertyChanged += (_, _) => RefreshTexts();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    #region 显示文字

    public string LensingText => $"{Settings.Lensing:P0}";

    public string FrameRateText => $"{Settings.FrameRate:F0} Hz";

    public string ThicknessText => $"{Settings.Thickness:F0} px";

    public string LightText => $"{Settings.LightIntensity:F2}";

    public string AngleText => $"{Settings.LightAngle:F0}°";

    public string ChromaticText => $"{Settings.Chromatic:F2}";

    /// <summary>
    /// 主题启用顺序的检查。
    /// </summary>
    /// <remarks>
    /// 主题按启用顺序依次叠加，后加的覆盖先加的。液态玻璃排在像素流动之前的话，
    /// 无通知模板会被像素流动那份盖掉，玻璃根本不会出现——而这种「装了没反应」
    /// 最难自查，所以这里直接把顺序摆出来。
    /// </remarks>
    public string StatusText
    {
        get
        {
            var themes = IAppHost.Host?.Services.GetService<IXamlThemeService>();
            if (themes is null)
            {
                return "主题服务未就绪。";
            }

            var order = themes.EnabledThemes.ToList();
            var mine = order.IndexOf("gordon.liquidglass");
            var pixel = order.IndexOf("gordon.ultracode");

            if (mine < 0)
            {
                return "主题未启用。到「外观 → 主题」里打开「液态玻璃」。";
            }

            if (pixel >= 0 && pixel > mine)
            {
                return "⚠ 顺序不对：像素流动排在液态玻璃之后，会把玻璃盖掉。\n"
                       + "把「液态玻璃」关掉再重新打开，让它排到最后。";
            }

            return pixel >= 0
                ? "已启用，顺序正确。无通知时是玻璃，有通知时交回像素流动。"
                : "已启用。";
        }
    }

    public string CaptureWarning =>
        "折射的背景来自屏幕捕获，因此 ClassIsland 会被排除在录屏和投屏之外——"
        + "状态栏在会议共享、录课软件里看不见。需要录课就关掉这项，只保留玻璃外壳。";

    #endregion

    private void RefreshTexts() =>
        Raise(nameof(LensingText), nameof(FrameRateText), nameof(ThicknessText),
            nameof(LightText), nameof(AngleText), nameof(ChromaticText), nameof(StatusText));

    private void Raise(params string[] names)
    {
        foreach (var name in names)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public new event PropertyChangedEventHandler? PropertyChanged;
}
