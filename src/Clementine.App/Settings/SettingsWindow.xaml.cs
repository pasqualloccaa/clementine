using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Clementine.App.Settings;

/// <summary>Impostazioni: ogni casella si applica subito, come nelle impostazioni di Windows.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(bool autostart, Action<bool> setAutostart, bool explorerMenu, Action<bool> setExplorerMenu,
        string? libreOffice, string logs)
    {
        InitializeComponent();
        Theme.Apply(this);
        AutostartBox.IsChecked = autostart;
        AutostartBox.Click += (_, _) => setAutostart(AutostartBox.IsChecked == true);
        ExplorerMenuBox.IsChecked = explorerMenu;
        ExplorerMenuBox.Click += (_, _) => setExplorerMenu(ExplorerMenuBox.IsChecked == true);
        LibreOfficeText.Text = libreOffice is null
            ? "Non trovato: senza LibreOffice niente conversioni di Word, Excel e PowerPoint. Installalo e riavvia Clementine."
            : $"Trovato in {Path.GetDirectoryName(Path.GetDirectoryName(libreOffice))}";
        LogsLink.Click += (_, _) => Process.Start("explorer.exe", $"\"{logs}\"");
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
