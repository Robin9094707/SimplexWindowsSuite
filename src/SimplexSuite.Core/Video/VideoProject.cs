using System.Text.Json;
using System.Text.Json.Serialization;
using SimplexSuite.Core.Media;

namespace SimplexSuite.Core.Video;

public enum TrackKind { Video, Audio, Text }

public sealed class MediaItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public MediaKind Kind { get; set; }
    public double Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public bool HasAudio { get; set; }
    public bool HasVideo { get; set; }
    [JsonIgnore] public string? ThumbnailPath { get; set; }
    [JsonIgnore] public float[]? Peaks { get; set; }   // 50 pro Sekunde
}

public sealed class Track
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public TrackKind Kind { get; set; }
    public bool Muted { get; set; }
    public bool Hidden { get; set; }
    public bool Locked { get; set; }
    public double Volume { get; set; } = 1;
    public List<Clip> Clips { get; set; } = new();
}

public sealed class Clip
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MediaId { get; set; }
    public string Name { get; set; } = "";

    // Zeit
    public double Start { get; set; }
    public double In { get; set; }
    public double Duration { get; set; } = 5;
    public double Speed { get; set; } = 1;
    [JsonIgnore] public double End => Start + Duration;

    // Bild
    public double Opacity { get; set; } = 1;
    public double Scale { get; set; } = 1;
    public double PosX { get; set; }
    public double PosY { get; set; }
    public double Rotation { get; set; }
    public bool FlipH { get; set; }
    public bool FlipV { get; set; }
    public double CropLeft { get; set; }
    public double CropTop { get; set; }
    public double CropRight { get; set; }
    public double CropBottom { get; set; }
    public bool Fill { get; set; }

    // Farbe
    public double Brightness { get; set; }
    public double Contrast { get; set; } = 1;
    public double Saturation { get; set; } = 1;
    public double Gamma { get; set; } = 1;
    public double Hue { get; set; }
    public double Temperature { get; set; }
    public double Blur { get; set; }
    public double Sharpen { get; set; }
    public double Vignette { get; set; }

    // Chroma Key
    public bool ChromaKey { get; set; }
    public string ChromaColor { get; set; } = "#00FF00";
    public double ChromaSimilarity { get; set; } = 0.15;
    public double ChromaBlend { get; set; } = 0.05;

    // Übergänge
    public double FadeIn { get; set; }
    public double FadeOut { get; set; }

    // Audio
    public double Volume { get; set; } = 1;
    public double AudioFadeIn { get; set; }
    public double AudioFadeOut { get; set; }
    public bool MuteAudio { get; set; }
    public bool Denoise { get; set; }

    // Text
    public string Text { get; set; } = "";
    public string FontFamily { get; set; } = "Segoe UI";
    public bool Bold { get; set; } = true;
    public bool Italic { get; set; }
    public double FontSize { get; set; } = 72;
    public string FontColor { get; set; } = "#FFFFFF";
    public bool Box { get; set; }
    public string BoxColor { get; set; } = "#000000";
    public double BoxOpacity { get; set; } = 0.6;
    public double BoxPadding { get; set; } = 18;
    public double OutlineWidth { get; set; } = 3;
    public string OutlineColor { get; set; } = "#000000";
    public double ShadowOffset { get; set; }
    public double TextX { get; set; } = 0.5;
    public double TextY { get; set; } = 0.5;
    public double LineSpacing { get; set; }

    public Clip CloneWithNewId()
    {
        var json = JsonSerializer.Serialize(this);
        var c = JsonSerializer.Deserialize<Clip>(json)!;
        c.Id = Guid.NewGuid();
        return c;
    }
}

public sealed class VideoProject
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public double Fps { get; set; } = 30;
    public string Background { get; set; } = "#000000";
    public List<MediaItem> Media { get; set; } = new();
    /// <summary>Spuren von oben nach unten (oberste Spur wird zuletzt, also oben, gezeichnet).</summary>
    public List<Track> Tracks { get; set; } = new();

    [JsonIgnore] public double Duration => Tracks.SelectMany(t => t.Clips).Select(c => c.End).DefaultIfEmpty(0).Max();

    public static VideoProject CreateDefault()
    {
        var p = new VideoProject();
        p.Tracks.Add(new Track { Name = "Text 1", Kind = TrackKind.Text });
        p.Tracks.Add(new Track { Name = "Video 2", Kind = TrackKind.Video });
        p.Tracks.Add(new Track { Name = "Video 1", Kind = TrackKind.Video });
        p.Tracks.Add(new Track { Name = "Audio 1", Kind = TrackKind.Audio });
        p.Tracks.Add(new Track { Name = "Audio 2", Kind = TrackKind.Audio });
        return p;
    }

    public MediaItem? FindMedia(Guid id) => Media.FirstOrDefault(m => m.Id == id);

    public (Track Track, Clip Clip)? FindClip(Guid id)
    {
        foreach (var t in Tracks)
            foreach (var c in t.Clips)
                if (c.Id == id) return (t, c);
        return null;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);
    public static VideoProject FromJson(string json) => JsonSerializer.Deserialize<VideoProject>(json, JsonOpts) ?? CreateDefault();

    public VideoProject DeepClone()
    {
        var clone = FromJson(ToJson());
        foreach (var m in clone.Media)
        {
            var src = FindMedia(m.Id);
            if (src != null) { m.ThumbnailPath = src.ThumbnailPath; m.Peaks = src.Peaks; }
        }
        return clone;
    }

    /// <summary>Teilt einen Clip an der Zeit t (Timeline).</summary>
    public Clip? Split(Track track, Clip clip, double t)
    {
        if (t <= clip.Start + 0.01 || t >= clip.End - 0.01) return null;
        var right = clip.CloneWithNewId();
        double leftDur = t - clip.Start;
        right.Start = t;
        right.In = clip.In + leftDur * clip.Speed;
        right.Duration = clip.Duration - leftDur;
        right.FadeIn = 0; right.AudioFadeIn = 0;
        clip.Duration = leftDur;
        clip.FadeOut = 0; clip.AudioFadeOut = 0;
        track.Clips.Add(right);
        track.Clips.Sort((a, b) => a.Start.CompareTo(b.Start));
        return right;
    }

    /// <summary>Entfernt Lücke: verschiebt alle Clips nach t um delta (für Ripple-Löschen).</summary>
    public void Ripple(double fromTime, double delta)
    {
        foreach (var t in Tracks)
            foreach (var c in t.Clips)
                if (c.Start >= fromTime - 1e-6) c.Start = Math.Max(0, c.Start + delta);
    }
}
