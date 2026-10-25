using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Dymka.Core;

namespace Dymka;

sealed record UpdateInfo(Version Version, string PageUrl, string? ExeUrl, string? ZipUrl);

/// <summary>Итог проверки: Update - новая версия (или null, если её нет); Error - проверить не удалось.</summary>
sealed record UpdateResult(UpdateInfo? Update, string? Error)
{
    public static readonly UpdateResult UpToDate = new(null, null);
}

/// <summary>Проверка новой версии в выпусках GitHub и установка с перезапуском.</summary>
static class UpdateChecker
{
    public const string ReleasesPage = UpdateCheck.PageLatest;

    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return UpdateCheck.Normalize(v);
        }
    }

    /// <summary>true, если программа собрана одним файлом (иначе - версия-папка).</summary>
    public static bool IsSingleFile => string.IsNullOrEmpty(typeof(UpdateChecker).Assembly.Location);

    static HttpClient NewClient(int seconds, bool redirects = true)
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = redirects }) { Timeout = TimeSpan.FromSeconds(seconds) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Dymka/" + Current);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Проверка в два шага: API GitHub, а если он отказал (лимит 60 запросов в час на адрес,
    /// блокировка, сбой) - по перенаправлению обычной страницы выпусков, у которой лимита нет.
    /// Ошибка возвращается, только если не сработали оба способа.</summary>
    public static async Task<UpdateResult> CheckAsync()
    {
        string apiError;
        try
        {
            var r = await CheckApiAsync();
            if (r.Error == null) return r;
            apiError = r.Error;
        }
        catch (Exception ex) { apiError = Explain(ex); }
        App.Log("Проверка обновлений через API GitHub не удалась: " + apiError + " Пробую страницу выпусков.");

        try
        {
            var r = await CheckPageAsync();
            if (r.Error == null)
            {
                App.Log("Проверка обновлений по странице выпусков: " + (r.Update != null ? "есть версия " + r.Update.Version : "новых версий нет"));
                return r;
            }
            App.Log("Проверка обновлений по странице выпусков не удалась: " + r.Error);
            return new UpdateResult(null, apiError);
        }
        catch (Exception ex)
        {
            App.Log("Проверка обновлений по странице выпусков не удалась: " + Explain(ex));
            return new UpdateResult(null, apiError);
        }
    }

    internal static async Task<UpdateResult> CheckApiAsync()
    {
        using var http = NewClient(20);
        using var resp = await http.GetAsync(UpdateCheck.ApiLatest);
        if (!resp.IsSuccessStatusCode)
        {
            var remaining = Header(resp, "X-RateLimit-Remaining");
            var reset = Header(resp, "X-RateLimit-Reset");
            App.Log($"API GitHub: код {(int)resp.StatusCode}, осталось запросов {remaining ?? "?"}, сброс {reset ?? "?"}");
            return new UpdateResult(null, UpdateCheck.DescribeHttpFailure((int)resp.StatusCode, remaining, reset, DateTimeOffset.UtcNow));
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var v = UpdateCheck.ParseTag(root.TryGetProperty("tag_name", out var t) ? t.GetString() : null);
        if (v == null) return new UpdateResult(null, "GitHub вернул выпуск без номера версии.");
        if (v <= Current) return UpdateResult.UpToDate;

        string? exe = null, zip = null;
        if (root.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString();
                var url = a.GetProperty("browser_download_url").GetString();
                if (name == "Dymka.exe") exe = url;
                else if (name == "Dymka-folder.zip") zip = url;
            }
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? UpdateCheck.TagUrl(v) : UpdateCheck.TagUrl(v);
        return new UpdateResult(new UpdateInfo(v, page, exe, zip), null);
    }

    /// <summary>github.com/.../releases/latest отвечает перенаправлением на .../releases/tag/v1.0.N.</summary>
    internal static async Task<UpdateResult> CheckPageAsync()
    {
        using var http = NewClient(20, redirects: false);
        using var req = new HttpRequestMessage(HttpMethod.Head, UpdateCheck.PageLatest);
        using var resp = await http.SendAsync(req);
        var location = resp.Headers.Location;
        if (location != null && !location.IsAbsoluteUri) location = new Uri(new Uri(UpdateCheck.PageLatest), location);
        var v = UpdateCheck.FromReleaseUrl(location?.ToString());
        if (v == null)
            return new UpdateResult(null, (int)resp.StatusCode is >= 300 and < 400
                ? "страница выпусков перенаправила на неожиданный адрес " + location
                : UpdateCheck.DescribeHttpFailure((int)resp.StatusCode, null, null, DateTimeOffset.UtcNow));
        if (v <= Current) return UpdateResult.UpToDate;
        // Ссылки на файлы выпуска предсказуемы и тоже не расходуют лимит API.
        return new UpdateResult(new UpdateInfo(v, UpdateCheck.TagUrl(v),
            UpdateCheck.DownloadUrl(v, "Dymka.exe"), UpdateCheck.DownloadUrl(v, "Dymka-folder.zip")), null);
    }

    static string? Header(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var vals) ? vals.FirstOrDefault() : null;

    static string Explain(Exception ex) => ex switch
    {
        TaskCanceledException => "GitHub не ответил за 20 секунд (медленная связь или блокировка).",
        HttpRequestException h when h.InnerException is System.Security.Authentication.AuthenticationException =>
            "не удалось установить защищённое соединение с GitHub (антивирус или прокси подменяет сертификат): " + h.Message,
        HttpRequestException h => "нет связи с GitHub: " + h.Message,
        _ => ex.Message,
    };

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
