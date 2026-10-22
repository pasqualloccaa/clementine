using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Clementine.App.Island;
using Clementine.App.Notifications;
using Clementine.App.Tray;
using Clementine.Core;
using Clementine.Engines;
using Serilog;

namespace Clementine.App;

public partial class App : Application
{
    TrayIcon? _tray;
    Pipeline? _pipeline;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AttachConsole(-1); // WinExe: fa arrivare Console.WriteLine al terminale da cui è stata lanciata
        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clementine", "logs", "clementine-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();
        JobToasts.Init();

        _pipeline = new Pipeline([new MagickEngine(), new FfmpegEngine(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg"))]);
        _tray = new TrayIcon(Shutdown);
        var island = new IslandWindow { OperationsFor = files => Formats.ConversionsFor(files).Where(_pipeline.CanRun).ToList() };

        island.Dropped += (op, files) =>
        {
            Console.WriteLine($"[Drop] {op.Label}: {string.Join(", ", files)}");
            Log.Information("Drop {Op} su {Files}", op.Id, files);
            _pipeline.Enqueue(files, op);
        };
        _pipeline.Started += batch =>
        {
            Dispatcher.BeginInvoke(() => island.SetProgress(0));
            Toast(() => JobToasts.Started(batch));
        };
        _pipeline.Progress += (batch, p) =>
        {
            Dispatcher.BeginInvoke(() => island.SetProgress(p));
            Toast(() => JobToasts.Progress(batch, p));
        };
        _pipeline.Completed += result =>
        {
            Dispatcher.BeginInvoke(() => island.SetProgress(null));
            foreach (var output in result.Outputs) Log.Information("Creato {Output}", output);
            foreach (var error in result.Errors) Log.Error("Fallito {Op}: {Error}", result.Batch.Op.Id, error);
            Toast(() => JobToasts.Completed(result));
        };

        island.Show();
    }

    // gli eventi arrivano dal worker della coda: un toast fallito non deve fermarla
    static void Toast(Action show)
    {
        try { show(); }
        catch (Exception e) { Log.Warning(e, "Notifica non mostrata"); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeline?.Dispose();
        _tray?.Dispose();
        JobToasts.Cleanup();
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
}
