using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SimplexSuite.Core.Common;
using SimplexSuite.Core.Pdf;
using SimplexSuite.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace SimplexSuite.Pages;

public sealed partial class PdfPage
{
    private PdfSaveOptions _lastOptions = new();

    private void ShowBusy(string text)
    {
        BusyText.Text = text;
        BusyBar.IsIndeterminate = true;
        BusyPanel.Visibility = Visibility.Visible;
    }

    private void HideBusy() => BusyPanel.Visibility = Visibility.Collapsed;

    // ================================================================== Speichern

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    private async Task SaveAsync()
    {
        if (_pages.Count == 0) return;
        var o = _lastOptions;
        var title = new TextBox { Header = "Titel", Text = o.Title ?? (_docPath != null ? Path.GetFileNameWithoutExtension(_docPath) : "") };
        var author = new TextBox { Header = "Autor", Text = o.Author ?? "" };
        var subject = new TextBox { Header = "Betreff", Text = o.Subject ?? "" };
        var keywords = new TextBox { Header = "Stichwörter", Text = o.Keywords ?? "" };
        var pw = new PasswordBox { Header = "Passwort zum Öffnen (optional)" };
        var ownerPw = new PasswordBox { Header = "Berechtigungs-Passwort (optional)" };
        var allowPrint = new CheckBox { Content = "Drucken erlauben", IsChecked = o.AllowPrint };
        var allowCopy = new CheckBox { Content = "Kopieren erlauben", IsChecked = o.AllowCopy };
        var allowModify = new CheckBox { Content = "Ändern erlauben", IsChecked = o.AllowModify };
        var wm = new TextBox { Header = "Wasserzeichen (leer = keins)", Text = o.WatermarkText ?? "" };
        var wmOpacity = new NumberBox { Header = "Deckkraft %", Value = o.WatermarkOpacity * 100, Minimum = 1, Maximum = 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var wmSize = new NumberBox { Header = "Größe pt", Value = o.WatermarkSize, Minimum = 8, Maximum = 400, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var wmAngle = new NumberBox { Header = "Winkel °", Value = o.WatermarkAngle, Minimum = -180, Maximum = 180, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var numbers = new ToggleSwitch { Header = "Seitenzahlen hinzufügen", IsOn = o.PageNumbers };
        var numFormat = new TextBox { Header = "Format ({n} = Seite, {N} = Gesamt)", Text = o.PageNumberFormat };
        var numPos = new ComboBox { Header = "Position", HorizontalAlignment = HorizontalAlignment.Stretch };
        var positions = new[] { ("BottomCenter", "Unten Mitte"), ("BottomRight", "Unten rechts"), ("BottomLeft", "Unten links"), ("TopCenter", "Oben Mitte"), ("TopRight", "Oben rechts"), ("TopLeft", "Oben links") };
        foreach (var p in positions) numPos.Items.Add(p.Item2);
        numPos.SelectedIndex = Math.Max(0, Array.FindIndex(positions, p => p.Item1 == o.PageNumberPosition));
        var compress = new ToggleSwitch { Header = "Komprimieren", IsOn = o.Compress };

        var panel = new StackPanel { Spacing = 10, Width = 460 };
        void Header(string t) => panel.Children.Add(new TextBlock { Text = t, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 8, 0, 0) });
        Grid Row(params FrameworkElement[] els)
        {
            var g = new Grid { ColumnSpacing = 10 };
            for (int i = 0; i < els.Length; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetColumn(els[i], i);
                g.Children.Add(els[i]);
            }
            return g;
        }
        Header("Dokumentinfo");
        panel.Children.Add(Row(title, author));
        panel.Children.Add(Row(subject, keywords));
        Header("Sicherheit");
        panel.Children.Add(Row(pw, ownerPw));
        panel.Children.Add(Row(allowPrint, allowCopy, allowModify));
        Header("Wasserzeichen");
        panel.Children.Add(wm);
        panel.Children.Add(Row(wmOpacity, wmSize, wmAngle));
        Header("Seitenzahlen");
        panel.Children.Add(numbers);
        panel.Children.Add(Row(numFormat, numPos));
        panel.Children.Add(compress);
        if (_pages.Any(p => p.NeedsFlatten))
            panel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Title = "Sichere Schwärzung", Message = "Seiten mit Schwärzungen werden gerastert – der verdeckte Inhalt wird endgültig entfernt." });

        var dlg = new ContentDialog
        {
            Title = "PDF speichern",
            Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
            PrimaryButtonText = "Speichern unter …",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await Ui.ShowDialogAsync(dlg) != ContentDialogResult.Primary) return;
        _lastOptions = new PdfSaveOptions
        {
            Title = title.Text, Author = author.Text, Subject = subject.Text, Keywords = keywords.Text,
            UserPassword = string.IsNullOrEmpty(pw.Password) ? null : pw.Password,
            OwnerPassword = string.IsNullOrEmpty(ownerPw.Password) ? null : ownerPw.Password,
            AllowPrint = allowPrint.IsChecked == true, AllowCopy = allowCopy.IsChecked == true, AllowModify = allowModify.IsChecked == true,
            WatermarkText = string.IsNullOrWhiteSpace(wm.Text) ? null : wm.Text,
            WatermarkOpacity = (double.IsNaN(wmOpacity.Value) ? 15 : wmOpacity.Value) / 100,
            WatermarkSize = double.IsNaN(wmSize.Value) ? 72 : wmSize.Value,
            WatermarkAngle = double.IsNaN(wmAngle.Value) ? -45 : wmAngle.Value,
            PageNumbers = numbers.IsOn,
            PageNumberFormat = numFormat.Text,
            PageNumberPosition = positions[Math.Max(0, numPos.SelectedIndex)].Item1,
            Compress = compress.IsOn,
        };
        var suggested = _docPath != null ? Path.GetFileNameWithoutExtension(_docPath) + (_docPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "" : "") : "Dokument";
        var path = await Ui.PickSaveFileAsync(suggested, ("PDF-Dokument", ".pdf"));
        if (path == null) return;
        if (await WritePdfAsync(_pages.ToList(), path, _lastOptions))
        {
            _docPath = path;
            _dirty = false;
            AppSettings.Current.AddRecent(path);
            UpdateUi();
            Ui.Success("Gespeichert", path);
        }
    }

    private async Task<bool> WritePdfAsync(List<PdfPageItem> pages, string path, PdfSaveOptions options)
    {
        ShowBusy("Speichere PDF …");
        try
        {
            var flattened = new Dictionary<Guid, byte[]>();
            foreach (var p in pages.Where(p => p.NeedsFlatten))
            {
                var bytes = await PdfRenderService.FlattenWithRedactionsAsync(p);
                if (bytes != null) flattened[p.Id] = bytes;
            }
            var tmp = AppPaths.NewTempFile(".pdf");
            await Task.Run(() => PdfExporter.Save(pages, tmp, options, flattened));
            // Quelle wird ggf. überschrieben: Renderer-Cache freigeben
            PdfRenderService.Forget(path);
            File.Copy(tmp, path, true);
            File.Delete(tmp);
            return true;
        }
        catch (Exception ex)
        {
            Ui.Error("Speichern fehlgeschlagen", ex);
            return false;
        }
        finally { HideBusy(); }
    }

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedPages();
        if (sel.Count == 0) return;
        var path = await Ui.PickSaveFileAsync("Auszug", ("PDF-Dokument", ".pdf"));
        if (path == null) return;
        if (await WritePdfAsync(sel, path, new PdfSaveOptions())) Ui.Success("Gespeichert", $"{sel.Count} Seiten → {Path.GetFileName(path)}");
    }

    private async void Split_Click(object sender, RoutedEventArgs e)
    {
        if (_pages.Count == 0) return;
        var folder = await Ui.PickFolderAsync();
        if (folder == null) return;
        var baseName = _docPath != null ? Path.GetFileNameWithoutExtension(_docPath) : "Seite";
        int i = 1;
        foreach (var p in _pages.ToList())
        {
            if (!await WritePdfAsync(new List<PdfPageItem> { p }, Path.Combine(folder, $"{baseName}-{i:000}.pdf"), new PdfSaveOptions())) return;
            i++;
        }
        Ui.Success("Aufgeteilt", $"{_pages.Count} Dateien in {folder}");
    }

    private async void ExportImages_Click(object sender, RoutedEventArgs e)
    {
        if (_pages.Count == 0) return;
        var dpi = new ComboBox { Header = "Auflösung", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var d in new[] { "96 dpi (Bildschirm)", "150 dpi", "200 dpi", "300 dpi (Druck)", "600 dpi" }) dpi.Items.Add(d);
        dpi.SelectedIndex = 2;
        var fmt = new ComboBox { Header = "Format", HorizontalAlignment = HorizontalAlignment.Stretch };
        fmt.Items.Add("PNG (verlustfrei)");
        fmt.Items.Add("JPEG (klein)");
        fmt.SelectedIndex = 0;
        var onlySel = new CheckBox { Content = "Nur ausgewählte Seiten", IsChecked = PageGrid.SelectedItems.Count > 1 };
        var dlg = new ContentDialog
        {
            Title = "Seiten als Bilder exportieren",
            Content = new StackPanel { Spacing = 10, Width = 360, Children = { dpi, fmt, onlySel } },
            PrimaryButtonText = "Ordner wählen …",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await Ui.ShowDialogAsync(dlg) != ContentDialogResult.Primary) return;
        var folder = await Ui.PickFolderAsync();
        if (folder == null) return;
        var pages = onlySel.IsChecked == true ? SelectedPages() : _pages.ToList();
        var tmp = AppPaths.NewTempFile(".pdf");
        if (!await WritePdfAsync(pages, tmp, new PdfSaveOptions())) return;
        ShowBusy("Exportiere Bilder …");
        try
        {
            double d = new[] { 96, 150, 200, 300, 600 }[dpi.SelectedIndex];
            var baseName = _docPath != null ? Path.GetFileNameWithoutExtension(_docPath) : "Seite";
            await PdfRenderService.ExportImagesAsync(tmp, folder, baseName, d, fmt.SelectedIndex == 1, null, CancellationToken.None);
            Ui.Success("Exportiert", $"{pages.Count} Bilder in {folder}");
        }
        catch (Exception ex) { Ui.Error("Export fehlgeschlagen", ex); }
        finally { HideBusy(); }
    }

    // ================================================================== OCR

    private string? OcrLanguage => (OcrLangBox.SelectedItem as ComboBoxItem)?.Tag as string;

    private async Task<bool> OcrPageAsync(PdfPageItem page)
    {
        var result = await PdfRenderService.RecognizeAsync(page, OcrLanguage);
        page.Annotations.RemoveAll(a => a.Kind == PdfAnnotationKind.OcrText);
        foreach (var w in result.Words)
            page.Annotations.Add(new PdfAnnotation { Kind = PdfAnnotationKind.OcrText, Text = w.Text, X = w.X, Y = w.Y, W = w.W, H = w.H });
        if (page == _current) OcrText.Text = result.Text;
        return result.Words.Count > 0;
    }

    private async void Ocr_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        ShowBusy("Texterkennung …");
        try
        {
            PushUndo();
            bool found = await OcrPageAsync(_current);
            MarkDirty();
            Ui.Success("Texterkennung", found ? "Text erkannt – die Seite ist nach dem Speichern durchsuchbar." : "Kein Text gefunden.");
        }
        catch (Exception ex) { Ui.Error("Texterkennung", ex); }
        finally { HideBusy(); }
    }

    private async void OcrAll_Click(object sender, RoutedEventArgs e)
    {
        if (_pages.Count == 0) return;
        ShowBusy("Texterkennung aller Seiten …");
        BusyBar.IsIndeterminate = false;
        try
        {
            PushUndo();
            int i = 0;
            foreach (var p in _pages.Where(p => p.Kind != PdfPageKind.Blank).ToList())
            {
                await OcrPageAsync(p);
                BusyBar.Value = ++i * 100.0 / _pages.Count;
            }
            MarkDirty();
            Ui.Success("Texterkennung", "Alle Seiten verarbeitet. Jetzt speichern, um ein durchsuchbares PDF zu erhalten.");
        }
        catch (Exception ex) { Ui.Error("Texterkennung", ex); }
        finally { HideBusy(); }
    }

    private void CopyOcr_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(OcrText.Text)) return;
        var dp = new DataPackage();
        dp.SetText(OcrText.Text);
        Clipboard.SetContent(dp);
        Ui.Success("Kopiert", "Text liegt in der Zwischenablage.");
    }

    // ================================================================== Eingabe

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        if (focused is TextBox or NumberBox or PasswordBox or ComboBox or AutoSuggestBox) return;
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool handled = true;
        switch (e.Key)
        {
            case VirtualKey.Delete:
                if (_selected != null) DeleteSelectedAnnotation(); else DeletePages();
                break;
            case VirtualKey.Escape: _selected = null; SelectTool("Select"); RenderAnnotations(); break;
            case VirtualKey.Z when ctrl: Undo(); break;
            case VirtualKey.S when ctrl: _ = SaveAsync(); break;
            case VirtualKey.O when ctrl: Open_Click(this, new RoutedEventArgs()); break;
            case VirtualKey.V when !ctrl: SelectTool("Select"); break;
            case VirtualKey.PageDown: if (_current != null && _pages.IndexOf(_current) + 1 < _pages.Count) PageGrid.SelectedIndex = _pages.IndexOf(_current) + 1; break;
            case VirtualKey.PageUp: if (_current != null && _pages.IndexOf(_current) > 0) PageGrid.SelectedIndex = _pages.IndexOf(_current) - 1; break;
            default: handled = false; break;
        }
        e.Handled = handled;
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = _pages.Count > 0 ? "Anhängen" : "Öffnen";
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var files = items.OfType<Windows.Storage.StorageFile>().Select(f => f.Path)
            .Where(p => OpenExtensions.Contains(Path.GetExtension(p).ToLowerInvariant())).ToList();
        if (files.Count == 0) return;
        if (_pages.Count == 0) OpenFiles(files);
        else await AddFilesAsync(files, _pages.Count);
    }
}
