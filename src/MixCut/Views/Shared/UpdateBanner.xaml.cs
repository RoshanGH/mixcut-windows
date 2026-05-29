using System.Windows;
using System.Windows.Controls;
using MixCut.ViewModels;

namespace MixCut.Views.Shared;

/// <summary>
/// 顶部「发现新版本」banner UI。
/// DataContext 由宿主（MainWindow）注入 UpdateBannerViewModel，UI 通过 HasUpdate 自动显示/隐藏。
/// </summary>
public partial class UpdateBanner : UserControl
{
    public UpdateBanner()
    {
        InitializeComponent();
    }

    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is UpdateBannerViewModel vm) vm.OpenReleasePage();
    }

    private void OnDismissClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is UpdateBannerViewModel vm) vm.DismissCurrent();
    }
}
