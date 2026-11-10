using PdfSharp.Fonts;
using SimplexSuite.Core.Common;

namespace SimplexSuite.Core.Pdf;

/// <summary>Schriftauflösung für PDFsharp über die installierten Systemschriften.</summary>
public sealed class PdfFontResolver : IFontResolver
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        try { GlobalFontSettings.FontResolver = new PdfFontResolver(); } catch { }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        var f = FontCatalog.Find(familyName, isBold, isItalic) ?? FontCatalog.Find("Arial", isBold, isItalic) ?? FontCatalog.Default;
        if (f == null) return null;
        bool simBold = isBold && !f.IsBold, simItalic = isItalic && !f.IsItalic;
        return new FontResolverInfo(f.FilePath, simBold, simItalic);
    }

    public byte[]? GetFont(string faceName)
    {
        try { return File.ReadAllBytes(faceName); } catch { return null; }
    }
}
