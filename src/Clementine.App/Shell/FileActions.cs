using System.ComponentModel;
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
    /// <summary>Va chiamata dopo la fine del drop: un dialogo aperto dentro Drop bloccherebbe Esplora file.</summary>
    public static async Task RunAsync(Operation op, string[] paths, IntPtr window)
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
                    try { Process.Start(new ProcessStartInfo("wt.exe") { ArgumentList = { "-d", dir } }); }
                    catch (Win32Exception) // Windows Terminal non installato
                    {
                        Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = dir, UseShellExecute = true });
                    }
                    break;

                case "action:share":
                    await ShareAsync(paths, window);
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

    // Finestra "Condividi" di Windows (Nearby Share, mail, app…), agganciata a una finestra desktop tramite interop.
    static async Task ShareAsync(string[] paths, IntPtr window)
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

    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow(IntPtr appWindow, ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }
}
