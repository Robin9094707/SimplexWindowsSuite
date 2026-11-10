namespace SimplexSuite.Core.Common;

/// <summary>Zentrale Pfade der Anwendung (portabel, ohne Paketidentität).</summary>
public static class AppPaths
{
    public static string DataRoot { get; } = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimplexSuite"));

    public static string ModelsDir => Ensure(Path.Combine(DataRoot, "models"));
    public static string TempDir => Ensure(Path.Combine(Path.GetTempPath(), "SimplexSuite"));
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");

    public static string NewTempFile(string extension)
    {
        if (!extension.StartsWith('.')) extension = "." + extension;
        return Path.Combine(TempDir, Guid.NewGuid().ToString("N") + extension);
    }

    public static string NewTempDir()
    {
        return Ensure(Path.Combine(TempDir, Guid.NewGuid().ToString("N")));
    }

    public static void CleanupTemp()
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(TempDir))
            {
                try
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, true);
                    else File.Delete(entry);
                }
                catch { /* Datei in Benutzung */ }
            }
        }
        catch { }
    }

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
