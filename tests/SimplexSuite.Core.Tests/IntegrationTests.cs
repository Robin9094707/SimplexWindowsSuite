using SimplexSuite.Core.Audio;
using SimplexSuite.Core.Audio.Effects;
using SimplexSuite.Core.Common;
using SimplexSuite.Core.Media;
using SimplexSuite.Core.Transcription;
using SimplexSuite.Core.Video;
using Xunit;

namespace SimplexSuite.Core.Tests;

/// <summary>Integrationstests mit echtem FFmpeg (laufen nur, wenn ffmpeg im PATH ist).</summary>
public class IntegrationTests
{
    private static bool HasFfmpeg => Ffmpeg.IsAvailable;

    private static float[] Sine(int sr, double seconds, double freq, double amp)
    {
        var d = new float[(int)(sr * seconds)];
        for (int i = 0; i < d.Length; i++) d[i] = (float)(amp * Math.Sin(2 * Math.PI * freq * i / sr));
        return d;
    }

    [Fact]
    public async Task Mp3RoundTripWithChapters()
    {
        if (!HasFfmpeg) return;
        var file = AppPaths.NewTempFile(".mp3");
        var opt = new AudioExportOptions { Format = "mp3", Title = "Test", Artist = "Simplex" };
        opt.Chapters.Add((0, "Intro"));
        opt.Chapters.Add((1.5, "Hauptteil"));
        await AudioIO.EncodeAsync(Sine(48000, 3, 440, 0.3), 48000, 1, file, opt);
        var info = await MediaProbe.ProbeAsync(file);
        Assert.True(info.HasAudio);
        Assert.InRange(info.Duration, 2.9, 3.2);
        var back = await AudioIO.DecodeAsync(file, 48000, 1);
        Assert.InRange(back.Length / 48000.0, 2.9, 3.2);
        var doc = await AudioDocument.LoadAsync(file, 48000, false, null, default);
        Assert.Equal(1, doc.Channels);
        File.Delete(file);
    }

    [Theory]
    [InlineData("wav16")]
    [InlineData("flac")]
    [InlineData("m4a")]
    [InlineData("opus")]
    public async Task AllAudioFormatsEncode(string format)
    {
        if (!HasFfmpeg) return;
        var file = AppPaths.NewTempFile(format == "wav16" ? ".wav" : "." + format);
        await AudioIO.EncodeAsync(Sine(48000, 1, 440, 0.3), 48000, 1, file, new AudioExportOptions { Format = format, BitrateKbps = 128 });
        Assert.True(new FileInfo(file).Length > 1000);
        File.Delete(file);
    }

    [Fact]
    public async Task FfmpegFiltersProcess()
    {
        if (!HasFfmpeg) return;
        var d = Sine(48000, 2, 300, 0.3);
        var denoised = await FfmpegFilterEffect.FftDenoise(12, -50, true).ProcessAsync(d, 1, 48000, null, default);
        Assert.InRange(denoised.Length, d.Length - 4800, d.Length + 4800);
        var faster = await FfmpegFilterEffect.Tempo(2).ProcessAsync(d, 1, 48000, null, default);
        Assert.InRange(faster.Length / 48000.0, 0.9, 1.1);
        var model = Environment.GetEnvironmentVariable("SIMPLEX_RNNOISE_DIR");
        if (!string.IsNullOrEmpty(model) && File.Exists(Path.Combine(model, "sh.rnnn")))
        {
            var rnn = new FfmpegFilterEffect("RNNoise", "arnndn=m=sh.rnnn:mix=1", model);
            var r = await rnn.ProcessAsync(d, 1, 48000, null, default);
            Assert.True(r.Length > 0);
        }
    }

    [Fact]
    public async Task VideoExportWithTextImageAndAudio()
    {
        if (!HasFfmpeg) return;
        var dir = AppPaths.NewTempDir();
        var video = Path.Combine(dir, "in.mp4");
        var image = Path.Combine(dir, "logo.png");
        var r1 = await Ffmpeg.RunAsync(new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=25:d=3", "-f", "lavfi", "-i", "sine=f=440:d=3", "-shortest", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", video }, default);
        Assert.Equal(0, r1.ExitCode);
        var r2 = await Ffmpeg.RunAsync(new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=red:s=200x100", "-frames:v", "1", image }, default);
        Assert.Equal(0, r2.ExitCode);

        var p = VideoProject.CreateDefault();
        p.Width = 640; p.Height = 360; p.Fps = 25;
        async Task<MediaItem> Add(string path)
        {
            var i = await MediaProbe.ProbeAsync(path);
            var m = new MediaItem { Path = path, Name = Path.GetFileName(path), Kind = i.Kind, Duration = i.Duration, Width = i.Width, Height = i.Height, HasAudio = i.HasAudio, HasVideo = i.HasVideo, Fps = i.Fps };
            p.Media.Add(m);
            return m;
        }
        var v = await Add(video);
        var img = await Add(image);
        Assert.Equal(MediaKind.Video, v.Kind);
        Assert.Equal(MediaKind.Image, img.Kind);
        p.Tracks[2].Clips.Add(new Clip { MediaId = v.Id, Start = 0, Duration = 3, FadeIn = 0.5, Saturation = 1.2 });
        p.Tracks[1].Clips.Add(new Clip { MediaId = img.Id, Start = 0.5, Duration = 2, Scale = 0.3, Rotation = 10, Opacity = 0.8 });
        p.Tracks[0].Clips.Add(new Clip { Start = 0, Duration = 2.5, Text = "Ärger: 100% \"Test\"", Box = true, FadeIn = 0.3 });
        p.Tracks[3].Clips.Add(new Clip { MediaId = v.Id, Start = 1, Duration = 1.5, Volume = 0.5, Denoise = true });

        var frame = await RenderGraphBuilder.RenderFrameAsync(p, 1.0, 0.5, default);
        Assert.True(frame != null && File.Exists(frame));

        var outFile = Path.Combine(dir, "out.mp4");
        await RenderGraphBuilder.ExportAsync(p, new ExportSettings(), outFile, null, default);
        var info = await MediaProbe.ProbeAsync(outFile);
        Assert.True(info.HasVideo && info.HasAudio);
        Assert.Equal(640, info.Width);
        Assert.InRange(info.Duration, 2.8, 3.3);

        var gif = Path.Combine(dir, "out.gif");
        await RenderGraphBuilder.ExportAsync(p, new ExportSettings { Container = "gif", ScaleFactor = 0.5 }, gif, null, default);
        Assert.True(new FileInfo(gif).Length > 1000);

        var wav = await RenderGraphBuilder.RenderAudioAsync(p, 16000, 1, null, default);
        Assert.True(new FileInfo(wav).Length > 16000);
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public async Task WhisperRunsWithLocalModel()
    {
        var model = Environment.GetEnvironmentVariable("SIMPLEX_WHISPER_MODEL");
        if (string.IsNullOrEmpty(model) || !File.Exists(model)) return;
        var samples = Sine(16000, 3, 300, 0.2);
        var t = await WhisperTranscriber.TranscribeAsync(samples, new TranscriptionOptions { ModelPath = model, Language = "de", Threads = 2 }, null, default);
        Assert.NotNull(t);
    }
}
