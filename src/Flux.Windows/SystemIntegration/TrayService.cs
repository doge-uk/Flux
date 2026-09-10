using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Flux.Windows.Updates;

namespace Flux.Windows.SystemIntegration;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Drawing.Icon? _applicationIcon;
    private readonly Forms.ToolStripMenuItem _updateItem;
    private readonly Action _checkForUpdates;
    private readonly Action<UpdatePackage> _installUpdate;
    private UpdatePackage? _updatePackage;

    public TrayService(
        Action show,
        Action settings,
        Action checkForUpdates,
        Action<UpdatePackage> installUpdate,
        Action exit)
    {
        _checkForUpdates = checkForUpdates;
        _installUpdate = installUpdate;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Flux", null, (_, _) => show());
        menu.Items.Add("Settings", null, (_, _) => settings());
        _updateItem = new Forms.ToolStripMenuItem("Check for updates");
        _updateItem.Click += (_, _) => InstallOrCheck();
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
        _icon.BalloonTipClicked += (_, _) => InstallAvailableUpdate();
    }

    public void ShowWarning(string title, string message)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.BalloonTipIcon = Forms.ToolTipIcon.Warning;
        _icon.ShowBalloonTip(4000);
    }

    public void ShowInformation(string title, string message)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _icon.ShowBalloonTip(4000);
    }

    public void ShowUpdateAvailable(UpdatePackage package)
    {
        _updatePackage = package;
        _updateItem.Enabled = true;
        _updateItem.Text = $"Install Flux {FormatVersion(package.Version)}";
        _updateItem.Font = new Drawing.Font(_updateItem.Font, Drawing.FontStyle.Bold);
        _icon.BalloonTipTitle = $"Flux {FormatVersion(package.Version)} is ready";
        _icon.BalloonTipText = "Click to download, verify, and install the update.";
        _icon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _icon.ShowBalloonTip(6000);
    }

    public void ShowUpdateDownloading(Version version)
    {
        _updateItem.Enabled = false;
        _updateItem.Text = $"Downloading Flux {FormatVersion(version)}…";
    }

    public void RestoreUpdateAvailable()
    {
        if (_updatePackage is not null)
        {
            _updateItem.Enabled = true;
            _updateItem.Text = $"Install Flux {FormatVersion(_updatePackage.Version)}";
        }
    }

    public void ClearUpdateAvailable()
    {
        _updatePackage = null;
        _updateItem.Enabled = true;
        _updateItem.Text = "Check for updates";
        _updateItem.Font = new Drawing.Font(_updateItem.Font, Drawing.FontStyle.Regular);
    }

    private void InstallOrCheck()
    {
        if (_updatePackage is not null)
        {
            InstallAvailableUpdate();
            return;
        }

        _checkForUpdates();
    }

    private void InstallAvailableUpdate()
    {
        if (_updatePackage is null || !_updateItem.Enabled)
        {
            return;
        }

        _installUpdate(_updatePackage);
    }

    private static string FormatVersion(Version version) => $"v{version.Major}.{version.Minor}.{version.Build}";

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _applicationIcon?.Dispose();
    }
}
