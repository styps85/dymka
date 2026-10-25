using System.Text.RegularExpressions;

namespace Dymka.Core;

/// <summary>Разбор ответов GitHub при проверке обновлений (без сети - для тестов).</summary>
public static class UpdateCheck
{
    public const string Repo = "styps85/dymka";
    public const string ApiLatest = "https://api.github.com/repos/" + Repo + "/releases/latest";
    /// <summary>Обычная страница: отвечает перенаправлением на .../releases/tag/v1.0.N; лимита API у неё нет.</summary>
    public const string PageLatest = "https://github.com/" + Repo + "/releases/latest";

    /// <summary>Номер версии из тега выпуска («v1.0.54» → 1.0.54). Служебные теги (selftest и т.п.) → null.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim().TrimStart('v', 'V');
        if (!Regex.IsMatch(t, @"^\d+\.\d+(\.\d+)?$")) return null;
        return Version.TryParse(t, out var v) ? Normalize(v) : null;
    }

    /// <summary>Номер версии из адреса страницы выпуска (https://github.com/.../releases/tag/v1.0.54).</summary>
    public static Version? FromReleaseUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var m = Regex.Match(url, @"/releases/tag/([^/?#]+)");
        return m.Success ? ParseTag(Uri.UnescapeDataString(m.Groups[1].Value)) : null;
    }

    /// <summary>Major.Minor.Build без четвёртой части (сборка ставит 1.0.N, сборщик .NET добавляет .0).</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public static string TagUrl(Version v) => $"https://github.com/{Repo}/releases/tag/v{v}";
    public static string DownloadUrl(Version v, string file) => $"https://github.com/{Repo}/releases/download/v{v}/{file}";

    /// <summary>Понятная причина отказа GitHub по коду ответа и заголовкам лимита.</summary>
    public static string DescribeHttpFailure(int status, string? rateRemaining, string? rateReset, DateTimeOffset now)
    {
        bool limited = (status == 403 || status == 429) && (rateRemaining == "0" || status == 429);
        if (limited)
        {
            var when = "";
            if (long.TryParse(rateReset, out var unix))
            {
                var mins = (int)Math.Ceiling((DateTimeOffset.FromUnixTimeSeconds(unix) - now).TotalMinutes);
                if (mins > 0) when = $" Ограничение снимется примерно через {mins} мин.";
            }
            return "GitHub временно ограничил число проверок с вашего интернет-адреса (не больше 60 в час; " +
                   "у мобильного интернета и многих провайдеров один адрес на много абонентов)." + when;
        }
        if (status == 403) return "GitHub отказал в доступе (код 403). Возможно, запрос блокирует провайдер, антивирус или прокси.";
        if (status == 404) return "Страница выпусков не найдена (код 404).";
        if (status >= 500) return $"Сервер GitHub временно недоступен (код {status}).";
        return $"GitHub ответил ошибкой (код {status}).";
    }
}
