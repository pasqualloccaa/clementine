using Clementine.Core;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;
using SharpCompress.Writers.Tar;

namespace Clementine.Engines;

/// <summary>Riconfeziona un archivio (zip, tar, tar.gz, gz, rar) in zip, tar o tar.gz, senza estrarre su disco.</summary>
public sealed class ArchiveEngine : IEngine
{
    public bool CanHandle(Operation op) => op.Kind == FileKind.Archive;

    public Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct)
    {
        var (type, compression) = job.Op.TargetExtension switch
        {
            "zip" => (ArchiveType.Zip, CompressionType.Deflate),
            "tar" => (ArchiveType.Tar, CompressionType.None),
            "tar.gz" => (ArchiveType.Tar, CompressionType.GZip),
            var ext => throw new NotSupportedException($"Formato di uscita non gestito: {ext}"),
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
        return Task.CompletedTask;
    }
}
