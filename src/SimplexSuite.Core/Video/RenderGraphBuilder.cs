using System.Globalization;
using System.Text;
using SimplexSuite.Core.Common;
using SimplexSuite.Core.Media;

namespace SimplexSuite.Core.Video;

public sealed class RenderRequest
{
    public double RangeStart { get; set; }
    public double RangeEnd { get; set; } = double.MaxValue;
    public double ScaleFactor { get; set; } = 1;
    public bool IncludeVideo { get; set; } = true;
    public bool IncludeAudio { get; set; } = true;
}

public sealed class RenderGraph
{
    public List<string> InputArgs { get; } = new();
    public string FilterComplex { get; set; } = "";
    public string? VideoLabel { get; set; }
    public string? AudioLabel { get; set; }
    public required string WorkDir { get; init; }
    public double Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
}

public sealed class ExportSettings
{
    public string Container { get; set; } = "mp4";      // mp4, mov, mkv, webm, gif, mp3, wav, m4a
    public string VideoCodec { get; set; } = "h264";    // h264, hevc, vp9, av1, prores
    public string Encoder { get; set; } = "cpu";        // cpu, nvenc, qsv, amf
    public int Quality { get; set; } = 20;              // CRF / CQ
    public string Preset { get; set; } = "medium";
    public int AudioBitrateKbps { get; set; } = 192;
    public double ScaleFactor { get; set; } = 1;
    public double RangeStart { get; set; }
    public double RangeEnd { get; set; } = double.MaxValue;

    public bool IsAudioOnly => Container is "mp3" or "wav" or "m4a";
}

/// <summary>Übersetzt ein Timeline-Projekt in einen FFmpeg-Filtergraphen.</summary>
public static class RenderGraphBuilder
{
    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
    private static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);

    public static RenderGraph Build(VideoProject p, RenderRequest r)
    {
        double total = p.Duration;
        double r0 = Math.Max(0, r.RangeStart);
        double r1 = Math.Min(total, r.RangeEnd);
        if (r1 <= r0) r1 = r0 + Math.Max(0.1, 1.0 / p.Fps);
        double T = r1 - r0;
        double s = r.ScaleFactor;
        int W = Even(p.Width * s), H = Even(p.Height * s);
        var g = new RenderGraph { WorkDir = AppPaths.NewTempDir(), Duration = T, Width = W, Height = H, Fps = p.Fps };
        var fc = new List<string>();
        var audioLabels = new List<string>();
        int input = 0;
        string baseLabel = "b0";
        int baseIdx = 0;
        var fontCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (r.IncludeVideo)
            fc.Add($"color=c={Ffmpeg.Hex(p.Background)}:s={W}x{H}:r={F(p.Fps)}:d={F(T)},format=yuv420p[b0]");

        // Unterste Spur zuerst zeichnen
        for (int ti = p.Tracks.Count - 1; ti >= 0; ti--)
        {
            var track = p.Tracks[ti];
            foreach (var clip in track.Clips.OrderBy(c => c.Start))
            {
                double cs = clip.Start, ce = clip.End;
                if (ce <= r0 || cs >= r1) continue;
                double o = Math.Max(0, r0 - cs);
                double sStart = Math.Max(cs, r0) - r0;
                double dur = Math.Min(ce, r1) - Math.Max(cs, r0);
                if (dur <= 0.001) continue;

                if (track.Kind == TrackKind.Text)
                {
                    if (!r.IncludeVideo || track.Hidden || string.IsNullOrWhiteSpace(clip.Text)) continue;
                    string next = "b" + (++baseIdx);
                    fc.Add($"[{baseLabel}]{DrawText(clip, g, s, sStart, sStart + dur, o, fontCache, baseIdx)}[{next}]");
                    baseLabel = next;
                    continue;
                }

                var media = p.FindMedia(clip.MediaId);
                if (media == null || !File.Exists(media.Path)) continue;
                bool visual = r.IncludeVideo && track.Kind == TrackKind.Video && !track.Hidden && media.Kind != MediaKind.Audio;
                bool audible = r.IncludeAudio && !track.Muted && !clip.MuteAudio && media.HasAudio && media.Kind != MediaKind.Image;
                if (!visual && !audible) continue;

                double speed = media.Kind == MediaKind.Image ? 1 : Math.Clamp(clip.Speed, 0.1, 16);
                if (media.Kind == MediaKind.Image)
                {
                    g.InputArgs.AddRange(new[] { "-loop", "1", "-framerate", F(p.Fps), "-t", F(dur + 0.1), "-i", media.Path });
                }
                else
                {
                    g.InputArgs.AddRange(new[] { "-ss", F(clip.In + o * speed), "-t", F(dur * speed + 0.05), "-i", media.Path });
                }
                int k = input++;

                if (visual)
                {
                    string vl = "v" + k;
                    fc.Add($"[{k}:v]{VideoChain(p, clip, media, s, speed, o, dur, sStart)}[{vl}]");
                    string next = "b" + (++baseIdx);
                    string ox = F(clip.PosX * s), oy = F(clip.PosY * s);
                    fc.Add($"[{baseLabel}][{vl}]overlay=x='(W-w)/2+{ox}':y='(H-h)/2+{oy}':eof_action=pass:enable='between(t,{F(sStart)},{F(sStart + dur)})'[{next}]");
                    baseLabel = next;
                }
                if (audible)
                {
                    string al = "a" + k;
                    fc.Add($"[{k}:a]{AudioChain(clip, track, speed, o, dur, sStart)}[{al}]");
                    audioLabels.Add(al);
                }
            }
        }

        if (r.IncludeVideo)
        {
            fc.Add($"[{baseLabel}]format=yuv420p[vout]");
            g.VideoLabel = "vout";
        }
        if (r.IncludeAudio)
        {
            fc.Add($"anullsrc=r=48000:cl=stereo,atrim=0:{F(T)}[asil]");
            if (audioLabels.Count == 0) fc.Add("[asil]anull[aout]");
            else fc.Add("[asil]" + string.Concat(audioLabels.Select(a => $"[{a}]")) +
                        $"amix=inputs={audioLabels.Count + 1}:duration=first:dropout_transition=0:normalize=0,atrim=0:{F(T)}[aout]");
            g.AudioLabel = "aout";
        }
        g.FilterComplex = string.Join(";", fc);
        return g;
    }

    private static string VideoChain(VideoProject p, Clip c, MediaItem m, double s, double speed, double o, double dur, double sStart)
    {
        var f = new List<string>();
        f.Add(m.Kind == MediaKind.Image ? "setpts=PTS-STARTPTS" : $"setpts=(PTS-STARTPTS)/{F(speed)}");
        f.Add($"fps={F(p.Fps)}");
        double cl = Math.Clamp(c.CropLeft, 0, 0.45), cr = Math.Clamp(c.CropRight, 0, 0.45);
        double ct = Math.Clamp(c.CropTop, 0, 0.45), cb = Math.Clamp(c.CropBottom, 0, 0.45);
        if (cl + cr + ct + cb > 0.0001)
            f.Add($"crop=iw*{F(1 - cl - cr)}:ih*{F(1 - ct - cb)}:iw*{F(cl)}:ih*{F(ct)}");
        double srcW = Math.Max(2, (m.Width > 0 ? m.Width : p.Width) * (1 - cl - cr));
        double srcH = Math.Max(2, (m.Height > 0 ? m.Height : p.Height) * (1 - ct - cb));
        double W = p.Width * s, H = p.Height * s;
        double fit = c.Fill ? Math.Max(W / srcW, H / srcH) : Math.Min(W / srcW, H / srcH);
        double sc = Math.Clamp(c.Scale, 0.01, 20);
        f.Add($"scale={Even(srcW * fit * sc)}:{Even(srcH * fit * sc)}:flags=bicubic");
        f.Add("setsar=1");
        if (c.FlipH) f.Add("hflip");
        if (c.FlipV) f.Add("vflip");
        if (Math.Abs(c.Brightness) > 1e-4 || Math.Abs(c.Contrast - 1) > 1e-4 || Math.Abs(c.Saturation - 1) > 1e-4 || Math.Abs(c.Gamma - 1) > 1e-4)
            f.Add($"eq=brightness={F(c.Brightness)}:contrast={F(c.Contrast)}:saturation={F(c.Saturation)}:gamma={F(c.Gamma)}");
        if (Math.Abs(c.Hue) > 1e-4) f.Add($"hue=h={F(c.Hue)}");
        if (Math.Abs(c.Temperature) > 1e-4)
        {
            double t = Math.Clamp(c.Temperature, -1, 1) * 0.3;
            f.Add($"colorbalance=rm={F(t)}:bm={F(-t)}:rh={F(t * 0.5)}:bh={F(-t * 0.5)}");
        }
        if (c.Blur > 0.01) f.Add($"gblur=sigma={F(c.Blur * s)}");
        if (c.Sharpen > 0.01) f.Add($"unsharp=5:5:{F(Math.Clamp(c.Sharpen, 0, 3))}:5:5:0");
        if (c.Vignette > 0.01) f.Add($"vignette=a={F(0.1 + Math.Clamp(c.Vignette, 0, 1) * 1.2)}");
        if (c.ChromaKey)
            f.Add($"chromakey=color={Ffmpeg.Hex(c.ChromaColor)}:similarity={F(Math.Clamp(c.ChromaSimilarity, 0.01, 1))}:blend={F(Math.Clamp(c.ChromaBlend, 0, 1))}");
        f.Add("format=yuva420p");
        if (c.Opacity < 0.999) f.Add($"colorchannelmixer=aa={F(Math.Clamp(c.Opacity, 0, 1))}");
        if (c.FadeIn > o + 0.001)
            f.Add($"fade=t=in:st=0:d={F(c.FadeIn - o)}:alpha=1");
        if (c.FadeOut > 0.001)
        {
            double st = c.Duration - c.FadeOut - o;
            if (st < dur) f.Add($"fade=t=out:st={F(Math.Max(0, st))}:d={F(c.FadeOut)}:alpha=1");
        }
        if (Math.Abs(c.Rotation) > 0.01)
        {
            string rad = F(c.Rotation * Math.PI / 180);
            f.Add($"rotate={rad}:c=none:ow=rotw({rad}):oh=roth({rad})");
        }
        f.Add($"setpts=PTS+{F(sStart)}/TB");
        return string.Join(",", f);
    }

    private static string AudioChain(Clip c, Track t, double speed, double o, double dur, double sStart)
    {
        var f = new List<string> { "asetpts=PTS-STARTPTS" };
        if (Math.Abs(speed - 1) > 1e-4)
        {
            double x = speed;
            while (x > 2.0) { f.Add("atempo=2.0"); x /= 2.0; }
            while (x < 0.5) { f.Add("atempo=0.5"); x /= 0.5; }
            f.Add("atempo=" + F(x));
        }
        f.Add("aformat=sample_rates=48000:channel_layouts=stereo");
        f.Add($"atrim=0:{F(dur)}");
        if (c.Denoise) f.Add("afftdn=nr=18:nf=-40:tn=1");
        double vol = Math.Max(0, c.Volume * t.Volume);
        if (Math.Abs(vol - 1) > 1e-4) f.Add($"volume={F(vol)}");
        if (c.AudioFadeIn > o + 0.001) f.Add($"afade=t=in:st=0:d={F(c.AudioFadeIn - o)}");
        if (c.AudioFadeOut > 0.001)
        {
            double st = c.Duration - c.AudioFadeOut - o;
            if (st < dur) f.Add($"afade=t=out:st={F(Math.Max(0, st))}:d={F(c.AudioFadeOut)}");
        }
        long ms = (long)Math.Round(sStart * 1000);
        if (ms > 0) f.Add($"adelay={ms}:all=1");
        return string.Join(",", f);
    }

    private static string DrawText(Clip c, RenderGraph g, double s, double a, double b, double o, Dictionary<string, string> fontCache, int idx)
    {
        var textFile = $"t{idx}.txt";
        var text = c.Text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        File.WriteAllText(Path.Combine(g.WorkDir, textFile), text, new UTF8Encoding(false));
        var opts = new List<string> { $"textfile={textFile}", "expansion=none" };
        var font = FontCatalog.Find(c.FontFamily, c.Bold, c.Italic) ?? FontCatalog.Default;
        if (font != null)
        {
            if (!fontCache.TryGetValue(font.FilePath, out var local))
            {
                local = $"font{fontCache.Count}{Path.GetExtension(font.FilePath).ToLowerInvariant()}";
                try { File.Copy(font.FilePath, Path.Combine(g.WorkDir, local), true); fontCache[font.FilePath] = local; }
                catch { local = ""; }
            }
            if (local.Length > 0) opts.Add($"fontfile={local}");
        }
        opts.Add($"fontsize={F(Math.Max(4, c.FontSize * s))}");
        opts.Add($"fontcolor={Ffmpeg.Hex(c.FontColor)}");
        if (c.LineSpacing != 0) opts.Add($"line_spacing={F(c.LineSpacing * s)}");
        opts.Add($"x='w*{F(c.TextX)}-text_w/2'");
        opts.Add($"y='h*{F(c.TextY)}-text_h/2'");
        if (c.OutlineWidth > 0.01)
        {
            opts.Add($"borderw={F(Math.Max(1, c.OutlineWidth * s))}");
            opts.Add($"bordercolor={Ffmpeg.Hex(c.OutlineColor)}");
        }
        if (c.ShadowOffset > 0.01)
        {
            opts.Add($"shadowx={F(c.ShadowOffset * s)}");
            opts.Add($"shadowy={F(c.ShadowOffset * s)}");
            opts.Add("shadowcolor=0x000000@0.6");
        }
        if (c.Box)
        {
            opts.Add("box=1");
            opts.Add($"boxcolor={Ffmpeg.Hex(c.BoxColor)}@{F(Math.Clamp(c.BoxOpacity, 0, 1))}");
            opts.Add($"boxborderw={F(Math.Max(0, c.BoxPadding * s))}");
        }
        // Deckkraft inkl. Ein-/Ausblendung als Ausdruck
        var terms = new List<string>();
        if (c.FadeIn > 0.001) terms.Add($"(t-{F(a - o)})/{F(c.FadeIn)}");
        if (c.FadeOut > 0.001) terms.Add($"({F(a - o + c.Duration)}-t)/{F(c.FadeOut)}");
        string alpha = terms.Count switch
        {
            0 => F(Math.Clamp(c.Opacity, 0, 1)),
            1 => $"{F(Math.Clamp(c.Opacity, 0, 1))}*clip({terms[0]},0,1)",
            _ => $"{F(Math.Clamp(c.Opacity, 0, 1))}*clip(min({terms[0]},{terms[1]}),0,1)",
        };
        opts.Add($"alpha='{alpha}'");
        opts.Add($"enable='between(t,{F(a)},{F(b)})'");
        return "drawtext=" + string.Join(":", opts);
    }

    // ------------------------------------------------------------------ Ausgabe-Argumente

    public static List<string> ExportArgs(RenderGraph g, ExportSettings e, string output)
    {
        var a = new List<string> { "-y" };
        a.AddRange(g.InputArgs);
        string fcx = g.FilterComplex;
        string? vLabel = g.VideoLabel;
        if (e.Container == "gif" && vLabel != null)
        {
            fcx += $";[{vLabel}]fps={F(Math.Min(g.Fps, 20))},split[g1][g2];[g1]palettegen=stats_mode=diff[pal];[g2][pal]paletteuse=dither=sierra2_4a[gif]";
            vLabel = "gif";
        }
        a.Add("-filter_complex");
        a.Add(fcx);
        if (!e.IsAudioOnly && vLabel != null) { a.Add("-map"); a.Add($"[{vLabel}]"); }
        if (e.Container != "gif" && g.AudioLabel != null) { a.Add("-map"); a.Add($"[{g.AudioLabel}]"); }

        switch (e.Container)
        {
            case "mp3": a.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", e.AudioBitrateKbps + "k" }); break;
            case "wav": a.AddRange(new[] { "-c:a", "pcm_s16le" }); break;
            case "m4a": a.AddRange(new[] { "-c:a", "aac", "-b:a", e.AudioBitrateKbps + "k" }); break;
            case "gif": a.AddRange(new[] { "-loop", "0" }); break;
            default:
                a.AddRange(VideoCodecArgs(e));
                if (e.Container == "webm") a.AddRange(new[] { "-c:a", "libopus", "-b:a", e.AudioBitrateKbps + "k" });
                else if (e.VideoCodec == "prores") a.AddRange(new[] { "-c:a", "pcm_s16le" });
                else a.AddRange(new[] { "-c:a", "aac", "-b:a", e.AudioBitrateKbps + "k" });
                if (e.Container is "mp4" or "mov") a.AddRange(new[] { "-movflags", "+faststart" });
                break;
        }
        a.AddRange(new[] { "-t", F(g.Duration), output });
        return a;
    }

    private static IEnumerable<string> VideoCodecArgs(ExportSettings e)
    {
        string q = e.Quality.ToString(CultureInfo.InvariantCulture);
        switch (e.VideoCodec)
        {
            case "prores":
                return new[] { "-c:v", "prores_ks", "-profile:v", "3", "-pix_fmt", "yuv422p10le" };
            case "vp9":
                return new[] { "-c:v", "libvpx-vp9", "-crf", q, "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "2", "-pix_fmt", "yuv420p" };
            case "av1":
                return e.Encoder switch
                {
                    "nvenc" => new[] { "-c:v", "av1_nvenc", "-preset", "p5", "-cq", q, "-pix_fmt", "yuv420p" },
                    "qsv" => new[] { "-c:v", "av1_qsv", "-global_quality", q, "-pix_fmt", "nv12" },
                    "amf" => new[] { "-c:v", "av1_amf", "-quality", "quality", "-rc", "cqp", "-qp_i", q, "-qp_p", q },
                    _ => new[] { "-c:v", "libsvtav1", "-crf", q, "-preset", "8", "-pix_fmt", "yuv420p" },
                };
            case "hevc":
                return e.Encoder switch
                {
                    "nvenc" => new[] { "-c:v", "hevc_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", q, "-b:v", "0", "-tag:v", "hvc1", "-pix_fmt", "yuv420p" },
                    "qsv" => new[] { "-c:v", "hevc_qsv", "-global_quality", q, "-tag:v", "hvc1", "-pix_fmt", "nv12" },
                    "amf" => new[] { "-c:v", "hevc_amf", "-quality", "quality", "-rc", "cqp", "-qp_i", q, "-qp_p", q, "-tag:v", "hvc1" },
                    _ => new[] { "-c:v", "libx265", "-preset", e.Preset, "-crf", q, "-tag:v", "hvc1", "-pix_fmt", "yuv420p" },
                };
            default:
                return e.Encoder switch
                {
                    "nvenc" => new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", q, "-b:v", "0", "-pix_fmt", "yuv420p" },
                    "qsv" => new[] { "-c:v", "h264_qsv", "-global_quality", q, "-pix_fmt", "nv12" },
                    "amf" => new[] { "-c:v", "h264_amf", "-quality", "quality", "-rc", "cqp", "-qp_i", q, "-qp_p", q },
                    _ => new[] { "-c:v", "libx264", "-preset", e.Preset, "-crf", q, "-pix_fmt", "yuv420p", "-profile:v", "high" },
                };
        }
    }

    public static List<string> FrameArgs(RenderGraph g, string outputJpg)
    {
        var a = new List<string> { "-hide_banner", "-v", "error", "-y" };
        a.AddRange(g.InputArgs);
        a.AddRange(new[] { "-filter_complex", g.FilterComplex, "-map", $"[{g.VideoLabel}]", "-frames:v", "1", "-q:v", "3", outputJpg });
        return a;
    }

    public static List<string> AudioArgs(RenderGraph g, string outputWav, int sampleRate, int channels)
    {
        var a = new List<string> { "-y" };
        a.AddRange(g.InputArgs);
        a.AddRange(new[] { "-filter_complex", g.FilterComplex, "-map", $"[{g.AudioLabel}]",
            "-ac", channels.ToString(), "-ar", sampleRate.ToString(), "-c:a", "pcm_s16le", "-t", F(g.Duration), outputWav });
        return a;
    }

    public static List<string> PreviewArgs(RenderGraph g, string outputMp4)
    {
        var a = new List<string> { "-y" };
        a.AddRange(g.InputArgs);
        a.AddRange(new[] { "-filter_complex", g.FilterComplex, "-map", $"[{g.VideoLabel}]", "-map", $"[{g.AudioLabel}]",
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-crf", "28", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", "-t", F(g.Duration), outputMp4 });
        return a;
    }

    // ------------------------------------------------------------------ Komfort

    public static async Task<string?> RenderFrameAsync(VideoProject p, double t, double scale, CancellationToken ct)
    {
        if (p.Duration <= 0) return null;
        var g = Build(p, new RenderRequest { RangeStart = Math.Min(t, Math.Max(0, p.Duration - 0.05)), RangeEnd = t + 1, ScaleFactor = scale, IncludeAudio = false });
        var output = Path.Combine(g.WorkDir, "frame.jpg");
        var res = await Ffmpeg.RunAsync(FrameArgs(g, output), ct, g.WorkDir).ConfigureAwait(false);
        if (res.ExitCode != 0) throw new FfmpegException("Vorschau fehlgeschlagen:\n" + Ffmpeg.Tail(res.StdErr, 1200));
        return File.Exists(output) ? output : null;
    }

    public static async Task ExportAsync(VideoProject p, ExportSettings e, string output, IProgress<double>? progress, CancellationToken ct)
    {
        var g = Build(p, new RenderRequest
        {
            RangeStart = e.RangeStart, RangeEnd = e.RangeEnd, ScaleFactor = e.ScaleFactor,
            IncludeVideo = !e.IsAudioOnly, IncludeAudio = e.Container != "gif",
        });
        try
        {
            await Ffmpeg.RunWithProgressAsync(ExportArgs(g, e, output), g.Duration, progress, ct, g.WorkDir).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(g.WorkDir, true); } catch { }
        }
    }

    public static async Task<string> RenderPreviewAsync(VideoProject p, double from, double length, double scale, IProgress<double>? progress, CancellationToken ct)
    {
        var g = Build(p, new RenderRequest { RangeStart = from, RangeEnd = from + length, ScaleFactor = scale });
        var output = Path.Combine(g.WorkDir, "preview.mp4");
        await Ffmpeg.RunWithProgressAsync(PreviewArgs(g, output), g.Duration, progress, ct, g.WorkDir).ConfigureAwait(false);
        return output;
    }

    public static async Task<string> RenderAudioAsync(VideoProject p, int sampleRate, int channels, IProgress<double>? progress, CancellationToken ct)
    {
        var g = Build(p, new RenderRequest { IncludeVideo = false });
        var output = Path.Combine(g.WorkDir, "audio.wav");
        await Ffmpeg.RunWithProgressAsync(AudioArgs(g, output, sampleRate, channels), g.Duration, progress, ct, g.WorkDir).ConfigureAwait(false);
        return output;
    }
}
