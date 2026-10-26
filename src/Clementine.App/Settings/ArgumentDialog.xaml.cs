using System.Windows;
using System.Windows.Controls;
using Clementine.Core;
using Clementine.Engines;

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
            "tool:trim" => Text("Taglia", "Quale parte tenere? Inizio e fine in minuti:secondi.", "es. 0:30-1:45",
                t => ToolArgs.TimeRange(t) is null ? "Scrivi inizio e fine, con la fine dopo l'inizio: es. 0:30-1:45" : null),
            "tool:speed" => Choice("Velocità", "Quanto più veloce?", [("0,5× (rallentato)", "0.5"), ("1,5×", "1.5"), ("2×", "2")]),
            "tool:target-size" => Text("Comprimi a", "Peso massimo di ogni file.", "es. 2 MB oppure 500 KB",
                t => ToolArgs.Size(t) is null ? "Scrivi un peso: es. 2 MB oppure 500 KB" : null),
            "tool:crop" => Choice("Ritaglia", "Quali proporzioni? Si tiene la parte centrale.",
                [("1:1 quadrato", "1:1"), ("4:3", "4:3"), ("3:2", "3:2"), ("16:9", "16:9"), ("9:16 verticale", "9:16")]),
            "tool:pages" => Pages(files),
            "metadata:edit" => MetadataDialog.Ask(files, exiftool),
            _ => throw new ArgumentException($"{op.Id} non chiede parametri"),
        };
        return value is null ? null : op with { Argument = value };
    }

    static string? Pages(IReadOnlyList<string> files)
    {
        var count = files.Min(PdfiumEngine.PageCount); // con più PDF le pagine scelte devono esserci in tutti
        return Text("Pagine", $"Quali pagine tenere, e in che ordine? Il PDF ne ha {count}.", "es. 3,1,2 oppure 1-5,8",
            t => ToolArgs.Pages(t, count) is null ? $"Numeri da 1 a {count}, separati da virgole; intervalli con il trattino" : null);
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
