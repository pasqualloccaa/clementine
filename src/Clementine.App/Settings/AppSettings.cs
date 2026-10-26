using System.IO;
using System.Text.Json;

namespace Clementine.App.Settings;

public enum DockSide { None, Left, Right }

/// <summary>Un file lavorato, per il menu "ultimi file": cosa, con quale operazione, dove è finito.</summary>
public sealed record RecentItem(string Input, string OpId, string Output);

public sealed class AppSettings
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clementine", "settings.json");

    // posizione dell'isola quando è visibile (non ritratta), in DIP
    public double? Left { get; set; }
    public double? Top { get; set; }
    public DockSide Dock { get; set; }
    public bool ExplorerMenu { get; set; } = true;
    public bool WelcomeShown { get; set; }
    public bool RecordMicrophone { get; set; }
    /// <summary>"auto" (come Windows), "it" o "en": vedi Lang.Apply.</summary>
    public string Language { get; set; } = "auto";
    public List<RecentItem> Recent { get; set; } = [];

    public void AddRecent(RecentItem item)
    {
        Recent.RemoveAll(r => r.Output == item.Output);
        Recent.Insert(0, item);
        if (Recent.Count > 5) Recent.RemoveRange(5, Recent.Count - 5);
    }

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        // prima un file a parte, poi lo scambio: un'interruzione a metà scrittura non tronca le impostazioni
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, FilePath, overwrite: true);
    }
}
