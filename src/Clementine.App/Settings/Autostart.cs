using Microsoft.Win32;

namespace Clementine.App.Settings;

/// <summary>Avvio con Windows: un valore nella chiave Run dell'utente, che è anche l'unica fonte di verità.</summary>
public static class Autostart
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Clementine";

    // se l'eseguibile è stato spostato il valore punta al vecchio percorso: risulta spento, e riaccenderlo lo corregge
    public static bool IsOn(RegistryKey run, string exe) => run.GetValue(Name) as string == Command(exe);

    public static void Set(RegistryKey run, string exe, bool on)
    {
        if (on) run.SetValue(Name, Command(exe));
        else run.DeleteValue(Name, throwOnMissingValue: false);
    }

    static string Command(string exe) => $"\"{exe}\"";
}
