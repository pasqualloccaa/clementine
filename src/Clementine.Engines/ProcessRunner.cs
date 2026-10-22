using System.Diagnostics;
using System.Text;

namespace Clementine.Engines;

static class ProcessRunner
{
    /// <summary>Esegue il processo, passa ogni riga di stdout a onLine e restituisce stdout; eccezione con la coda di stderr se fallisce.</summary>
    public static async Task<string> RunAsync(string exe, IEnumerable<string> args, Action<string>? onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // ExifTool è Perl: con l'ordine casuale degli hash, scrivere nei HEIC dell'encoder di Windows fallisce
        // ~6 volte su 10 ("Chunk offset in iloc atom is outside media data"). Con l'ordine fisso riesce sempre.
        psi.Environment["PERL_HASH_SEED"] = "0";
        psi.Environment["PERL_PERTURB_KEYS"] = "0";

        using var process = Process.Start(psi)!;
        using var kill = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var stderr = process.StandardError.ReadToEndAsync(); // va svuotato in parallelo, o il processo si blocca
        var stdout = new StringBuilder();
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            stdout.AppendLine(line);
            onLine?.Invoke(line);
        }
        await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.Join(" ", (await stderr).Trim().Split('\n').TakeLast(2)).Trim());
        return stdout.ToString().Trim();
    }
}
