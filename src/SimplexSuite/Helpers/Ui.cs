using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace SimplexSuite;

/// <summary>Hilfsfunktionen für Dialoge und Dateiauswahl (unpaketierte App: Fenster-Handle nötig).</summary>
public static class Ui
{
    private static XamlRoot? Root => App.MainWindow?.Content?.XamlRoot;
    private static bool _dialogOpen;

    public static async Task<string?> PickOpenFileAsync(params string[] extensions)
    {
        var files = await PickOpenFilesAsync(false, extensions);
        return files.FirstOrDefault();
    }

    public static async Task<IReadOnlyList<string>> PickOpenFilesAsync(bool multiple, params string[] extensions)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow!.Hwnd);
        if (extensions.Length == 0) picker.FileTypeFilter.Add("*");
        foreach (var e in extensions) picker.FileTypeFilter.Add(e);
        if (multiple)
        {
            var files = await picker.PickMultipleFilesAsync();
            return files?.Select(f => f.Path).ToList() ?? new List<string>();
        }
        var file = await picker.PickSingleFileAsync();
        return file != null ? new[] { file.Path } : Array.Empty<string>();
    }

    /// <param name="choices">Paare aus Beschreibung und Erweiterung, z. B. ("MP3-Audio", ".mp3")</param>
    public static async Task<string?> PickSaveFileAsync(string suggestedName, params (string Label, string Ext)[] choices)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = suggestedName };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow!.Hwnd);
        foreach (var c in choices) picker.FileTypeChoices.Add(c.Label, new List<string> { c.Ext });
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow!.Hwnd);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public static async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (Root == null || _dialogOpen) return ContentDialogResult.None;
        dialog.XamlRoot = Root;
        dialog.Style ??= (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        if (App.MainWindow?.Content is FrameworkElement fe) dialog.RequestedTheme = fe.ActualTheme;
        _dialogOpen = true;
        try { return await dialog.ShowAsync(); }
        finally { _dialogOpen = false; }
    }

    public static async Task<bool> ConfirmAsync(string title, string message, string primary = "OK", string close = "Abbrechen")
    {
        var d = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = close,
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowDialogAsync(d) == ContentDialogResult.Primary;
    }

    public static async Task InfoAsync(string title, string message)
    {
        var d = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer { Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, MaxHeight = 420 },
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        };
        await ShowDialogAsync(d);
    }

    public static async Task<string?> InputAsync(string title, string label, string initial = "", bool password = false, bool multiline = false)
    {
        Control box;
        if (password) box = new PasswordBox { Header = label, Password = initial };
        else box = new TextBox { Header = label, Text = initial, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 120 : 0 };
        var d = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "OK",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await ShowDialogAsync(d) != ContentDialogResult.Primary) return null;
        return box is PasswordBox pb ? pb.Password : ((TextBox)box).Text;
    }

    public static void Error(string title, Exception ex)
    {
        if (ex is OperationCanceledException) { App.MainWindow?.ShowMessage("Abgebrochen", "Der Vorgang wurde abgebrochen.", InfoBarSeverity.Informational); return; }
        App.MainWindow?.ShowError(title, ex.Message);
        _ = InfoAsync(title, ex.Message);
    }

    public static void Success(string title, string message) => App.MainWindow?.ShowMessage(title, message);

    public static BitmapImage? LoadImage(string? path, int decodeWidth = 0)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var bmp = new BitmapImage();
        if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
        bmp.UriSource = new Uri(path);
        return bmp;
    }

    public static async Task<BitmapImage?> LoadImageFreshAsync(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var bytes = await File.ReadAllBytesAsync(path);
        var bmp = new BitmapImage();
        using var ms = new MemoryStream(bytes);
        await bmp.SetSourceAsync(ms.AsRandomAccessStream());
        return bmp;
    }

    public static Windows.UI.Color ParseColor(string hex)
    {
        var c = hex.Trim().TrimStart('#');
        if (c.Length == 6) c = "FF" + c;
        if (c.Length != 8) return Microsoft.UI.Colors.White;
        var v = Convert.ToUInt32(c, 16);
        return Windows.UI.Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static string ToHex(Windows.UI.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Brush ThemeBrush(string key) => (Brush)Application.Current.Resources[key];

    public static Windows.UI.Color AccentColor =>
        Application.Current.Resources.TryGetValue("SystemAccentColor", out var v) && v is Windows.UI.Color c ? c : Windows.UI.Color.FromArgb(255, 0, 120, 212);

    public static FontIcon Icon(string glyph, double size = 16) => new() { Glyph = glyph, FontSize = size };
}
