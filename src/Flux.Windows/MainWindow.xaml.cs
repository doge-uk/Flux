using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Flux.Core;
using Flux.Core.Agent;
using Flux.Core.Search;
using Flux.Windows.Configuration;
using Flux.Windows.SystemIntegration;

namespace Flux.Windows;

public partial class MainWindow : Window
{
    private readonly ICommandRouter _router;
    private readonly FluxAgent _agent;
    private readonly IToolRegistry _tools;
    private readonly IProcessService _processes;
    private readonly ISystemInfoService _systemInfo;
    private readonly ICommandHistory _history;
    private readonly IApplicationCatalog _applications;
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly StartupManager _startup;
    private readonly GlobalHotkeyManager _hotkey;
    private readonly ILogService _log;
    private readonly ObservableCollection<SearchResult> _results = [];
    private readonly DispatcherTimer _searchTimer;
    private CancellationTokenSource? _searchCancellation;
    private FluxAction? _pendingAction;
    private IReadOnlyList<PendingToolCall> _pendingTools = Array.Empty<PendingToolCall>();
    private int _historyIndex = -1;
    private bool _allowClose;
    private bool _hotkeyInitialized;
    private bool _settingsOpen;
    private bool _isHiding;
    private int _animationGeneration;

    public MainWindow(
        ICommandRouter router,
        FluxAgent agent,
        IToolRegistry tools,
        IProcessService processes,
        ISystemInfoService systemInfo,
        ICommandHistory history,
        IApplicationCatalog applications,
        AppSettings settings,
        SettingsService settingsService,
        StartupManager startup,
        GlobalHotkeyManager hotkey,
        ILogService log)
    {
        InitializeComponent();
        _router = router;
        _agent = agent;
        _tools = tools;
        _processes = processes;
        _systemInfo = systemInfo;
        _history = history;
        _applications = applications;
        _settings = settings;
        _settingsService = settingsService;
        _startup = startup;
        _hotkey = hotkey;
        _log = log;

        ResultsList.ItemsSource = _results;
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _searchTimer.Tick += SearchTimer_Tick;
        _hotkey.Pressed += (_, _) => Dispatcher.Invoke(ToggleLauncher);
    }

    public event EventHandler? HotkeyRegistrationFailed;

    public void InitializeHotkey()
    {
        if (_hotkeyInitialized)
        {
            return;
        }

        new WindowInteropHelper(this).EnsureHandle();
        _hotkeyInitialized = true;
        if (!_hotkey.Register(this, _settings.HotkeyModifiers, _settings.HotkeyKey))
        {
            HotkeyRegistrationFailed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ShowLauncher()
    {
        _animationGeneration++;
        _isHiding = false;
        InitializeHotkey();
        ResetPanels();
        QueryBox.Clear();
        Height = 104;
        PositionOnActiveScreen();
        BeginAnimation(OpacityProperty, null);
        LauncherScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        LauncherScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        LauncherTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        Opacity = 0;
        LauncherScale.ScaleX = 0.96;
        LauncherScale.ScaleY = 0.96;
        LauncherTranslate.Y = -11;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        QueryBox.Focus();
        Keyboard.Focus(QueryBox);
        AnimateLauncherOpen();
    }

    private void ToggleLauncher()
    {
        if (IsVisible && !_isHiding)
        {
            HideLauncher();
            return;
        }

        ShowLauncher();
    }

    public void ShowSettings()
    {
        _settingsOpen = true;
        var window = new SettingsWindow(_settingsService, _startup, _history)
        {
            Owner = IsVisible ? this : null
        };
        bool saved;
        try
        {
            saved = window.ShowDialog() == true;
        }
        finally
        {
            _settingsOpen = false;
        }
        if (saved)
        {
            _hotkey.Dispose();
            _hotkeyInitialized = false;
            InitializeHotkey();
        }
    }

    public void AllowClose()
    {
        _allowClose = true;
        _hotkey.Dispose();
        Close();
    }

    private void QueryBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        Placeholder.Visibility = string.IsNullOrEmpty(QueryBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        _searchCancellation?.Cancel();
        if (string.IsNullOrWhiteSpace(QueryBox.Text))
        {
            ResetPanels();
            AnimateHeight(104);
            return;
        }

        _searchTimer.Start();
    }

    private async void SearchTimer_Tick(object? sender, EventArgs e)
    {
        _searchTimer.Stop();
        await SearchAsync(QueryBox.Text);
    }

    private async Task SearchAsync(string query)
    {
        _searchCancellation?.Cancel();
        _searchCancellation = new CancellationTokenSource();
        try
        {
            var decision = await _router.RouteAsync(query, _searchCancellation.Token);
            if (query != QueryBox.Text)
            {
                return;
            }

            _results.Clear();
            if (decision.Kind == RouteKind.Search)
            {
                foreach (var result in decision.Results)
                {
                    _results.Add(result);
                }
            }
            else if (decision.Kind == RouteKind.ImmediateAction && decision.Action is not null)
            {
                _results.Add(new SearchResult(
                    "command:deterministic",
                    decision.Action.DisplayName ?? "Run command",
                    decision.Explanation ?? "Handled without AI",
                    SearchResultKind.Command,
                    1000,
                    decision.Action));
            }
            else
            {
                _results.Add(new SearchResult(
                    "command:ai",
                    "Ask the local model",
                    "Use AI only for interpretation and planning",
                    SearchResultKind.Ai,
                    1,
                    new FluxAction(FluxActionType.AskAi, query, PermissionLevel.ReadOnly, "Ask Flux")));
            }

            ShowResults();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _log.Error("Search failed.", exception);
            ShowStatus("Search failed", exception.Message, "ERROR");
        }
    }

    private async void QueryBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideLauncher();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down)
        {
            if (ResultsList.Visibility == Visibility.Visible && _results.Count > 0)
            {
                ResultsList.SelectedIndex = Math.Min(_results.Count - 1, ResultsList.SelectedIndex + 1);
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            }
            else
            {
                NavigateHistory(-1);
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up)
        {
            if (ResultsList.Visibility == Visibility.Visible && ResultsList.SelectedIndex > 0)
            {
                ResultsList.SelectedIndex--;
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            }
            else
            {
                NavigateHistory(1);
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_results.Count == 0)
            {
                await SearchAsync(QueryBox.Text);
            }

            await ExecuteSelectedAsync();
        }
    }

    private async Task ExecuteSelectedAsync()
    {
        var selected = ResultsList.SelectedItem as SearchResult ?? _results.FirstOrDefault();
        if (selected is null)
        {
            return;
        }

        _history.Add(QueryBox.Text);
        _historyIndex = -1;

        if (ShouldConfirm(selected.Action))
        {
            _pendingAction = selected.Action;
            var targetDescription = selected.Action.Type == FluxActionType.CloseAllExcept
                ? "Keep open: " + selected.Action.Target.Replace("\n", ", ", StringComparison.Ordinal)
                : "Target: " + selected.Action.Target;
            ShowConfirmation(
                selected.Action.DisplayName ?? "Run this action?",
                $"This can interrupt an application or cause unsaved work to be lost.\n\n{targetDescription}",
                selected.Action.Permission);
            return;
        }

        await ExecuteActionAsync(selected.Action);
    }

    private async Task ExecuteActionAsync(FluxAction action)
    {
        try
        {
            switch (action.Type)
            {
                case FluxActionType.Launch:
                    Process.Start(new ProcessStartInfo(action.Target) { UseShellExecute = true });
                    _settings.ApplicationUsage[action.Target] = _settings.ApplicationUsage.GetValueOrDefault(action.Target) + 1;
                    _settingsService.Save();
                    HideLauncher();
                    break;
                case FluxActionType.OpenPath:
                    Process.Start(new ProcessStartInfo(action.Target) { UseShellExecute = true });
                    HideLauncher();
                    break;
                case FluxActionType.CreateFolder:
                    Directory.CreateDirectory(action.Target);
                    ShowStatus("Folder created", action.Target, "DONE");
                    break;
                case FluxActionType.ShowProcesses:
                    await ShowProcessesAsync(action.Target);
                    break;
                case FluxActionType.ShowSystemStats:
                    await ShowSystemStatsAsync();
                    break;
                case FluxActionType.TerminateProcess:
                case FluxActionType.RestartProcess:
                    await ExecuteProcessActionAsync(action);
                    break;
                case FluxActionType.CloseAllExcept:
                    await ExecuteCloseAllExceptAsync(action);
                    break;
                case FluxActionType.AskAi:
                    await RunAgentAsync(action.Target);
                    break;
            }
        }
        catch (Exception exception)
        {
            _log.Error($"Action {action.Type} failed.", exception);
            ShowStatus("Action failed", exception.Message, "ERROR");
        }
    }

    private async Task ShowProcessesAsync(string sort)
    {
        ShowBusy("SYSTEM");
        var processes = await _processes.ListAsync();
        var ordered = sort == "cpu"
            ? processes.OrderByDescending(item => item.CpuPercent)
            : processes.OrderByDescending(item => item.WorkingSetBytes);
        var lines = ordered.Take(12).Select(item =>
            $"{item.Name,-24} {item.WorkingSetBytes / 1024d / 1024d,7:N0} MB   {item.CpuPercent,5:N1}% CPU" +
            (item.Responding ? string.Empty : "   NOT RESPONDING"));
        ShowStatus(
            sort == "cpu" ? "Top CPU users" : "Top memory users",
            string.Join(Environment.NewLine, lines),
            "LIVE SYSTEM SNAPSHOT");
    }

    private async Task ShowSystemStatsAsync()
    {
        ShowBusy("SYSTEM");
        var snapshot = await _systemInfo.GetSnapshotAsync();
        var usedMemory = snapshot.TotalMemoryBytes - snapshot.AvailableMemoryBytes;
        var builder = new StringBuilder();
        builder.AppendLine($"CPU    {snapshot.CpuPercent:N1}%");
        builder.AppendLine($"RAM    {FormatBytes(usedMemory)} used of {FormatBytes(snapshot.TotalMemoryBytes)}");
        builder.AppendLine($"Network    {(snapshot.NetworkAvailable ? "connected" : "offline")}");
        if (snapshot.BatteryStatus is not null)
        {
            builder.AppendLine($"Battery    {snapshot.BatteryStatus}");
        }
        foreach (var drive in snapshot.Drives)
        {
            builder.AppendLine($"Drive {drive.Name}    {FormatBytes((ulong)drive.AvailableBytes)} free of {FormatBytes((ulong)drive.TotalBytes)}");
        }
        builder.AppendLine(snapshot.WindowsVersion);
        ShowStatus("This PC", builder.ToString().Trim(), "SYSTEM");
    }

    private async Task ExecuteProcessActionAsync(FluxAction action)
    {
        var gracefulClose = action.Arguments?.GetValueOrDefault("verb") == "close";
        if (action.Type != FluxActionType.RestartProcess)
        {
            var directResult = gracefulClose
                ? await _processes.CloseApplicationAsync(action.Target)
                : await _processes.TerminateApplicationAsync(action.Target);
            ShowToolResults([directResult]);
            return;
        }

        var processes = await _processes.ListAsync();
        var match = processes
            .Where(item => item.IsUserApplication &&
                (string.Equals(item.Name, action.Target, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(item.FriendlyName, action.Target, StringComparison.OrdinalIgnoreCase)))
            .Select(item => (Process: item, Score: 1))
            .FirstOrDefault();

        if (match.Process is null)
        {
            ShowStatus("Application not found", $"No running application matched “{action.Target}”.", "NOTHING CHANGED");
            return;
        }

        if (match.Process.IsProtected)
        {
            ShowStatus("Action refused", $"{match.Process.Name} is protected because closing it could destabilize Windows or Flux.", "SAFETY");
            return;
        }

        string? executable = null;
        if (action.Type == FluxActionType.RestartProcess)
        {
            try
            {
                using var process = Process.GetProcessById(match.Process.Id);
                executable = process.MainModule?.FileName;
            }
            catch
            {
            }
        }

        var result = await _processes.TerminateAsync(match.Process.Id);
        if (result.Success && executable is not null)
        {
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
            result = result with { Output = result.Output + " Restarted it." };
        }
        ShowToolResults([result]);
    }

    private async Task ExecuteCloseAllExceptAsync(FluxAction action)
    {
        var exclusions = action.Target.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = await _processes.CloseAllExceptAsync(exclusions);
        ShowToolResults([result]);
    }

    private async Task RunAgentAsync(string request)
    {
        ShowBusy("LOCAL AI");
        try
        {
            var streamedText = new StringBuilder();
            var streamStarted = false;
            var progress = new Progress<string>(chunk =>
            {
                if (!streamStarted)
                {
                    streamStarted = true;
                    ShowStatus("Flux", string.Empty, "LOCAL AI");
                }
                streamedText.Append(chunk);
                StatusText.Text = NormalizeAiText(streamedText.ToString());
            });
            var context = _results.Count == 0
                ? "No deterministic search results were relevant."
                : "Search candidates: " + string.Join("; ", _results.Take(5).Select(result => $"{result.Title} [{result.Subtitle}]"));
            var result = await _agent.RunAsync(request, context, progress);
            if (result.PendingActions.Count > 0)
            {
                if (!_settings.ConfirmBeforeClosingApplications && result.PendingActions.All(IsApplicationCloseTool))
                {
                    ShowBusy("EXECUTING");
                    ShowToolResults(await ExecutePendingToolsAsync(result.PendingActions));
                    return;
                }

                _pendingTools = result.PendingActions;
                var details = string.Join(Environment.NewLine, result.PendingActions.Select(pending => pending.ConfirmationText));
                ShowConfirmation(
                    "Confirm action",
                    details,
                    result.PendingActions.Max(item => item.Definition.Permission));
            }
            else if (result.ExecutedTools.Any(IsActionResult))
            {
                ShowToolResults(result.ExecutedTools.Where(IsActionResult).ToArray());
            }
            else
            {
                var finalText = NormalizeAiText(result.Text);
                if (!streamStarted || !string.Equals(StatusText.Text, finalText, StringComparison.Ordinal))
                {
                    ShowStatus("Flux", string.IsNullOrWhiteSpace(finalText) ? "Done." : finalText, "LOCAL AI");
                }
            }
        }
        catch (Exception exception)
        {
            _log.Error("AI request failed.", exception);
            ShowStatus("Local model unavailable", exception.Message + "\n\nOpen Settings to test the endpoint and choose an installed model.", "LOCAL AI");
        }
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var directAction = _pendingAction;
        var toolCalls = _pendingTools;
        _pendingAction = null;
        _pendingTools = Array.Empty<PendingToolCall>();

        if (directAction is not null)
        {
            await ExecuteActionAsync(directAction);
            return;
        }

        ShowBusy("EXECUTING");
        ShowToolResults(await ExecutePendingToolsAsync(toolCalls));
    }

    private async Task<IReadOnlyList<ToolResult>> ExecutePendingToolsAsync(IReadOnlyList<PendingToolCall> toolCalls)
    {
        var results = new List<ToolResult>();
        foreach (var pending in toolCalls)
        {
            if (!_tools.TryGet(pending.Call.Name, out var tool) || tool is null)
            {
                results.Add(new ToolResult(pending.Call.Id, pending.Call.Name, false, "No action was performed."));
                continue;
            }

            try
            {
                results.Add(await tool.ExecuteAsync(pending.Call));
            }
            catch (Exception exception)
            {
                _log.Error($"Tool {pending.Call.Name} failed.", exception);
                results.Add(new ToolResult(pending.Call.Id, pending.Call.Name, false, "The action could not be completed."));
            }
        }
        return results;
    }

    private bool ShouldConfirm(FluxAction action) =>
        action.Permission >= PermissionLevel.Disruptive &&
        (_settings.ConfirmBeforeClosingApplications || !IsGracefulApplicationClose(action));

    private static bool IsGracefulApplicationClose(FluxAction action) =>
        action.Type == FluxActionType.CloseAllExcept ||
        action.Type == FluxActionType.TerminateProcess &&
        string.Equals(action.Arguments?.GetValueOrDefault("verb"), "close", StringComparison.OrdinalIgnoreCase);

    private static bool IsApplicationCloseTool(PendingToolCall pending) =>
        pending.Definition.Name is "close_application" or "close_applications" or "close_applications_except";

    private bool IsActionResult(ToolResult result) =>
        _tools.TryGet(result.Name, out var tool) && tool is not null &&
        tool.Definition.Permission > PermissionLevel.ReadOnly;

    private void ShowToolResults(IReadOnlyList<ToolResult> results)
    {
        if (results.Count == 0)
        {
            ShowStatus("No action was performed", string.Empty, "NOTHING CHANGED");
            return;
        }

        var output = string.Join(Environment.NewLine, results.Select(result => result.Output).Where(value => !string.IsNullOrWhiteSpace(value)));
        var lines = NormalizeAiText(output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var title = lines.FirstOrDefault() ?? (results.All(result => result.Success) ? "Done" : "Action failed");
        var details = lines.Length > 1 ? string.Join(Environment.NewLine, lines.Skip(1)) : string.Empty;
        ShowStatus(title, details, results.All(result => result.Success) ? "DONE" : "CHECK RESULT");
    }

    private static string NormalizeAiText(string text) =>
        (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

    private void CancelConfirmation_Click(object sender, RoutedEventArgs e)
    {
        _pendingAction = null;
        _pendingTools = Array.Empty<PendingToolCall>();
        ShowStatus("Cancelled", "Nothing was changed.", "SAFE");
    }

    private void ShowResults()
    {
        BusyPanel.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Collapsed;
        ConfirmationPanel.Visibility = Visibility.Collapsed;
        ResultsList.Visibility = Visibility.Visible;
        RevealResultsSurface();
        Footer.Visibility = Visibility.Visible;
        ModeLabel.Text = _results.FirstOrDefault()?.Kind == SearchResultKind.Ai ? "LOCAL AI ON ENTER" : "DETERMINISTIC";
        ResultsList.SelectedIndex = _results.Count > 0 ? 0 : -1;
        AnimateElement(ResultsList);
        AnimateHeight(Math.Min(650, 156 + Math.Max(82, _results.Count * 68)));
    }

    private void ShowStatus(string title, string text, string eyebrow)
    {
        ResultsList.Visibility = Visibility.Collapsed;
        BusyPanel.Visibility = Visibility.Collapsed;
        ConfirmationPanel.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Visible;
        RevealResultsSurface();
        Footer.Visibility = Visibility.Visible;
        StatusEyebrow.Text = eyebrow;
        StatusTitle.Text = title;
        StatusText.Text = text;
        ModeLabel.Text = "ESC TO DISMISS";
        AnimateElement(StatusPanel);
        AnimateHeight(466);
    }

    private void ShowBusy(string mode)
    {
        ResultsList.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Collapsed;
        ConfirmationPanel.Visibility = Visibility.Collapsed;
        BusyPanel.Visibility = Visibility.Visible;
        RevealResultsSurface();
        Footer.Visibility = Visibility.Visible;
        ModeLabel.Text = mode;
        AnimateElement(BusyPanel);
        AnimateHeight(270);
    }

    private void ShowConfirmation(string title, string details, PermissionLevel permission)
    {
        ResultsList.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Collapsed;
        BusyPanel.Visibility = Visibility.Collapsed;
        ConfirmationPanel.Visibility = Visibility.Visible;
        RevealResultsSurface();
        Footer.Visibility = Visibility.Visible;
        ConfirmationTitle.Text = title;
        ConfirmationDetails.Text = details;
        ConfirmButton.Content = permission == PermissionLevel.Destructive ? "Allow destructive action" : "Confirm";
        ModeLabel.Text = $"PERMISSION LEVEL {(int)permission}";
        AnimateElement(ConfirmationPanel);
        AnimateHeight(430);
    }

    private void ResetPanels()
    {
        _results.Clear();
        ResultsList.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Collapsed;
        BusyPanel.Visibility = Visibility.Collapsed;
        ConfirmationPanel.Visibility = Visibility.Collapsed;
        ResultsSurface.Visibility = Visibility.Collapsed;
        ResultsSurface.BeginAnimation(OpacityProperty, null);
        ResultsSurface.Opacity = 1;
        ResultsTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        ResultsTranslate.Y = 0;
        Footer.Visibility = Visibility.Collapsed;
        _pendingAction = null;
        _pendingTools = Array.Empty<PendingToolCall>();
    }

    private void NavigateHistory(int delta)
    {
        if (_history.Items.Count == 0)
        {
            return;
        }

        _historyIndex = Math.Clamp(_historyIndex + delta, -1, _history.Items.Count - 1);
        QueryBox.Text = _historyIndex < 0 ? string.Empty : _history.Items[_historyIndex];
        QueryBox.CaretIndex = QueryBox.Text.Length;
    }

    private void PositionOnActiveScreen()
    {
        var mouse = System.Windows.Forms.Control.MousePosition;
        var screen = System.Windows.Forms.Screen.FromPoint(mouse).WorkingArea;
        Left = screen.Left + (screen.Width - Width) / 2d;
        Top = screen.Top + Math.Max(72, screen.Height * 0.18);
    }

    private static string FormatBytes(ulong bytes)
    {
        var gibibytes = bytes / 1024d / 1024d / 1024d;
        return gibibytes.ToString("N1", CultureInfo.CurrentCulture) + " GB";
    }

    private async void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await ExecuteSelectedAsync();

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private void AnimateLauncherOpen()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190)) { EasingFunction = easing });
        LauncherScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(230)) { EasingFunction = easing });
        LauncherScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(230)) { EasingFunction = easing });
        LauncherTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(-11, 0, TimeSpan.FromMilliseconds(230)) { EasingFunction = easing });
        SearchPill.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.24, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = easing });
        BeginParticleBuild();
    }

    private void BeginParticleBuild()
    {
        ParticleCanvas.Children.Clear();
        var random = Random.Shared;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var width = ActualWidth > 0 ? ActualWidth : Width;
        const double top = 9;
        const double bottom = 94;
        const double left = 10;
        var right = Math.Max(left + 1, width - 10);
        const int particleCount = 56;

        for (var index = 0; index < particleCount; index++)
        {
            var horizontalEdge = random.NextDouble() < 0.76;
            var targetX = horizontalEdge
                ? left + random.NextDouble() * (right - left)
                : random.NextDouble() < 0.5 ? left : right;
            var targetY = horizontalEdge
                ? random.NextDouble() < 0.5 ? top : bottom
                : top + random.NextDouble() * (bottom - top);
            var fromCentreX = targetX - width / 2;
            var fromCentreY = targetY - (top + bottom) / 2;
            var distance = 28 + random.NextDouble() * 66;
            var length = Math.Max(1, Math.Sqrt(fromCentreX * fromCentreX + fromCentreY * fromCentreY));
            var startOffsetX = fromCentreX / length * distance + (random.NextDouble() - 0.5) * 18;
            var startOffsetY = fromCentreY / length * distance + (random.NextDouble() - 0.5) * 18;
            var size = 1.8 + random.NextDouble() * 3.4;
            var particle = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(index % 5 == 0
                    ? System.Windows.Media.Color.FromRgb(156, 145, 255)
                    : index % 7 == 0 ? System.Windows.Media.Color.FromRgb(111, 94, 249) : Colors.White),
                Opacity = 0,
                IsHitTestVisible = false,
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5)
            };
            var transform = new TransformGroup();
            var scale = new ScaleTransform(0.35, 0.35);
            var translate = new TranslateTransform(startOffsetX, startOffsetY);
            transform.Children.Add(scale);
            transform.Children.Add(translate);
            particle.RenderTransform = transform;
            Canvas.SetLeft(particle, targetX - size / 2);
            Canvas.SetTop(particle, targetY - size / 2);
            ParticleCanvas.Children.Add(particle);

            var delay = TimeSpan.FromMilliseconds(random.Next(0, 95));
            var travel = TimeSpan.FromMilliseconds(random.Next(340, 590));
            var total = delay + travel;
            translate.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(startOffsetX, 0, travel) { BeginTime = delay, EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } });
            translate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(startOffsetY, 0, travel) { BeginTime = delay, EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } });
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.35, 1.15, travel) { BeginTime = delay, EasingFunction = easing });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.35, 1.15, travel) { BeginTime = delay, EasingFunction = easing });

            var opacity = new DoubleAnimationUsingKeyFrames { Duration = total + TimeSpan.FromMilliseconds(80) };
            opacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(delay)));
            opacity.KeyFrames.Add(new EasingDoubleKeyFrame(0.96, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(55)), easing));
            opacity.KeyFrames.Add(new EasingDoubleKeyFrame(0.58, KeyTime.FromTimeSpan(total), easing));
            opacity.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(total + TimeSpan.FromMilliseconds(80)), easing));
            particle.BeginAnimation(OpacityProperty, opacity);
        }
    }

    private void HideLauncher()
    {
        if (!IsVisible || _isHiding)
        {
            return;
        }

        _isHiding = true;
        var generation = ++_animationGeneration;
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        var opacity = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(90)) { EasingFunction = easing };
        opacity.Completed += (_, _) =>
        {
            if (generation != _animationGeneration)
            {
                return;
            }

            Hide();
            ParticleCanvas.Children.Clear();
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            _isHiding = false;
        };
        BeginAnimation(OpacityProperty, opacity);
        LauncherScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
            new DoubleAnimation(LauncherScale.ScaleX, 0.986, TimeSpan.FromMilliseconds(90)) { EasingFunction = easing });
        LauncherScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
            new DoubleAnimation(LauncherScale.ScaleY, 0.986, TimeSpan.FromMilliseconds(90)) { EasingFunction = easing });
        LauncherTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(LauncherTranslate.Y, -4, TimeSpan.FromMilliseconds(90)) { EasingFunction = easing });
    }

    private void RevealResultsSurface()
    {
        if (ResultsSurface.Visibility == Visibility.Visible)
        {
            return;
        }

        ResultsSurface.Visibility = Visibility.Visible;
        ResultsSurface.Opacity = 0;
        ResultsTranslate.Y = -5;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        ResultsSurface.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(115)) { EasingFunction = easing });
        ResultsTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(-5, 0, TimeSpan.FromMilliseconds(145)) { EasingFunction = easing });
    }

    private void AnimateHeight(double targetHeight)
    {
        var from = ActualHeight > 0 ? ActualHeight : Height;
        Height = targetHeight;
        if (!IsVisible || Math.Abs(from - targetHeight) < 1)
        {
            return;
        }

        var animation = new DoubleAnimation(from, targetHeight, TimeSpan.FromMilliseconds(145))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        BeginAnimation(HeightProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void AnimateElement(UIElement element)
    {
        element.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(95))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!_settingsOpen && ConfirmationPanel.Visibility != Visibility.Visible && !IsKeyboardFocusWithin)
        {
            HideLauncher();
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideLauncher();
        }
    }
}
