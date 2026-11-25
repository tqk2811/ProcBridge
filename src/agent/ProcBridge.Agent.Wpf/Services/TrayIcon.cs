using System.Drawing;
using System.Windows.Forms;

namespace ProcBridge.Agent.Wpf.Services;

/// <summary>Notification area icon with Open / Exit; owns the Windows Forms <see cref="NotifyIcon"/>.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(Action open, Action exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Mở ProcBridge Agent", null, (_, _) => open());
        menu.Items.Add("Thoát", null, (_, _) => exit());
        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "ProcBridge Agent",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
