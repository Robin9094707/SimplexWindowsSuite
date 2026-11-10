using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SimplexSuite.Core.Pdf;

namespace SimplexSuite.Pages;

public sealed partial class PdfPage
{
    private double Kpx => _current == null ? 1 : PageHost.Width / _current.WidthPt; // px pro pt

    // ================================================================== Werkzeuge

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb || tb.Tag is not string tag) return;
        SelectTool(tag);
        if (tag == "Signature") _ = CaptureSignatureAsync();
    }

    private void SelectTool(string tag)
    {
        _tool = tag;
        foreach (var b in ToolBar.Children.OfType<ToggleButton>()) b.IsChecked = (string)b.Tag == tag;
        PropsTitle.Text = tag switch
        {
            "Select" => "Auswahl",
            "Text" => "Text",
            "Ink" => "Freihand",
            "Highlight" => "Textmarker",
            "Rectangle" => "Rechteck",
            "Ellipse" => "Ellipse",
            "Line" => "Linie",
            "Arrow" => "Pfeil",
            "Whiteout" => "Abdecken",
            "Redact" => "Schwärzen",
            "Image" => "Bild",
            "Signature" => "Unterschrift",
            _ => "Werkzeug",
        };
        PageHost.ProtectedCursorSet(tag == "Select" ? Microsoft.UI.Input.InputSystemCursorShape.Arrow : Microsoft.UI.Input.InputSystemCursorShape.Cross);
    }

    private PdfAnnotation NewAnnotation(PdfAnnotationKind kind) => new()
    {
        Kind = kind,
        Color = Ui.ToHex(StrokePicker.Color),
        FillColor = kind == PdfAnnotationKind.Whiteout ? "#FFFFFF" : Ui.ToHex(FillPicker.Color),
        Filled = FillCheck.IsChecked == true,
        StrokeWidth = double.IsNaN(StrokeBox.Value) ? 2 : StrokeBox.Value,
        FontSize = double.IsNaN(FontSizeBox.Value) ? 14 : FontSizeBox.Value,
        FontFamily = FontBox.SelectedItem as string ?? "Arial",
        Bold = BoldToggle.IsChecked == true,
        Italic = ItalicToggle.IsChecked == true,
        Opacity = OpacitySlider.Value / 100,
        Text = TextInput.Text,
    };

    // ================================================================== Zeiger

    private (double X, double Y) Norm(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(PageHost).Position;
        return (Math.Clamp(p.X / PageHost.Width, 0, 1), Math.Clamp(p.Y / PageHost.Height, 0, 1));
    }

    private void PageHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_current == null || !e.GetCurrentPoint(PageHost).Properties.IsLeftButtonPressed) return;
        Focus(FocusState.Pointer);
        var (x, y) = Norm(e);
        _downPt = new Windows.Foundation.Point(x, y);
        _pointerDown = true;
        _movedSinceDown = false;
        PageHost.CapturePointer(e.Pointer);
        e.Handled = true;
        switch (_tool)
        {
            case "Select":
                _selected = HitTest(x, y);
                _moveLastX = x; _moveLastY = y;
                if (_selected != null) PushUndo();
                UpdatePropsPanel();
                RenderAnnotations();
                break;
            case "Ink":
                _draft = NewAnnotation(PdfAnnotationKind.Ink);
                _draft.Points.Add(new PdfPoint(x, y));
                break;
            case "Line":
            case "Arrow":
                _draft = NewAnnotation(_tool == "Line" ? PdfAnnotationKind.Line : PdfAnnotationKind.Arrow);
                _draft.Points.Add(new PdfPoint(x, y));
                _draft.Points.Add(new PdfPoint(x, y));
                break;
            case "Text":
                break;
            default:
                var kind = _tool switch
                {
                    "Rectangle" => PdfAnnotationKind.Rectangle,
                    "Ellipse" => PdfAnnotationKind.Ellipse,
                    "Highlight" => PdfAnnotationKind.Highlight,
                    "Whiteout" => PdfAnnotationKind.Whiteout,
                    "Redact" => PdfAnnotationKind.Redact,
                    _ => PdfAnnotationKind.Image,
                };
                _draft = NewAnnotation(kind);
                _draft.X = x; _draft.Y = y;
                break;
        }
    }

    private void PageHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown || _current == null) return;
        var (x, y) = Norm(e);
        if (Math.Abs(x - _downPt.X) * PageHost.Width > 2 || Math.Abs(y - _downPt.Y) * PageHost.Height > 2) _movedSinceDown = true;
        if (_tool == "Select")
        {
            if (_selected == null) return;
            _selected.Offset(x - _moveLastX, y - _moveLastY);
            _moveLastX = x; _moveLastY = y;
            RenderAnnotations();
            return;
        }
        if (_draft == null) return;
        switch (_draft.Kind)
        {
            case PdfAnnotationKind.Ink:
                _draft.Points.Add(new PdfPoint(x, y));
                break;
            case PdfAnnotationKind.Line:
            case PdfAnnotationKind.Arrow:
                _draft.Points[1] = new PdfPoint(x, y);
                break;
            default:
                _draft.X = Math.Min(x, _downPt.X);
                _draft.Y = Math.Min(y, _downPt.Y);
                _draft.W = Math.Abs(x - _downPt.X);
                _draft.H = Math.Abs(y - _downPt.Y);
                break;
        }
        RenderDraft();
    }

    private async void PageHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown || _current == null) return;
        _pointerDown = false;
        PageHost.ReleasePointerCaptures();
        DraftCanvas.Children.Clear();
        var (x, y) = Norm(e);
        var page = _current;
        if (_tool == "Select")
        {
            if (_selected != null && _movedSinceDown) MarkDirty();
            else if (_selected != null && !_movedSinceDown && _undo.Count > 0) _undo.Pop();
            return;
        }
        if (_tool == "Text")
        {
            PushUndo();
            var t = NewAnnotation(PdfAnnotationKind.Text);
            if (string.IsNullOrWhiteSpace(t.Text)) t.Text = "Text";
            t.X = x; t.Y = y;
            page.Annotations.Add(t);
            _selected = t;
            SelectTool("Select");
            _selected = t;
            UpdatePropsPanel();
            RenderAnnotations();
            TextInput.Focus(FocusState.Programmatic);
            TextInput.SelectAll();
            MarkDirty();
            return;
        }
        var draft = _draft;
        _draft = null;
        if (draft == null) return;
        bool tiny = !_movedSinceDown;
        if (_tool == "Signature")
        {
            if (_signature == null) { await CaptureSignatureAsync(); if (_signature == null) return; }
            if (tiny) { draft.W = 0.3; draft.H = 0.1 * _current.WidthPt / _current.HeightPt; draft.X = Math.Min(x, 1 - draft.W); draft.Y = Math.Min(y, 1 - draft.H); }
            PushUndo();
            PlaceSignature(page, draft);
            RenderAnnotations();
            MarkDirty();
            return;
        }
        if (draft.Kind == PdfAnnotationKind.Image)
        {
            var file = await Ui.PickOpenFileAsync(".png", ".jpg", ".jpeg", ".bmp", ".gif");
            if (file == null) return;
            draft.ImagePath = file;
            if (tiny)
            {
                var (iw, ih) = await Services.PdfRenderService.ImageSizeAsync(file);
                draft.W = 0.3;
                draft.H = 0.3 * ih / Math.Max(1, iw) * page.WidthPt / page.HeightPt;
                draft.X = Math.Min(x, 1 - draft.W); draft.Y = Math.Min(y, 1 - draft.H);
            }
        }
        else if (draft.Kind is PdfAnnotationKind.Ink)
        {
            if (draft.Points.Count < 2) draft.Points.Add(new PdfPoint(x + 0.001, y));
        }
        else if (draft.Kind is PdfAnnotationKind.Line or PdfAnnotationKind.Arrow)
        {
            if (tiny) return;
        }
        else if (tiny || draft.W * PageHost.Width < 3 || draft.H * PageHost.Height < 3) return;
        PushUndo();
        page.Annotations.Add(draft);
        RenderAnnotations();
        MarkDirty();
        if (draft.Kind == PdfAnnotationKind.Redact)
            StatusText.Text = "Schwärzung vorgemerkt – beim Speichern wird die Seite gerastert, der Inhalt darunter ist danach endgültig entfernt.";
    }

    private PdfAnnotation? HitTest(double x, double y)
    {
        if (_current == null) return null;
        double tolX = 6 / PageHost.Width, tolY = 6 / PageHost.Height;
        for (int i = _current.Annotations.Count - 1; i >= 0; i--)
        {
            var a = _current.Annotations[i];
            if (a.Kind == PdfAnnotationKind.OcrText) continue;
            var (bx, by, bw, bh) = a.Bounds;
            if (x >= bx - tolX && x <= bx + bw + tolX && y >= by - tolY && y <= by + bh + tolY) return a;
        }
        return null;
    }

    // ================================================================== Darstellung

    private void RenderDraft()
    {
        DraftCanvas.Children.Clear();
        if (_draft != null) DraftCanvas.Children.Add(BuildVisual(_draft, true));
    }

    private void RenderAnnotations()
    {
        AnnotationCanvas.Children.Clear();
        if (_current == null) return;
        foreach (var a in _current.Annotations)
        {
            if (a.Kind == PdfAnnotationKind.OcrText) continue;
            AnnotationCanvas.Children.Add(BuildVisual(a, false));
            if (a == _selected)
            {
                var (bx, by, bw, bh) = a.Bounds;
                var sel = new Rectangle
                {
                    Width = bw * PageHost.Width + 8, Height = bh * PageHost.Height + 8,
                    Stroke = Ui.ThemeBrush("AccentFillColorDefaultBrush"), StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                };
                Canvas.SetLeft(sel, bx * PageHost.Width - 4);
                Canvas.SetTop(sel, by * PageHost.Height - 4);
                AnnotationCanvas.Children.Add(sel);
            }
        }
        DeleteAnnotationButton.IsEnabled = _selected != null;
    }

    private UIElement BuildVisual(PdfAnnotation a, bool draft)
    {
        double W = PageHost.Width, H = PageHost.Height, k = Kpx;
        var stroke = new SolidColorBrush(Ui.ParseColor(a.Color)) { Opacity = a.Opacity };
        var fill = new SolidColorBrush(Ui.ParseColor(a.FillColor)) { Opacity = a.Opacity };
        double sw = Math.Max(0.5, a.StrokeWidth * k);
        FrameworkElement el;
        switch (a.Kind)
        {
            case PdfAnnotationKind.Text:
            {
                var tb = new TextBlock
                {
                    Text = a.Text,
                    FontSize = Math.Max(1, a.FontSize * k),
                    FontFamily = new FontFamily(a.FontFamily),
                    FontWeight = a.Bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                    FontStyle = a.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                    Foreground = stroke,
                    LineHeight = a.FontSize * k * 1.2,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                };
                tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                a.W = tb.DesiredSize.Width / W;
                a.H = tb.DesiredSize.Height / H;
                el = a.Filled ? new Border { Background = fill, Child = tb } : tb;
                Canvas.SetLeft(el, a.X * W); Canvas.SetTop(el, a.Y * H);
                return el;
            }
            case PdfAnnotationKind.Ink:
            {
                var pl = new Polyline { Stroke = stroke, StrokeThickness = sw, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                foreach (var p in a.Points) pl.Points.Add(new Windows.Foundation.Point(p.X * W, p.Y * H));
                return pl;
            }
            case PdfAnnotationKind.Line:
            case PdfAnnotationKind.Arrow:
            {
                var canvas = new Canvas();
                if (a.Points.Count < 2) return canvas;
                double x1 = a.Points[0].X * W, y1 = a.Points[0].Y * H, x2 = a.Points[1].X * W, y2 = a.Points[1].Y * H;
                canvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = stroke, StrokeThickness = sw, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                if (a.Kind == PdfAnnotationKind.Arrow)
                {
                    double ang = Math.Atan2(y2 - y1, x2 - x1), len = Math.Max(8 * k, sw * 4);
                    var poly = new Polygon { Fill = stroke };
                    poly.Points.Add(new Windows.Foundation.Point(x2, y2));
                    poly.Points.Add(new Windows.Foundation.Point(x2 - len * Math.Cos(ang - 0.45), y2 - len * Math.Sin(ang - 0.45)));
                    poly.Points.Add(new Windows.Foundation.Point(x2 - len * Math.Cos(ang + 0.45), y2 - len * Math.Sin(ang + 0.45)));
                    canvas.Children.Add(poly);
                }
                return canvas;
            }
            case PdfAnnotationKind.Ellipse:
                el = new Ellipse { Stroke = stroke, StrokeThickness = sw, Fill = a.Filled ? fill : null };
                break;
            case PdfAnnotationKind.Highlight:
                el = new Rectangle { Fill = new SolidColorBrush(Ui.ParseColor(a.FillColor)) { Opacity = Math.Min(a.Opacity, 0.4) } };
                break;
            case PdfAnnotationKind.Redact:
                el = new Rectangle { Fill = new SolidColorBrush(Microsoft.UI.Colors.Black), Stroke = new SolidColorBrush(Microsoft.UI.Colors.Red), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 2 } };
                break;
            case PdfAnnotationKind.Whiteout:
                el = new Rectangle { Fill = new SolidColorBrush(Ui.ParseColor(a.FillColor)), Stroke = draft ? new SolidColorBrush(Microsoft.UI.Colors.Gray) : null, StrokeThickness = 1 };
                break;
            case PdfAnnotationKind.Image:
                el = a.ImagePath != null
                    ? new Image { Source = Ui.LoadImage(a.ImagePath), Stretch = Stretch.Fill }
                    : new Rectangle { Stroke = new SolidColorBrush(Microsoft.UI.Colors.Gray), StrokeDashArray = new DoubleCollection { 4, 2 }, StrokeThickness = 1 };
                break;
            default:
                el = new Rectangle { Stroke = stroke, StrokeThickness = sw, Fill = a.Filled ? fill : null };
                break;
        }
        el.Width = Math.Max(1, a.W * W);
        el.Height = Math.Max(1, a.H * H);
        Canvas.SetLeft(el, a.X * W);
        Canvas.SetTop(el, a.Y * H);
        return el;
    }

    // ================================================================== Eigenschaften

    private void UpdatePropsPanel()
    {
        DeleteAnnotationButton.IsEnabled = _selected != null;
        if (_selected == null) return;
        _suppressProps = true;
        var a = _selected;
        TextInput.Text = a.Text;
        FontSizeBox.Value = a.FontSize;
        FontBox.SelectedItem = FontBox.Items.OfType<string>().FirstOrDefault(f => f.Equals(a.FontFamily, StringComparison.OrdinalIgnoreCase));
        BoldToggle.IsChecked = a.Bold;
        ItalicToggle.IsChecked = a.Italic;
        StrokePicker.Color = Ui.ParseColor(a.Color);
        StrokeSwatch.Background = new SolidColorBrush(StrokePicker.Color);
        FillPicker.Color = Ui.ParseColor(a.FillColor);
        FillSwatch.Background = new SolidColorBrush(FillPicker.Color);
        FillCheck.IsChecked = a.Filled;
        StrokeBox.Value = a.StrokeWidth;
        OpacitySlider.Value = a.Opacity * 100;
        _suppressProps = false;
    }

    private void ApplyPropsToSelection()
    {
        if (_suppressProps || _selected == null) return;
        var a = _selected;
        if (a.Kind == PdfAnnotationKind.Text) a.Text = TextInput.Text;
        if (!double.IsNaN(FontSizeBox.Value)) a.FontSize = FontSizeBox.Value;
        if (FontBox.SelectedItem is string f) a.FontFamily = f;
        a.Bold = BoldToggle.IsChecked == true;
        a.Italic = ItalicToggle.IsChecked == true;
        a.Color = Ui.ToHex(StrokePicker.Color);
        if (a.Kind != PdfAnnotationKind.Redact) a.FillColor = Ui.ToHex(FillPicker.Color);
        a.Filled = FillCheck.IsChecked == true;
        if (!double.IsNaN(StrokeBox.Value)) a.StrokeWidth = StrokeBox.Value;
        a.Opacity = OpacitySlider.Value / 100;
        RenderAnnotations();
        MarkDirty();
    }

    private void TextInput_TextChanged(object sender, TextChangedEventArgs e) => ApplyPropsToSelection();
    private void Prop_Changed(object sender, SelectionChangedEventArgs e) => ApplyPropsToSelection();
    private void Prop_Click(object sender, RoutedEventArgs e) => ApplyPropsToSelection();
    private void PropNumber_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) => ApplyPropsToSelection();
    private void Opacity_Changed(object sender, RangeBaseValueChangedEventArgs e) { if (IsLoaded) ApplyPropsToSelection(); }

    private void StrokePicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        StrokeSwatch.Background = new SolidColorBrush(args.NewColor);
        ApplyPropsToSelection();
    }

    private void FillPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        FillSwatch.Background = new SolidColorBrush(args.NewColor);
        ApplyPropsToSelection();
    }

    private void DeleteAnnotation_Click(object sender, RoutedEventArgs e) => DeleteSelectedAnnotation();

    private void DeleteSelectedAnnotation()
    {
        if (_current == null || _selected == null) return;
        PushUndo();
        _current.Annotations.Remove(_selected);
        _selected = null;
        RenderAnnotations();
        MarkDirty();
    }

    private void ClearAnnotations_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || _current.Annotations.Count == 0) return;
        PushUndo();
        _current.Annotations.Clear();
        _selected = null;
        RenderAnnotations();
        MarkDirty();
    }

    // ================================================================== Unterschrift

    private async Task CaptureSignatureAsync()
    {
        const double padW = 480, padH = 160;
        var strokes = new List<List<PdfPoint>>();
        var pad = new Canvas { Width = padW, Height = padH, Background = new SolidColorBrush(Microsoft.UI.Colors.White) };
        var line = new Rectangle { Width = padW - 40, Height = 1, Fill = new SolidColorBrush(Microsoft.UI.Colors.LightGray) };
        Canvas.SetLeft(line, 20); Canvas.SetTop(line, padH - 36);
        pad.Children.Add(line);
        List<PdfPoint>? cur = null;
        Polyline? curLine = null;
        pad.PointerPressed += (_, e) =>
        {
            var p = e.GetCurrentPoint(pad).Position;
            cur = new List<PdfPoint> { new(p.X / padW, p.Y / padH) };
            curLine = new Polyline { Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 26, 35, 126)), StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            curLine.Points.Add(p);
            pad.Children.Add(curLine);
            pad.CapturePointer(e.Pointer);
        };
        pad.PointerMoved += (_, e) =>
        {
            if (cur == null || curLine == null) return;
            var p = e.GetCurrentPoint(pad).Position;
            cur.Add(new PdfPoint(Math.Clamp(p.X / padW, 0, 1), Math.Clamp(p.Y / padH, 0, 1)));
            curLine.Points.Add(p);
        };
        pad.PointerReleased += (_, _) =>
        {
            if (cur is { Count: > 1 }) strokes.Add(cur);
            cur = null; curLine = null;
            pad.ReleasePointerCaptures();
        };
        var clear = new Button { Content = "Löschen" };
        clear.Click += (_, _) =>
        {
            strokes.Clear();
            foreach (var pl in pad.Children.OfType<Polyline>().ToList()) pad.Children.Remove(pl);
        };
        var content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Mit Maus, Stift oder Finger unterschreiben. Danach auf der Seite einen Bereich aufziehen oder klicken." , TextWrapping = TextWrapping.Wrap },
                new Border { BorderBrush = Ui.ThemeBrush("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = pad },
                clear,
            },
        };
        var dlg = new ContentDialog { Title = "Unterschrift", Content = content, PrimaryButtonText = "Übernehmen", CloseButtonText = "Abbrechen", DefaultButton = ContentDialogButton.Primary };
        if (await Ui.ShowDialogAsync(dlg) == ContentDialogResult.Primary && strokes.Count > 0)
        {
            // Auf Inhalt zuschneiden
            double minX = strokes.SelectMany(s => s).Min(p => p.X), maxX = strokes.SelectMany(s => s).Max(p => p.X);
            double minY = strokes.SelectMany(s => s).Min(p => p.Y), maxY = strokes.SelectMany(s => s).Max(p => p.Y);
            double sx = Math.Max(1e-3, maxX - minX), sy = Math.Max(1e-3, maxY - minY);
            _signature = strokes.Select(s => s.Select(p => new PdfPoint((p.X - minX) / sx, (p.Y - minY) / sy)).ToList()).ToList();
            _signatureAspect = sx * padW / (sy * padH);
            StatusText.Text = "Unterschrift bereit – jetzt auf der Seite platzieren.";
        }
        else if (_signature == null) SelectTool("Select");
    }

    private double _signatureAspect = 3;

    private void PlaceSignature(PdfPageItem page, PdfAnnotation rect)
    {
        if (_signature == null) return;
        // Seitenverhältnis wahren
        double rw = rect.W * page.WidthPt, rh = rect.H * page.HeightPt;
        double w = rw, h = rw / _signatureAspect;
        if (h > rh && rh > 1) { h = rh; w = rh * _signatureAspect; }
        double nx = rect.X, ny = rect.Y, nw = w / page.WidthPt, nh = h / page.HeightPt;
        foreach (var stroke in _signature)
        {
            var a = new PdfAnnotation { Kind = PdfAnnotationKind.Ink, Color = "#1A237E", StrokeWidth = Math.Max(0.8, h / 40), Opacity = 1 };
            foreach (var p in stroke) a.Points.Add(new PdfPoint(nx + p.X * nw, ny + p.Y * nh));
            page.Annotations.Add(a);
        }
    }
}

internal static class CursorExtensions
{
    public static void ProtectedCursorSet(this UIElement element, Microsoft.UI.Input.InputSystemCursorShape shape)
    {
        // ProtectedCursor ist geschützt – per Reflection setzen (funktioniert für alle UIElements)
        try
        {
            var prop = typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            prop?.SetValue(element, Microsoft.UI.Input.InputSystemCursor.Create(shape));
        }
        catch { }
    }
}
