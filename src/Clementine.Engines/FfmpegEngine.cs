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

        string[] args = ["-hide_banner", "-nostdin", "-n", "-i", job.Input, "-sn", .. CodecArgs(job.Op.TargetExtension!),
            "-progress", "pipe:1", "-nostats", job.Output];
        await ProcessRunner.RunAsync(_ffmpeg, args, line =>
        {
            if (duration > 0 && line.StartsWith("out_time_us=") && long.TryParse(line[12..], out var us))
                progress.Report(Math.Clamp(us / 1e6 / duration, 0, 1));
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
        "gif" => ["-an", "-loop", "0", "-vf", "fps=12,scale='min(480,iw)':-2:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse"],
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

    static readonly string[] H264 = ["-c:v", "libx264", "-crf", "23", "-preset", "medium", "-pix_fmt", "yuv420p",
        "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:a", "aac", "-b:a", "160k"];
}
