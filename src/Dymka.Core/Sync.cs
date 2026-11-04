using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dymka.Core;

/// <summary>Одна синхронизируемая запись: пароль, карточка, банковская карта, плитки контейнера или закреплённые вкладки.
/// Stamp - когда запись в последний раз изменили (на любом компьютере); побеждает более свежая.</summary>
public sealed class SyncRecord
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public DateTime Stamp { get; set; }
    public string Json { get; set; } = "";
}

/// <summary>Файл компьютера в наборе (содержимое до шифрования): все записи набора и отметки об удалении.</summary>
public sealed class SyncDocument
{
    public int Format { get; set; } = 1;
    public string Computer { get; set; } = "";
    public DateTime Written { get; set; }
    public List<SyncRecord> Records { get; set; } = new();
    /// <summary>Удалённые записи: id → когда удалили. Нужны, чтобы удаление на одном компьютере дошло до остальных.</summary>
    public Dictionary<string, DateTime> Deleted { get; set; } = new();
}

/// <summary>Что этот компьютер знал после прошлой синхронизации (хранится локально, без секретов):
/// по отпечатку записи видно, изменили ли её здесь, а по пропавшим - что её здесь удалили.</summary>
public sealed class SyncState
{
    public string Set { get; set; } = "";
    public Dictionary<string, SyncSeen> Seen { get; set; } = new();
    public Dictionary<string, DateTime> Deleted { get; set; } = new();
    /// <summary>Отпечаток того, что этот компьютер записал в свой файл в прошлый раз (без изменений файл не переписывается).</summary>
    public string LastWritten { get; set; } = "";
}

public sealed class SyncSeen
{
    public string Mac { get; set; } = "";
    public DateTime Stamp { get; set; }
}

/// <summary>Служебный файл набора «набор.dat»: соль и проверочная метка пароля (сам пароль не хранится).</summary>
public sealed class SyncSetInfo
{
    public int Format { get; set; } = 1;
    public string Salt { get; set; } = "";
    public int Iter { get; set; } = SyncCrypto.Iterations;
    public string Check { get; set; } = "";
    public DateTime Created { get; set; }
}

/// <summary>Шифрование набора: ключ из пароля синхронизации (PBKDF2-SHA256, 600 000 итераций - перебор паролей
/// идёт медленно), данные - AES-256-GCM (подменённый или повреждённый файл не расшифруется).</summary>
public static class SyncCrypto
{
    public const int Iterations = 600_000;
    public const int MinPasswordLength = 12;
    static readonly byte[] Magic = Encoding.ASCII.GetBytes("DYMKASYNC1");
    static readonly byte[] Aad = Encoding.UTF8.GetBytes("dymka-sync-v1");
    const string CheckText = "dymka-sync-check";

    public static string? CheckPassword(string password)
    {
        if (password.Length < MinPasswordLength)
            return $"Пароль синхронизации - не короче {MinPasswordLength} символов: файлы набора лежат в общем Dropbox, и короткий пароль можно подобрать.";
        if (password.Distinct().Count() < 5) return "Пароль слишком простой: в нём почти одни одинаковые символы.";
        return null;
    }

    public static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    public static byte[] Encrypt(byte[] key, byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var g = new AesGcm(key, 16)) g.Encrypt(nonce, plain, cipher, tag, Aad);
        var res = new byte[Magic.Length + 12 + 16 + cipher.Length];
        Magic.CopyTo(res, 0);
        nonce.CopyTo(res, Magic.Length);
        tag.CopyTo(res, Magic.Length + 12);
        cipher.CopyTo(res, Magic.Length + 28);
        return res;
    }

    /// <summary>Расшифровать. Чужой ключ, повреждение или подмена - CryptographicException.</summary>
    public static byte[] Decrypt(byte[] key, byte[] data)
    {
        if (data.Length < Magic.Length + 28 || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new CryptographicException("это не файл синхронизации «Дымки»");
        var nonce = data.AsSpan(Magic.Length, 12);
        var tag = data.AsSpan(Magic.Length + 12, 16);
        var cipher = data.AsSpan(Magic.Length + 28);
        var plain = new byte[cipher.Length];
        using var g = new AesGcm(key, 16);
        g.Decrypt(nonce, cipher, tag, plain, Aad);
        return plain;
    }

    public static (SyncSetInfo info, byte[] key) CreateSet(string password, DateTime now)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = DeriveKey(password, salt, Iterations);
        var info = new SyncSetInfo
        {
            Salt = Convert.ToBase64String(salt), Iter = Iterations, Created = now,
            Check = Convert.ToBase64String(Encrypt(key, Encoding.UTF8.GetBytes(CheckText))),
        };
        return (info, key);
    }

    /// <summary>Ключ набора по паролю или null, если пароль не подходит.</summary>
    public static byte[]? OpenSet(SyncSetInfo info, string password)
    {
        var key = DeriveKey(password, Convert.FromBase64String(info.Salt), info.Iter);
        try
        {
            var check = Encoding.UTF8.GetString(Decrypt(key, Convert.FromBase64String(info.Check)));
            return check == CheckText ? key : null;
        }
        catch (CryptographicException) { return null; }
    }

    /// <summary>Отпечаток записи с ключом набора: по нему видно изменение, но содержимое из него не восстановить.</summary>
    public static string Mac(byte[] key, string json) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(json)).AsSpan(0, 16));

    public static byte[] Seal(byte[] key, SyncDocument doc) => Encrypt(key, JsonSerializer.SerializeToUtf8Bytes(doc));
    public static SyncDocument Open(byte[] key, byte[] data) => JsonSerializer.Deserialize<SyncDocument>(Decrypt(key, data)) ?? new SyncDocument();

    /// <summary>Имя папки набора или компьютера: буквы, цифры, пробел, дефис, подчёркивание и точка.</summary>
    public static string SafeName(string name)
    {
        var s = new string(name.Trim().Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' ? c : '_').ToArray()).Trim('.', ' ');
        return s.Length > 40 ? s[..40] : s;
    }
}

/// <summary>Итог слияния: что применить на этом компьютере и что записать в свой файл.</summary>
public sealed class SyncMergeResult
{
    public SyncDocument Document { get; init; } = new();
    /// <summary>Записи с других компьютеров, которые новее здешних (или которых здесь нет).</summary>
    public List<SyncRecord> Apply { get; } = new();
    /// <summary>Здешние записи, удалённые на другом компьютере.</summary>
    public List<string> Remove { get; } = new();
    /// <summary>Изменённые здесь записи (для журнала).</summary>
    public int LocalChanges { get; set; }
}

/// <summary>Слияние данных компьютеров набора: по каждой записи побеждает самая свежая правка,
/// удаление побеждает, если сделано позже последней правки. Ничего не теряется молча: пропавший файл
/// другого компьютера просто не участвует в слиянии.</summary>
public static class SyncMerge
{
    public static readonly TimeSpan KeepDeleted = TimeSpan.FromDays(365);

    /// <param name="local">Текущие данные этого компьютера: id → (вид, JSON).</param>
    public static SyncMergeResult Merge(IReadOnlyDictionary<string, (string kind, string json)> local, SyncState state,
        IEnumerable<SyncDocument> remotes, Func<string, string> mac, DateTime now, string computer)
    {
        var best = new Dictionary<string, (SyncRecord rec, bool remote)>();
        int localChanges = 0;
        foreach (var (id, (kind, json)) in local)
        {
            var m = mac(json);
            DateTime stamp;
            if (state.Seen.TryGetValue(id, out var seen) && seen.Mac == m) stamp = seen.Stamp;
            else { stamp = now; localChanges++; }
            best[id] = (new SyncRecord { Id = id, Kind = kind, Stamp = stamp, Json = json }, false);
        }
        var deleted = new Dictionary<string, DateTime>(state.Deleted);
        // здесь была, а теперь нет - значит, удалили на этом компьютере
        foreach (var id in state.Seen.Keys)
            if (!local.ContainsKey(id)) { Max(deleted, id, now); localChanges++; }

        foreach (var rd in remotes)
        {
            foreach (var (id, t) in rd.Deleted) Max(deleted, id, t);
            foreach (var r in rd.Records)
                if (!best.TryGetValue(r.Id, out var cur) || r.Stamp > cur.rec.Stamp)
                    best[r.Id] = (r, true);
        }

        var apply = new List<SyncRecord>();
        var remove = new List<string>();
        foreach (var (id, (rec, remote)) in best.ToList())
        {
            if (deleted.TryGetValue(id, out var del) && del >= rec.Stamp)
            {
                best.Remove(id);
                if (local.ContainsKey(id)) remove.Add(id);
                continue;
            }
            if (remote && (!local.TryGetValue(id, out var l) || l.json != rec.Json)) apply.Add(rec);
        }
        foreach (var id in deleted.Where(d => now - d.Value > KeepDeleted).Select(d => d.Key).ToList()) deleted.Remove(id);

        var doc = new SyncDocument
        {
            Computer = computer, Written = now, Deleted = deleted,
            Records = best.Values.Select(v => v.rec).OrderBy(r => r.Id, StringComparer.Ordinal).ToList(),
        };
        var result = new SyncMergeResult { Document = doc, LocalChanges = localChanges };
        result.Apply.AddRange(apply);
        result.Remove.AddRange(remove);
        return result;
    }

    /// <summary>После применения: запомнить, какими записи стали (отпечатки уже здешних данных и время правок).</summary>
    public static SyncState Remember(SyncMergeResult result, IReadOnlyDictionary<string, (string kind, string json)> localAfter, Func<string, string> mac, string set)
    {
        var st = new SyncState { Set = set, Deleted = new(result.Document.Deleted) };
        foreach (var r in result.Document.Records)
            if (localAfter.TryGetValue(r.Id, out var l)) st.Seen[r.Id] = new SyncSeen { Mac = mac(l.json), Stamp = r.Stamp };
        return st;
    }

    static void Max(Dictionary<string, DateTime> d, string id, DateTime t)
    {
        if (!d.TryGetValue(id, out var cur) || t > cur) d[id] = t;
    }
}
