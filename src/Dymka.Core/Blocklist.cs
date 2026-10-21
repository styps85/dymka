using System.Reflection;
using System.Text.RegularExpressions;

namespace Dymka.Core;

/// <summary>Список доменов трекеров и рекламы. Совпадение по домену и всем его поддоменам.</summary>
public sealed class Blocklist
{
    readonly HashSet<string> _block = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _allow = new(StringComparer.OrdinalIgnoreCase);

    static readonly Regex DomainRx = new(@"^([a-z0-9_-]+\.)+[a-z0-9-]{2,}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public int Count => _block.Count;

    /// <summary>Добавляет строку любого из поддерживаемых форматов:
    /// «domain.com», «0.0.0.0 domain.com», «||domain.com^», «||domain.com^$third-party», «@@||domain.com^».</summary>
    public void AddLine(string? line)
    {
        if (line == null) return;
        var s = line.Trim();
        if (s.Length == 0 || s[0] == '!' || s[0] == '#' || s[0] == '[') return;

        bool allow = false;
        if (s.StartsWith("@@")) { allow = true; s = s[2..]; }

        if (s.StartsWith("||"))
        {
            s = s[2..];
            int caret = s.IndexOf('^');
            if (caret <= 0) return;
            string opts = s[(caret + 1)..];
            s = s[..caret];
            if (opts.Length > 0)
            {
                if (!opts.StartsWith("$")) return;      // путь после домена - не доменное правило
                var o = opts[1..].ToLowerInvariant();
                if (allow)
                {
                    if (o != "document" && o != "third-party" && o != "important") return;
                }
                else if (o != "third-party" && o != "3p" && o != "important" && o != "third-party,important")
                    return;                              // правило с ограничениями (типы, домены) - пропускаем
            }
        }
        else if (allow)
        {
            return;
        }
        else
        {
            var parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && (parts[0] == "0.0.0.0" || parts[0] == "127.0.0.1" || parts[0] == "::1" || parts[0] == "::"))
                s = parts[1];
            else if (parts.Length == 1)
                s = parts[0];
            else
                return;
            int hash = s.IndexOf('#');
            if (hash >= 0) s = s[..hash];
        }

        s = s.Trim().TrimEnd('.').ToLowerInvariant();
        if (s is "localhost" or "local" or "0.0.0.0" or "broadcasthost") return;
        if (!DomainRx.IsMatch(s)) return;
        (allow ? _allow : _block).Add(s);
    }

    public void AddLines(IEnumerable<string> lines)
    {
        foreach (var l in lines) AddLine(l);
    }

    public bool IsBlocked(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        host = host.ToLowerInvariant().TrimEnd('.');
        bool blocked = false;
        string cur = host;
        while (true)
        {
            if (_allow.Contains(cur)) return false;
            if (!blocked && _block.Contains(cur)) blocked = true;
            int dot = cur.IndexOf('.');
            if (dot < 0 || cur.IndexOf('.', dot + 1) < 0) break;   // дальше остался бы только домен верхнего уровня
            cur = cur[(dot + 1)..];
        }
        return blocked;
    }

    public static IEnumerable<string> BaseListLines()
    {
        var asm = typeof(Blocklist).Assembly;
        using var stream = asm.GetManifestResourceStream("Dymka.Core.base-blocklist.txt");
        if (stream == null) yield break;
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) != null) yield return line;
    }

    /// <summary>Встроенный список плюс скачанные списки из папки кэша.</summary>
    public static Blocklist Build(string? cacheFolder)
    {
        var list = new Blocklist();
        list.AddLines(BaseListLines());
        if (cacheFolder != null && Directory.Exists(cacheFolder))
        {
            foreach (var f in Directory.GetFiles(cacheFolder, "*.txt"))
            {
                try { list.AddLines(File.ReadLines(f)); } catch { }
            }
        }
        return list;
    }
}
