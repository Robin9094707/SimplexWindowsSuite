using SimplexSuite.Core.Common;

namespace SimplexSuite.Core.Transcription;

public sealed record WhisperModelInfo(string FileName, string DisplayName, int SizeMb, string RamHint, string Description)
{
    public string Url => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + FileName;
    public string LocalPath => Path.Combine(AppPaths.ModelsDir, FileName);
    public bool IsInstalled => File.Exists(LocalPath) && new FileInfo(LocalPath).Length > SizeMb * 1024L * 1024 * 8 / 10;
}

public static class WhisperModels
{
    public static readonly IReadOnlyList<WhisperModelInfo> All = new[]
    {
        new WhisperModelInfo("ggml-tiny.bin", "Tiny", 75, "≈ 0,4 GB RAM", "Sehr schnell, für Entwürfe"),
        new WhisperModelInfo("ggml-base.bin", "Base", 142, "≈ 0,5 GB RAM", "Schnell, gute Grundqualität"),
        new WhisperModelInfo("ggml-small.bin", "Small", 466, "≈ 1 GB RAM", "Ausgewogen – empfohlen für ältere PCs"),
        new WhisperModelInfo("ggml-large-v3-turbo-q5_0.bin", "Large v3 Turbo (Q5)", 547, "≈ 1,5 GB RAM", "Beste Qualität für 8-GB-PCs – empfohlen"),
        new WhisperModelInfo("ggml-medium.bin", "Medium", 1462, "≈ 2,6 GB RAM", "Sehr genau, langsamer"),
        new WhisperModelInfo("ggml-large-v3-turbo.bin", "Large v3 Turbo", 1549, "≈ 3 GB RAM", "Höchste Genauigkeit, ab 16 GB RAM"),
    };

    public static WhisperModelInfo? Find(string fileName) => All.FirstOrDefault(m => m.FileName == fileName);

    public static IEnumerable<WhisperModelInfo> Installed => All.Where(m => m.IsInstalled);

    public static async Task DownloadAsync(WhisperModelInfo model, IProgress<double>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SimplexSuite/1.0");
        using var resp = await http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? model.SizeMb * 1024L * 1024;
        var tmp = model.LocalPath + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = File.Create(tmp))
        {
            var buffer = new byte[1 << 16];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                progress?.Report((double)read / total);
            }
        }
        File.Move(tmp, model.LocalPath, true);
    }

    public static void Delete(WhisperModelInfo model)
    {
        if (File.Exists(model.LocalPath)) File.Delete(model.LocalPath);
    }

    public static readonly IReadOnlyList<(string Code, string Name)> Languages = new[]
    {
        ("auto", "Automatisch erkennen"), ("de", "Deutsch"), ("en", "Englisch"), ("fr", "Französisch"), ("es", "Spanisch"),
        ("it", "Italienisch"), ("nl", "Niederländisch"), ("pl", "Polnisch"), ("pt", "Portugiesisch"), ("tr", "Türkisch"),
        ("ru", "Russisch"), ("uk", "Ukrainisch"), ("cs", "Tschechisch"), ("sv", "Schwedisch"), ("da", "Dänisch"),
        ("no", "Norwegisch"), ("fi", "Finnisch"), ("el", "Griechisch"), ("hu", "Ungarisch"), ("ro", "Rumänisch"),
        ("ar", "Arabisch"), ("zh", "Chinesisch"), ("ja", "Japanisch"), ("ko", "Koreanisch"), ("hi", "Hindi"),
    };
}
