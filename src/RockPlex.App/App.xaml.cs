using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockPlex.App.Services;
using RockPlex.App.ViewModels;
using RockPlex.Device;
using RockPlex.Media;
using RockPlex.Plex;

namespace RockPlex.App;

public partial class App : Application
{
    private IHost? _host;
    private ILogger<App>? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = new AppPaths();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddProvider(new FileLoggerProvider(paths.Logs));

        var services = builder.Services;
        services.AddSingleton(paths);
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<TokenStore>();
        services.AddSingleton<IBrowserLauncher, BrowserLauncher>();
        services.AddSingleton<LibraryState>();
        services.AddSingleton(sp => new PlexClientOptions
        {
            ClientIdentifier = sp.GetRequiredService<SettingsStore>().Current.ClientIdentifier,
            Version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
        });
        services.AddRockPlexPlex();
        services.AddRockPlexDevice();
        services.AddRockPlexMedia(paths.Cache);
        services.AddSingleton<IFolderPicker, FolderPicker>();
        services.AddSingleton<ISyncConfirmation, SyncConfirmation>();

        services.AddSingleton<SignInViewModel>();
        services.AddSingleton<ServerViewModel>();
        services.AddSingleton<DeviceViewModel>();
        services.AddSingleton<SelectionViewModel>();
        services.AddSingleton<SummaryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ExportViewModel>();
        services.AddSingleton<SyncViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        _host = builder.Build();
        _logger = _host.Services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("RockPlex {Version} starting", typeof(App).Assembly.GetName().Version);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger.LogCritical(args.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            // Stop polling drives first; the container disposes the singletons (MainViewModel included) below.
            _host.Services.GetRequiredService<DeviceWatcher>().DisposeAsync().AsTask().GetAwaiter().GetResult();
            _logger?.LogInformation("RockPlex exiting");
            _host.Dispose();
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI exception");
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nDetails are in the log folder.",
            "RockPlex",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
