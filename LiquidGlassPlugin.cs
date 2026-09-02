using System;
using System.Reflection;
using System.Runtime.Loader;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Core.Models.XamlTheme;
using ClassIsland.LiquidGlass.Models;
using ClassIsland.LiquidGlass.Services;
using ClassIsland.LiquidGlass.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.LiquidGlass;

/// <summary>液态玻璃插件入口。</summary>
public class LiquidGlassPlugin : PluginBase
{
    private static readonly Assembly SelfAssembly = typeof(LiquidGlassPlugin).Assembly;

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        EnsureAssemblyResolvable();

        LiquidGlassSettings.Initialize(PluginConfigFolder);

        services.AddXamlTheme(
            new Uri("avares://ClassIsland.LiquidGlass/Themes/Styles.axaml"),
            new ThemeManifest
            {
                Id = "gordon.liquidglass",
                Name = "液态玻璃",
                Description = "把状态栏换成苹果液态玻璃质感。边缘折射、镜面高光、菲涅耳边缘与色散实时计算。"
                              + "只接管无通知时的状态栏，可与像素流动同时启用。",
                Author = "GordonYoung",
                Version = "1.0.0.0",
                Url = "https://github.com/Gordonynh/LiquidGlass"
            });

        services.AddSettingsPage<LiquidGlassSettingsPage>();
        services.AddHostedService<ThemeWatch>();

    }

    /// <summary>
    /// 让 <c>avares://ClassIsland.LiquidGlass/...</c> 能被解析到。
    /// </summary>
    /// <remarks>
    /// Avalonia 的资源加载器按名字用 <see cref="Assembly.Load(AssemblyName)"/> 找程序集，走的是
    /// 默认 <see cref="AssemblyLoadContext"/>；插件却在独立的 PluginLoadContext 里，默认上下文看不到它。
    /// 主题模板里引用了本程序集的 <c>LiquidGlassBorder</c>，所以这一步是必需的。
    /// </remarks>
    private static void EnsureAssemblyResolvable()
    {
        var selfName = SelfAssembly.GetName().Name;
        AssemblyLoadContext.Default.Resolving += (_, requested) =>
            requested.Name == selfName ? SelfAssembly : null;
    }
}
