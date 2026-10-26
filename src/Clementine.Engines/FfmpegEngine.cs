using System.Globalization;
using Clementine.Core;

namespace Clementine.Engines;

/// <summary>Audio, video e GIF animate con ffmpeg (tools\ffmpeg).</summary>
public sealed class FfmpegEngine(string ffmpegDir) : IEngine
{
    readonly string _ffmpeg = Path.Combine(ffmpegDir, "ffmpeg.exe");
    readonly string _ffprobe = Path.Combine(ffmpegDir, "ffprobe.exe");

    public bool CanHandle(Operation op) => op.Kind is FileKind.Audio or FileKind.Video && File.Exists(_ffmpeg);

    public async Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct)
    {
        var duration = double.TryParse(
            await ProcessRunner.RunAsync(_ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", job.Input], null, ct),
            CultureInfo.InvariantCulture, out var d) ? d : 0;

        var output = job.Output;
        var ext = Formats.Extension(output);
        var arg = job.Op.Argument ?? "";
        string[] input = [];
        if (job.Op.Id == "tool:trim")
        {
            var (start, end) = ToolArgs.TimeRange(arg) ?? throw new ArgumentException($"Intervallo non valido: {arg}");
            if (duration > 0 && start >= duration) throw new InvalidOperationException($"Il file dura {duration:0} secondi: l'inizio è oltre la fine");
            input = ["-ss", Invariant(start), "-to", Invariant(end)];
            duration = (duration > 0 ? Math.Min(end, duration) : end) - start;
        }
        var speed = job.Op.Id == "tool:speed" ? double.Parse(arg, CultureInfo.InvariantCulture) : 1;
        string[] codec = job.Op.Id switch
        {
            "tool:compress-video" => Compress,
            "tool:web" => Web,
            "tool:speed" => Speed(speed),
            "tool:target-size" => TargetSize(ToolArgs.Size(arg) ?? throw new ArgumentException($"Dimensione non valida: {arg}"), duration),
            // loudnorm lavora a 192 kHz: si torna a 48 kHz, che vale per tutti i formati di uscita
            "tool:normalize" => ["-af", "loudnorm=I=-16:TP=-1.5:LRA=11", "-ar", "48000", .. CodecArgs(ext)],
            "tool:mono" => ["-ac", "1", .. CodecArgs(ext)],
            "tool:frames" => ["-an", "-vf", "fps=1", "-q:v", "3"],
            _ => CodecArgs(ext), // conversioni ed "Estrai audio"
        };
        if (job.Op.Id == "tool:frames")
        {
            // un JPG al secondo, in una cartella
            output = Path.Combine(Directory.CreateDirectory(job.Output).FullName, $"{Path.GetFileNameWithoutExtension(job.Input)} - %04d.jpg");
        }

        string[] args = ["-hide_banner", "-nostdin", "-n", .. input, "-i", job.Input, "-sn", .. codec, "-progress", "pipe:1", "-nostats", output];
        await ProcessRunner.RunAsync(_ffmpeg, args, line =>
        {
            if (duration > 0 && line.StartsWith("out_time_us=") && long.TryParse(line[12..], out var us))
                progress.Report(Math.Clamp(us / 1e6 / (duration / speed), 0, 1));
        }, ct);
    }

    static string[] CodecArgs(string ext) => ext switch
    {
        // x264 con yuv420p vuole dimensioni pari (le GIF spesso non lo sono)
        "mp4" or "mov" => [.. H264, "-movflags", "+faststart"],
        "mkv" => H264,
        "webm" => ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-c:a", "libopus", "-b:a", "128k"],
        "avi" => ["-c:v", "mpeg4", "-q:v", "3", "-c:a", "libmp3lame", "-q:a", "4"],
        "wmv" => ["-c:v", "wmv2", "-q:v", "3", "-c:a", "wmav2", "-b:a", "192k"],
        // 25 fotogrammi al secondo e 640 px: a 12 fps scattava, a 20 non ancora fluida; palette ricalcolata solo sulle parti che cambiano
        "gif" => ["-an", "-loop", "0", "-vf", "fps=25,scale='min(640,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=sierra2_4a:diff_mode=rectangle"],
        "mp3" => ["-vn", "-c:a", "libmp3lame", "-q:a", "2"],
        "m4a" => ["-vn", "-c:a", "aac", "-b:a", "192k"],
        "wav" => ["-vn", "-c:a", "pcm_s16le"],
        "aiff" => ["-vn", "-c:a", "pcm_s16be"],
        "flac" => ["-vn", "-c:a", "flac"],
        "ogg" => ["-vn", "-c:a", "libvorbis", "-q:a", "5"],
        "opus" => ["-vn", "-c:a", "libopus", "-b:a", "128k"],
        "wma" => ["-vn", "-c:a", "wmav2", "-b:a", "192k"],
        _ => throw new NotSupportedException($"Formato di uscita non gestito: {ext}"),
    };

    // H.264 più compresso, al massimo 1080p di larghezza 1920
    static readonly string[] Compress = ["-c:v", "libx264", "-crf", "28", "-preset", "medium", "-pix_fmt", "yuv420p",
        "-vf", @"scale=trunc(min(1920\,iw)/2)*2:-2", "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart"];

    static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);

    // video più veloce o più lento, audio compreso (atempo accetta 0,5–2)
    static string[] Speed(double speed) => ["-vf", $"setpts=PTS/{Invariant(speed)},scale=trunc(iw/2)*2:trunc(ih/2)*2", "-af", $"atempo={Invariant(speed)}",
        "-c:v", "libx264", "-crf", "23", "-preset", "medium", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart"];

    /// <summary>
    /// Bitrate dal peso voluto e dalla durata, con il 5% di margine; maxrate tiene il risultato sotto il limite.
    /// Con pochi kbit/s si riduce anche la risoluzione: meglio piccolo e nitido che grande e a quadretti.
    /// </summary>
    internal static string[] TargetSize(long bytes, double duration)
    {
        if (duration <= 0) throw new InvalidOperationException("Durata del video sconosciuta: non si può calcolare la compressione");
        const int audio = 96;
        var video = (int)Math.Max(bytes * 8 / 1000.0 / duration * 0.95 - audio, 50);
        var width = video >= 2500 ? 1920 : video >= 1000 ? 1280 : 854;
        return ["-c:v", "libx264", "-b:v", $"{video}k", "-maxrate", $"{video * 6 / 5}k", "-bufsize", $"{video * 2}k", "-preset", "medium",
            "-pix_fmt", "yuv420p", "-vf", $@"scale=trunc(min({width}\,iw)/2)*2:-2", "-c:a", "aac", "-b:a", $"{audio}k", "-movflags", "+faststart"];
    }

    // preset "Per il web": 1280 px, più compresso, senza metadati, riproducibile mentre si scarica
    static readonly string[] Web = ["-map_metadata", "-1", "-c:v", "libx264", "-crf", "26", "-preset", "medium", "-pix_fmt", "yuv420p",
        "-vf", @"scale=trunc(min(1280\,iw)/2)*2:-2", "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart"];

    static readonly string[] H264 =["-c:v", "libx264", "-crf", "23", "-preset", "medium", "-pix_fmt", "yuv420p",
        "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:a", "aac", "-b:a", "160k"];
}
