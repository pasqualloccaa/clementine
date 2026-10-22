using System.Runtime.InteropServices;
using System.Windows;
using Clementine.App.Island;
using Clementine.App.Tray;

namespace Clementine.App;

public partial class App : Application
{
    TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AttachConsole(-1); // WinExe: fa arrivare Console.WriteLine al terminale da cui è stata lanciata

        _tray = new TrayIcon(Shutdown);
        var island = new IslandWindow();
        island.Dropped += (slice, files) =>
        {
            Console.WriteLine($"[Drop] spicchio: {slice}");
            foreach (var f in files) Console.WriteLine($"  {f}");
            _tray.Notify($"Spicchio: {slice}", string.Join("\n", files));
        };
        island.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
}
