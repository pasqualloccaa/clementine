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

namespace Clementine.App.Island;

public partial class IslandWindow : Window
{
    const double IslandSize = 56, WheelSize = 300, TabWidth = 12, SnapDistance = 24;
    const double HubRadius = 34, OuterRadius = 145;
    static readonly string[] FakeSlices = ["JPG", "PNG", "WebP", "HEIC", "PDF", "TIFF"]; // M1: spicchi finti
    static readonly Brush SliceBrush = Frozen(Color.FromRgb(0x3A, 0x3A, 0x3A));
    static readonly Brush HotBrush = Frozen(Color.FromRgb(0xF5, 0x7C, 0x00));

    readonly AppSettings _settings = AppSettings.Load();
    readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    readonly List<Path> _slices = [];
    Rect _dockArea;
    bool _wheelOpen;
    int _hot = -1;

    /// <summary>Spicchio scelto e percorsi rilasciati.</summary>
    public event Action<string, string[]>? Dropped;

    public IslandWindow()
    {
        InitializeComponent();
        BuildWheel();
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
        _settings.Dock = DockSide.None; // niente ritrazione mentre si trascina
        DragMove();
        Snap();
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
        if (_wheelOpen || _settings.Dock == DockSide.None) return;
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

    // ---- ruota ----

    void BuildWheel()
    {
        var c = new Point(WheelSize / 2, WheelSize / 2);
        var step = 2 * Math.PI / FakeSlices.Length;
        const double gap = 0.02;
        for (var i = 0; i < FakeSlices.Length; i++)
        {
            var path = new Path
            {
                Data = WheelLayout.Sector(c, HubRadius, OuterRadius, i * step - step / 2 + gap, i * step + step / 2 - gap),
                Fill = SliceBrush,
            };
            _slices.Add(path);
            WheelCanvas.Children.Add(path);

            var mid = WheelLayout.PointAt(c, (HubRadius + OuterRadius) / 2, i * step);
            var label = new TextBlock
            {
                Text = FakeSlices[i], Width = 70, TextAlignment = TextAlignment.Center,
                Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold, IsHitTestVisible = false,
            };
            Canvas.SetLeft(label, mid.X - 35);
            Canvas.SetTop(label, mid.Y - 10);
            WheelCanvas.Children.Add(label);
        }
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
        SetHot(_wheelOpen
            ? WheelLayout.HitTest(p.X - WheelSize / 2, p.Y - WheelSize / 2, HubRadius, OuterRadius, FakeSlices.Length)
            : -1);
        e.Effects = _hot >= 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // ---- drag and drop ----
    // DragLeave arriva anche passando tra elementi figli o durante il ridimensionamento:
    // si chiude solo se entro 80 ms non arriva un DragEnter/DragOver.

    protected override void OnDragEnter(DragEventArgs e)
    {
        _closeTimer.Stop();
        if (!_wheelOpen && e.Data.GetDataPresent(DataFormats.FileDrop)) OpenWheel(e);
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
            Dropped?.Invoke(FakeSlices[hot], files);
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
