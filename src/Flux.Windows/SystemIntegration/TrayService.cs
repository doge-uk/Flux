using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using System.Diagnostics;

namespace Flux.Windows.SystemIntegration;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Drawing.Icon? _applicationIcon;
    private readonly Forms.ToolStripMenuItem _updateItem;
    private readonly Action _checkForUpdates;
    private Uri? _releaseUri;
    private Uri? _balloonUri;

    public TrayService(Action show, Action settings, Action checkForUpdates, Action exit)
    {
        _checkForUpdates = checkForUpdates;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Flux", null, (_, _) => show());
        menu.Items.Add("Settings", null, (_, _) => settings());
        _updateItem = new Forms.ToolStripMenuItem("Check for updates");
        _updateItem.Click += (_, _) => OpenReleaseOrCheck();
        menu.Items.Add(_updateItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _applicationIcon = Environment.ProcessPath is { } executable
            ? Drawing.Icon.ExtractAssociatedIcon(executable)
            : null;
        _icon = new Forms.NotifyIcon
        {
            Text = "Flux",
            Icon = _applicationIcon ?? Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => show();
        _icon.BalloonTipClicked += (_, _) => OpenUri(_balloonUri);
    }

    public void ShowWarning(string title, string message)
    {
        _balloonUri = null;
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.BalloonTipIcon = Forms.ToolTipIcon.Warning;
        _icon.ShowBalloonTip(4000);
    }

    public void ShowInformation(string title, string message)
    {
        _balloonUri = null;
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _icon.ShowBalloonTip(4000);
    }

    public void ShowUpdateAvailable(Version version, Uri releaseUri)
    {
        if (!IsSafeGitHubUri(releaseUri))
        {
            return;
        }

        _releaseUri = releaseUri;
        _balloonUri = releaseUri;
        _updateItem.Text = $"Download Flux {FormatVersion(version)}";
        _updateItem.Font = new Drawing.Font(_updateItem.Font, Drawing.FontStyle.Bold);
        _icon.BalloonTipTitle = $"Flux {FormatVersion(version)} is available";
        _icon.BalloonTipText = "Click to view the release and download the update.";
        _icon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _icon.ShowBalloonTip(6000);
    }

    public void ClearUpdateAvailable()
    {
        _releaseUri = null;
        _updateItem.Text = "Check for updates";
        _updateItem.Font = new Drawing.Font(_updateItem.Font, Drawing.FontStyle.Regular);
    }

    private void OpenReleaseOrCheck()
    {
        if (_releaseUri is not null)
        {
            OpenUri(_releaseUri);
            return;
        }

        _checkForUpdates();
    }

    private static void OpenUri(Uri? uri)
    {
        if (uri is null || !IsSafeGitHubUri(uri))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // A missing browser association must not affect the tray process.
        }
    }

    private static bool IsSafeGitHubUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    private static string FormatVersion(Version version) => $"v{version.Major}.{version.Minor}.{version.Build}";

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _applicationIcon?.Dispose();
    }
}
