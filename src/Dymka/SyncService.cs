using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Dymka.Core;

namespace Dymka;

/// <summary>Синхронизация паролей, карточек, банковских карт, плиток и закреплённых вкладок между компьютерами одного человека
/// через папку (Dropbox): папка набора, в ней «набор.dat» и по зашифрованному файлу на каждый компьютер.
/// Каждый компьютер пишет только свой файл и читает остальные; данные шифруются ключом из пароля синхронизации.</summary>
static class SyncService
{
    public const string InfoFile = "набор.dat";
    public const string Ext = ".dymka";

    static AppSettings _s = null!;
    static string _dataDir = "";
    static DispatcherTimer? _timer, _debounce;
    static FileSystemWatcher? _watch;
    static bool _running;

    public static string Status { get; private set; } = "Синхронизация не настроена";
    public static event Action? StatusChanged;

    static string StatePath => Path.Combine(_dataDir, "sync-state.json");
    public static string SetDir => Path.Combine(_s.SyncFolder, _s.SyncSet);
    public static string OwnFileName => SyncCrypto.SafeName(Environment.MachineName) + "-" + _s.SyncDeviceId + Ext;

    /// <summary>Запуск при старте браузера: проверка каждые 45 секунд и сразу, когда в папке меняется файл другого компьютера.</summary>
    public static void Start(AppSettings settings, string dataDir)
    {
        _s = settings;
        _dataDir = dataDir;
        if (_s.SyncDeviceId.Length == 0) { _s.SyncDeviceId = Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant(); _s.Save(); }
        _timer ??= new DispatcherTimer(TimeSpan.FromSeconds(45), DispatcherPriority.Background, (_, _) => RunOnce("по расписанию"), Dispatcher.CurrentDispatcher);
        _debounce ??= new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background, (_, _) => { _debounce!.Stop(); RunOnce("изменился файл другого компьютера"); }, Dispatcher.CurrentDispatcher);
        _debounce.Stop();
        Rewatch();
        if (_s.SyncConfigured) { _timer.Start(); SetStatus("Синхронизация включена: набор «" + _s.SyncSet + "»"); }
        else _timer.Stop();
    }

    static void Rewatch()
    {
        try { _watch?.Dispose(); } catch { }
        _watch = null;
        if (!_s.SyncConfigured || !Directory.Exists(SetDir)) return;
        try
        {
            var dispatcher = Application.Current.Dispatcher;
            _watch = new FileSystemWatcher(SetDir, "*" + Ext) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            FileSystemEventHandler h = (_, e) =>
            {
                if (string.Equals(Path.GetFileName(e.FullPath), OwnFileName, StringComparison.OrdinalIgnoreCase)) return;
                dispatcher.BeginInvoke(new Action(() => { _debounce?.Stop(); _debounce?.Start(); }));
            };
            _watch.Changed += h;
            _watch.Created += h;
            _watch.Renamed += (s, e) => h(s, e);
            _watch.EnableRaisingEvents = true;
        }
        catch (Exception ex) { App.Log("Синхронизация: не удалось следить за папкой: " + ex.Message); }
    }

    static void SetStatus(string s)
    {
        Status = s;
        StatusChanged?.Invoke();
    }

    static byte[]? Key()
    {
        try { return new Dpapi().Unprotect(Convert.FromBase64String(_s.SyncKey)); }
        catch { return null; }
    }

    // ===================================================================== настройка

    /// <summary>Наборы, уже созданные в папке (имена подпапок с «набор.dat»).</summary>
    public static List<string> ExistingSets(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.GetDirectories(folder).Where(d => File.Exists(Path.Combine(d, InfoFile))).Select(Path.GetFileName).OfType<string>().OrderBy(n => n).ToList()
                : new List<string>();
        }
        catch { return new List<string>(); }
    }

    public static SyncSetInfo? ReadInfo(string folder, string set)
    {
        try { return JsonSerializer.Deserialize<SyncSetInfo>(File.ReadAllText(Path.Combine(folder, set, InfoFile))); }
        catch { return null; }
    }

    /// <summary>Создать набор (папку и «набор.dat») и подключить к нему этот компьютер. null - успешно, иначе текст ошибки.</summary>
    public static string? Create(string folder, string set, string password)
    {
        set = SyncCrypto.SafeName(set);
        if (set.Length == 0) return "Пустое имя набора";
        var dir = Path.Combine(folder, set);
        if (File.Exists(Path.Combine(dir, InfoFile))) return $"Набор «{set}» уже есть - подключитесь к нему своим паролем";
        var bad = SyncCrypto.CheckPassword(password);
        if (bad != null) return bad;
        try
        {
            Directory.CreateDirectory(dir);
            var (info, key) = SyncCrypto.CreateSet(password, DateTime.UtcNow);
            File.WriteAllText(Path.Combine(dir, InfoFile), JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
            Connect(folder, set, key);
            App.Log($"Синхронизация: создан набор «{set}» в {folder}");
            return null;
        }
        catch (Exception ex) { return "Не удалось создать набор: " + ex.Message; }
    }

    /// <summary>Подключиться к набору по паролю. null - успешно, иначе текст ошибки.</summary>
    public static string? Join(string folder, string set, string password)
    {
        var info = ReadInfo(folder, set);
        if (info == null) return $"В папке нет набора «{set}»";
        var key = SyncCrypto.OpenSet(info, password);
        if (key == null) return "Пароль не подходит к этому набору";
        Connect(folder, set, key);
        App.Log($"Синхронизация: компьютер подключён к набору «{set}»");
        return null;
    }

    static void Connect(string folder, string set, byte[] key)
    {
        _s.SyncFolder = folder;
        _s.SyncSet = set;
        _s.SyncKey = Convert.ToBase64String(new Dpapi().Protect(key));
        _s.Save();
        try { File.Delete(StatePath); } catch { }   // новый набор - начинаем с чистого состояния
        Start(_s, _dataDir);
    }

    public static void Disconnect()
    {
        App.Log("Синхронизация отключена (набор «" + _s.SyncSet + "»)");
        _s.SyncKey = "";
        _s.SyncSet = "";
        _s.Save();
        try { File.Delete(StatePath); } catch { }
        _timer?.Stop();
        try { _watch?.Dispose(); } catch { }
        _watch = null;
        SetStatus("Синхронизация не настроена");
    }

    // ===================================================================== данные этого компьютера

    static readonly JsonSerializerOptions Json = new();

    static Dictionary<string, (string kind, string json)> Collect(VaultData d)
    {
        var r = new Dictionary<string, (string, string)>();
        foreach (var p in d.Passwords) r["pw:" + p.Id] = ("password", JsonSerializer.Serialize(p, Json));
        foreach (var c in d.Cards) r["card:" + c.Id] = ("card", JsonSerializer.Serialize(c, Json));
        foreach (var b in d.BankCards) r["bank:" + b.Id] = ("bankcard", JsonSerializer.Serialize(b, Json));
        foreach (var c in AppSettings.Containers) r["tiles:" + c.Id] = ("tiles", JsonSerializer.Serialize(_s.ShortcutsFor(c.Id), Json));
        r["pinned"] = ("pinned", JsonSerializer.Serialize(_s.Pinned, Json));
        return r;
    }

    /// <summary>Первая синхронизация компьютера: одинаковые записи, заведённые на разных компьютерах вручную
    /// (тот же сайт и логин, та же карточка или карта), получают общий номер - иначе после слияния были бы дубли.</summary>
    static void AdoptIds(VaultData d, IEnumerable<SyncDocument> remotes)
    {
        foreach (var rec in remotes.SelectMany(x => x.Records))
        {
            try
            {
                switch (rec.Kind)
                {
                    case "password":
                        var p = JsonSerializer.Deserialize<PasswordEntry>(rec.Json);
                        var lp = p == null ? null : d.Passwords.FirstOrDefault(x => x.Id != p.Id && x.Host == p.Host && x.Username == p.Username);
                        if (lp != null && d.Passwords.All(x => x.Id != p!.Id)) lp.Id = p!.Id;
                        break;
                    case "card":
                        var c = JsonSerializer.Deserialize<Card>(rec.Json);
                        var lc = c == null ? null : d.Cards.FirstOrDefault(x => x.Id != c.Id && x.Title == c.Title && x.FullName == c.FullName);
                        if (lc != null && d.Cards.All(x => x.Id != c!.Id)) lc.Id = c!.Id;
                        break;
                    case "bankcard":
                        var b = JsonSerializer.Deserialize<BankCard>(rec.Json);
                        var lb = b == null ? null : d.BankCards.FirstOrDefault(x => x.Id != b.Id && x.Number == b.Number);
                        if (lb != null && d.BankCards.All(x => x.Id != b!.Id)) lb.Id = b!.Id;
                        break;
                }
            }
            catch { }
        }
    }

    static void Replace<T>(List<T> list, Func<T, string> id, T item)
    {
        int i = list.FindIndex(x => id(x) == id(item));
        if (i >= 0) list[i] = item; else list.Add(item);
    }

    /// <summary>Применить изменения с других компьютеров. Возвращает, что изменилось (для сообщения).</summary>
    static (int vault, bool tiles, List<PinnedSite>? pinned) Apply(VaultData d, SyncMergeResult res)
    {
        int vault = 0;
        bool tiles = false;
        List<PinnedSite>? pinned = null;
        foreach (var rec in res.Apply)
        {
            try
            {
                switch (rec.Kind)
                {
                    case "password": Replace(d.Passwords, x => x.Id, JsonSerializer.Deserialize<PasswordEntry>(rec.Json)!); vault++; break;
                    case "card": Replace(d.Cards, x => x.Id, JsonSerializer.Deserialize<Card>(rec.Json)!); vault++; break;
                    case "bankcard": Replace(d.BankCards, x => x.Id, JsonSerializer.Deserialize<BankCard>(rec.Json)!); vault++; break;
                    case "tiles":
                        _s.ShortcutsByContainer[rec.Id["tiles:".Length..]] = JsonSerializer.Deserialize<List<Shortcut>>(rec.Json) ?? new();
                        tiles = true;
                        break;
                    case "pinned": pinned = JsonSerializer.Deserialize<List<PinnedSite>>(rec.Json) ?? new(); break;
                }
            }
            catch (Exception ex) { App.Log($"Синхронизация: запись {rec.Kind} не применена: {ex.Message}"); }
        }
        foreach (var id in res.Remove)
        {
            int n = id.StartsWith("pw:") ? d.Passwords.RemoveAll(x => "pw:" + x.Id == id)
                  : id.StartsWith("card:") ? d.Cards.RemoveAll(x => "card:" + x.Id == id)
                  : id.StartsWith("bank:") ? d.BankCards.RemoveAll(x => "bank:" + x.Id == id) : 0;
            vault += n;
        }
        return (vault, tiles, pinned);
    }

    // ===================================================================== синхронизация

    /// <summary>Один проход: прочитать файлы других компьютеров, слить, применить у себя и записать свой файл.
    /// Работает в потоке окна (файлы небольшие). Ничего не делает, если хранилище закрыто главным паролем.</summary>
    public static void RunOnce(string reason)
    {
        if (_running || _s == null || !_s.SyncConfigured) return;
        _running = true;
        try { RunCore(reason); }
        catch (Exception ex)
        {
            App.Log("Синхронизация: " + ex);
            SetStatus("Ошибка синхронизации: " + ex.Message);
        }
        finally { _running = false; }
    }

    static void RunCore(string reason)
    {
        var v = Shared.Vault;
        var d = v.Peek;
        if (d == null)
        {
            SetStatus("Ждёт: пароли закрыты главным паролем - синхронизация продолжится, когда вы их откроете");
            return;
        }
        var key = Key();
        if (key == null) { SetStatus("Ключ синхронизации недоступен на этом компьютере - подключитесь к набору заново"); return; }
        if (!File.Exists(Path.Combine(SetDir, InfoFile)))
        {
            SetStatus($"Папка набора «{_s.SyncSet}» не найдена ({SetDir}). Данные на этом компьютере не тронуты.");
            return;
        }
        if (_watch == null) Rewatch();

        // файлы других компьютеров
        var remotes = new List<SyncDocument>();
        int unread = 0;
        foreach (var f in Directory.GetFiles(SetDir, "*" + Ext))
        {
            if (string.Equals(Path.GetFileName(f), OwnFileName, StringComparison.OrdinalIgnoreCase)) continue;
            try { remotes.Add(SyncCrypto.Open(key, File.ReadAllBytes(f))); }
            catch (Exception ex)
            {
                unread++;
                App.Log($"Синхронизация: файл {Path.GetFileName(f)} пропущен ({ex.GetType().Name}: {ex.Message}) - возможно, Dropbox ещё его загружает");
            }
        }

        SyncState state;
        try { state = File.Exists(StatePath) ? JsonSerializer.Deserialize<SyncState>(File.ReadAllText(StatePath)) ?? new() : new(); }
        catch { state = new(); }
        if (state.Set != _s.SyncSet) state = new SyncState { Set = _s.SyncSet };
        bool first = state.Seen.Count == 0;
        if (first) AdoptIds(d, remotes);

        string Mac(string j) => SyncCrypto.Mac(key, j);
        var local = Collect(d);
        if (first)
        {
            // первое подключение: если запись уже есть в наборе, верна версия набора (здешняя могла устареть)
            var known = remotes.SelectMany(x => x.Records).Select(x => x.Id).ToHashSet();
            foreach (var (id, (_, json)) in local)
                if (known.Contains(id)) state.Seen[id] = new SyncSeen { Mac = Mac(json), Stamp = DateTime.MinValue };
        }
        var now = DateTime.UtcNow;
        var res = SyncMerge.Merge(local, state, remotes, Mac, now, Environment.MachineName);

        var (vaultChanged, tilesChanged, pinned) = Apply(d, res);
        if (vaultChanged > 0) v.Save();
        if (pinned != null) Shared.Windows.FirstOrDefault(w => !w.Incognito)?.ApplySyncedPinned(pinned);
        if (tilesChanged) Shared.Windows.FirstOrDefault()?.BroadcastStartDataFromSync();
        if (tilesChanged || pinned != null) _s.Save();

        var after = Collect(d);
        var st = SyncMerge.Remember(res, after, Mac, _s.SyncSet);
        // свой файл переписывается, только если его содержимое изменилось (или файла нет)
        var content = Mac(JsonSerializer.Serialize(res.Document.Records) + JsonSerializer.Serialize(res.Document.Deleted.OrderBy(x => x.Key)));
        var own = Path.Combine(SetDir, OwnFileName);
        if (content != state.LastWritten || !File.Exists(own))
        {
            var tmp = Path.Combine(SetDir, "~" + OwnFileName + ".tmp");
            File.WriteAllBytes(tmp, SyncCrypto.Seal(key, res.Document));
            File.Move(tmp, own, true);
        }
        st.LastWritten = content;
        File.WriteAllText(StatePath, JsonSerializer.Serialize(st));

        _s.SyncLast = DateTime.Now;
        int computers = remotes.Count + 1;
        int got = res.Apply.Count + res.Remove.Count;
        if (got > 0 || res.LocalChanges > 0 || first)
            App.Log($"Синхронизация ({reason}): компьютеров {computers}, получено изменений {got}, отправлено {res.LocalChanges}" + (unread > 0 ? $", не прочитано файлов {unread}" : ""));
        SetStatus($"Синхронизировано в {DateTime.Now:HH:mm}: набор «{_s.SyncSet}», компьютеров {computers}" +
                  (got > 0 ? $", получено изменений: {got}" : "") + (unread > 0 ? $" (файл другого компьютера пока не прочитан - Dropbox его ещё загружает)" : ""));
    }

    /// <summary>Для самопроверки: записать файл «другого компьютера».</summary>
    public static void WriteRemoteForTest(string computer, SyncDocument doc)
    {
        var key = Key()!;
        File.WriteAllBytes(Path.Combine(SetDir, SyncCrypto.SafeName(computer) + Ext), SyncCrypto.Seal(key, doc));
    }

    public static SyncDocument ReadOwnForTest() => SyncCrypto.Open(Key()!, File.ReadAllBytes(Path.Combine(SetDir, OwnFileName)));
}
