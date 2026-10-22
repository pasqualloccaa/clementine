using System.Threading.Channels;

namespace Clementine.Core;

/// <summary>Un rilascio sulla ruota: la stessa operazione su uno o più file.</summary>
public sealed record Batch(int Id, IReadOnlyList<string> Inputs, Operation Op);

/// <summary>Sources[i] è l'originale da cui è nato Outputs[i] (i file falliti stanno solo in Errors).</summary>
public sealed record BatchResult(Batch Batch, IReadOnlyList<string> Outputs, IReadOnlyList<string> Errors, IReadOnlyList<string> Sources);

/// <summary>Coda in background: un batch alla volta. Gli eventi arrivano sul thread del worker.</summary>
public sealed class Pipeline : IDisposable
{
    readonly Channel<Batch> _queue = Channel.CreateUnbounded<Batch>(new() { SingleReader = true });
    readonly CancellationTokenSource _cts = new();
    readonly IReadOnlyList<IEngine> _engines;
    int _lastId;

    public event Action<Batch>? Started;
    public event Action<Batch, double>? Progress;
    public event Action<BatchResult>? Completed;

    public Pipeline(IEnumerable<IEngine> engines)
    {
        _engines = engines.ToList();
        _ = Task.Run(WorkAsync);
    }

    public bool CanRun(Operation op) => _engines.Any(e => e.CanHandle(op));

    public Batch Enqueue(IReadOnlyList<string> inputs, Operation op)
    {
        var batch = new Batch(Interlocked.Increment(ref _lastId), inputs, op);
        _queue.Writer.TryWrite(batch);
        return batch;
    }

    public void Dispose() => _cts.Cancel(); // i motori uccidono i processi figli sulla cancellazione

    async Task WorkAsync()
    {
        try
        {
            await foreach (var batch in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                Started?.Invoke(batch);
                var engine = _engines.FirstOrDefault(e => e.CanHandle(batch.Op));
                var outputs = new List<string>();
                var sources = new List<string>();
                var errors = new List<string>();
                for (var i = 0; i < batch.Inputs.Count; i++)
                {
                    var input = batch.Inputs[i];
                    var done = i;
                    var progress = new InlineProgress(p => Progress?.Invoke(batch, (done + p) / batch.Inputs.Count));
                    try
                    {
                        if (engine is null) throw new NotSupportedException($"Nessun motore per {batch.Op.Label}");
                        outputs.Add(await RunAsync(engine, input, batch.Op, progress, _cts.Token));
                        sources.Add(input);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        errors.Add($"{Path.GetFileName(input)}: {e.Message}");
                    }
                }
                Completed?.Invoke(new BatchResult(batch, outputs, errors, sources));
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
    }

    // Il motore scrive su un file (o una cartella) temporaneo accanto all'originale; solo a lavoro finito lo si
    // rinomina senza sovrascrivere nulla. Così un errore non lascia file a metà e l'originale non viene mai toccato.
    static async Task<string> RunAsync(IEngine engine, string input, Operation op, IProgress<double> progress, CancellationToken ct)
    {
        var extension = op.TargetExtension ?? Path.GetExtension(input).TrimStart('.');
        var temp = Path.Combine(Path.GetDirectoryName(input)!,
            $"~{Path.GetFileNameWithoutExtension(input)}.{Guid.NewGuid().ToString("N")[..8]}.{extension}");
        try
        {
            await engine.RunAsync(new Job(input, op, temp), progress, ct);
            var isFolder = Directory.Exists(temp);
            while (true)
            {
                var output = isFolder ? OutputPath.Next(input, null, op.Label) : OutputPath.Next(input, extension, op.Suffix);
                try
                {
                    if (isFolder) Directory.Move(temp, output);
                    else File.Move(temp, output, overwrite: false);
                    return output;
                }
                // creato da qualcun altro nel frattempo: nome successivo
                catch (IOException) when (File.Exists(output) || Directory.Exists(output)) { }
            }
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            else File.Delete(temp);
        }
    }

    sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
