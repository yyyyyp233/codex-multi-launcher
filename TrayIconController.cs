using DrawingIcon = System.Drawing.Icon;
using DrawingSystemIcons = System.Drawing.SystemIcons;
using FormsContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using FormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using FormsToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;
using FormsToolStripSeparator = System.Windows.Forms.ToolStripSeparator;

namespace CodexChannelLauncher;

internal sealed class TrayIconController : IDisposable
{
    private readonly FormsNotifyIcon notifyIcon;
    private readonly FormsContextMenuStrip contextMenu;
    private readonly DrawingIcon icon;
    private bool disposed;

    public TrayIconController(Action showWindow, Action exitApplication)
    {
        ArgumentNullException.ThrowIfNull(showWindow);
        ArgumentNullException.ThrowIfNull(exitApplication);

        icon = LoadApplicationIcon();
        var showItem = new FormsToolStripMenuItem("显示主窗口");
        var exitItem = new FormsToolStripMenuItem("退出多开器");
        showItem.Click += (_, _) => showWindow();
        exitItem.Click += (_, _) => exitApplication();

        contextMenu = new FormsContextMenuStrip();
        contextMenu.Items.Add(showItem);
        contextMenu.Items.Add(new FormsToolStripSeparator());
        contextMenu.Items.Add(exitItem);

        notifyIcon = new FormsNotifyIcon
        {
            Icon = icon,
            Text = "Codex 多开器",
            ContextMenuStrip = contextMenu,
            Visible = true
        };
        notifyIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == System.Windows.Forms.MouseButtons.Left)
            {
                showWindow();
            }
        };
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        contextMenu.Dispose();
        icon.Dispose();
    }

    private static DrawingIcon LoadApplicationIcon()
    {
        DrawingIcon? extracted = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                extracted = DrawingIcon.ExtractAssociatedIcon(Environment.ProcessPath);
            }

            return (DrawingIcon)(extracted ?? DrawingSystemIcons.Application).Clone();
        }
        finally
        {
            extracted?.Dispose();
        }
    }
}
