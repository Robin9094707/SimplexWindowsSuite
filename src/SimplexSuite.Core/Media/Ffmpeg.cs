using System.Diagnostics;
using System.Globalization;
using System.Text;
using SimplexSuite.Core.Common;

namespace SimplexSuite.Core.Media;

public sealed class FfmpegException : Exception
{
    public FfmpegException(string message) : base(message) { }
}

/// <summary>Findet und startet ffmpeg/ffprobe (werden mit der App ausgeliefert).</summary>
public static class Ffmpeg
{
    private static string? _ffmpeg, _ffprobe;

    public static string Exe(string name)
    {
        var isWin = OperatingSystem.IsWindows();
        var file = isWin ? name + ".exe" : name;
        var custom = AppSettings.Current.FfmpegPath;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(custom))
        {
            var dir = File.Exists(custom) ? Path.GetDirectoryName(custom)! : custom;
            candidates.Add(Path.Combine(dir, file));
        }
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", file));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, file));
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), file);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return file;
    }

    public static string FfmpegPath => _ffmpeg ??= Exe("ffmpeg");
    public static string FfprobePath => _ffprobe ??= Exe("ffprobe");

    public static void ResetCache() { _ffmpeg = null; _ffprobe = null; }

    public static bool IsAvailable
    {
        get
        {
            try
            {
                var r = RunAsync(new[] { "-hide_banner", "-version" }, CancellationToken.None).GetAwaiter().GetResult();
                return r.ExitCode == 0;
            }
            catch { return false; }
        }
    }

    public static string DataFile(string name) => Path.Combine(AppContext.BaseDirectory, "ffmpeg", name);

    public static ProcessStartInfo CreateStartInfo(string exe, IEnumerable<string> args, string? workDir = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workDir != null) psi.WorkingDirectory = workDir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    public sealed record Result(int ExitCode, string StdOut, string StdErr);

    public static Task<Result> RunAsync(IEnumerable<string> args, CancellationToken ct, string? workDir = null)
        => RunToolAsync(FfmpegPath, args, ct, workDir);

    public static async Task<Result> RunToolAsync(string exe, IEnumerable<string> args, CancellationToken ct, string? workDir = null)
    {
        using var p = new Process { StartInfo = CreateStartInfo(exe, args, workDir) };
        if (!p.Start()) throw new FfmpegException("FFmpeg konnte nicht gestartet werden.");
        p.StandardInput.Close();
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using (ct.Register(() => TryKill(p)))
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        return new Result(p.ExitCode, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
    }

    /// <summary>Startet ffmpeg mit "-progress pipe:1" und meldet den Fortschritt (0..1) bezogen auf totalSeconds.</summary>
    public static async Task RunWithProgressAsync(IEnumerable<string> args, double totalSeconds, IProgress<double>? progress,
        CancellationToken ct, string? workDir = null)
    {
        var all = new List<string> { "-hide_banner", "-nostats", "-progress", "pipe:1" };
        all.AddRange(args);
        using var p = new Process { StartInfo = CreateStartInfo(FfmpegPath, all, workDir) };
        if (!p.Start()) throw new FfmpegException("FFmpeg konnte nicht gestartet werden.");
        p.StandardInput.Close();
        var errBuilder = new StringBuilder();
        var errTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                lock (errBuilder)
                {
                    errBuilder.AppendLine(line);
                    if (errBuilder.Length > 64000) errBuilder.Remove(0, errBuilder.Length - 32000);
                }
            }
        });
        var outTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) || line.StartsWith("out_time_ms=", StringComparison.Ordinal))
                {
                    var v = line[(line.IndexOf('=') + 1)..];
                    if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && totalSeconds > 0)
                        progress?.Report(Math.Clamp(us / 1_000_000.0 / totalSeconds, 0, 1));
                }
            }
        });
        using (ct.Register(() => TryKill(p)))
        {
            try { await p.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { TryKill(p); throw; }
        }
        await Task.WhenAll(errTask, outTask).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
        {
            string err;
            lock (errBuilder) err = errBuilder.ToString();
            throw new FfmpegException("FFmpeg-Fehler (Code " + p.ExitCode + "):\n" + Tail(err, 2500));
        }
        progress?.Report(1);
    }

    public static string Tail(string text, int max) => text.Length <= max ? text : "…" + text[^max..];

    public static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(true); } catch { }
    }

    public static string Hex(string color)
    {
        var c = color.Trim().TrimStart('#');
        if (c.Length == 8) c = c[2..]; // ARGB -> RGB
        if (c.Length != 6) c = "FFFFFF";
        return "0x" + c.ToUpperInvariant();
    }
}
