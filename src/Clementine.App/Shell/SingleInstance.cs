using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Clementine.App.Shell;

/// <summary>Una richiesta "--op id file…" inoltrata all'istanza già aperta.</summary>
public sealed record OpRequest(string Op, string[] Paths)
{
    /// <summary>"--op &lt;id&gt; &lt;file&gt;…", altrimenti null.</summary>
    public static OpRequest? Parse(string[] args) =>
        args is ["--op", var op, .. var paths] && paths.Length > 0 ? new OpRequest(op, paths) : null;
}

/// <summary>Una sola Clementine per utente: le successive inoltrano la richiesta alla prima tramite named pipe.</summary>
public sealed class SingleInstance : IDisposable
{
    readonly string _name;
    readonly Mutex _mutex; // conta che esista finché il processo vive: chi lo crea per primo è "la" Clementine
    readonly CancellationTokenSource _cts = new();

    public SingleInstance(string name)
    {
        _name = name;
        _mutex = new Mutex(false, $@"Local\{name}", out var created);
        IsFirst = created;
    }

    public static string DefaultName => $"Clementine-{Environment.UserName}";

    public bool IsFirst { get; }

    /// <summary>Riceve le richieste delle istanze successive (sul thread del pool: chi le usa passi alla UI).</summary>
    public void Listen(Action<OpRequest> onRequest) => Task.Run(async () =>
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                // il server legge e il client scrive: per default solo lo stesso utente (e gli admin) può scrivere
                await using var server = new NamedPipeServerStream(_name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_cts.Token);
                if (await JsonSerializer.DeserializeAsync<OpRequest>(server, cancellationToken: _cts.Token) is { } request)
                    onRequest(request);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is IOException or JsonException) { Serilog.Log.Warning(e, "Richiesta dalla pipe scartata"); }
        }
    });

    /// <summary>Inoltra alla prima istanza; false se non risponde entro il timeout.</summary>
    public static bool TrySend(string name, OpRequest request, int timeoutMs = 10_000)
    {
        // chi ci ha lanciati (Esplora file) ci ha dato il primo piano: lo passiamo all'istanza aperta (dialoghi, Condividi)
        AllowSetForegroundWindow(-1);
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
            client.Connect(timeoutMs); // aspetta anche se la prima istanza sta ancora partendo
            JsonSerializer.Serialize(client, request);
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException) { return false; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _mutex.Dispose();
    }

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
}
