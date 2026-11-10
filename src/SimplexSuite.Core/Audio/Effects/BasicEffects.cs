using SimplexSuite.Core.Dsp;

namespace SimplexSuite.Core.Audio.Effects;

public sealed class GainEffect(double gainDb) : SyncEffect
{
    public override string Name => $"Verstärkung {gainDb:+0.0;-0.0} dB";
    public override float[] Process(float[] d, int ch, int sr, IProgress<double>? p, CancellationToken ct)
    {
        var g = DbToLin(gainDb);
        var o = new float[d.Length];
        for (long i = 0; i < d.LongLength; i++) { o[i] = d[i] * g; Report(p, ct, i, d.LongLength); }
        return o;
    }
}

public sealed class NormalizeEffect(double peakDb, bool removeDc, bool independentChannels) : SyncEffect
{
    public override string Name => $"Normalisieren auf {peakDb:0.0} dBFS";
    public override float[] Process(float[] d, int ch, int sr, IProgress<double>? p, CancellationToken ct)
    {
        var o = (float[])d.Clone();
        long frames = o.LongLength / ch;
        if (removeDc)
        {
            for (int c = 0; c < ch; c++)
            {
                double sum = 0;
                for (long f = 0; f < frames; f++) sum += o[f * ch + c];
                float mean = (float)(sum / Math.Max(1, frames));
                for (long f = 0; f < frames; f++) o[f * ch + c] -= mean;
            }
        }
        var target = DbToLin(peakDb);
        var peaks = new float[ch];
        for (long i = 0; i < o.LongLength; i++) peaks[i % ch] = Math.Max(peaks[i % ch], Math.Abs(o[i]));
        float global = peaks.Max();
        for (long i = 0; i < o.LongLength; i++)
        {
            float pk = independentChannels ? peaks[i % ch] : global;
            if (pk > 1e-9f) o[i] *= target / pk;
            Report(p, ct, i, o.LongLength);
        }
        return o;
    }
}

public sealed class LoudnessEffect(double targetLufs, double ceilingDb) : SyncEffect
{
    public override string Name => $"Lautheit auf {targetLufs:0.0} LUFS";
    public override float[] Process(float[] d, int ch, int sr, IProgress<double>? p, CancellationToken ct)
    {
        var m = Loudness.Measure(d, ch, sr, ct);
        if (m.IntegratedLufs <= -69) return (float[])d.Clone();
        var gain = DbToLin(targetLufs - m.IntegratedLufs);
        var o = new float[d.Length];
        float peak = 0;
        for (long i = 0; i < d.LongLength; i++) { o[i] = d[i] * gain; peak = Math.Max(peak, Math.Abs(o[i])); }
        if (LinToDb(peak) > ceilingDb)
            o = new LimiterEffect(ceilingDb, 80, 5).Process(o, ch, sr, p, ct);
        return o;
    }
}

public enum FadeCurve { Linear, Logarithmic, Exponential, SCurve }

public sealed class FadeEffect(bool fadeIn, FadeCurve curve) : SyncEffect
{
    public override string Name => fadeIn ? "Einblenden" : "Ausblenden";
    public override float[] Process(float[] d, int ch, int sr, IProgress<double>? p, CancellationToken ct)
    {
        var o = new float[d.Length];
        long frames = d.LongLength / ch;
        for (long f = 0; f < frames; f++)
        {
            double t = frames <= 1 ? 1 : (double)f / (frames - 1);
            if (!fadeIn) t = 1 - t;
            double g = curve switch
            {
                FadeCurve.Logarithmic => Math.Sqrt(t),
                FadeCurve.Exponential => t * t * t,
                FadeCurve.SCurve => 0.5 - 0.5 * Math.Cos(Math.PI * t),
                _ => t,
            };
            for (int c = 0; c < ch; c++) o[f * ch + c] = (float)(d[f * ch + c] * g);
            Report(p, ct, f, frames);
        }
        return o;
    }
}

public enum UtilityMode { Invert, Reverse, SwapChannels, MonoMix, RemoveDc }

public sealed class UtilityEffect(UtilityMode mode) : SyncEffect
{
    public override string Name => mode switch
    {
        UtilityMode.Invert => "Invertieren",
        UtilityMode.Reverse => "Umkehren",
        UtilityMode.SwapChannels => "Kanäle tauschen",
        UtilityMode.MonoMix => "Zu Mono mischen",
        _ => "Gleichspannung entfernen",
    };

    public override float[] Process(float[] d, int ch, int sr, IProgress<double>? p, CancellationToken ct)
    {
        var o = new float[d.Length];
        long frames = d.LongLength / ch;
        switch (mode)
        {
            case UtilityMode.Invert:
                for (long i = 0; i < d.LongLength; i++) o[i] = -d[i];
                break;
            case UtilityMode.Reverse:
                for (long f = 0; f < frames; f++)
                    for (int c = 0; c < ch; c++) o[f * ch + c] = d[(frames - 1 - f) * ch + c];
                break;
            case UtilityMode.SwapChannels:
                for (long f = 0; f < frames; f++)
                    for (int c = 0; c < ch; c++) o[f * ch + c] = d[f * ch + (ch - 1 - c)];
                break;
            case UtilityMode.MonoMix:
                for (long f = 0; f < frames; f++)
                {
                    float s = 0;
                    for (int c = 0; c < ch; c++) s += d[f * ch + c];
                    s /= ch;
                    for (int c = 0; c < ch; c++) o[f * ch + c] = s;
                }
                break;
            default:
                for (int c = 0; c < ch; c++)
                {
                    var hp = new Biquad();
                    hp.SetHighPass(sr, 12, 0.707);
                    for (long f = 0; f < frames; f++) o[f * ch + c] = hp.Process(d[f * ch + c]);
                }
                break;
        }
        return o;
    }
}

public sealed class EqBand
{
    public FilterType Type { get; set; } = FilterType.Peak;
    public double Frequency { get; set; } = 1000;
    public double GainDb { get; set; }
    public double Q { get; set; } = 1.0;
    public bool Enabled { get; set; } = true;
}

public sealed class EqualizerEffect(IReadOnlyList<EqBand> bands, double outputGainDb = 0) : SyncEffect
{
    public override string Name => $"Equalizer ({bands.Count(b => b.Enabled)} Bänder)";
    public override float[] Process(float[] d, int ch, int sr, IProgress<double>? p, CancellationToken ct)
    {
        var active = bands.Where(b => b.Enabled).ToList();
        var o = new float[d.Length];
        var g = DbToLin(outputGainDb);
        long frames = d.LongLength / ch;
        for (int c = 0; c < ch; c++)
        {
            var filters = active.Select(b => { var f = new Biquad(); f.Set(b.Type, sr, b.Frequency, b.Q, b.GainDb); return f; }).ToArray();
            for (long f = 0; f < frames; f++)
            {
                float x = d[f * ch + c];
                foreach (var flt in filters) x = flt.Process(x);
                o[f * ch + c] = x * g;
                Report(p, ct, f + c * frames, frames * ch);
            }
        }
        return o;
    }

    public static double ResponseDb(IReadOnlyList<EqBand> bands, double freq, int sr)
    {
        double sum = 0;
        foreach (var b in bands.Where(b => b.Enabled))
        {
            var f = new Biquad();
            f.Set(b.Type, sr, b.Frequency, b.Q, b.GainDb);
            sum += f.MagnitudeDb(freq, sr);
        }
        return sum;
    }
}

public sealed class EchoEffect(double delayMs, double feedback, double mix) : SyncEffect
{
    public override string Name => "Echo";
    public override float[] Process(float[] d, int ch, int sr, IProgress<double>? p, CancellationToken ct)
    {
        int delay = Math.Max(1, (int)(delayMs / 1000 * sr));
        long frames = d.LongLength / ch;
        var o = new float[d.Length];
        for (int c = 0; c < ch; c++)
        {
            var buf = new float[delay];
            int pos = 0;
            for (long f = 0; f < frames; f++)
            {
                float x = d[f * ch + c];
                float delayed = buf[pos];
                buf[pos] = x + delayed * (float)feedback;
                pos = (pos + 1) % delay;
                o[f * ch + c] = x + delayed * (float)mix;
                Report(p, ct, f, frames);
            }
        }
        return o;
    }
}
