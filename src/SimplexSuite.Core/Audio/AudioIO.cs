using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SimplexSuite.Core.Common;
using SimplexSuite.Core.Media;

namespace SimplexSuite.Core.Audio;

public sealed class AudioExportOptions
{
    public string Format { get; set; } = "mp3";       // wav16, wav24, wav32f, mp3, flac, m4a, opus, ogg
    public int BitrateKbps { get; set; } = 192;
    public int SampleRate { get; set; }                // 0 = unverändert
    public bool Mono { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? Comment { get; set; }
    public string? Year { get; set; }
    public string? Genre { get; set; }
    public string? CoverImage { get; set; }
    public List<(double Time, string Title)> Chapters { get; set; } = new();
}

public static class AudioIO
{
    public static readonly string[] SupportedExtensions =
    {
        ".wav", ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aiff", ".aif", ".mp4", ".mkv", ".mov", ".webm", ".avi"
    };

    /// <summary>Dekodiert jede von FFmpeg lesbare Datei in Float-Samples (interleaved).</summary>
    public static async Task<float[]> DecodeAsync(string path, int sampleRate, int channels, CancellationToken ct = default,
        IProgress<double>? progress = null, double durationHint = 0)
    {
        var args = new[]
        {
            "-hide_banner", "-v", "error", "-i", path, "-vn", "-sn", "-dn",
            "-ac", channels.ToString(), "-ar", sampleRate.ToString(),
            "-f", "f32le", "-acodec", "pcm_f32le", "pipe:1"
        };
        using var p = new Process { StartInfo = Ffmpeg.CreateStartInfo(Ffmpeg.FfmpegPath, args) };
        if (!p.Start()) throw new FfmpegException("FFmpeg konnte nicht gestartet werden.");
        p.StandardInput.Close();
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var reg = ct.Register(() => Ffmpeg.TryKill(p));
        var stream = p.StandardOutput.BaseStream;
        long expected = durationHint > 0 ? (long)(durationHint * sampleRate * channels) : 0;
        var chunks = new List<float[]>();
        long total = 0;
        var buffer = new byte[1 << 20];
        int pending = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(pending, buffer.Length - pending), ct).ConfigureAwait(false);
            if (read <= 0) break;
            pending += read;
            int usable = pending - pending % 4;
            var floats = new float[usable / 4];
            Buffer.BlockCopy(buffer, 0, floats, 0, usable);
            chunks.Add(floats);
            total += floats.Length;
            int rest = pending - usable;
            if (rest > 0) Buffer.BlockCopy(buffer, usable, buffer, 0, rest);
            pending = rest;
            if (expected > 0) progress?.Report(Math.Min(1, (double)total / expected));
        }
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        var err = await errTask.ConfigureAwait(false);
        if (p.ExitCode != 0 && total == 0) throw new FfmpegException("Audio konnte nicht gelesen werden:\n" + Ffmpeg.Tail(err, 1500));
        var result = new float[total];
        long pos = 0;
        foreach (var c in chunks)
        {
            Array.Copy(c, 0, result, pos, c.Length);
            pos += c.Length;
        }
        return result;
    }

    /// <summary>Schreibt Float-Samples über FFmpeg in das gewünschte Format.</summary>
    public static async Task EncodeAsync(float[] samples, int sampleRate, int channels, string outPath, AudioExportOptions opt,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var args = new List<string> { "-hide_banner", "-v", "error", "-y",
            "-f", "f32le", "-ar", sampleRate.ToString(), "-ac", channels.ToString(), "-i", "pipe:0" };
        int inputs = 1;
        string? metaFile = null;
        if (opt.Chapters.Count > 0)
        {
            metaFile = AppPaths.NewTempFile(".txt");
            File.WriteAllText(metaFile, BuildFfMetadata(opt.Chapters, samples.Length / channels / (double)sampleRate), new UTF8Encoding(false));
            args.AddRange(new[] { "-f", "ffmetadata", "-i", metaFile });
            inputs++;
        }
        bool cover = !string.IsNullOrEmpty(opt.CoverImage) && File.Exists(opt.CoverImage) && opt.Format is "mp3" or "flac" or "m4a";
        int coverIndex = -1;
        if (cover)
        {
            args.AddRange(new[] { "-i", opt.CoverImage! });
            coverIndex = inputs++;
        }
        args.AddRange(new[] { "-map", "0:a" });
        if (metaFile != null) args.AddRange(new[] { "-map_metadata", "1", "-map_chapters", "1" });
        if (cover)
        {
            args.AddRange(new[] { "-map", coverIndex + ":v", "-disposition:v:0", "attached_pic" });
            args.AddRange(opt.Format == "m4a" ? new[] { "-c:v", "mjpeg" } : new[] { "-c:v", "mjpeg", "-metadata:s:v", "title=Album cover", "-metadata:s:v", "comment=Cover (front)" });
        }
        if (opt.Mono && channels > 1) args.AddRange(new[] { "-ac", "1" });
        if (opt.SampleRate > 0) args.AddRange(new[] { "-ar", opt.SampleRate.ToString() });
        switch (opt.Format)
        {
            case "wav16": args.AddRange(new[] { "-c:a", "pcm_s16le" }); break;
            case "wav24": args.AddRange(new[] { "-c:a", "pcm_s24le" }); break;
            case "wav32f": args.AddRange(new[] { "-c:a", "pcm_f32le" }); break;
            case "flac": args.AddRange(new[] { "-c:a", "flac", "-compression_level", "8" }); break;
            case "m4a": args.AddRange(new[] { "-c:a", "aac", "-b:a", opt.BitrateKbps + "k", "-movflags", "+faststart" }); break;
            case "opus": args.AddRange(new[] { "-c:a", "libopus", "-b:a", opt.BitrateKbps + "k" }); break;
            case "ogg": args.AddRange(new[] { "-c:a", "libvorbis", "-b:a", opt.BitrateKbps + "k" }); break;
            default: args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", opt.BitrateKbps + "k", "-id3v2_version", "3", "-write_id3v1", "1" }); break;
        }
        void Meta(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) { args.Add("-metadata"); args.Add(key + "=" + value); }
        }
        Meta("title", opt.Title);
        Meta("artist", opt.Artist);
        Meta("album", opt.Album);
        Meta("comment", opt.Comment);
        Meta("date", opt.Year);
        Meta("genre", opt.Genre);
        args.Add(outPath);

        using var p = new Process { StartInfo = Ffmpeg.CreateStartInfo(Ffmpeg.FfmpegPath, args) };
        if (!p.Start()) throw new FfmpegException("FFmpeg konnte nicht gestartet werden.");
        var errTask = p.StandardError.ReadToEndAsync(ct);
        _ = p.StandardOutput.ReadToEndAsync(ct);
        using var reg = ct.Register(() => Ffmpeg.TryKill(p));
        try
        {
            var stdin = p.StandardInput.BaseStream;
            const int chunk = 1 << 18;
            for (long i = 0; i < samples.Length; i += chunk)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(chunk, samples.Length - i);
                var bytes = MemoryMarshal.AsBytes(samples.AsSpan((int)i, n)).ToArray();
                await stdin.WriteAsync(bytes, ct).ConfigureAwait(false);
                progress?.Report((double)(i + n) / samples.Length);
            }
            stdin.Close();
        }
        catch (IOException)
        {
            // FFmpeg beendet – Fehlertext folgt unten
        }
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        var err = await errTask.ConfigureAwait(false);
        if (metaFile != null) try { File.Delete(metaFile); } catch { }
        if (p.ExitCode != 0) throw new FfmpegException("Export fehlgeschlagen:\n" + Ffmpeg.Tail(err, 1500));
    }

    private static string BuildFfMetadata(List<(double Time, string Title)> chapters, double totalSeconds)
    {
        var sb = new StringBuilder(";FFMETADATA1\n");
        var sorted = chapters.OrderBy(c => c.Time).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            long start = (long)(sorted[i].Time * 1000);
            long end = (long)((i + 1 < sorted.Count ? sorted[i + 1].Time : totalSeconds) * 1000);
            if (end <= start) continue;
            sb.Append("[CHAPTER]\nTIMEBASE=1/1000\n");
            sb.Append("START=").Append(start).Append('\n');
            sb.Append("END=").Append(end).Append('\n');
            sb.Append("title=").Append(Escape(sorted[i].Title)).Append('\n');
        }
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("=", "\\=").Replace(";", "\\;").Replace("#", "\\#").Replace("\n", " ");

    /// <summary>Verarbeitet Samples durch einen beliebigen FFmpeg-Audiofilter (z. B. afftdn, arnndn, atempo).</summary>
    public static async Task<float[]> ProcessWithFilterAsync(float[] samples, int sampleRate, int channels, string filter,
        CancellationToken ct = default, string? workDir = null)
    {
        var inFile = AppPaths.NewTempFile(".f32");
        try
        {
            await File.WriteAllBytesAsync(inFile, MemoryMarshal.AsBytes(samples.AsSpan()).ToArray(), ct).ConfigureAwait(false);
            var args = new[]
            {
                "-hide_banner", "-v", "error", "-f", "f32le", "-ar", sampleRate.ToString(), "-ac", channels.ToString(), "-i", inFile,
                "-af", filter + ",aresample=" + sampleRate, "-ac", channels.ToString(), "-f", "f32le", "-acodec", "pcm_f32le", "pipe:1"
            };
            using var p = new Process { StartInfo = Ffmpeg.CreateStartInfo(Ffmpeg.FfmpegPath, args, workDir) };
            if (!p.Start()) throw new FfmpegException("FFmpeg konnte nicht gestartet werden.");
            p.StandardInput.Close();
            var errTask = p.StandardError.ReadToEndAsync(ct);
            using var reg = ct.Register(() => Ffmpeg.TryKill(p));
            using var ms = new MemoryStream();
            await p.StandardOutput.BaseStream.CopyToAsync(ms, ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            var err = await errTask.ConfigureAwait(false);
            if (p.ExitCode != 0) throw new FfmpegException("Filter fehlgeschlagen:\n" + Ffmpeg.Tail(err, 1500));
            var bytes = ms.GetBuffer();
            var result = new float[ms.Length / 4];
            Buffer.BlockCopy(bytes, 0, result, 0, result.Length * 4);
            return result;
        }
        finally
        {
            try { File.Delete(inFile); } catch { }
        }
    }

    /// <summary>Schreibt eine 16-bit-WAV-Datei (für Whisper).</summary>
    public static void WriteWav16(Stream stream, float[] samples, int sampleRate, int channels)
    {
        using var bw = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        int dataLen = samples.Length * 2;
        bw.Write("RIFF"u8.ToArray());
        bw.Write(36 + dataLen);
        bw.Write("WAVE"u8.ToArray());
        bw.Write("fmt "u8.ToArray());
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * 2);
        bw.Write((short)(channels * 2));
        bw.Write((short)16);
        bw.Write("data"u8.ToArray());
        bw.Write(dataLen);
        foreach (var s in samples)
            bw.Write((short)Math.Clamp(s * 32767f, -32768f, 32767f));
    }

    /// <summary>Einfache Resampling-Funktion (linear interpoliert, mit Vorfilter) auf Mono.</summary>
    public static float[] ToMono16k(float[] samples, int sampleRate, int channels)
    {
        long frames = samples.Length / channels;
        var mono = new float[frames];
        for (long i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++) sum += samples[i * channels + c];
            mono[i] = sum / channels;
        }
        if (sampleRate == 16000) return mono;
        // Tiefpass gegen Aliasing
        var lp = new Dsp.Biquad();
        lp.SetLowPass(sampleRate, 7600, 0.707);
        for (long i = 0; i < mono.Length; i++) mono[i] = lp.Process(mono[i]);
        var lp2 = new Dsp.Biquad();
        lp2.SetLowPass(sampleRate, 7600, 0.707);
        for (long i = 0; i < mono.Length; i++) mono[i] = lp2.Process(mono[i]);
        double ratio = (double)sampleRate / 16000;
        long outLen = (long)(mono.Length / ratio);
        var res = new float[outLen];
        for (long i = 0; i < outLen; i++)
        {
            double src = i * ratio;
            long i0 = (long)src;
            double f = src - i0;
            float a = mono[Math.Min(i0, mono.Length - 1)];
            float b = mono[Math.Min(i0 + 1, mono.Length - 1)];
            res[i] = (float)(a + (b - a) * f);
        }
        return res;
    }
}
