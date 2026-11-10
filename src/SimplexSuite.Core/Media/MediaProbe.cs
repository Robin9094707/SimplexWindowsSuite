using System.Globalization;
using System.Text.Json;

namespace SimplexSuite.Core.Media;

public enum MediaKind { Video, Audio, Image }

public sealed class MediaInfo
{
    public double Duration { get; set; }
    public bool HasVideo { get; set; }
    public bool HasAudio { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public int AudioChannels { get; set; }
    public int SampleRate { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public int Rotation { get; set; }
    public MediaKind Kind { get; set; }
}

public static class MediaProbe
{
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

    public static bool IsImage(string path) => ImageExt.Contains(Path.GetExtension(path));

    public static async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        var r = await Ffmpeg.RunToolAsync(Ffmpeg.FfprobePath, new[]
        {
            "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path
        }, ct).ConfigureAwait(false);
        if (r.ExitCode != 0) throw new FfmpegException("Datei konnte nicht gelesen werden: " + Ffmpeg.Tail(r.StdErr, 600));
        var info = new MediaInfo();
        using var doc = JsonDocument.Parse(r.StdOut);
        var root = doc.RootElement;
        if (root.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var d))
            info.Duration = ParseD(d.GetString());
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                if (type == "video" && !info.HasVideo)
                {
                    var disposition = s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                    if (disposition) continue;
                    info.HasVideo = true;
                    info.Width = s.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                    info.Height = s.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                    info.VideoCodec = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                    if (s.TryGetProperty("avg_frame_rate", out var fr)) info.Fps = ParseRate(fr.GetString());
                    if (info.Fps <= 0 && s.TryGetProperty("r_frame_rate", out var rr)) info.Fps = ParseRate(rr.GetString());
                    if (s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rot))
                        info.Rotation = (int)ParseD(rot.GetString());
                    if (s.TryGetProperty("side_data_list", out var sdl))
                        foreach (var sd in sdl.EnumerateArray())
                            if (sd.TryGetProperty("rotation", out var rv) && rv.ValueKind == JsonValueKind.Number)
                                info.Rotation = -rv.GetInt32();
                    if (info.Duration <= 0 && s.TryGetProperty("duration", out var sdur)) info.Duration = ParseD(sdur.GetString());
                }
                else if (type == "audio" && !info.HasAudio)
                {
                    info.HasAudio = true;
                    info.AudioChannels = s.TryGetProperty("channels", out var c) ? c.GetInt32() : 2;
                    info.SampleRate = s.TryGetProperty("sample_rate", out var sr) ? (int)ParseD(sr.GetString()) : 48000;
                    info.AudioCodec = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                    if (info.Duration <= 0 && s.TryGetProperty("duration", out var sdur)) info.Duration = ParseD(sdur.GetString());
                }
            }
        }
        // Hochkant-Videos: Breite/Höhe tauschen
        if (Math.Abs(info.Rotation) % 180 == 90) (info.Width, info.Height) = (info.Height, info.Width);
        if (IsImage(path))
        {
            info.Kind = MediaKind.Image;
            info.HasAudio = false;
            info.Duration = 5;
        }
        else info.Kind = info.HasVideo && info.Fps > 0 && info.VideoCodec is not ("mjpeg" or "png") ? MediaKind.Video : (info.HasAudio ? MediaKind.Audio : MediaKind.Image);
        if (info.Kind == MediaKind.Video && info.Fps <= 0) info.Fps = 30;
        return info;
    }

    private static double ParseD(string? s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double ParseRate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var parts = s.Split('/');
        if (parts.Length == 2)
        {
            var n = ParseD(parts[0]);
            var d = ParseD(parts[1]);
            return d > 0 ? n / d : 0;
        }
        return ParseD(s);
    }

    /// <summary>Erzeugt ein Vorschaubild (JPEG) für Video/Bild.</summary>
    public static async Task<string?> ThumbnailAsync(string path, double atSeconds, int width, CancellationToken ct = default)
    {
        var outFile = Common.AppPaths.NewTempFile(".jpg");
        var args = new List<string> { "-hide_banner", "-v", "error" };
        if (!IsImage(path)) { args.Add("-ss"); args.Add(Common.TimeFormat.Inv(Math.Max(0, atSeconds))); }
        args.AddRange(new[] { "-i", path, "-frames:v", "1", "-vf", $"scale={width}:-2", "-q:v", "4", "-y", outFile });
        var r = await Ffmpeg.RunAsync(args, ct).ConfigureAwait(false);
        return r.ExitCode == 0 && File.Exists(outFile) ? outFile : null;
    }

    /// <summary>Hüllkurve (Peaks) für Waveform-Darstellung: Werte pro Sekunde = rate.</summary>
    public static async Task<float[]> PeaksAsync(string path, int rate, CancellationToken ct = default)
    {
        const int sr = 8000;
        var samples = await Audio.AudioIO.DecodeAsync(path, sr, 1, ct).ConfigureAwait(false);
        int block = sr / rate;
        var peaks = new float[(samples.Length + block - 1) / block];
        for (int i = 0; i < peaks.Length; i++)
        {
            float m = 0;
            int end = Math.Min(samples.Length, (i + 1) * block);
            for (int j = i * block; j < end; j++) m = Math.Max(m, Math.Abs(samples[j]));
            peaks[i] = m;
        }
        return peaks;
    }
}
