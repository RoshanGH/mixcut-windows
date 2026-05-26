using System.Windows;
using System.Windows.Media;
using MixCut.Utilities;

namespace MixCut.Views;

/// <summary>
/// 首次启动 4 步使用引导。对应 macOS 版 OnboardingView。
/// 通过 <see cref="AppSettings.HasCompletedOnboarding"/> 持久化是否已完成。
/// </summary>
public partial class OnboardingWindow : Window
{
    private const int TotalSteps = 4;

    private readonly AppSettings _settings;
    private readonly IServiceProvider _services;
    private int _step;

    public OnboardingWindow(AppSettings settings, IServiceProvider services)
    {
        _settings = settings;
        _services = services;
        InitializeComponent();
        UpdateStep();
    }

    private void UpdateStep()
    {
        // 切换页面
        WelcomePage.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyPage.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        ImportPage.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        GeneratePage.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;

        // 圆点
        var dots = new[] { Dot0, Dot1, Dot2, Dot3 };
        for (var i = 0; i < dots.Length; i++)
        {
            dots[i].Fill = i == _step
                ? (Brush)new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0xE5))
                : new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD5));
        }

        // 按钮
        PrevButton.Visibility = _step > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _step == TotalSteps - 1 ? "开始使用 MixCut" : "下一步 ›";
    }

    private void OnPrev(object sender, RoutedEventArgs e)
    {
        if (_step > 0)
        {
            _step--;
            UpdateStep();
        }
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_step < TotalSteps - 1)
        {
            _step++;
            UpdateStep();
            return;
        }
        Complete();
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Complete();

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var window = (SettingsWindow)_services.GetService(typeof(SettingsWindow))!;
        window.Owner = this;
        window.ShowDialog();
    }

    private void Complete()
    {
        _settings.HasCompletedOnboarding = true;
        DialogResult = true;
        Close();
    }
}
