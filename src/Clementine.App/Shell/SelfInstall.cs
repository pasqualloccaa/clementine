using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Clementine.App.Shell;

/// <summary>
/// Clementine si installa da sé: cartella del programma, menu Start, voce in "Programmi installati".
/// Un Setup.exe separato è un eseguibile non firmato in più, e Smart App Control blocca quello di Inno Setup.
/// </summary>
public static class SelfInstall
{
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Clementine";
    const string ExeName = "Clementine.App.exe";

    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Clementine");

    static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Clementine.lnk");

    /// <summary>Copia i file (sostituendo una versione precedente) e registra la disinstallazione; restituisce l'exe installato.</summary>
    public static string Install(string source, string target, RegistryKey hkcu)
    {
        if (!SamePath(source, target))
        {
            if (IsInstall(target)) Directory.Delete(target, recursive: true); // niente file vecchi di una versione precedente
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to, overwrite: true);
            }
        }
        var exe = Path.Combine(target, ExeName);
        using var key = hkcu.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "Clementine");
        key.SetValue("DisplayVersion", FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "1.0");
        key.SetValue("Publisher", "Clementine");
        key.SetValue("DisplayIcon", exe);
        key.SetValue("InstallLocation", target);
        key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
        key.SetValue("NoModify", 1);
        key.SetValue("NoRepair", 1);
        key.SetValue("EstimatedSize", (int)(Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024));
        return exe;
    }

    public static void CreateShortcut(string exe)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        var link = shell.CreateShortcut(Shortcut);
        link.TargetPath = exe;
        link.WorkingDirectory = Path.GetDirectoryName(exe);
        link.Save();
    }

    /// <summary>Toglie la voce da "Programmi installati" e il collegamento; la cartella la toglie <see cref="RemoveFolderAfterExit"/>.</summary>
    public static void Uninstall(RegistryKey hkcu)
    {
        hkcu.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        File.Delete(Shortcut);
    }

    /// <summary>
    /// L'exe che si sta disinstallando non può cancellare la propria cartella: lo fa cmd due secondi dopo l'uscita.
    /// Solo se la cartella è davvero un'installazione di Clementine.
    /// </summary>
    public static void RemoveFolderAfterExit(string target)
    {
        if (!IsInstall(target)) return;
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{target}\"")
            { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
    }

    public static bool IsInstall(string dir) => File.Exists(Path.Combine(dir, ExeName));

    static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
}
