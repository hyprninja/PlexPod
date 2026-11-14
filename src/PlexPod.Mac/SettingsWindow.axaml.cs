using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PlexPod.Mac;

public sealed partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
