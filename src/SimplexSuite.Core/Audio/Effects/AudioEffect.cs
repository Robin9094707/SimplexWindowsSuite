namespace SimplexSuite.Core.Audio.Effects;

public interface IAudioEffect
{
    string Name { get; }
    Task<float[]> ProcessAsync(float[] data, int channels, int sampleRate, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>Basisklasse für synchrone DSP-Effekte (laufen im Hintergrundthread).</summary>
public abstract class SyncEffect : IAudioEffect
{
    public abstract string Name { get; }

    public Task<float[]> ProcessAsync(float[] data, int channels, int sampleRate, IProgress<double>? progress, CancellationToken ct)
        => Task.Run(() => Process(data, channels, sampleRate, progress, ct), ct);

    public abstract float[] Process(float[] data, int channels, int sampleRate, IProgress<double>? progress, CancellationToken ct);

    protected static float DbToLin(double db) => (float)Math.Pow(10, db / 20);
    protected static double LinToDb(double lin) => 20 * Math.Log10(Math.Max(1e-9, lin));

    protected static void Report(IProgress<double>? p, CancellationToken ct, long i, long total)
    {
        if ((i & 0xFFFF) == 0)
        {
            ct.ThrowIfCancellationRequested();
            p?.Report(total > 0 ? (double)i / total : 0);
        }
    }
}
