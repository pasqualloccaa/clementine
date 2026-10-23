using System.Threading.Channels;

namespace Clementine.Core;

/// <summary>Un rilascio sulla ruota: la stessa operazione su uno o più file.</summary>
public sealed record Batch(int Id, IReadOnlyList<string> Inputs, Operation Op);

/// <summary>Sources[i] è l'originale da cui è nato Outputs[i] (i file falliti stanno solo in Errors).</summary>
public sealed record BatchResult(Batch Batch, IReadOnlyList<string> Outputs, IReadOnlyList<string> Errors, IReadOnlyList<string> Sources,
    bool Cancelled = false);

/// <summary>Coda in background: un batch alla volta. Gli eventi arrivano sul thread del worker.</summary>
public sealed class Pipeline : IDisposable
{
    readonly Channel<Batch> _queue = Channel.CreateUnbounded<Batch>(new() { SingleReader = true });
    readonly CancellationTokenSource _cts = new();
    readonly IReadOnlyList<IEngine> _engines;
    readonly object _lock = new();
    readonly List<Batch> _waiting = [];
    readonly HashSet<int> _cancelled = [];
    Batch? _running;
    CancellationTokenSource? _runningCts;
    int _lastId;

    public event Action<Batch>? Started;
    public event Action<Batch, double>? Progress;
    public event Action<BatchResult>? Completed;

    readonly Task _worker;

    public Pipeline(IEnumerable<IEngine> engines)
    {
        _engines = engines.ToList();
        _worker = Task.Run(WorkAsync);
    }

    public bool CanRun(Operation op) => _engines.Any(e => e.CanHandle(op));

    /// <summary>Il batch in corso (primo) e quelli in attesa.</summary>
    public IReadOnlyList<Batch> Queue
    {
        get { lock (_lock) return _running is null ? [.. _waiting] : [_running, .. _waiting]; }
    }

    public Batch Enqueue(IReadOnlyList<string> inputs, Operation op)
    {
        var batch = new Batch(Interlocked.Increment(ref _lastId), inputs, op);
        lock (_lock) _waiting.Add(batch);
        _queue.Writer.TryWrite(batch);
        return batch;
    }

    /// <summary>Annulla un batch in corso o in attesa. I file già finiti restano; quello a metà viene scartato.</summary>
    public void Cancel(int batchId)
    {
        lock (_lock)
        {
            if (_running?.Id == batchId) _runningCts!.Cancel();
            else if (_waiting.RemoveAll(b => b.Id == batchId) > 0) _cancelled.Add(batchId);
        }
    }

    /// <summary>
    /// Annulla il lavoro in corso (i motori uccidono i processi figli) e aspetta che cancelli il suo file
    /// temporaneo: altrimenti chiudendo l'app accanto all'originale resterebbe un "~nome.xxxx.ext" a metà.
    /// </summary>
    public void Dispose()
    {
        _cts.Cancel();
        // un motore sincrono (ImageMagick) potrebbe non fermarsi prima; un worker già fallito non deve far fallire la chiusura
        try { _worker.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { }
    }

    async Task WorkAsync()
    {
        try
        {
            await foreach (var batch in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                CancellationToken ct;
                lock (_lock)
                {
                    _waiting.Remove(batch);
                    if (_cancelled.Remove(batch.Id)) ct = new CancellationToken(canceled: true);
                    else
                    {
                        _running = batch;
                        _runningCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                        ct = _runningCts.Token;
                    }
                }
                if (ct.IsCancellationRequested)
                {
                    Completed?.Invoke(new BatchResult(batch, [], [], [], Cancelled: true));
                    continue;
                }
                BatchResult result;
                try { result = await RunBatchAsync(batch, ct); }
                finally
                {
                    lock (_lock)
                    {
                        _running = null;
                        _runningCts!.Dispose();
                        _runningCts = null;
                    }
                }
                Completed?.Invoke(result);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
    }

    async Task<BatchResult> RunBatchAsync(Batch batch, CancellationToken ct)
    {
        Started?.Invoke(batch);
        var engine = _engines.FirstOrDefault(e => e.CanHandle(batch.Op));
        if (engine is IBatchEngine many && !batch.Op.Combines && batch.Inputs.Count > 1)
            return await RunManyAsync(many, batch, ct);
        // un'operazione che unisce è un solo lavoro con tutti i file; il nome viene dal primo
        var items = batch.Op.Combines ? [batch.Inputs[0]] : batch.Inputs;
        var outputs = new List<string>();
        var sources = new List<string>();
        var errors = new List<string>();
        for (var i = 0; i < items.Count; i++)
        {
            var input = items[i];
            var done = i;
            var progress = new InlineProgress(p => Progress?.Invoke(batch, (done + p) / items.Count));
            try
            {
                if (engine is null) throw new NotSupportedException($"Nessun motore per {batch.Op.Label}");
                outputs.Add(await RunAsync(engine, input, batch.Op, batch.Op.Combines ? batch.Inputs : null, progress, ct));
                sources.Add(input);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _cts.Token.ThrowIfCancellationRequested(); // chiusura dell'app: si esce dal worker
                return new BatchResult(batch, outputs, errors, sources, Cancelled: true);
            }
            catch (Exception e)
            {
                errors.Add($"{Path.GetFileName(input)}: {e.Message}");
            }
        }
        return new BatchResult(batch, outputs, errors, sources);
    }

    // Tutti i file del batch in una sola chiamata al motore; ognuno poi si pubblica (o fallisce) per conto suo.
    async Task<BatchResult> RunManyAsync(IBatchEngine engine, Batch batch, CancellationToken ct)
    {
        var jobs = batch.Inputs.Select(input => new Job(input, batch.Op, TempPath(input, batch.Op))).ToList();
        var outputs = new List<string>();
        var sources = new List<string>();
        var errors = new List<string>();
        try
        {
            string? failure = null;
            try { await engine.RunAsync(jobs, new InlineProgress(p => Progress?.Invoke(batch, p)), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _cts.Token.ThrowIfCancellationRequested(); // chiusura dell'app: si esce dal worker
                return new BatchResult(batch, [], [], [], Cancelled: true);
            }
            catch (Exception e) { failure = e.Message; }

            foreach (var job in jobs)
            {
                try
                {
                    if (!File.Exists(job.Output) && !Directory.Exists(job.Output))
                        throw new InvalidOperationException(failure ?? "nessun file prodotto");
                    outputs.Add(Publish(job.Input, job.Op, job.Output));
                    sources.Add(job.Input);
                }
                catch (Exception e) { errors.Add($"{Path.GetFileName(job.Input)}: {e.Message}"); }
            }
        }
        finally { foreach (var job in jobs) Discard(job.Output); }
        return new BatchResult(batch, outputs, errors, sources);
    }

    // Il motore scrive su un file (o una cartella) temporaneo accanto all'originale; solo a lavoro finito lo si
    // rinomina senza sovrascrivere nulla. Così un errore non lascia file a metà e l'originale non viene mai toccato.
    static async Task<string> RunAsync(IEngine engine, string input, Operation op, IReadOnlyList<string>? inputs,
        IProgress<double> progress, CancellationToken ct)
    {
        var temp = TempPath(input, op);
        try
        {
            await engine.RunAsync(new Job(input, op, temp, inputs), progress, ct);
            ct.ThrowIfCancellationRequested(); // i motori sincroni possono finire anche se annullati
            return Publish(input, op, temp);
        }
        finally { Discard(temp); }
    }

    static string Extension(string input, Operation op) => op.TargetExtension ?? Path.GetExtension(input).TrimStart('.');

    static string TempPath(string input, Operation op) =>
        Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(input))!,
            $"~{Path.GetFileNameWithoutExtension(input)}.{Guid.NewGuid().ToString("N")[..8]}.{Extension(input, op)}");

    // dal nome temporaneo al primo nome libero, senza sovrascrivere nulla
    static string Publish(string input, Operation op, string temp)
    {
        var isFolder = Directory.Exists(temp);
        while (true)
        {
            var output = isFolder ? OutputPath.Next(input, null, op.Suffix ?? op.Label) : OutputPath.Next(input, Extension(input, op), op.Suffix);
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

    static void Discard(string temp)
    {
        if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        else File.Delete(temp);
    }

    sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
