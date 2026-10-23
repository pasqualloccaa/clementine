using Clementine.Core;
using Clementine.Engines;
using ImageMagick;
using Xunit;

namespace Clementine.Tests;

public class MagickSecurityTests
{
    static readonly Operation ToPng = new("convert:png", "PNG", FileKind.Image, "png");

    // "ImageTragick": un file con estensione da foto che in realtà è uno script MVG; ephemeral: legge e poi CANCELLA.
    // Magick.NET 15 ne è già immune (formato dall'estensione, MVG vietato dalla sua policy): il test fa da guardia
    // in caso un aggiornamento della libreria cambi comportamento.
    [Fact]
    public async Task ScriptDisguisedAsPhoto_CannotDeleteOtherFiles()
    {
        using var dir = new TempDir();
        var victim = Path.Combine(dir.Path, "importante.png");
        new MagickImage(MagickColors.Green, 8, 8).Write(victim);
        var evil = dir.File("foto.jpg",
            $"push graphic-context\nviewbox 0 0 8 8\nimage over 0,0 0,0 'ephemeral:{victim.Replace('\\', '/')}'\npop graphic-context\n");

        try { await new MagickEngine().RunAsync(new Job(evil, ToPng, Path.Combine(dir.Path, "out.png")), new Progress<double>(), default); }
        catch (Exception) { } // rifiutare il file va benissimo: conta che la vittima resti

        Assert.True(File.Exists(victim), "il file 'importante.png' è stato cancellato da un falso JPG");
    }
}

public class TerminalTests
{
    [Fact]
    public void FolderName_CannotChainCommands() =>
        Assert.Equal(["-d", @"C:\Download\foto \; new-tab calc.exe"], Clementine.App.Shell.FileActions.TerminalArgs(@"C:\Download\foto ; new-tab calc.exe"));
}
