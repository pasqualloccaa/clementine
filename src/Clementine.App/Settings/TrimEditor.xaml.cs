using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Clementine.App.Settings;

/// <summary>
/// Taglia guardando il video: si scorre, si mette "inizio" e "fine" e si vede la parte tenuta sulla barra.
/// Riproduce con Media Foundation di Windows (MediaElement); se non sa aprire il file si torna al minutaggio scritto a mano.
/// </summary>
public partial class TrimEditor : Window
{
    const double Frame = 1 / 30.0;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    bool _playing, _updating, _failed;
    double _start, _end;
    string? _range;

    TrimEditor(string file)
    {
        InitializeComponent();
        Theme.Apply(this);
        Title = $"Taglia – {Path.GetFileName(file)}";
        Player.Source = new Uri(file);
        Player.MediaOpened += (_, _) =>
        {
            var length = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalSeconds : 0;
            Timeline.Maximum = length;
            Timeline.SmallChange = Frame;
            Timeline.LargeChange = 1;
            (_start, _end) = (0, length);
            Seek(0);
        };
        Player.MediaFailed += (_, _) => { _failed = true; Close(); };
        Player.MediaEnded += (_, _) => Pause();
        Player.Play(); // apre il file; MediaOpened mette subito in pausa sul primo fotogramma
        Player.Pause();
        _tick.Tick += (_, _) => ShowPosition(Player.Position.TotalSeconds);
        Timeline.ValueChanged += (_, e) => { if (!_updating) Seek(e.NewValue); };
        PreviewKeyDown += OnKey;
        Closed += (_, _) => { _tick.Stop(); Player.Close(); };
    }

    /// <summary>"inizio-fine" in secondi (vedi ToolArgs.TimeRange); null se annullato; false in <paramref name="opened"/> se il video non si apre.</summary>
    public static string? Ask(string file, out bool opened)
    {
        var editor = new TrimEditor(file);
        editor.ShowDialog();
        opened = !editor._failed;
        return editor._range;
    }

    void Seek(double seconds)
    {
        seconds = Math.Clamp(seconds, 0, Timeline.Maximum);
        Player.Position = TimeSpan.FromSeconds(seconds);
        ShowPosition(seconds);
    }

    void ShowPosition(double seconds)
    {
        _updating = true;
        Timeline.Value = seconds;
        _updating = false;
        Timeline.SelectionStart = _start;
        Timeline.SelectionEnd = _end;
        Position.Text = $"{Clock(seconds)} / {Clock(Timeline.Maximum)}";
        Selection.Text = $"Tieni da {Clock(_start)} a {Clock(_end)}  ({_end - _start:0.0} s)";
    }

    static string Clock(double seconds) => TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss\.f" : @"m\:ss\.f", CultureInfo.InvariantCulture);

    void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) Pause();
        else
        {
            Player.Play();
            _tick.Start();
            _playing = true;
            PlayButton.Content = "⏸  Pausa";
        }
    }

    void Pause()
    {
        Player.Pause();
        _tick.Stop();
        _playing = false;
        PlayButton.Content = "▶  Riproduci";
        ShowPosition(Player.Position.TotalSeconds);
    }

    void Start_Click(object sender, RoutedEventArgs e)
    {
        _start = Timeline.Value;
        if (_end <= _start) _end = Timeline.Maximum;
        ShowPosition(Timeline.Value);
    }

    void End_Click(object sender, RoutedEventArgs e)
    {
        _end = Timeline.Value;
        if (_start >= _end) _start = 0;
        ShowPosition(Timeline.Value);
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : Frame;
        switch (e.Key)
        {
            case Key.Space: Play_Click(this, e); break;
            case Key.Left: if (_playing) Pause(); Seek(Timeline.Value - step); break;
            case Key.Right: if (_playing) Pause(); Seek(Timeline.Value + step); break;
            case Key.I: Start_Click(this, e); break;
            case Key.O: End_Click(this, e); break;
            default: return;
        }
        e.Handled = true;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_end - _start < 0.1) return; // niente da tenere
        _range = FormattableString.Invariant($"{_start:0.###}-{_end:0.###}");
        DialogResult = true;
    }
}
