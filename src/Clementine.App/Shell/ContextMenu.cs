using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Clementine.Core;
using Microsoft.Win32;

namespace Clementine.App.Shell;

/// <summary>
/// Voce "Clementine" nel menu del tasto destro di Esplora file (menu classico; su Windows 11 "Mostra altre opzioni").
/// Tutto sotto HKCU\Software\Classes, senza privilegi; ogni voce lancia "Clementine.App.exe --op &lt;id&gt; &lt;file&gt;".
/// Esplora file mostra al massimo 16 voci in tutto un menu a cascata statico, sottomenu annidati compresi
/// (verificato: oltre, spariscono in silenzio), quindi una lista unica: conversioni, strumenti, azioni.
/// </summary>
public static class ContextMenu
{
    public const int MaxCascadeItems = 16;
    const string Menus = "Clementine.Menus";
    const int SeparatorBefore = 0x20; // ECF_SEPARATORBEFORE

    /// <summary>Le voci di registro da scrivere: (chiave relativa a Classes, nome del valore, dato).</summary>
    public static List<(string Key, string Name, string Value)> Entries(string exe, Func<Operation, bool> canRun)
    {
        var entries = new List<(string, string, string)>();
        foreach (var ext in Formats.InputExtensions.Order())
        {
            // due file finti: così compaiono anche le operazioni che uniscono (Unisci PDF), valide con più file
            var rings = Toolbox.RingsFor([$"a.{ext}", $"b.{ext}"]);
            AddMenus(entries, @$"SystemFileAssociations\.{ext}\shell", ext, rings, exe, canRun);
        }
        AddMenus(entries, @"Directory\shell", "folder", [[], [Toolbox.Zip], Toolbox.Actions], exe, canRun);
        return entries;
    }

    static void AddMenus(List<(string, string, string)> entries, string shellKey, string menu,
        IReadOnlyList<Operation>[] rings, string exe, Func<Operation, bool> canRun)
    {
        // oltre le 16 voci restano fuori le ultime azioni: "Copia percorso", la meno usata, va in fondo
        var ops = rings.SelectMany((ring, r) => ring.Where(op => Toolbox.IsAction(op) || canRun(op))
                .OrderBy(op => op.Id == "action:copy-path").Select(op => (Op: op, Ring: r)))
            .Take(MaxCascadeItems).ToList();
        if (ops.Count == 0) return;
        var verbKey = $@"{shellKey}\Clementine";
        var items = $@"{Menus}\{menu}";
        entries.Add((verbKey, "MUIVerb", "Clementine"));
        entries.Add((verbKey, "Icon", exe));
        entries.Add((verbKey, "ExtendedSubCommandsKey", items));
        entries.Add((verbKey, "MultiSelectModel", "Player")); // fino a 100 file selezionati invece di 15
        for (var i = 0; i < ops.Count; i++)
        {
            var item = $@"{items}\shell\{i + 1:D2}";
            // in una lista unica "PNG" da solo si confonderebbe con gli strumenti
            entries.Add((item, "MUIVerb", ops[i].Ring == (int)Ring.Convert ? $"Converti in {ops[i].Op.Label}" : ops[i].Op.Label));
            if (i > 0 && ops[i].Ring != ops[i - 1].Ring) entries.Add((item, "CommandFlags", SeparatorBefore.ToString())); // linea tra gli anelli
            entries.Add(($@"{item}\command", "", $"\"{exe}\" --op {ops[i].Op.Id} \"%1\""));
        }
    }

    /// <summary>Scrive il menu; se è già identico non tocca nulla (si chiama a ogni avvio).</summary>
    public static void Register(RegistryKey classes, string exe, Func<Operation, bool> canRun)
    {
        var entries = Entries(exe, canRun);
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n", entries.Select(e => $"{e.Key}|{e.Name}|{e.Value}")))));
        using (var root = classes.OpenSubKey(Menus))
            if (root?.GetValue("Signature") as string == signature) return;

        Unregister(classes);
        foreach (var group in entries.GroupBy(e => e.Key))
        {
            using var key = classes.CreateSubKey(group.Key);
            foreach (var (_, name, value) in group) key.SetValue(name, name == "CommandFlags" ? int.Parse(value) : value); // CommandFlags è un DWORD
        }
        using (var root = classes.CreateSubKey(Menus))
        {
            root.SetValue("Signature", signature);
            // per poter togliere anche le voci di estensioni che in futuro non ci saranno più
            root.SetValue("VerbKeys", string.Join(";", entries.Select(e => e.Key).Where(k => k.EndsWith(@"\shell\Clementine")).Distinct()));
        }
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED: Esplora file rilegge i menu
    }

    public static void Unregister(RegistryKey classes)
    {
        using (var root = classes.OpenSubKey(Menus))
            foreach (var key in (root?.GetValue("VerbKeys") as string ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                classes.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        classes.DeleteSubKeyTree(Menus, throwOnMissingSubKey: false);
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
