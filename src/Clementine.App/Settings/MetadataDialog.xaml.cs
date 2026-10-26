using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using Clementine.Engines;
using static Clementine.Core.Lang;

namespace Clementine.App.Settings;

/// <summary>
/// Metadati di una foto: si leggono quelli del primo file, si scrivono solo i campi cambiati (su tutti i file trascinati).
/// Il risultato è l'argomento JSON di "metadata:edit" (vedi MetadataEngine.EditArgs).
/// </summary>
public partial class MetadataDialog : Window
{
    readonly Dictionary<string, string> _original = [];
    string? _changes;

    MetadataDialog()
    {
        InitializeComponent();
        Theme.Apply(this);
    }

    public static string? Ask(IReadOnlyList<string> files, string exiftool)
    {
        // su un thread del pool: ProcessRunner è asincrono e qui siamo sul thread della UI
        var tags = Task.Run(() => MetadataEngine.ReadAsync(exiftool, files[0], default)).GetAwaiter().GetResult();
        string Tag(params string[] names) => names.Select(n => tags.GetValueOrDefault(n)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

        var dialog = new MetadataDialog { Title = T($"Metadati – {Path.GetFileName(files[0])}{(files.Count > 1 ? $" e altri {files.Count - 1}" : "")}",
            $"Metadata – {Path.GetFileName(files[0])}{(files.Count > 1 ? $" and {files.Count - 1} more" : "")}") };
        dialog._original["Title"] = dialog.TitleBox.Text = Tag("Title", "ImageDescription");
        dialog._original["Author"] = dialog.AuthorBox.Text = Tag("Artist", "Creator");
        dialog._original["Copyright"] = dialog.CopyrightBox.Text = Tag("Copyright");
        dialog._original["Date"] = dialog.DateBox.Text = DateTime.TryParseExact(Tag("DateTimeOriginal"), "yyyy:MM:dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var taken) ? taken.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) : "";

        var camera = string.Join(" ", new[] { Tag("Make"), Tag("Model") }.Where(s => s != ""));
        var gps = Tag("GPSLatitude") is { Length: > 0 } lat ? $"{lat}, {Tag("GPSLongitude")}" : null;
        dialog.Info.Text = string.Join("\n", new[]
        {
            Tag("ImageSize") is { Length: > 0 } size ? T($"Dimensioni: {size.Replace('x', '×').Replace(' ', '×')} pixel", $"Size: {size.Replace('x', '×').Replace(' ', '×')} pixels") : null,
            camera != "" ? T($"Fotocamera: {camera}", $"Camera: {camera}") : null,
            Tag("LensModel") is { Length: > 0 } lens ? T($"Obiettivo: {lens}", $"Lens: {lens}") : null,
            gps is null ? T("Nessuna posizione GPS", "No GPS location") : T($"Posizione GPS: {gps}", $"GPS location: {gps}"),
        }.Where(s => s is not null));
        dialog.RemoveGpsBox.Content = gps is null ? T("Togli la posizione GPS (non c'è)", "Remove the GPS location (there is none)") : T("Togli la posizione GPS", "Remove the GPS location");
        dialog.RemoveGpsBox.IsEnabled = gps is not null || files.Count > 1;
        return dialog.ShowDialog() == true ? dialog._changes : null;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        var changes = new Dictionary<string, string>();
        void Compare(string field, string value)
        {
            if (value.Trim() != _original[field]) changes[field] = value.Trim();
        }
        Compare("Title", TitleBox.Text);
        Compare("Author", AuthorBox.Text);
        Compare("Copyright", CopyrightBox.Text);
        if (DateBox.Text.Trim() != _original["Date"])
        {
            if (MetadataEngine.ExifDate(DateBox.Text) is not { } date)
            {
                Error.Text = T("Data non valida: scrivi gg/mm/aaaa hh:mm, es. 26/10/2026 14:30", "Invalid date: enter dd/mm/yyyy hh:mm, e.g. 26/10/2026 14:30");
                Error.Visibility = Visibility.Visible;
                return;
            }
            changes["Date"] = date;
        }
        if (RemoveGpsBox.IsChecked == true) changes["RemoveGps"] = "1";
        if (changes.Count == 0)
        {
            Close(); // niente da cambiare: niente copia
            return;
        }
        _changes = JsonSerializer.Serialize(changes);
        DialogResult = true;
    }
}
