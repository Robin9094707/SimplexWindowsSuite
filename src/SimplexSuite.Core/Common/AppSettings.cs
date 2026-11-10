using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimplexSuite.Core.Common;

public sealed class AppSettings
{
    public string Theme { get; set; } = "System";           // System | Light | Dark
    public string Backdrop { get; set; } = "Mica";          // Mica | MicaAlt | Acrylic | None
    public string? FfmpegPath { get; set; }
    public string WhisperModel { get; set; } = "ggml-base.bin";
    public string WhisperLanguage { get; set; } = "de";
    public int WhisperThreads { get; set; } = Math.Max(1, Environment.ProcessorCount - 1);
    public bool WhisperVerbatim { get; set; } = true;
    public int WorkingSampleRate { get; set; } = 48000;
    public bool ForceMono { get; set; }
    public int MaxUndoMegabytes { get; set; } = 1024;
    public string? LastExportFolder { get; set; }
    public List<string> RecentFiles { get; set; } = new();
    public string FillerWords { get; set; } = "äh, ähm, äähm, öh, öhm, hm, hmm, mhm, ehm, uh, um, uhm, erm";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static AppSettings? _current;
    public static AppSettings Current => _current ??= Load();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Options);
                if (s != null) return s;
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Options));
        }
        catch { }
    }

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 12) RecentFiles.RemoveRange(12, RecentFiles.Count - 12);
        Save();
    }
}
