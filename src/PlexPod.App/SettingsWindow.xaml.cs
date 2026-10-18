using System.Windows;

namespace PlexPod.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
