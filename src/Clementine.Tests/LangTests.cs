using Clementine.Core;
using Xunit;

namespace Clementine.Tests;

// Lang.English è globale: questi test non devono girare insieme agli altri, che si aspettano l'italiano
[CollectionDefinition(nameof(LangTests), DisableParallelization = true)]
public class LangCollection;

[Collection(nameof(LangTests))]
public class LangTests
{
    [Fact]
    public void English_ChangesLabelsAndFileNames_AndSwitchesBackLive()
    {
        using var dir = new TempDir();
        var jpg = dir.File("foto.jpg");
        try
        {
            Lang.English = true;
            var compress = Toolbox.ToolsFor([jpg]).Single(o => o.Id == "tool:compress");
            Assert.Equal("Compress", compress.Label);
            Assert.Equal(Path.Combine(dir.Path, "foto (compressed).jpg"), OutputPath.Next(jpg, "jpg", compress.Suffix));
            Assert.Equal("Copy to…", Toolbox.Actions[0].Label);
            Assert.Equal("Remove metadata", Formats.StripMetadata.Label);
        }
        finally { Lang.English = false; }
        Assert.Equal("Comprimi", Toolbox.ToolsFor([jpg]).Single(o => o.Id == "tool:compress").Label); // niente valori rimasti in memoria
    }

    [Theory]
    [InlineData("it", false)]
    [InlineData("en", true)]
    public void Setting_WinsOverWindows(string setting, bool english)
    {
        try
        {
            Lang.Apply(setting);
            Assert.Equal(english, Lang.English);
        }
        finally { Lang.English = false; }
    }
}
