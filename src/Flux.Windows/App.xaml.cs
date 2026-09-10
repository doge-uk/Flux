using System.Threading;
using System.Windows;
using Flux.Core;
using Flux.Core.Agent;
using Flux.Core.Routing;
using Flux.Windows.Ai;
using Flux.Windows.Configuration;
using Flux.Windows.Infrastructure;
using Flux.Windows.Search;
using Flux.Windows.SystemIntegration;
using Flux.Windows.Tools;
using Flux.Windows.Updates;

namespace Flux.Windows;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private TrayService? _tray;
    private AiProviderRouter? _provider;
    private MainWindow? _window;
    private SettingsService? _settingsService;
    private StartupManager? _startup;
    private GitHubReleaseUpdateService? _updates;
    private int _updateCheckInProgress;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, "Local\\Flux.Desktop.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        var log = new LogService();
        DispatcherUnhandledException += (_, args) =>
        {
            log.Error("Unhandled UI exception.", args.Exception);
            System.Windows.MessageBox.Show(args.Exception.Message, "Flux encountered a problem", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        _settingsService = new SettingsService();
        _startup = new StartupManager();
        _updates = new GitHubReleaseUpdateService(log);
        var settings = _settingsService.Current;
        var history = new CommandHistory(settings);
        var applications = new ApplicationCatalog(settings, log);
        var fileSearch = new FileSearchService();
        var processes = new WindowsProcessService();
        var systemInfo = new WindowsSystemInfoService();
        var router = new DeterministicCommandRouter(applications, fileSearch);
        var tools = new ToolRegistry(processes, systemInfo, fileSearch);
        _provider = new AiProviderRouter(settings);
        var agent = new FluxAgent(_provider, tools, log);
        var hotkey = new GlobalHotkeyManager();

        _window = new MainWindow(
            router, agent, tools, processes, systemInfo, history,
            applications, settings, _settingsService, _startup, hotkey, log);
        MainWindow = _window;

        _tray = new TrayService(
            () => _window.ShowLauncher(),
            () => _window.ShowSettings(),
            () => _ = CheckForUpdatesAsync(showCurrentStatus: true),
            ExitApplication);
        _window.HotkeyRegistrationFailed += (_, _) =>
            _tray.ShowWarning("Flux hotkey unavailable", "Alt + Space is already in use. Flux is still available from the tray.");

        _window.InitializeHotkey();

        var showSettings = e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase);
        var hidden = e.Args.Contains("--hidden", StringComparer.OrdinalIgnoreCase) || settings.LaunchHidden;
        if (!hidden || showSettings)
        {
            _window.ShowLauncher();
        }

        if (showSettings)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, _window.ShowSettings);
        }

        if (settings.CheckForUpdatesOnLaunch)
        {
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                new Action(() => _ = CheckForUpdatesAsync(showCurrentStatus: false)));
        }

        _ = applications.RefreshAsync().ContinueWith(
            task => log.Error("Application catalog refresh failed.", task.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private async Task CheckForUpdatesAsync(bool showCurrentStatus)
    {
        if (_updates is null || Interlocked.Exchange(ref _updateCheckInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            var result = await _updates.CheckAsync();
            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable when result.LatestVersion is not null && result.ReleaseUri is not null:
                    _tray?.ShowUpdateAvailable(result.LatestVersion, result.ReleaseUri);
                    break;

                case UpdateCheckStatus.UpToDate:
                    _tray?.ClearUpdateAvailable();
                    if (showCurrentStatus)
                    {
                        _tray?.ShowInformation(
                            "Flux is up to date",
                            $"You are running v{result.CurrentVersion.Major}.{result.CurrentVersion.Minor}.{result.CurrentVersion.Build}.");
                    }
                    break;

                case UpdateCheckStatus.NotConfigured when showCurrentStatus:
                    _tray?.ShowInformation(
                        "Updates are not configured yet",
                        "Add a GitHub repository when building Flux to enable release checks.");
                    break;

                case UpdateCheckStatus.Failed when showCurrentStatus:
                    _tray?.ShowWarning(
                        "Could not check for updates",
                        result.Message ?? "Try again when GitHub is reachable.");
                    break;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _updateCheckInProgress, 0);
        }
    }

    private void ExitApplication()
    {
        _window?.AllowClose();
        _tray?.Dispose();
        _provider?.Dispose();
        _updates?.Dispose();
        _singleInstance?.Dispose();
        Shutdown();
    }
}
