using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SimplexSuite.Core.Common;
using SimplexSuite.Core.Media;
using SimplexSuite.Core.Video;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace SimplexSuite.Controls;

/// <summary>Mehrspur-Timeline mit Ziehen, Trimmen, Einrasten und Spurwechsel.</summary>
public sealed partial class TimelineView : UserControl
{
    public const double RulerHeight = 28;
    private const double EdgePx = 8;
    private VideoProject? _project;
    private double _pps = 60;
    private double _playhead;
    private Guid? _selected;

    private enum DragMode { None, Move, TrimLeft, TrimRight, Playhead }
    private DragMode _drag;
    private Clip? _dragClip;
    private Track? _dragTrack;
    private Border? _dragElement;
    private double _dragStartX, _dragStartY;
    private double _origStart, _origIn, _origDuration;
    private bool _dragMoved;
    private Track? _dropTrack;
    private readonly Dictionary<Guid, Border> _clipElements = new();

    public event EventHandler? SelectionChanged;
    public event EventHandler<double>? PlayheadChanged;
    public event EventHandler? EditStarting;
    public event EventHandler? Edited;
    public event EventHandler<(Guid MediaId, double Time, Track? Track)>? MediaDropped;

    public TimelineView()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => Rebuild();
    }

    public VideoProject? Project
    {
        get => _project;
        set { _project = value; _selected = null; Rebuild(); }
    }

    public double PixelsPerSecond
    {
        get => _pps;
        set { _pps = Math.Clamp(value, 2, 800); Rebuild(); }
    }

    public Clip? SelectedClip => _selected.HasValue ? _project?.FindClip(_selected.Value)?.Clip : null;
    public Track? SelectedTrack => _selected.HasValue ? _project?.FindClip(_selected.Value)?.Track : null;

    public void Select(Guid? id)
    {
        _selected = id;
        foreach (var (cid, el) in _clipElements) ApplySelectionVisual(el, cid == id);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public double Playhead
    {
        get => _playhead;
        set { _playhead = Math.Max(0, value); UpdatePlayhead(); }
    }

    public void EnsurePlayheadVisible()
    {
        double x = _playhead * _pps;
        double vw = MainScroll.ViewportWidth;
        if (x < MainScroll.HorizontalOffset || x > MainScroll.HorizontalOffset + vw * 0.95)
            MainScroll.ChangeView(Math.Max(0, x - vw * 0.1), null, null, true);
    }

    public void ZoomToFit()
    {
        if (_project == null) return;
        double d = Math.Max(10, _project.Duration);
        PixelsPerSecond = Math.Max(2, (MainScroll.ActualWidth - 40) / d);
    }

    private static double TrackHeight(Track t) => t.Kind switch { TrackKind.Text => 40, TrackKind.Audio => 54, _ => 64 };

    private double TrackTop(int index)
    {
        double y = RulerHeight;
        for (int i = 0; i < index; i++) y += TrackHeight(_project!.Tracks[i]);
        return y;
    }

    private int TrackAt(double y)
    {
        if (_project == null) return -1;
        double top = RulerHeight;
        for (int i = 0; i < _project.Tracks.Count; i++)
        {
            double h = TrackHeight(_project.Tracks[i]);
            if (y >= top && y < top + h) return i;
            top += h;
        }
        return -1;
    }

    // ------------------------------------------------------------------ Aufbau

    public void Rebuild()
    {
        RulerLayer.Children.Clear();
        TrackLayer.Children.Clear();
        ClipLayer.Children.Clear();
        HeaderPanel.Children.Clear();
        _clipElements.Clear();
        if (_project == null) return;
        double duration = Math.Max(_project.Duration + 30, (MainScroll.ActualWidth + 200) / _pps);
        double width = duration * _pps;
        double height = TrackTop(_project.Tracks.Count) + 20;
        Surface.Width = width;
        Surface.Height = height;

        // Lineal
        HeaderPanel.Children.Add(new Border { Height = RulerHeight });
        BuildRuler(duration);

        var stripe = Ui.ThemeBrush("SubtleFillColorSecondaryBrush");
        var divider = Ui.ThemeBrush("DividerStrokeColorDefaultBrush");
        for (int i = 0; i < _project.Tracks.Count; i++)
        {
            var track = _project.Tracks[i];
            double top = TrackTop(i), h = TrackHeight(track);
            var bg = new Rectangle { Width = width, Height = h, Fill = i % 2 == 0 ? stripe : new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            Canvas.SetTop(bg, top);
            TrackLayer.Children.Add(bg);
            var line = new Rectangle { Width = width, Height = 1, Fill = divider };
            Canvas.SetTop(line, top + h - 1);
            TrackLayer.Children.Add(line);
            HeaderPanel.Children.Add(BuildHeader(track, h));
            foreach (var clip in track.Clips) ClipLayer.Children.Add(BuildClip(track, clip, top, h));
        }
        UpdatePlayhead();
    }

    private void BuildRuler(double duration)
    {
        double[] steps = { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
        double step = steps.FirstOrDefault(s => s * _pps >= 80, 600);
        var fg = Ui.ThemeBrush("TextFillColorSecondaryBrush");
        var tick = Ui.ThemeBrush("ControlStrongStrokeColorDefaultBrush");
        for (double t = 0; t <= duration; t += step)
        {
            double x = t * _pps;
            var r = new Rectangle { Width = 1, Height = 10, Fill = tick };
            Canvas.SetLeft(r, x); Canvas.SetTop(r, RulerHeight - 10);
            RulerLayer.Children.Add(r);
            var tb = new TextBlock { Text = TimeFormat.Format(t, false), FontSize = 11, Foreground = fg };
            Canvas.SetLeft(tb, x + 4); Canvas.SetTop(tb, 3);
            RulerLayer.Children.Add(tb);
            for (int k = 1; k < 4; k++)
            {
                var m = new Rectangle { Width = 1, Height = 5, Fill = tick, Opacity = 0.6 };
                Canvas.SetLeft(m, x + k * step / 4 * _pps); Canvas.SetTop(m, RulerHeight - 5);
                RulerLayer.Children.Add(m);
            }
        }
        var bottom = new Rectangle { Width = duration * _pps, Height = 1, Fill = Ui.ThemeBrush("DividerStrokeColorDefaultBrush") };
        Canvas.SetTop(bottom, RulerHeight - 1);
        RulerLayer.Children.Add(bottom);
    }

    private FrameworkElement BuildHeader(Track track, double h)
    {
        var grid = new Grid { Height = h, Padding = new Thickness(12, 0, 6, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new FontIcon
        {
            Glyph = track.Kind switch { TrackKind.Text => "", TrackKind.Audio => "", _ => "" },
            FontSize = 14,
            Foreground = Ui.ThemeBrush("TextFillColorSecondaryBrush"),
            Margin = new Thickness(0, 0, 8, 0),
        };
        var name = new TextBlock { Text = track.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 1);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(buttons, 2);
        if (track.Kind != TrackKind.Audio)
        {
            var eye = SmallToggle(track.Hidden ? "" : "", track.Hidden, "Ausblenden");
            eye.Click += (_, _) => { EditStarting?.Invoke(this, EventArgs.Empty); track.Hidden = eye.IsChecked == true; Rebuild(); Edited?.Invoke(this, EventArgs.Empty); };
            buttons.Children.Add(eye);
        }
        if (track.Kind != TrackKind.Text)
        {
            var mute = SmallToggle(track.Muted ? "" : "", track.Muted, "Stummschalten");
            mute.Click += (_, _) => { EditStarting?.Invoke(this, EventArgs.Empty); track.Muted = mute.IsChecked == true; Rebuild(); Edited?.Invoke(this, EventArgs.Empty); };
            buttons.Children.Add(mute);
        }
        var lockB = SmallToggle(track.Locked ? "" : "", track.Locked, "Sperren");
        lockB.Click += (_, _) => { track.Locked = lockB.IsChecked == true; Rebuild(); };
        buttons.Children.Add(lockB);
        grid.Children.Add(icon);
        grid.Children.Add(name);
        grid.Children.Add(buttons);
        return grid;
    }

    private static ToggleButton SmallToggle(string glyph, bool isChecked, string tip)
    {
        var b = new ToggleButton
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            IsChecked = isChecked,
            Padding = new Thickness(5),
            MinWidth = 0,
            MinHeight = 0,
            Margin = new Thickness(1, 0, 1, 0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        ToolTipService.SetToolTip(b, tip);
        return b;
    }

    private Windows.UI.Color ClipColor(Track t)
    {
        var accent = Ui.AccentColor;
        return t.Kind switch
        {
            TrackKind.Audio => Windows.UI.Color.FromArgb(255, 16, 137, 62),
            TrackKind.Text => Windows.UI.Color.FromArgb(255, 136, 23, 152),
            _ => accent,
        };
    }

    private Border BuildClip(Track track, Clip clip, double top, double h)
    {
        var media = _project!.FindMedia(clip.MediaId);
        var color = ClipColor(track);
        var border = new Border
        {
            Height = h - 6,
            Width = Math.Max(4, clip.Duration * _pps),
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(track.Hidden || track.Muted ? (byte)90 : (byte)220, color.R, color.G, color.B)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(color.R * 0.6), (byte)(color.G * 0.6), (byte)(color.B * 0.6))),
            Tag = clip.Id,
        };
        Canvas.SetLeft(border, clip.Start * _pps);
        Canvas.SetTop(border, top + 3);
        var grid = new Grid { IsHitTestVisible = false };
        if (track.Kind == TrackKind.Audio || (media?.Kind == MediaKind.Video && track.Kind == TrackKind.Video))
        {
            if (media?.Peaks is { Length: > 0 } peaks && track.Kind == TrackKind.Audio)
                grid.Children.Add(BuildWave(peaks, clip, border.Width, h - 8));
        }
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Padding = new Thickness(6, 3, 6, 3) };
        if (media?.ThumbnailPath != null && track.Kind == TrackKind.Video && border.Width > 60)
        {
            content.Children.Add(new Image { Source = Ui.LoadImage(media.ThumbnailPath, 160), Height = h - 14, Stretch = Stretch.Uniform });
        }
        string label = track.Kind == TrackKind.Text ? (clip.Text.Split('\n').FirstOrDefault() ?? "") : (media?.Name ?? clip.Name);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), TextTrimming = TextTrimming.CharacterEllipsis });
        var info = new List<string>();
        if (Math.Abs(clip.Speed - 1) > 1e-3) info.Add($"{clip.Speed:0.##}×");
        if (clip.FadeIn > 0 || clip.FadeOut > 0) info.Add("Blende");
        if (clip.ChromaKey) info.Add("Key");
        if (info.Count > 0 && h > 50)
            stack.Children.Add(new TextBlock { Text = string.Join(" · ", info), FontSize = 10, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 255, 255, 255)) });
        content.Children.Add(stack);
        grid.Children.Add(content);
        // Griffe zum Trimmen
        var leftHandle = new Rectangle { Width = 4, HorizontalAlignment = HorizontalAlignment.Left, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 255, 255, 255)), RadiusX = 2, RadiusY = 2, Margin = new Thickness(1, 6, 0, 6) };
        var rightHandle = new Rectangle { Width = 4, HorizontalAlignment = HorizontalAlignment.Right, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 255, 255, 255)), RadiusX = 2, RadiusY = 2, Margin = new Thickness(0, 6, 1, 6) };
        grid.Children.Add(leftHandle);
        grid.Children.Add(rightHandle);
        border.Child = grid;
        ApplySelectionVisual(border, clip.Id == _selected);
        border.PointerPressed += Clip_PointerPressed;
        border.PointerMoved += Clip_PointerHover;
        _clipElements[clip.Id] = border;
        return border;
    }

    private static Microsoft.UI.Xaml.Shapes.Path BuildWave(float[] peaks, Clip clip, double width, double height)
    {
        var fig = new PathFigure { StartPoint = new Windows.Foundation.Point(0, height / 2), IsClosed = true, IsFilled = true };
        int n = Math.Max(2, (int)(width / 2));
        var top = new List<Windows.Foundation.Point>();
        for (int i = 0; i <= n; i++)
        {
            double t = clip.In + (double)i / n * clip.Duration * clip.Speed;
            int idx = Math.Clamp((int)(t * 50), 0, peaks.Length - 1);
            double v = Math.Min(1, peaks[idx] * clip.Volume);
            top.Add(new Windows.Foundation.Point((double)i / n * width, height / 2 - v * height / 2 * 0.9));
        }
        var seg = new PolyLineSegment();
        foreach (var p in top) seg.Points.Add(p);
        for (int i = top.Count - 1; i >= 0; i--) seg.Points.Add(new Windows.Foundation.Point(top[i].X, height - top[i].Y));
        fig.Segments.Add(seg);
        var pg = new PathGeometry();
        pg.Figures.Add(fig);
        return new Microsoft.UI.Xaml.Shapes.Path { Data = pg, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(110, 255, 255, 255)), Margin = new Thickness(0, 1, 0, 0) };
    }

    private static void ApplySelectionVisual(Border b, bool selected)
    {
        b.BorderThickness = new Thickness(selected ? 2 : 1);
        if (selected) b.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.White);
        else if (b.Background is SolidColorBrush sb)
            b.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(sb.Color.R * 0.6), (byte)(sb.Color.G * 0.6), (byte)(sb.Color.B * 0.6)));
    }

    private void UpdatePlayhead()
    {
        Canvas.SetLeft(Playhead, _playhead * _pps - 1);
        Canvas.SetTop(Playhead, RulerHeight - 8);
        if (Playhead.Children[0] is Rectangle r) r.Height = Math.Max(0, Surface.Height - RulerHeight + 8);
    }

    // ------------------------------------------------------------------ Interaktion

    private void Clip_PointerHover(object sender, PointerRoutedEventArgs e)
    {
        if (_drag != DragMode.None || sender is not Border b) return;
        double x = e.GetCurrentPoint(b).Position.X;
        ProtectedCursor = InputSystemCursor.Create(x < EdgePx || x > b.Width - EdgePx ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.SizeAll);
    }

    private void Clip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_project == null || sender is not Border b || b.Tag is not Guid id) return;
        var found = _project.FindClip(id);
        if (found == null) return;
        Focus(FocusState.Pointer);
        Select(id);
        var (track, clip) = found.Value;
        e.Handled = true;
        if (track.Locked) return;
        var pos = e.GetCurrentPoint(Surface).Position;
        double localX = e.GetCurrentPoint(b).Position.X;
        _drag = localX < EdgePx ? DragMode.TrimLeft : localX > b.Width - EdgePx ? DragMode.TrimRight : DragMode.Move;
        _dragClip = clip;
        _dragTrack = track;
        _dragElement = b;
        _dragStartX = pos.X;
        _dragStartY = pos.Y;
        _origStart = clip.Start;
        _origIn = clip.In;
        _origDuration = clip.Duration;
        _dragMoved = false;
        _dropTrack = track;
        Surface.CapturePointer(e.Pointer);
    }

    private void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled) return;
        Focus(FocusState.Pointer);
        var pos = e.GetCurrentPoint(Surface).Position;
        _drag = DragMode.Playhead;
        Surface.CapturePointer(e.Pointer);
        if (pos.Y > RulerHeight) Select(null);
        SetPlayheadFromX(pos.X);
    }

    private void SetPlayheadFromX(double x)
    {
        _playhead = Math.Max(0, x / _pps);
        UpdatePlayhead();
        PlayheadChanged?.Invoke(this, _playhead);
    }

    private IEnumerable<double> SnapTargets()
    {
        yield return 0;
        yield return _playhead;
        foreach (var t in _project!.Tracks)
            foreach (var c in t.Clips)
            {
                if (c == _dragClip) continue;
                yield return c.Start;
                yield return c.End;
            }
    }

    private double Snap(double t, out double? snapped)
    {
        snapped = null;
        double best = 10 / _pps;
        double result = t;
        foreach (var s in SnapTargets())
        {
            double d = Math.Abs(s - t);
            if (d < best) { best = d; result = s; snapped = s; }
        }
        return result;
    }

    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == DragMode.None) return;
        var pos = e.GetCurrentPoint(Surface).Position;
        if (_drag == DragMode.Playhead) { SetPlayheadFromX(pos.X); return; }
        if (_dragClip == null || _dragElement == null || _project == null) return;
        double dx = pos.X - _dragStartX;
        if (!_dragMoved && Math.Abs(dx) < 3 && Math.Abs(pos.Y - _dragStartY) < 3) return;
        if (!_dragMoved) { _dragMoved = true; EditStarting?.Invoke(this, EventArgs.Empty); }
        double dt = dx / _pps;
        var media = _project.FindMedia(_dragClip.MediaId);
        double maxSrc = media != null && media.Kind != MediaKind.Image ? media.Duration : double.MaxValue;
        double? snapped = null;
        switch (_drag)
        {
            case DragMode.Move:
            {
                double start = Math.Max(0, _origStart + dt);
                double s1 = Snap(start, out var sn1);
                double s2 = Snap(start + _origDuration, out var sn2) - _origDuration;
                if (sn1.HasValue) { start = s1; snapped = sn1; }
                else if (sn2.HasValue) { start = Math.Max(0, s2); snapped = sn2; }
                _dragClip.Start = start;
                // Spurwechsel
                int ti = TrackAt(pos.Y);
                if (ti >= 0 && Compatible(_project.Tracks[ti], media, _dragTrack!)) _dropTrack = _project.Tracks[ti];
                Canvas.SetTop(_dragElement, TrackTop(_project.Tracks.IndexOf(_dropTrack!)) + 3);
                break;
            }
            case DragMode.TrimLeft:
            {
                double start = Snap(_origStart + dt, out snapped);
                double minStart = media != null && media.Kind != MediaKind.Image ? _origStart - _origIn / _dragClip.Speed : 0;
                start = Math.Clamp(start, Math.Max(0, minStart), _origStart + _origDuration - 0.1);
                double delta = start - _origStart;
                _dragClip.Start = start;
                _dragClip.In = Math.Max(0, _origIn + delta * _dragClip.Speed);
                _dragClip.Duration = _origDuration - delta;
                break;
            }
            case DragMode.TrimRight:
            {
                double end = Snap(_origStart + _origDuration + dt, out snapped);
                double maxDur = maxSrc == double.MaxValue ? double.MaxValue : (maxSrc - _dragClip.In) / _dragClip.Speed;
                _dragClip.Duration = Math.Clamp(end - _origStart, 0.1, maxDur);
                break;
            }
        }
        Canvas.SetLeft(_dragElement, _dragClip.Start * _pps);
        _dragElement.Width = Math.Max(4, _dragClip.Duration * _pps);
        if (snapped.HasValue)
        {
            SnapLine.Visibility = Visibility.Visible;
            SnapLine.Height = Surface.Height;
            Canvas.SetLeft(SnapLine, snapped.Value * _pps);
        }
        else SnapLine.Visibility = Visibility.Collapsed;
    }

    private static bool Compatible(Track target, MediaItem? media, Track source)
    {
        if (target.Kind == source.Kind) return true;
        if (media == null) return false;
        return target.Kind switch
        {
            TrackKind.Video => media.Kind != MediaKind.Audio,
            TrackKind.Audio => media.HasAudio,
            _ => false,
        };
    }

    private void Surface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        Surface.ReleasePointerCaptures();
        SnapLine.Visibility = Visibility.Collapsed;
        var mode = _drag;
        _drag = DragMode.None;
        if (mode is DragMode.None or DragMode.Playhead) return;
        if (_dragMoved && _dragClip != null && _project != null)
        {
            if (_dropTrack != null && _dropTrack != _dragTrack)
            {
                _dragTrack!.Clips.Remove(_dragClip);
                _dropTrack.Clips.Add(_dragClip);
            }
            ResolveOverlaps(_dropTrack ?? _dragTrack!, _dragClip);
            foreach (var t in _project.Tracks) t.Clips.Sort((a, b) => a.Start.CompareTo(b.Start));
            Rebuild();
            Edited?.Invoke(this, EventArgs.Empty);
        }
        _dragClip = null;
        _dragElement = null;
    }

    /// <summary>Überlappungen auf derselben Spur werden durch Kürzen/Verschieben der anderen Clips aufgelöst (Überschreiben).</summary>
    private static void ResolveOverlaps(Track track, Clip moved)
    {
        foreach (var c in track.Clips.ToList())
        {
            if (c == moved) continue;
            if (c.End <= moved.Start + 1e-6 || c.Start >= moved.End - 1e-6) continue;
            if (c.Start >= moved.Start && c.End <= moved.End) { track.Clips.Remove(c); continue; }
            if (c.Start < moved.Start && c.End > moved.End)
            {
                // Clip wird geteilt
                var right = c.CloneWithNewId();
                double cut = moved.End - c.Start;
                right.Start = moved.End;
                right.In = c.In + cut * c.Speed;
                right.Duration = c.Duration - cut;
                c.Duration = moved.Start - c.Start;
                track.Clips.Add(right);
                continue;
            }
            if (c.Start < moved.Start) c.Duration = moved.Start - c.Start;
            else
            {
                double cut = moved.End - c.Start;
                c.Start = moved.End;
                c.In += cut * c.Speed;
                c.Duration -= cut;
            }
        }
    }

    private void Surface_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(MainScroll);
        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)) return;
        double mouseX = pt.Position.X;
        double t = (MainScroll.HorizontalOffset + mouseX) / _pps;
        PixelsPerSecond = _pps * (pt.Properties.MouseWheelDelta > 0 ? 1.2 : 1 / 1.2);
        MainScroll.UpdateLayout();
        MainScroll.ChangeView(Math.Max(0, t * _pps - mouseX), null, null, true);
        e.Handled = true;
    }

    private void MainScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        HeaderScroll.ChangeView(null, MainScroll.VerticalOffset, null, true);
    }

    private void Surface_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Zur Timeline hinzufügen";
    }

    private async void Surface_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.Text)) return;
        var text = await e.DataView.GetTextAsync();
        if (!text.StartsWith("media:") || !Guid.TryParse(text[6..], out var id)) return;
        var pos = e.GetPosition(Surface);
        int ti = TrackAt(pos.Y);
        MediaDropped?.Invoke(this, (id, Math.Max(0, pos.X / _pps), ti >= 0 ? _project?.Tracks[ti] : null));
    }
}
