using SimplexSuite.Core.Audio;
using Whisper.net;

namespace SimplexSuite.Core.Transcription;

public sealed class TranscriptionOptions
{
    public string ModelPath { get; set; } = "";
    public string Language { get; set; } = "de";
    public bool Translate { get; set; }
    public int Threads { get; set; } = Math.Max(1, Environment.ProcessorCount - 1);
    /// <summary>Wörtlich: ermutigt Whisper, Füllwörter (äh, ähm) mit zu transkribieren.</summary>
    public bool Verbatim { get; set; } = true;
    public string? Prompt { get; set; }
}

/// <summary>Lokale Spracherkennung mit whisper.cpp (Whisper.net) – komplett offline.</summary>
public static class WhisperTranscriber
{
    /// <param name="mono16k">16 kHz Mono-Samples</param>
    public static async Task<Transcript> TranscribeAsync(float[] mono16k, TranscriptionOptions opt, IProgress<double>? progress, CancellationToken ct)
    {
        if (!File.Exists(opt.ModelPath)) throw new FileNotFoundException("Whisper-Modell nicht gefunden. Bitte in den Einstellungen herunterladen.", opt.ModelPath);
        return await Task.Run(async () =>
        {
            using var factory = WhisperFactory.FromPath(opt.ModelPath);
            var builder = factory.CreateBuilder()
                .WithLanguage(string.IsNullOrEmpty(opt.Language) ? "auto" : opt.Language)
                .WithThreads(opt.Threads)
                .WithTokenTimestamps()
                .SplitOnWord()
                .WithMaxSegmentLength(1)
                .WithProgressHandler(pct => progress?.Report(pct / 100.0));
            if (opt.Translate) builder = builder.WithTranslate();
            var prompt = opt.Prompt;
            if (opt.Verbatim && string.IsNullOrEmpty(prompt))
                prompt = opt.Language == "en" ? "Umm, so, uh, I mean, like, you know." : "Ähm, also, äh, ich meine, ja, hm.";
            if (!string.IsNullOrEmpty(prompt)) builder = builder.WithPrompt(prompt);
            using var processor = builder.Build();

            using var wav = new MemoryStream();
            AudioIO.WriteWav16(wav, mono16k, 16000, 1);
            wav.Position = 0;
            var transcript = new Transcript { Language = opt.Language };
            await foreach (var seg in processor.ProcessAsync(wav, ct).ConfigureAwait(false))
            {
                var text = seg.Text.Trim();
                if (text.Length == 0 || (text.StartsWith('[') && text.EndsWith(']'))) continue;
                // Wörter, die mit Satzzeichen beginnen, an das vorige Wort hängen
                if (transcript.Words.Count > 0 && text.Length <= 2 && text.All(ch => char.IsPunctuation(ch)))
                {
                    transcript.Words[^1].Text += text;
                    continue;
                }
                transcript.Words.Add(new TranscriptWord
                {
                    Start = seg.Start.TotalSeconds,
                    End = Math.Max(seg.End.TotalSeconds, seg.Start.TotalSeconds + 0.05),
                    Text = text,
                    Probability = seg.Probability,
                });
            }
            if (opt.Language == "auto" && transcript.Words.Count > 0) transcript.Language = "auto";
            return transcript;
        }, ct).ConfigureAwait(false);
    }

    public static string ToSrt(Transcript t, int maxWords = 9)
    {
        var sb = new System.Text.StringBuilder();
        int i = 1;
        foreach (var line in t.BuildLines(maxWords, 0.8))
        {
            sb.AppendLine((i++).ToString());
            sb.Append(Common.TimeFormat.Srt(line.Start)).Append(" --> ").AppendLine(Common.TimeFormat.Srt(line.End));
            sb.AppendLine(line.Text);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string ToVtt(Transcript t, int maxWords = 9)
    {
        var sb = new System.Text.StringBuilder("WEBVTT\n\n");
        foreach (var line in t.BuildLines(maxWords, 0.8))
        {
            sb.Append(Common.TimeFormat.Vtt(line.Start)).Append(" --> ").AppendLine(Common.TimeFormat.Vtt(line.End));
            sb.AppendLine(line.Text);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string ToText(Transcript t, bool timestamps)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in t.BuildLines(30, 1.5))
        {
            if (timestamps) sb.Append('[').Append(Common.TimeFormat.Format(line.Start, false)).Append("] ");
            sb.AppendLine(line.Text);
        }
        return sb.ToString();
    }
}
