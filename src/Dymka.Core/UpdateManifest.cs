using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dymka.Core;

/// <summary>Файл выпуска в манифесте: имя, размер и SHA-256.</summary>
public sealed record UpdateFile(string Name, long Size, string Sha256);

/// <summary>Манифест обновления update.json: версия, файлы с контрольными суммами и подпись.
/// Подпись ставит сборка на GitHub закрытым ключом, «Дымка» проверяет её открытым ключом (вшит в программу).
/// Файл с любого источника (GitHub или запасного) ставится, только если подпись и сумма сходятся.</summary>
public sealed class UpdateManifest
{
    public int Format { get; set; } = 1;
    public string Version { get; set; } = "";
    public List<UpdateFile> Files { get; set; } = new();
    public string Signature { get; set; } = "";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Что именно подписывается: версия и файлы в порядке имён.</summary>
    public string Canonical()
    {
        var sb = new StringBuilder("dymka-update\n").Append(Version).Append('\n');
        foreach (var f in Files.OrderBy(f => f.Name, StringComparer.Ordinal))
            sb.Append(f.Name).Append(' ').Append(f.Size).Append(' ').Append(f.Sha256.ToLowerInvariant()).Append('\n');
        return sb.ToString();
    }

    public void Sign(ECDsa key) =>
        Signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(Canonical()), HashAlgorithmName.SHA256));

    public bool Verify(string publicKeyBase64)
    {
        if (string.IsNullOrEmpty(Signature)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(Canonical()), Convert.FromBase64String(Signature), HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }

    public UpdateFile? File(string name) => Files.FirstOrDefault(f => f.Name == name);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static UpdateManifest? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<UpdateManifest>(json); }
        catch { return null; }
    }

    public static string Sha256Of(string path)
    {
        using var fs = System.IO.File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    public static UpdateFile Describe(string path) => new(Path.GetFileName(path), new FileInfo(path).Length, Sha256Of(path));
}
