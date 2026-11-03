using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Dymka.Core;

namespace Dymka;

/// <summary>Найденная версия. ManifestUrl - update.json с размерами и суммами файлов, UpdateZipUrl - сжатый Dymka.exe
/// (в 2,5 раза меньше); у старых выпусков их нет - тогда качается сам Dymka.exe.</summary>
sealed record UpdateInfo(Version Version, string PageUrl, string? ExeUrl, string? ZipUrl, string? ManifestUrl = null, string? UpdateZipUrl = null);

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

        string? exe = null, zip = null, manifest = null, upd = null;
        if (root.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString();
                var url = a.GetProperty("browser_download_url").GetString();
                if (name == "Dymka.exe") exe = url;
                else if (name == "Dymka-folder.zip") zip = url;
                else if (name == "update.json") manifest = url;
                else if (name == "Dymka-update.zip") upd = url;
            }
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? UpdateCheck.TagUrl(v) : UpdateCheck.TagUrl(v);
        return new UpdateResult(new UpdateInfo(v, page, exe, zip, manifest, upd), null);
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
            UpdateCheck.DownloadUrl(v, "Dymka.exe"), UpdateCheck.DownloadUrl(v, "Dymka-folder.zip"),
            UpdateCheck.DownloadUrl(v, "update.json"), UpdateCheck.DownloadUrl(v, "Dymka-update.zip")), null);
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

    /// <summary>true, если папка с программой закрыта для записи (например, Program Files) -
    /// тогда для замены файлов Windows спросит разрешение администратора.</summary>
    public static bool NeedsAdmin
    {
        get
        {
            var exePath = Environment.ProcessPath;
            return !string.IsNullOrEmpty(exePath) && !CanWrite(Path.GetDirectoryName(exePath)!);
        }
    }

    public enum PrepareResult { Ready, Cancelled, Failed }

    /// <summary>Почему не удалось скачать обновление (для журнала и сообщения).</summary>
    public static string LastError { get; private set; } = "";

    /// <summary>Клиент для скачивания: без общего таймаута (большой файл на медленной связи качается долго),
    /// зависшее соединение обрывает сама докачка.</summary>
    static HttpClient NewDownloadClient()
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Dymka/" + Current);
        return http;
    }

    /// <summary>update.json выпуска (null - его нет или не скачался: тогда качаем без сверки сумм, как раньше).</summary>
    static async Task<UpdateManifest?> FetchManifestAsync(HttpClient http, UpdateInfo u)
    {
        if (u.ManifestUrl == null) return null;
        for (int i = 1; i <= 3; i++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var json = await http.GetStringAsync(u.ManifestUrl, cts.Token);
                var m = UpdateManifest.Parse(json);
                if (m != null && UpdateCheck.ParseTag(m.Version) == u.Version) return m;
                App.Log("Обновление: манифест выпуска не подходит к версии " + u.Version);
                return null;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
            catch (Exception ex)
            {
                App.Log($"Обновление: манифест, попытка {i}: {ResumableDownload.Describe(ex)}");
                await Task.Delay(TimeSpan.FromSeconds(2 * i));
            }
        }
        return null;
    }

    static async Task DownloadChecked(HttpClient http, string url, string path, UpdateFile? info, Action<string>? status, string what)
    {
        DateTime last = DateTime.MinValue;
        var err = await ResumableDownload.RunAsync(http, url, path, info?.Size ?? 0, info?.Sha256,
            progress: (got, total) =>
            {
                if (DateTime.Now - last < TimeSpan.FromMilliseconds(400)) return;
                last = DateTime.Now;
                status?.Invoke(total > 0 ? $"Скачиваю обновление: {got / 1048576} из {total / 1048576} МБ" : $"Скачиваю обновление: {got / 1048576} МБ");
            },
            log: m => App.Log($"Обновление, {what}: {m}"));
        if (err != null) throw new IOException(err);
    }

    /// <summary>Скачивает обновление и запускает замену файлов после закрытия браузера.
    /// Если папка программы защищена (Program Files), файлы заменяет PowerShell с правами администратора
    /// (окно разрешения Windows), а браузер после обновления запускается снова уже без этих прав.</summary>
    public static async Task<PrepareResult> PrepareAsync(UpdateInfo u, Action<string>? status = null)
    {
        LastError = "";
        try { return await PrepareCoreAsync(u, status); }
        catch (Exception ex)
        {
            LastError = ResumableDownload.Describe(ex);
            App.Log("Обновление не скачано: " + LastError);
            return PrepareResult.Failed;
        }
    }

    static async Task<PrepareResult> PrepareCoreAsync(UpdateInfo u, Action<string>? status)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return PrepareResult.Failed;
        var appDir = Path.GetDirectoryName(exePath)!;
        bool admin = !CanWrite(appDir);

        var work = Path.Combine(Path.GetTempPath(), "dymka-update-" + u.Version);
        Directory.CreateDirectory(work);
        var flag = Path.Combine(work, "done.flag");
        try { File.Delete(flag); } catch { }
        using var http = NewDownloadClient();
        string copy;
        int pid = Environment.ProcessId;
        var manifest = await FetchManifestAsync(http, u);
        App.Log($"Обновление до {u.Version}: " + (manifest != null ? "манифест получен, файлы сверяются по контрольным суммам" : "манифеста нет, качаю как раньше"));

        if (IsSingleFile)
        {
            var newExe = Path.Combine(work, "Dymka.exe");
            if (manifest?.File("Dymka-update.zip") is { } zipInfo && u.UpdateZipUrl != null)
            {
                // сжатый файл: ~90 МБ вместо ~240
                var zipPath = Path.Combine(work, "Dymka-update.zip");
                await DownloadChecked(http, u.UpdateZipUrl, zipPath, zipInfo, status, "Dymka-update.zip");
                status?.Invoke("Распаковываю обновление…");
                await Task.Run(() =>
                {
                    using var z = System.IO.Compression.ZipFile.OpenRead(zipPath);
                    var entry = z.Entries.FirstOrDefault(e => e.Name.Equals("Dymka.exe", StringComparison.OrdinalIgnoreCase))
                                ?? throw new IOException("в архиве обновления нет Dymka.exe");
                    entry.ExtractToFile(newExe, true);
                });
                var exeInfo = manifest.File("Dymka.exe");
                if (exeInfo != null && !UpdateManifest.Sha256Of(newExe).Equals(exeInfo.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("распакованный Dymka.exe не совпал с выпуском по контрольной сумме");
                try { File.Delete(zipPath); } catch { }
            }
            else
            {
                if (u.ExeUrl == null) return PrepareResult.Failed;
                await DownloadChecked(http, u.ExeUrl, newExe, manifest?.File("Dymka.exe"), status, "Dymka.exe");
            }
            copy = $"Copy-Item -LiteralPath '{Esc(newExe)}' -Destination '{Esc(exePath)}' -Force -ErrorAction Stop";
        }
        else
        {
            if (u.ZipUrl == null) return PrepareResult.Failed;
            var zip = Path.Combine(work, "Dymka-folder.zip");
            await DownloadChecked(http, u.ZipUrl, zip, manifest?.File("Dymka-folder.zip"), status, "Dymka-folder.zip");
            var unpacked = Path.Combine(work, "unpacked");
            if (Directory.Exists(unpacked)) Directory.Delete(unpacked, true);
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, unpacked);
            var src = Path.Combine(unpacked, "Dymka");
            if (!Directory.Exists(src)) src = unpacked;
            copy = $"Copy-Item -Path '{Esc(src)}\\*' -Destination '{Esc(appDir)}' -Recurse -Force -ErrorAction Stop";
        }

        // замена файлов: ждём закрытия браузера, пробуем до 20 раз (файл может быть ещё занят), потом - отметка «готово»
        var copier =
            $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 700; " +
            $"for ($i = 0; $i -lt 20; $i++) {{ try {{ {copy}; break }} catch {{ Start-Sleep -Milliseconds 500 }} }}; " +
            $"Set-Content -LiteralPath '{Esc(flag)}' -Value 'ok'";
        if (!admin) copier += $"; Start-Process -FilePath '{Esc(exePath)}'";
        var copierPs1 = Path.Combine(work, "update.ps1");
        await File.WriteAllTextAsync(copierPs1, copier, new System.Text.UTF8Encoding(true));

        if (!admin)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{copierPs1}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return PrepareResult.Ready;
        }

        // защищённая папка: замена - с правами администратора, запуск браузера - отдельно, с обычными правами
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{copierPs1}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            App.Log("Обновление: разрешение администратора не дано");
            return PrepareResult.Cancelled;
        }
        var launcher =
            $"$until = (Get-Date).AddMinutes(3); " +
            $"while (-not (Test-Path -LiteralPath '{Esc(flag)}') -and (Get-Date) -lt $until) {{ Start-Sleep -Milliseconds 400 }}; " +
            $"Start-Process -FilePath '{Esc(exePath)}'";
        var launcherPs1 = Path.Combine(work, "restart.ps1");
        await File.WriteAllTextAsync(launcherPs1, launcher, new System.Text.UTF8Encoding(true));
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{launcherPs1}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        App.Log("Обновление: папка программы защищена, замена файлов - с правами администратора");
        return PrepareResult.Ready;
    }

    static string Esc(string path) => path.Replace("'", "''");

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
