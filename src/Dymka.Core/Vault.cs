using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dymka.Core;

public sealed class PasswordEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = "";
    public string Host { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Note { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
    public DateTime LastUsed { get; set; } = DateTime.MinValue;
}

public sealed class CardAddress
{
    /// <summary>«Домашний», «Рабочий», «Почтовый», «Юридический» или своё название.</summary>
    public string Label { get; set; } = "Домашний";
    public string Postcode { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>Карточка автозаполнения: «Я», «АУ», должник и т.п.</summary>
public sealed class Card
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Новая карточка";
    public string LastName { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string MiddleName { get; set; } = "";
    /// <summary>Дата в виде дд.мм.гггг.</summary>
    public string BirthDate { get; set; } = "";
    public string Inn { get; set; } = "";
    public string Snils { get; set; } = "";
    public string PassportSeries { get; set; } = "";
    public string PassportNumber { get; set; } = "";
    public string PassportIssuedBy { get; set; } = "";
    public string PassportIssueDate { get; set; } = "";
    public string PassportCode { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    /// <summary>Регистрационный номер арбитражного управляющего.</summary>
    public string RegNumber { get; set; } = "";
    public string Sro { get; set; } = "";
    public List<CardAddress> Addresses { get; set; } = new();

    [JsonIgnore]
    public string FullName => string.Join(" ", new[] { LastName, FirstName, MiddleName }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));

    [JsonIgnore]
    public string ShortName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(LastName)) return FullName;
            string i(string s) => string.IsNullOrWhiteSpace(s) ? "" : char.ToUpper(s.Trim()[0]) + ".";
            return (LastName.Trim() + " " + i(FirstName) + i(MiddleName)).Trim();
        }
    }
}

public sealed class VaultData
{
    public List<PasswordEntry> Passwords { get; set; } = new();
    public List<Card> Cards { get; set; } = new();
    /// <summary>Сайты, для которых вы выбрали «Никогда не сохранять пароль».</summary>
    public List<string> NeverSave { get; set; } = new();
}

/// <summary>Защита ключа средствами Windows (DPAPI): расшифровать может только ваша учётная запись на этом компьютере.</summary>
public interface IKeyProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

public sealed class WrongPasswordException() : Exception("Неверный главный пароль");

public enum VaultState { Empty, Locked, Unlocked }

/// <summary>Зашифрованное хранилище паролей и карточек.
/// Данные шифруются AES-256-GCM случайным ключом. Ключ защищён DPAPI (учётная запись Windows),
/// а если задан главный пароль - ещё и ключом из пароля (PBKDF2-SHA256, 600 000 итераций).</summary>
public sealed class Vault
{
    public const int Iterations = 600_000;
    static readonly byte[] Aad = Encoding.UTF8.GetBytes("dymka-vault-v1");

    sealed class FileModel
    {
        public int V { get; set; } = 1;
        public bool Master { get; set; }
        public string Salt { get; set; } = "";
        public int Iter { get; set; } = Iterations;
        public string Key { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Data { get; set; } = "";
    }

    readonly string _path;
    readonly IKeyProtector _protector;
    FileModel? _file;
    byte[]? _key;
    VaultData? _data;

    public DateTime LastUsed { get; private set; } = DateTime.Now;

    public Vault(string path, IKeyProtector protector)
    {
        _path = path;
        _protector = protector;
        try
        {
            if (File.Exists(path)) _file = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path));
        }
        catch
        {
            _file = null;
        }
    }

    public bool Exists => _file != null;
    public bool HasMasterPassword => _file?.Master == true;
    public VaultState State => _data != null ? VaultState.Unlocked : _file == null ? VaultState.Empty : VaultState.Locked;
    public bool IsUnlocked => _data != null;

    /// <summary>Данные хранилища. Без главного пароля открывается само; с главным паролем - только после Unlock.</summary>
    public VaultData? Data
    {
        get
        {
            if (_data == null && !HasMasterPassword) TryOpenWithoutMaster();
            if (_data != null) LastUsed = DateTime.Now;
            return _data;
        }
    }

    void TryOpenWithoutMaster()
    {
        if (_file == null)
        {
            // первое обращение: создаём пустое хранилище
            _key = RandomNumberGenerator.GetBytes(32);
            _data = new VaultData();
            _file = new FileModel { Master = false, Key = Convert.ToBase64String(_protector.Protect(_key)) };
            Save();
            return;
        }
        try { Unlock(null); } catch { }
    }

    /// <summary>Открыть хранилище. Неверный главный пароль - WrongPasswordException.</summary>
    public void Unlock(string? masterPassword)
    {
        if (_file == null) { TryOpenWithoutMaster(); return; }
        byte[] wrapped = _protector.Unprotect(Convert.FromBase64String(_file.Key));
        byte[] key;
        if (_file.Master)
        {
            if (string.IsNullOrEmpty(masterPassword)) throw new WrongPasswordException();
            var kek = DeriveKek(masterPassword, Convert.FromBase64String(_file.Salt), _file.Iter);
            try { key = Decrypt(kek, wrapped, null); }
            catch (CryptographicException) { throw new WrongPasswordException(); }
            finally { CryptographicOperations.ZeroMemory(kek); }
        }
        else key = wrapped;

        VaultData data;
        if (string.IsNullOrEmpty(_file.Data)) data = new VaultData();
        else
        {
            var plain = Decrypt(key, Convert.FromBase64String(_file.Data), Convert.FromBase64String(_file.Nonce));
            data = JsonSerializer.Deserialize<VaultData>(plain) ?? new VaultData();
            CryptographicOperations.ZeroMemory(plain);
        }
        _key = key;
        _data = data;
        LastUsed = DateTime.Now;
    }

    /// <summary>Закрыть: ключ и расшифрованные данные стираются из памяти программы.</summary>
    public void Lock()
    {
        if (!HasMasterPassword) return;   // без главного пароля «замка» нет - хранилище защищено учётной записью Windows
        if (_key != null) CryptographicOperations.ZeroMemory(_key);
        _key = null;
        _data = null;
    }

    public void Save()
    {
        if (_data == null || _key == null || _file == null) return;
        var plain = JsonSerializer.SerializeToUtf8Bytes(_data);
        var nonce = RandomNumberGenerator.GetBytes(12);
        _file.Nonce = Convert.ToBase64String(nonce);
        _file.Data = Convert.ToBase64String(Encrypt(_key, plain, nonce));
        CryptographicOperations.ZeroMemory(plain);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_file));
        File.Move(tmp, _path, true);
        LastUsed = DateTime.Now;
    }

    /// <summary>Задать или сменить главный пароль (null - убрать). Хранилище должно быть открыто.</summary>
    public void SetMasterPassword(string? password)
    {
        _ = Data;
        if (_data == null || _key == null || _file == null) throw new InvalidOperationException("Хранилище закрыто");
        if (string.IsNullOrEmpty(password))
        {
            _file.Master = false;
            _file.Salt = "";
            _file.Key = Convert.ToBase64String(_protector.Protect(_key));
        }
        else
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var kek = DeriveKek(password, salt, Iterations);
            var wrapped = Encrypt(kek, _key, null);
            CryptographicOperations.ZeroMemory(kek);
            _file.Master = true;
            _file.Salt = Convert.ToBase64String(salt);
            _file.Iter = Iterations;
            _file.Key = Convert.ToBase64String(_protector.Protect(wrapped));
        }
        Save();
    }

    public bool CheckMasterPassword(string password)
    {
        if (_file == null || !_file.Master) return false;
        try
        {
            byte[] wrapped = _protector.Unprotect(Convert.FromBase64String(_file.Key));
            var kek = DeriveKek(password, Convert.FromBase64String(_file.Salt), _file.Iter);
            var key = Decrypt(kek, wrapped, null);
            CryptographicOperations.ZeroMemory(kek);
            CryptographicOperations.ZeroMemory(key);
            return true;
        }
        catch (CryptographicException) { return false; }
    }

    static byte[] DeriveKek(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>AES-256-GCM. Если nonce не задан, он случайный и хранится в начале результата.</summary>
    static byte[] Encrypt(byte[] key, byte[] plain, byte[]? nonce)
    {
        bool own = nonce == null;
        nonce ??= RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag, Aad);
        var result = new byte[(own ? 12 : 0) + cipher.Length + 16];
        int o = 0;
        if (own) { Buffer.BlockCopy(nonce, 0, result, 0, 12); o = 12; }
        Buffer.BlockCopy(cipher, 0, result, o, cipher.Length);
        Buffer.BlockCopy(tag, 0, result, o + cipher.Length, 16);
        return result;
    }

    static byte[] Decrypt(byte[] key, byte[] data, byte[]? nonce)
    {
        int o = 0;
        if (nonce == null)
        {
            nonce = data[..12];
            o = 12;
        }
        int len = data.Length - o - 16;
        if (len < 0) throw new CryptographicException("повреждённые данные");
        var plain = new byte[len];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, data.AsSpan(o, len), data.AsSpan(o + len, 16), plain, Aad);
        return plain;
    }

    // ===================================================================== пароли

    /// <summary>Пароли для сайта: сначала точное совпадение адреса сайта, затем тот же домен (поддомены).</summary>
    public static List<PasswordEntry> ForHost(IEnumerable<PasswordEntry> entries, string host)
    {
        host = host.ToLowerInvariant();
        var reg = UrlTools.RegistrableDomain(host);
        return entries
            .Where(e => e.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || UrlTools.RegistrableDomain(e.Host) == reg)
            .OrderByDescending(e => e.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(e => e.LastUsed)
            .ThenBy(e => e.Username)
            .ToList();
    }

    /// <summary>Сохранить пароль после входа на сайт. Возвращает "added", "updated" или "same".</summary>
    public static string Remember(VaultData data, string url, string username, string password)
    {
        var host = UrlTools.HostOf(url).ToLowerInvariant();
        var existing = data.Passwords.FirstOrDefault(e => e.Host == host && e.Username == username);
        if (existing != null)
        {
            if (existing.Password == password) return "same";
            existing.Password = password;
            existing.Updated = DateTime.Now;
            return "updated";
        }
        data.Passwords.Add(new PasswordEntry { Url = url, Host = host, Username = username, Password = password });
        return "added";
    }
}
