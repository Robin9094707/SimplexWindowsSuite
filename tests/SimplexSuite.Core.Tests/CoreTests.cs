using SimplexSuite.Core.Audio;
using SimplexSuite.Core.Audio.Effects;
using SimplexSuite.Core.Dsp;
using SimplexSuite.Core.Pdf;
using SimplexSuite.Core.Video;
using Xunit;

namespace SimplexSuite.Core.Tests;

public class AudioTests
{
    private static float[] Sine(int sr, double seconds, double freq, double amp, int ch = 1)
    {
        long n = (long)(sr * seconds);
        var d = new float[n * ch];
        for (long i = 0; i < n; i++)
            for (int c = 0; c < ch; c++) d[i * ch + c] = (float)(amp * Math.Sin(2 * Math.PI * freq * i / sr));
        return d;
    }

    [Fact]
    public void DeleteAndUndoRestoresSamples()
    {
        var doc = new AudioDocument(Sine(48000, 2, 440, 0.5), 1, 48000);
        var original = (float[])doc.Samples.Clone();
        doc.Delete(1000, 24000);
        Assert.Equal(original.Length - 24000, doc.Samples.Length);
        doc.Undo();
        Assert.Equal(original, doc.Samples);
        doc.Redo();
        Assert.Equal(original.Length - 24000, doc.Samples.Length);
    }

    [Fact]
    public void LoudnessOfFullScaleSineIsAbout3Lufs()
    {
        // 1 kHz Sinus mit 0 dBFS ≈ -3.01 LUFS (Mono)
        var m = Loudness.Measure(Sine(48000, 5, 1000, 1.0), 1, 48000);
        Assert.InRange(m.IntegratedLufs, -3.4, -2.6);
    }

    [Fact]
    public void LoudnessEffectHitsTarget()
    {
        var d = Sine(48000, 5, 1000, 0.05);
        var o = new LoudnessEffect(-16, -1).Process(d, 1, 48000, null, default);
        var m = Loudness.Measure(o, 1, 48000);
        Assert.InRange(m.IntegratedLufs, -16.6, -15.4);
    }

    [Fact]
    public void LimiterRespectsCeiling()
    {
        var d = Sine(48000, 1, 200, 1.5);
        var o = new LimiterEffect(-1, 50, 5).Process(d, 1, 48000, null, default);
        Assert.True(o.Max(Math.Abs) <= Math.Pow(10, -1 / 20.0) + 1e-4);
    }

    [Fact]
    public void TruncateSilenceShortensPauses()
    {
        var a = Sine(48000, 1, 300, 0.5);
        var silence = new float[48000 * 3];
        var d = a.Concat(silence).Concat(a).ToArray();
        var o = new TruncateSilenceEffect(-40, 500, 400).Process(d, 1, 48000, null, default);
        Assert.InRange(o.Length / 48000.0, 2.3, 2.6);
    }

    [Fact]
    public void SpectralNoiseReductionReducesNoise()
    {
        var rnd = new Random(1);
        var noise = Enumerable.Range(0, 48000 * 2).Select(_ => (float)(rnd.NextDouble() - 0.5) * 0.02f).ToArray();
        var profile = NoiseProfile.Learn(noise, 1, 48000);
        var o = new SpectralNoiseReduction(profile, 24, 6, 3, 100).Process(noise, 1, 48000, null, default);
        double rmsIn = Math.Sqrt(noise.Average(x => x * x)), rmsOut = Math.Sqrt(o.Average(x => x * x));
        Assert.True(rmsOut < rmsIn * 0.3, $"in {rmsIn} out {rmsOut}");
    }

    [Fact]
    public void TranscriptShiftsAfterDelete()
    {
        var t = new Transcript();
        t.Words.Add(new TranscriptWord { Start = 0, End = 0.5, Text = "Hallo" });
        t.Words.Add(new TranscriptWord { Start = 1, End = 1.3, Text = "äh" });
        t.Words.Add(new TranscriptWord { Start = 2, End = 2.5, Text = "Welt" });
        t.ApplyEdit(0.9, 0.5, 0);
        Assert.Equal(2, t.Words.Count);
        Assert.Equal(1.5, t.Words[1].Start, 3);
        t.MarkFillers(new[] { "welt" });
        Assert.True(t.Words[1].IsFiller);
    }
}

public class VideoTests
{
    [Fact]
    public void GraphContainsOverlayAndMix()
    {
        var p = VideoProject.CreateDefault();
        var path = Path.GetTempFileName();
        var m = new MediaItem { Path = path, Kind = Media.MediaKind.Video, Duration = 10, Width = 1280, Height = 720, HasAudio = true, HasVideo = true, Fps = 30 };
        p.Media.Add(m);
        p.Tracks[2].Clips.Add(new Clip { MediaId = m.Id, Start = 1, Duration = 4, FadeIn = 1 });
        p.Tracks[0].Clips.Add(new Clip { Start = 0, Duration = 2, Text = "Titel" });
        var g = RenderGraphBuilder.Build(p, new RenderRequest());
        Assert.Contains("overlay=", g.FilterComplex);
        Assert.Contains("amix=", g.FilterComplex);
        Assert.Contains("drawtext=", g.FilterComplex);
        Assert.Equal(5, g.Duration, 3);
        File.Delete(path);
    }

    [Fact]
    public void SplitKeepsSourceContinuity()
    {
        var p = VideoProject.CreateDefault();
        var c = new Clip { Start = 2, In = 1, Duration = 4, Speed = 2 };
        p.Tracks[2].Clips.Add(c);
        var right = p.Split(p.Tracks[2], c, 3)!;
        Assert.Equal(1, c.Duration, 6);
        Assert.Equal(3, right.In, 6);
        Assert.Equal(3, right.Duration, 6);
    }
}

public class PdfTests
{
    [Fact]
    public void SaveBlankAndAnnotatedPages()
    {
        var pages = new List<PdfPageItem>
        {
            new() { Kind = PdfPageKind.Blank, WidthPt = 595, HeightPt = 842 },
            new() { Kind = PdfPageKind.Blank, WidthPt = 842, HeightPt = 595, ExtraRotation = 90 },
        };
        pages[0].Annotations.Add(new PdfAnnotation { Kind = PdfAnnotationKind.Rectangle, X = 0.1, Y = 0.1, W = 0.2, H = 0.1 });
        pages[0].Annotations.Add(new PdfAnnotation { Kind = PdfAnnotationKind.Ink, Points = { new(0.1, 0.1), new(0.3, 0.4) } });
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        PdfExporter.Save(pages, file, new PdfSaveOptions());
        var read = PdfExporter.ReadPages(file);
        Assert.Equal(2, read.Count);
        Assert.Equal(90, read[1].SourceRotation);
        File.Delete(file);
    }
}
