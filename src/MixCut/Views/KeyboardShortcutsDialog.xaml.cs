using System.Windows;

namespace MixCut.Views;

public partial class KeyboardShortcutsDialog : Window
{
    public KeyboardShortcutsDialog()
    {
        InitializeComponent();
    }

    private void OnClose(object sender, System.Windows.Input.ExecutedRoutedEventArgs e) => Close();
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
