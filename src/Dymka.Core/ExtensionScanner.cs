using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dymka.Core;

public sealed class FoundExtension
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Version { get; init; } = "";
    public string Folder { get; init; } = "";
    public string? IconPath { get; init; }
    public string Source { get; init; } = "";
    public bool ForSignature { get; init; }
    public bool UsesNativeMessaging { get; init; }
}

/// <summary>Поиск расширений, установленных в других браузерах (Яндекс, Chrome, Edge, Opera и др.).</summary>
public static class ExtensionScanner
{
    static readonly string[] SignatureWords =
    {
        "криптопро", "cryptopro", "cades", "госуслуг", "gosuslugi", "контур", "kontur", "рутокен", "rutoken",
        "jacarta", "джакарта", "эцп", "эп ", "электронн", "signal-com", "vipnet", "inist", "инист", "лисси",
        "lissi", "sbis", "сбис", "тензор", "tensor", "infotecs", "ifcplugin", "plugin for chrome",
    };

    public static IEnumerable<(string browser, string extensionsDir)> KnownLocations(string localAppData, string roamingAppData)
    {
        var chromium = new (string name, string root)[]
        {
            ("Яндекс Браузер", Path.Combine(localAppData, "Yandex", "YandexBrowser", "User Data")),
            ("Google Chrome", Path.Combine(localAppData, "Google", "Chrome", "User Data")),
            ("Microsoft Edge", Path.Combine(localAppData, "Microsoft", "Edge", "User Data")),
            ("Brave", Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data")),
            ("Vivaldi", Path.Combine(localAppData, "Vivaldi", "User Data")),
            ("Chromium", Path.Combine(localAppData, "Chromium", "User Data")),
            ("Chromium-Gost", Path.Combine(localAppData, "Chromium-Gost", "User Data")),
        };
        foreach (var (name, root) in chromium)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> profiles;
            try
            {
                profiles = Directory.GetDirectories(root)
                    .Where(d => Path.GetFileName(d) is "Default" || Path.GetFileName(d).StartsWith("Profile ", StringComparison.Ordinal));
            }
            catch { continue; }
            foreach (var p in profiles)
            {
                var ext = Path.Combine(p, "Extensions");
                if (Directory.Exists(ext)) yield return (name, ext);
            }
        }
        foreach (var opera in new[] { "Opera Stable", "Opera GX Stable" })
        {
            var root = Path.Combine(roamingAppData, "Opera Software", opera);
            foreach (var ext in new[] { Path.Combine(root, "Extensions"), Path.Combine(root, "Default", "Extensions") })
                if (Directory.Exists(ext)) yield return ("Opera", ext);
        }
    }

    public static List<FoundExtension> ScanInstalledBrowsers(string localAppData, string roamingAppData)
    {
        var result = new Dictionary<string, FoundExtension>();
        foreach (var (browser, dir) in KnownLocations(localAppData, roamingAppData))
        {
            string[] ids;
            try { ids = Directory.GetDirectories(dir); } catch { continue; }
            foreach (var idDir in ids)
            {
                var id = Path.GetFileName(idDir);
                if (id.StartsWith("_") || id.StartsWith(".")) continue;
                var versionDir = LatestVersionDir(idDir);
                if (versionDir == null) continue;
                var info = ReadManifest(versionDir, browser, id);
                if (info == null) continue;
                if (!result.TryGetValue(info.Id, out var prev) || CompareVersions(info.Version, prev.Version) > 0)
                    result[info.Id] = info;
            }
        }
        return result.Values
            .OrderByDescending(e => e.ForSignature)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    static string? LatestVersionDir(string idDir)
    {
        try
        {
            return Directory.GetDirectories(idDir)
                .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
                .OrderByDescending(d => Path.GetFileName(d), Comparer<string>.Create(CompareVersions))
                .FirstOrDefault();
        }
        catch { return null; }
    }

    public static int CompareVersions(string a, string b)
    {
        static int[] Parts(string v) => Regex.Matches(v, @"\d+").Select(m => int.TryParse(m.Value, out var n) ? n : 0).ToArray();
        var x = Parts(a);
        var y = Parts(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }

    /// <summary>Читает manifest.json; null - не расширение (тема, служебный компонент) или файл повреждён.</summary>
    public static FoundExtension? ReadManifest(string folder, string source, string idFromPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var m = doc.RootElement;
            if (m.TryGetProperty("theme", out _)) return null;
            string defaultLocale = m.TryGetProperty("default_locale", out var dl) ? dl.GetString() ?? "en" : "en";
            string name = Localize(folder, defaultLocale, Str(m, "name"));
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("__MSG_")) return null;
            string desc = Localize(folder, defaultLocale, Str(m, "description"));
            string version = Str(m, "version");

            bool native = false;
            foreach (var key in new[] { "permissions", "optional_permissions" })
                if (m.TryGetProperty(key, out var perms) && perms.ValueKind == JsonValueKind.Array)
                    native |= perms.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.String && p.GetString() == "nativeMessaging");

            string? icon = null;
            if (m.TryGetProperty("icons", out var icons) && icons.ValueKind == JsonValueKind.Object)
            {
                var best = icons.EnumerateObject()
                    .Select(p => (size: int.TryParse(p.Name, out var s) ? s : 0, path: p.Value.GetString()))
                    .Where(p => p.path != null)
                    .OrderBy(p => Math.Abs(p.size - 48))
                    .FirstOrDefault();
                if (best.path != null)
                {
                    var full = Path.Combine(folder, best.path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(full)) icon = full;
                }
            }

            var hay = (name + " " + desc).ToLowerInvariant();
            bool signature = SignatureWords.Any(w => hay.Contains(w));
            return new FoundExtension
            {
                Id = idFromPath, Name = name.Trim(), Description = desc.Trim(), Version = version,
                Folder = folder, IconPath = icon, Source = source,
                ForSignature = signature, UsesNativeMessaging = native,
            };
        }
        catch
        {
            return null;
        }
    }

    static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Подставляет «__MSG_имя__» из _locales (сначала русский, потом язык по умолчанию).</summary>
    static string Localize(string folder, string defaultLocale, string text)
    {
        var mm = Regex.Match(text ?? "", @"^__MSG_(.+)__$");
        if (!mm.Success) return text ?? "";
        var key = mm.Groups[1].Value;
        foreach (var loc in new[] { "ru", defaultLocale, "en" })
        {
            var file = Path.Combine(folder, "_locales", loc, "messages.json");
            if (!File.Exists(file)) continue;
            try
            {
                using var d = JsonDocument.Parse(File.ReadAllText(file),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                foreach (var p in d.RootElement.EnumerateObject())
                    if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase) &&
                        p.Value.TryGetProperty("message", out var msg))
                        return msg.GetString() ?? "";
            }
            catch { }
        }
        return text ?? "";
    }

    /// <summary>Копирует расширение в папку «Дымки» (без служебной папки _metadata с подписью магазина).</summary>
    public static string CopyTo(FoundExtension ext, string extensionsRoot)
    {
        var dest = Path.Combine(extensionsRoot, ext.Id);
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        CopyDir(ext.Folder, dest);
        return dest;
    }

    static void CopyDir(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src))
        {
            var name = Path.GetFileName(d);
            if (name == "_metadata") continue;
            CopyDir(d, Path.Combine(dest, name));
        }
    }
}
