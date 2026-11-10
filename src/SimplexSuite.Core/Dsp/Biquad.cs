namespace SimplexSuite.Core.Dsp;

public enum FilterType { LowCut, HighCut, Peak, LowShelf, HighShelf, Notch, BandPass }

/// <summary>Biquad-Filter nach RBJ-Audio-EQ-Cookbook (Direktform I, double-genau).</summary>
public sealed class Biquad
{
    private double _b0, _b1, _b2, _a1, _a2;
    private double _x1, _x2, _y1, _y2;

    public void Reset() { _x1 = _x2 = _y1 = _y2 = 0; }

    public void Set(FilterType type, double sampleRate, double freq, double q, double gainDb)
    {
        freq = Math.Clamp(freq, 5, sampleRate * 0.49);
        q = Math.Max(0.05, q);
        double a = Math.Pow(10, gainDb / 40);
        double w0 = 2 * Math.PI * freq / sampleRate;
        double cos = Math.Cos(w0), sin = Math.Sin(w0);
        double alpha = sin / (2 * q);
        double b0, b1, b2, a0, a1, a2;
        switch (type)
        {
            case FilterType.LowCut:
                b0 = (1 + cos) / 2; b1 = -(1 + cos); b2 = (1 + cos) / 2;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha; break;
            case FilterType.HighCut:
                b0 = (1 - cos) / 2; b1 = 1 - cos; b2 = (1 - cos) / 2;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha; break;
            case FilterType.Peak:
                b0 = 1 + alpha * a; b1 = -2 * cos; b2 = 1 - alpha * a;
                a0 = 1 + alpha / a; a1 = -2 * cos; a2 = 1 - alpha / a; break;
            case FilterType.LowShelf:
            {
                double sq = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) - (a - 1) * cos + sq);
                b1 = 2 * a * ((a - 1) - (a + 1) * cos);
                b2 = a * ((a + 1) - (a - 1) * cos - sq);
                a0 = (a + 1) + (a - 1) * cos + sq;
                a1 = -2 * ((a - 1) + (a + 1) * cos);
                a2 = (a + 1) + (a - 1) * cos - sq; break;
            }
            case FilterType.HighShelf:
            {
                double sq = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) + (a - 1) * cos + sq);
                b1 = -2 * a * ((a - 1) + (a + 1) * cos);
                b2 = a * ((a + 1) + (a - 1) * cos - sq);
                a0 = (a + 1) - (a - 1) * cos + sq;
                a1 = 2 * ((a - 1) - (a + 1) * cos);
                a2 = (a + 1) - (a - 1) * cos - sq; break;
            }
            case FilterType.Notch:
                b0 = 1; b1 = -2 * cos; b2 = 1;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha; break;
            default: // BandPass (0 dB Spitze)
                b0 = alpha; b1 = 0; b2 = -alpha;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha; break;
        }
        _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0;
    }

    public void SetLowPass(double sr, double f, double q) => Set(FilterType.HighCut, sr, f, q, 0);
    public void SetHighPass(double sr, double f, double q) => Set(FilterType.LowCut, sr, f, q, 0);

    public void SetRaw(double b0, double b1, double b2, double a1, double a2)
    {
        _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2;
    }

    public float Process(float x)
    {
        double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1; _x1 = x;
        _y2 = _y1; _y1 = y;
        return (float)y;
    }

    /// <summary>Betragsfrequenzgang in dB (für die EQ-Kurve).</summary>
    public double MagnitudeDb(double freq, double sampleRate)
    {
        double w = 2 * Math.PI * freq / sampleRate;
        var z1 = System.Numerics.Complex.FromPolarCoordinates(1, -w);
        var z2 = z1 * z1;
        var num = _b0 + _b1 * z1 + _b2 * z2;
        var den = 1 + _a1 * z1 + _a2 * z2;
        return 20 * Math.Log10(Math.Max(1e-12, (num / den).Magnitude));
    }
}
