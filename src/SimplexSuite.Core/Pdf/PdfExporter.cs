using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Security;

namespace SimplexSuite.Core.Pdf;

/// <summary>Schreibt das bearbeitete Dokument mit PDFsharp (Seiten bleiben Vektor, Text bleibt erhalten).</summary>
public static class PdfExporter
{
    public static Dictionary<string, string> Passwords { get; } = new(StringComparer.OrdinalIgnoreCase);

    static PdfExporter() => PdfFontResolver.Install();

    public static PdfDocument OpenForImport(string path)
    {
        Passwords.TryGetValue(path, out var pw);
        return string.IsNullOrEmpty(pw)
            ? PdfReader.Open(path, PdfDocumentOpenMode.Import)
            : PdfReader.Open(path, pw, PdfDocumentOpenMode.Import);
    }

    /// <summary>Liest Seitenformate einer PDF (Grundansicht inkl. /Rotate).</summary>
    public static List<PdfPageItem> ReadPages(string path)
    {
        using var doc = OpenForImport(path);
        var list = new List<PdfPageItem>();
        for (int i = 0; i < doc.PageCount; i++)
        {
            var p = doc.Pages[i];
            var box = p.CropBox.IsZero ? p.MediaBox : p.CropBox;
            int rot = ((p.Rotate % 360) + 360) % 360;
            double w = box.Width, h = box.Height;
            if (rot is 90 or 270) (w, h) = (h, w);
            list.Add(new PdfPageItem { Kind = PdfPageKind.Pdf, SourcePath = path, SourceIndex = i, WidthPt = w, HeightPt = h, SourceRotation = rot });
        }
        return list;
    }

    /// <param name="flattened">Optional: gerasterte Seiten (PNG/JPEG) für sicheres Schwärzen, Schlüssel = Seiten-Id.</param>
    public static void Save(IReadOnlyList<PdfPageItem> pages, string outPath, PdfSaveOptions opt,
        IReadOnlyDictionary<Guid, byte[]>? flattened = null, IProgress<double>? progress = null)
    {
        var sources = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);
        var output = new PdfDocument();
        try
        {
            output.Info.Title = opt.Title ?? "";
            output.Info.Author = opt.Author ?? "";
            output.Info.Subject = opt.Subject ?? "";
            output.Info.Keywords = opt.Keywords ?? "";
            output.Info.Creator = "Simplex Suite";
            int n = 0;
            foreach (var item in pages)
            {
                PdfPage page;
                int baseRotation = 0;
                double nativeW, nativeH;
                byte[]? flat = null;
                flattened?.TryGetValue(item.Id, out flat);
                if (item.Kind == PdfPageKind.Pdf && flat == null)
                {
                    if (!sources.TryGetValue(item.SourcePath!, out var src))
                    {
                        src = OpenForImport(item.SourcePath!);
                        sources[item.SourcePath!] = src;
                    }
                    page = output.AddPage(src.Pages[item.SourceIndex]);
                    baseRotation = ((page.Rotate % 360) + 360) % 360;
                    nativeW = page.MediaBox.Width;
                    nativeH = page.MediaBox.Height;
                }
                else
                {
                    page = output.AddPage();
                    page.Width = XUnit.FromPoint(item.WidthPt);
                    page.Height = XUnit.FromPoint(item.HeightPt);
                    nativeW = item.WidthPt;
                    nativeH = item.HeightPt;
                }

                bool needsDrawing = flat != null || item.Kind == PdfPageKind.Image || item.Annotations.Count > 0
                                    || !string.IsNullOrWhiteSpace(opt.WatermarkText) || opt.PageNumbers;
                if (needsDrawing)
                {
                    var orientation = page.Orientation;
                    page.Orientation = PdfSharp.PageOrientation.Portrait;
                    using (var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append))
                    {
                        gfx.MultiplyTransform(BaseViewTransform(baseRotation, nativeW, nativeH));
                        double bw = item.WidthPt, bh = item.HeightPt;
                        if (flat != null)
                        {
                            using var ms = new MemoryStream(flat);
                            var img = XImage.FromStream(ms);
                            gfx.DrawImage(img, 0, 0, bw, bh);
                        }
                        else if (item.Kind == PdfPageKind.Image && item.ImagePath != null && File.Exists(item.ImagePath))
                        {
                            var img = XImage.FromFile(item.ImagePath);
                            gfx.DrawImage(img, 0, 0, bw, bh);
                        }
                        foreach (var a in item.Annotations) DrawAnnotation(gfx, a, bw, bh);
                        // Wasserzeichen und Seitenzahlen beziehen sich auf die endgültige Ausrichtung
                        int finalRot = item.ExtraRotation;
                        double fw = finalRot is 90 or 270 ? bh : bw, fh = finalRot is 90 or 270 ? bw : bh;
                        var state = gfx.Save();
                        gfx.MultiplyTransform(BaseViewTransform((360 - finalRot) % 360, bw, bh, inverseFinal: true, finalRot));
                        if (!string.IsNullOrWhiteSpace(opt.WatermarkText)) DrawWatermark(gfx, opt, fw, fh);
                        if (opt.PageNumbers) DrawPageNumber(gfx, opt, n + opt.PageNumberStart, pages.Count + opt.PageNumberStart - 1, fw, fh);
                        gfx.Restore(state);
                    }
                    page.Orientation = orientation;
                }
                page.Rotate = (baseRotation + item.ExtraRotation) % 360;
                n++;
                progress?.Report((double)n / pages.Count);
            }

            output.Options.CompressContentStreams = opt.Compress;
            output.Options.NoCompression = !opt.Compress;
            if (!string.IsNullOrEmpty(opt.UserPassword) || !string.IsNullOrEmpty(opt.OwnerPassword))
            {
                var sec = output.SecuritySettings;
                if (!string.IsNullOrEmpty(opt.UserPassword)) sec.UserPassword = opt.UserPassword;
                sec.OwnerPassword = string.IsNullOrEmpty(opt.OwnerPassword) ? (opt.UserPassword ?? Guid.NewGuid().ToString("N")) : opt.OwnerPassword;
                sec.PermitPrint = opt.AllowPrint;
                sec.PermitFullQualityPrint = opt.AllowPrint;
                sec.PermitExtractContent = opt.AllowCopy;
                sec.PermitModifyDocument = opt.AllowModify;
                sec.PermitAnnotations = opt.AllowModify;
                sec.PermitAssembleDocument = opt.AllowModify;
                sec.PermitFormsFill = true;
            }
            output.Save(outPath);
        }
        finally
        {
            foreach (var s in sources.Values) s.Dispose();
            output.Dispose();
        }
    }

    /// <summary>
    /// Matrix von Grundansicht (pt, oben links, y nach unten) in die native Seitenfläche (unrotiert).
    /// </summary>
    private static XMatrix BaseViewTransform(int rot, double nw, double nh, bool inverseFinal = false, int finalRot = 0)
    {
        if (inverseFinal)
        {
            // Koordinaten der endgültig gedrehten Ansicht (fw x fh) -> Grundansicht (bw=nw x bh=nh)
            return finalRot switch
            {
                90 => new XMatrix(0, -1, 1, 0, 0, nh),
                180 => new XMatrix(-1, 0, 0, -1, nw, nh),
                270 => new XMatrix(0, 1, -1, 0, nw, 0),
                _ => XMatrix.Identity,
            };
        }
        return rot switch
        {
            90 => new XMatrix(0, -1, 1, 0, 0, nh),
            180 => new XMatrix(-1, 0, 0, -1, nw, nh),
            270 => new XMatrix(0, 1, -1, 0, nw, 0),
            _ => XMatrix.Identity,
        };
    }

    public static XColor ParseColor(string hex, double opacity = 1)
    {
        var c = hex.Trim().TrimStart('#');
        if (c.Length == 8) c = c[2..];
        if (c.Length != 6) c = "000000";
        int r = int.Parse(c[..2], NumberStyles.HexNumber), g = int.Parse(c.Substring(2, 2), NumberStyles.HexNumber), b = int.Parse(c.Substring(4, 2), NumberStyles.HexNumber);
        return XColor.FromArgb((int)Math.Round(Math.Clamp(opacity, 0, 1) * 255), r, g, b);
    }

    private static XFont Font(PdfAnnotation a, double size)
    {
        var style = (a.Bold ? XFontStyleEx.Bold : XFontStyleEx.Regular) | (a.Italic ? XFontStyleEx.Italic : XFontStyleEx.Regular);
        return new XFont(string.IsNullOrWhiteSpace(a.FontFamily) ? "Arial" : a.FontFamily, Math.Max(1, size), style);
    }

    private static void DrawAnnotation(XGraphics gfx, PdfAnnotation a, double bw, double bh)
    {
        double x = a.X * bw, y = a.Y * bh, w = a.W * bw, h = a.H * bh;
        var pen = new XPen(ParseColor(a.Color, a.Opacity), Math.Max(0.1, a.StrokeWidth)) { LineCap = XLineCap.Round, LineJoin = XLineJoin.Round };
        XBrush? fill = a.Filled ? new XSolidBrush(ParseColor(a.FillColor, a.Opacity)) : null;
        switch (a.Kind)
        {
            case PdfAnnotationKind.Text:
            {
                var font = Font(a, a.FontSize);
                var brush = new XSolidBrush(ParseColor(a.Color, a.Opacity));
                if (a.Filled) gfx.DrawRectangle(new XSolidBrush(ParseColor(a.FillColor, a.Opacity)), x, y, w, h);
                double lh = a.FontSize * 1.2;
                var lines = a.Text.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                    gfx.DrawString(lines[i], font, brush, new XPoint(x, y + i * lh), XStringFormats.TopLeft);
                break;
            }
            case PdfAnnotationKind.OcrText:
            {
                if (string.IsNullOrWhiteSpace(a.Text) || w <= 0 || h <= 0) break;
                var font = new XFont("Arial", Math.Max(1, h * 0.85));
                var size = gfx.MeasureString(a.Text, font);
                var state = gfx.Save();
                gfx.TranslateTransform(x, y);
                if (size.Width > 0) gfx.ScaleTransform(w / size.Width, 1);
                // Unsichtbarer, aber durchsuchbarer Text
                gfx.DrawString(a.Text, font, new XSolidBrush(XColor.FromArgb(1, 255, 255, 255)), new XPoint(0, 0), XStringFormats.TopLeft);
                gfx.Restore(state);
                break;
            }
            case PdfAnnotationKind.Ink:
                if (a.Points.Count > 1)
                    gfx.DrawLines(pen, a.Points.Select(p => new XPoint(p.X * bw, p.Y * bh)).ToArray());
                else if (a.Points.Count == 1)
                    gfx.DrawEllipse(new XSolidBrush(pen.Color), a.Points[0].X * bw - pen.Width / 2, a.Points[0].Y * bh - pen.Width / 2, pen.Width, pen.Width);
                break;
            case PdfAnnotationKind.Rectangle:
                if (fill != null) gfx.DrawRectangle(pen, fill, x, y, w, h); else gfx.DrawRectangle(pen, x, y, w, h);
                break;
            case PdfAnnotationKind.Ellipse:
                if (fill != null) gfx.DrawEllipse(pen, fill, x, y, w, h); else gfx.DrawEllipse(pen, x, y, w, h);
                break;
            case PdfAnnotationKind.Line:
            case PdfAnnotationKind.Arrow:
            {
                if (a.Points.Count < 2) break;
                var p1 = new XPoint(a.Points[0].X * bw, a.Points[0].Y * bh);
                var p2 = new XPoint(a.Points[1].X * bw, a.Points[1].Y * bh);
                gfx.DrawLine(pen, p1, p2);
                if (a.Kind == PdfAnnotationKind.Arrow)
                {
                    double ang = Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);
                    double len = Math.Max(8, pen.Width * 4);
                    var l = new XPoint(p2.X - len * Math.Cos(ang - 0.45), p2.Y - len * Math.Sin(ang - 0.45));
                    var r = new XPoint(p2.X - len * Math.Cos(ang + 0.45), p2.Y - len * Math.Sin(ang + 0.45));
                    gfx.DrawPolygon(new XSolidBrush(pen.Color), new[] { p2, l, r }, XFillMode.Winding);
                }
                break;
            }
            case PdfAnnotationKind.Highlight:
                gfx.DrawRectangle(new XSolidBrush(ParseColor(a.FillColor, Math.Min(a.Opacity, 0.4))), x, y, w, h);
                break;
            case PdfAnnotationKind.Redact:
                gfx.DrawRectangle(XBrushes.Black, x, y, w, h);
                break;
            case PdfAnnotationKind.Whiteout:
                gfx.DrawRectangle(new XSolidBrush(ParseColor(a.FillColor, 1)), x, y, w, h);
                break;
            case PdfAnnotationKind.Image:
                if (a.ImagePath != null && File.Exists(a.ImagePath))
                {
                    var img = XImage.FromFile(a.ImagePath);
                    gfx.DrawImage(img, x, y, w, h);
                }
                break;
        }
    }

    private static void DrawWatermark(XGraphics gfx, PdfSaveOptions o, double w, double h)
    {
        var font = new XFont("Arial", Math.Max(6, o.WatermarkSize), XFontStyleEx.Bold);
        var state = gfx.Save();
        gfx.TranslateTransform(w / 2, h / 2);
        gfx.RotateTransform(o.WatermarkAngle);
        gfx.DrawString(o.WatermarkText!, font, new XSolidBrush(ParseColor(o.WatermarkColor, o.WatermarkOpacity)), new XPoint(0, 0), XStringFormats.Center);
        gfx.Restore(state);
    }

    private static void DrawPageNumber(XGraphics gfx, PdfSaveOptions o, int n, int total, double w, double h)
    {
        var text = o.PageNumberFormat.Replace("{n}", n.ToString()).Replace("{N}", total.ToString());
        var font = new XFont("Arial", Math.Max(5, o.PageNumberSize));
        double margin = 24;
        var size = gfx.MeasureString(text, font);
        double x = o.PageNumberPosition.EndsWith("Left") ? margin
            : o.PageNumberPosition.EndsWith("Right") ? w - margin - size.Width
            : (w - size.Width) / 2;
        double y = o.PageNumberPosition.StartsWith("Top") ? margin : h - margin - size.Height;
        gfx.DrawString(text, font, XBrushes.Black, new XRect(x, y, size.Width, size.Height), XStringFormats.TopLeft);
    }
}
