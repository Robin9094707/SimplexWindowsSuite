using SimplexSuite.Core.Dsp;

namespace SimplexSuite.Core.Audio.Effects;

/// <summary>Rauschprofil: mittleres Betragsspektrum (Leistung in dB) je Frequenzbin.</summary>
public sealed class NoiseProfile
{
    public int FftSize { get; init; }
    public int SampleRate { get; init; }
    public required double[] BinDb { get; init; }

    public static NoiseProfile Learn(float[] d, int ch, int sr, int fftSize = 2048)
    {
        var mags = SpectralNoiseReduction.Frames(d, ch, fftSize, fftSize / 2, int.MaxValue);
        int bins = fftSize / 2 + 1;
        var avg = new double[bins];
        if (mags.Count == 0) mags.Add(new double[bins]);
        foreach (var m in mags)
            for (int k = 0; k < bins; k++) avg[k] += m[k] * m[k];
        for (int k = 0; k < bins; k++) avg[k] = 10 * Math.Log10(avg[k] / mags.Count + 1e-20);
        return new NoiseProfile { FftSize = fftSize, SampleRate = sr, BinDb = avg };
    }
}

/// <summary>
/// Spektrale Rauschunterdrückung (STFT, 75 % Überlappung) mit Profil aus einer Auswahl
/// oder automatisch geschätztem Rauschboden. Glättung über Frequenz und Zeit verhindert "Musical Noise".
/// </summary>
public sealed class SpectralNoiseReduction(NoiseProfile? profile, double reductionDb, double sensitivityDb, int smoothingBands, double releaseMs) : SyncEffect
{
    public const int DefaultFft = 2048;
    public override string Name => profile != null ? "Rauschunterdrückung (Profil)" : "Rauschunterdrückung (automatisch)";

    internal static List<double[]> Frames(float[] d, int ch, int n, int hop, int maxFrames)
    {
        long frames = d.LongLength / ch;
        var win = Fft.Hann(n);
        var list = new List<double[]>();
        long count = Math.Max(0, (frames - n) / hop + 1);
        long stride = count > maxFrames ? count / maxFrames : 1;
        var re = new double[n];
        var im = new double[n];
        for (long fi = 0; fi < count; fi += stride)
        {
            long start = fi * hop;
            for (int i = 0; i < n; i++)
            {
                double s = 0;
                for (int c = 0; c < ch; c++) s += d[(start + i) * ch + c];
                re[i] = s / ch * win[i];
                im[i] = 0;
            }
            Fft.Transform(re, im, false);
            var mag = new double[n / 2 + 1];
            for (int k = 0; k <= n / 2; k++) mag[k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            list.Add(mag);
        }
        return list;
    }

    public override float[] Process(float[] d, int ch, int sr, IProgress<double>? p, CancellationToken ct)
    {
        int n = profile?.FftSize ?? DefaultFft;
        int hop = n / 4;
        int bins = n / 2 + 1;
        long frames = d.LongLength / ch;
        double[] noiseDb;
        if (profile != null && profile.BinDb.Length == bins) noiseDb = profile.BinDb;
        else
        {
            // Automatisch: 10%-Perzentil je Bin über (bis zu) 3000 Frames
            var mags = Frames(d, ch, n, hop, 3000);
            noiseDb = new double[bins];
            if (mags.Count > 0)
            {
                var col = new double[mags.Count];
                for (int k = 0; k < bins; k++)
                {
                    for (int i = 0; i < mags.Count; i++) col[i] = mags[i][k];
                    Array.Sort(col);
                    double v = col[(int)(col.Length * 0.10)];
                    noiseDb[k] = 20 * Math.Log10(v + 1e-10) + 3;
                }
            }
        }
        var win = Fft.Hann(n);
        double floor = Math.Pow(10, -Math.Abs(reductionDb) / 20);
        double rel = Math.Exp(-hop / (Math.Max(1, releaseMs) * 0.001 * sr));
        var o = new float[d.Length];
        // Normierung für Hann² bei 75 % Überlappung
        double norm = 0;
        for (int i = 0; i < n; i += hop) norm += win[i] * win[i];
        norm = 1.0 / Math.Max(1e-9, norm * 1.0);
        // exakte Normierung: Summe der quadrierten Fenster über alle Verschiebungen
        double sumSq = 0;
        for (int i = 0; i < n; i++) sumSq += win[i] * win[i];
        norm = hop / sumSq;

        long total = frames + n;
        for (int c = 0; c < ch; c++)
        {
            var re = new double[n];
            var im = new double[n];
            var gainsPrev = new double[bins];
            Array.Fill(gainsPrev, 1.0);
            var g = new double[bins];
            var gs = new double[bins];
            var acc = new double[frames + 2 * n];
            for (long start = -n; start < frames; start += hop)
            {
                for (int i = 0; i < n; i++)
                {
                    long idx = start + i;
                    re[i] = idx >= 0 && idx < frames ? d[idx * ch + c] * win[i] : 0;
                    im[i] = 0;
                }
                Fft.Transform(re, im, false);
                for (int k = 0; k < bins; k++)
                {
                    double mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                    double magDb = 20 * Math.Log10(mag + 1e-10);
                    double over = magDb - (noiseDb[k] + sensitivityDb);
                    // weicher Übergang über 6 dB oberhalb der Schwelle
                    double t = Math.Clamp(over / 6, 0, 1);
                    g[k] = floor + (1 - floor) * t;
                }
                // Frequenzglättung
                int sb = Math.Max(0, smoothingBands);
                for (int k = 0; k < bins; k++)
                {
                    double s = 0; int cnt = 0;
                    for (int j = Math.Max(0, k - sb); j <= Math.Min(bins - 1, k + sb); j++) { s += g[j]; cnt++; }
                    gs[k] = s / cnt;
                    // Zeitglättung: sofort öffnen, langsam schließen
                    gs[k] = Math.Max(gs[k], gainsPrev[k] * rel + floor * (1 - rel));
                    gainsPrev[k] = gs[k];
                }
                for (int k = 0; k < bins; k++)
                {
                    re[k] *= gs[k]; im[k] *= gs[k];
                    if (k > 0 && k < n / 2) { re[n - k] *= gs[k]; im[n - k] *= gs[k]; }
                }
                Fft.Transform(re, im, true);
                for (int i = 0; i < n; i++)
                {
                    long idx = start + i + n;
                    if (idx >= 0 && idx < acc.LongLength) acc[idx] += re[i] * win[i] * norm;
                }
                if (((start / hop) & 255) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    p?.Report(((double)c * total + start + n) / (total * (double)ch));
                }
            }
            for (long f = 0; f < frames; f++) o[f * ch + c] = (float)acc[f + n];
        }
        return o;
    }
}

/// <summary>Freeverb-Hall (Jezar), Parameter frei einstellbar.</summary>
public sealed class ReverbEffect(double roomSize, double damping, double wet, double dry, double width, double preDelayMs) : SyncEffect
{
    public override string Name => "Hall";

    private sealed class Comb(int size)
    {
        private readonly float[] _buf = new float[size];
        private int _i;
        private float _store;
        public float Feedback, Damp1, Damp2;
        public float Process(float x)
        {
            float o = _buf[_i];
            _store = o * Damp2 + _store * Damp1;
            _buf[_i] = x + _store * Feedback;
            if (++_i >= _buf.Length) _i = 0;
            return o;
        }
    }

    private sealed class AllPass(int size)
    {
        private readonly float[] _buf = new float[size];
        private int _i;
        public float Process(float x)
        {
            float b = _buf[_i];
            float o = -x + b;
            _buf[_i] = x + b * 0.5f;
            if (++_i >= _buf.Length) _i = 0;
            return o;
        }
    }

    private static readonly int[] CombT = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
    private static readonly int[] AllT = { 556, 441, 341, 225 };

    public override float[] Process(float[] d, int ch, int sr, IProgress<double>? p, CancellationToken ct)
    {
        double scale = sr / 44100.0;
        const int spread = 23;
        float fb = (float)(roomSize * 0.28 + 0.7);
        float d1 = (float)(damping * 0.4), d2 = 1 - d1;
        Comb[] MakeCombs(int off) => CombT.Select(t => new Comb((int)((t + off) * scale)) { Feedback = fb, Damp1 = d1, Damp2 = d2 }).ToArray();
        AllPass[] MakeAll(int off) => AllT.Select(t => new AllPass((int)((t + off) * scale))).ToArray();
        var cl = MakeCombs(0); var cr = MakeCombs(spread);
        var al = MakeAll(0); var ar = MakeAll(spread);
        float w = (float)wet * 3f, dr = (float)dry * 2f;
        float wet1 = w * ((float)width / 2 + 0.5f), wet2 = w * ((1 - (float)width) / 2);
        long frames = d.LongLength / ch;
        int pre = (int)(preDelayMs * 0.001 * sr);
        var preBuf = new float[Math.Max(1, pre)];
        int preIdx = 0;
        // Ausklingen des Halls anhängen (max 3 s)
        long tail = (long)(Math.Min(3.0, 0.5 + roomSize * 2.5) * sr);
        var o = new float[(frames + tail) * ch];
        for (long f = 0; f < frames + tail; f++)
        {
            float inL = f < frames ? d[f * ch] : 0;
            float inR = f < frames ? d[f * ch + (ch > 1 ? 1 : 0)] : 0;
            float input = (inL + inR) * 0.015f;
            if (pre > 0)
            {
                float delayed = preBuf[preIdx];
                preBuf[preIdx] = input;
                preIdx = (preIdx + 1) % pre;
                input = delayed;
            }
            float outL = 0, outR = 0;
            for (int i = 0; i < 8; i++) { outL += cl[i].Process(input); outR += cr[i].Process(input); }
            for (int i = 0; i < 4; i++) { outL = al[i].Process(outL); outR = ar[i].Process(outR); }
            float l = outL * wet1 + outR * wet2 + inL * dr * 0.5f;
            float r = outR * wet1 + outL * wet2 + inR * dr * 0.5f;
            if (ch == 1) o[f] = (l + r) * 0.5f;
            else { o[f * ch] = l; o[f * ch + 1] = r; }
            Report(p, ct, f, frames + tail);
        }
        return o;
    }
}

/// <summary>Beliebiger FFmpeg-Audiofilter – z. B. RNNoise-KI (arnndn), afftdn, Tempo, Tonhöhe.</summary>
public sealed class FfmpegFilterEffect(string name, string filter, string? workDir = null) : IAudioEffect
{
    public string Name => name;
    public string Filter => filter;

    public Task<float[]> ProcessAsync(float[] data, int channels, int sampleRate, IProgress<double>? progress, CancellationToken ct)
        => AudioIO.ProcessWithFilterAsync(data, sampleRate, channels, filter, ct, workDir);

    public static FfmpegFilterEffect RnNoise(double mix)
    {
        var dir = Path.GetDirectoryName(Media.Ffmpeg.DataFile("sh.rnnn"))!;
        return new FfmpegFilterEffect("KI-Rauschunterdrückung (RNNoise)",
            $"arnndn=m=sh.rnnn:mix={Common.TimeFormat.Inv(Math.Clamp(mix, -1, 1))}", dir);
    }

    public static FfmpegFilterEffect FftDenoise(double reductionDb, double noiseFloorDb, bool trackNoise) =>
        new("FFT-Entrauschen", $"afftdn=nr={Common.TimeFormat.Inv(Math.Clamp(reductionDb, 0.01, 97))}:nf={Common.TimeFormat.Inv(Math.Clamp(noiseFloorDb, -80, -20))}:tn={(trackNoise ? 1 : 0)}");

    public static FfmpegFilterEffect Tempo(double factor)
    {
        var chain = new List<string>();
        double f = factor;
        while (f > 2.0) { chain.Add("atempo=2.0"); f /= 2.0; }
        while (f < 0.5) { chain.Add("atempo=0.5"); f /= 0.5; }
        chain.Add("atempo=" + Common.TimeFormat.Inv(f));
        return new FfmpegFilterEffect($"Tempo ×{factor:0.##}", string.Join(",", chain));
    }

    public static FfmpegFilterEffect Pitch(double semitones, int sampleRate)
    {
        double k = Math.Pow(2, semitones / 12.0);
        var tempo = Tempo(1 / k).Filter;
        return new FfmpegFilterEffect($"Tonhöhe {semitones:+0.#;-0.#} HT",
            $"asetrate={Common.TimeFormat.Inv(sampleRate * k)},aresample={sampleRate},{tempo}");
    }
}
