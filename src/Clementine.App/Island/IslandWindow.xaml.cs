using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Clementine.App.Settings;
using Clementine.App.Wheel;
using Clementine.Core;

namespace Clementine.App.Island;

public partial class IslandWindow : Window
{
    const double IslandSize = 56, WheelSize = 300, TabWidth = 12, SnapDistance = 24;
    const double HubRadius = 34, OuterRadius = 145;
    static readonly Brush SliceBrush = Frozen(Color.FromRgb(0x3A, 0x3A, 0x3A));
    static readonly Brush HotBrush = Frozen(Color.FromRgb(0xF5, 0x7C, 0x00));

    readonly AppSettings _settings;
    readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    readonly List<Path> _slices = [];
    readonly List<UIElement> _wheelItems = [];
    static readonly string[] RingNames = ["Converti", "Strumenti", "Azioni"];
    static readonly TimeSpan HubDwell = TimeSpan.FromMilliseconds(400);
    IReadOnlyList<Operation>[] _rings = [];
    IReadOnlyList<Operation> _ops = [];
    Ring _ring, _manualRing;
    DateTime? _hubSince;
    Rect _dockArea;
    bool _wheelOpen, _working;
    int _hot = -1;

    /// <summary>Le operazioni dei tre anelli (indice = Ring) per i file trascinati; tutti vuoti = la ruota non si apre.</summary>
    public Func<string[], IReadOnlyList<Operation>[]> OperationsFor { get; init; } = _ => [[], [], []];

    /// <summary>Operazione scelta e percorsi rilasciati.</summary>
    public event Action<Operation, string[]>? Dropped;

    /// <summary>Clic sinistro senza spostare l'isola.</summary>
    public event Action? Clicked;

    public IslandWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        _closeTimer.Tick += (_, _) => CloseWheel();
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = _settings.Left ?? wa.Right - IslandSize;
            Top = _settings.Top ?? wa.Top + wa.Height / 2;
            Snap();
            SetRetracted(true);
        };
        MouseEnter += (_, _) => SetRetracted(false);
        MouseLeave += (_, _) => SetRetracted(true);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    // ---- isola: spostamento, aggancio ai bordi, ritrazione ----

    void Island_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var before = (Left, Top);
        _settings.Dock = DockSide.None; // niente ritrazione mentre si trascina
        DragMove();
        var moved = before != (Left, Top);
        Snap();
        if (!moved) Clicked?.Invoke();
    }

    void Snap()
    {
        _dockArea = WorkAreaAt(new Point(Left + IslandSize / 2, Top + IslandSize / 2));
        var wa = _dockArea;
        _settings.Dock = Left < wa.Left + SnapDistance ? DockSide.Left
            : Left + IslandSize > wa.Right - SnapDistance ? DockSide.Right
            : DockSide.None;
        Top = Math.Clamp(Top, wa.Top, wa.Bottom - IslandSize);
        Left = _settings.Dock switch
        {
            DockSide.Left => wa.Left,
            DockSide.Right => wa.Right - IslandSize,
            _ => Math.Clamp(Left, wa.Left, wa.Right - IslandSize),
        };
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();
    }

    void SetRetracted(bool retracted)
    {
        if (_wheelOpen || _settings.Dock == DockSide.None || (retracted && _working)) return;
        Left = _settings.Dock == DockSide.Left
            ? _dockArea.Left - (retracted ? IslandSize - TabWidth : 0)
            : _dockArea.Right - (retracted ? TabWidth : IslandSize);
    }

    // shortcut: usa il DPI di questa finestra per tutti i monitor; impreciso con monitor a DPI diversi (M7)
    Rect WorkAreaAt(Point dip)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var wa = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)(dip.X * dpi.DpiScaleX), (int)(dip.Y * dpi.DpiScaleY))).WorkingArea;
        return new Rect(wa.Left / dpi.DpiScaleX, wa.Top / dpi.DpiScaleY, wa.Width / dpi.DpiScaleX, wa.Height / dpi.DpiScaleY);
    }

    /// <summary>Anello di avanzamento sull'isola (0..1); null lo nasconde. Mentre lavora l'isola resta fuori dal bordo.</summary>
    public void SetProgress(double? value)
    {
        _working = value is not null;
        ProgressRing.Visibility = _working ? Visibility.Visible : Visibility.Collapsed;
        // tratto in unità di StrokeThickness: circonferenza della linea centrale / spessore (anello di 50 px, Margin 3)
        var t = ProgressRing.StrokeThickness;
        var circumference = Math.PI * (IslandSize - 2 * ProgressRing.Margin.Left - t) / t;
        ProgressRing.StrokeDashArray = [Math.Max(value ?? 0, 0.001) * circumference, circumference];
        SetRetracted(!_working && !IsMouseOver);
    }

    // ---- ruota ----

    void ShowRing(Ring ring)
    {
        _ring = ring;
        HubLabel.Text = RingNames[(int)ring];
        BuildWheel(_rings[(int)ring]);
    }

    // anello scelto: Ctrl → Strumenti, Alt → Azioni, altrimenti quello scelto con il mozzo
    Ring RingFor(DragEventArgs e) =>
        e.KeyStates.HasFlag(DragDropKeyStates.ControlKey) ? Ring.Tools
        : e.KeyStates.HasFlag(DragDropKeyStates.AltKey) ? Ring.Actions
        : _manualRing;

    // il prossimo anello non vuoto dopo "from"
    Ring NextRing(Ring from)
    {
        for (var i = 1; i <= _rings.Length; i++)
        {
            var next = (Ring)(((int)from + i) % _rings.Length);
            if (_rings[(int)next].Count > 0) return next;
        }
        return from;
    }

    void BuildWheel(IReadOnlyList<Operation> ops)
    {
        foreach (var item in _wheelItems) WheelCanvas.Children.Remove(item);
        _wheelItems.Clear();
        _slices.Clear();
        _hot = -1;
        _ops = ops;

        var c = new Point(WheelSize / 2, WheelSize / 2);
        var step = 2 * Math.PI / ops.Count;
        var mid = (HubRadius + OuterRadius) / 2;
        var labelWidth = Math.Min(80, 2 * mid * Math.Sin(Math.Min(step, Math.PI) / 2) - 4);
        const double gap = 0.02;
        for (var i = 0; i < ops.Count; i++)
        {
            var path = new Path
            {
                Data = WheelLayout.Sector(c, HubRadius, OuterRadius, i * step - step / 2 + gap, i * step + step / 2 - gap),
                Fill = SliceBrush,
            };
            _slices.Add(path);

            var at = WheelLayout.PointAt(c, mid, i * step);
            // etichette lunghe ("Estrai audio") vanno a capo: il blocco di due righe resta centrato sullo spicchio
            var label = new Grid
            {
                Width = labelWidth, Height = 40, IsHitTestVisible = false,
                Children =
                {
                    new TextBlock
                    {
                        Text = ops[i].Label, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White,
                        FontSize = ops.Count > 8 || ops[i].Label.Length > 6 ? 12 : 15, FontWeight = FontWeights.SemiBold,
                    },
                },
            };
            Canvas.SetLeft(label, at.X - labelWidth / 2);
            Canvas.SetTop(label, at.Y - 20);
            _wheelItems.Add(path);
            _wheelItems.Add(label);
        }
        var hub = WheelCanvas.Children.IndexOf(Hub);
        foreach (var item in _wheelItems) WheelCanvas.Children.Insert(hub++, item); // sotto il mozzo
    }

    void OpenWheel(DragEventArgs e)
    {
        _wheelOpen = true;
        var dpi = VisualTreeHelper.GetDpi(this);
        var px = PointToScreen(e.GetPosition(this));
        var cursor = new Point(px.X / dpi.DpiScaleX, px.Y / dpi.DpiScaleY);
        var wa = WorkAreaAt(cursor);

        IslandView.Visibility = Visibility.Collapsed;
        WheelCanvas.Visibility = Visibility.Visible;
        Width = Height = WheelSize;
        Left = Math.Clamp(cursor.X - WheelSize / 2, wa.Left, wa.Right - WheelSize);
        Top = Math.Clamp(cursor.Y - WheelSize / 2, wa.Top, wa.Bottom - WheelSize);

        var grow = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() };
        WheelScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        WheelScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    void CloseWheel()
    {
        _closeTimer.Stop();
        if (!_wheelOpen) return;
        SetHot(-1);
        _wheelOpen = false;
        WheelCanvas.Visibility = Visibility.Collapsed;
        IslandView.Visibility = Visibility.Visible;
        Width = Height = IslandSize;
        Left = _settings.Left ?? Left;
        Top = _settings.Top ?? Top;
        SetRetracted(true);
    }

    void SetHot(int hot)
    {
        if (hot == _hot) return;
        if (_hot >= 0) _slices[_hot].Fill = SliceBrush;
        if (hot >= 0) _slices[hot].Fill = HotBrush;
        _hot = hot;
    }

    void UpdateHot(DragEventArgs e)
    {
        var p = e.GetPosition(WheelCanvas);
        var (dx, dy) = (p.X - WheelSize / 2, p.Y - WheelSize / 2);
        if (_wheelOpen)
        {
            var ring = RingFor(e);
            var onHub = dx * dx + dy * dy < HubRadius * HubRadius && ring == _manualRing;
            if (!onHub) _hubSince = null;
            else if (_hubSince is null) _hubSince = DateTime.UtcNow;
            else if (DateTime.UtcNow - _hubSince >= HubDwell) // restando fermi gira ancora ogni 400 ms
            {
                ring = _manualRing = NextRing(_manualRing);
                _hubSince = DateTime.UtcNow;
            }
            if (ring != _ring) ShowRing(ring);
        }
        SetHot(_wheelOpen ? WheelLayout.HitTest(dx, dy, HubRadius, OuterRadius, _ops.Count) : -1);
        e.Effects = _hot >= 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // ---- drag and drop ----
    // DragLeave arriva anche passando tra elementi figli o durante il ridimensionamento:
    // si chiude solo se entro 80 ms non arriva un DragEnter/DragOver.

    protected override void OnDragEnter(DragEventArgs e)
    {
        _closeTimer.Stop();
        if (!_wheelOpen && e.Data.GetData(DataFormats.FileDrop) is string[] files
            && OperationsFor(files) is var rings && rings.Any(r => r.Count > 0))
        {
            _rings = rings;
            _manualRing = rings[(int)Ring.Convert].Count > 0 ? Ring.Convert : NextRing(Ring.Convert);
            _hubSince = null;
            ShowRing(RingFor(e));
            OpenWheel(e);
        }
        UpdateHot(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        _closeTimer.Stop();
        UpdateHot(e);
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        if (_wheelOpen) _closeTimer.Start();
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        UpdateHot(e);
        var hot = _hot;
        CloseWheel();
        if (hot >= 0 && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            Dropped?.Invoke(_ops[hot], files);
        e.Handled = true;
    }

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
