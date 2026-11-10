namespace SimplexSuite.Core.Dsp;

/// <summary>Iterative Radix-2-FFT (in place).</summary>
public static class Fft
{
    public static void Transform(double[] re, double[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                int half = len >> 1;
                for (int j = 0; j < half; j++)
                {
                    int a = i + j, b = a + half;
                    double ur = re[a], ui = im[a];
                    double vr = re[b] * cr - im[b] * ci;
                    double vi = re[b] * ci + im[b] * cr;
                    re[a] = ur + vr; im[a] = ui + vi;
                    re[b] = ur - vr; im[b] = ui - vi;
                    double nr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = nr;
                }
            }
        }
        if (inverse)
        {
            for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
        }
    }

    public static double[] Hann(int n)
    {
        var w = new double[n];
        for (int i = 0; i < n; i++) w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
        return w;
    }
}
