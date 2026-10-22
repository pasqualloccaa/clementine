using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Clementine.App.Tray;

sealed class TrayIcon : IDisposable
{
    readonly NotifyIcon _icon;

    public TrayIcon(Action exit)
    {
        _icon = new NotifyIcon { Text = "Clementine", Icon = CreateIcon(), ContextMenuStrip = new ContextMenuStrip(), Visible = true };
        _icon.ContextMenuStrip.Items.Add("Esci", null, (_, _) => exit());
    }

    public void Notify(string title, string text) => _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);

    public void Dispose() => _icon.Dispose();

    // shortcut: icona disegnata a runtime, sostituire con un .ico vero in M7
    static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        using (var orange = new SolidBrush(Color.FromArgb(245, 124, 0)))
        using (var green = new SolidBrush(Color.FromArgb(46, 125, 50)))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillEllipse(orange, 2, 5, 28, 26);
            g.FillEllipse(green, 15, 0, 11, 7);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
