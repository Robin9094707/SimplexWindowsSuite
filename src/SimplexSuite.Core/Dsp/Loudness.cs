namespace SimplexSuite.Core.Dsp;

public sealed record LoudnessResult(double IntegratedLufs, double PeakDb, double RmsDb, double LoudnessRange, double ShortTermMax);

/// <summary>Lautheitsmessung nach ITU-R BS.1770-4 / EBU R128.</summary>
public static class Loudness
{
    public static LoudnessResult Measure(float[] data, int channels, int sampleRate, CancellationToken ct = default)
    {
        long frames = data.Length / channels;
        var shelf = new Biquad[channels];
        var hp = new Biquad[channels];
        for (int c = 0; c < channels; c++)
        {
            shelf[c] = new Biquad();
            hp[c] = new Biquad();
            KWeighting(sampleRate, shelf[c], hp[c]);
        }
        int block = (int)(0.4 * sampleRate);
        int step = (int)(0.1 * sampleRate);
        int stBlock = 3 * sampleRate;
        // Energie pro 100-ms-Schritt
        int steps = (int)(frames / step);
        var stepEnergy = new double[steps];
        double peak = 0, sumSq = 0;
        for (int s = 0; s < steps; s++)
        {
            if ((s & 63) == 0) ct.ThrowIfCancellationRequested();
            double e = 0;
            long baseIdx = (long)s * step;
            for (int i = 0; i < step; i++)
            {
                long f = baseIdx + i;
                for (int c = 0; c < channels; c++)
                {
                    float x = data[f * channels + c];
                    double ax = Math.Abs(x);
                    if (ax > peak) peak = ax;
                    sumSq += (double)x * x;
                    float y = hp[c].Process(shelf[c].Process(x));
                    e += (double)y * y;
                }
            }
            stepEnergy[s] = e;
        }
        int perBlock = block / step; // 4
        var blocks = new List<double>();
        for (int s = 0; s + perBlock <= steps; s++)
        {
            double e = 0;
            for (int k = 0; k < perBlock; k++) e += stepEnergy[s + k];
            blocks.Add(e / (perBlock * step));
        }
        static double ToLufs(double ms) => -0.691 + 10 * Math.Log10(Math.Max(1e-12, ms));
        var abs = blocks.Where(b => ToLufs(b) > -70).ToList();
        double integrated = -70;
        if (abs.Count > 0)
        {
            double rel = ToLufs(abs.Average()) - 10;
            var gated = abs.Where(b => ToLufs(b) > rel).ToList();
            if (gated.Count > 0) integrated = ToLufs(gated.Average());
        }
        // Kurzzeit-Lautheit + LRA
        int perSt = stBlock / step;
        var st = new List<double>();
        for (int s = 0; s + perSt <= steps; s += 10)
        {
            double e = 0;
            for (int k = 0; k < perSt; k++) e += stepEnergy[s + k];
            st.Add(ToLufs(e / (perSt * step)));
        }
        double lra = 0, stMax = st.Count > 0 ? st.Max() : integrated;
        var stAbs = st.Where(v => v > -70).ToList();
        if (stAbs.Count > 2)
        {
            double relGate = 10 * Math.Log10(stAbs.Select(v => Math.Pow(10, v / 10)).Average()) - 20;
            var g = stAbs.Where(v => v > relGate).OrderBy(v => v).ToList();
            if (g.Count > 2)
                lra = g[(int)(g.Count * 0.95) - 1] - g[(int)(g.Count * 0.10)];
        }
        double rms = data.Length > 0 ? Math.Sqrt(sumSq / data.Length) : 0;
        return new LoudnessResult(integrated, 20 * Math.Log10(Math.Max(1e-9, peak)), 20 * Math.Log10(Math.Max(1e-9, rms)), lra, stMax);
    }

    /// <summary>K-Filter (Pre-Filter + RLB) für beliebige Abtastraten.</summary>
    public static void KWeighting(double fs, Biquad shelf, Biquad hp)
    {
        // Koeffizienten-Herleitung wie in libebur128
        double f0 = 1681.974450955533, g = 3.999843853973347, q = 0.7071752369554196;
        double k = Math.Tan(Math.PI * f0 / fs);
        double vh = Math.Pow(10.0, g / 20.0);
        double vb = Math.Pow(vh, 0.4996667741545416);
        double a0 = 1.0 + k / q + k * k;
        shelf.SetRaw((vh + vb * k / q + k * k) / a0, 2.0 * (k * k - vh) / a0, (vh - vb * k / q + k * k) / a0,
            2.0 * (k * k - 1.0) / a0, (1.0 - k / q + k * k) / a0);
        f0 = 38.13547087602444; q = 0.5003270373238773;
        k = Math.Tan(Math.PI * f0 / fs);
        double a0b = 1.0 + k / q + k * k;
        hp.SetRaw(1.0, -2.0, 1.0, 2.0 * (k * k - 1.0) / a0b, (1.0 - k / q + k * k) / a0b);
    }
}
