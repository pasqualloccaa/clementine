namespace Clementine.Core;

public enum FileKind { Image, Audio, Video }

/// <summary>Un'operazione della ruota. Kind indica quale famiglia di motori la esegue.</summary>
public sealed record Operation(string Id, string Label, FileKind Kind, string TargetExtension);

/// <summary>Un singolo file da elaborare; Output è il percorso (temporaneo) dove il motore deve scrivere.</summary>
public sealed record Job(string Input, Operation Op, string Output);

public interface IEngine
{
    bool CanHandle(Operation op);
    Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct);
}
