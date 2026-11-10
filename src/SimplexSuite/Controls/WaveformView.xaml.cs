using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using SimplexSuite.Core.Audio;
using SimplexSuite.Core.Common;
using Windows.System;
using Windows.UI.Core;

namespace SimplexSuite.Controls;

/// <summary>Schnelle Wellenform-Darstellung mit Zoom, Auswahl, Markern und Abspielkopf.</summary>
public sealed partial class WaveformView : UserControl
{
    private const int Block = 256;
    private AudioDocument? _doc;
    private float[] _min = Array.Empty<float>(), _max = Array.Empty<float>();
    private WriteableBitmap? _bitmap;
    private byte[] _pixels = Array.Empty<byte>();
    private double _viewStart;       // Sekunden
    private double _spp = 0.01;      // Sekunden pro DIP
    private bool _dragging;
    private double _anchor;
    private bool _updatingScroll;
    private bool _renderQueued;
    private double _verticalZoom = 1;

    public event EventHandler? SelectionChanged;
    public event EventHandler? CursorMoved;
    public event EventHandler? PlayRequested;

    public double Cursor { get; private set; }
    public double SelStart { get; private set; }
    public double SelEnd { get; private set; }
    public bool HasSelection => SelEnd - SelStart > 0.0005;
    public double Duration => _doc?.Duration ?? 0;
    public IList<AudioMarker>? Markers { get; set; }
    public List<(double Start, double End)> Highlights { get; } = new();

    public WaveformView()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => QueueRender();
    }

    public AudioDocument? Document
    {
        get => _doc;
        set
        {
            _doc = value;
            EmptyText.Visibility = value == null ? Visibility.Visible : Visibility.Collapsed;
            Cursor = 0; SelStart = SelEnd = 0;
            _ = RebuildSummaryAsync(0, true);
        }
    }

    public double VerticalZoom
    {
        get => _verticalZoom;
        set { _verticalZoom = Math.Clamp(value, 1, 16); QueueRender(); }
    }

    // ------------------------------------------------------------------ Peaks

    public Task RefreshAsync(long fromFrame = 0) => RebuildSummaryAsync(fromFrame, false);

    private async Task RebuildSummaryAsync(long fromFrame, bool fit)
    {
        var doc = _doc;
        if (doc == null) { QueueRender(); return; }
        var samples = doc.Samples;
        int ch = doc.Channels;
        var oldMin = _min;
        var oldMax = _max;
        var (mn, mx) = await Task.Run(() =>
        {
            long frames = samples.LongLength / ch;
            long bins = (frames + Block - 1) / Block;
            var min = new float[bins * ch];
            var max = new float[bins * ch];
            long firstBin = Math.Max(0, Math.Min(fromFrame / Block, bins));
            if (firstBin > 0 && oldMin.LongLength >= firstBin * ch)
            {
                Array.Copy(oldMin, min, firstBin * ch);
                Array.Copy(oldMax, max, firstBin * ch);
            }
            else firstBin = 0;
            Parallel.For(firstBin, bins, b =>
            {
                long s = b * Block, e = Math.Min(frames, s + Block);
                for (int c = 0; c < ch; c++)
                {
                    float lo = 0, hi = 0;
                    for (long f = s; f < e; f++)
                    {
                        float v = samples[f * ch + c];
                        if (v < lo) lo = v;
                        if (v > hi) hi = v;
                    }
                    min[b * ch + c] = lo;
                    max[b * ch + c] = hi;
                }
            });
            return (min, max);
        });
        if (!ReferenceEquals(doc, _doc)) return;
        _min = mn; _max = mx;
        if (fit) ZoomToFit();
        else
        {
            ClampView();
            UpdateScrollBar();
            QueueRender();
        }
    }

    // ------------------------------------------------------------------ Ansicht

    private double ViewWidth => Math.Max(1, WaveHost.ActualWidth);
    public double ViewStart => _viewStart;
    public double ViewEnd => _viewStart + ViewWidth * _spp;

    public void ZoomToFit()
    {
        if (_doc == null || ViewWidth < 2) { QueueRender(); return; }
        _spp = Math.Max(1.0 / _doc.SampleRate / 8, _doc.Duration / ViewWidth);
        _viewStart = 0;
        UpdateScrollBar();
        QueueRender();
    }

    public void Zoom(double factor, double? aroundTime = null)
    {
        if (_doc == null) return;
        double t = aroundTime ?? (HasSelection ? (SelStart + SelEnd) / 2 : Cursor);
        double xRatio = Math.Clamp((t - _viewStart) / (ViewWidth * _spp), 0, 1);
        double minSpp = 1.0 / _doc.SampleRate / 8;
        double maxSpp = Math.Max(minSpp, _doc.Duration / ViewWidth * 1.05);
        _spp = Math.Clamp(_spp / factor, minSpp, maxSpp);
        _viewStart = t - xRatio * ViewWidth * _spp;
        ClampView();
        UpdateScrollBar();
        QueueRender();
    }

    public void ZoomToSelection()
    {
        if (!HasSelection || _doc == null) return;
        double len = SelEnd - SelStart;
        _spp = Math.Max(1.0 / _doc.SampleRate / 8, len * 1.1 / ViewWidth);
        _viewStart = SelStart - len * 0.05;
        ClampView();
        UpdateScrollBar();
        QueueRender();
    }

    public void EnsureVisible(double t)
    {
        double w = ViewWidth * _spp;
        if (t < _viewStart || t > _viewStart + w)
        {
            _viewStart = t - w * 0.1;
            ClampView();
            UpdateScrollBar();
            QueueRender();
        }
    }

    private void ClampView()
    {
        double w = ViewWidth * _spp;
        _viewStart = Math.Clamp(_viewStart, 0, Math.Max(0, Duration - w));
    }

    private void UpdateScrollBar()
    {
        _updatingScroll = true;
        double w = ViewWidth * _spp;
        HScroll.Minimum = 0;
        HScroll.Maximum = Math.Max(0, Duration - w);
        HScroll.ViewportSize = w;
        HScroll.LargeChange = w * 0.9;
        HScroll.SmallChange = w * 0.1;
        HScroll.Value = _viewStart;
        HScroll.Visibility = HScroll.Maximum > w * 0.01 ? Visibility.Visible : Visibility.Collapsed;
        _updatingScroll = false;
    }

    private void HScroll_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingScroll) return;
        _viewStart = e.NewValue;
        QueueRender();
    }

    // ------------------------------------------------------------------ Auswahl / Cursor

    public void SetSelection(double start, double end, bool notify = true)
    {
        if (end < start) (start, end) = (end, start);
        SelStart = Math.Clamp(start, 0, Duration);
        SelEnd = Math.Clamp(end, 0, Duration);
        Cursor = SelStart;
        UpdateOverlay();
        if (notify) { SelectionChanged?.Invoke(this, EventArgs.Empty); CursorMoved?.Invoke(this, EventArgs.Empty); }
    }

    public void SetCursor(double t, bool clearSelection = true, bool notify = true)
    {
        Cursor = Math.Clamp(t, 0, Duration);
        if (clearSelection) { SelStart = SelEnd = Cursor; }
        UpdateOverlay();
        if (notify)
        {
            if (clearSelection) SelectionChanged?.Invoke(this, EventArgs.Empty);
            CursorMoved?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SelectAll() => SetSelection(0, Duration);

    private double? _playhead;
    public void SetPlayhead(double? t, bool follow)
    {
        _playhead = t;
        if (t.HasValue && follow)
        {
            double w = ViewWidth * _spp;
            if (t.Value > _viewStart + w * 0.95 || t.Value < _viewStart)
            {
                _viewStart = t.Value - w * 0.05;
                ClampView();
                UpdateScrollBar();
                QueueRender();
                return;
            }
        }
        UpdateOverlay();
    }

    private double TimeToX(double t) => (t - _viewStart) / _spp;
    private double XToTime(double x) => Math.Clamp(_viewStart + x * _spp, 0, Duration);

    private void Wave_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_doc == null) return;
        Focus(FocusState.Pointer);
        var pt = e.GetCurrentPoint(WaveHost);
        if (!pt.Properties.IsLeftButtonPressed) return;
        double t = XToTime(pt.Position.X);
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        if (shift)
        {
            _anchor = HasSelection ? (Math.Abs(t - SelStart) < Math.Abs(t - SelEnd) ? SelEnd : SelStart) : Cursor;
            SetSelection(Math.Min(_anchor, t), Math.Max(_anchor, t), false);
        }
        else
        {
            _anchor = t;
            SetCursor(t, true, false);
        }
        _dragging = true;
        WaveHost.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Wave_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || _doc == null) return;
        var x = e.GetCurrentPoint(WaveHost).Position.X;
        // Automatisches Scrollen am Rand
        if (x > ViewWidth) { _viewStart += (x - ViewWidth) * _spp * 0.2; ClampView(); UpdateScrollBar(); QueueRender(); }
        else if (x < 0) { _viewStart += x * _spp * 0.2; ClampView(); UpdateScrollBar(); QueueRender(); }
        double t = XToTime(x);
        SelStart = Math.Min(_anchor, t);
        SelEnd = Math.Max(_anchor, t);
        Cursor = SelStart;
        UpdateOverlay();
    }

    private void Wave_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        WaveHost.ReleasePointerCapture(e.Pointer);
        if ((SelEnd - SelStart) / _spp < 3) { SelStart = SelEnd = Cursor = _anchor; }
        UpdateOverlay();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        CursorMoved?.Invoke(this, EventArgs.Empty);
    }

    private void Wave_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => PlayRequested?.Invoke(this, EventArgs.Empty);

    private void Wave_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_doc == null) return;
        var pt = e.GetCurrentPoint(WaveHost);
        int delta = pt.Properties.MouseWheelDelta;
        var mods = e.KeyModifiers;
        if (mods.HasFlag(VirtualKeyModifiers.Control))
            Zoom(delta > 0 ? 1.25 : 0.8, XToTime(pt.Position.X));
        else if (mods.HasFlag(VirtualKeyModifiers.Shift))
            VerticalZoom *= delta > 0 ? 1.25 : 0.8;
        else
        {
            bool horizontal = pt.Properties.IsHorizontalMouseWheel;
            _viewStart += (horizontal ? delta : -delta) / 120.0 * ViewWidth * _spp * 0.15;
            ClampView();
            UpdateScrollBar();
            QueueRender();
        }
        e.Handled = true;
    }

    private void Ruler_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_doc == null) return;
        SetCursor(XToTime(e.GetCurrentPoint(RulerCanvas).Position.X));
    }

    private void Wave_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        WaveHost.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        if (_doc != null && e.PreviousSize.Width < 2) ZoomToFit();
        else { ClampView(); UpdateScrollBar(); QueueRender(); }
    }

    // ------------------------------------------------------------------ Zeichnen

    public void QueueRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _renderQueued = false;
            Render();
        });
    }

    private void Render()
    {
        RenderRuler();
        UpdateOverlay();
        double scale = XamlRoot?.RasterizationScale ?? 1;
        int pw = Math.Max(1, (int)(WaveHost.ActualWidth * scale));
        int ph = Math.Max(1, (int)(WaveHost.ActualHeight * scale));
        if (_bitmap == null || _bitmap.PixelWidth != pw || _bitmap.PixelHeight != ph)
        {
            _bitmap = new WriteableBitmap(pw, ph);
            _pixels = new byte[pw * ph * 4];
            WaveImage.Source = _bitmap;
        }
        Array.Clear(_pixels);
        var doc = _doc;
        if (doc != null && _min.Length > 0)
        {
            var accent = Ui.AccentColor;
            bool dark = ActualTheme == ElementTheme.Dark;
            var wave = dark ? Lighten(accent, 0.35) : accent;
            var center = dark ? Windows.UI.Color.FromArgb(60, 255, 255, 255) : Windows.UI.Color.FromArgb(50, 0, 0, 0);
            var clip = Windows.UI.Color.FromArgb(255, 232, 17, 35);
            var hl = Windows.UI.Color.FromArgb(70, 255, 185, 0);
            int ch = doc.Channels;
            double lane = (double)ph / ch;
            double sppPx = _spp / scale;
            double framesPerPx = sppPx * doc.SampleRate;
            var samples = doc.Samples;
            long totalFrames = doc.Frames;
            // Hervorhebungen (z. B. Füllwörter)
            foreach (var (hs, he) in Highlights)
            {
                int x0 = (int)((hs - _viewStart) / sppPx), x1 = (int)Math.Ceiling((he - _viewStart) / sppPx);
                for (int x = Math.Max(0, x0); x < Math.Min(pw, x1); x++)
                    for (int y = 0; y < ph; y++) Blend(x, y, pw, hl);
            }
            for (int c = 0; c < ch; c++)
            {
                int mid = (int)(lane * c + lane / 2);
                for (int x = 0; x < pw; x++) Blend(x, mid, pw, center);
            }
            for (int x = 0; x < pw; x++)
            {
                double t0 = _viewStart + x * sppPx;
                long f0 = (long)(t0 * doc.SampleRate);
                long f1 = (long)((t0 + sppPx) * doc.SampleRate);
                if (f0 >= totalFrames) break;
                f1 = Math.Clamp(f1, f0 + 1, totalFrames);
                for (int c = 0; c < ch; c++)
                {
                    float lo = 0, hi = 0;
                    if (framesPerPx >= Block)
                    {
                        long b0 = f0 / Block, b1 = Math.Min((f1 + Block - 1) / Block, _min.LongLength / ch);
                        for (long b = b0; b < b1; b++)
                        {
                            lo = Math.Min(lo, _min[b * ch + c]);
                            hi = Math.Max(hi, _max[b * ch + c]);
                        }
                    }
                    else if (framesPerPx >= 1)
                    {
                        for (long f = f0; f < f1; f++)
                        {
                            float v = samples[f * ch + c];
                            if (v < lo) lo = v;
                            if (v > hi) hi = v;
                        }
                    }
                    else
                    {
                        // Starker Zoom: Linie zwischen benachbarten Samples
                        double fx = t0 * doc.SampleRate;
                        long i0 = Math.Min(totalFrames - 1, (long)fx);
                        long i1 = Math.Min(totalFrames - 1, i0 + 1);
                        float a = samples[i0 * ch + c], b = samples[i1 * ch + c];
                        float v = (float)(a + (b - a) * (fx - i0));
                        float nextX = (float)((t0 + sppPx) * doc.SampleRate);
                        long j0 = Math.Min(totalFrames - 1, (long)nextX);
                        long j1 = Math.Min(totalFrames - 1, j0 + 1);
                        float v2 = samples[j0 * ch + c] + (samples[j1 * ch + c] - samples[j0 * ch + c]) * (float)(nextX - j0);
                        lo = Math.Min(v, v2); hi = Math.Max(v, v2);
                    }
                    double half = lane / 2 * 0.95;
                    int midY = (int)(lane * c + lane / 2);
                    int y0 = (int)(midY - Math.Clamp(hi * _verticalZoom, -1, 1) * half);
                    int y1 = (int)(midY - Math.Clamp(lo * _verticalZoom, -1, 1) * half);
                    if (y1 < y0) (y0, y1) = (y1, y0);
                    var col = (hi >= 0.999f || lo <= -0.999f) ? clip : wave;
                    for (int y = Math.Max(0, y0); y <= Math.Min(ph - 1, y1); y++) Put(x, y, pw, col);
                }
            }
        }
        using (var stream = _bitmap.PixelBuffer.AsStream())
        {
            stream.Write(_pixels, 0, _pixels.Length);
        }
        _bitmap.Invalidate();
    }

    private void Put(int x, int y, int pw, Windows.UI.Color c)
    {
        int i = (y * pw + x) * 4;
        _pixels[i] = c.B; _pixels[i + 1] = c.G; _pixels[i + 2] = c.R; _pixels[i + 3] = 255;
    }

    private void Blend(int x, int y, int pw, Windows.UI.Color c)
    {
        // vormultipliziertes BGRA
        int i = (y * pw + x) * 4;
        float a = c.A / 255f, inv = 1 - a;
        _pixels[i] = (byte)(c.B * a + _pixels[i] * inv);
        _pixels[i + 1] = (byte)(c.G * a + _pixels[i + 1] * inv);
        _pixels[i + 2] = (byte)(c.R * a + _pixels[i + 2] * inv);
        _pixels[i + 3] = (byte)(c.A + _pixels[i + 3] * inv);
    }

    private static Windows.UI.Color Lighten(Windows.UI.Color c, double k) => Windows.UI.Color.FromArgb(255,
        (byte)(c.R + (255 - c.R) * k), (byte)(c.G + (255 - c.G) * k), (byte)(c.B + (255 - c.B) * k));

    private static readonly double[] Steps = { 0.001, 0.005, 0.01, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 };

    private void RenderRuler()
    {
        RulerCanvas.Children.Clear();
        if (_doc == null) return;
        double step = Steps.FirstOrDefault(s => s / _spp >= 90, 3600);
        double first = Math.Floor(_viewStart / step) * step;
        var brush = Ui.ThemeBrush("TextFillColorSecondaryBrush");
        var tick = Ui.ThemeBrush("ControlStrongStrokeColorDefaultBrush");
        for (double t = first; t <= ViewEnd; t += step)
        {
            double x = TimeToX(t);
            if (x < -60) continue;
            var line = new Rectangle { Width = 1, Height = 8, Fill = tick };
            Canvas.SetLeft(line, x); Canvas.SetTop(line, 18);
            RulerCanvas.Children.Add(line);
            var label = new TextBlock
            {
                Text = step < 1 ? TimeFormat.Format(t, true) : TimeFormat.Format(t, false),
                FontSize = 11,
                Foreground = brush,
            };
            Canvas.SetLeft(label, x + 4); Canvas.SetTop(label, 2);
            RulerCanvas.Children.Add(label);
            for (int k = 1; k < 5; k++)
            {
                var minor = new Rectangle { Width = 1, Height = 4, Fill = tick, Opacity = 0.6 };
                Canvas.SetLeft(minor, x + k * step / 5 / _spp); Canvas.SetTop(minor, 22);
                RulerCanvas.Children.Add(minor);
            }
        }
    }

    private void UpdateOverlay()
    {
        double h = WaveHost.ActualHeight;
        if (HasSelection)
        {
            double x0 = TimeToX(SelStart), x1 = TimeToX(SelEnd);
            double l = Math.Max(-2, x0), r = Math.Min(ViewWidth + 2, x1);
            SelectionRect.Visibility = r > l ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetLeft(SelectionRect, l);
            SelectionRect.Width = Math.Max(0, r - l);
            SelectionRect.Height = h;
        }
        else SelectionRect.Visibility = Visibility.Collapsed;

        CursorLine.Height = h;
        Canvas.SetLeft(CursorLine, TimeToX(Cursor));
        CursorLine.Visibility = _doc != null ? Visibility.Visible : Visibility.Collapsed;

        if (_playhead.HasValue)
        {
            PlayheadLine.Visibility = Visibility.Visible;
            PlayheadLine.Height = h;
            Canvas.SetLeft(PlayheadLine, TimeToX(_playhead.Value) - 1);
        }
        else PlayheadLine.Visibility = Visibility.Collapsed;

        MarkerCanvas.Children.Clear();
        if (Markers != null)
        {
            var brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 140, 0));
            foreach (var m in Markers)
            {
                double x = TimeToX(m.Time);
                if (x < -100 || x > ViewWidth + 2) continue;
                var line = new Rectangle { Width = 1, Height = h, Fill = brush };
                Canvas.SetLeft(line, x);
                MarkerCanvas.Children.Add(line);
                var label = new Border
                {
                    Background = brush,
                    CornerRadius = new CornerRadius(0, 4, 4, 0),
                    Padding = new Thickness(4, 1, 6, 1),
                    Child = new TextBlock { Text = m.Name, FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black) },
                };
                Canvas.SetLeft(label, x);
                MarkerCanvas.Children.Add(label);
            }
        }
    }
}
