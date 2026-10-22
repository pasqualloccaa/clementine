using System.IO;
using System.Text.Json;

namespace Clementine.App.Settings;

public enum DockSide { None, Left, Right }

public sealed class AppSettings
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clementine", "settings.json");

    // posizione dell'isola quando è visibile (non ritratta), in DIP
    public double? Left { get; set; }
    public double? Top { get; set; }
    public DockSide Dock { get; set; }

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
}
