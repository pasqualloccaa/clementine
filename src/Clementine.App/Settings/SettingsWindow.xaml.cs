using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using static Clementine.Core.Lang;

namespace Clementine.App.Settings;

/// <summary>Impostazioni: ogni casella si applica subito, come nelle impostazioni di Windows.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(bool autostart, Action<bool> setAutostart, bool explorerMenu, Action<bool> setExplorerMenu,
        string language, Action<string> setLanguage, string? libreOffice, string logs)
    {
        InitializeComponent();
        Theme.Apply(this);
        AutostartBox.IsChecked = autostart;
        AutostartBox.Click += (_, _) => setAutostart(AutostartBox.IsChecked == true);
        ExplorerMenuBox.IsChecked = explorerMenu;
        ExplorerMenuBox.Click += (_, _) => setExplorerMenu(ExplorerMenuBox.IsChecked == true);
        foreach (var choice in LanguageChoices.Children.OfType<RadioButton>())
        {
            choice.IsChecked = (string)choice.Tag == language;
            choice.Checked += (_, _) => setLanguage((string)choice.Tag);
        }
        LibreOfficeText.Text = libreOffice is null
            ? T("Non trovato: senza LibreOffice niente conversioni di Word, Excel e PowerPoint. Installalo e riavvia Clementine.",
                "Not found: without LibreOffice there are no Word, Excel and PowerPoint conversions. Install it and restart Clementine.")
            : T($"Trovato in {Path.GetDirectoryName(Path.GetDirectoryName(libreOffice))}", $"Found in {Path.GetDirectoryName(Path.GetDirectoryName(libreOffice))}");
        LogsLink.Click += (_, _) => Process.Start("explorer.exe", $"\"{logs}\"");
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
