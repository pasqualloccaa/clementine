using Clementine.Core;
using Xunit;

namespace Clementine.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("clementine-test-").FullName;
    public string File(string name, string content = "x")
    {
        var p = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllText(p, content);
        return p;
    }
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

public class FormatsTests
{
    static string[] Targets(params string[] files) => Formats.ConversionsFor(files).Select(o => o.TargetExtension!).ToArray();

    [Fact]
    public void Pdf_OffersDocumentsTextAndImages() => Assert.Equal(["docx", "pptx", "epub", "txt", "md", "jpg", "png"], Targets("doc.PDF"));

    [Fact]
    public void OfficeKinds()
    {
        Assert.Equal(["odt", "pdf", "txt", "md", "epub"], Targets("a.docx"));
        Assert.Equal(["xlsx", "ods", "csv", "pdf"], Targets("a.tsv"));
        Assert.Contains("png", Targets("a.pptx"));
    }

    [Theory]
    [InlineData("a.tar.gz")]
    [InlineData("a.tgz")]
    public void TarGz_IsOneFormat(string file) => Assert.Equal(["zip", "tar"], Targets(file));

    [Fact]
    public void Rar_IsReadOnly() => Assert.Equal(["zip", "tar", "tar.gz"], Targets("a.rar"));

    [Fact]
    public void Heic_OffersImageFormats_NotItself()
    {
        var t = Targets(@"C:\a\foto.HEIC");
        Assert.Contains("jpg", t);
        Assert.DoesNotContain("heic", t);
        Assert.DoesNotContain("mp4", t);
    }

    [Fact]
    public void Gif_IsBothImageAndVideo() => Assert.Superset(new HashSet<string> { "png", "mp4", "webm" }, Targets("a.gif").ToHashSet());

    [Fact]
    public void Aliases_AreNormalized() => Assert.DoesNotContain("jpg", Targets("a.jpeg"));

    [Fact]
    public void Batch_KeepsOnlyCommonConversions()
    {
        var t = Targets("a.jpg", "b.png");
        Assert.Contains("webp", t);
        Assert.DoesNotContain("jpg", t);
        Assert.DoesNotContain("png", t);
    }

    [Theory]
    [InlineData("a.jpg", "b.mp3")]
    [InlineData("a.exe")]
    [InlineData]
    public void NothingInCommon_IsEmpty(params string[] files) => Assert.Empty(Targets(files));
}

public class OutputPathTests
{
    [Fact]
    public void AddsCounter_WhenNameIsTaken()
    {
        using var dir = new TempDir();
        var input = dir.File("foto.heic");
        Assert.Equal(System.IO.Path.Combine(dir.Path, "foto.jpg"), OutputPath.Next(input, "jpg"));
        dir.File("foto.jpg");
        dir.File("foto (2).jpg");
        Assert.Equal(System.IO.Path.Combine(dir.Path, "foto (3).jpg"), OutputPath.Next(input, "jpg"));
    }

    [Theory]
    [InlineData("archivio.tar.gz", "zip", null, "archivio.zip")]
    [InlineData("foto.jpg", "jpg", "senza metadati", "foto (senza metadati).jpg")]
    [InlineData("doc.pdf", null, "PNG", "doc (PNG)")]
    public void Names(string input, string? ext, string? suffix, string expected) =>
        Assert.Equal(System.IO.Path.Combine(@"C:\x", expected), OutputPath.Next(System.IO.Path.Combine(@"C:\x", input), ext, suffix));
}

public class PipelineTests
{
    static readonly Operation ToJpg = new("convert:jpg", "JPG", FileKind.Image, "jpg");

    sealed class FakeEngine(Func<Job, Task> run) : IEngine
    {
        public bool CanHandle(Operation op) => true;
        public Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct) => run(job);
    }

    static Task<BatchResult> RunAsync(IEngine engine, params string[] inputs) => RunAsync(engine, ToJpg, inputs);

    static async Task<BatchResult> RunAsync(IEngine engine, Operation op, params string[] inputs)
    {
        using var pipeline = new Pipeline([engine]);
        var done = new TaskCompletionSource<BatchResult>();
        pipeline.Completed += done.SetResult;
        pipeline.Enqueue(inputs, op);
        return await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task FolderOutput_IsMovedToLabelledFolder()
    {
        using var dir = new TempDir();
        var input = dir.File("doc.pdf");
        var toPng = new Operation("convert:png", "PNG", FileKind.Pdf, "png");

        var result = await RunAsync(new FakeEngine(j =>
        {
            Directory.CreateDirectory(j.Output);
            return File.WriteAllTextAsync(System.IO.Path.Combine(j.Output, "doc - 1.png"), "p1");
        }), toPng, input);

        Assert.Equal(System.IO.Path.Combine(dir.Path, "doc (PNG)"), Assert.Single(result.Outputs));
        Assert.True(File.Exists(System.IO.Path.Combine(dir.Path, "doc (PNG)", "doc - 1.png")));
        Assert.Single(Directory.GetDirectories(dir.Path)); // nessuna cartella temporanea rimasta
    }

    [Fact]
    public async Task SameExtensionOperation_UsesSuffix()
    {
        using var dir = new TempDir();
        var input = dir.File("foto.JPG", "originale");

        var result = await RunAsync(new FakeEngine(j => File.WriteAllTextAsync(j.Output, "pulito")), Formats.StripMetadata, input);

        Assert.Equal(System.IO.Path.Combine(dir.Path, "foto (senza metadati).JPG"), Assert.Single(result.Outputs));
        Assert.Equal(input, Assert.Single(result.Sources));
        Assert.Equal("originale", File.ReadAllText(input));
    }

    [Fact]
    public async Task WritesNextToOriginal_WithoutTouchingExistingFiles()
    {
        using var dir = new TempDir();
        var input = dir.File("foto.heic", "originale");
        var existing = dir.File("foto.jpg", "esistente");

        var result = await RunAsync(new FakeEngine(j => File.WriteAllTextAsync(j.Output, "nuovo")), input);

        Assert.Empty(result.Errors);
        Assert.Equal(System.IO.Path.Combine(dir.Path, "foto (2).jpg"), Assert.Single(result.Outputs));
        Assert.Equal("nuovo", File.ReadAllText(result.Outputs[0]));
        Assert.Equal("originale", File.ReadAllText(input));
        Assert.Equal("esistente", File.ReadAllText(existing));
        Assert.Equal(3, Directory.GetFiles(dir.Path).Length); // nessun file temporaneo rimasto
    }

    [Fact]
    public async Task FailedJob_LeavesNoPartialFile_AndOthersContinue()
    {
        using var dir = new TempDir();
        var bad = dir.File("rotto.png");
        var good = dir.File("buono.png");

        var result = await RunAsync(new FakeEngine(async j =>
        {
            await File.WriteAllTextAsync(j.Output, "a metà");
            if (j.Input == bad) throw new InvalidOperationException("boom");
        }), bad, good);

        Assert.Contains("boom", Assert.Single(result.Errors));
        Assert.Equal(System.IO.Path.Combine(dir.Path, "buono.jpg"), Assert.Single(result.Outputs));
        Assert.Equal(good, Assert.Single(result.Sources)); // allineato alle uscite, non agli ingressi
        Assert.Equal(3, Directory.GetFiles(dir.Path).Length);
    }
}
