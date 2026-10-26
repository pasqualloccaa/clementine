using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Clementine.Core;
using ImageMagick;

namespace Clementine.App.Settings;

/// <summary>
/// "Ritaglia…" e "Annota…" guardando la foto: si lavora su un'anteprima (lato lungo 1600 px) e il risultato
/// è in frazioni dell'immagine (ToolArgs.Rect / Mark), applicato poi da MagickEngine all'originale.
/// </summary>
public partial class ImageEditor : Window
{
    static readonly (string Label, double? Ratio)[] Ratios = [("Libero", null), ("1:1", 1), ("4:3", 4 / 3.0), ("3:2", 1.5), ("16:9", 16 / 9.0), ("9:16", 9 / 16.0)];
    static readonly (string Label, string Kind)[] Tools = [("➔ Freccia", Mark.Arrow), ("▭ Rettangolo", Mark.Box), ("T Testo", Mark.Label), ("■ Oscura", Mark.Redact)];
    static readonly string[] Colors = ["#E53935", "#FDD835", "#43A047", "#1E88E5", "#FFFFFF", "#000000"];

    readonly bool _cropping;
    readonly double _w, _h; // anteprima, in pixel
    readonly uint _fullWidth, _fullHeight;
    // ritaglio
    Rect _crop;
    double? _ratio;
    Point? _grab;
    // annotazioni
    readonly List<Mark> _marks = [];
    Mark? _current;
    string _kind = Mark.Arrow, _color = Colors[0];
    TextBox? _typing;

    Point? _from;
    string? _result;

    ImageEditor(string file, bool cropping)
    {
        InitializeComponent();
        Theme.Apply(this);
        _cropping = cropping;
        (var preview, _fullWidth, _fullHeight) = Load(file);
        Picture.Source = preview;
        (_w, _h) = (preview.PixelWidth, preview.PixelHeight); // in pixel, non in DIP: i dpi del PNG non contano
        Stage.Width = Picture.Width = _w;
        Stage.Height = Picture.Height = _h;
        _crop = new Rect(0, 0, _w, _h);
        Title = $"{(cropping ? "Ritaglia" : "Annota")} – {System.IO.Path.GetFileName(file)}";
        OkButton.Content = cropping ? "Ritaglia" : "Salva";
        if (cropping)
        {
            foreach (var (label, ratio) in Ratios) AddOption(label, label == "Libero", () => SetRatio(ratio));
            Hint.Text = "Trascina sulla foto per scegliere la parte da tenere; trascina dall'interno del riquadro per spostarlo.";
        }
        else
        {
            foreach (var (label, kind) in Tools) AddOption(label, kind == _kind, () => _kind = kind, "tool");
            Options.Children.Add(new Border { Width = 16 });
            foreach (var color in Colors)
                AddOption(new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                    Background = Brush(color) }, color == _color, () => _color = color, "color");
            UndoButton.Visibility = Visibility.Visible;
            Hint.Text = "Trascina sulla foto per disegnare; con Testo fai clic dove scrivere e premi Invio. Oscura copre con un riquadro nero.";
        }
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && _typing is null) { Undo_Click(this, e); e.Handled = true; }
        };
        Redraw();
    }

    /// <summary>Il riquadro da tenere (ToolArgs.Rect), null se annullato.</summary>
    public static string? Crop(string file) => Run(new ImageEditor(file, cropping: true));

    /// <summary>I segni disegnati (ToolArgs.Marks), null se annullato o se non c'è niente.</summary>
    public static string? Annotate(string file) => Run(new ImageEditor(file, cropping: false));

    static string? Run(ImageEditor editor) => editor.ShowDialog() == true ? editor._result : null;

    static (BitmapSource, uint, uint) Load(string file)
    {
        using var image = new MagickImage(file); // solo il primo fotogramma, HEIC compreso
        image.AutoOrient();
        var (width, height) = (image.Width, image.Height);
        image.Resize(new MagickGeometry(1600, 1600) { Greater = true });
        var preview = new BitmapImage();
        preview.BeginInit();
        preview.CacheOption = BitmapCacheOption.OnLoad;
        preview.StreamSource = new MemoryStream(image.ToByteArray(MagickFormat.Png));
        preview.EndInit();
        preview.Freeze();
        return (preview, width, height);
    }

    void AddOption(object content, bool selected, Action choose, string group = "ratio")
    {
        var button = new RadioButton
        {
            Content = content, GroupName = group, IsChecked = selected, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6),
            Style = (Style)FindResource(typeof(ToggleButton)),
        };
        button.Checked += (_, _) => choose();
        Options.Children.Add(button);
    }

    static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    // ---- ritaglio ----

    void SetRatio(double? ratio)
    {
        _ratio = ratio;
        if (ratio is { } r) // il riquadro più grande con quelle proporzioni, al centro (come il vecchio "Ritaglia")
        {
            var (w, h) = _w / _h > r ? (_h * r, _h) : (_w, _w / r);
            _crop = new Rect((_w - w) / 2, (_h - h) / 2, w, h);
        }
        Redraw();
    }

    /// <summary>Il riquadro da <paramref name="a"/> verso <paramref name="p"/>, con le proporzioni scelte e dentro la foto.</summary>
    Rect Span(Point a, Point p)
    {
        var (sx, sy) = (p.X >= a.X ? 1 : -1, p.Y >= a.Y ? 1 : -1);
        var (maxW, maxH) = (sx > 0 ? _w - a.X : a.X, sy > 0 ? _h - a.Y : a.Y);
        var (w, h) = (Math.Min(Math.Abs(p.X - a.X), maxW), Math.Min(Math.Abs(p.Y - a.Y), maxH));
        if (_ratio is { } r)
        {
            if (w / Math.Max(h, 1e-6) > r) w = h * r; else h = w / r;
            if (w > maxW) (w, h) = (maxW, maxW / r);
            if (h > maxH) (w, h) = (maxH * r, maxH);
        }
        return new Rect(sx > 0 ? a.X : a.X - w, sy > 0 ? a.Y : a.Y - h, w, h);
    }

    // ---- mouse ----

    Point Clamp(Point p) => new(Math.Clamp(p.X, 0, _w), Math.Clamp(p.Y, 0, _h));

    void Overlay_Down(object sender, MouseButtonEventArgs e)
    {
        var p = Clamp(e.GetPosition(Overlay));
        if (!_cropping && _kind == Mark.Label)
        {
            StartTyping(p);
            e.Handled = true; // altrimenti il clic toglie subito il focus alla casella appena aperta
            return;
        }
        if (_cropping && _crop.Contains(p) && _crop.Size != new Size(_w, _h)) _grab = new Point(p.X - _crop.X, p.Y - _crop.Y);
        else _from = p;
        Overlay.CaptureMouse();
    }

    void Overlay_Move(object sender, MouseEventArgs e)
    {
        var p = Clamp(e.GetPosition(Overlay));
        if (_grab is { } g)
            _crop.Location = new Point(Math.Clamp(p.X - g.X, 0, _w - _crop.Width), Math.Clamp(p.Y - g.Y, 0, _h - _crop.Height));
        else if (_from is not { } a) return;
        else if (_cropping) _crop = Span(a, p);
        else _current = new Mark(_kind, a.X / _w, a.Y / _h, p.X / _w, p.Y / _h, _color);
        Redraw();
    }

    void Overlay_Up(object sender, MouseButtonEventArgs e)
    {
        Overlay.ReleaseMouseCapture();
        if (_cropping && (_crop.Width < 8 || _crop.Height < 8)) // un clic senza trascinare: si riparte da tutta la foto
        {
            _crop = new Rect(0, 0, _w, _h);
            SetRatio(_ratio);
        }
        if (_current is { } m && Math.Abs(m.X2 - m.X1) * _w + Math.Abs(m.Y2 - m.Y1) * _h > 6) _marks.Add(m);
        (_from, _grab, _current) = (null, null, null);
        Redraw();
    }

    // ---- testo ----

    void StartTyping(Point p)
    {
        CommitText();
        _typing = new TextBox
        {
            FontSize = Mark.FontSize(Math.Min(_w, _h)), FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.Bold, MinWidth = 120,
            Foreground = Brush(_color), Background = new SolidColorBrush(Color.FromArgb(0x80, 0x80, 0x80, 0x80)), BorderThickness = new Thickness(1),
            Padding = new Thickness(0), Tag = p,
        };
        _typing.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) CommitText();
            else if (e.Key == Key.Escape) { _typing = null; Redraw(); }
            else return;
            e.Handled = true; // Invio ed Esc non devono chiudere la finestra
        };
        _typing.LostKeyboardFocus += (_, _) => CommitText();
        Redraw();
        _typing.Focus();
    }

    void CommitText()
    {
        if (_typing is not { } box) return;
        _typing = null;
        var p = (Point)box.Tag;
        if (!string.IsNullOrWhiteSpace(box.Text)) _marks.Add(new Mark(Mark.Label, p.X / _w, p.Y / _h, 0, 0, _color, box.Text.Trim()));
        Redraw();
    }

    // ---- disegno ----

    void Redraw()
    {
        Overlay.Children.Clear();
        if (_cropping)
        {
            // fuori dal riquadro la foto è scurita
            Overlay.Children.Add(new System.Windows.Shapes.Path
            {
                Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
                Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, _w, _h)), new RectangleGeometry(_crop)),
            });
            var frame = new Rectangle { Width = _crop.Width, Height = _crop.Height, Stroke = Brushes.White, StrokeThickness = Mark.StrokeWidth(Math.Min(_w, _h)) / 2 };
            Canvas.SetLeft(frame, _crop.X);
            Canvas.SetTop(frame, _crop.Y);
            Overlay.Children.Add(frame);
            Info.Text = $"{Math.Round(_crop.Width / _w * _fullWidth)} × {Math.Round(_crop.Height / _h * _fullHeight)} px";
            return;
        }
        foreach (var m in _current is null ? _marks : _marks.Append(_current))
            foreach (var shape in Shapes(m)) Overlay.Children.Add(shape);
        if (_typing is { } box)
        {
            Canvas.SetLeft(box, ((Point)box.Tag).X);
            Canvas.SetTop(box, ((Point)box.Tag).Y);
            Overlay.Children.Add(box);
        }
        Info.Text = _marks.Count == 0 ? "" : $"{_marks.Count} {(_marks.Count == 1 ? "segno" : "segni")}";
    }

    /// <summary>Lo stesso disegno di MagickEngine.Draw, sull'anteprima.</summary>
    IEnumerable<UIElement> Shapes(Mark m)
    {
        var (x1, y1, x2, y2) = (m.X1 * _w, m.Y1 * _h, m.X2 * _w, m.Y2 * _h);
        var stroke = Mark.StrokeWidth(Math.Min(_w, _h));
        var color = Brush(m.Color);
        var box = new Rect(new Point(x1, y1), new Point(x2, y2));
        switch (m.Kind)
        {
            case Mark.Redact or Mark.Box:
                var rect = m.Kind == Mark.Redact
                    ? new Rectangle { Width = box.Width, Height = box.Height, Fill = Brushes.Black }
                    : new Rectangle { Width = box.Width, Height = box.Height, Stroke = color, StrokeThickness = stroke };
                Canvas.SetLeft(rect, box.X);
                Canvas.SetTop(rect, box.Y);
                yield return rect;
                break;
            case Mark.Arrow:
                var head = Mark.ArrowHead(x1, y1, x2, y2, stroke);
                yield return new Line
                {
                    X1 = x1, Y1 = y1, X2 = (head[1].X + head[2].X) / 2, Y2 = (head[1].Y + head[2].Y) / 2,
                    Stroke = color, StrokeThickness = stroke, StrokeStartLineCap = PenLineCap.Round,
                };
                yield return new Polygon { Fill = color, Points = [.. head.Select(p => new Point(p.X, p.Y))] };
                break;
            case Mark.Label:
                var text = new TextBlock { Text = m.Text, FontSize = Mark.FontSize(Math.Min(_w, _h)), FontFamily = new FontFamily("Segoe UI"),
                    FontWeight = FontWeights.Bold, Foreground = color };
                Canvas.SetLeft(text, x1);
                Canvas.SetTop(text, y1);
                yield return text;
                break;
        }
    }

    void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_marks.Count == 0) return;
        _marks.RemoveAt(_marks.Count - 1);
        Redraw();
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        CommitText();
        if (_cropping)
            _result = ToolArgs.Rect(_crop.X / _w, _crop.Y / _h, Math.Min(_crop.Width / _w, 1 - _crop.X / _w), Math.Min(_crop.Height / _h, 1 - _crop.Y / _h));
        else if (_marks.Count == 0) return; // niente da salvare
        else _result = ToolArgs.Marks(_marks);
        DialogResult = true;
    }
}
