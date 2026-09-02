using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.LiquidGlass.Models;
using ClassIsland.LiquidGlass.Rendering;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.LiquidGlass.Services;

/// <summary>
/// 把主题的加载结果写出来。
/// </summary>
/// <remarks>
/// <b>ClassIsland 的主题加载失败是静默的</b>：<c>XamlThemeService.LoadThemes</c> 的
/// catch 只写 <c>themeInfo.IsError</c> 和 <c>themeInfo.Error</c>，<b>不写日志</b>。
/// 所以「日志里有『正在从资源加载主题』」并不代表加载成功——
/// 主题炸了的现象是插件装好了、日志干干净净，但界面上什么都没变。
/// <para/>
/// 这个服务把 <c>IsError</c> 和异常本身取出来落盘，省得每次都要靠排除法猜。
/// </remarks>
internal sealed class ThemeWatch(IXamlThemeService themes) : IHostedService
{
    private const string ThemeId = "gordon.liquidglass";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 主题在宿主启动流程里加载，晚于插件的 Initialize，所以等一会儿再看。
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
            Write();
        }, cancellationToken);

        return Task.CompletedTask;
    }

    private void Write()
    {
        try
        {
            var info = themes.Themes.FirstOrDefault(x => x.Manifest.Id == ThemeId);
            var order = string.Join(" → ", themes.EnabledThemes);

            string state;
            if (info is null)
            {
                state = "主题没被登记。";
            }
            else if (info.IsError)
            {
                state = "主题加载失败：" + Environment.NewLine + info.Error;
            }
            else if (!info.IsLoaded)
            {
                state = "主题已登记但未加载（可能没启用）。";
            }
            else
            {
                state = "主题已加载。";
            }

            var text = $"{DateTime.Now:HH:mm:ss}{Environment.NewLine}"
                       + $"启用顺序：{order}{Environment.NewLine}"
                       + $"{state}{Environment.NewLine}"
                       + $"渲染：{LiquidGlassBorder.Diagnostics}{Environment.NewLine}";

            Directory.CreateDirectory(LiquidGlassSettings.ConfigFolder);
            File.WriteAllText(
                Path.Combine(LiquidGlassSettings.ConfigFolder, "theme-status.txt"), text);
        }
        catch (Exception)
        {
            // 诊断失败不该影响任何东西。
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
