using System.Collections.Concurrent;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Clementine.App.Notifications;
using Clementine.Core;
using Microsoft.Win32;
using Serilog;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Clementine.App.Shell;

/// <summary>Le azioni dell'anello Alt: lavorano sui file così come sono, senza passare dalla coda.</summary>
static class FileActions
{
    static readonly ConcurrentDictionary<string, string[]> PendingShares = new();

    /// <summary>Va chiamata dopo la fine del drop: un dialogo aperto dentro Drop bloccherebbe Esplora file.</summary>
    public static async Task RunAsync(Operation op, string[] paths)
    {
        try
        {
            switch (op.Id)
            {
                case "action:copy-to" or "action:move-to":
                    var move = op.Id == "action:move-to";
                    var dialog = new OpenFolderDialog { Title = op.Label, InitialDirectory = Path.GetDirectoryName(paths[0]) };
                    if (dialog.ShowDialog() != true) return;
                    var done = await Task.Run(() => move ? FileOps.MoveInto(paths, dialog.FolderName) : FileOps.CopyInto(paths, dialog.FolderName));
                    JobToasts.Info(move ? "Spostati" : "Copiati", $"{done.Count} in {dialog.FolderName}", done[0]);
                    break;

                case "action:copy-path":
                    System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, paths));
                    break;

                case "action:terminal":
                    var dir = Directory.Exists(paths[0]) ? paths[0] : Path.GetDirectoryName(paths[0])!;
                    try
                    {
                        var wt = new ProcessStartInfo("wt.exe");
                        foreach (var arg in TerminalArgs(dir)) wt.ArgumentList.Add(arg);
                        Process.Start(wt);
                    }
                    catch (Win32Exception) // Windows Terminal non installato
                    {
                        Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = dir, UseShellExecute = true });
                    }
                    break;

                case "action:read-qr":
                    var texts = await Task.Run(() => paths.SelectMany(Clementine.Engines.QrReader.Read).Distinct().ToArray());
                    if (texts.Length == 0)
                    {
                        JobToasts.Info("Nessun QR trovato", "Prova con un'immagine più nitida o più grande.");
                        break;
                    }
                    // negli appunti e basta: un link da un QR sconosciuto non si apre da solo
                    System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, texts));
                    var shown = string.Join("\n", texts);
                    JobToasts.Info("QR copiato negli appunti", shown.Length > 300 ? shown[..300] + "…" : shown);
                    break;

                case "action:new-folder":
                    var folder = OutputPath.Free(Path.Combine(Path.GetDirectoryName(paths[0])!, "Nuova cartella"), isFolder: true);
                    Directory.CreateDirectory(folder);
                    var moved = await Task.Run(() => FileOps.MoveInto(paths, folder));
                    JobToasts.Info("Nuova cartella", $"{moved.Count} file in {Path.GetFileName(folder)}", folder);
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Azione {Op} fallita su {Paths}", op.Id, paths);
            JobToasts.Info($"{op.Label}: non riuscito", e.Message);
        }
    }

    /// <summary>
    /// Condividi: Windows apre la finestra solo per l'app in primo piano. Va chiamata dentro il Drop, quando è più
    /// probabile che Windows lo conceda; se lo rifiuta si ripiega su una notifica, il cui clic dà il primo piano.
    /// </summary>
    public static void Share(string[] paths)
    {
        var host = CreateHost();
        var direct = GetForegroundWindow() == new WindowInteropHelper(host).Handle;
        Log.Information("Condividi {Mode} su {Paths}", direct ? "diretto" : "via notifica", paths);
        if (direct)
        {
            _ = ShareAsync(paths, host);
            return;
        }
        host.Close();
        var id = Guid.NewGuid().ToString("N");
        PendingShares[id] = paths;
        JobToasts.ShareReady(id, paths.Length == 1 ? Path.GetFileName(paths[0]) : $"{paths.Length} file");
    }

    /// <summary>
    /// Argomenti per Windows Terminal. Per wt il ";" separa due comandi: una cartella chiamata
    /// "x ; new-tab calc" lancerebbe calc. "\;" è il ";" letterale.
    /// </summary>
    internal static string[] TerminalArgs(string dir) => ["-d", dir.Replace(";", @"\;")];

    /// <summary>Dal clic sulla notifica "Pronto per condividere" (thread della UI).</summary>
    public static Task ShareFromToastAsync(string id) =>
        PendingShares.TryRemove(id, out var paths) ? ShareAsync(paths, CreateHost()) : Task.CompletedTask;

    // finestra d'appoggio invisibile e attivabile: l'isola (WS_EX_NOACTIVATE) non può andare in primo piano
    static Window CreateHost()
    {
        var work = SystemParameters.WorkArea;
        var host = new Window
        {
            Width = 1, Height = 1, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, Left = work.Left + work.Width / 2, Top = work.Top + work.Height / 2,
        };
        host.Show();
        host.Activate();
        SetForegroundWindow(new WindowInteropHelper(host).Handle);
        // chiuderla subito potrebbe chiudere anche la finestra Condividi: resta qualche minuto, invisibile
        _ = Task.Delay(TimeSpan.FromMinutes(3)).ContinueWith(_ => host.Dispatcher.Invoke(() => { if (host.IsLoaded) host.Close(); }));
        return host;
    }

    // Finestra "Condividi" di Windows (Nearby Share, mail, app…), agganciata alla finestra d'appoggio tramite interop.
    static async Task ShareAsync(string[] paths, Window host)
    {
        try { await ShowShareUiAsync(paths, new WindowInteropHelper(host).Handle); }
        catch (Exception e)
        {
            Log.Error(e, "Condividi fallito su {Paths}", paths);
            JobToasts.Info("Condividi: non riuscito", e.Message);
        }
    }

    static async Task ShowShareUiAsync(string[] paths, IntPtr window)
    {
        var items = new List<IStorageItem>();
        foreach (var p in paths)
            items.Add(Directory.Exists(p) ? await StorageFolder.GetFolderFromPathAsync(p) : await StorageFile.GetFileFromPathAsync(p));

        var interop = DataTransferManager.As<IDataTransferManagerInterop>();
        var iid = new Guid("a5caee9b-8708-49d1-8d36-67d25a8da00c"); // IID di DataTransferManager
        var manager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(interop.GetForWindow(window, ref iid));
        void OnRequested(DataTransferManager sender, DataRequestedEventArgs e)
        {
            sender.DataRequested -= OnRequested; // il manager è per finestra: niente gestori accumulati
            e.Request.Data.Properties.Title = paths.Length == 1 ? Path.GetFileName(paths[0]) : $"{paths.Length} file";
            e.Request.Data.SetStorageItems(items);
        }
        manager.DataRequested += OnRequested;
        interop.ShowShareUIForWindow(window);
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);

    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow(IntPtr appWindow, ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }
}
