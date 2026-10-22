using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Clementine.App.Tray;

/// <summary>Icona nella tray e il menu condiviso con il clic sinistro sull'isola, ricostruito a ogni apertura.</summary>
sealed class TrayIcon : IDisposable
{
    readonly NotifyIcon _icon;
    readonly ContextMenuStrip _menu = new();
    readonly NativeWindow _foreground = new();

    public TrayIcon(Action<ToolStripItemCollection> buildMenu)
    {
        _menu.Opening += (_, e) =>
        {
            _menu.Items.Clear();
            buildMenu(_menu.Items);
            e.Cancel = false; // un menu vuoto parte con Cancel = true
        };
        _foreground.CreateHandle(new CreateParams());
        _icon = new NotifyIcon { Text = "Clementine", Icon = CreateIcon(), ContextMenuStrip = _menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMenu(Cursor.Position); };
    }

    /// <summary>Apre il menu in un punto dello schermo (pixel).</summary>
    public void ShowMenu(Point screen)
    {
        // senza essere in primo piano il menu non si chiuderebbe cliccando altrove (stesso trucco di NotifyIcon)
        SetForegroundWindow(_foreground.Handle);
        _menu.Show(screen);
    }

    public void Dispose()
    {
        _icon.Dispose();
        _menu.Dispose();
        _foreground.DestroyHandle();
    }

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

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);
}
