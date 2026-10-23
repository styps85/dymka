using System.Text;

namespace Dymka.Core;

public sealed class TranslationException(string message) : Exception(message);

/// <summary>Клиент бесплатного Яндекс Переводчика. Запросы идут без cookie и без данных браузера - только текст.</summary>
public sealed class YandexTranslator
{
    readonly HttpClient _http;
    readonly SemaphoreSlim _sidLock = new(1, 1);
    readonly SemaphoreSlim _parallel = new(3, 3);
    string? _sid;
    DateTime _sidAt;

    public string LastError { get; private set; } = "";
    /// <summary>Каким способом сработал последний запрос (POST или GET) - для самопроверки.</summary>
    public string LastMethod { get; private set; } = "";
    public int Requests { get; private set; }

    public YandexTranslator(HttpMessageHandler? handler = null)
    {
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
    }

    async Task<string?> GetSidAsync(bool refresh, CancellationToken ct)
    {
        await _sidLock.WaitAsync(ct);
        try
        {
            if (!refresh && _sid != null && DateTime.Now - _sidAt < TimeSpan.FromMinutes(20)) return _sid;
            foreach (var url in new[] { Translation.SidPageUrl, Translation.SidWidgetUrl })
            {
                try
                {
                    var sid = Translation.ExtractSid(await _http.GetStringAsync(url, ct));
                    if (sid != null)
                    {
                        _sid = sid;
                        _sidAt = DateTime.Now;
                        return sid;
                    }
                    LastError = "в ответе сервиса нет идентификатора сессии";
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    LastError = ex.Message;
                }
            }
            return null;
        }
        finally
        {
            _sidLock.Release();
        }
    }

    /// <summary>Переводит сегменты (куски внутри сегмента разделены &lt;wbr&gt;, текст экранирован как HTML).
    /// Возвращает для каждого сегмента массив переведённых кусков и язык оригинала.</summary>
    public async Task<(string[][] pieces, string sourceLang)> TranslateSegmentsAsync(IReadOnlyList<string> segments, string target,
        CancellationToken ct = default)
    {
        var result = new string[segments.Count][];
        string src = "";
        var tasks = Translation.Batch(segments).Select(async batch =>
        {
            var (texts, lang) = await RequestAsync(batch.Select(i => segments[i]).ToList(), target, ct);
            for (int k = 0; k < batch.Count; k++) result[batch[k]] = Translation.SplitPieces(texts[k]);
            if (lang.Length > 0) src = Translation.SourceOf(lang);
        });
        await Task.WhenAll(tasks);
        return (result, src);
    }

    /// <summary>Перевод обычного текста (выделенного на странице); строки переводятся по отдельности.</summary>
    public async Task<(string text, string sourceLang)> TranslateTextAsync(string text, string target, CancellationToken ct = default)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var idx = new List<int>();
        var segs = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            idx.Add(i);
            segs.Add(Translation.EscapePiece(lines[i].Trim()));
        }
        if (segs.Count == 0) return ("", "");
        var (pieces, src) = await TranslateSegmentsAsync(segs, target, ct);
        var outLines = lines.Select(l => l.Trim().Length == 0 ? "" : l).ToArray();
        for (int k = 0; k < idx.Count; k++) outLines[idx[k]] = string.Concat(pieces[k]);
        return (string.Join("\n", outLines).Trim(), src);
    }

    async Task<(List<string> texts, string lang)> RequestAsync(List<string> segs, string target, CancellationToken ct)
    {
        await _parallel.WaitAsync(ct);
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var sid = await GetSidAsync(attempt > 0, ct)
                          ?? throw new TranslationException("нет связи с Яндекс Переводчиком" + (LastError.Length > 0 ? ": " + LastError : ""));
                var url = Translation.BuildUrl(sid, target);
                var body = Translation.BuildBody(segs);
                string? json = null;
                try
                {
                    Requests++;
                    using var resp = await _http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"), ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        json = await resp.Content.ReadAsStringAsync(ct);
                        LastMethod = "POST";
                    }
                    else LastError = $"POST: ответ {(int)resp.StatusCode}";
                }
                catch (HttpRequestException ex) { LastError = ex.Message; }

                if (json == null && url.Length + body.Length < 7000)
                {
                    try
                    {
                        Requests++;
                        using var resp = await _http.GetAsync(url + "&" + body, ct);
                        if (resp.IsSuccessStatusCode)
                        {
                            json = await resp.Content.ReadAsStringAsync(ct);
                            LastMethod = "GET";
                        }
                        else LastError = $"GET: ответ {(int)resp.StatusCode}";
                    }
                    catch (HttpRequestException ex) { LastError = ex.Message; }
                }

                if (json != null)
                {
                    try
                    {
                        var (code, lang, texts) = Translation.ParseResponse(json);
                        if (code == 200 && texts.Count == segs.Count) return (texts, lang);
                        LastError = $"сервис ответил кодом {code}";
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        LastError = "непонятный ответ сервиса";
                    }
                }
            }
            throw new TranslationException(LastError);
        }
        finally
        {
            _parallel.Release();
        }
    }
}
