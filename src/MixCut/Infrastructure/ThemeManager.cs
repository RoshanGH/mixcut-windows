using System.Windows;

namespace MixCut.Infrastructure;

/// <summary>
/// 主题管理。Stage 0 仅做骨架（暗黑模式在 Stage 6 实现）。
/// 切换原理：替换 App.Current.Resources.MergedDictionaries 中的 Light.xaml ↔ Dark.xaml。
/// </summary>
public static class ThemeManager
{
    private const string LightThemeUri = "pack://application:,,,/MixCut;component/Resources/Theme/Light.xaml";
    private const string DarkThemeUri = "pack://application:,,,/MixCut;component/Resources/Theme/Dark.xaml";

    public enum AppTheme
    {
        Light,
        Dark,
    }

    public static AppTheme Current { get; private set; } = AppTheme.Light;

    /// <summary>
    /// 切换主题（运行时无需重启）。Stage 6 暗黑模式实现前，调 Dark 不生效（仅切回 Light）。
    /// </summary>
    public static void Apply(AppTheme theme)
    {
        var app = Application.Current;
        if (app is null) return;

        var newUri = theme switch
        {
            AppTheme.Dark => DarkThemeUri,
            _ => LightThemeUri,
        };

        var dict = app.Resources.MergedDictionaries;
        // 找到当前 theme dictionary（包含 Theme/ 路径的）并替换
        for (var i = 0; i < dict.Count; i++)
        {
            var src = dict[i].Source?.ToString() ?? string.Empty;
            if (src.Contains("/Theme/Light.xaml", StringComparison.OrdinalIgnoreCase)
                || src.Contains("/Theme/Dark.xaml", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    dict[i] = new ResourceDictionary { Source = new Uri(newUri, UriKind.Absolute) };
                    Current = theme;
                    return;
                }
                catch
                {
                    // Dark.xaml 未实现时静默忽略
                    return;
                }
            }
        }
    }
}
