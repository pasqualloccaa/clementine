using System.Text;
using System.Text.RegularExpressions;
using Clementine.Core;

namespace Clementine.Engines;

/// <summary>
/// SRT ↔ VTT a mano: cambiano intestazione, virgola/punto nei tempi e numerazione.
/// Le impostazioni di posizione VTT si perdono; dei tag restano solo &lt;i&gt; &lt;b&gt; &lt;u&gt;, che capiscono entrambi.
/// </summary>
public sealed class SubtitleEngine : IEngine
{
    static readonly Regex Time = new(@"(?:(\d+):)?(\d{1,2}):(\d{2})[.,](\d{1,3})");
    static readonly Regex OtherTags = new(@"<(?!/?[ibu]>)[^>]*>");

    public bool CanHandle(Operation op) => op.Kind == FileKind.Subtitle;

    public async Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct)
    {
        var cues = Parse(Read(job.Input));
        if (cues.Count == 0) throw new InvalidDataException("Nessun sottotitolo trovato nel file.");
        var text = job.Op.TargetExtension switch
        {
            "vtt" => "WEBVTT\n\n" + string.Concat(cues.Select(c => $"{Stamp(c.Start, '.')} --> {Stamp(c.End, '.')}\n{c.Text}\n\n")),
            "srt" => string.Concat(cues.Select((c, i) =>
                $"{i + 1}\r\n{Stamp(c.Start, ',')} --> {Stamp(c.End, ',')}\r\n{c.Text.Replace("\n", "\r\n")}\r\n\r\n")),
            var ext => throw new NotSupportedException($"Formato di uscita non gestito: {ext}"),
        };
        await File.WriteAllTextAsync(job.Output, text, new UTF8Encoding(false), ct);
        progress.Report(1);
    }

    /// <summary>UTF-8 (o UTF-16 con BOM); i vecchi SRT italiani sono spesso in Windows-1252.</summary>
    static string Read(string path)
    {
        try
        {
            using var reader = new StreamReader(path, new UTF8Encoding(false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            return File.ReadAllText(path, CodePagesEncodingProvider.Instance.GetEncoding(1252)!);
        }
    }

    sealed record Cue(TimeSpan Start, TimeSpan End, string Text);

    static List<Cue> Parse(string text)
    {
        var cues = new List<Cue>();
        foreach (var block in Regex.Split(text.Replace("\r\n", "\n").Replace('\r', '\n'), @"\n[ \t]*\n"))
        {
            var lines = block.Split('\n');
            var timing = Array.FindIndex(lines, l => l.Contains("-->"));
            if (timing < 0) continue; // intestazione WEBVTT, NOTE, STYLE, REGION
            var times = Time.Matches(lines[timing]);
            if (times.Count < 2) continue;
            var body = OtherTags.Replace(string.Join("\n", lines[(timing + 1)..]), "").Trim();
            cues.Add(new(ToTime(times[0]), ToTime(times[1]), body));
        }
        return cues;
    }

    static TimeSpan ToTime(Match m) => new(0,
        m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
        int.Parse(m.Groups[4].Value.PadRight(3, '0')));

    static string Stamp(TimeSpan t, char separator) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{separator}{t.Milliseconds:000}";
}
