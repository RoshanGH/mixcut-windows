using System.Windows;
using MixCut.ViewModels;

namespace MixCut.Views;

/// <summary>新建项目对话框。对应 macOS 版 NewProjectSheet。</summary>
public partial class NewProjectDialog : Window
{
    private readonly ProjectViewModel _projectVM;

    public NewProjectDialog(ProjectViewModel projectVM)
    {
        _projectVM = projectVM;
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnNameChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var hasText = !string.IsNullOrWhiteSpace(NameBox.Text);
        NamePlaceholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        CreateButton.IsEnabled = hasText;
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            // 按钮已 disabled，此分支理论上走不到；保留作为防御
            return;
        }
        _projectVM.NewProjectName = name;
        _projectVM.CreateProjectCommand.Execute(null);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
