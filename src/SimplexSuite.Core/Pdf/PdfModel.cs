using SimplexSuite.Core.Common;

namespace SimplexSuite.Core.Pdf;

public enum PdfPageKind { Pdf, Blank, Image }

public enum PdfAnnotationKind { Text, Ink, Rectangle, Ellipse, Line, Arrow, Highlight, Redact, Whiteout, Image, OcrText }

public struct PdfPoint
{
    public double X { get; set; }
    public double Y { get; set; }
    public PdfPoint(double x, double y) { X = x; Y = y; }
}

/// <summary>Anmerkung in normierten Koordinaten (0..1) der unrotierten Seitenansicht.</summary>
public sealed class PdfAnnotation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public PdfAnnotationKind Kind { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public List<PdfPoint> Points { get; set; } = new();
    public string Text { get; set; } = "";
    public string FontFamily { get; set; } = "Arial";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public double FontSize { get; set; } = 14;      // pt
    public string Color { get; set; } = "#E81123";
    public string FillColor { get; set; } = "#FFF100";
    public bool Filled { get; set; }
    public double Opacity { get; set; } = 1;
    public double StrokeWidth { get; set; } = 2;     // pt
    public string? ImagePath { get; set; }

    public PdfAnnotation Clone()
    {
        var c = (PdfAnnotation)MemberwiseClone();
        c.Id = Guid.NewGuid();
        c.Points = new List<PdfPoint>(Points);
        return c;
    }

    public (double X, double Y, double W, double H) Bounds
    {
        get
        {
            if (Points.Count == 0) return (X, Y, W, H);
            double minX = Points.Min(p => p.X), minY = Points.Min(p => p.Y);
            double maxX = Points.Max(p => p.X), maxY = Points.Max(p => p.Y);
            return (minX, minY, maxX - minX, maxY - minY);
        }
    }

    public void Offset(double dx, double dy)
    {
        X += dx; Y += dy;
        for (int i = 0; i < Points.Count; i++) Points[i] = new PdfPoint(Points[i].X + dx, Points[i].Y + dy);
    }
}

public sealed class PdfPageItem : ObservableObject
{
    private int _extraRotation;
    private int _number;

    public Guid Id { get; set; } = Guid.NewGuid();
    public PdfPageKind Kind { get; set; }
    public string? SourcePath { get; set; }
    public int SourceIndex { get; set; }
    public string? ImagePath { get; set; }
    /// <summary>Breite/Höhe in pt in der Grundansicht (inkl. Quell-Rotation).</summary>
    public double WidthPt { get; set; } = 595;
    public double HeightPt { get; set; } = 842;
    public int SourceRotation { get; set; }
    public List<PdfAnnotation> Annotations { get; set; } = new();

    public int ExtraRotation
    {
        get => _extraRotation;
        set { if (Set(ref _extraRotation, ((value % 360) + 360) % 360)) OnPropertyChanged(nameof(Label)); }
    }

    public int Number
    {
        get => _number;
        set { if (Set(ref _number, value)) OnPropertyChanged(nameof(Label)); }
    }

    public string Label => Kind switch
    {
        PdfPageKind.Blank => $"{Number} · Leer",
        PdfPageKind.Image => $"{Number} · Bild",
        _ => $"{Number}",
    };

    public bool NeedsFlatten => Annotations.Any(a => a.Kind == PdfAnnotationKind.Redact);

    public PdfPageItem CloneDeep()
    {
        var c = (PdfPageItem)MemberwiseClone();
        c.Id = Guid.NewGuid();
        c.Annotations = Annotations.Select(a => a.Clone()).ToList();
        return c;
    }
}

public sealed class PdfSaveOptions
{
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Subject { get; set; }
    public string? Keywords { get; set; }

    public string? UserPassword { get; set; }
    public string? OwnerPassword { get; set; }
    public bool AllowPrint { get; set; } = true;
    public bool AllowCopy { get; set; } = true;
    public bool AllowModify { get; set; } = true;

    public string? WatermarkText { get; set; }
    public double WatermarkOpacity { get; set; } = 0.15;
    public double WatermarkSize { get; set; } = 72;
    public double WatermarkAngle { get; set; } = -45;
    public string WatermarkColor { get; set; } = "#808080";

    public bool PageNumbers { get; set; }
    public string PageNumberFormat { get; set; } = "Seite {n} von {N}";
    public string PageNumberPosition { get; set; } = "BottomCenter"; // BottomCenter, BottomRight, BottomLeft, TopCenter, TopRight, TopLeft
    public double PageNumberSize { get; set; } = 10;
    public int PageNumberStart { get; set; } = 1;

    public bool Compress { get; set; } = true;
}
