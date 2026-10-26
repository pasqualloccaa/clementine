using System.Drawing;
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
            // vale anche per i sottomenu, che usano il renderer comune
            ToolStripManager.Renderer = Settings.Theme.IsDark ? new DarkRenderer() : new ToolStripProfessionalRenderer();
            _menu.Items.Clear();
            buildMenu(_menu.Items);
            e.Cancel = false; // un menu vuoto parte con Cancel = true
        };
        _foreground.CreateHandle(new CreateParams());
        _icon = new NotifyIcon { Text = "Clementine", Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!), ContextMenuStrip = _menu, Visible = true };
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

    sealed class DarkRenderer() : ToolStripProfessionalRenderer(new DarkColors())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.FromArgb(240, 240, 240) : Color.Gray;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Color.FromArgb(240, 240, 240);
            base.OnRenderArrow(e);
        }
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        static readonly Color Back = Color.FromArgb(43, 43, 43), Hover = Color.FromArgb(65, 65, 65), Line = Color.FromArgb(75, 75, 75);
        public override Color ToolStripDropDownBackground => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Back;
    }

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);
}
