using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Clementine.App.Island;
using Clementine.App.Notifications;
using Clementine.App.Settings;
using Clementine.App.Shell;
using Clementine.App.Tray;
using Clementine.Core;
using Clementine.Engines;
using Serilog;
using Forms = System.Windows.Forms;

namespace Clementine.App;

public partial class App : Application
{
    readonly AppSettings _settings = AppSettings.Load();
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
        JobToasts.Init(id => _pipeline?.Cancel(id), id => Dispatcher.BeginInvoke(() => FileActions.ShareFromToastAsync(id)));

        var tools = Path.Combine(AppContext.BaseDirectory, "tools");
        var exiftool = Path.Combine(tools, "exiftool", "exiftool.exe");
        _pipeline = new Pipeline([
            new MagickEngine(exiftool), new FfmpegEngine(Path.Combine(tools, "ffmpeg")),
            new PdfiumEngine(), new ArchiveEngine(), new MetadataEngine(exiftool),
        ]);
        _tray = new TrayIcon(BuildMenu);
        var island = new IslandWindow(_settings) { OperationsFor = RingsFor };

        island.Clicked += () => _tray.ShowMenu(Forms.Cursor.Position);
        island.Dropped += (op, files) =>
        {
            Console.WriteLine($"[Drop] {op.Label}: {string.Join(", ", files)}");
            if (op.Id == "action:share") FileActions.Share(files); // dentro il Drop: unica occasione di primo piano
            else if (Toolbox.IsAction(op))
                Dispatcher.BeginInvoke(() => FileActions.RunAsync(op, files));
            else Enqueue(files, op);
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
            Dispatcher.BeginInvoke(() =>
            {
                island.SetProgress(null);
                for (var i = result.Outputs.Count - 1; i >= 0; i--) // il più recente in cima
                    _settings.AddRecent(new RecentItem(result.Sources[i], result.Batch.Op.Id, result.Outputs[i]));
                _settings.Save();
            });
            foreach (var output in result.Outputs) Log.Information("Creato {Output}", output);
            foreach (var error in result.Errors) Log.Error("Fallito {Op}: {Error}", result.Batch.Op.Id, error);
            Toast(() => JobToasts.Completed(result));
        };

        island.Show();
    }

    IReadOnlyList<Operation> OperationsFor(IReadOnlyList<string> files) =>
        Formats.ConversionsFor(files).Where(_pipeline!.CanRun).ToList();

    // anelli Converti e Strumenti solo con le operazioni che un motore sa fare (es. niente HEIC senza encoder)
    IReadOnlyList<Operation>[] RingsFor(IReadOnlyList<string> files) =>
        Toolbox.RingsFor(files).Select(ops => (IReadOnlyList<Operation>)ops.Where(op => Toolbox.IsAction(op) || _pipeline!.CanRun(op)).ToList()).ToArray();

    void Enqueue(IReadOnlyList<string> files, Operation op)
    {
        Log.Information("{Op} su {Files}", op.Id, files);
        _pipeline!.Enqueue(files, op);
    }

    // ---- menu (clic sinistro sull'isola e tray) ----

    void BuildMenu(Forms.ToolStripItemCollection items)
    {
        var queue = _pipeline!.Queue;
        for (var i = 0; i < queue.Count; i++)
        {
            var batch = queue[i];
            var files = batch.Inputs.Count == 1 ? Path.GetFileName(batch.Inputs[0]) : $"{batch.Inputs.Count} file";
            var item = new Forms.ToolStripMenuItem($"{(i == 0 ? "▶" : "⏸")} {batch.Op.Label} – {files}");
            item.DropDownItems.Add("Annulla", null, (_, _) => _pipeline.Cancel(batch.Id));
            items.Add(item);
        }
        if (queue.Count > 0) items.Add(new Forms.ToolStripSeparator());

        foreach (var recent in _settings.Recent)
        {
            var op = Toolbox.Find(recent.Input, recent.OpId);
            var item = new Forms.ToolStripMenuItem(Path.GetFileName(recent.Output));
            item.DropDownItems.Add(new Forms.ToolStripMenuItem($"Ripeti ({op?.Label ?? recent.OpId})", null,
                (_, _) => Enqueue([recent.Input], op!)) { Enabled = op is not null && File.Exists(recent.Input) });
            item.DropDownItems.Add(new Forms.ToolStripMenuItem("Apri cartella", null,
                (_, _) => Process.Start("explorer.exe", $"/select,\"{recent.Output}\"")));
            items.Add(item);
        }
        if (_settings.Recent.Count > 0) items.Add(new Forms.ToolStripSeparator());

        // appunti letti solo qui, all'apertura del menu: mai durante un drag
        if (ClipboardMenu() is { } clipboard) items.Add(clipboard);
        items.Add(new Forms.ToolStripMenuItem("Esci", null, (_, _) => Shutdown()));
    }

    Forms.ToolStripMenuItem? ClipboardMenu()
    {
        var files = Forms.Clipboard.ContainsFileDropList() ? Forms.Clipboard.GetFileDropList().Cast<string>().ToArray() : null;
        var isImage = files is null && Forms.Clipboard.ContainsImage();
        var ops = files is not null ? OperationsFor(files) : isImage ? OperationsFor(["appunti.png"]) : [];
        if (ops.Count == 0) return null;

        var menu = new Forms.ToolStripMenuItem("Converti dagli appunti");
        foreach (var op in ops)
            menu.DropDownItems.Add(op.Label, null, (_, _) =>
            {
                if (files is not null) Enqueue(files, op);
                else if (SaveClipboardImage() is { } png) Enqueue([png], op);
            });
        return menu;
    }

    // L'immagine negli appunti non ha un "originale" accanto a cui salvare: diventa un PNG in Download.
    static string? SaveClipboardImage()
    {
        using var image = Forms.Clipboard.GetImage();
        if (image is null) return null;
        var path = OutputPath.Next(Path.Combine(DownloadsFolder(), $"Appunti {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png"), "png");
        image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return path;
    }

    static string DownloadsFolder() => SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero);

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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    static extern string SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token);
}
