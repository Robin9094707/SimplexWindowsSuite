using SimplexSuite.Core.Common;

namespace SimplexSuite.Core.Audio;

public sealed class TranscriptWord : ObservableObject
{
    private bool _isSelected;
    private bool _isFiller;
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
    public float Probability { get; set; } = 1;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public bool IsFiller { get => _isFiller; set => Set(ref _isFiller, value); }
    public TranscriptWord Clone() => new() { Start = Start, End = End, Text = Text, Probability = Probability, IsFiller = IsFiller };
}

public sealed class TranscriptLine
{
    public List<TranscriptWord> Words { get; } = new();
    public double Start => Words.Count > 0 ? Words[0].Start : 0;
    public double End => Words.Count > 0 ? Words[^1].End : 0;
    public string Text => string.Join(" ", Words.Select(w => w.Text));
    public string TimeText => TimeFormat.Format(Start, false);
}

public sealed class Transcript
{
    public List<TranscriptWord> Words { get; set; } = new();
    public string Language { get; set; } = "";

    public Transcript Clone() => new() { Words = Words.Select(w => w.Clone()).ToList(), Language = Language };

    public List<TranscriptLine> BuildLines(int maxWords = 14, double maxGap = 0.9)
    {
        var lines = new List<TranscriptLine>();
        TranscriptLine? cur = null;
        TranscriptWord? prev = null;
        foreach (var w in Words)
        {
            bool split = cur == null
                         || cur.Words.Count >= maxWords
                         || (prev != null && w.Start - prev.End > maxGap)
                         || (prev != null && cur.Words.Count >= 4 && EndsSentence(prev.Text));
            if (split)
            {
                cur = new TranscriptLine();
                lines.Add(cur);
            }
            cur!.Words.Add(w);
            prev = w;
        }
        return lines;
    }

    private static bool EndsSentence(string t) => t.EndsWith('.') || t.EndsWith('?') || t.EndsWith('!');

    public void MarkFillers(IEnumerable<string> fillers)
    {
        var set = new HashSet<string>(fillers.Select(Normalize).Where(f => f.Length > 0), StringComparer.OrdinalIgnoreCase);
        foreach (var w in Words) w.IsFiller = set.Contains(Normalize(w.Text));
    }

    public static string Normalize(string word) =>
        new string(word.Trim().ToLowerInvariant().Where(ch => char.IsLetterOrDigit(ch)).ToArray());

    /// <summary>Passt die Zeitstempel an eine Bearbeitung an (Bereich [start,start+oldLen) wurde durch newLen ersetzt).</summary>
    public void ApplyEdit(double start, double oldLen, double newLen)
    {
        if (Math.Abs(oldLen - newLen) < 1e-9) return;
        double oldEnd = start + oldLen;
        double delta = newLen - oldLen;
        var result = new List<TranscriptWord>(Words.Count);
        foreach (var w in Words)
        {
            double mid = (w.Start + w.End) / 2;
            if (w.End <= start + 1e-6) result.Add(w);
            else if (w.Start >= oldEnd - 1e-6)
            {
                w.Start += delta; w.End += delta;
                result.Add(w);
            }
            else if (oldLen > 0 && mid >= start && mid < oldEnd)
            {
                // Wort lag im gelöschten Bereich – bei Längenänderung (Tempo) proportional skalieren
                if (newLen > 0)
                {
                    double k = newLen / oldLen;
                    w.Start = start + (w.Start - start) * k;
                    w.End = start + (w.End - start) * k;
                    result.Add(w);
                }
            }
            else
            {
                // teilweise überlappt
                if (w.Start < start) w.End = Math.Min(w.End, start);
                else { w.Start = start + newLen; w.End = Math.Max(w.Start, w.End + delta); }
                result.Add(w);
            }
        }
        Words = result;
    }
}
