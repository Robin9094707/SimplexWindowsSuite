using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SimplexSuite.Core.Common;
using SimplexSuite.Core.Media;
using SimplexSuite.Core.Video;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;

namespace SimplexSuite.Pages;

public sealed partial class VideoPage : Page, IClosableEditor, IDisposable
{
    private static readonly string[] ImportExtensions =
    {
        ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".wmv", ".mts", ".m2ts", ".mpg", ".mpeg",
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma",
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff",
    };

    private VideoProject _project = VideoProject.CreateDefault();
    private string? _projectPath;
    private bool _dirty;
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private readonly Dictionary<Guid, (string? Thumb, float[]? Peaks)> _mediaCache = new();
    private CancellationTokenSource? _busyCts;
    private CancellationTokenSource? _frameCts;
    private readonly DispatcherTimer _frameDebounce = new() { Interval = TimeSpan.FromMilliseconds(110) };
    private readonly DispatcherTimer _playTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private MediaPlayer? _mediaPlayer;
    private double _playStart;
    private double _playLength;
    private bool _playing;
    private CancellationTokenSource? _playCts;

    public VideoPage()
    {
        InitializeComponent();
        Timeline.Project = _project;
        Timeline.SelectionChanged += (_, _) => BuildInspector();
        Timeline.PlayheadChanged += (_, t) => { StopPlayback(); UpdateTime(); RequestFrame(); };
        Timeline.EditStarting += (_, _) => PushUndo();
        Timeline.Edited += (_, _) => { MarkDirty(); BuildInspector(); RequestFrame(); };
        Timeline.MediaDropped += (_, d) => AddMediaToTimeline(d.MediaId, d.Time, d.Track);
        _frameDebounce.Tick += async (_, _) => { _frameDebounce.Stop(); await RenderFrameAsync(); };
        _playTimer.Tick += PlayTimer_Tick;
        KeyDown += Page_KeyDown;
        Loaded += (_, _) => { BuildInspector(); UpdateTime(); };
        SizeChanged += (_, e) =>
        {
            bool narrow = e.NewSize.Width < 1150;
            MediaColumn.Width = new GridLength(narrow ? 190 : 260);
            InspectorColumn.Width = new GridLength(narrow ? 290 : 360);
        };
        UpdateUndoUi();
    }

    public bool HasUnsavedChanges => _dirty && _project.Tracks.Any(t => t.Clips.Count > 0);
    public void DiscardChanges() => _dirty = false;

    public void Dispose()
    {
        _mediaPlayer?.Dispose();
    }

    // ================================================================== Undo

    private void PushUndo()
    {
        _undo.Push(_project.ToJson());
        if (_undo.Count > 200) { var keep = _undo.Take(150).Reverse().ToList(); _undo.Clear(); foreach (var k in keep) _undo.Push(k); }
        _redo.Clear();
        UpdateUndoUi();
    }

    private void RestoreFrom(string json)
    {
        var sel = Timeline.SelectedClip?.Id;
        _project = VideoProject.FromJson(json);
        AttachCache();
        Timeline.Project = _project;
        if (sel.HasValue && _project.FindClip(sel.Value) != null) Timeline.Select(sel);
        RebuildMediaList();
        BuildInspector();
        MarkDirty();
        RequestFrame();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(_project.ToJson());
        RestoreFrom(_undo.Pop());
        UpdateUndoUi();
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(_project.ToJson());
        RestoreFrom(_redo.Pop());
        UpdateUndoUi();
    }

    private void UpdateUndoUi()
    {
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
    }

    private void MarkDirty()
    {
        _dirty = true;
        ProjectNameText.Text = (_projectPath != null ? Path.GetFileNameWithoutExtension(_projectPath) : "Neues Projekt") + " •";
        UpdateTime();
        StatusText.Text = $"{_project.Width}×{_project.Height} · {_project.Fps:0.##} fps · {_project.Tracks.Sum(t => t.Clips.Count)} Clips · Länge {TimeFormat.Format(_project.Duration)}";
    }

    private void AttachCache()
    {
        foreach (var m in _project.Media)
            if (_mediaCache.TryGetValue(m.Id, out var c)) { m.ThumbnailPath = c.Thumb; m.Peaks = c.Peaks; }
    }

    // ================================================================== Busy

    private async Task RunBusyAsync(string text, Func<IProgress<double>, CancellationToken, Task> work)
    {
        if (_busyCts != null) { Ui.Success("Bitte warten", "Es läuft bereits ein Vorgang."); return; }
        _busyCts = new CancellationTokenSource();
        BusyText.Text = text;
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

    // ================================================================== Medien

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var files = await Ui.PickOpenFilesAsync(true, ImportExtensions);
        if (files.Count > 0) ImportFiles(files);
    }

    public async void ImportFiles(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        bool timelineWasEmpty = !_project.Tracks.Any(t => t.Clips.Count > 0);
        var added = new List<MediaItem>();
        await RunBusyAsync("Importiere Medien", async (p, ct) =>
        {
            int i = 0;
            foreach (var path in list)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var info = await MediaProbe.ProbeAsync(path, ct);
                    var item = new MediaItem
                    {
                        Path = path, Name = Path.GetFileName(path), Kind = info.Kind, Duration = info.Duration,
                        Width = info.Width, Height = info.Height, Fps = info.Fps, HasAudio = info.HasAudio, HasVideo = info.HasVideo,
                    };
                    if (item.Kind != MediaKind.Audio)
                        item.ThumbnailPath = await MediaProbe.ThumbnailAsync(path, Math.Min(1, item.Duration / 3), 320, ct);
                    _mediaCache[item.Id] = (item.ThumbnailPath, null);
                    _project.Media.Add(item);
                    added.Add(item);
                    RebuildMediaList();
                    if (item.HasAudio) _ = LoadPeaksAsync(item);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    App.MainWindow?.ShowError("Import fehlgeschlagen", Path.GetFileName(path) + ": " + ex.Message);
                }
                p.Report(++i / (double)list.Count);
            }
        });
        if (timelineWasEmpty && added.Count > 0)
        {
            // Erstes Video bestimmt das Projektformat
            var first = added.FirstOrDefault(m => m.Kind == MediaKind.Video);
            if (first != null && first.Width > 0)
            {
                _project.Width = first.Width % 2 == 0 ? first.Width : first.Width + 1;
                _project.Height = first.Height % 2 == 0 ? first.Height : first.Height + 1;
                if (first.Fps is > 1 and < 121) _project.Fps = Math.Round(first.Fps, 3);
            }
            PushUndo();
            double t = 0;
            foreach (var m in added) { AddMediaToTimeline(m.Id, t, null, pushUndo: false); t += m.Duration; }
            Timeline.ZoomToFit();
        }
        MarkDirty();
    }

    private async Task LoadPeaksAsync(MediaItem item)
    {
        try
        {
            var peaks = await MediaProbe.PeaksAsync(item.Path, 50);
            item.Peaks = peaks;
            _mediaCache[item.Id] = (item.ThumbnailPath, peaks);
            foreach (var m in _project.Media.Where(m => m.Id == item.Id)) m.Peaks = peaks;
            Timeline.Rebuild();
        }
        catch { }
    }

    private void RebuildMediaList()
    {
        MediaList.Items.Clear();
        foreach (var m in _project.Media)
        {
            var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 6, 0, 6), Tag = m };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            FrameworkElement thumb;
            if (m.ThumbnailPath != null)
                thumb = new Border { CornerRadius = new CornerRadius(4), Height = 42, Background = new SolidColorBrush(Microsoft.UI.Colors.Black), Child = new Image { Source = Ui.LoadImage(m.ThumbnailPath, 144), Stretch = Stretch.UniformToFill } };
            else
                thumb = new Border
                {
                    CornerRadius = new CornerRadius(4), Height = 42, Background = Ui.ThemeBrush("SubtleFillColorSecondaryBrush"),
                    Child = new FontIcon { Glyph = "", FontSize = 18 },
                };
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = m.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            string kind = m.Kind switch { MediaKind.Video => $"Video · {m.Width}×{m.Height}", MediaKind.Audio => "Audio", _ => $"Bild · {m.Width}×{m.Height}" };
            info.Children.Add(new TextBlock
            {
                Text = m.Kind == MediaKind.Image ? kind : $"{kind} · {TimeFormat.Format(m.Duration, false)}",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = Ui.ThemeBrush("TextFillColorSecondaryBrush"),
            });
            Grid.SetColumn(info, 1);
            grid.Children.Add(thumb);
            grid.Children.Add(info);
            var menu = new MenuFlyout();
            var addItem = new MenuFlyoutItem { Text = "Am Abspielkopf einfügen", Icon = Ui.Icon("") };
            addItem.Click += (_, _) => AddMediaToTimeline(m.Id, Timeline.Playhead, null);
            var removeItem = new MenuFlyoutItem { Text = "Aus Projekt entfernen", Icon = Ui.Icon("") };
            removeItem.Click += (_, _) => RemoveMedia(m);
            menu.Items.Add(addItem);
            menu.Items.Add(removeItem);
            grid.ContextFlyout = menu;
            MediaList.Items.Add(grid);
        }
        MediaEmpty.Visibility = _project.Media.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RemoveMedia(MediaItem m)
    {
        PushUndo();
        foreach (var t in _project.Tracks) t.Clips.RemoveAll(c => c.MediaId == m.Id);
        _project.Media.Remove(m);
        RebuildMediaList();
        Timeline.Rebuild();
        MarkDirty();
        RequestFrame();
    }

    private void MediaList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is Grid { Tag: MediaItem m })
        {
            e.Data.SetText("media:" + m.Id);
            e.Data.RequestedOperation = DataPackageOperation.Copy;
        }
    }

    private void MediaList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (MediaList.SelectedItem is Grid { Tag: MediaItem m }) AddMediaToTimeline(m.Id, Timeline.Playhead, null);
    }

    private void AddMediaToTimeline(Guid mediaId, double time, Track? target, bool pushUndo = true)
    {
        var media = _project.FindMedia(mediaId);
        if (media == null) return;
        if (pushUndo) PushUndo();
        double dur = media.Kind == MediaKind.Image ? 5 : media.Duration;
        bool wantVideo = media.Kind != MediaKind.Audio;
        if (target != null && target.Kind == TrackKind.Text) target = null;
        if (target != null && target.Kind == TrackKind.Video && !wantVideo) target = null;
        if (target == null)
        {
            var kind = wantVideo ? TrackKind.Video : TrackKind.Audio;
            // unterste freie Spur des passenden Typs
            target = _project.Tracks.Where(t => t.Kind == kind).Reverse()
                         .FirstOrDefault(t => !t.Clips.Any(c => c.Start < time + dur && c.End > time))
                     ?? AddTrack(kind);
        }
        var clip = new Clip { MediaId = media.Id, Name = media.Name, Start = time, Duration = dur };
        // Eingebettete Tonspur bei Videos: Audio bleibt am Clip (Lautstärke im Inspektor)
        target.Clips.Add(clip);
        target.Clips.Sort((a, b) => a.Start.CompareTo(b.Start));
        Timeline.Rebuild();
        Timeline.Select(clip.Id);
        MarkDirty();
        RequestFrame();
    }

    private Track AddTrack(TrackKind kind)
    {
        int n = _project.Tracks.Count(t => t.Kind == kind) + 1;
        var name = kind switch { TrackKind.Video => "Video ", TrackKind.Audio => "Audio ", _ => "Text " } + n;
        var track = new Track { Name = name, Kind = kind };
        if (kind == TrackKind.Audio) _project.Tracks.Add(track);
        else
        {
            // Neue Bild-/Textspuren oberhalb der bestehenden Videospuren einfügen
            int idx = _project.Tracks.FindIndex(t => t.Kind == kind);
            _project.Tracks.Insert(Math.Max(0, idx), track);
        }
        return track;
    }

    private void AddTrack_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag || !Enum.TryParse<TrackKind>(tag, out var kind)) return;
        PushUndo();
        AddTrack(kind);
        Timeline.Rebuild();
        MarkDirty();
    }

    private void RemoveEmptyTracks_Click(object sender, RoutedEventArgs e)
    {
        PushUndo();
        _project.Tracks.RemoveAll(t => t.Clips.Count == 0);
        Timeline.Rebuild();
        MarkDirty();
    }

    private void AddText_Click(object sender, RoutedEventArgs e)
    {
        PushUndo();
        double t = Timeline.Playhead;
        var track = _project.Tracks.FirstOrDefault(tr => tr.Kind == TrackKind.Text && !tr.Clips.Any(c => c.Start < t + 4 && c.End > t)) ?? AddTrack(TrackKind.Text);
        var clip = new Clip { Start = t, Duration = 4, Text = "Dein Titel", FontSize = Math.Round(_project.Height / 12.0), FadeIn = 0.3, FadeOut = 0.3, ShadowOffset = 3 };
        track.Clips.Add(clip);
        track.Clips.Sort((a, b) => a.Start.CompareTo(b.Start));
        Timeline.Rebuild();
        Timeline.Select(clip.Id);
        MarkDirty();
        RequestFrame();
    }

    // ================================================================== Clip-Befehle

    private void Split_Click(object sender, RoutedEventArgs e) => SplitAtPlayhead();

    private void SplitAtPlayhead()
    {
        double t = Timeline.Playhead;
        var targets = new List<(Track, Clip)>();
        var sel = Timeline.SelectedClip;
        if (sel != null && t > sel.Start && t < sel.End) targets.Add((Timeline.SelectedTrack!, sel));
        else
            foreach (var tr in _project.Tracks.Where(tr => !tr.Locked))
                foreach (var c in tr.Clips.Where(c => t > c.Start + 0.01 && t < c.End - 0.01)) targets.Add((tr, c));
        if (targets.Count == 0) return;
        PushUndo();
        Clip? last = null;
        foreach (var (tr, c) in targets) last = _project.Split(tr, c, t) ?? last;
        Timeline.Rebuild();
        if (last != null) Timeline.Select(last.Id);
        MarkDirty();
    }

    private void DeleteClip_Click(object sender, RoutedEventArgs e) => DeleteSelected(false);

    private void DeleteSelected(bool ripple)
    {
        var clip = Timeline.SelectedClip;
        var track = Timeline.SelectedTrack;
        if (clip == null || track == null) return;
        PushUndo();
        track.Clips.Remove(clip);
        if (ripple)
            foreach (var c in track.Clips.Where(c => c.Start >= clip.End - 1e-6)) c.Start -= clip.Duration;
        Timeline.Select(null);
        Timeline.Rebuild();
        MarkDirty();
        RequestFrame();
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        var clip = Timeline.SelectedClip;
        var track = Timeline.SelectedTrack;
        if (clip == null || track == null) return;
        PushUndo();
        var copy = clip.CloneWithNewId();
        copy.Start = clip.End;
        foreach (var c in track.Clips.Where(c => c.Start >= clip.End - 1e-6)) c.Start += clip.Duration;
        track.Clips.Add(copy);
        track.Clips.Sort((a, b) => a.Start.CompareTo(b.Start));
        Timeline.Rebuild();
        Timeline.Select(copy.Id);
        MarkDirty();
    }

    private void TlZoomIn_Click(object sender, RoutedEventArgs e) => Timeline.PixelsPerSecond *= 1.5;
    private void TlZoomOut_Click(object sender, RoutedEventArgs e) => Timeline.PixelsPerSecond /= 1.5;
    private void TlFit_Click(object sender, RoutedEventArgs e) => Timeline.ZoomToFit();

    // ================================================================== Vorschau

    private double PreviewScale
    {
        get
        {
            var tag = (PreviewQualityBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "540";
            double h = double.Parse(tag);
            return Math.Min(1, h / Math.Max(1, _project.Height));
        }
    }

    private void UpdateTime()
    {
        TimeText.Text = $"{TimeFormat.Format(Timeline.Playhead)} / {TimeFormat.Format(_project.Duration)}";
    }

    private void RequestFrame()
    {
        _frameDebounce.Stop();
        _frameDebounce.Start();
    }

    private async Task RenderFrameAsync()
    {
        if (_playing) return;
        _frameCts?.Cancel();
        var cts = _frameCts = new CancellationTokenSource();
        if (_project.Duration <= 0)
        {
            PreviewImage.Source = null;
            PreviewHint.Visibility = Visibility.Visible;
            PreviewHint.Text = "Importiere Medien, um zu beginnen";
            return;
        }
        PreviewHint.Visibility = Visibility.Collapsed;
        PreviewRing.IsActive = true;
        try
        {
            var snapshot = _project.DeepClone();
            double t = Math.Min(Timeline.Playhead, Math.Max(0, _project.Duration - 0.01));
            double scale = PreviewScale;
            var file = await Task.Run(() => RenderGraphBuilder.RenderFrameAsync(snapshot, t, scale, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || file == null) return;
            PreviewPlayer.Visibility = Visibility.Collapsed;
            PreviewImage.Source = await Ui.LoadImageFreshAsync(file);
            try { Directory.Delete(Path.GetDirectoryName(file)!, true); } catch { }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PreviewHint.Visibility = Visibility.Visible;
            var line = ex.Message.Split('\n').LastOrDefault(l => l.Trim().Length > 0) ?? ex.GetType().Name;
            PreviewHint.Text = "Vorschau nicht möglich: " + line;
            Log(ex);
        }
        finally
        {
            if (ReferenceEquals(cts, _frameCts)) PreviewRing.IsActive = false;
        }
    }

    private static void Log(Exception ex)
    {
        try { File.AppendAllText(Path.Combine(AppPaths.DataRoot, "error.log"), $"[{DateTime.Now:u}] {ex}\n\n"); } catch { }
    }

    private void PrevFrame_Click(object sender, RoutedEventArgs e) => StepFrame(-1);
    private void NextFrame_Click(object sender, RoutedEventArgs e) => StepFrame(1);

    private void StepFrame(int dir)
    {
        StopPlayback();
        Timeline.Playhead = Math.Max(0, Timeline.Playhead + dir / _project.Fps);
        Timeline.EnsurePlayheadVisible();
        UpdateTime();
        RequestFrame();
    }

    private async void Play_Click(object sender, RoutedEventArgs e) => await TogglePlayAsync();

    private async Task TogglePlayAsync()
    {
        if (_playing) { StopPlayback(); RequestFrame(); return; }
        if (_project.Duration <= 0) return;
        double from = Timeline.Playhead >= _project.Duration - 0.05 ? 0 : Timeline.Playhead;
        double length = Math.Min(30, _project.Duration - from);
        _playing = true;
        PlayIcon.Glyph = "";
        PreviewRing.IsActive = true;
        PreviewHint.Visibility = Visibility.Collapsed;
        _playCts = new CancellationTokenSource();
        try
        {
            var snapshot = _project.DeepClone();
            var scale = Math.Min(PreviewScale, 540.0 / Math.Max(1, _project.Height));
            var token = _playCts.Token;
            var file = await Task.Run(() => RenderGraphBuilder.RenderPreviewAsync(snapshot, from, length, scale, null, token), token);
            if (!_playing || token.IsCancellationRequested) return;
            _mediaPlayer ??= new MediaPlayer();
            _mediaPlayer.Source = MediaSource.CreateFromUri(new Uri(file));
            PreviewPlayer.SetMediaPlayer(_mediaPlayer);
            PreviewPlayer.Visibility = Visibility.Visible;
            _playStart = from;
            _playLength = length;
            _mediaPlayer.Play();
            _playTimer.Start();
        }
        catch (OperationCanceledException) { StopPlayback(); }
        catch (Exception ex) { StopPlayback(); Ui.Error("Wiedergabe", ex); }
        finally { PreviewRing.IsActive = false; }
    }

    private void PlayTimer_Tick(object? sender, object e)
    {
        if (_mediaPlayer == null || !_playing) return;
        var session = _mediaPlayer.PlaybackSession;
        double pos = session.Position.TotalSeconds;
        Timeline.Playhead = _playStart + pos;
        Timeline.EnsurePlayheadVisible();
        UpdateTime();
        if (pos >= _playLength - 0.05 || session.PlaybackState == MediaPlaybackState.Paused && pos > 0.2)
        {
            // Nächsten Abschnitt rendern, falls noch Material folgt
            double next = _playStart + _playLength;
            StopPlayback();
            if (next < _project.Duration - 0.1)
            {
                Timeline.Playhead = next;
                _ = TogglePlayAsync();
            }
            else RequestFrame();
        }
    }

    private void StopPlayback()
    {
        _playCts?.Cancel();
        _playTimer.Stop();
        if (_mediaPlayer != null) { try { _mediaPlayer.Pause(); } catch { } }
        _playing = false;
        PlayIcon.Glyph = "";
        PreviewPlayer.Visibility = Visibility.Collapsed;
    }

    // ================================================================== Projekt

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        if (HasUnsavedChanges && !await Ui.ConfirmAsync("Neues Projekt", "Ungespeicherte Änderungen verwerfen?", "Verwerfen")) return;
        StopPlayback();
        _project = VideoProject.CreateDefault();
        _projectPath = null;
        _undo.Clear(); _redo.Clear();
        Timeline.Project = _project;
        Timeline.Playhead = 0;
        RebuildMediaList();
        BuildInspector();
        _dirty = false;
        ProjectNameText.Text = "Neues Projekt";
        UpdateUndoUi();
        RequestFrame();
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var file = await Ui.PickOpenFileAsync(".ssvproj");
        if (file != null) OpenProject(file);
    }

    public async void OpenProject(string path)
    {
        try
        {
            StopPlayback();
            _project = VideoProject.FromJson(await File.ReadAllTextAsync(path));
            _projectPath = path;
            _undo.Clear(); _redo.Clear();
            Timeline.Project = _project;
            RebuildMediaList();
            BuildInspector();
            _dirty = false;
            ProjectNameText.Text = Path.GetFileNameWithoutExtension(path);
            AppSettings.Current.AddRecent(path);
            var missing = _project.Media.Where(m => !File.Exists(m.Path)).Select(m => m.Name).ToList();
            if (missing.Count > 0) App.MainWindow?.ShowError("Fehlende Medien", string.Join(", ", missing));
            foreach (var m in _project.Media.Where(m => File.Exists(m.Path)))
            {
                if (m.Kind != MediaKind.Audio) m.ThumbnailPath = await MediaProbe.ThumbnailAsync(m.Path, Math.Min(1, m.Duration / 3), 320);
                _mediaCache[m.Id] = (m.ThumbnailPath, null);
                if (m.HasAudio) _ = LoadPeaksAsync(m);
            }
            RebuildMediaList();
            Timeline.Rebuild();
            Timeline.ZoomToFit();
            RequestFrame();
            UpdateUndoUi();
        }
        catch (Exception ex) { Ui.Error("Projekt öffnen", ex); }
    }

    private async void SaveProject_Click(object sender, RoutedEventArgs e) => await SaveProjectAsync();

    private async Task SaveProjectAsync()
    {
        var path = _projectPath ?? await Ui.PickSaveFileAsync("Videoprojekt", ("Simplex-Videoprojekt", ".ssvproj"));
        if (path == null) return;
        await File.WriteAllTextAsync(path, _project.ToJson());
        _projectPath = path;
        _dirty = false;
        ProjectNameText.Text = Path.GetFileNameWithoutExtension(path);
        AppSettings.Current.AddRecent(path);
        Ui.Success("Gespeichert", path);
    }

    // ================================================================== Eingabe

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot);
        if (focused is TextBox or NumberBox or AutoSuggestBox or ComboBox or PasswordBox) return;
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool handled = true;
        switch (e.Key)
        {
            case VirtualKey.Space: _ = TogglePlayAsync(); break;
            case VirtualKey.S when !ctrl: SplitAtPlayhead(); break;
            case VirtualKey.S when ctrl: _ = SaveProjectAsync(); break;
            case VirtualKey.Delete: DeleteSelected(shift); break;
            case VirtualKey.Z when ctrl: Undo(); break;
            case VirtualKey.Y when ctrl: Redo(); break;
            case VirtualKey.D when ctrl: Duplicate_Click(this, new RoutedEventArgs()); break;
            case VirtualKey.I when ctrl: Import_Click(this, new RoutedEventArgs()); break;
            case VirtualKey.E when ctrl: Export_Click(this, new RoutedEventArgs()); break;
            case VirtualKey.T when !ctrl: AddText_Click(this, new RoutedEventArgs()); break;
            case VirtualKey.Left: StepFrame(shift ? -(int)Math.Round(_project.Fps) : -1); break;
            case VirtualKey.Right: StepFrame(shift ? (int)Math.Round(_project.Fps) : 1); break;
            case VirtualKey.Home: StopPlayback(); Timeline.Playhead = 0; Timeline.EnsurePlayheadVisible(); UpdateTime(); RequestFrame(); break;
            case VirtualKey.End: StopPlayback(); Timeline.Playhead = _project.Duration; Timeline.EnsurePlayheadVisible(); UpdateTime(); RequestFrame(); break;
            default: handled = false; break;
        }
        e.Handled = handled;
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Importieren";
        }
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var files = items.OfType<Windows.Storage.StorageFile>().Select(f => f.Path).ToList();
        var proj = files.FirstOrDefault(f => f.EndsWith(".ssvproj", StringComparison.OrdinalIgnoreCase));
        if (proj != null) { OpenProject(proj); return; }
        if (files.Count > 0) ImportFiles(files);
    }
}
