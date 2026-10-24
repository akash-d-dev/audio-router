using System.Windows;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace AudioRouter.App;

internal sealed class TrayIcon : IDisposable
{
    private readonly Drawing.Icon image;
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ToolStripMenuItem state = new("Stopped") { Enabled = false };
    private bool disposed;

    public TrayIcon(Action open, Action exit)
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AudioRouter.ico"))
            ?? throw new InvalidOperationException("The application icon is missing.");
        using (resource.Stream)
        using (var original = new Drawing.Icon(resource.Stream)) image = (Drawing.Icon)original.Clone();
        menu.Items.Add(state);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Open Audio Router", null, (_, _) => open());
        menu.Items.Add("Exit Audio Router", null, (_, _) => exit());
        icon = new Forms.NotifyIcon { Icon = image, Text = "Audio Router · Stopped", ContextMenuStrip = menu, Visible = true };
        icon.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) open(); };
    }

    public bool IsVisible => !disposed && icon.Visible;
    public void SetRoutingState(bool running)
    {
        if (disposed) return;
        state.Text = running ? "Routing live" : "Stopped";
        icon.Text = running ? "Audio Router · Routing live" : "Audio Router · Stopped";
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        icon.Visible = false;
        icon.Dispose();
        menu.Dispose();
        image.Dispose();
    }
}
