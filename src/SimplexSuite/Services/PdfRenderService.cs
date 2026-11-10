using System.Runtime.InteropServices.WindowsRuntime;
using SimplexSuite.Core.Pdf;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SimplexSuite.Services;

/// <summary>Rendert PDF-Seiten mit der in Windows eingebauten PDF-Engine und erkennt Text per Windows-OCR.</summary>
public static class PdfRenderService
{
    private static readonly Dictionary<string, PdfDocument> Docs = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<PdfDocument> GetDocumentAsync(string path, string? password = null)
    {
        path = Path.GetFullPath(path);
        if (Docs.TryGetValue(path, out var d)) return d;
        var file = await StorageFile.GetFileFromPathAsync(path);
        d = string.IsNullOrEmpty(password) ? await PdfDocument.LoadFromFileAsync(file) : await PdfDocument.LoadFromFileAsync(file, password);
        Docs[path] = d;
        return d;
    }

    public static void Forget(string path) { try { Docs.Remove(Path.GetFullPath(path)); } catch { } }

    /// <summary>Rendert die Grundansicht einer Seite (ohne Zusatzdrehung) als PNG.</summary>
    public static async Task<byte[]?> RenderAsync(PdfPageItem item, double widthPx)
    {
        uint w = (uint)Math.Clamp(widthPx, 16, 9000);
        switch (item.Kind)
        {
            case PdfPageKind.Pdf:
            {
                var doc = await GetDocumentAsync(item.SourcePath!);
                using var page = doc.GetPage((uint)item.SourceIndex);
                using var stream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
                {
                    DestinationWidth = w,
                    BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
                });
                return await ReadAllAsync(stream);
            }
            case PdfPageKind.Image when item.ImagePath != null:
                return await ScaleImageAsync(item.ImagePath, w);
            default:
                return null;
        }
    }

    private static async Task<byte[]> ReadAllAsync(IRandomAccessStream stream)
    {
        stream.Seek(0);
        var bytes = new byte[stream.Size];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
        return bytes;
    }

    private static async Task<byte[]> ScaleImageAsync(string path, uint width)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        using var input = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(input);
        double k = Math.Min(1, (double)width / decoder.OrientedPixelWidth);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateForTranscodingAsync(output, decoder);
        encoder.BitmapTransform.ScaledWidth = (uint)Math.Max(1, decoder.OrientedPixelWidth * k);
        encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(1, decoder.OrientedPixelHeight * k);
        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        await encoder.FlushAsync();
        return await ReadAllAsync(output);
    }

    public static async Task<(int W, int H)> ImageSizeAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        using var input = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(input);
        return ((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
    }

    /// <summary>
    /// Rastert eine Seite und brennt Schwärzungen in die Pixel ein. Damit ist der verdeckte Inhalt
    /// tatsächlich entfernt (nicht nur überdeckt).
    /// </summary>
    public static async Task<byte[]?> FlattenWithRedactionsAsync(PdfPageItem item, double dpi = 200)
    {
        var png = await RenderAsync(item, item.WidthPt / 72.0 * dpi);
        if (png == null) return null;
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(png.AsBuffer());
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input);
        var pixelData = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var px = pixelData.DetachPixelData();
        int w = (int)decoder.PixelWidth, h = (int)decoder.PixelHeight;
        foreach (var a in item.Annotations.Where(a => a.Kind == PdfAnnotationKind.Redact))
        {
            int x0 = Math.Clamp((int)Math.Floor(a.X * w), 0, w), x1 = Math.Clamp((int)Math.Ceiling((a.X + a.W) * w), 0, w);
            int y0 = Math.Clamp((int)Math.Floor(a.Y * h), 0, h), y1 = Math.Clamp((int)Math.Ceiling((a.Y + a.H) * h), 0, h);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int i = (y * w + x) * 4;
                    px[i] = 0; px[i + 1] = 0; px[i + 2] = 0; px[i + 3] = 255;
                }
        }
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)w, (uint)h, dpi, dpi, px);
        await encoder.FlushAsync();
        return await ReadAllAsync(output);
    }

    public sealed record OcrWord(string Text, double X, double Y, double W, double H);
    public sealed record OcrPageResult(string Text, List<OcrWord> Words);

    public static IReadOnlyList<Windows.Globalization.Language> OcrLanguages => OcrEngine.AvailableRecognizerLanguages;

    /// <summary>Texterkennung einer Seite (normierte Koordinaten der Grundansicht).</summary>
    public static async Task<OcrPageResult> RecognizeAsync(PdfPageItem item, string? languageTag)
    {
        OcrEngine? engine = null;
        if (!string.IsNullOrEmpty(languageTag))
        {
            var lang = new Windows.Globalization.Language(languageTag);
            if (OcrEngine.IsLanguageSupported(lang)) engine = OcrEngine.TryCreateFromLanguage(lang);
        }
        engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null) throw new InvalidOperationException("Keine OCR-Sprache installiert. Installiere in den Windows-Einstellungen unter „Zeit und Sprache“ ein Sprachpaket mit Texterkennung.");
        double width = Math.Min(OcrEngine.MaxImageDimension, item.WidthPt / 72.0 * 250);
        if (item.HeightPt / item.WidthPt * width > OcrEngine.MaxImageDimension) width = OcrEngine.MaxImageDimension * item.WidthPt / item.HeightPt;
        var png = await RenderAsync(item, width) ?? throw new InvalidOperationException("Leere Seiten enthalten keinen Text.");
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(png.AsBuffer());
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);
        double w = bitmap.PixelWidth, h = bitmap.PixelHeight;
        var words = new List<OcrWord>();
        var sb = new System.Text.StringBuilder();
        foreach (var line in result.Lines)
        {
            sb.AppendLine(line.Text);
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                words.Add(new OcrWord(word.Text, r.X / w, r.Y / h, r.Width / w, r.Height / h));
            }
        }
        return new OcrPageResult(sb.ToString(), words);
    }

    /// <summary>Rendert eine (gespeicherte) PDF-Datei seitenweise in Bilder.</summary>
    public static async Task ExportImagesAsync(string pdfPath, string folder, string baseName, double dpi, bool jpeg, IProgress<double>? progress, CancellationToken ct)
    {
        var file = await StorageFile.GetFileFromPathAsync(pdfPath);
        var doc = await PdfDocument.LoadFromFileAsync(file);
        for (uint i = 0; i < doc.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var page = doc.GetPage(i);
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(16, page.Size.Width / 96.0 * dpi),
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
                BitmapEncoderId = jpeg ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId,
            });
            var bytes = await ReadAllAsync(stream);
            await File.WriteAllBytesAsync(Path.Combine(folder, $"{baseName}-{i + 1:000}{(jpeg ? ".jpg" : ".png")}"), bytes, ct);
            progress?.Report((i + 1.0) / doc.PageCount);
        }
    }
}
