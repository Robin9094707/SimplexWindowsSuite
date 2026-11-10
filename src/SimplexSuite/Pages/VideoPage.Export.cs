using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimplexSuite.Core.Audio;
using SimplexSuite.Core.Common;
using SimplexSuite.Core.Media;
using SimplexSuite.Core.Transcription;
using SimplexSuite.Core.Video;

namespace SimplexSuite.Pages;

public sealed partial class VideoPage
{
    private static readonly (string Label, string Container, string Codec, string Ext)[] Formats =
    {
        ("MP4 · H.264 (überall abspielbar)", "mp4", "h264", ".mp4"),
        ("MP4 · H.265 / HEVC (kleiner)", "mp4", "hevc", ".mp4"),
        ("MP4 · AV1 (modern, sehr effizient)", "mp4", "av1", ".mp4"),
        ("WebM · VP9 (Web)", "webm", "vp9", ".webm"),
        ("MOV · ProRes 422 HQ (Schnitt)", "mov", "prores", ".mov"),
        ("MKV · H.264", "mkv", "h264", ".mkv"),
        ("GIF (animiert)", "gif", "gif", ".gif"),
        ("Nur Audio · MP3", "mp3", "", ".mp3"),
        ("Nur Audio · WAV", "wav", "", ".wav"),
        ("Nur Audio · M4A", "m4a", "", ".m4a"),
    };

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        if (_project.Duration <= 0) { await Ui.InfoAsync("Nichts zu exportieren", "Die Timeline ist leer."); return; }
        var formatBox = new ComboBox { Header = "Format", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var f in Formats) formatBox.Items.Add(f.Label);
        formatBox.SelectedIndex = 0;
        var encoderBox = new ComboBox { Header = "Encoder", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var en in new[] { "Prozessor (beste Qualität)", "NVIDIA (NVENC)", "Intel (Quick Sync)", "AMD (AMF)" }) encoderBox.Items.Add(en);
        encoderBox.SelectedIndex = 0;
        var resBox = new ComboBox { Header = "Auflösung", HorizontalAlignment = HorizontalAlignment.Stretch };
        var scales = new List<double>();
        foreach (var h in new[] { _project.Height, 2160, 1440, 1080, 720, 480, 360 }.Distinct().Where(h => h <= _project.Height))
        {
            double s = (double)h / _project.Height;
            scales.Add(s);
            int w = (int)Math.Round(_project.Width * s / 2) * 2;
            resBox.Items.Add(h == _project.Height ? $"Original ({_project.Width}×{_project.Height})" : $"{w}×{h}");
        }
        resBox.SelectedIndex = 0;
        var quality = new Slider { Header = "Qualität (CRF – kleiner = besser)", Minimum = 0, Maximum = 51, Value = 20, StepFrequency = 1, TickFrequency = 5, TickPlacement = Microsoft.UI.Xaml.Controls.Primitives.TickPlacement.Outside };
        var presetBox = new ComboBox { Header = "Geschwindigkeit", HorizontalAlignment = HorizontalAlignment.Stretch };
        var presets = new[] { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow" };
        foreach (var p in presets) presetBox.Items.Add(p);
        presetBox.SelectedIndex = 5;
        var audioBox = new ComboBox { Header = "Audio-Bitrate", HorizontalAlignment = HorizontalAlignment.Stretch };
        var bitrates = new[] { 96, 128, 160, 192, 256, 320 };
        foreach (var b in bitrates) audioBox.Items.Add(b + " kbit/s");
        audioBox.SelectedIndex = 3;
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.ThemeBrush("TextFillColorSecondaryBrush"), FontSize = 12,
            Text = "Grafikkarten-Encoder sind deutlich schneller. Falls der Export fehlschlägt, wird deine Grafikkarte nicht unterstützt – wähle dann „Prozessor“." };
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12, Width = 500 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Place(FrameworkElement el, int r, int c, int span = 1)
        {
            while (grid.RowDefinitions.Count <= r) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(el, r); Grid.SetColumn(el, c); Grid.SetColumnSpan(el, span); grid.Children.Add(el);
        }
        Place(formatBox, 0, 0, 2);
        Place(resBox, 1, 0); Place(encoderBox, 1, 1);
        Place(quality, 2, 0, 2);
        Place(presetBox, 3, 0); Place(audioBox, 3, 1);
        Place(hint, 4, 0, 2);
        formatBox.SelectionChanged += (_, _) =>
        {
            var f = Formats[formatBox.SelectedIndex];
            bool video = f.Codec is "h264" or "hevc" or "av1" or "vp9";
            encoderBox.IsEnabled = f.Codec is "h264" or "hevc" or "av1";
            quality.IsEnabled = video;
            presetBox.IsEnabled = f.Codec is "h264" or "hevc";
            resBox.IsEnabled = f.Codec != "";
            audioBox.IsEnabled = f.Container is not ("gif" or "wav") && f.Codec != "prores";
            if (f.Codec == "vp9" || f.Codec == "av1") quality.Value = 30;
        };
        var dialog = new ContentDialog
        {
            Title = "Video exportieren",
            Content = grid,
            PrimaryButtonText = "Exportieren …",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await Ui.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        var fmt = Formats[formatBox.SelectedIndex];
        var name = _projectPath != null ? Path.GetFileNameWithoutExtension(_projectPath) : "Video";
        var path = await Ui.PickSaveFileAsync(name, (fmt.Label, fmt.Ext));
        if (path == null) return;
        var settings = new ExportSettings
        {
            Container = fmt.Container,
            VideoCodec = fmt.Codec,
            Encoder = new[] { "cpu", "nvenc", "qsv", "amf" }[encoderBox.SelectedIndex],
            Quality = (int)quality.Value,
            Preset = presets[presetBox.SelectedIndex],
            AudioBitrateKbps = bitrates[audioBox.SelectedIndex],
            ScaleFactor = scales[Math.Max(0, resBox.SelectedIndex)],
        };
        var snapshot = _project.DeepClone();
        var started = DateTime.Now;
        await RunBusyAsync("Exportiere " + Path.GetFileName(path), async (p, ct) =>
        {
            await Task.Run(() => RenderGraphBuilder.ExportAsync(snapshot, settings, path, p, ct), ct);
            var took = DateTime.Now - started;
            Ui.Success("Export fertig", $"{Path.GetFileName(path)} in {took.TotalSeconds:0} s erstellt.");
            AppSettings.Current.AddRecent(path);
        });
    }

    private async void ProjectSettings_Click(object sender, RoutedEventArgs e)
    {
        var presets = new (string Label, int W, int H)[]
        {
            ("Full HD 1920×1080 (16:9)", 1920, 1080), ("4K UHD 3840×2160 (16:9)", 3840, 2160), ("HD 1280×720 (16:9)", 1280, 720),
            ("Hochformat 1080×1920 (9:16, Reels/Shorts/TikTok)", 1080, 1920), ("Quadrat 1080×1080 (1:1)", 1080, 1080),
            ("Instagram 1080×1350 (4:5)", 1080, 1350), ("Kino 2560×1080 (21:9)", 2560, 1080), ("Benutzerdefiniert", 0, 0),
        };
        var presetBox = new ComboBox { Header = "Format", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var p in presets) presetBox.Items.Add(p.Label);
        int idx = Array.FindIndex(presets, p => p.W == _project.Width && p.H == _project.Height);
        presetBox.SelectedIndex = idx >= 0 ? idx : presets.Length - 1;
        var w = new NumberBox { Header = "Breite", Value = _project.Width, Minimum = 16, Maximum = 8192, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var h = new NumberBox { Header = "Höhe", Value = _project.Height, Minimum = 16, Maximum = 8192, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        presetBox.SelectionChanged += (_, _) =>
        {
            var p = presets[presetBox.SelectedIndex];
            if (p.W > 0) { w.Value = p.W; h.Value = p.H; }
        };
        var fps = new ComboBox { Header = "Bildrate", HorizontalAlignment = HorizontalAlignment.Stretch, IsEditable = true };
        foreach (var f in new[] { "23.976", "24", "25", "29.97", "30", "50", "59.94", "60", "120" }) fps.Items.Add(f);
        fps.Text = _project.Fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var bg = new TextBox { Header = "Hintergrundfarbe (Hex)", Text = _project.Background };
        var panel = new StackPanel { Spacing = 12, Width = 420 };
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(h, 1);
        row.Children.Add(w); row.Children.Add(h);
        panel.Children.Add(presetBox);
        panel.Children.Add(row);
        panel.Children.Add(fps);
        panel.Children.Add(bg);
        var dialog = new ContentDialog { Title = "Projekteinstellungen", Content = panel, PrimaryButtonText = "Übernehmen", CloseButtonText = "Abbrechen", DefaultButton = ContentDialogButton.Primary };
        if (await Ui.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        PushUndo();
        _project.Width = Math.Max(16, (int)Math.Round(w.Value / 2) * 2);
        _project.Height = Math.Max(16, (int)Math.Round(h.Value / 2) * 2);
        var fpsText = (fps.SelectedItem as string ?? fps.Text ?? "30").Replace(',', '.');
        if (double.TryParse(fpsText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f2) && f2 is > 1 and <= 240) _project.Fps = f2;
        if (bg.Text.Trim().TrimStart('#').Length == 6) _project.Background = "#" + bg.Text.Trim().TrimStart('#');
        MarkDirty();
        BuildInspector();
        RequestFrame();
    }

    // ------------------------------------------------------------------ Untertitel

    private async void Subtitles_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        if (_project.Duration <= 0) return;
        var model = WhisperModels.Find(AppSettings.Current.WhisperModel) is { IsInstalled: true } m ? m : WhisperModels.Installed.FirstOrDefault();
        if (model == null)
        {
            if (await Ui.ConfirmAsync("Kein Sprachmodell", "Für automatische Untertitel wird ein Whisper-Modell benötigt. Jetzt in den Einstellungen herunterladen?", "Zu den Einstellungen"))
                App.MainWindow?.Navigate("settings");
            return;
        }
        var langBox = new ComboBox { Header = "Sprache", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (code, nameL) in WhisperModels.Languages) langBox.Items.Add(new ComboBoxItem { Content = nameL, Tag = code });
        langBox.SelectedItem = langBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == AppSettings.Current.WhisperLanguage) ?? langBox.Items[0];
        var translate = new ToggleSwitch { Header = "Ins Englische übersetzen" };
        var words = new NumberBox { Header = "Max. Wörter pro Untertitel", Value = 7, Minimum = 1, Maximum = 20, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var panel = new StackPanel { Spacing = 12, Width = 380, Children = { new TextBlock { Text = $"Modell: {model.DisplayName} (lokal)", Foreground = Ui.ThemeBrush("TextFillColorSecondaryBrush") }, langBox, translate, words } };
        var dlg = new ContentDialog { Title = "Untertitel erzeugen", Content = panel, PrimaryButtonText = "Erzeugen", CloseButtonText = "Abbrechen", DefaultButton = ContentDialogButton.Primary };
        if (await Ui.ShowDialogAsync(dlg) != ContentDialogResult.Primary) return;
        var lang = (langBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "de";
        int maxWords = (int)(double.IsNaN(words.Value) ? 7 : words.Value);
        var snapshot = _project.DeepClone();
        await RunBusyAsync("Erzeuge Untertitel (lokal)", async (p, ct) =>
        {
            var audioProgress = new Progress<double>(v => ((IProgress<double>)p).Report(v * 0.1));
            var wav = await Task.Run(() => RenderGraphBuilder.RenderAudioAsync(snapshot, 16000, 1, audioProgress, ct), ct);
            var samples = await AudioIO.DecodeAsync(wav, 16000, 1, ct);
            try { Directory.Delete(Path.GetDirectoryName(wav)!, true); } catch { }
            var whisperProgress = new Progress<double>(v => ((IProgress<double>)p).Report(0.1 + v * 0.9));
            var transcript = await WhisperTranscriber.TranscribeAsync(samples, new TranscriptionOptions
            {
                ModelPath = model.LocalPath, Language = lang, Translate = translate.IsOn, Threads = AppSettings.Current.WhisperThreads, Verbatim = false,
            }, whisperProgress, ct);
            PushUndo();
            var track = _project.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Text && t.Name == "Untertitel");
            if (track == null)
            {
                track = new Track { Name = "Untertitel", Kind = TrackKind.Text };
                _project.Tracks.Insert(0, track);
            }
            track.Clips.Clear();
            foreach (var line in transcript.BuildLines(maxWords, 0.7))
            {
                track.Clips.Add(new Clip
                {
                    Start = line.Start, Duration = Math.Max(0.4, line.End - line.Start + 0.15), Text = line.Text,
                    FontSize = Math.Round(_project.Height / 20.0), Bold = true, TextY = 0.88, Box = true, BoxOpacity = 0.55,
                    BoxPadding = Math.Round(_project.Height / 90.0), OutlineWidth = 0,
                });
            }
            // Überlappungen vermeiden
            for (int i = 0; i + 1 < track.Clips.Count; i++)
                if (track.Clips[i].End > track.Clips[i + 1].Start) track.Clips[i].Duration = Math.Max(0.2, track.Clips[i + 1].Start - track.Clips[i].Start);
            Timeline.Rebuild();
            MarkDirty();
            RequestFrame();
            Ui.Success("Untertitel erstellt", $"{track.Clips.Count} Untertitel auf der Spur „Untertitel“. Stil im Inspektor anpassbar.");
        });
    }

    private async void ExportSrt_Click(object sender, RoutedEventArgs e)
    {
        var track = Timeline.SelectedTrack is { Kind: TrackKind.Text } t ? t : _project.Tracks.FirstOrDefault(x => x.Kind == TrackKind.Text && x.Clips.Count > 0);
        if (track == null || track.Clips.Count == 0) { await Ui.InfoAsync("Keine Untertitel", "Es gibt keine Textspur mit Inhalt."); return; }
        var path = await Ui.PickSaveFileAsync("Untertitel", ("SubRip-Untertitel", ".srt"));
        if (path == null) return;
        var sb = new StringBuilder();
        int i = 1;
        foreach (var c in track.Clips.OrderBy(c => c.Start))
        {
            sb.AppendLine((i++).ToString());
            sb.Append(TimeFormat.Srt(c.Start)).Append(" --> ").AppendLine(TimeFormat.Srt(c.End));
            sb.AppendLine(c.Text);
            sb.AppendLine();
        }
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false));
        Ui.Success("Gespeichert", path);
    }

    private async void SaveFrame_Click(object sender, RoutedEventArgs e)
    {
        if (_project.Duration <= 0) return;
        var path = await Ui.PickSaveFileAsync("Standbild", ("JPEG-Bild", ".jpg"));
        if (path == null) return;
        await RunBusyAsync("Speichere Standbild", async (p, ct) =>
        {
            var snapshot = _project.DeepClone();
            double at = Timeline.Playhead;
            var file = await Task.Run(() => RenderGraphBuilder.RenderFrameAsync(snapshot, at, 1, ct), ct);
            if (file != null) File.Copy(file, path, true);
            Ui.Success("Gespeichert", path);
        });
    }
}
