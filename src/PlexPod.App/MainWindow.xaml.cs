using System.Windows;
using PlexPod.App.ViewModels;

namespace PlexPod.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var settings = ((MainViewModel)DataContext).Settings;
        settings.Reload();
        settings.UpdateCacheUsage();
        new SettingsWindow { Owner = this, DataContext = settings }.ShowDialog();
    }
}
