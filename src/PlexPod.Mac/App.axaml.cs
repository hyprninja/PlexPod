using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PlexPod.App.Services;
using PlexPod.App.ViewModels;
using PlexPod.Device;
using PlexPod.Mac.Services;
using PlexPod.Media;
using PlexPod.Plex;

namespace PlexPod.Mac;

/// <summary>The Mac app's host: the same services and view models as the Windows app (App.xaml.cs), with the macOS seams.</summary>
public sealed partial class App : Application
{
    private IHost? _host;
    private ILogger<App>? _logger;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        _host = BuildHost(new AppPaths());
        _logger = _host.Services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("PlexPod {Version} (macOS) starting", typeof(App).Assembly.GetName().Version);

        Dispatcher.UIThread.UnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger.LogCritical(args.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        var window = _host.Services.GetRequiredService<MainWindow>();
        desktop.MainWindow = window;
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        desktop.Exit += (_, _) => Shutdown();

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The app's services and view models. Also used to render the window headlessly for checks.</summary>
    public static IHost BuildHost(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
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
        services.AddSingleton<ITokenStore, KeychainTokenStore>();
        services.AddSingleton<IUiThread, AvaloniaUiThread>();
        services.AddSingleton<IBrowserLauncher, BrowserLauncher>();
        services.AddSingleton<LibraryState>();
        services.AddSingleton(sp => new PlexClientOptions
        {
            ClientIdentifier = sp.GetRequiredService<SettingsStore>().Current.ClientIdentifier,
            Version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.1.0",
            Platform = "macOS",
        });
        services.AddPlexPodPlex();
        services.AddPlexPodDevice();
        services.AddPlexPodMedia(paths.Cache);
        services.AddSingleton<WindowLocator>();
        services.AddSingleton<IFolderPicker, MacFolderPicker>();
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
        services.AddSingleton<EjectViewModel>();
        services.AddSingleton<MainWindow>();
        return builder.Build();
    }

    private void Shutdown()
    {
        if (_host is null)
        {
            return;
        }

        // Stop polling drives first; the container disposes the singletons (MainViewModel included) below.
        _host.Services.GetRequiredService<DeviceWatcher>().DisposeAsync().AsTask().GetAwaiter().GetResult();
        _logger?.LogInformation("PlexPod exiting");
        _host.Dispose();
        _host = null;
    }

    private void OnUiException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI exception");
        e.Handled = true;
        if (_host?.Services.GetService<WindowLocator>() is { } windows)
        {
            _ = MessageWindow.ShowAsync(windows.Main, "PlexPod", $"Something went wrong:\n\n{e.Exception.Message}\n\nDetails are in the log folder.");
        }
    }
}
