using System.Windows;

namespace RockPlex.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
