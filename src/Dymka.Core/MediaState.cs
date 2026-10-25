using System.Text.Json;

namespace Dymka.Core;

/// <summary>Что играет на вкладке (по данным страницы: Media Session и элементы audio/video).</summary>
public sealed record MediaState(bool Has, bool Playing, string Title, string Artist, string Art, bool Video, bool CanPrev, bool CanNext)
{
    /// <summary>Разбор ответа скрипта страницы. Мусор и пустой ответ → null.</summary>
    public static MediaState? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null") return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.String) return Parse(r.GetString());   // ExecuteScriptAsync отдаёт строку в кавычках
            if (r.ValueKind != JsonValueKind.Object) return null;
            bool B(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            string S(string n, int max) { var s = r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : ""; s = s.Trim(); return s.Length > max ? s[..max] : s; }
            var art = S("art", 2048);
            if (!(art.StartsWith("https://") || art.StartsWith("http://"))) art = "";   // data:, blob: и прочее - не загружаем
            return new MediaState(B("has"), B("playing"), S("title", 200), S("artist", 200), art, B("video"), B("prev"), B("next"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Мини-плеер нужен для звука без видео (видео уходит в «картинку в картинке»).</summary>
    public bool WantsBar => Has && !Video;
}
