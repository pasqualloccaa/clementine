using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Clementine.Engines;

/// <summary>
/// Registrazione di uno schermo in MP4 con ffmpeg (gdigrab), microfono facoltativo.
/// shortcut: niente audio del PC: servirebbe un dispositivo "Stereo Mix" o la cattura WASAPI, che ffmpeg per Windows non ha.
/// </summary>
public sealed class ScreenRecorder(string ffmpegDir)
{
    readonly string _ffmpeg = Path.Combine(ffmpegDir, "ffmpeg.exe");
    Process? _process;

    public string? Output { get; private set; }
    public bool IsRecording => _process is { HasExited: false };

    public void Start(Rectangle screen, string output, string? microphone)
    {
        if (IsRecording) throw new InvalidOperationException("Registrazione già in corso");
        var psi = new ProcessStartInfo(_ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        // x264 vuole dimensioni pari
        string[] args = ["-hide_banner", "-loglevel", "error", "-n", "-f", "gdigrab", "-framerate", "30",
            "-offset_x", $"{screen.X}", "-offset_y", $"{screen.Y}", "-video_size", $"{screen.Width / 2 * 2}x{screen.Height / 2 * 2}", "-i", "desktop",
            .. microphone is null ? [] : new[] { "-f", "dshow", "-i", $"audio={microphone}" },
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p",
            .. microphone is null ? [] : new[] { "-c:a", "aac", "-b:a", "128k" },
            "-movflags", "+faststart", output];
        foreach (var a in args) psi.ArgumentList.Add(a);
        _process = Process.Start(psi);
        Output = output;
    }

    /// <summary>"q" a ffmpeg: chiude il file per bene (un kill lascerebbe un MP4 illeggibile senza faststart).</summary>
    public async Task<string?> StopAsync()
    {
        if (_process is null) return null;
        if (!_process.HasExited)
        {
            await _process.StandardInput.WriteAsync('q');
            await _process.StandardInput.FlushAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await _process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { _process.Kill(); }
        }
        _process.Dispose();
        _process = null;
        return File.Exists(Output) ? Output : null;
    }

    /// <summary>Il primo microfono che ffmpeg vede (DirectShow), oppure null.</summary>
    public string? Microphone()
    {
        var psi = new ProcessStartInfo(_ffmpeg, "-hide_banner -list_devices true -f dshow -i dummy")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var list = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return Regex.Match(list, "\"([^\"]+)\" \\(audio\\)") is { Success: true } m ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Gli schermi in pixel veri, come li vuole gdigrab. Non Screen.AllScreens: in un'app non "per monitor"
    /// Windows scala le coordinate dei monitor a DPI diversi (125% e 100%) e la registrazione prenderebbe il riquadro sbagliato.
    /// </summary>
    public static List<Rectangle> Screens()
    {
        var screens = new List<Rectangle>();
        var old = SetThreadDpiAwarenessContext(-4); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr _, IntPtr _, ref Rect rect, IntPtr _) =>
            {
                screens.Add(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
                return true;
            }, IntPtr.Zero);
        }
        finally { SetThreadDpiAwarenessContext(old); }
        return screens.OrderBy(s => s.X).ThenBy(s => s.Y).ToList();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    delegate bool MonitorProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorProc proc, IntPtr data);

    [DllImport("user32.dll")]
    static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
