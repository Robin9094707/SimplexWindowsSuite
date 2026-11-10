using System.Text;

namespace SimplexSuite.Core.Common;

public sealed record FontEntry(string Family, string Style, string FullName, string FilePath)
{
    public bool IsBold => Style.Contains("Bold", StringComparison.OrdinalIgnoreCase)
                          || Style.Contains("Black", StringComparison.OrdinalIgnoreCase)
                          || Style.Contains("Fett", StringComparison.OrdinalIgnoreCase);
    public bool IsItalic => Style.Contains("Italic", StringComparison.OrdinalIgnoreCase)
                            || Style.Contains("Oblique", StringComparison.OrdinalIgnoreCase)
                            || Style.Contains("Kursiv", StringComparison.OrdinalIgnoreCase);
    public override string ToString() => FullName;
}

/// <summary>Liest installierte TrueType/OpenType-Schriften (Name-Tabelle) ohne Fremdbibliothek.</summary>
public static class FontCatalog
{
    private static List<FontEntry>? _fonts;
    private static readonly object Gate = new();

    public static IReadOnlyList<FontEntry> Fonts
    {
        get
        {
            lock (Gate) return _fonts ??= Scan();
        }
    }

    public static IEnumerable<string> Families => Fonts.Select(f => f.Family).Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

    public static FontEntry? Find(string family, bool bold = false, bool italic = false)
    {
        var candidates = Fonts.Where(f => string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0)
        {
            candidates = Fonts.Where(f => string.Equals(f.FullName, family, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (candidates.Count == 0) return null;
        return candidates.FirstOrDefault(f => f.IsBold == bold && f.IsItalic == italic)
               ?? candidates.FirstOrDefault(f => f.IsBold == bold)
               ?? candidates.FirstOrDefault(f => !f.IsBold && !f.IsItalic)
               ?? candidates[0];
    }

    public static FontEntry? Default =>
        Find("Segoe UI") ?? Find("Arial") ?? Find("DejaVu Sans") ?? Find("Liberation Sans") ?? Fonts.FirstOrDefault();

    private static IEnumerable<string> FontDirectories()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (!string.IsNullOrEmpty(win)) yield return win;
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
        yield return local;
        yield return "/usr/share/fonts";
        yield return "/usr/local/share/fonts";
    }

    private static List<FontEntry> Scan()
    {
        var list = new List<FontEntry>();
        foreach (var dir in FontDirectories().Distinct())
        {
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories).ToList(); }
            catch { continue; }
            foreach (var file in files)
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".ttf" && ext != ".otf") continue;
                try
                {
                    var entry = ReadNames(file);
                    if (entry != null) list.Add(entry);
                }
                catch { /* defekte Schrift ignorieren */ }
            }
        }
        return list.OrderBy(f => f.Family, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Style).ToList();
    }

    private static FontEntry? ReadNames(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        uint sfnt = ReadU32(br);
        if (sfnt != 0x00010000 && sfnt != 0x4F54544F && sfnt != 0x74727565) return null;
        int numTables = ReadU16(br);
        br.ReadBytes(6);
        long nameOffset = -1;
        for (int i = 0; i < numTables; i++)
        {
            var tag = Encoding.ASCII.GetString(br.ReadBytes(4));
            ReadU32(br);
            uint offset = ReadU32(br);
            ReadU32(br);
            if (tag == "name") nameOffset = offset;
        }
        if (nameOffset < 0) return null;
        fs.Position = nameOffset;
        ReadU16(br);
        int count = ReadU16(br);
        int stringOffset = ReadU16(br);
        string? family = null, style = null, full = null, typoFamily = null, typoStyle = null;
        int bestScore = -1;
        var records = new List<(int platform, int encoding, int lang, int nameId, int length, int offset)>();
        for (int i = 0; i < count; i++)
        {
            records.Add((ReadU16(br), ReadU16(br), ReadU16(br), ReadU16(br), ReadU16(br), ReadU16(br)));
        }
        foreach (var r in records.OrderByDescending(r => Score(r.platform, r.lang)))
        {
            if (r.nameId is not (1 or 2 or 4 or 16 or 17)) continue;
            var score = Score(r.platform, r.lang);
            if (score < 0) continue;
            fs.Position = nameOffset + stringOffset + r.offset;
            var bytes = br.ReadBytes(r.length);
            string value = r.platform == 1 ? Encoding.Latin1.GetString(bytes) : Encoding.BigEndianUnicode.GetString(bytes);
            value = value.Trim('\0', ' ');
            if (value.Length == 0) continue;
            switch (r.nameId)
            {
                case 1: family ??= value; break;
                case 2: style ??= value; break;
                case 4: full ??= value; break;
                case 16: typoFamily ??= value; break;
                case 17: typoStyle ??= value; break;
            }
            bestScore = Math.Max(bestScore, score);
        }
        family = typoFamily ?? family;
        style = typoStyle ?? style ?? "Regular";
        if (family == null) return null;
        full ??= family + " " + style;
        return new FontEntry(family, style, full, path);
    }

    private static int Score(int platform, int lang) => platform switch
    {
        3 when lang == 0x409 => 3,
        3 => 2,
        0 => 1,
        1 when lang == 0 => 0,
        _ => -1,
    };

    private static ushort ReadU16(BinaryReader br)
    {
        var b = br.ReadBytes(2);
        return (ushort)(b[0] << 8 | b[1]);
    }

    private static uint ReadU32(BinaryReader br)
    {
        var b = br.ReadBytes(4);
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }
}
