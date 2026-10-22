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
    static string[] Targets(params string[] files) => Formats.ConversionsFor(files).Select(o => o.TargetExtension).ToArray();

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
}

public class PipelineTests
{
    static readonly Operation ToJpg = new("convert:jpg", "JPG", FileKind.Image, "jpg");

    sealed class FakeEngine(Func<Job, Task> run) : IEngine
    {
        public bool CanHandle(Operation op) => true;
        public Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct) => run(job);
    }

    static async Task<BatchResult> RunAsync(IEngine engine, params string[] inputs)
    {
        using var pipeline = new Pipeline([engine]);
        var done = new TaskCompletionSource<BatchResult>();
        pipeline.Completed += done.SetResult;
        pipeline.Enqueue(inputs, ToJpg);
        return await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
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
        Assert.Equal(3, Directory.GetFiles(dir.Path).Length);
    }
}
