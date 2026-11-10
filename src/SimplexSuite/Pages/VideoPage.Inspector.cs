using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SimplexSuite.Core.Common;
using SimplexSuite.Core.Media;
using SimplexSuite.Core.Video;

namespace SimplexSuite.Pages;

public sealed partial class VideoPage
{
    private DateTime _lastInspectorUndo = DateTime.MinValue;
    private Guid? _lastInspectorClip;

    /// <summary>Undo-Schnappschuss für Inspektor-Änderungen (zusammengefasst innerhalb von 1,5 s).</summary>
    private void InspectorUndo(Clip c)
    {
        if (_lastInspectorClip != c.Id || (DateTime.Now - _lastInspectorUndo).TotalSeconds > 1.5) PushUndo();
        _lastInspectorUndo = DateTime.Now;
        _lastInspectorClip = c.Id;
    }

    private void BuildInspector()
    {
        InspectorHost.Children.Clear();
        var clip = Timeline.SelectedClip;
        var track = Timeline.SelectedTrack;
        if (clip == null || track == null)
        {
            InspectorTitle.Text = "Projekt";
            BuildProjectInspector();
            return;
        }
        var media = _project.FindMedia(clip.MediaId);
        InspectorTitle.Text = track.Kind == TrackKind.Text ? "Text" : (media?.Name ?? "Clip");
        bool visual = track.Kind == TrackKind.Video && media?.Kind != MediaKind.Audio;
        bool audio = track.Kind != TrackKind.Text && media?.HasAudio == true;
        bool isText = track.Kind == TrackKind.Text;

        // --- Zeit
        var time = Expander("Zeit", "", true);
        Num(time, "Start", "s", 0, 36000, 0.01, () => clip.Start, v => clip.Start = v, timeline: true);
        Num(time, "Dauer", "s", 0.05, 36000, 0.01, () => clip.Duration, v =>
        {
            if (media != null && media.Kind != MediaKind.Image) v = Math.Min(v, (media.Duration - clip.In) / clip.Speed);
            clip.Duration = v;
        }, timeline: true);
        if (media != null && media.Kind != MediaKind.Image)
        {
            Num(time, "Quell-Startpunkt", "s", 0, Math.Max(0, media.Duration - 0.05), 0.01, () => clip.In, v =>
            {
                clip.In = v;
                clip.Duration = Math.Min(clip.Duration, (media.Duration - v) / clip.Speed);
            }, timeline: true);
            Num(time, "Geschwindigkeit", "×", 0.1, 16, 0.05, () => clip.Speed, v =>
            {
                double srcLen = clip.Duration * clip.Speed;
                clip.Speed = v;
                clip.Duration = Math.Min(srcLen / v, (media.Duration - clip.In) / v);
            }, timeline: true);
        }
        InspectorHost.Children.Add(time);

        if (isText)
        {
            var text = Expander("Text", "", true);
            var tb = new TextBox { Text = clip.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, HorizontalAlignment = HorizontalAlignment.Stretch };
            tb.TextChanged += (_, _) => { InspectorUndo(clip); clip.Text = tb.Text; Timeline.Rebuild(); MarkDirty(); RequestFrame(); };
            text.Items.Add(new SettingsCard { ContentAlignment = ContentAlignment.Vertical, HorizontalContentAlignment = HorizontalAlignment.Stretch, Header = "Inhalt", Content = tb });
            var fonts = new ComboBox { MinWidth = 180, MaxWidth = 200 };
            var families = FontCatalog.Families.ToList();
            foreach (var f in families) fonts.Items.Add(f);
            fonts.SelectedItem = families.FirstOrDefault(f => string.Equals(f, clip.FontFamily, StringComparison.OrdinalIgnoreCase));
            fonts.SelectionChanged += (_, _) => { if (fonts.SelectedItem is string f) { InspectorUndo(clip); clip.FontFamily = f; MarkDirty(); RequestFrame(); } };
            text.Items.Add(new SettingsCard { Header = "Schriftart", Content = fonts });
            Toggle(text, "Fett", () => clip.Bold, v => clip.Bold = v);
            Toggle(text, "Kursiv", () => clip.Italic, v => clip.Italic = v);
            Num(text, "Größe", "px", 6, 600, 1, () => clip.FontSize, v => clip.FontSize = v);
            Color(text, "Farbe", () => clip.FontColor, v => clip.FontColor = v);
            Num(text, "Position X", "%", 0, 100, 0.5, () => clip.TextX * 100, v => clip.TextX = v / 100);
            Num(text, "Position Y", "%", 0, 100, 0.5, () => clip.TextY * 100, v => clip.TextY = v / 100);
            Num(text, "Zeilenabstand", "px", -50, 200, 1, () => clip.LineSpacing, v => clip.LineSpacing = v);
            Num(text, "Kontur", "px", 0, 30, 0.5, () => clip.OutlineWidth, v => clip.OutlineWidth = v);
            Color(text, "Konturfarbe", () => clip.OutlineColor, v => clip.OutlineColor = v);
            Num(text, "Schatten", "px", 0, 40, 0.5, () => clip.ShadowOffset, v => clip.ShadowOffset = v);
            Toggle(text, "Hintergrundbox", () => clip.Box, v => clip.Box = v);
            Color(text, "Boxfarbe", () => clip.BoxColor, v => clip.BoxColor = v);
            Num(text, "Box-Deckkraft", "%", 0, 100, 1, () => clip.BoxOpacity * 100, v => clip.BoxOpacity = v / 100);
            Num(text, "Box-Abstand", "px", 0, 200, 1, () => clip.BoxPadding, v => clip.BoxPadding = v);
            InspectorHost.Children.Add(text);
        }

        if (visual)
        {
            var tr = Expander("Transformieren", "", false);
            Num(tr, "Skalierung", "%", 1, 2000, 1, () => clip.Scale * 100, v => clip.Scale = v / 100);
            Num(tr, "Position X", "px", -10000, 10000, 1, () => clip.PosX, v => clip.PosX = v);
            Num(tr, "Position Y", "px", -10000, 10000, 1, () => clip.PosY, v => clip.PosY = v);
            Num(tr, "Drehung", "°", -360, 360, 0.5, () => clip.Rotation, v => clip.Rotation = v);
            Toggle(tr, "Bild füllen (zuschneiden)", () => clip.Fill, v => clip.Fill = v);
            Toggle(tr, "Horizontal spiegeln", () => clip.FlipH, v => clip.FlipH = v);
            Toggle(tr, "Vertikal spiegeln", () => clip.FlipV, v => clip.FlipV = v);
            Num(tr, "Zuschnitt links", "%", 0, 45, 0.5, () => clip.CropLeft * 100, v => clip.CropLeft = v / 100);
            Num(tr, "Zuschnitt rechts", "%", 0, 45, 0.5, () => clip.CropRight * 100, v => clip.CropRight = v / 100);
            Num(tr, "Zuschnitt oben", "%", 0, 45, 0.5, () => clip.CropTop * 100, v => clip.CropTop = v / 100);
            Num(tr, "Zuschnitt unten", "%", 0, 45, 0.5, () => clip.CropBottom * 100, v => clip.CropBottom = v / 100);
            InspectorHost.Children.Add(tr);

            var col = Expander("Farbe & Bild", "", false);
            Num(col, "Helligkeit", "", -1, 1, 0.01, () => clip.Brightness, v => clip.Brightness = v);
            Num(col, "Kontrast", "", 0, 3, 0.01, () => clip.Contrast, v => clip.Contrast = v);
            Num(col, "Sättigung", "", 0, 3, 0.01, () => clip.Saturation, v => clip.Saturation = v);
            Num(col, "Gamma", "", 0.1, 3, 0.01, () => clip.Gamma, v => clip.Gamma = v);
            Num(col, "Farbton", "°", -180, 180, 1, () => clip.Hue, v => clip.Hue = v);
            Num(col, "Farbtemperatur", "", -1, 1, 0.01, () => clip.Temperature, v => clip.Temperature = v);
            Num(col, "Weichzeichnen", "", 0, 50, 0.1, () => clip.Blur, v => clip.Blur = v);
            Num(col, "Schärfen", "", 0, 3, 0.05, () => clip.Sharpen, v => clip.Sharpen = v);
            Num(col, "Vignette", "", 0, 1, 0.01, () => clip.Vignette, v => clip.Vignette = v);
            InspectorHost.Children.Add(col);

            var key = Expander("Chroma Key (Greenscreen)", "", false);
            Toggle(key, "Aktiv", () => clip.ChromaKey, v => clip.ChromaKey = v);
            Color(key, "Schlüsselfarbe", () => clip.ChromaColor, v => clip.ChromaColor = v);
            Num(key, "Toleranz", "", 0.01, 1, 0.005, () => clip.ChromaSimilarity, v => clip.ChromaSimilarity = v);
            Num(key, "Weichheit", "", 0, 1, 0.005, () => clip.ChromaBlend, v => clip.ChromaBlend = v);
            InspectorHost.Children.Add(key);
        }

        if (visual || isText)
        {
            var fx = Expander("Deckkraft & Blenden", "", false);
            Num(fx, "Deckkraft", "%", 0, 100, 1, () => clip.Opacity * 100, v => clip.Opacity = v / 100);
            Num(fx, "Einblenden", "s", 0, 30, 0.05, () => clip.FadeIn, v => clip.FadeIn = Math.Min(v, clip.Duration), timeline: true);
            Num(fx, "Ausblenden", "s", 0, 30, 0.05, () => clip.FadeOut, v => clip.FadeOut = Math.Min(v, clip.Duration), timeline: true);
            InspectorHost.Children.Add(fx);
        }

        if (audio)
        {
            var au = Expander("Audio", "", false);
            Num(au, "Lautstärke", "%", 0, 400, 1, () => clip.Volume * 100, v => clip.Volume = v / 100, timeline: true);
            Num(au, "Einblenden", "s", 0, 30, 0.05, () => clip.AudioFadeIn, v => clip.AudioFadeIn = v);
            Num(au, "Ausblenden", "s", 0, 30, 0.05, () => clip.AudioFadeOut, v => clip.AudioFadeOut = v);
            Toggle(au, "Ton stumm", () => clip.MuteAudio, v => clip.MuteAudio = v);
            Toggle(au, "Rauschunterdrückung", () => clip.Denoise, v => clip.Denoise = v);
            InspectorHost.Children.Add(au);
        }

        var actions = new SettingsCard { Header = "Clip-Aktionen" };
        var split = new Button { Content = "Teilen" };
        split.Click += (_, _) => SplitAtPlayhead();
        var del = new Button { Content = "Löschen" };
        del.Click += (_, _) => DeleteSelected(false);
        actions.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { split, del } };
        InspectorHost.Children.Add(actions);
    }

    private void BuildProjectInspector()
    {
        var info = new SettingsCard
        {
            Header = $"{_project.Width} × {_project.Height} · {_project.Fps:0.##} fps",
            Description = "Wähle einen Clip in der Timeline, um ihn zu bearbeiten.",
            HeaderIcon = Ui.Icon(""),
        };
        var btn = new Button { Content = "Ändern" };
        btn.Click += (_, _) => ProjectSettings_Click(this, new RoutedEventArgs());
        info.Content = btn;
        InspectorHost.Children.Add(info);
        InspectorHost.Children.Add(new SettingsCard
        {
            Header = "Tastenkürzel",
            Description = "Leertaste Wiedergabe · S Teilen · Entf Löschen · Umschalt+Entf Lücke schließen · ←/→ Einzelbild · T Text · Strg+D Duplizieren",
            HeaderIcon = Ui.Icon(""),
        });
    }

    // ------------------------------------------------------------------ Bausteine

    private static SettingsExpander Expander(string header, string glyph, bool expanded) =>
        new() { Header = header, HeaderIcon = Ui.Icon(glyph), IsExpanded = expanded };

    private void Changed(Clip c, bool timeline)
    {
        if (timeline) Timeline.Rebuild();
        MarkDirty();
        RequestFrame();
    }

    private void Num(SettingsExpander exp, string label, string unit, double min, double max, double step,
        Func<double> get, Action<double> set, bool timeline = false)
    {
        var clip = Timeline.SelectedClip!;
        var nb = new NumberBox
        {
            Minimum = min, Maximum = max, Value = Math.Round(get(), 4), SmallChange = step, LargeChange = step * 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 120,
            NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter
            {
                FractionDigits = step >= 1 ? 0 : (step >= 0.1 ? 1 : (step >= 0.01 ? 2 : 3)),
                NumberRounder = new Windows.Globalization.NumberFormatting.IncrementNumberRounder { Increment = step },
            },
        };
        nb.ValueChanged += (_, e) =>
        {
            if (double.IsNaN(e.NewValue) || Math.Abs(e.NewValue - get()) < 1e-9) return;
            InspectorUndo(clip);
            set(e.NewValue);
            Changed(clip, timeline);
        };
        var unitText = new TextBlock { Text = unit, MinWidth = 18, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.ThemeBrush("TextFillColorSecondaryBrush") };
        exp.Items.Add(new SettingsCard { Header = label, Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { nb, unitText } } });
    }

    private void Toggle(SettingsExpander exp, string label, Func<bool> get, Action<bool> set)
    {
        var clip = Timeline.SelectedClip!;
        var ts = new ToggleSwitch { IsOn = get(), OnContent = "", OffContent = "", MinWidth = 0 };
        ts.Toggled += (_, _) =>
        {
            if (ts.IsOn == get()) return;
            InspectorUndo(clip);
            set(ts.IsOn);
            Changed(clip, true);
        };
        exp.Items.Add(new SettingsCard { Header = label, Content = ts });
    }

    private void Color(SettingsExpander exp, string label, Func<string> get, Action<string> set)
    {
        var clip = Timeline.SelectedClip!;
        var swatch = new Border { Width = 28, Height = 20, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = Ui.ThemeBrush("ControlStrokeColorDefaultBrush"), Background = new SolidColorBrush(Ui.ParseColor(get())) };
        var hex = new TextBlock { Text = get().ToUpperInvariant(), VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas") };
        var picker = new ColorPicker
        {
            Color = Ui.ParseColor(get()),
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorSliderVisible = true,
            IsHexInputVisible = true,
            IsColorChannelTextInputVisible = false,
        };
        picker.ColorChanged += (_, e) =>
        {
            var h = Ui.ToHex(e.NewColor);
            if (string.Equals(h, get(), StringComparison.OrdinalIgnoreCase)) return;
            InspectorUndo(clip);
            set(h);
            swatch.Background = new SolidColorBrush(e.NewColor);
            hex.Text = h;
            Changed(clip, false);
        };
        var button = new DropDownButton
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { swatch, hex } },
            Flyout = new Flyout { Content = picker },
        };
        exp.Items.Add(new SettingsCard { Header = label, Content = button });
    }
}
