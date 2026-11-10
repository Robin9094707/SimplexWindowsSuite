using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SimplexSuite.Core.Audio;
using SimplexSuite.Core.Audio.Effects;
using SimplexSuite.Core.Common;
using SimplexSuite.Core.Transcription;
using SimplexSuite.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace SimplexSuite.Pages;

public sealed partial class PodcastPage : Page, IClosableEditor, IDisposable
{
    private AudioDocument? _doc;
    private readonly AudioPlayer _player = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private CancellationTokenSource? _busyCts;
    private float[]? _clipboard;
    private int _clipboardChannels;
    private NoiseProfile? _noiseProfile;
    private double _playOffset;       // bei Vorhören: Position der Vorschau im Dokument
    private bool _previewing;
    private double _playEnd;

    public PodcastPage()
    {
        InitializeComponent();
        Wave.SelectionChanged += (_, _) => UpdateSelectionUi();
        Wave.CursorMoved += (_, _) => { if (!_player.IsPlaying) PositionText.Text = TimeFormat.Format(Wave.Cursor); };
        Wave.PlayRequested += (_, _) => StartPlayback(Wave.Cursor, Wave.Duration);
        _timer.Tick += Timer_Tick;
        _player.PlaybackStopped += (_, _) => DispatcherQueue.TryEnqueue(OnPlaybackStopped);
        KeyDown += Page_KeyDown;
        BuildEffects();
        FillTranscriptCombos();
        UpdateUi();
        Loaded += (_, _) => FillTranscriptCombos();
    }

    public bool HasUnsavedChanges => _doc?.IsDirty == true;
    public void DiscardChanges() => _doc?.MarkSaved();
    public void Dispose() { _player.Dispose(); }

    // ================================================================== Datei

    public async void OpenFile(string path) => await LoadAsync(path);

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var file = await Ui.PickOpenFileAsync(AudioIO.SupportedExtensions);
        if (file != null) await LoadAsync(file);
    }

    private async Task LoadAsync(string path)
    {
        if (_doc?.IsDirty == true && !await Ui.ConfirmAsync("Ungespeicherte Änderungen", "Die aktuelle Datei wurde verändert. Verwerfen und neue Datei öffnen?", "Verwerfen"))
            return;
        await RunBusyAsync("Lade " + Path.GetFileName(path), async (p, ct) =>
        {
            var s = AppSettings.Current;
            var doc = await AudioDocument.LoadAsync(path, s.WorkingSampleRate, s.ForceMono, p, ct);
            SetDocument(doc);
            s.AddRecent(path);
        });
    }

    private void SetDocument(AudioDocument? doc)
    {
        _player.Stop();
        if (_doc != null) _doc.Changed -= Doc_Changed;
        _doc = doc;
        if (doc != null)
        {
            doc.MaxUndoBytes = AppSettings.Current.MaxUndoMegabytes * 1024L * 1024;
            doc.Changed += Doc_Changed;
        }
        _noiseProfile = null;
        Wave.Markers = doc?.Markers;
        Wave.Highlights.Clear();
        Wave.Document = doc;
        RebuildTranscript();
        RebuildMarkers();
        UpdateUi();
        UpdateSelectionUi();
        LufsText.Text = LraText.Text = PeakText.Text = RmsText.Text = "–";
        FormatText.Text = doc == null ? "–" : $"{doc.SampleRate} Hz · {(doc.Channels == 1 ? "Mono" : "Stereo")} · 32-bit Float";
    }

    private void Doc_Changed(object? sender, AudioEditEventArgs e)
    {
        _player.Stop();
        _ = Wave.RefreshAsync(e.StartFrame);
        Wave.Highlights.Clear();
        if (Wave.Cursor > Wave.Duration) Wave.SetCursor(Wave.Duration);
        RebuildTranscript();
        RebuildMarkers();
        UpdateUi();
    }

    private void UpdateUi()
    {
        bool has = _doc != null;
        EmptyPanel.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        Wave.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        UndoButton.IsEnabled = _doc?.CanUndo == true;
        RedoButton.IsEnabled = _doc?.CanRedo == true;
        UndoButton.Label = _doc?.UndoName is string u ? $"Rückgängig: {u}" : "Rückgängig";
        DurationText.Text = TimeFormat.Format(_doc?.Duration ?? 0);
        var name = _doc?.FilePath != null ? Path.GetFileName(_doc.FilePath) : (has ? "Neue Aufnahme" : "");
        FileNameText.Text = has ? name + (_doc!.IsDirty ? " •" : "") : "";
        App.MainWindow?.SetTitle(has ? name : null);
    }

    private void UpdateSelectionUi()
    {
        if (_doc == null) { SelectionText.Text = "–"; TargetInfo.Message = "Keine Datei geladen"; return; }
        if (Wave.HasSelection)
        {
            SelectionText.Text = $"{TimeFormat.Format(Wave.SelStart)} – {TimeFormat.Format(Wave.SelEnd)}";
            TargetInfo.Title = "Ziel: Auswahl";
            TargetInfo.Message = $"{TimeFormat.Format(Wave.SelStart)} bis {TimeFormat.Format(Wave.SelEnd)} ({Wave.SelEnd - Wave.SelStart:0.00} s)";
        }
        else
        {
            SelectionText.Text = "–";
            TargetInfo.Title = "Ziel: Gesamte Datei";
            TargetInfo.Message = "Ziehe in der Wellenform, um nur einen Bereich zu bearbeiten.";
        }
        PositionText.Text = TimeFormat.Format(Wave.Cursor);
    }

    private (long Start, long Length) TargetRange()
    {
        if (_doc == null) return (0, 0);
        if (Wave.HasSelection)
        {
            long s = _doc.ToFrame(Wave.SelStart), e = _doc.ToFrame(Wave.SelEnd);
            return (s, e - s);
        }
        return (0, _doc.Frames);
    }

    // ================================================================== Busy

    private async Task RunBusyAsync(string text, Func<IProgress<double>, CancellationToken, Task> work)
    {
        if (_busyCts != null) { Ui.Success("Bitte warten", "Es läuft bereits ein Vorgang."); return; }
        _busyCts = new CancellationTokenSource();
        BusyText.Text = text;
        BusyBar.Value = 0;
        BusyBar.IsIndeterminate = true;
        BusyPanel.Visibility = Visibility.Visible;
        var progress = new Progress<double>(v => { BusyBar.IsIndeterminate = false; BusyBar.Value = v * 100; });
        try { await work(progress, _busyCts.Token); }
        catch (Exception ex) { Ui.Error(text, ex); }
        finally
        {
            _busyCts.Dispose();
            _busyCts = null;
            BusyPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelBusy_Click(object sender, RoutedEventArgs e) => _busyCts?.Cancel();

    // ================================================================== Wiedergabe

    private void StartPlayback(double from, double to)
    {
        if (_doc == null) return;
        _previewing = false;
        _playOffset = 0;
        _playEnd = to;
        if (from >= _doc.Duration - 0.01) from = 0;
        _player.Volume = (float)(VolumeSlider.Value / 100);
        _player.Play(_doc.Samples, _doc.Channels, _doc.SampleRate, _doc.ToFrame(from), _doc.ToFrame(to));
        PlayIcon.Glyph = "";
        _timer.Start();
    }

    private void PlayBuffer(float[] data, double offset)
    {
        if (_doc == null) return;
        _previewing = true;
        _playOffset = offset;
        _player.Volume = (float)(VolumeSlider.Value / 100);
        _player.Play(data, _doc.Channels, _doc.SampleRate, 0);
        PlayIcon.Glyph = "";
        _timer.Start();
    }

    private void Timer_Tick(object? sender, object e)
    {
        if (!_player.IsPlaying) return;
        double t = _player.PositionSeconds + _playOffset;
        Wave.SetPlayhead(t, FollowToggle.IsChecked == true);
        PositionText.Text = TimeFormat.Format(t);
    }

    private void OnPlaybackStopped()
    {
        _timer.Stop();
        Wave.SetPlayhead(null, false);
        PlayIcon.Glyph = "";
        PositionText.Text = TimeFormat.Format(Wave.Cursor);
    }

    private void TogglePlay()
    {
        if (_player.IsPlaying)
        {
            double t = _player.PositionSeconds + _playOffset;
            bool wasPreview = _previewing;
            _player.Stop();
            if (!wasPreview) Wave.SetCursor(t, !Wave.HasSelection);
        }
        else if (Wave.HasSelection) StartPlayback(Wave.SelStart, Wave.SelEnd);
        else StartPlayback(Wave.Cursor, Wave.Duration);
    }

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();
    private void Stop_Click(object sender, RoutedEventArgs e) => _player.Stop();
    private void PlaySelection_Click(object sender, RoutedEventArgs e)
    {
        if (Wave.HasSelection) StartPlayback(Wave.SelStart, Wave.SelEnd);
    }
    private void Volume_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => _player.Volume = (float)(e.NewValue / 100);

    // ================================================================== Bearbeiten

    private void Undo_Click(object sender, RoutedEventArgs e) => _doc?.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => _doc?.Redo();

    private void Copy_Click(object sender, RoutedEventArgs e) => CopySelection();

    private bool CopySelection()
    {
        if (_doc == null || !Wave.HasSelection) return false;
        var (s, l) = TargetRange();
        _clipboard = _doc.GetRange(s, l);
        _clipboardChannels = _doc.Channels;
        return true;
    }

    private void Cut_Click(object sender, RoutedEventArgs e)
    {
        if (!CopySelection() || _doc == null) return;
        var (s, l) = TargetRange();
        double at = Wave.SelStart;
        _doc.Replace(s, l, Array.Empty<float>(), "Ausschneiden");
        Wave.SetCursor(at);
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null || _clipboard == null) return;
        var data = AdaptChannels(_clipboard, _clipboardChannels, _doc.Channels);
        if (Wave.HasSelection)
        {
            var (s, l) = TargetRange();
            _doc.Replace(s, l, data, "Einfügen");
        }
        else _doc.Insert(_doc.ToFrame(Wave.Cursor), data);
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelection();

    private void DeleteSelection()
    {
        if (_doc == null || !Wave.HasSelection) return;
        var (s, l) = TargetRange();
        double at = Wave.SelStart;
        _doc.Delete(s, l);
        Wave.SetCursor(at);
    }

    private void Trim_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null || !Wave.HasSelection) return;
        var (s, l) = TargetRange();
        _doc.Trim(s, l);
        Wave.ZoomToFit();
        Wave.SetCursor(0);
    }

    private void Silence_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null || !Wave.HasSelection) return;
        var (s, l) = TargetRange();
        _doc.Silence(s, l);
    }

    private async void InsertSilence_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        var text = await Ui.InputAsync("Stille einfügen", "Dauer in Sekunden", "1.0");
        if (text == null || !TimeFormat.TryParse(text, out var sec) || sec <= 0) return;
        _doc.InsertSilence(_doc.ToFrame(Wave.Cursor), sec);
    }

    private async void InsertFile_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        var file = await Ui.PickOpenFileAsync(AudioIO.SupportedExtensions);
        if (file == null) return;
        await RunBusyAsync("Einfügen", async (p, ct) =>
        {
            var data = await AudioIO.DecodeAsync(file, _doc.SampleRate, _doc.Channels, ct, p);
            _doc.Insert(_doc.ToFrame(Wave.Cursor), data, "Datei einfügen");
        });
    }

    private async void MixFile_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        var file = await Ui.PickOpenFileAsync(AudioIO.SupportedExtensions);
        if (file == null) return;
        var gainText = await Ui.InputAsync("Datei unterlegen", "Lautstärke der unterlegten Datei in dB (z. B. −18 für Hintergrundmusik)", "-18");
        if (gainText == null || !double.TryParse(gainText.Replace(',', '.').Replace('−', '-'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var db)) return;
        await RunBusyAsync("Mischen", async (p, ct) =>
        {
            var data = await AudioIO.DecodeAsync(file, _doc.SampleRate, _doc.Channels, ct, p);
            long start = _doc.ToFrame(Wave.HasSelection ? Wave.SelStart : Wave.Cursor);
            long len = data.LongLength / _doc.Channels;
            if (Wave.HasSelection) len = Math.Min(len, _doc.ToFrame(Wave.SelEnd) - start);
            len = Math.Min(len, _doc.Frames - start);
            if (len <= 0) return;
            var region = _doc.GetRange(start, len);
            float g = (float)Math.Pow(10, db / 20);
            long fadeFrames = Math.Min(len / 4, _doc.SampleRate);
            for (long f = 0; f < len; f++)
            {
                float fade = 1;
                if (f < fadeFrames) fade = (float)f / fadeFrames;
                if (len - f < fadeFrames) fade = Math.Min(fade, (float)(len - f) / fadeFrames);
                for (int c = 0; c < _doc.Channels; c++)
                    region[f * _doc.Channels + c] += data[f * _doc.Channels + c] * g * fade;
            }
            _doc.Replace(start, len, region, "Unterlegen");
        });
    }

    private static float[] AdaptChannels(float[] data, int from, int to)
    {
        if (from == to) return data;
        long frames = data.LongLength / from;
        var o = new float[frames * to];
        for (long f = 0; f < frames; f++)
        {
            if (to == 1) o[f] = (data[f * from] + data[f * from + from - 1]) * 0.5f;
            else for (int c = 0; c < to; c++) o[f * to + c] = data[f * from + Math.Min(c, from - 1)];
        }
        return o;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => Wave.SelectAll();

    private async void ExactSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        var cur = Wave.HasSelection ? $"{TimeFormat.Format(Wave.SelStart)} - {TimeFormat.Format(Wave.SelEnd)}" : $"{TimeFormat.Format(Wave.Cursor)} - {TimeFormat.Format(Wave.Duration)}";
        var text = await Ui.InputAsync("Auswahl festlegen", "Start - Ende (z. B. 01:20.5 - 02:10)", cur);
        if (text == null) return;
        var parts = text.Split(" - ", StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && TimeFormat.TryParse(parts[0], out var a) && TimeFormat.TryParse(parts[1], out var b))
        {
            Wave.SetSelection(a, b);
            Wave.ZoomToSelection();
        }
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Wave.Zoom(1.6);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Wave.Zoom(1 / 1.6);
    private void ZoomSel_Click(object sender, RoutedEventArgs e) => Wave.ZoomToSelection();
    private void ZoomFit_Click(object sender, RoutedEventArgs e) => Wave.ZoomToFit();

    // ================================================================== Tastatur

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot);
        if (focused is TextBox or NumberBox or AutoSuggestBox or PasswordBox or ComboBox) return;
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool handled = true;
        switch (e.Key)
        {
            case VirtualKey.Space: TogglePlay(); break;
            case VirtualKey.Delete: DeleteSelection(); break;
            case VirtualKey.Home: Wave.SetCursor(0); Wave.EnsureVisible(0); break;
            case VirtualKey.End: Wave.SetCursor(Wave.Duration); Wave.EnsureVisible(Wave.Duration); break;
            case VirtualKey.M when !ctrl: AddMarker(); break;
            case VirtualKey.Z when ctrl: _doc?.Undo(); break;
            case VirtualKey.Y when ctrl: _doc?.Redo(); break;
            case VirtualKey.X when ctrl: Cut_Click(this, new RoutedEventArgs()); break;
            case VirtualKey.C when ctrl: CopySelection(); break;
            case VirtualKey.V when ctrl: Paste_Click(this, new RoutedEventArgs()); break;
            case VirtualKey.A when ctrl: Wave.SelectAll(); break;
            case VirtualKey.T when ctrl: Trim_Click(this, new RoutedEventArgs()); break;
            case VirtualKey.O when ctrl: Open_Click(this, new RoutedEventArgs()); break;
            case VirtualKey.S when ctrl: Export_Click(this, new RoutedEventArgs()); break;
            case VirtualKey.Add when ctrl: Wave.Zoom(1.6); break;
            case VirtualKey.Subtract when ctrl: Wave.Zoom(1 / 1.6); break;
            case VirtualKey.Left: Wave.SetCursor(Wave.Cursor - (ctrl ? 5 : 0.5)); Wave.EnsureVisible(Wave.Cursor); break;
            case VirtualKey.Right: Wave.SetCursor(Wave.Cursor + (ctrl ? 5 : 0.5)); Wave.EnsureVisible(Wave.Cursor); break;
            default: handled = false; break;
        }
        e.Handled = handled;
    }

    // ================================================================== Drag & Drop

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Öffnen";
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var file = items.OfType<Windows.Storage.StorageFile>().FirstOrDefault();
        if (file != null) await LoadAsync(file.Path);
    }

    // ================================================================== Seitenleiste

    private void SideSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tag = sender.SelectedItem?.Tag as string;
        FxPanel.Visibility = tag == "fx" ? Visibility.Visible : Visibility.Collapsed;
        TranscriptPanel.Visibility = tag == "tr" ? Visibility.Visible : Visibility.Collapsed;
        MarkerPanel.Visibility = tag == "mk" ? Visibility.Visible : Visibility.Collapsed;
        AnalysisPanel.Visibility = tag == "an" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "tr") FillTranscriptCombos();
    }

    // ================================================================== Marker

    private void AddMarker_Click(object sender, RoutedEventArgs e) => AddMarker();

    private void AddMarker()
    {
        if (_doc == null) return;
        double t = _player.IsPlaying && !_previewing ? _player.PositionSeconds : Wave.Cursor;
        _doc.Markers.Add(new AudioMarker { Time = t, Name = "Kapitel " + (_doc.Markers.Count + 1) });
        _doc.Markers.Sort((a, b) => a.Time.CompareTo(b.Time));
        RebuildMarkers();
        Wave.QueueRender();
    }

    private void RebuildMarkers()
    {
        MarkerList.ItemsSource = _doc?.Markers.ToList();
        Wave.QueueRender();
    }

    private void MarkerTime_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AudioMarker m) { Wave.SetCursor(m.Time); Wave.EnsureVisible(m.Time); }
    }

    private void RemoveMarker_Click(object sender, RoutedEventArgs e)
    {
        if (_doc != null && (sender as FrameworkElement)?.DataContext is AudioMarker m)
        {
            _doc.Markers.Remove(m);
            RebuildMarkers();
        }
    }

    private void CopyChapters_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null || _doc.Markers.Count == 0) return;
        var sb = new StringBuilder();
        if (_doc.Markers[0].Time > 0.5) sb.AppendLine("00:00 Intro");
        foreach (var m in _doc.Markers.OrderBy(m => m.Time)) sb.AppendLine($"{TimeFormat.Format(m.Time, false)} {m.Name}");
        var dp = new DataPackage();
        dp.SetText(sb.ToString());
        Clipboard.SetContent(dp);
        Ui.Success("Kopiert", "Kapitelliste liegt in der Zwischenablage.");
    }

    // ================================================================== Analyse

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        var (s, l) = TargetRange();
        var data = _doc.GetRange(s, l);
        await RunBusyAsync("Messe Lautheit", async (p, ct) =>
        {
            var r = await Task.Run(() => Core.Dsp.Loudness.Measure(data, _doc.Channels, _doc.SampleRate, ct), ct);
            LufsText.Text = $"{r.IntegratedLufs:0.0} LUFS";
            LraText.Text = $"{r.LoudnessRange:0.0} LU";
            PeakText.Text = $"{r.PeakDb:0.0} dBFS";
            RmsText.Text = $"{r.RmsDb:0.0} dBFS";
        });
    }

    // ================================================================== Export

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        var formats = new (string Label, string Key, string Ext)[]
        {
            ("MP3", "mp3", ".mp3"), ("WAV 16 Bit", "wav16", ".wav"), ("WAV 24 Bit", "wav24", ".wav"), ("WAV 32 Bit Float", "wav32f", ".wav"),
            ("FLAC (verlustfrei)", "flac", ".flac"), ("M4A / AAC", "m4a", ".m4a"), ("Opus", "opus", ".opus"), ("Ogg Vorbis", "ogg", ".ogg"),
        };
        var formatBox = new ComboBox { Header = "Format", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var f in formats) formatBox.Items.Add(f.Label);
        formatBox.SelectedIndex = 0;
        var bitrateBox = new ComboBox { Header = "Bitrate", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var b in new[] { 64, 96, 128, 160, 192, 256, 320 }) bitrateBox.Items.Add(b + " kbit/s");
        bitrateBox.SelectedIndex = 4;
        var monoSwitch = new ToggleSwitch { Header = "Als Mono speichern", IsOn = _doc.Channels == 1 };
        var rangeSwitch = new ToggleSwitch { Header = "Nur Auswahl exportieren", IsOn = false, IsEnabled = Wave.HasSelection };
        var chaptersSwitch = new ToggleSwitch { Header = "Marker als Kapitel einbetten", IsOn = _doc.Markers.Count > 0, IsEnabled = _doc.Markers.Count > 0 };
        var title = new TextBox { Header = "Titel", Text = Path.GetFileNameWithoutExtension(_doc.FilePath ?? "Podcast") };
        var artist = new TextBox { Header = "Interpret / Sprecher" };
        var album = new TextBox { Header = "Podcast / Album" };
        var year = new TextBox { Header = "Jahr", Text = DateTime.Now.Year.ToString() };
        var genre = new TextBox { Header = "Genre", Text = "Podcast" };
        var comment = new TextBox { Header = "Beschreibung", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60 };
        string? cover = null;
        var coverButton = new Button { Content = "Cover-Bild wählen …" };
        coverButton.Click += async (_, _) =>
        {
            var c = await Ui.PickOpenFileAsync(".jpg", ".jpeg", ".png");
            if (c != null) { cover = c; coverButton.Content = Path.GetFileName(c); }
        };
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 10, Width = 520 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Add(FrameworkElement el, int row, int col, int span = 1)
        {
            while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(el, row); Grid.SetColumn(el, col); Grid.SetColumnSpan(el, span);
            grid.Children.Add(el);
        }
        Add(formatBox, 0, 0); Add(bitrateBox, 0, 1);
        Add(monoSwitch, 1, 0); Add(rangeSwitch, 1, 1);
        Add(chaptersSwitch, 2, 0, 2);
        Add(title, 3, 0); Add(artist, 3, 1);
        Add(album, 4, 0); Add(year, 4, 1);
        Add(genre, 5, 0); Add(coverButton, 5, 1);
        Add(comment, 6, 0, 2);
        formatBox.SelectionChanged += (_, _) =>
        {
            var k = formats[formatBox.SelectedIndex].Key;
            bitrateBox.IsEnabled = k is "mp3" or "m4a" or "opus" or "ogg";
        };
        var dialog = new ContentDialog
        {
            Title = "Exportieren",
            Content = new ScrollViewer { Content = grid, MaxHeight = 560 },
            PrimaryButtonText = "Speichern unter …",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await Ui.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        var fmt = formats[formatBox.SelectedIndex];
        var path = await Ui.PickSaveFileAsync(title.Text.Length > 0 ? title.Text : "Podcast", (fmt.Label, fmt.Ext));
        if (path == null) return;
        var opt = new AudioExportOptions
        {
            Format = fmt.Key,
            BitrateKbps = new[] { 64, 96, 128, 160, 192, 256, 320 }[Math.Max(0, bitrateBox.SelectedIndex)],
            Mono = monoSwitch.IsOn,
            Title = title.Text, Artist = artist.Text, Album = album.Text, Year = year.Text, Genre = genre.Text, Comment = comment.Text,
            CoverImage = cover,
        };
        long start = 0, len = _doc.Frames;
        if (rangeSwitch.IsOn && Wave.HasSelection) (start, len) = TargetRange();
        if (chaptersSwitch.IsOn)
        {
            double off = _doc.ToSeconds(start), end = _doc.ToSeconds(start + len);
            foreach (var m in _doc.Markers.Where(m => m.Time >= off && m.Time < end))
                opt.Chapters.Add((m.Time - off, m.Name));
            if (opt.Chapters.Count > 0 && opt.Chapters.Min(c => c.Time) > 0.5) opt.Chapters.Insert(0, (0, "Intro"));
        }
        var data = start == 0 && len == _doc.Frames ? _doc.Samples : _doc.GetRange(start, len);
        await RunBusyAsync("Exportiere " + Path.GetFileName(path), async (p, ct) =>
        {
            await AudioIO.EncodeAsync(data, _doc.SampleRate, _doc.Channels, path, opt, p, ct);
            _doc.MarkSaved();
            UpdateUi();
            AppSettings.Current.AddRecent(path);
            Ui.Success("Exportiert", path);
        });
    }

    // ================================================================== Aufnahme

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        var devices = AudioRecorder.Devices();
        if (devices.Count == 0) { await Ui.InfoAsync("Kein Mikrofon", "Es wurde kein Aufnahmegerät gefunden."); return; }
        int sr = _doc?.SampleRate ?? AppSettings.Current.WorkingSampleRate;
        int ch = _doc?.Channels ?? 1;
        var deviceBox = new ComboBox { Header = "Mikrofon", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var d in devices) deviceBox.Items.Add(d);
        deviceBox.SelectedIndex = 0;
        var level = new ProgressBar { Maximum = 100, Height = 8, Margin = new Thickness(0, 8, 0, 0) };
        var timeText = new TextBlock { Text = "00:00", FontSize = 32, FontFamily = new FontFamily("Consolas"), HorizontalAlignment = HorizontalAlignment.Center };
        var recButton = new Button { Content = "● Aufnahme starten", HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var info = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ui.ThemeBrush("TextFillColorSecondaryBrush"),
            Text = _doc == null ? "Die Aufnahme wird als neues Dokument geöffnet." : $"Die Aufnahme wird an der Cursorposition ({TimeFormat.Format(Wave.Cursor)}) eingefügt.",
        };
        var panel = new StackPanel { Spacing = 12, Width = 420, Children = { deviceBox, timeText, level, recButton, info } };
        AudioRecorder? recorder = null;
        float[]? recorded = null;
        var dialog = new ContentDialog
        {
            Title = "Aufnehmen",
            Content = panel,
            PrimaryButtonText = "Übernehmen",
            CloseButtonText = "Verwerfen",
            IsPrimaryButtonEnabled = false,
            DefaultButton = ContentDialogButton.Primary,
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => { if (recorder != null) timeText.Text = TimeFormat.Format(recorder.Seconds, false); };
        recButton.Click += (_, _) =>
        {
            if (recorder == null)
            {
                recorder = new AudioRecorder(sr, ch);
                recorder.Level += v => DispatcherQueue.TryEnqueue(() => level.Value = Math.Min(100, Math.Sqrt(v) * 100));
                try { recorder.Start(deviceBox.SelectedIndex); }
                catch (Exception ex) { recorder = null; info.Text = "Fehler: " + ex.Message; return; }
                recButton.Content = "■ Aufnahme beenden";
                deviceBox.IsEnabled = false;
                dialog.IsPrimaryButtonEnabled = false;
                timer.Start();
            }
            else
            {
                recorded = recorder.Stop();
                recorder = null;
                timer.Stop();
                level.Value = 0;
                recButton.Content = "● Neu aufnehmen";
                deviceBox.IsEnabled = true;
                dialog.IsPrimaryButtonEnabled = recorded.Length > 0;
            }
        };
        var result = await Ui.ShowDialogAsync(dialog);
        timer.Stop();
        if (recorder != null) { recorded = recorder.Stop(); recorder.Dispose(); }
        if (result != ContentDialogResult.Primary || recorded == null || recorded.Length == 0) return;
        if (_doc == null)
        {
            SetDocument(new AudioDocument(recorded, ch, sr));
        }
        else _doc.Insert(_doc.ToFrame(Wave.Cursor), recorded, "Aufnahme");
    }

    // ================================================================== Transkript

    public static Brush WordBackground(bool isFiller) =>
        isFiller ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0xFF, 0x8C, 0x00)) : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private void FillTranscriptCombos()
    {
        var installed = WhisperModels.Installed.ToList();
        var current = (ModelBox.SelectedItem as ComboBoxItem)?.Tag as string ?? AppSettings.Current.WhisperModel;
        ModelBox.Items.Clear();
        foreach (var m in installed) ModelBox.Items.Add(new ComboBoxItem { Content = m.DisplayName, Tag = m.FileName });
        ModelBox.SelectedItem = ModelBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current) ?? ModelBox.Items.FirstOrDefault();
        NoModelInfo.IsOpen = installed.Count == 0;
        TranscribeButton.IsEnabled = installed.Count > 0;
        if (LangBox.Items.Count == 0)
        {
            foreach (var (code, name) in WhisperModels.Languages) LangBox.Items.Add(new ComboBoxItem { Content = name, Tag = code });
            LangBox.SelectedItem = LangBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == AppSettings.Current.WhisperLanguage) ?? LangBox.Items[0];
        }
    }

    private void GoSettings_Click(object sender, RoutedEventArgs e) => App.MainWindow?.Navigate("settings");

    private async void Transcribe_Click(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        if (ModelBox.SelectedItem is not ComboBoxItem { Tag: string modelFile }) return;
        var model = WhisperModels.Find(modelFile);
        if (model == null || !model.IsInstalled) return;
        var lang = (LangBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "de";
        await RunBusyAsync("Transkribiere (lokal) …", async (p, ct) =>
        {
            var samples = _doc.Samples;
            int ch = _doc.Channels, sr = _doc.SampleRate;
            var mono = await Task.Run(() => AudioIO.ToMono16k(samples, sr, ch), ct);
            var s = AppSettings.Current;
            var t = await WhisperTranscriber.TranscribeAsync(mono, new TranscriptionOptions
            {
                ModelPath = model.LocalPath,
                Language = lang,
                Threads = s.WhisperThreads,
                Verbatim = s.WhisperVerbatim,
            }, p, ct);
            t.MarkFillers(s.FillerWords.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            _doc.Transcript = t;
            RebuildTranscript();
            int fillers = t.Words.Count(w => w.IsFiller);
            Ui.Success("Transkript fertig", $"{t.Words.Count} Wörter erkannt, davon {fillers} Füllwörter (orange).");
        });
    }

    private void RebuildTranscript()
    {
        var t = _doc?.Transcript;
        if (t == null || t.Words.Count == 0)
        {
            TranscriptList.ItemsSource = null;
            TranscriptEmpty.Visibility = Visibility.Visible;
            return;
        }
        TranscriptEmpty.Visibility = Visibility.Collapsed;
        TranscriptList.ItemsSource = t.BuildLines();
    }

    private void UpdateWordHighlights()
    {
        Wave.Highlights.Clear();
        if (_doc?.Transcript != null)
            foreach (var w in _doc.Transcript.Words.Where(w => w.IsSelected)) Wave.Highlights.Add((w.Start, w.End));
        Wave.QueueRender();
    }

    private void Word_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.DataContext is TranscriptWord w)
        {
            w.IsSelected = tb.IsChecked == true;
            Wave.SetSelection(w.Start, w.End);
            Wave.EnsureVisible(w.Start);
            UpdateWordHighlights();
        }
    }

    private void Word_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TranscriptWord w)
        {
            Wave.SetCursor(w.Start);
            StartPlayback(w.Start, Wave.Duration);
        }
    }

    private void LineTime_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TranscriptLine line)
        {
            Wave.SetSelection(line.Start, line.End);
            Wave.EnsureVisible(line.Start);
        }
    }

    private void MarkFillers_Click(object sender, RoutedEventArgs e)
    {
        if (_doc?.Transcript == null) return;
        var fillers = AppSettings.Current.FillerWords.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        _doc.Transcript.MarkFillers(fillers);
        int n = 0;
        foreach (var w in _doc.Transcript.Words) { w.IsSelected = w.IsFiller; if (w.IsFiller) n++; }
        UpdateWordHighlights();
        Ui.Success("Füllwörter", $"{n} Füllwörter markiert. Mit „Herausschneiden“ entfernen.");
    }

    private void ClearWordSel_Click(object sender, RoutedEventArgs e)
    {
        if (_doc?.Transcript == null) return;
        foreach (var w in _doc.Transcript.Words) w.IsSelected = false;
        UpdateWordHighlights();
    }

    private void RemoveWords_Click(object sender, RoutedEventArgs e)
    {
        if (_doc?.Transcript == null) return;
        var words = _doc.Transcript.Words;
        var ranges = new List<(double, double)>();
        for (int i = 0; i < words.Count; i++)
        {
            if (!words[i].IsSelected) continue;
            double prevEnd = i > 0 ? words[i - 1].End : 0;
            double nextStart = i + 1 < words.Count ? words[i + 1].Start : _doc.Duration;
            double s = Math.Max(prevEnd, words[i].Start - 0.04);
            double en = Math.Min(nextStart, words[i].End + 0.04);
            ranges.Add((s, en));
        }
        if (ranges.Count == 0) { Ui.Success("Nichts markiert", "Klicke Wörter an oder nutze „Füllwörter markieren“."); return; }
        double removed = ranges.Sum(r => r.Item2 - r.Item1);
        _doc.DeleteRanges(ranges, $"{ranges.Count} Wörter entfernen");
        Ui.Success("Herausgeschnitten", $"{ranges.Count} Wörter entfernt ({removed:0.0} s kürzer).");
    }

    private void Search_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (_doc?.Transcript == null || string.IsNullOrWhiteSpace(args.QueryText)) return;
        var q = Transcript.Normalize(args.QueryText);
        int n = 0;
        TranscriptWord? first = null;
        foreach (var w in _doc.Transcript.Words)
        {
            bool hit = Transcript.Normalize(w.Text).Contains(q);
            w.IsSelected = hit;
            if (hit) { n++; first ??= w; }
        }
        UpdateWordHighlights();
        if (first != null) { Wave.SetCursor(first.Start); Wave.EnsureVisible(first.Start); }
        Ui.Success("Suche", $"{n} Treffer markiert.");
    }

    private async void ExportTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (_doc?.Transcript == null) return;
        var kind = (sender as FrameworkElement)?.Tag as string ?? "srt";
        var ext = kind switch { "vtt" => ".vtt", "srt" => ".srt", _ => ".txt" };
        var path = await Ui.PickSaveFileAsync(Path.GetFileNameWithoutExtension(_doc.FilePath ?? "Transkript"), (kind.ToUpperInvariant(), ext));
        if (path == null) return;
        var text = kind switch
        {
            "srt" => WhisperTranscriber.ToSrt(_doc.Transcript),
            "vtt" => WhisperTranscriber.ToVtt(_doc.Transcript),
            "txt" => WhisperTranscriber.ToText(_doc.Transcript, true),
            _ => WhisperTranscriber.ToText(_doc.Transcript, false),
        };
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
        Ui.Success("Gespeichert", path);
    }
}
