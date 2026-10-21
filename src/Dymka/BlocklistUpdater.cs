using System.Net.Http;

namespace Dymka;

/// <summary>Скачивает общедоступные списки блокировки (раз в неделю) в папку кэша.</summary>
static class BlocklistUpdater
{
    static readonly (string file, string url)[] Lists =
    {
        ("easyprivacy.txt", "https://easylist.to/easylist/easyprivacy.txt"),
        ("easylist.txt", "https://easylist.to/easylist/easylist.txt"),
        ("ruadlist.txt", "https://easylist-downloads.adblockplus.org/advblock.txt"),
        ("peterlowe.txt", "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=nohtml&showintro=0&mimetype=plaintext"),
    };

    /// <summary>Возвращает число успешно обновлённых списков.</summary>
    public static async Task<int> UpdateAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Dymka/1.0");
        int ok = 0;
        foreach (var (file, url) in Lists)
        {
            try
            {
                var text = await http.GetStringAsync(url);
                if (text.Length < 1000) continue;
                var path = Path.Combine(folder, file);
                await File.WriteAllTextAsync(path + ".tmp", text);
                File.Move(path + ".tmp", path, true);
                ok++;
            }
            catch (Exception ex)
            {
                App.Log($"Список {file}: {ex.Message}");
            }
        }
        return ok;
    }
}
