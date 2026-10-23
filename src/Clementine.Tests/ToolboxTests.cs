using Clementine.Core;
using Xunit;

namespace Clementine.Tests;

public class ToolboxTests
{
    static string[] Tools(params string[] files) => Toolbox.ToolsFor(files).Select(o => o.Id).ToArray();

    [Fact]
    public void Jpg_HasImageTools()
    {
        using var dir = new TempDir();
        Assert.Equal(["tool:compress", "tool:resize50", "tool:rotate90", "tool:images-to-pdf", "metadata:strip", "tool:zip"],
            Tools(dir.File("a.jpg")));
    }

    [Fact]
    public void MixedKinds_OnlyCommonTools()
    {
        using var dir = new TempDir();
        Assert.Equal(["metadata:strip", "tool:zip"], Tools(dir.File("a.jpg"), dir.File("b.mp4")));
    }

    [Fact]
    public void MergePdf_NeedsTwoFiles()
    {
        using var dir = new TempDir();
        var a = dir.File("a.pdf");
        Assert.DoesNotContain("tool:merge-pdf", Tools(a));
        Assert.Contains("tool:merge-pdf", Tools(a, dir.File("b.pdf")));
    }

    [Fact]
    public void Folder_OnlyZip()
    {
        using var dir = new TempDir();
        Assert.Equal(["tool:zip"], Tools(dir.Path));
    }

    [Fact]
    public void AnimatedOrVector_NoPixelTools()
    {
        using var dir = new TempDir();
        Assert.DoesNotContain("tool:resize50", Tools(dir.File("a.gif")));
        Assert.DoesNotContain("tool:compress", Tools(dir.File("a.svg")));
    }

    [Fact]
    public void Rings_AreIndexedByRing()
    {
        using var dir = new TempDir();
        var rings = Toolbox.RingsFor([dir.File("a.mp3")]);
        Assert.Contains(rings[(int)Ring.Convert], o => o.Id == "convert:wav");
        Assert.Contains(rings[(int)Ring.Tools], o => o.Id == "tool:normalize");
        Assert.Contains(rings[(int)Ring.Actions], o => o.Id == "action:copy-to");
    }

    [Fact]
    public void Find_LooksInConversionsAndTools()
    {
        using var dir = new TempDir();
        var jpg = dir.File("a.jpg");
        Assert.Equal("PNG", Toolbox.Find(jpg, "convert:png")?.Label);
        Assert.Equal("Ruota 90°", Toolbox.Find(jpg, "tool:rotate90")?.Label);
        Assert.Null(Toolbox.Find(jpg, "tool:merge-pdf"));
    }
}

public class FileOpsTests
{
    [Fact]
    public void Copy_NeverOverwrites_AndCopiesFolders()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        var file = src.File("a.txt", "nuovo");
        var existing = dst.File("a.txt", "vecchio");
        var folder = Directory.CreateDirectory(Path.Combine(src.Path, "cartella")).FullName;
        File.WriteAllText(Path.Combine(folder, "dentro.txt"), "x");

        var copied = FileOps.CopyInto([file, folder], dst.Path);

        Assert.Equal(Path.Combine(dst.Path, "a (2).txt"), copied[0]);
        Assert.Equal("vecchio", File.ReadAllText(existing));
        Assert.Equal("nuovo", File.ReadAllText(copied[0]));
        Assert.True(File.Exists(Path.Combine(dst.Path, "cartella", "dentro.txt")));
        Assert.True(File.Exists(file)); // la copia lascia l'originale
    }

    [Fact]
    public void Move_RemovesSource_AndRefusesFolderIntoItself()
    {
        using var dir = new TempDir();
        var file = dir.File("a.txt", "dati");
        var target = Directory.CreateDirectory(Path.Combine(dir.Path, "dest")).FullName;

        var moved = FileOps.MoveInto([file], target);

        Assert.False(File.Exists(file));
        Assert.Equal("dati", File.ReadAllText(moved[0]));
        Assert.Throws<IOException>(() => FileOps.MoveInto([target], Path.Combine(target)));
    }
}

public class PipelineQueueTests
{
    sealed class FakeEngine(Func<Job, CancellationToken, Task> run) : IEngine
    {
        public bool CanHandle(Operation op) => true;
        public Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct) => run(job, ct);
    }

    static readonly Operation ToJpg = new("convert:jpg", "JPG", FileKind.Image, "jpg");

    [Fact]
    public async Task CombiningOperation_GetsAllInputs_OneOutput()
    {
        using var dir = new TempDir();
        var a = dir.File("a.pdf");
        var b = dir.File("b.pdf");
        IReadOnlyList<string>? seen = null;
        using var pipeline = new Pipeline([new FakeEngine((j, _) => { seen = j.AllInputs; return File.WriteAllTextAsync(j.Output, "unito"); })]);
        var done = new TaskCompletionSource<BatchResult>();
        pipeline.Completed += done.SetResult;

        pipeline.Enqueue([a, b], Toolbox.MergePdf);
        var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([a, b], seen);
        Assert.Equal(Path.Combine(dir.Path, "a (unito).pdf"), Assert.Single(result.Outputs));
    }

    [Fact]
    public async Task Dispose_DuringWork_LeavesNoHalfFile()
    {
        using var dir = new TempDir();
        var started = new TaskCompletionSource();
        var pipeline = new Pipeline([new FakeEngine(async (j, ct) =>
        {
            await File.WriteAllTextAsync(j.Output, "a metà", CancellationToken.None);
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        })]);
        pipeline.Enqueue([dir.File("video.mp4")], ToJpg);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        pipeline.Dispose(); // come alla chiusura dell'app: subito dopo il processo esce

        Assert.Equal(["video.mp4"], Directory.GetFiles(dir.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Cancel_StopsRunningBatch_AndSkipsWaitingOne()
    {
        using var dir = new TempDir();
        var started = new TaskCompletionSource();
        using var pipeline = new Pipeline([new FakeEngine(async (j, ct) =>
        {
            await File.WriteAllTextAsync(j.Output, "a metà", CancellationToken.None);
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        })]);
        var results = new List<BatchResult>();
        var bothDone = new TaskCompletionSource();
        pipeline.Completed += r => { lock (results) { results.Add(r); if (results.Count == 2) bothDone.SetResult(); } };

        var running = pipeline.Enqueue([dir.File("a.png")], ToJpg);
        var waiting = pipeline.Enqueue([dir.File("b.png")], ToJpg);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([running, waiting], pipeline.Queue);

        pipeline.Cancel(waiting.Id);
        pipeline.Cancel(running.Id);
        await bothDone.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results, r => Assert.True(r.Cancelled));
        Assert.Empty(pipeline.Queue);
        Assert.Equal(2, Directory.GetFiles(dir.Path).Length); // solo i due originali: il file a metà è sparito
    }
}
