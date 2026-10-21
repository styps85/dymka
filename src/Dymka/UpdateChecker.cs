using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Dymka;

sealed record UpdateInfo(Version Version, string PageUrl, string? ExeUrl, string? ZipUrl);

/// <summary>Проверка новой версии в выпусках GitHub и установка с перезапуском.</summary>
static class UpdateChecker
{
    const string Api = "https://api.github.com/repos/styps85/dymka/releases/latest";
    public const string ReleasesPage = "https://github.com/styps85/dymka/releases/latest";

    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>true, если программа собрана одним файлом (иначе - версия-папка).</summary>
    public static bool IsSingleFile => string.IsNullOrEmpty(typeof(UpdateChecker).Assembly.Location);

    static HttpClient NewClient(int seconds)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(seconds) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Dymka/" + Current);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Новая версия или null (нет обновления, нет сети, репозиторий закрыт).</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var http = NewClient(20);
        using var resp = await http.GetAsync(Api);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
        v = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        if (v <= Current) return null;

        string? exe = null, zip = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString();
            var url = a.GetProperty("browser_download_url").GetString();
            if (name == "Dymka.exe") exe = url;
            else if (name == "Dymka-folder.zip") zip = url;
        }
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;
        return new UpdateInfo(v, page, exe, zip);
    }

    /// <summary>Скачивает обновление и запускает замену файлов после закрытия браузера.
    /// Возвращает false, если обновить автоматически нельзя (тогда открываем страницу выпуска).</summary>
    public static async Task<bool> PrepareAsync(UpdateInfo u)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return false;
        var appDir = Path.GetDirectoryName(exePath)!;
        if (!CanWrite(appDir)) return false;

        var work = Path.Combine(Path.GetTempPath(), "dymka-update-" + u.Version);
        Directory.CreateDirectory(work);
        using var http = NewClient(600);
        string script;
        int pid = Environment.ProcessId;

        if (IsSingleFile)
        {
            if (u.ExeUrl == null) return false;
            var newExe = Path.Combine(work, "Dymka.exe");
            await Download(http, u.ExeUrl, newExe);
            script =
                $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 700; " +
                $"for ($i = 0; $i -lt 20; $i++) {{ try {{ Copy-Item -LiteralPath '{Esc(newExe)}' -Destination '{Esc(exePath)}' -Force -ErrorAction Stop; break }} catch {{ Start-Sleep -Milliseconds 500 }} }}; " +
                $"Start-Process -FilePath '{Esc(exePath)}'";
        }
        else
        {
            if (u.ZipUrl == null) return false;
            var zip = Path.Combine(work, "Dymka-folder.zip");
            await Download(http, u.ZipUrl, zip);
            var unpacked = Path.Combine(work, "unpacked");
            if (Directory.Exists(unpacked)) Directory.Delete(unpacked, true);
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, unpacked);
            var src = Path.Combine(unpacked, "Dymka");
            if (!Directory.Exists(src)) src = unpacked;
            script =
                $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 700; " +
                $"for ($i = 0; $i -lt 20; $i++) {{ try {{ Copy-Item -Path '{Esc(src)}\\*' -Destination '{Esc(appDir)}' -Recurse -Force -ErrorAction Stop; break }} catch {{ Start-Sleep -Milliseconds 500 }} }}; " +
                $"Start-Process -FilePath '{Esc(exePath)}'";
        }

        var ps1 = Path.Combine(work, "update.ps1");
        await File.WriteAllTextAsync(ps1, script, new System.Text.UTF8Encoding(true));
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        return true;
    }

    static string Esc(string path) => path.Replace("'", "''");

    static async Task Download(HttpClient http, string url, string path)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(path + ".part");
        await resp.Content.CopyToAsync(fs);
        fs.Close();
        File.Move(path + ".part", path, true);
    }

    static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".dymka-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
}
