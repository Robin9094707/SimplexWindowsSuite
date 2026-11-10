using System.Globalization;

namespace SimplexSuite.Core.Common;

public static class TimeFormat
{
    public static string Format(double seconds, bool millis = true)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds)) seconds = 0;
        var negative = seconds < 0;
        seconds = Math.Abs(seconds);
        var ts = TimeSpan.FromSeconds(seconds);
        string s = ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}";
        if (millis) s += "." + ts.Milliseconds.ToString("000");
        return negative ? "-" + s : s;
    }

    public static string Srt(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00},{ts.Milliseconds:000}";
    }

    public static string Vtt(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
    }

    /// <summary>Parst "hh:mm:ss.fff", "mm:ss", "ss.fff".</summary>
    public static bool TryParse(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim().Replace(',', '.');
        var parts = text.Split(':');
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return false;
            total = total * 60 + v;
        }
        seconds = total;
        return true;
    }

    public static string Inv(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
