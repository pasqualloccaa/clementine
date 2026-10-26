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
    // ruota nei colori del tema di Windows: scelti a ogni apertura
    static readonly Brush DarkDisk = Frozen(Color.FromArgb(0xE6, 0x1E, 0x1E, 0x1E)), DarkSlice = Frozen(Color.FromRgb(0x3A, 0x3A, 0x3A));
    static readonly Brush LightDisk = Frozen(Color.FromArgb(0xF2, 0xF7, 0xF7, 0xF7)), LightSlice = Frozen(Color.FromRgb(0xE2, 0xE2, 0xE2));
    static readonly Brush LightText = Frozen(Color.FromRgb(0x20, 0x20, 0x20));
    static readonly Brush HotBrush = Frozen(Color.FromRgb(0xF5, 0x7C, 0x00));

    readonly AppSettings _settings;
    readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    readonly List<Path> _slices = [];
    readonly List<UIElement> _wheelItems = [];
    static readonly string[] RingNames = ["Converti", "Strumenti", "Azioni"];
    readonly HubDwell _hub = new(TimeSpan.FromMilliseconds(1500)); // provati 400 e 800 ms: troppo rapidi all'uso
    IReadOnlyList<Operation>[] _rings = [];
    IReadOnlyList<Operation> _ops = [];
    Ring _ring, _manualRing;
    Rect _dockArea;
    bool _wheelOpen, _working;
    int _hot = -1;
    Brush _sliceBrush = DarkSlice, _labelBrush = Brushes.White;

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
        StopSlide();
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
        _settings.Dock = DockFor(Left, wa, ScreenBeside(DockSide.Left), ScreenBeside(DockSide.Right));
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
        Slide(_settings.Dock == DockSide.Left
            ? _dockArea.Left - (retracted ? IslandSize - TabWidth : 0)
            : _dockArea.Right - (retracted ? TabWidth : IslandSize));
    }

    // l'isola scivola sul bordo invece di saltare; FillBehavior.Stop: a fine animazione resta il valore vero di Left
    void Slide(double left)
    {
        var from = Left;
        StopSlide();
        Left = left;
        if (from != left)
            BeginAnimation(LeftProperty, new DoubleAnimation(from, left, TimeSpan.FromMilliseconds(140))
                { EasingFunction = new QuadraticEase(), FillBehavior = FillBehavior.Stop });
    }

    // prima di ogni spostamento diretto: un'animazione ancora in corso coprirebbe il nuovo valore
    void StopSlide() => BeginAnimation(LeftProperty, null);

    /// <summary>
    /// Aggancio solo ai bordi esterni del desktop: sul confine tra due monitor la parte "ritratta" finirebbe sull'altro
    /// schermo, ancora visibile e sotto il mouse, e l'isola salterebbe avanti e indietro all'infinito.
    /// </summary>
    internal static DockSide DockFor(double left, Rect workArea, bool screenOnLeft, bool screenOnRight) =>
        left < workArea.Left + SnapDistance && !screenOnLeft ? DockSide.Left
        : left + IslandSize > workArea.Right - SnapDistance && !screenOnRight ? DockSide.Right
        : DockSide.None;

    // un altro monitor da quel lato, alla stessa altezza; non "il pixel subito oltre il bordo": con monitor a scale
    // diverse (125% e 100%) Windows lascia un buco tra le loro coordinate e quel pixel non è di nessuno schermo
    bool ScreenBeside(DockSide side)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var center = new System.Drawing.Point((int)((Left + IslandSize / 2) * dpi.DpiScaleX), (int)((Top + IslandSize / 2) * dpi.DpiScaleY));
        var here = System.Windows.Forms.Screen.FromPoint(center).Bounds;
        return System.Windows.Forms.Screen.AllScreens.Select(s => s.Bounds).Any(b => b != here
            && b.Top <= center.Y && center.Y < b.Bottom
            && (side == DockSide.Right ? b.Left >= here.Right : b.Right <= here.Left));
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
                Fill = _sliceBrush,
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
                        VerticalAlignment = VerticalAlignment.Center, Foreground = _labelBrush,
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
        StopSlide();
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
        // da 0.4 e non da 0: una finestra trasparente al 100% non riceve il drag e la ruota si richiuderebbe
        WheelCanvas.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(120)));
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
        StopSlide();
        Left = _settings.Left ?? Left;
        Top = _settings.Top ?? Top;
        SetRetracted(true);
    }

    void SetHot(int hot)
    {
        if (hot == _hot) return;
        if (_hot >= 0) _slices[_hot].Fill = _sliceBrush;
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
            if (_hub.Update(dx * dx + dy * dy < HubRadius * HubRadius, modifierHeld: ring != _manualRing, DateTime.UtcNow))
                ring = _manualRing = NextRing(_manualRing);
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
            var dark = Theme.IsDark;
            Disk.Fill = dark ? DarkDisk : LightDisk;
            (_sliceBrush, _labelBrush) = dark ? (DarkSlice, Brushes.White) : (LightSlice, LightText);
            _manualRing = rings[(int)Ring.Convert].Count > 0 ? Ring.Convert : NextRing(Ring.Convert);
            _hub.Reset();
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
