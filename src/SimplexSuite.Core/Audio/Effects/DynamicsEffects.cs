using SimplexSuite.Core.Dsp;

namespace SimplexSuite.Core.Audio.Effects;

public sealed class CompressorEffect(double thresholdDb, double ratio, double attackMs, double releaseMs, double kneeDb, double makeupDb, bool autoMakeup) : SyncEffect
{
    public override string Name => $"Kompressor {ratio:0.#}:1";

    public override float[] Process(float[] d, int ch, int sr, IProgress<double>? p, CancellationToken ct)
    {
        long frames = d.LongLength / ch;
        var o = new float[d.Length];
        double aA = Math.Exp(-1.0 / (Math.Max(0.05, attackMs) * 0.001 * sr));
        double aR = Math.Exp(-1.0 / (Math.Max(1, releaseMs) * 0.001 * sr));
        double g = 0; // dB (≤ 0)
        double makeup = makeupDb;
        if (autoMakeup) makeup += -Gain(0) * 0.5;
        for (long f = 0; f < frames; f++)
        {
            float peak = 0;
            for (int c = 0; c < ch; c++) peak = Math.Max(peak, Math.Abs(d[f * ch + c]));
            double lvl = LinToDb(peak);
            double target = Gain(lvl);
            g = target < g ? aA * g + (1 - aA) * target : aR * g + (1 - aR) * target;
            float lin = DbToLin(g + makeup);
            for (int c = 0; c < ch; c++) o[f * ch + c] = d[f * ch + c] * lin;
            Report(p, ct, f, frames);
        }
        return o;
    }

    private double Gain(double lvl)
    {
        double over = lvl - thresholdDb;
        double slope = 1.0 / Math.Max(1, ratio) - 1;
        if (kneeDb > 0 && 2 * Math.Abs(over) <= kneeDb)
            return slope * Math.Pow(over + kneeDb / 2, 2) / (2 * kneeDb);
        return over > 0 ? slope * over : 0;
    }
}

public sealed class LimiterEffect(double ceilingDb, double releaseMs, double lookaheadMs) : SyncEffect
{
    public override string Name => $"Limiter {ceilingDb:0.0} dB";

    public override float[] Process(float[] d, int ch, int sr, IProgress<double>? p, CancellationToken ct)
    {
        long frames = d.LongLength / ch;
        float ceiling = DbToLin(ceilingDb);
        var gain = new float[frames];
        for (long f = 0; f < frames; f++)
        {
            float peak = 0;
            for (int c = 0; c < ch; c++) peak = Math.Max(peak, Math.Abs(d[f * ch + c]));
            gain[f] = peak > ceiling ? ceiling / peak : 1f;
        }
        int la = Math.Max(1, (int)(lookaheadMs * 0.001 * sr));
        float step = 1f / la;
        for (long f = frames - 2; f >= 0; f--) gain[f] = Math.Min(gain[f], gain[f + 1] + step);
        double rel = Math.Exp(-1.0 / (Math.Max(1, releaseMs) * 0.001 * sr));
        double g = 1;
        var o = new float[d.Length];
        for (long f = 0; f < frames; f++)
        {
            g = Math.Min(gain[f], 1 - (1 - g) * rel);
            for (int c = 0; c < ch; c++) o[f * ch + c] = Math.Clamp(d[f * ch + c] * (float)g, -ceiling, ceiling);
            Report(p, ct, f, frames);
        }
        return o;
    }
}

public sealed class NoiseGateEffect(double thresholdDb, double reductionDb, double attackMs, double holdMs, double releaseMs) : SyncEffect
{
    public override string Name => "Noise Gate";

    public override float[] Process(float[] d, int ch, int sr, IProgress<double>? p, CancellationToken ct)
    {
        long frames = d.LongLength / ch;
        var o = new float[d.Length];
        float floor = DbToLin(-Math.Abs(reductionDb));
        double env = 0;
        double envRel = Math.Exp(-1.0 / (0.010 * sr));
        double aA = Math.Exp(-1.0 / (Math.Max(0.1, attackMs) * 0.001 * sr));
        double aR = Math.Exp(-1.0 / (Math.Max(1, releaseMs) * 0.001 * sr));
        long holdFrames = (long)(holdMs * 0.001 * sr);
        long holdLeft = 0;
        double g = floor;
        double thr = DbToLin(thresholdDb);
        for (long f = 0; f < frames; f++)
        {
            float peak = 0;
            for (int c = 0; c < ch; c++) peak = Math.Max(peak, Math.Abs(d[f * ch + c]));
            env = peak > env ? peak : env * envRel;
            double target;
            if (env >= thr) { target = 1; holdLeft = holdFrames; }
            else if (holdLeft > 0) { target = 1; holdLeft--; }
            else target = floor;
            g = target > g ? aA * g + (1 - aA) * target : aR * g + (1 - aR) * target;
            for (int c = 0; c < ch; c++) o[f * ch + c] = d[f * ch + c] * (float)g;
            Report(p, ct, f, frames);
        }
        return o;
    }
}

public sealed class DeEsserEffect(double frequency, double thresholdDb, double maxReductionDb) : SyncEffect
{
    public override string Name => "De-Esser";

    public override float[] Process(float[] d, int ch, int sr, IProgress<double>? p, CancellationToken ct)
    {
        long frames = d.LongLength / ch;
        var o = new float[d.Length];
        var detector = new Biquad();
        detector.Set(FilterType.BandPass, sr, frequency * 1.3, 0.8, 0);
        // Einpoliger Hochpass für komplementäre Bandaufteilung
        double rc = 1.0 / (2 * Math.PI * frequency);
        double dt = 1.0 / sr;
        double alpha = rc / (rc + dt);
        var prevX = new double[ch];
        var prevY = new double[ch];
        double env = 0;
        double att = Math.Exp(-1.0 / (0.0015 * sr)), rel = Math.Exp(-1.0 / (0.060 * sr));
        for (long f = 0; f < frames; f++)
        {
            float mono = 0;
            for (int c = 0; c < ch; c++) mono += d[f * ch + c];
            mono /= ch;
            double s = Math.Abs(detector.Process(mono));
            env = s > env ? att * env + (1 - att) * s : rel * env + (1 - rel) * s;
            double envDb = LinToDb(env);
            double red = envDb > thresholdDb ? Math.Min(maxReductionDb, (envDb - thresholdDb) * 0.75) : 0;
            float gHigh = DbToLin(-red);
            for (int c = 0; c < ch; c++)
            {
                double x = d[f * ch + c];
                double high = alpha * (prevY[c] + x - prevX[c]);
                prevX[c] = x; prevY[c] = high;
                double low = x - high;
                o[f * ch + c] = (float)(low + high * gHigh);
            }
            Report(p, ct, f, frames);
        }
        return o;
    }
}

/// <summary>Kürzt Pausen (Stille) auf eine Wunschlänge – ideal für Podcasts.</summary>
public sealed class TruncateSilenceEffect(double thresholdDb, double minSilenceMs, double keepMs) : SyncEffect
{
    public override string Name => "Pausen kürzen";

    public static List<(long Start, long End)> Detect(float[] d, int ch, int sr, double thresholdDb, double minSilenceMs)
    {
        long frames = d.LongLength / ch;
        int win = Math.Max(1, sr / 100); // 10 ms
        double thr = Math.Pow(10, thresholdDb / 20);
        var result = new List<(long, long)>();
        long runStart = -1;
        long minLen = (long)(minSilenceMs * 0.001 * sr);
        for (long w = 0; w * win < frames; w++)
        {
            long s = w * win, e = Math.Min(frames, s + win);
            double sum = 0;
            for (long f = s; f < e; f++)
                for (int c = 0; c < ch; c++) { double x = d[f * ch + c]; sum += x * x; }
            double rms = Math.Sqrt(sum / ((e - s) * ch));
            if (rms < thr)
            {
                if (runStart < 0) runStart = s;
            }
            else if (runStart >= 0)
            {
                if (s - runStart >= minLen) result.Add((runStart, s));
                runStart = -1;
            }
        }
        if (runStart >= 0 && frames - runStart >= minLen) result.Add((runStart, frames));
        return result;
    }

    public override float[] Process(float[] d, int ch, int sr, IProgress<double>? p, CancellationToken ct)
    {
        var runs = Detect(d, ch, sr, thresholdDb, minSilenceMs);
        long frames = d.LongLength / ch;
        long keep = (long)(keepMs * 0.001 * sr);
        var output = new List<float>(d.Length);
        long cursor = 0;
        int xf = Math.Max(1, sr / 200);
        foreach (var (s, e) in runs)
        {
            long len = e - s;
            if (len <= keep) continue;
            long cutStart = s + keep / 2;
            long cutEnd = e - (keep - keep / 2);
            for (long f = cursor; f < cutStart; f++)
                for (int c = 0; c < ch; c++) output.Add(d[f * ch + c]);
            // Kurze Überblendung über die Schnittstelle
            int n = (int)Math.Min(xf, Math.Min(cutStart - cursor, frames - cutEnd));
            int baseIdx = output.Count - n * ch;
            for (int i = 0; i < n; i++)
            {
                float t = (float)(i + 1) / (n + 1);
                for (int c = 0; c < ch; c++)
                {
                    int idx = baseIdx + i * ch + c;
                    output[idx] = output[idx] * (1 - t) + d[(cutEnd + i) * ch + c] * t;
                }
            }
            cursor = cutEnd + n;
            ct.ThrowIfCancellationRequested();
            p?.Report((double)cursor / frames);
        }
        for (long f = cursor; f < frames; f++)
            for (int c = 0; c < ch; c++) output.Add(d[f * ch + c]);
        return output.ToArray();
    }
}
