using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Dymka.Core;

/// <summary>Скачивание большого файла с докачкой: при обрыве связи продолжает с того же места (заголовок Range),
/// делает несколько попыток с паузами, а если данные перестали идти - обрывает зависшее соединение и пробует снова.
/// В конце сверяет размер и SHA-256 (если известны).</summary>
public static class ResumableDownload
{
    public sealed class Options
    {
        public int Attempts { get; init; } = 6;
        /// <summary>Сколько ждать новых данных, прежде чем считать соединение зависшим.</summary>
        public TimeSpan Stall { get; init; } = TimeSpan.FromSeconds(45);
        /// <summary>Пауза перед повтором: 2, 4, 8… секунд (не больше 30).</summary>
        public Func<int, TimeSpan> Backoff { get; init; } = n => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, n)));
    }

    /// <summary>Скачать url в path. expectedSize/expectedSha256 - для проверки (0 / null - не проверять).
    /// progress(получено, всего) вызывается по мере скачивания. Возвращает null при успехе или текст ошибки.</summary>
    public static async Task<string?> RunAsync(HttpClient http, string url, string path, long expectedSize, string? expectedSha256,
        Action<long, long>? progress = null, Action<string>? log = null, Options? options = null, CancellationToken ct = default)
    {
        options ??= new Options();
        var part = path + ".part";
        string? lastError = null;
        for (int attempt = 1; attempt <= options.Attempts; attempt++)
        {
            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (expectedSize > 0 && have > expectedSize) { File.Delete(part); have = 0; }
            if (expectedSize > 0 && have == expectedSize) break;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stall.CancelAfter(options.Stall);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stall.Token);
                if (have > 0 && resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // сервер считает, что докачивать нечего - начинаем заново
                    File.Delete(part);
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                bool resumed = have > 0 && resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
                if (have > 0 && !resumed) have = 0;   // сервер не умеет докачку - качаем целиком
                long total = expectedSize > 0 ? expectedSize : (resp.Content.Headers.ContentLength ?? 0) + have;
                if (resumed) log?.Invoke($"докачка с {have / 1048576} МБ");
                await using (var fs = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                await using (var src = await resp.Content.ReadAsStreamAsync(stall.Token))
                {
                    var buf = new byte[1 << 16];
                    while (true)
                    {
                        stall.CancelAfter(options.Stall);
                        int n = await src.ReadAsync(buf, stall.Token);
                        if (n == 0) break;
                        await fs.WriteAsync(buf.AsMemory(0, n), ct);
                        have += n;
                        progress?.Invoke(have, total);
                    }
                }
                if (expectedSize > 0 && have < expectedSize)
                {
                    lastError = $"соединение закрылось на {have / 1048576} из {expectedSize / 1048576} МБ";
                    log?.Invoke(lastError);
                    continue;
                }
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                lastError = Describe(ex);
                log?.Invoke($"попытка {attempt} из {options.Attempts}: {lastError}");
                if (attempt < options.Attempts) await Task.Delay(options.Backoff(attempt), ct);
            }
        }
        if (!File.Exists(part)) return lastError ?? "файл не скачан";
        long size = new FileInfo(part).Length;
        if (expectedSize > 0 && size != expectedSize) return lastError ?? $"скачано {size} байт вместо {expectedSize}";
        if (!string.IsNullOrEmpty(expectedSha256))
        {
            string sha;
            await using (var fs = File.OpenRead(part)) sha = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            if (!sha.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                return "контрольная сумма скачанного файла не совпала - файл повреждён или подменён";
            }
        }
        File.Move(part, path, true);
        return null;
    }

    /// <summary>Понятное описание ошибки вместе с внутренними причинами (например, почему не установилось защищённое соединение).</summary>
    public static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null && parts.Count < 4; e = e.InnerException)
        {
            var m = e is OperationCanceledException ? "данные перестали поступать (зависшее соединение)" : e.Message;
            if (!parts.Contains(m)) parts.Add(m);
        }
        return string.Join(" → ", parts);
    }
}
