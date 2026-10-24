using Clementine.App.Shell;
using Clementine.Core;
using Microsoft.Win32;
using Xunit;

namespace Clementine.Tests;

public class ContextMenuTests : IDisposable
{
    const string Exe = @"C:\App\Clementine.App.exe";
    // un ramo di prova al posto di HKCU\Software\Classes
    readonly string _root = $@"Software\Clementine.Tests\{Guid.NewGuid():N}";
    readonly RegistryKey _classes;

    public ContextMenuTests() => _classes = Registry.CurrentUser.CreateSubKey(_root);

    public void Dispose()
    {
        _classes.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
    }

    static string? Value(RegistryKey classes, string key, string name = "")
    {
        using var k = classes.OpenSubKey(key);
        return k?.GetValue(name) as string;
    }

    [Fact]
    public void Entries_HaveOneMenuPerExtension_WithValidCommands()
    {
        var entries = ContextMenu.Entries(Exe, _ => true);

        Assert.Contains(entries, e => e.Key == @"SystemFileAssociations\.heic\shell\Clementine" && e.Name == "ExtendedSubCommandsKey");
        Assert.DoesNotContain(entries, e => e.Key.Contains(@"\.tar.gz\"));
        var commands = entries.Where(e => e.Key.EndsWith(@"\command")).Select(e => e.Value).ToList();
        Assert.Contains($"\"{Exe}\" --op convert:jpg \"%1\"", commands);
        Assert.Contains($"\"{Exe}\" --op tool:merge-pdf \"%1\"", commands); // anche le operazioni che uniscono
        Assert.All(commands, c => Assert.Matches("^\"[^\"]+\" --op [a-z]+:[a-z0-9.-]+ \"%1\"$", c));
    }

    // Esplora file nasconde in silenzio le voci oltre la 16ª di tutto il menu: il taglio non tocca mai le conversioni
    // (la GIF, immagine e video insieme, ne ha 13: lì restano fuori anche alcuni strumenti)
    [Fact]
    public void Menu_FitsExplorerLimit_NeverCuttingConversions()
    {
        foreach (var ext in Formats.InputExtensions)
            Assert.InRange(Toolbox.RingsFor([$"a.{ext}", $"b.{ext}"])[(int)Ring.Convert].Count, 0, ContextMenu.MaxCascadeItems);
        var entries = ContextMenu.Entries(Exe, _ => true);
        Assert.All(entries.Where(e => e.Key.EndsWith(@"\shell\Clementine")).Select(e => e.Key).Distinct(), verb =>
            Assert.InRange(entries.Count(e => e.Name == "MUIVerb" && e.Key.StartsWith(@"Clementine.Menus\")
                && e.Key.Split('\\')[1] == entries.Single(v => v.Key == verb && v.Name == "ExtendedSubCommandsKey").Value.Split('\\')[1]), 1, ContextMenu.MaxCascadeItems));
        var jpg = entries.Where(e => e.Key.StartsWith(@"Clementine.Menus\jpg\") && e.Key.EndsWith(@"\command")).Select(e => e.Value).ToList();
        Assert.DoesNotContain(jpg, c => c.Contains("action:copy-path")); // la prima a restare fuori
        Assert.Contains(entries, e => e.Key == @"Clementine.Menus\jpg\shell\07" && e.Name == "CommandFlags"); // linea prima degli strumenti
    }

    [Fact]
    public void Entries_SkipOperationsNoEngineCanRun()
    {
        var entries = ContextMenu.Entries(Exe, op => op.TargetExtension != "heic");
        Assert.DoesNotContain(entries, e => e.Value.Contains("convert:heic"));
        Assert.Contains(entries, e => e.Value.Contains("action:copy-to")); // le azioni non dipendono dai motori
    }

    [Fact]
    public void Register_ThenUnregister_LeavesNothing()
    {
        ContextMenu.Register(_classes, Exe, _ => true);
        Assert.Equal("Clementine", Value(_classes, @"SystemFileAssociations\.pdf\shell\Clementine", "MUIVerb"));
        Assert.Equal($"\"{Exe}\" --op tool:zip \"%1\"", Value(_classes, @"Clementine.Menus\folder\shell\01\command"));
        using (var item = _classes.OpenSubKey(@"Clementine.Menus\folder\shell\02")!) Assert.Equal(0x20, item.GetValue("CommandFlags")); // DWORD, non testo

        ContextMenu.Unregister(_classes);
        Assert.Null(_classes.OpenSubKey("Clementine.Menus"));
        using var sfa = _classes.OpenSubKey(@"SystemFileAssociations\.pdf\shell");
        Assert.DoesNotContain(sfa?.GetSubKeyNames() ?? [], n => n.StartsWith("Clementine"));
    }

    [Fact]
    public void Register_IsSkippedWhenUnchanged_AndRewrittenWhenExeMoves()
    {
        ContextMenu.Register(_classes, Exe, _ => true);
        using (var k = _classes.OpenSubKey(@"SystemFileAssociations\.pdf\shell\Clementine", writable: true)!) k.SetValue("MUIVerb", "toccato");

        ContextMenu.Register(_classes, Exe, _ => true);
        Assert.Equal("toccato", Value(_classes, @"SystemFileAssociations\.pdf\shell\Clementine", "MUIVerb"));

        ContextMenu.Register(_classes, @"D:\Altrove\Clementine.App.exe", _ => true);
        Assert.Equal("Clementine", Value(_classes, @"SystemFileAssociations\.pdf\shell\Clementine", "MUIVerb"));
    }
}

public class SingleInstanceTests
{
    [Theory]
    [InlineData(new[] { "--op", "convert:jpg", @"C:\a.heic", @"C:\b.heic" }, "convert:jpg", 2)]
    [InlineData(new[] { "--op", "convert:jpg" }, null, 0)]
    [InlineData(new[] { "--altro" }, null, 0)]
    [InlineData(new string[0], null, 0)]
    public void Parse(string[] args, string? op, int files)
    {
        var request = OpRequest.Parse(args);
        Assert.Equal(op, request?.Op);
        Assert.Equal(files, request?.Paths.Length ?? 0);
    }

    [Fact]
    public async Task SecondInstance_ForwardsRequestToFirst()
    {
        var name = $"Clementine-test-{Guid.NewGuid():N}";
        using var first = new SingleInstance(name);
        using var second = new SingleInstance(name);
        Assert.True(first.IsFirst);
        Assert.False(second.IsFirst);

        var received = new TaskCompletionSource<OpRequest>();
        first.Listen(received.SetResult);
        Assert.True(SingleInstance.TrySend(name, new OpRequest("convert:png", [@"C:\x.jpg"])));

        var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("convert:png", request.Op);
        Assert.Equal([@"C:\x.jpg"], request.Paths);
    }

    [Fact]
    public void Send_WithoutListener_TimesOut() =>
        Assert.False(SingleInstance.TrySend($"Clementine-test-{Guid.NewGuid():N}", new OpRequest("convert:png", ["x"]), timeoutMs: 300));
}

public class ResolveTests
{
    [Fact]
    public void OnlyOperationsValidForTheseFiles()
    {
        using var dir = new TempDir();
        var a = dir.File("a.pdf");
        var b = dir.File("b.pdf");
        Assert.Equal("Unisci", Toolbox.Resolve("tool:merge-pdf", [a, b])?.Label);
        Assert.Null(Toolbox.Resolve("tool:merge-pdf", [a]));         // serve più di un file
        Assert.Null(Toolbox.Resolve("convert:mp3", [a]));            // non si applica ai PDF
        Assert.Null(Toolbox.Resolve("rm -rf", [a]));                  // id inventato
        Assert.Equal("Copia in…", Toolbox.Resolve("action:copy-to", [a])?.Label);
        Assert.Null(Toolbox.Resolve("convert:png", []));
    }
}

public class AutostartTests : IDisposable
{
    const string Exe = @"C:\Program Files\Clementine\Clementine.App.exe";
    readonly string _root = $@"Software\Clementine.Tests\{Guid.NewGuid():N}";
    readonly RegistryKey _run;

    public AutostartTests() => _run = Registry.CurrentUser.CreateSubKey(_root);

    public void Dispose()
    {
        _run.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
    }

    [Fact]
    public void OnOff_WritesQuotedPath_AndMovedExeCountsAsOff()
    {
        Assert.False(Clementine.App.Settings.Autostart.IsOn(_run, Exe));
        Clementine.App.Settings.Autostart.Set(_run, Exe, true);
        Assert.Equal($"\"{Exe}\"", _run.GetValue("Clementine")); // virgolette: il percorso ha spazi
        Assert.True(Clementine.App.Settings.Autostart.IsOn(_run, Exe));
        Assert.False(Clementine.App.Settings.Autostart.IsOn(_run, @"D:\Altrove\Clementine.App.exe"));
        Clementine.App.Settings.Autostart.Set(_run, Exe, false);
        Clementine.App.Settings.Autostart.Set(_run, Exe, false); // spegnere due volte non è un errore
        Assert.Null(_run.GetValue("Clementine"));
    }
}
