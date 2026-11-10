using SimplexSuite.Core.Common;

namespace SimplexSuite.Core.Audio;

public sealed class AudioMarker : ObservableObject
{
    private double _time;
    private string _name = "";
    public double Time { get => _time; set { if (Set(ref _time, value)) OnPropertyChanged(nameof(TimeText)); } }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string TimeText => TimeFormat.Format(Time);
}

public sealed class AudioEditEventArgs : EventArgs
{
    public long StartFrame { get; init; }
    public long OldLength { get; init; }
    public long NewLength { get; init; }
    public bool IsUndoRedo { get; init; }
}

/// <summary>
/// Ein Audiodokument im Speicher (Float, interleaved). Jede Bearbeitung ist ein "Ersetze Bereich"-Schritt,
/// der rückgängig gemacht werden kann. Das hält den Speicherbedarf für Undo gering.
/// </summary>
public sealed class AudioDocument
{
    private sealed class EditRecord
    {
        public required string Name { get; init; }
        public long Start { get; init; }
        public required float[] OldData { get; init; }
        public required float[] NewData { get; init; }
        public Transcript? TranscriptBefore { get; init; }
        public Transcript? TranscriptAfter { get; set; }
        public long Bytes => (OldData.LongLength + NewData.LongLength) * 4;
    }

    private readonly LinkedList<EditRecord> _undo = new();
    private readonly Stack<EditRecord> _redo = new();

    public float[] Samples { get; private set; }
    public int Channels { get; }
    public int SampleRate { get; }
    public string? FilePath { get; set; }
    public bool IsDirty { get; private set; }
    public Transcript? Transcript { get; set; }
    public List<AudioMarker> Markers { get; } = new();
    public long MaxUndoBytes { get; set; } = 1024L * 1024 * 1024;

    public event EventHandler<AudioEditEventArgs>? Changed;

    public AudioDocument(float[] samples, int channels, int sampleRate)
    {
        Samples = samples;
        Channels = channels;
        SampleRate = sampleRate;
    }

    public long Frames => Samples.LongLength / Channels;
    public double Duration => (double)Frames / SampleRate;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoName => _undo.Last?.Value.Name;
    public string? RedoName => _redo.Count > 0 ? _redo.Peek().Name : null;

    public long ToFrame(double seconds) => Math.Clamp((long)Math.Round(seconds * SampleRate), 0, Frames);
    public double ToSeconds(long frame) => (double)frame / SampleRate;

    public float[] GetRange(long startFrame, long lengthFrames)
    {
        startFrame = Math.Clamp(startFrame, 0, Frames);
        lengthFrames = Math.Clamp(lengthFrames, 0, Frames - startFrame);
        var result = new float[lengthFrames * Channels];
        Array.Copy(Samples, startFrame * Channels, result, 0, result.LongLength);
        return result;
    }

    /// <summary>Ersetzt [start, start+length) durch newData (interleaved). Grundoperation jeder Bearbeitung.</summary>
    public void Replace(long startFrame, long lengthFrames, float[] newData, string name)
    {
        startFrame = Math.Clamp(startFrame, 0, Frames);
        lengthFrames = Math.Clamp(lengthFrames, 0, Frames - startFrame);
        var old = GetRange(startFrame, lengthFrames);
        var before = Transcript?.Clone();
        ApplyRaw(startFrame, lengthFrames, newData);
        var rec = new EditRecord { Name = name, Start = startFrame, OldData = old, NewData = newData, TranscriptBefore = before };
        Transcript?.ApplyEdit(ToSeconds(startFrame), ToSeconds(lengthFrames), ToSeconds(newData.LongLength / Channels));
        ShiftMarkers(startFrame, lengthFrames, newData.LongLength / Channels);
        rec.TranscriptAfter = Transcript?.Clone();
        _undo.AddLast(rec);
        _redo.Clear();
        TrimUndo();
        IsDirty = true;
        Changed?.Invoke(this, new AudioEditEventArgs { StartFrame = startFrame, OldLength = lengthFrames, NewLength = newData.LongLength / Channels });
    }

    private void ShiftMarkers(long start, long oldLen, long newLen)
    {
        if (oldLen == newLen) return;
        double s = ToSeconds(start), e = ToSeconds(start + oldLen), d = ToSeconds(newLen - oldLen);
        foreach (var m in Markers.ToList())
        {
            if (m.Time >= e) m.Time += d;
            else if (m.Time > s) m.Time = s;
        }
    }

    private void ApplyRaw(long start, long oldLen, float[] newData)
    {
        long newLen = newData.LongLength / Channels;
        if (newLen == oldLen)
        {
            Array.Copy(newData, 0, Samples, start * Channels, newData.LongLength);
            return;
        }
        var result = new float[(Frames - oldLen + newLen) * Channels];
        Array.Copy(Samples, 0, result, 0, start * Channels);
        Array.Copy(newData, 0, result, start * Channels, newData.LongLength);
        long tailSrc = (start + oldLen) * Channels;
        Array.Copy(Samples, tailSrc, result, (start + newLen) * Channels, Samples.LongLength - tailSrc);
        Samples = result;
    }

    private void TrimUndo()
    {
        long total = _undo.Sum(r => r.Bytes);
        while (_undo.Count > 1 && total > MaxUndoBytes)
        {
            total -= _undo.First!.Value.Bytes;
            _undo.RemoveFirst();
        }
    }

    public void Undo()
    {
        if (_undo.Last == null) return;
        var rec = _undo.Last.Value;
        _undo.RemoveLast();
        long newLen = rec.NewData.LongLength / Channels;
        ApplyRaw(rec.Start, newLen, rec.OldData);
        ShiftMarkers(rec.Start, newLen, rec.OldData.LongLength / Channels);
        Transcript = rec.TranscriptBefore?.Clone() ?? Transcript;
        _redo.Push(rec);
        IsDirty = true;
        Changed?.Invoke(this, new AudioEditEventArgs { StartFrame = rec.Start, OldLength = newLen, NewLength = rec.OldData.LongLength / Channels, IsUndoRedo = true });
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var rec = _redo.Pop();
        long oldLen = rec.OldData.LongLength / Channels;
        ApplyRaw(rec.Start, oldLen, rec.NewData);
        ShiftMarkers(rec.Start, oldLen, rec.NewData.LongLength / Channels);
        Transcript = rec.TranscriptAfter?.Clone() ?? Transcript;
        _undo.AddLast(rec);
        IsDirty = true;
        Changed?.Invoke(this, new AudioEditEventArgs { StartFrame = rec.Start, OldLength = oldLen, NewLength = rec.NewData.LongLength / Channels, IsUndoRedo = true });
    }

    public void MarkSaved() => IsDirty = false;

    // ---------- Komfortfunktionen ----------

    public void Delete(long start, long length) => Replace(start, length, Array.Empty<float>(), "Löschen");

    public void Insert(long at, float[] data, string name = "Einfügen") => Replace(at, 0, data, name);

    public void InsertSilence(long at, double seconds) =>
        Replace(at, 0, new float[(long)(seconds * SampleRate) * Channels], "Stille einfügen");

    public void Silence(long start, long length) =>
        Replace(start, length, new float[length * Channels], "Stummschalten");

    /// <summary>Behält nur den Bereich (Zuschneiden).</summary>
    public void Trim(long start, long length)
    {
        // Als zwei Schritte wäre unschön – deshalb Gesamtersetzung
        var keep = GetRange(start, length);
        Replace(0, Frames, keep, "Zuschneiden");
    }

    /// <summary>Löscht mehrere Bereiche (Sekunden) in einem einzigen Rückgängig-Schritt.</summary>
    public void DeleteRanges(IEnumerable<(double Start, double End)> ranges, string name)
    {
        var sorted = ranges.Where(r => r.End > r.Start).OrderBy(r => r.Start).ToList();
        if (sorted.Count == 0) return;
        // Bereiche zusammenführen
        var merged = new List<(long S, long E)>();
        foreach (var r in sorted)
        {
            long s = ToFrame(r.Start), e = ToFrame(r.End);
            if (merged.Count > 0 && s <= merged[^1].E) merged[^1] = (merged[^1].S, Math.Max(merged[^1].E, e));
            else merged.Add((s, e));
        }
        long regionStart = merged[0].S, regionEnd = merged[^1].E;
        // Kurze Blenden (5 ms) an Schnittstellen vermeiden Knackser
        int fade = Math.Min(SampleRate / 200, 240);
        var output = new List<float>();
        long cursor = regionStart;
        var region = GetRange(regionStart, regionEnd - regionStart);
        foreach (var (s, e) in merged)
        {
            AppendWithFades(output, region, cursor - regionStart, s - regionStart, fade);
            cursor = e;
        }
        var newData = output.ToArray();
        // Transkript: Bereiche nacheinander von hinten anwenden
        var before = Transcript?.Clone();
        Replace(regionStart, regionEnd - regionStart, newData, name);
        if (before != null)
        {
            var t = before;
            for (int i = merged.Count - 1; i >= 0; i--)
                t.ApplyEdit(ToSeconds(merged[i].S), ToSeconds(merged[i].E - merged[i].S), 0);
            Transcript = t;
            if (_undo.Last != null) _undo.Last.Value.TranscriptAfter = t.Clone();
        }
    }

    private void AppendWithFades(List<float> output, float[] region, long fromFrame, long toFrame, int fade)
    {
        long len = toFrame - fromFrame;
        if (len <= 0) return;
        for (long f = 0; f < len; f++)
        {
            float g = 1;
            if (f < fade && fromFrame > 0) g = (float)f / fade;
            long fromEnd = len - f;
            if (fromEnd < fade && toFrame < region.LongLength / Channels) g = Math.Min(g, (float)fromEnd / fade);
            for (int c = 0; c < Channels; c++) output.Add(region[(fromFrame + f) * Channels + c] * g);
        }
    }

    public static async Task<AudioDocument> LoadAsync(string path, int sampleRate, bool forceMono, IProgress<double>? progress, CancellationToken ct)
    {
        var info = await Media.MediaProbe.ProbeAsync(path, ct).ConfigureAwait(false);
        if (!info.HasAudio) throw new InvalidOperationException("Die Datei enthält keine Audiospur.");
        int channels = forceMono ? 1 : Math.Clamp(info.AudioChannels, 1, 2);
        var samples = await AudioIO.DecodeAsync(path, sampleRate, channels, ct, progress, info.Duration).ConfigureAwait(false);
        return new AudioDocument(samples, channels, sampleRate) { FilePath = path };
    }
}
