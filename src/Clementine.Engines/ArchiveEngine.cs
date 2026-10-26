using Clementine.Core;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;
using SharpCompress.Writers.Tar;
using static Clementine.Core.Lang;

namespace Clementine.Engines;

/// <summary>
/// Riconfeziona un archivio (zip, tar, tar.gz, gz, rar) in zip, tar o tar.gz, senza estrarre su disco;
/// "ZIP" mette file e cartelle in un nuovo archivio.
/// </summary>
public sealed class ArchiveEngine : IEngine
{
    public bool CanHandle(Operation op) => op.Kind == FileKind.Archive || op.Id == Toolbox.Zip.Id;

    public Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct)
    {
        if (job.Op.Id == Toolbox.Zip.Id) Zip(job, progress, ct);
        else Repack(job, progress, ct);
        return Task.CompletedTask;
    }

    static void Zip(Job job, IProgress<double> progress, CancellationToken ct)
    {
        // ogni file con il percorso dentro l'archivio: le cartelle mantengono la loro struttura
        var entries = job.AllInputs.SelectMany(path => Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Select(f => (File: f, Key: Path.Combine(Path.GetFileName(path.TrimEnd('\\', '/')), Path.GetRelativePath(path, f))))
            : [(File: path, Key: Path.GetFileName(path))]).ToList();

        using var output = File.Create(job.Output);
        using var writer = WriterFactory.OpenWriter(output, ArchiveType.Zip, new WriterOptions(CompressionType.Deflate));
        for (var i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            writer.Write(entries[i].Key.Replace('\\', '/'), entries[i].File);
            progress.Report((i + 1.0) / entries.Count);
        }
    }

    static void Repack(Job job, IProgress<double> progress, CancellationToken ct)
    {
        var (type, compression) = job.Op.TargetExtension switch
        {
            "zip" => (ArchiveType.Zip, CompressionType.Deflate),
            "tar" => (ArchiveType.Tar, CompressionType.None),
            "tar.gz" => (ArchiveType.Tar, CompressionType.GZip),
            var ext => throw new NotSupportedException(T($"Formato di uscita non gestito: {ext}", $"Unsupported output format: {ext}")),
        };

        using var input = File.OpenRead(job.Input);
        using var reader = ReaderFactory.OpenReader(input, new ReaderOptions());
        using var output = File.Create(job.Output);
        using var writer = WriterFactory.OpenWriter(output, type, new WriterOptions(compression));
        while (reader.MoveToNextEntry())
        {
            ct.ThrowIfCancellationRequested();
            var entry = reader.Entry;
            if (entry.IsDirectory) continue;
            var key = entry.Key ?? Path.GetFileNameWithoutExtension(job.Input); // un .gz semplice può non avere nome
            using var data = reader.OpenEntryStream();
            if (writer is TarWriter tar)
            {
                // il tar scrive la dimensione prima dei dati, e lo stream della voce non la conosce
                if (entry.Size > 0) tar.Write(key, data, entry.LastModifiedTime, entry.Size);
                else
                {
                    // shortcut: dimensione ignota (.gz semplice): la voce passa dalla RAM, da evitare per file enormi
                    using var buffer = new MemoryStream();
                    data.CopyTo(buffer);
                    buffer.Position = 0;
                    tar.Write(key, buffer, entry.LastModifiedTime, buffer.Length);
                }
            }
            else writer.Write(key, data, entry.LastModifiedTime);
            progress.Report((double)input.Position / input.Length);
        }
    }
}
