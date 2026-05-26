using System.Windows;

namespace MixCut.Views;

/// <summary>项目重命名对话框。</summary>
public partial class RenameDialog : Window
{
    public string NewName { get; private set; } = string.Empty;

    public RenameDialog(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        NewName = NameBox.Text.Trim();
        DialogResult = true;
    }
}
