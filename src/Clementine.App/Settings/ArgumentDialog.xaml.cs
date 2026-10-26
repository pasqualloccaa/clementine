using System.Windows;
using System.Windows.Controls;
using Clementine.Core;
using Clementine.Engines;
using static Clementine.Core.Lang;

namespace Clementine.App.Settings;

/// <summary>Il valore che chiedono gli strumenti con parametri, prima di mettere il lavoro in coda.</summary>
public partial class ArgumentDialog : Window
{
    Func<string, string?>? _validate;
    string? _value;

    ArgumentDialog(string title, string prompt)
    {
        InitializeComponent();
        Theme.Apply(this);
        Title = title;
        Prompt.Text = prompt;
    }

    /// <summary>L'operazione con il valore scelto, oppure null se l'utente annulla.</summary>
    public static Operation? Ask(Operation op, IReadOnlyList<string> files, string exiftool)
    {
        var value = op.Id switch
        {
            "tool:trim" => Trim(files),
            "tool:speed" => Choice(T("Velocità", "Speed"), T("Quanto più veloce?", "How much faster?"), [(T("0,5× (rallentato)", "0.5× (slowed down)"), "0.5"), (T("1,5×", "1.5×"), "1.5"), ("2×", "2")]),
            "tool:target-size" => Text(T("Comprimi a", "Compress to"), T("Peso massimo di ogni file.", "Maximum size of each file."), T("es. 2 MB oppure 500 KB", "e.g. 2 MB or 500 KB"),
                t => ToolArgs.Size(t) is null ? T("Scrivi un peso: es. 2 MB oppure 500 KB", "Enter a size: e.g. 2 MB or 500 KB") : null),
            // con più foto: l'editor sulla prima, la stessa parte (in proporzione) su tutte
            "tool:crop" => ImageEditor.Crop(files[0]),
            "tool:annotate" => ImageEditor.Annotate(files[0]),
            "tool:resize" => Text(T("Ridimensiona", "Resize"), T("Nuova misura del lato più lungo, in pixel. Le proporzioni restano quelle della foto.", "New size of the longest side, in pixels. The photo keeps its proportions."),
                T("es. 1080, 1920x1080 oppure 50%", "e.g. 1080, 1920x1080 or 50%"), t => ToolArgs.Resize(t) is null ? T("Scrivi una misura: es. 1080, 1920x1080 oppure 50%", "Enter a size: e.g. 1080, 1920x1080 or 50%") : null),
            "tool:pages" => Pages(files),
            "metadata:edit" => MetadataDialog.Ask(files, exiftool),
            _ => throw new ArgumentException($"{op.Id} non chiede parametri"),
        };
        return value is null ? null : op with { Argument = value };
    }

    // l'editor sul primo file, la stessa parte su tutti; se Windows non sa riprodurlo (es. alcuni MKV/WebM), il minutaggio a mano
    static string? Trim(IReadOnlyList<string> files) =>
        TrimEditor.Ask(files[0], out var opened) is { } range ? range
        : opened ? null
        : Text(T("Taglia", "Trim"), T("Windows non riesce a mostrare questo file. Quale parte tenere? Inizio e fine in minuti:secondi.", "Windows can't play this file. Which part do you want to keep? Start and end in minutes:seconds."), T("es. 0:30-1:45", "e.g. 0:30-1:45"),
            t => ToolArgs.TimeRange(t) is null ? T("Scrivi inizio e fine, con la fine dopo l'inizio: es. 0:30-1:45", "Enter a start and an end, with the end after the start: e.g. 0:30-1:45") : null);

    static string? Pages(IReadOnlyList<string> files)
    {
        var count = files.Min(PdfiumEngine.PageCount); // con più PDF le pagine scelte devono esserci in tutti
        return Text(T("Pagine", "Pages"), T($"Quali pagine tenere, e in che ordine? Il PDF ne ha {count}.", $"Which pages to keep, and in what order? The PDF has {count}."), T("es. 3,1,2 oppure 1-5,8", "e.g. 3,1,2 or 1-5,8"),
            t => ToolArgs.Pages(t, count) is null ? T($"Numeri da 1 a {count}, separati da virgole; intervalli con il trattino", $"Numbers from 1 to {count}, separated by commas; ranges with a hyphen") : null);
    }

    static string? Text(string title, string prompt, string example, Func<string, string?> validate)
    {
        var dialog = new ArgumentDialog(title, prompt) { _validate = validate };
        dialog.Input.Visibility = dialog.Example.Visibility = Visibility.Visible;
        dialog.Example.Text = example;
        dialog.Loaded += (_, _) => dialog.Input.Focus();
        return dialog.ShowDialog() == true ? dialog._value : null;
    }

    static string? Choice(string title, string prompt, (string Label, string Value)[] choices)
    {
        var dialog = new ArgumentDialog(title, prompt);
        dialog.Choices.Visibility = Visibility.Visible;
        foreach (var (label, value) in choices)
            dialog.Choices.Children.Add(new RadioButton { Content = label, Tag = value, Margin = new Thickness(0, 3, 0, 3), IsChecked = dialog.Choices.Children.Count == 0 });
        return dialog.ShowDialog() == true ? dialog._value : null;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_validate is not null)
        {
            if (_validate(Input.Text) is { } error)
            {
                Error.Text = error;
                Error.Visibility = Visibility.Visible;
                return;
            }
            _value = Input.Text.Trim();
        }
        else _value = Choices.Children.OfType<RadioButton>().First(r => r.IsChecked == true).Tag as string;
        DialogResult = true;
    }
}
