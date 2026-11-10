using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SimplexSuite.Core.Common;
using SimplexSuite.Core.Pdf;
using SimplexSuite.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace SimplexSuite.Pages;

public sealed partial class PdfPage : Page, IClosableEditor
{
    private static readonly string[] OpenExtensions = { ".pdf", ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

    private readonly ObservableCollection<PdfPageItem> _pages = new();
    private readonly Dictionary<Guid, BitmapImage> _thumbs = new();
    private readonly Stack<List<PdfPageItem>> _undo = new();
    private PdfPageItem? _current;
    private string? _docPath;
    private bool _dirty;
    private double _zoom = 1;
    private bool _fitMode = true;
    private string _tool = "Select";
    private PdfAnnotation? _selected;
    private bool _suppressProps;
    private int _renderVersion;

    // Zeichnen
    private Windows.Foundation.Point _downPt;
    private bool _pointerDown;
    private PdfAnnotation? _draft;
    private double _moveLastX, _moveLastY;
    private bool _movedSinceDown;
    private List<List<PdfPoint>>? _signature;   // normierte Striche (Seitenverhältnis 3:1)

    public PdfPage()
    {
        InitializeComponent();
        PageGrid.ItemsSource = _pages;
        foreach (var f in FontCatalog.Families) FontBox.Items.Add(f);
        FontBox.SelectedItem = FontCatalog.Families.FirstOrDefault(f => f.Equals("Arial", StringComparison.OrdinalIgnoreCase))
                               ?? FontCatalog.Families.FirstOrDefault();
        StrokePicker.Color = Ui.ParseColor("#E81123");
        FillPicker.Color = Ui.ParseColor("#FFF100");
        try
        {
            foreach (var lang in PdfRenderService.OcrLanguages) OcrLangBox.Items.Add(new ComboBoxItem { Content = lang.DisplayName, Tag = lang.LanguageTag });
            OcrLangBox.SelectedItem = OcrLangBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ((string)i.Tag).StartsWith("de")) ?? OcrLangBox.Items.FirstOrDefault();
        }
        catch { }
        KeyDown += Page_KeyDown;
        SizeChanged += (_, e) =>
        {
            bool narrow = e.NewSize.Width < 1150;
            ThumbColumn.Width = new GridLength(narrow ? 190 : 210);
            PropsColumn.Width = new GridLength(narrow ? 260 : 320);
        };
        UpdateUi();
    }

    public bool HasUnsavedChanges => _dirty && _pages.Count > 0;
    public void DiscardChanges() => _dirty = false;

    // ================================================================== Dateien

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var files = await Ui.PickOpenFilesAsync(true, OpenExtensions);
        if (files.Count > 0) OpenFiles(files);
    }

    public async void OpenFiles(IEnumerable<string> paths)
    {
        var list = paths.Select(p => { try { return Path.GetFullPath(p); } catch { return p; } }).ToList();
        if (_pages.Count > 0)
        {
            var add = await Ui.ConfirmAsync("Dokument geöffnet", "Sollen die Dateien an das aktuelle Dokument angehängt werden? (Nein = neues Dokument)", "Anhängen", "Neues Dokument");
            if (!add)
            {
                if (_dirty && !await Ui.ConfirmAsync("Ungespeicherte Änderungen", "Änderungen verwerfen?", "Verwerfen")) return;
                ClearDocument();
            }
        }
        await AddFilesAsync(list, _pages.Count);
        if (_docPath == null) _docPath = list.FirstOrDefault(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        foreach (var p in list) AppSettings.Current.AddRecent(p);
        UpdateUi();
    }

    private async void Append_Click(object sender, RoutedEventArgs e)
    {
        var files = await Ui.PickOpenFilesAsync(true, OpenExtensions);
        if (files.Count == 0) return;
        int at = PageGrid.SelectedItems.Count > 0 ? PageGrid.SelectedItems.OfType<PdfPageItem>().Max(p => _pages.IndexOf(p)) + 1 : _pages.Count;
        await AddFilesAsync(files, at);
    }

    private async Task AddFilesAsync(IReadOnlyList<string> files, int insertAt)
    {
        PushUndo();
        ShowBusy("Lade Dateien …");
        try
        {
            foreach (var path in files)
            {
                var items = new List<PdfPageItem>();
                if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    if (!await EnsureOpenableAsync(path)) continue;
                    try { items = await Task.Run(() => PdfExporter.ReadPages(path)); }
                    catch
                    {
                        // Fallback über die Windows-PDF-Engine
                        var doc = await PdfRenderService.GetDocumentAsync(path, PdfExporter.Passwords.GetValueOrDefault(path));
                        for (uint i = 0; i < doc.PageCount; i++)
                        {
                            using var pg = doc.GetPage(i);
                            items.Add(new PdfPageItem { Kind = PdfPageKind.Pdf, SourcePath = path, SourceIndex = (int)i, WidthPt = pg.Size.Width * 72 / 96, HeightPt = pg.Size.Height * 72 / 96 });
                        }
                    }
                }
                else
                {
                    var (w, h) = await PdfRenderService.ImageSizeAsync(path);
                    double pw = w >= h ? 842 : 595;
                    items.Add(new PdfPageItem { Kind = PdfPageKind.Image, ImagePath = path, WidthPt = pw, HeightPt = pw * h / Math.Max(1, w) });
                }
                foreach (var it in items) _pages.Insert(Math.Min(insertAt++, _pages.Count), it);
            }
        }
        catch (Exception ex) { Ui.Error("Öffnen fehlgeschlagen", ex); }
        finally { HideBusy(); }
        Renumber();
        MarkDirty(_pages.Count > 0 && _docPath != null && files.Count > 1);
        if (_current == null && _pages.Count > 0) PageGrid.SelectedIndex = 0;
        UpdateUi();
    }

    private async Task<bool> EnsureOpenableAsync(string path)
    {
        try
        {
            await PdfRenderService.GetDocumentAsync(path, PdfExporter.Passwords.GetValueOrDefault(path));
            return true;
        }
        catch
        {
            var pw = await Ui.InputAsync("Passwortgeschützte PDF", Path.GetFileName(path) + " – Passwort eingeben:", password: true);
            if (string.IsNullOrEmpty(pw)) return false;
            try
            {
                await PdfRenderService.GetDocumentAsync(path, pw);
                PdfExporter.Passwords[path] = pw;
                return true;
            }
            catch (Exception ex) { Ui.Error("Falsches Passwort", ex); return false; }
        }
    }

    private void NewBlank_Click(object sender, RoutedEventArgs e) => InsertBlank_Click(sender, e);

    private void Close_Click(object sender, RoutedEventArgs e) => _ = CloseAsync();

    private async Task CloseAsync()
    {
        if (_dirty && !await Ui.ConfirmAsync("Ungespeicherte Änderungen", "Änderungen verwerfen?", "Verwerfen")) return;
        ClearDocument();
    }

    private void ClearDocument()
    {
        _pages.Clear();
        _thumbs.Clear();
        _undo.Clear();
        _current = null;
        _selected = null;
        _docPath = null;
        _dirty = false;
        UpdateUi();
    }

    private void Renumber()
    {
        for (int i = 0; i < _pages.Count; i++) _pages[i].Number = i + 1;
        PagesHeader.Text = $"Seiten ({_pages.Count})";
    }

    private void MarkDirty(bool dirty = true)
    {
        _dirty = dirty || _dirty;
        UpdateUi();
    }

    private void UpdateUi()
    {
        bool has = _pages.Count > 0;
        EmptyPanel.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        EditorScroll.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        UndoButton.IsEnabled = _undo.Count > 0;
        var name = _docPath != null ? Path.GetFileName(_docPath) : (has ? "Neues Dokument" : "");
        DocNameText.Text = has ? name + (_dirty ? " •" : "") : "";
        PagesHeader.Text = $"Seiten ({_pages.Count})";
    }

    // ================================================================== Undo

    private static List<PdfPageItem> Snapshot(IEnumerable<PdfPageItem> pages) =>
        pages.Select(p => { var c = p.CloneDeep(); c.Id = p.Id; return c; }).ToList();

    private void PushUndo()
    {
        _undo.Push(Snapshot(_pages));
        if (_undo.Count > 60)
        {
            var keep = _undo.Take(50).Reverse().ToList();
            _undo.Clear();
            foreach (var k in keep) _undo.Push(k);
        }
        UndoButton.IsEnabled = true;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Undo()
    {
        if (_undo.Count == 0) return;
        var state = _undo.Pop();
        var currentId = _current?.Id;
        _pages.Clear();
        foreach (var p in state) _pages.Add(p);
        Renumber();
        _selected = null;
        var cur = _pages.FirstOrDefault(p => p.Id == currentId) ?? _pages.FirstOrDefault();
        PageGrid.SelectedItem = cur;
        ShowPage(cur);
        MarkDirty();
    }

    // ================================================================== Seitenbefehle

    private List<PdfPageItem> SelectedPages()
    {
        var sel = PageGrid.SelectedItems.OfType<PdfPageItem>().OrderBy(p => _pages.IndexOf(p)).ToList();
        if (sel.Count == 0 && _current != null) sel.Add(_current);
        return sel;
    }

    private void Rotate(int delta)
    {
        var sel = SelectedPages();
        if (sel.Count == 0) return;
        PushUndo();
        foreach (var p in sel)
        {
            p.ExtraRotation += delta;
            UpdateThumbTransform(p);
        }
        if (_current != null && sel.Contains(_current)) LayoutPage();
        MarkDirty();
    }

    private void RotateLeft_Click(object sender, RoutedEventArgs e) => Rotate(-90);
    private void RotateRight_Click(object sender, RoutedEventArgs e) => Rotate(90);

    private void DeletePages_Click(object sender, RoutedEventArgs e) => DeletePages();

    private void DeletePages()
    {
        var sel = SelectedPages();
        if (sel.Count == 0) return;
        PushUndo();
        int idx = _pages.IndexOf(sel[0]);
        foreach (var p in sel) _pages.Remove(p);
        Renumber();
        if (_pages.Count > 0) PageGrid.SelectedIndex = Math.Min(idx, _pages.Count - 1);
        else { _current = null; ShowPage(null); }
        MarkDirty();
    }

    private void InsertBlank_Click(object sender, RoutedEventArgs e)
    {
        PushUndo();
        var reference = _current;
        var blank = new PdfPageItem
        {
            Kind = PdfPageKind.Blank,
            WidthPt = reference?.WidthPt ?? 595.28,
            HeightPt = reference?.HeightPt ?? 841.89,
        };
        int at = _current != null ? _pages.IndexOf(_current) + 1 : _pages.Count;
        _pages.Insert(at, blank);
        Renumber();
        PageGrid.SelectedItem = blank;
        MarkDirty();
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedPages();
        if (sel.Count == 0) return;
        PushUndo();
        int at = _pages.IndexOf(sel[^1]) + 1;
        foreach (var p in sel) _pages.Insert(at++, p.CloneDeep());
        Renumber();
        MarkDirty();
    }

    private void Reverse_Click(object sender, RoutedEventArgs e)
    {
        if (_pages.Count < 2) return;
        PushUndo();
        var list = _pages.Reverse().ToList();
        _pages.Clear();
        foreach (var p in list) _pages.Add(p);
        Renumber();
        MarkDirty();
    }

    private void PageGrid_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        Renumber();
        MarkDirty();
    }

    private void PageGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var first = PageGrid.SelectedItems.OfType<PdfPageItem>().OrderBy(p => _pages.IndexOf(p)).FirstOrDefault();
        if (first != null && first != _current) ShowPage(first);
    }

    // ================================================================== Miniaturen

    private void PageGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not PdfPageItem item) return;
        var frame = FindThumbFrame(args.ItemContainer.ContentTemplateRoot);
        if (frame == null) return;
        ApplyThumbTransform(frame, item);
        var image = (Image)frame.Child;
        if (item.Kind == PdfPageKind.Blank)
        {
            image.Source = null;
            image.Width = 140; image.Height = 140 * item.HeightPt / item.WidthPt;
            if (image.Height > 175) { image.Height = 175; image.Width = 175 * item.WidthPt / item.HeightPt; }
            return;
        }
        image.Width = double.NaN; image.Height = double.NaN;
        if (_thumbs.TryGetValue(item.Id, out var bmp)) { image.Source = bmp; return; }
        image.Source = null;
        args.RegisterUpdateCallback(async (s, a) =>
        {
            try
            {
                var bytes = await PdfRenderService.RenderAsync(item, 340);
                if (bytes == null) return;
                var b = new BitmapImage();
                using var ms = new MemoryStream(bytes);
                await b.SetSourceAsync(ms.AsRandomAccessStream());
                _thumbs[item.Id] = b;
                if (FindThumbFrame(a.ItemContainer.ContentTemplateRoot) is Border f && a.Item == item) ((Image)f.Child).Source = b;
            }
            catch { }
        });
    }

    private static Border? FindThumbFrame(UIElement root)
    {
        if (root is StackPanel sp && sp.Children.FirstOrDefault() is Grid g && g.Children.FirstOrDefault() is Border b) return b;
        return null;
    }

    private static void ApplyThumbTransform(Border frame, PdfPageItem item)
    {
        bool sideways = item.ExtraRotation is 90 or 270;
        frame.MaxWidth = sideways ? 180 : 158;
        frame.MaxHeight = sideways ? 156 : 188;
        frame.RenderTransform = new RotateTransform { Angle = item.ExtraRotation };
    }

    private void UpdateThumbTransform(PdfPageItem item)
    {
        if (PageGrid.ContainerFromItem(item) is GridViewItem c && FindThumbFrame(c.ContentTemplateRoot) is Border f) ApplyThumbTransform(f, item);
    }

    // ================================================================== Seitenansicht

    private void ShowPage(PdfPageItem? page)
    {
        _current = page;
        _selected = null;
        OcrText.Text = "";
        UpdatePropsPanel();
        if (page == null) { PageImage.Source = null; AnnotationCanvas.Children.Clear(); return; }
        if (_fitMode) ComputeFitZoom();
        LayoutPage();
        _ = RenderCurrentAsync();
    }

    private double PageWidthPx => (_current?.WidthPt ?? 595) * _zoom * 96 / 72;
    private double PageHeightPx => (_current?.HeightPt ?? 842) * _zoom * 96 / 72;

    private void ComputeFitZoom()
    {
        if (_current == null) return;
        bool sideways = _current.ExtraRotation is 90 or 270;
        double w = sideways ? _current.HeightPt : _current.WidthPt;
        double h = sideways ? _current.WidthPt : _current.HeightPt;
        double vw = Math.Max(100, EditorScroll.ActualWidth - 60), vh = Math.Max(100, EditorScroll.ActualHeight - 60);
        _zoom = Math.Clamp(Math.Min(vw / (w * 96 / 72), vh / (h * 96 / 72)), 0.1, 8);
    }

    private void LayoutPage()
    {
        if (_current == null) return;
        double w = PageWidthPx, h = PageHeightPx;
        PageHost.Width = w;
        PageHost.Height = h;
        bool sideways = _current.ExtraRotation is 90 or 270;
        double ow = sideways ? h : w, oh = sideways ? w : h;
        PageOuter.Width = ow;
        PageOuter.Height = oh;
        Canvas.SetLeft(PageHost, (ow - w) / 2);
        Canvas.SetTop(PageHost, (oh - h) / 2);
        PageTransform.Rotation = _current.ExtraRotation;
        ZoomText.Text = $"{_zoom * 100:0} %";
        RenderAnnotations();
    }

    private async Task RenderCurrentAsync()
    {
        var page = _current;
        if (page == null) return;
        int version = ++_renderVersion;
        if (page.Kind == PdfPageKind.Blank) { PageImage.Source = null; return; }
        if (_thumbs.TryGetValue(page.Id, out var thumb)) PageImage.Source = thumb;
        double scale = XamlRoot?.RasterizationScale ?? 1;
        try
        {
            var bytes = await PdfRenderService.RenderAsync(page, Math.Min(6000, PageWidthPx * scale));
            if (bytes == null || version != _renderVersion || page != _current) return;
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            await bmp.SetSourceAsync(ms.AsRandomAccessStream());
            if (version == _renderVersion) PageImage.Source = bmp;
        }
        catch (Exception ex) { App.MainWindow?.ShowError("Seite konnte nicht angezeigt werden", ex.Message); }
    }

    private void EditorScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitMode && _current != null) { ComputeFitZoom(); LayoutPage(); }
    }

    private void SetZoom(double z)
    {
        _fitMode = false;
        _zoom = Math.Clamp(z, 0.1, 8);
        LayoutPage();
        _ = RenderCurrentAsync();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.25);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom / 1.25);
    private void ZoomFit_Click(object sender, RoutedEventArgs e)
    {
        _fitMode = true;
        ComputeFitZoom();
        LayoutPage();
        _ = RenderCurrentAsync();
    }

    private void EditorScroll_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)) return;
        int d = e.GetCurrentPoint(EditorScroll).Properties.MouseWheelDelta;
        SetZoom(_zoom * (d > 0 ? 1.15 : 1 / 1.15));
        e.Handled = true;
    }
}
