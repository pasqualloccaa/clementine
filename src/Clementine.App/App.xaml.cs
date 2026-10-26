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
    ScreenRecorder? _recorder;
    IslandWindow? _island;
    SingleInstance? _instance;
    SettingsWindow? _settingsWindow;
    static readonly string LogFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clementine", "logs");
    // Esplora file lancia un processo per ogni file selezionato: le richieste ravvicinate diventano un solo lavoro
    readonly Dictionary<string, List<string>> _external = [];
    readonly System.Windows.Threading.DispatcherTimer _externalTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AttachConsole(-1); // WinExe: fa arrivare Console.WriteLine al terminale da cui è stata lanciata
        if (e.Args is ["--install"] or ["--uninstall"])
        {
            try
            {
                if (e.Args[0] == "--install") Install();
                else Uninstall();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(ex.Message, "Clementine", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Shutdown();
            return;
        }
        var request = OpRequest.Parse(e.Args);
        _instance = new SingleInstance(SingleInstance.DefaultName);
        if (!_instance.IsFirst)
        {
            // se l'istanza aperta non risponde (es. ancora in avvio), il clic non deve sparire nel nulla
            if (request is not null && !SingleInstance.TrySend(SingleInstance.DefaultName, request))
                JobToasts.Info("Clementine non risponde", "Riprova tra qualche secondo.");
            Shutdown();
            return;
        }

        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(Path.Combine(LogFolder, "clementine-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();
        JobToasts.Init(id => _pipeline?.Cancel(id), id => Dispatcher.BeginInvoke(() => FileActions.ShareFromToastAsync(id)));

        var tools = Path.Combine(AppContext.BaseDirectory, "tools");
        var exiftool = Path.Combine(tools, "exiftool", "exiftool.exe");
        _pipeline = new Pipeline([
            new MagickEngine(exiftool), new FfmpegEngine(Path.Combine(tools, "ffmpeg")),
            new LibreOfficeEngine(LibreOfficeEngine.Find()), new PdfiumEngine(), new ArchiveEngine(), new MetadataEngine(exiftool),
        ]);
        _recorder = new ScreenRecorder(Path.Combine(tools, "ffmpeg"));
        _tray = new TrayIcon(BuildMenu);
        var island = _island = new IslandWindow(_settings) { OperationsFor = RingsFor };

        island.Clicked += () => _tray.ShowMenu(Forms.Cursor.Position);
        island.Dropped += (op, files) =>
        {
            Console.WriteLine($"[Drop] {op.Label}: {string.Join(", ", files)}");
            Run(op, files);
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
        if (!_settings.WelcomeShown)
        {
            new WelcomeWindow().Show();
            _settings.WelcomeShown = true;
            _settings.Save();
        }

        SetExplorerMenu(_settings.ExplorerMenu);
        _externalTimer.Tick += (_, _) => RunExternal();
        _instance.Listen(r => Dispatcher.BeginInvoke(() =>
        {
            if (r.Op == SingleInstance.QuitOp) Shutdown();
            else AddExternal(r);
        }));
        if (request is not null) AddExternal(request);
    }

    // ---- registrazione dello schermo ----

    Forms.ToolStripMenuItem RecordMenu()
    {
        if (_recorder!.IsRecording)
            return new Forms.ToolStripMenuItem("■ Ferma registrazione", null, async (_, _) => await StopRecordingAsync());
        var menu = new Forms.ToolStripMenuItem("Registra schermo");
        var screens = ScreenRecorder.Screens();
        for (var i = 0; i < screens.Count; i++)
        {
            var screen = screens[i];
            menu.DropDownItems.Add($"Schermo {i + 1} ({screen.Width}×{screen.Height})", null, (_, _) => StartRecording(screen));
        }
        menu.DropDownItems.Add(new Forms.ToolStripSeparator());
        menu.DropDownItems.Add(new Forms.ToolStripMenuItem("Con il microfono", null, (_, _) =>
        {
            _settings.RecordMicrophone = !_settings.RecordMicrophone;
            _settings.Save();
        }) { Checked = _settings.RecordMicrophone });
        return menu;
    }

    void StartRecording(System.Drawing.Rectangle screen)
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var output = OutputPath.Next(Path.Combine(videos, $"Registrazione {DateTime.Now:yyyy-MM-dd HH.mm.ss}.mp4"), "mp4");
        var microphone = _settings.RecordMicrophone ? _recorder!.Microphone() : null;
        if (_settings.RecordMicrophone && microphone is null) Toast(() => JobToasts.Info("Nessun microfono", "Registro solo lo schermo."));
        _recorder!.Start(screen, output, microphone);
        Log.Information("Registrazione di {Screen} in {Output}", screen, output);
        _island!.SetRecording(true);
    }

    async Task StopRecordingAsync()
    {
        var output = await _recorder!.StopAsync();
        _island!.SetRecording(false);
        if (output is null) Toast(() => JobToasts.Info("Registrazione non riuscita", "Il video non è stato salvato."));
        else Toast(() => JobToasts.Info("Registrazione salvata", Path.GetFileName(output), select: output));
    }

    // ---- installazione (vedi SelfInstall) ----

    static void Install()
    {
        var answer = MessageBox.Show($"Clementine verrà installata in\n{SelfInstall.DefaultDir}\n\nVuoi che parta all'accensione del computer?",
            "Installa Clementine", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return;
        SingleInstance.Quit(SingleInstance.DefaultName); // una versione aperta va chiusa prima di sostituirne i file
        var exe = SelfInstall.Install(AppContext.BaseDirectory, SelfInstall.DefaultDir, Microsoft.Win32.Registry.CurrentUser);
        SelfInstall.CreateShortcut(exe);
        using (var run = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Autostart.RunKey)) Autostart.Set(run, exe, answer == MessageBoxResult.Yes);
        Process.Start(exe);
    }

    static void Uninstall()
    {
        if (MessageBox.Show("Disinstallare Clementine?\n\nImpostazioni e log in %APPDATA%\\Clementine restano.", "Clementine",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        // prima si chiude quella aperta: appena avviata potrebbe riscrivere il menu dopo che l'abbiamo tolto
        SingleInstance.Quit(SingleInstance.DefaultName);
        var hkcu = Microsoft.Win32.Registry.CurrentUser;
        using (var classes = hkcu.CreateSubKey(@"Software\Classes")) ContextMenu.Unregister(classes);
        using (var run = hkcu.CreateSubKey(Autostart.RunKey)) Autostart.Set(run, Environment.ProcessPath!, false);
        SelfInstall.Uninstall(hkcu);
        SelfInstall.RemoveFolderAfterExit(SelfInstall.DefaultDir);
    }

    /// <summary>Drop sulla ruota o richiesta dal menu di Esplora file: azioni subito, il resto in coda.</summary>
    void Run(Operation op, string[] files)
    {
        if (op.Id == "action:share") FileActions.Share(files); // va chiamata subito: è il momento in cui si ha il primo piano
        else if (Toolbox.IsAction(op)) Dispatcher.BeginInvoke(() => FileActions.RunAsync(op, files));
        // la finestra si apre dopo il Drop: dentro, Esplora file resterebbe bloccato finché non si chiude
        else if (Toolbox.AsksArgument(op)) Dispatcher.BeginInvoke(() => { if (ArgumentDialog.Ask(op, files) is { } chosen) Enqueue(files, chosen); });
        else Enqueue(files, op);
    }

    void AddExternal(OpRequest request)
    {
        if (!_external.TryGetValue(request.Op, out var files)) _external[request.Op] = files = [];
        files.AddRange(request.Paths.Except(files));
        _externalTimer.Stop();
        _externalTimer.Start();
    }

    // Le richieste esterne si ricontrollano qui: l'operazione deve essere tra quelle proponibili per quei file.
    void RunExternal()
    {
        _externalTimer.Stop();
        foreach (var (id, paths) in _external)
        {
            var files = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
            var op = Toolbox.Resolve(id, files);
            if (op is null || !(Toolbox.IsAction(op) || _pipeline!.CanRun(op)))
            {
                Log.Warning("Richiesta esterna rifiutata: {Op} su {Files}", id, paths);
                Toast(() => JobToasts.Info("Operazione non disponibile", $"{id} non si applica a questi file"));
                continue;
            }
            Run(op, files);
        }
        _external.Clear();
    }

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
        items.Add(RecordMenu());
        items.Add(new Forms.ToolStripSeparator());
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
                (_, _) => Run(op!, [recent.Input])) { Enabled = op is not null && File.Exists(recent.Input) });
            item.DropDownItems.Add(new Forms.ToolStripMenuItem("Apri cartella", null,
                (_, _) => Process.Start("explorer.exe", $"/select,\"{recent.Output}\"")));
            items.Add(item);
        }
        if (_settings.Recent.Count > 0) items.Add(new Forms.ToolStripSeparator());

        // appunti letti solo qui, all'apertura del menu: mai durante un drag
        if (ClipboardMenu() is { } clipboard) items.Add(clipboard);
        items.Add(new Forms.ToolStripMenuItem("Come si usa", null, (_, _) => new WelcomeWindow().Show()));
        items.Add(new Forms.ToolStripMenuItem("Impostazioni…", null, (_, _) => ShowSettings()));
        items.Add(new Forms.ToolStripMenuItem("Esci", null, (_, _) => Shutdown()));
    }

    void ShowSettings()
    {
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }
        using var run = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Autostart.RunKey);
        _settingsWindow = new SettingsWindow(
            Autostart.IsOn(run, Environment.ProcessPath!), on =>
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Autostart.RunKey);
                Autostart.Set(key, Environment.ProcessPath!, on);
            },
            _settings.ExplorerMenu, on =>
            {
                _settings.ExplorerMenu = on;
                _settings.Save();
                SetExplorerMenu(on);
            },
            LibreOfficeEngine.Find(), LogFolder);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    void SetExplorerMenu(bool on)
    {
        try
        {
            using var classes = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes");
            if (on) ContextMenu.Register(classes, Environment.ProcessPath!, _pipeline!.CanRun);
            else ContextMenu.Unregister(classes);
        }
        catch (Exception ex) { Log.Error(ex, "Menu di Esplora file non aggiornato"); }
    }

    Forms.ToolStripMenuItem? ClipboardMenu()
    {
        var files = Forms.Clipboard.ContainsFileDropList() ? Forms.Clipboard.GetFileDropList().Cast<string>().ToArray() : null;
        var isImage = files is null && Forms.Clipboard.ContainsImage();
        var ops = files is not null ? RingsFor(files)[(int)Ring.Convert] : isImage ? RingsFor(["appunti.png"])[(int)Ring.Convert] : [];
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
        _recorder?.StopAsync().Wait(TimeSpan.FromSeconds(15)); // un MP4 chiuso a metà non si apre
        _pipeline?.Dispose();
        _tray?.Dispose();
        _instance?.Dispose();
        if (_pipeline is not null) JobToasts.Cleanup(); // solo l'istanza principale: le altre si chiudono subito dopo aver inoltrato
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    static extern string SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token);
}
