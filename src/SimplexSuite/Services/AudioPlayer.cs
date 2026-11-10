using NAudio.Wave;

namespace SimplexSuite.Services;

/// <summary>Wiedergabe von Float-Puffern über WaveOut (niedrige Latenz, Positionsabfrage).</summary>
public sealed class AudioPlayer : IDisposable
{
    private sealed class BufferProvider : ISampleProvider
    {
        private readonly float[] _data;
        private readonly int _channels;
        public long Position;   // Frames
        public long End;
        public float Volume = 1;
        public WaveFormat WaveFormat { get; }

        public BufferProvider(float[] data, int channels, int sampleRate, long start, long end)
        {
            _data = data;
            _channels = channels;
            Position = start;
            End = end;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            long available = Math.Max(0, (End - Position) * _channels);
            int n = (int)Math.Min(count - count % _channels, available);
            if (n <= 0) return 0;
            Array.Copy(_data, Position * _channels, buffer, offset, n);
            if (Math.Abs(Volume - 1) > 1e-4)
                for (int i = 0; i < n; i++) buffer[offset + i] *= Volume;
            Position += n / _channels;
            return n;
        }
    }

    private WaveOutEvent? _out;
    private BufferProvider? _provider;
    private int _sampleRate = 48000;
    private DateTime _startedAt;
    private long _startFrame;
    private float _volume = 1;

    public event EventHandler? PlaybackStopped;
    public bool IsPlaying => _out?.PlaybackState == PlaybackState.Playing;

    public float Volume
    {
        get => _volume;
        set { _volume = value; if (_provider != null) _provider.Volume = value; }
    }

    /// <summary>Aktuelle Wiedergabeposition in Sekunden (inkl. Latenzausgleich).</summary>
    public double PositionSeconds
    {
        get
        {
            if (_out == null || _provider == null) return 0;
            try
            {
                // GetPosition liefert die tatsächlich ausgegebenen Bytes
                long bytes = _out.GetPosition();
                long frames = bytes / _provider.WaveFormat.BlockAlign;
                return (double)(_startFrame + frames) / _sampleRate;
            }
            catch
            {
                return (_startFrame + (DateTime.Now - _startedAt).TotalSeconds * _sampleRate) / _sampleRate;
            }
        }
    }

    public void Play(float[] data, int channels, int sampleRate, long startFrame, long endFrame = -1)
    {
        Stop();
        long frames = data.LongLength / channels;
        if (endFrame < 0 || endFrame > frames) endFrame = frames;
        if (startFrame >= endFrame) return;
        _sampleRate = sampleRate;
        _startFrame = startFrame;
        _provider = new BufferProvider(data, channels, sampleRate, startFrame, endFrame) { Volume = _volume };
        _out = new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 3 };
        _out.Init(_provider);
        var current = _out;
        _out.PlaybackStopped += (_, _) =>
        {
            if (ReferenceEquals(current, _out)) PlaybackStopped?.Invoke(this, EventArgs.Empty);
        };
        _startedAt = DateTime.Now;
        _out.Play();
    }

    public void Stop()
    {
        var o = _out;
        _out = null;
        _provider = null;
        if (o != null)
        {
            try { o.Stop(); } catch { }
            o.Dispose();
            PlaybackStopped?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose() => Stop();
}

/// <summary>Aufnahme vom Mikrofon in Float-Samples.</summary>
public sealed class AudioRecorder : IDisposable
{
    private WaveInEvent? _in;
    private readonly List<float> _samples = new();
    public int SampleRate { get; }
    public int Channels { get; }
    public event Action<float>? Level;
    public double Seconds => (double)_samples.Count / Channels / SampleRate;

    public AudioRecorder(int sampleRate, int channels)
    {
        SampleRate = sampleRate;
        Channels = channels;
    }

    public static IReadOnlyList<string> Devices()
    {
        var list = new List<string>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            try { list.Add(WaveInEvent.GetCapabilities(i).ProductName); } catch { list.Add("Gerät " + (i + 1)); }
        }
        return list;
    }

    public void Start(int device)
    {
        _samples.Clear();
        _in = new WaveInEvent { DeviceNumber = device, WaveFormat = new WaveFormat(SampleRate, 16, Channels), BufferMilliseconds = 50 };
        _in.DataAvailable += (_, e) =>
        {
            float peak = 0;
            lock (_samples)
            {
                for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                {
                    float v = BitConverter.ToInt16(e.Buffer, i) / 32768f;
                    _samples.Add(v);
                    peak = Math.Max(peak, Math.Abs(v));
                }
            }
            Level?.Invoke(peak);
        };
        _in.StartRecording();
    }

    public float[] Stop()
    {
        try { _in?.StopRecording(); } catch { }
        _in?.Dispose();
        _in = null;
        lock (_samples) return _samples.ToArray();
    }

    public void Dispose()
    {
        try { _in?.StopRecording(); } catch { }
        _in?.Dispose();
    }
}
