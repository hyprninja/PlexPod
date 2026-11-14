using Avalonia.Controls;
using Avalonia.Interactivity;
using PlexPod.App.ViewModels;
using PlexPod.Mac.Services;

namespace PlexPod.Mac;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _initialized;

    public MainWindow()
        : this(null!, null!, new WindowLocator())
    {
    }

    public MainWindow(MainViewModel viewModel, EjectViewModel eject, WindowLocator windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        EjectButton.DataContext = eject;
        EjectStatus.DataContext = eject;
        windows.Main = this;
        Opened += async (_, _) =>
        {
            if (!_initialized && _viewModel is not null)
            {
                _initialized = true;
                await _viewModel.InitializeAsync();
            }
        };
    }

    private async void OnOpenSettings(object? sender, RoutedEventArgs e)
    {
        var settings = _viewModel.Settings;
        settings.Reload();
        settings.UpdateCacheUsage();
        await new SettingsWindow { DataContext = settings }.ShowDialog(this);
    }
}
