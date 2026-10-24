using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Dymka.Core;

/// <summary>Разбор адресной строки, очистка ссылок от меток слежения, домены.</summary>
public static class UrlTools
{
    public const string StartHost = "dymka.start";
    public const string StartUrl = "https://dymka.start/start.html";

    static readonly Regex HostLike = new(
        @"^(localhost|(\d{1,3}\.){3}\d{1,3}|([\p{L}\p{N}-]+\.)+\p{L}[\p{L}\p{N}-]+)(:\d{1,5})?([/?#].*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static readonly string[] KnownSchemes = { "http://", "https://", "file://", "about:", "data:", "view-source:" };

    /// <summary>Превращает ввод пользователя в адрес: ссылку или поисковый запрос.</summary>
    public static string? ToNavigable(string? input, string searchTemplate)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0) return null;

        foreach (var s in KnownSchemes)
            if (text.StartsWith(s, StringComparison.OrdinalIgnoreCase))
                return text;

        if (!text.Contains(' ') && HostLike.IsMatch(text))
            return (text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ? "http://" : "https://") + text;

        return string.Format(searchTemplate, Uri.EscapeDataString(text));
    }

    static readonly HashSet<string> TrackingParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "gclid", "dclid", "gbraid", "wbraid", "gclsrc", "fbclid", "yclid", "ysclid", "msclkid",
        "igshid", "mc_cid", "mc_eid", "_openstat", "_ga", "_gl", "oly_enc_id", "oly_anon_id",
        "vero_id", "vero_conv", "mkt_tok", "twclid", "ttclid", "li_fat_id", "rb_clickid",
        "s_cid", "wickedid", "_hsenc", "_hsmi", "hsctatracking", "cmpid", "srsltid", "erid",
    };

    public static bool IsTrackingParam(string name) =>
        name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) || TrackingParams.Contains(name);

    /// <summary>Возвращает адрес без меток слежения или null, если чистить нечего.</summary>
    public static string? CleanUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;

        int q = url.IndexOf('?');
        if (q < 0) return null;
        int hash = url.IndexOf('#', q);
        string query = hash < 0 ? url[(q + 1)..] : url[(q + 1)..hash];
        string fragment = hash < 0 ? "" : url[hash..];

        var parts = query.Split('&');
        var kept = new List<string>(parts.Length);
        bool changed = false;
        foreach (var p in parts)
        {
            if (p.Length == 0) { changed = true; continue; }
            int eq = p.IndexOf('=');
            string name = WebUtility.UrlDecode(eq < 0 ? p : p[..eq]);
            if (IsTrackingParam(name)) { changed = true; continue; }
            kept.Add(p);
        }
        if (!changed) return null;
        var sb = new StringBuilder(url[..q]);
        if (kept.Count > 0) sb.Append('?').Append(string.Join("&", kept));
        sb.Append(fragment);
        return sb.ToString();
    }

    static readonly HashSet<string> MultiPartSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.uk", "org.uk", "ac.uk", "gov.uk", "com.ru", "org.ru", "net.ru", "pp.ru", "msk.ru", "spb.ru",
        "gov.ru", "edu.ru", "ac.ru", "mil.ru", "nov.ru", "com.ua", "org.ua", "co.jp", "com.au", "com.br",
        "com.cn", "com.tr", "co.kr", "com.kz", "org.kz", "gov.kz", "by.ru", "net.ua", "co.il",
        "github.io", "blogspot.com", "appspot.com", "azurewebsites.net", "cloudfront.net",
        "herokuapp.com", "web.app", "firebaseapp.com", "pages.dev", "netlify.app", "vercel.app",
        "narod.ru", "ucoz.ru", "tilda.ws", "turbo.site",
    };

    /// <summary>«Регистрируемый» домен: sub.example.co.uk -> example.co.uk.</summary>
    public static string RegistrableDomain(string? host)
    {
        if (string.IsNullOrEmpty(host)) return "";
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(host.Trim('[', ']'), out _)) return host;
        var labels = host.Split('.');
        if (labels.Length <= 2) return host;
        string lastTwo = labels[^2] + "." + labels[^1];
        if (MultiPartSuffixes.Contains(lastTwo))
            return labels[^3] + "." + lastTwo;
        return lastTwo;
    }

    public static bool IsThirdParty(string requestHost, string pageHost) =>
        !string.Equals(RegistrableDomain(requestHost), RegistrableDomain(pageHost), StringComparison.OrdinalIgnoreCase);

    public static string HostOf(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : "";
    }

    public const string GalleryUrl = "https://dymka.start/gallery.html";

    /// <summary>Стартовая страница (новая вкладка). Другие страницы того же служебного адреса (галерея фонов) - не она.</summary>
    public static bool IsStartPage(string? url)
    {
        if (string.IsNullOrEmpty(url) || HostOf(url) != StartHost) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return true;
        return u.AbsolutePath is "/" or "" or "/start.html";
    }

    /// <summary>Галерея фонов (служебная страница браузера).</summary>
    public static bool IsGalleryPage(string? url) =>
        !string.IsNullOrEmpty(url) && HostOf(url) == StartHost && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.AbsolutePath == "/gallery.html";

    /// <summary>Любая служебная страница браузера (стартовая, галерея фонов).</summary>
    public static bool IsInternalPage(string? url) => !string.IsNullOrEmpty(url) && HostOf(url) == StartHost;

    /// <summary>Делит адрес на «домен» и «остальное» для красивого показа в адресной строке.</summary>
    public static (string host, string rest) SplitForDisplay(string? url)
    {
        if (string.IsNullOrEmpty(url) || IsStartPage(url) || url == "about:blank") return ("", "");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            return (url, "");
        string host = u.IdnHost == u.Host ? u.Host : TryUnicodeHost(u.Host);
        if (!u.IsDefaultPort) host += ":" + u.Port;
        string rest = u.PathAndQuery + u.Fragment;
        if (rest == "/") rest = "";
        try { rest = Uri.UnescapeDataString(rest); } catch { }
        return (host, rest);
    }

    static string TryUnicodeHost(string host)
    {
        try { return new IdnMapping().GetUnicode(host); } catch { return host; }
    }

    /// <summary>Совпадает ли хост с доменом из списка (сам домен или его поддомен).</summary>
    public static bool HostMatches(string host, IEnumerable<string> domains)
    {
        host = host.ToLowerInvariant().TrimEnd('.');
        foreach (var raw in domains)
        {
            var d = NormalizeDomain(raw);
            if (d.Length == 0) continue;
            if (host == d || host.EndsWith("." + d, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Приводит строку из списка («https://www.site.ru/путь», «.site.ru») к домену site.ru.</summary>
    public static string NormalizeDomain(string? raw)
    {
        var d = (raw ?? "").Trim().ToLowerInvariant();
        if (d.Length == 0) return "";
        if (d.Contains("://"))
            d = Uri.TryCreate(d, UriKind.Absolute, out var u) ? u.Host : d[(d.IndexOf("://", StringComparison.Ordinal) + 3)..];
        int slash = d.IndexOfAny(new[] { '/', '?', '#', ':' });
        if (slash >= 0) d = d[..slash];
        d = d.Trim('.');
        if (d.StartsWith("www.")) d = d[4..];
        return d;
    }

    /// <summary>Первая буква названия сайта для значка-заглушки.</summary>
    public static string InitialFor(string? title, string? url)
    {
        var host = HostOf(url);
        if (host.StartsWith("www.")) host = host[4..];
        var source = !string.IsNullOrWhiteSpace(title) ? title! : host;
        foreach (var ch in source)
            if (char.IsLetterOrDigit(ch)) return char.ToUpperInvariant(ch).ToString();
        return "•";
    }
}
