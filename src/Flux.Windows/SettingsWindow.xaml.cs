using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Flux.Windows.Ai;
using Flux.Core;
using Flux.Windows.Configuration;
using Flux.Windows.SystemIntegration;

namespace Flux.Windows;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly StartupManager _startup;
    private readonly ICommandHistory _history;
    private readonly OllamaDiscoveryService _discovery = new();
    private readonly HotkeyChoice[] _hotkeys =
    [
        new("Alt + Space", 0x0001, 0x20),
        new("Ctrl + Space", 0x0002, 0x20),
        new("Ctrl + Alt + Space", 0x0003, 0x20)
    ];
    private bool _closing;

    public SettingsWindow(SettingsService settingsService, StartupManager startup, ICommandHistory history)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _startup = startup;
        _history = history;
        var settings = settingsService.Current;

        ModeBox.ItemsSource = Enum.GetValues<AiMode>();
        ModeBox.SelectedItem = settings.AiMode;
        LocalEndpointBox.Text = settings.LocalEndpoint;
        LocalModelBox.Text = settings.LocalModel;
        CloudModelBox.Text = settings.CloudModel;
        StartupBox.IsChecked = _startup.IsEnabled;
        UpdateCheckBox.IsChecked = settings.CheckForUpdatesOnLaunch;
        HotkeyBox.ItemsSource = _hotkeys;
        HotkeyBox.SelectedItem = _hotkeys.FirstOrDefault(choice =>
            choice.Modifiers == settings.HotkeyModifiers && choice.Key == settings.HotkeyKey) ?? _hotkeys[0];
        HistoryLimitBox.Text = settings.HistoryLimit.ToString();
        UpdateEnabledState();
    }

    private async void TestLocal_Click(object sender, RoutedEventArgs e)
    {
        LocalStatus.Text = "Checking…";
        var result = await _discovery.ProbeAsync(LocalEndpointBox.Text);
        LocalStatus.Text = result.Message;
        LocalStatus.Foreground = result.Available
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(91, 207, 145))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 140, 120));

        if (result.Models.Count > 0)
        {
            var current = LocalModelBox.Text;
            LocalModelBox.ItemsSource = result.Models;
            LocalModelBox.Text = result.Models.Contains(current, StringComparer.OrdinalIgnoreCase)
                ? current
                : result.Models[0];
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(HistoryLimitBox.Text, out var historyLimit) || historyLimit is < 0 or > 1000)
        {
            System.Windows.MessageBox.Show("History retention must be between 0 and 1000.", "Flux Settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!Uri.TryCreate(LocalEndpointBox.Text, UriKind.Absolute, out _))
        {
            System.Windows.MessageBox.Show("Enter a valid absolute local endpoint.", "Flux Settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = _settingsService.Current;
        settings.AiMode = (AiMode)(ModeBox.SelectedItem ?? AiMode.Local);
        settings.LocalEndpoint = LocalEndpointBox.Text.TrimEnd('/');
        settings.LocalModel = LocalModelBox.Text.Trim();
        settings.CloudModel = CloudModelBox.Text.Trim();
        settings.StartWithWindows = StartupBox.IsChecked == true;
        settings.CheckForUpdatesOnLaunch = UpdateCheckBox.IsChecked == true;
        var hotkey = HotkeyBox.SelectedItem as HotkeyChoice ?? _hotkeys[0];
        settings.HotkeyModifiers = hotkey.Modifiers;
        settings.HotkeyKey = hotkey.Key;
        settings.HistoryLimit = historyLimit;

        try
        {
            _startup.SetEnabled(settings.StartWithWindows);
            _settingsService.Save();
            CloseWithResult(true);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(exception.Message, "Could not save Flux Settings",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _history.Clear();
            System.Windows.MessageBox.Show("Command history cleared.",
                "Flux Settings", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(exception.Message, "Could not clear history",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateEnabledState();

    private void UpdateEnabledState()
    {
        var mode = ModeBox.SelectedItem is AiMode selected ? selected : AiMode.Local;
        var localEnabled = mode is AiMode.Local or AiMode.Automatic;
        LocalEndpointBox.IsEnabled = localEnabled;
        LocalModelBox.IsEnabled = localEnabled;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseWithResult(false);

    private void Close_Click(object sender, RoutedEventArgs e) => CloseWithResult(false);

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SettingsScale.ScaleX = 0.975;
        SettingsScale.ScaleY = 0.975;
        SettingsTranslate.Y = 8;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = easing });
        SettingsScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.975, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = easing });
        SettingsScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.975, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = easing });
        SettingsTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(170)) { EasingFunction = easing });
    }

    private void CloseWithResult(bool result)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        var opacity = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(105)) { EasingFunction = easing };
        opacity.Completed += (_, _) => DialogResult = result;
        BeginAnimation(OpacityProperty, opacity);
        SettingsScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
            new DoubleAnimation(SettingsScale.ScaleX, 0.985, TimeSpan.FromMilliseconds(105)) { EasingFunction = easing });
        SettingsScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
            new DoubleAnimation(SettingsScale.ScaleY, 0.985, TimeSpan.FromMilliseconds(105)) { EasingFunction = easing });
        SettingsTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(SettingsTranslate.Y, 5, TimeSpan.FromMilliseconds(105)) { EasingFunction = easing });
    }

    private sealed record HotkeyChoice(string Label, int Modifiers, int Key);
}
