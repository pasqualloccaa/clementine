using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Clementine.App.Settings;

/// <summary>Tema chiaro o scuro come le app di Windows (Impostazioni → Personalizzazione → Colori). Letto a ogni apertura.</summary>
public static class Theme
{
    public static bool IsDark =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;

    /// <summary>Colori di una finestra normale (Impostazioni, Benvenuto), barra del titolo compresa.</summary>
    public static void Apply(Window window)
    {
        if (!IsDark) return;
        var text = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
        window.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        window.Foreground = text;
        // gli stili di sistema prendono questi colori da qui: testo di caselle ed etichette, link
        window.Resources[SystemColors.ControlTextBrushKey] = text;
        window.Resources[SystemColors.HotTrackBrushKey] = new SolidColorBrush(Color.FromRgb(0x6C, 0xB8, 0xFF));
        // i pulsanti restano chiari (il loro stile di sistema non ha una versione scura): testo scuro
        window.Resources[typeof(Button)] = new Style(typeof(Button)) { Setters = { new Setter(Control.ForegroundProperty, Brushes.Black) } };
        // le caselle di testo invece si scuriscono: con lo stile di sistema il testo chiaro finiva su sfondo bianco
        window.Resources[typeof(TextBox)] = new Style(typeof(TextBox))
        {
            Setters =
            {
                new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B))),
                new Setter(Control.ForegroundProperty, text),
                new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70))),
                new Setter(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, text),
            },
        };
        window.SourceInitialized += (_, _) =>
        {
            var on = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        };
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
