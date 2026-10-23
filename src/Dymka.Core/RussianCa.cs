using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Dymka.Core;

/// <summary>Российские TLS-сертификаты (Минцифры, НУЦ). «Дымка» доверяет им сама, без установки в Windows,
/// и только для российских сайтов: .ru, .рф, .su, .рус и сайтов из вашего списка.</summary>
public static class RussianCa
{
    /// <summary>Зоны, для которых действуют сертификаты Минцифры (в Punycode: рф = xn--p1ai, рус = xn--p1acf).</summary>
    public static readonly string[] Zones = { "ru", "xn--p1ai", "su", "xn--p1acf" };

    public static string ToAscii(string host)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        try { return new IdnMapping().GetAscii(host); } catch { return host; }
    }

    public static bool IsAllowedHost(string host, IEnumerable<string>? extraSites)
    {
        var h = ToAscii(host);
        if (h.Length == 0) return false;
        var tld = h[(h.LastIndexOf('.') + 1)..];
        if (Zones.Contains(tld)) return true;
        return extraSites != null && UrlTools.HostMatches(h, extraSites.Select(ToAscii));
    }

    public static X509Certificate2 FromPem(string pem) => X509Certificate2.CreateFromPem(pem);

    /// <summary>Проверка сертификата сайта по корневым сертификатам Минцифры: цепочка, срок действия,
    /// назначение «проверка подлинности сервера», имя сайта в сертификате.</summary>
    public static bool Verify(string leafPem, IEnumerable<string>? chainPems, string host,
        IReadOnlyCollection<X509Certificate2> roots, IReadOnlyCollection<X509Certificate2>? intermediates, out string reason)
    {
        reason = "";
        if (roots.Count == 0) { reason = "нет корневых сертификатов"; return false; }
        using var leaf = FromPem(leafPem);
        if (!leaf.MatchesHostname(ToAscii(host), allowWildcards: true, allowCommonName: false))
        {
            reason = "сертификат выдан другому сайту";
            return false;
        }
        using var chain = new X509Chain();
        var p = chain.ChainPolicy;
        p.TrustMode = X509ChainTrustMode.CustomRootTrust;
        p.CustomTrustStore.AddRange(roots.ToArray());
        if (intermediates != null) p.ExtraStore.AddRange(intermediates.ToArray());
        var extra = new List<X509Certificate2>();
        foreach (var pem in chainPems ?? Array.Empty<string>())
        {
            try { var c = FromPem(pem); extra.Add(c); p.ExtraStore.Add(c); } catch { }
        }
        p.RevocationMode = X509RevocationMode.NoCheck;
        p.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        try
        {
            if (!chain.Build(leaf))
            {
                reason = string.Join(", ", chain.ChainStatus.Select(s => s.Status.ToString()));
                return false;
            }
            var top = chain.ChainElements[^1].Certificate;
            if (!roots.Any(r => r.RawData.AsSpan().SequenceEqual(top.RawData)))
            {
                reason = "цепочка не ведёт к сертификату Минцифры";
                return false;
            }
            return true;
        }
        finally
        {
            foreach (var c in extra) c.Dispose();
        }
    }

    public static string Sha256(X509Certificate2 c) => Convert.ToHexString(SHA256.HashData(c.RawData));
}
