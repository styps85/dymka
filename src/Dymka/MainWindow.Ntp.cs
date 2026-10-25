using System.Net.Http;
using System.Text.Json;
using Dymka.Core;
using Microsoft.Web.WebView2.Core;

namespace Dymka;

/// <summary>Новая вкладка: фоны (стандартные, из галереи, свои), часы, меню настроек внизу справа, галерея фонов.</summary>
public partial class MainWindow
{
    /// <summary>Откуда галерея берёт каталог и картинки (открытый репозиторий «Дымки»).</summary>
    public const string GalleryBase = "https://raw.githubusercontent.com/styps85/dymka/main/wallpapers/";

    string WallDir => Path.Combine(_uiDir, "wallpapers");

    /// <summary>Распаковывает стандартные фоны и служебные страницы в папку ui (при каждом запуске, если изменились).</summary>
    void EnsureBuiltinWallpapers()
    {
        try
        {
            Directory.CreateDirectory(WallDir);
            var asm = typeof(MainWindow).Assembly;
            foreach (var res in asm.GetManifestResourceNames().Where(r => r.StartsWith("Dymka.Wallpapers.")))
            {
                var name = res["Dymka.Wallpapers.".Length..];
                var path = Path.Combine(WallDir, name);
                using var s = asm.GetManifestResourceStream(res)!;
                if (File.Exists(path) && new FileInfo(path).Length == s.Length) continue;
                using var f = File.Create(path);
                s.CopyTo(f);
            }
            // обои из прежней версии (файл wallpaper-*.jpg в папке ui) - в общую папку фонов
            if (_settings.Wallpaper.StartsWith("wallpaper-") && File.Exists(Path.Combine(_uiDir, _settings.Wallpaper)))
            {
                var moved = "custom-" + _settings.Wallpaper;
                File.Move(Path.Combine(_uiDir, _settings.Wallpaper), Path.Combine(WallDir, moved), true);
                _settings.Wallpaper = "wallpapers/" + moved;
                _settings.Save();
            }
            using var g = asm.GetManifestResourceStream("Dymka.Web.gallery.html");
            if (g != null)
            {
                using var f = File.Create(Path.Combine(_uiDir, "gallery.html"));
                g.CopyTo(f);
            }
        }
        catch (Exception ex) { App.Log("Стандартные фоны: " + ex.Message); }
    }

    /// <summary>Все фоны по порядку (имена файлов в папке wallpapers).</summary>
    List<string> WallpaperNames()
    {
        try { return Wallpapers.Order(Directory.Exists(WallDir) ? Directory.GetFiles(WallDir).Select(f => Path.GetFileName(f)) : Array.Empty<string>()); }
        catch { return new List<string>(); }
    }

    /// <summary>Текущий фон (путь внутри папки ui) или пусто, если фон выключен.</summary>
    string CurrentWallpaper()
    {
        if (!_settings.WallpaperOn) return "";
        var names = WallpaperNames();
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_settings.WallpaperDaily && _settings.WallpaperDay != today && names.Count > 0)
        {
            _settings.Wallpaper = "wallpapers/" + Wallpapers.OfTheDay(names, DateTime.Now, _settings.WallpaperSeasonal);
            _settings.WallpaperDay = today;
            _settings.Save();
        }
        var cur = _settings.Wallpaper;
        if (cur.Length > 0 && File.Exists(Path.Combine(_uiDir, cur.Replace('/', Path.DirectorySeparatorChar)))) return cur;
        return names.Count > 0 ? "wallpapers/" + names[0] : "";
    }

    /// <summary>Сведения о фонах для стартовой страницы.</summary>
    object WallpaperInfo()
    {
        var names = WallpaperNames();
        var cur = CurrentWallpaper();
        var shown = cur.Length > 0 ? cur : (_settings.Wallpaper.Length > 0 ? _settings.Wallpaper : (names.Count > 0 ? "wallpapers/" + names[0] : ""));
        int idx = names.FindIndex(n => "wallpapers/" + n == shown);
        return new
        {
            on = _settings.WallpaperOn,
            current = cur,
            preview = shown,
            index = idx + 1,
            count = names.Count,
            daily = _settings.WallpaperDaily,
            seasonal = _settings.WallpaperSeasonal,
        };
    }

    /// <summary>Обновляет все открытые новые вкладки во всех окнах.</summary>
    void BroadcastStartData()
    {
        foreach (var win in Shared.Windows.ToList())
            foreach (var t in win._tabs.Where(t => t.IsStart && t.Core != null).ToList())
                try { win.SendStartData(t, t.Core!); } catch { }
    }

    /// <summary>Команда из меню новой вкладки.</summary>
    void OnNtpCommand(BrowserTab tab, JsonElement root)
    {
        var action = root.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
        bool on = root.TryGetProperty("on", out var o) && o.ValueKind == JsonValueKind.True;
        switch (action)
        {
            case "clock": _settings.ShowClock = on; break;
            case "bg": _settings.WallpaperOn = on; break;
            case "daily":
                _settings.WallpaperDaily = on;
                if (on) _settings.WallpaperDay = DateTime.Now.ToString("yyyy-MM-dd");   // сегодня остаётся выбранный фон
                break;
            case "seasonal": _settings.WallpaperSeasonal = on; break;
            case "step":
            {
                int dir = root.TryGetProperty("dir", out var d) && d.TryGetInt32(out var dv) ? Math.Sign(dv) : 1;
                var names = WallpaperNames();
                if (names.Count == 0) break;
                var cur = (_settings.Wallpaper.StartsWith("wallpapers/") ? _settings.Wallpaper["wallpapers/".Length..] : "");
                if (cur.Length == 0 || !names.Contains(cur)) cur = CurrentWallpaper().Replace("wallpapers/", "");
                _settings.Wallpaper = "wallpapers/" + Wallpapers.Step(names, cur, dir == 0 ? 1 : dir);
                _settings.WallpaperOn = true;
                _settings.WallpaperDay = DateTime.Now.ToString("yyyy-MM-dd");   // выбранный вручную фон держится до завтра
                break;
            }
            case "upload":
                Dispatcher.BeginInvoke(new Action(UploadWallpaper));
                return;
            case "gallery":
                Dispatcher.BeginInvoke(new Action(() => OpenGallery(tab.Container)));
                return;
            case "settings":
                Dispatcher.BeginInvoke(new Action(() => OpenSettings(null)));
                return;
            default:
                return;
        }
        _settings.Save();
        BroadcastStartData();
    }

    /// <summary>Свой фон: выбор файла и копирование в папку фонов браузера.</summary>
    public void UploadWallpaper()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Свой фон для новой вкладки",
            Filter = "Изображения (*.jpg;*.jpeg;*.png;*.webp;*.bmp)|*.jpg;*.jpeg;*.png;*.webp;*.bmp",
        };
        if (dlg.ShowDialog(this) != true) return;
        AddCustomWallpaper(dlg.FileName);
    }

    /// <summary>Копирует картинку в папку фонов и делает её текущим фоном.</summary>
    public void AddCustomWallpaper(string file)
    {
        try
        {
            Directory.CreateDirectory(WallDir);
            var name = Wallpapers.SafeName("custom-", Path.GetFileName(file));
            if (File.Exists(Path.Combine(WallDir, name)))
                name = Wallpapers.SafeName("custom-", Path.GetFileNameWithoutExtension(file) + "-" + DateTime.Now.ToString("HHmmss") + Path.GetExtension(file));
            File.Copy(file, Path.Combine(WallDir, name), true);
            _settings.Wallpaper = "wallpapers/" + name;
            _settings.WallpaperOn = true;
            _settings.WallpaperDay = DateTime.Now.ToString("yyyy-MM-dd");
            _settings.Save();
            App.Log("Свой фон новой вкладки: " + name);
            BroadcastStartData();
        }
        catch (Exception ex)
        {
            App.Log("Свой фон: " + ex.Message);
            Dialogs.Show(this, "Не удалось добавить фон", ex.Message, new[] { "Понятно" });
        }
    }

    public void OpenGallery(string? container = null)
    {
        var existing = _tabs.FirstOrDefault(t => UrlTools.IsGalleryPage(t.Url));
        if (existing != null) { Activate(existing); return; }
        _ = NewTabAsync(UrlTools.GalleryUrl, activate: true, container: container);
    }

    static readonly HttpClient GalleryHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Сообщение со страницы галереи: загрузить каталог, скачать или выбрать фон, удалить свой.</summary>
    async void OnGalleryMessage(CoreWebView2 core, JsonElement root)
    {
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        try
        {
            if (type == "gallery-init")
            {
                var json = await GalleryHttp.GetStringAsync(GalleryBase + "catalog.json");
                using var doc = JsonDocument.Parse(json);
                var names = WallpaperNames();
                var items = doc.RootElement.GetProperty("items").EnumerateArray().Select(it =>
                {
                    var file = it.GetProperty("file").GetString() ?? "";
                    bool builtin = it.TryGetProperty("builtin", out var b) && b.ValueKind == JsonValueKind.True;
                    var local = builtin ? file : Wallpapers.SafeName("gallery-", file.StartsWith("gallery-") ? file["gallery-".Length..] : file);
                    return new
                    {
                        file,
                        local,
                        title = it.TryGetProperty("title", out var ti) ? ti.GetString() : file,
                        thumb = GalleryBase + "thumbs/" + Uri.EscapeDataString(file),
                        theme = Wallpapers.ThemeOf(file),
                        tags = it.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array
                            ? tg.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
                            : Array.Empty<string>(),
                        installed = names.Contains(local),
                    };
                }).ToList();
                var custom = names.Where(n => n.StartsWith("custom-")).Select(n => new { file = n, local = n, title = "Свой фон", thumb = "wallpapers/" + Uri.EscapeDataString(n), theme = "", installed = true });
                // Подборки («Азия», «Пейзажи» и т.п.) описаны в самом каталоге: новые добавляются без выпуска браузера.
                var tagNames = new Dictionary<string, string>();
                if (doc.RootElement.TryGetProperty("tags", out var tn) && tn.ValueKind == JsonValueKind.Object)
                    foreach (var p in tn.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String) tagNames[p.Name] = p.Value.GetString()!;
                var cur = CurrentWallpaper();
                core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "gallery", items, custom, tagNames, current = cur.Replace("wallpapers/", "") }));
            }
            else if (type == "gallery-use")
            {
                var file = root.GetProperty("file").GetString() ?? "";
                var local = root.GetProperty("local").GetString() ?? "";
                local = Path.GetFileName(local);
                if (local.Length == 0 || !Wallpapers.Extensions.Contains(Path.GetExtension(local).ToLowerInvariant())) return;
                var path = Path.Combine(WallDir, local);
                if (!File.Exists(path))
                {
                    // скачиваем только из каталога «Дымки»
                    var bytes = await GalleryHttp.GetByteArrayAsync(GalleryBase + Uri.EscapeDataString(Path.GetFileName(file)));
                    Directory.CreateDirectory(WallDir);
                    await File.WriteAllBytesAsync(path, bytes);
                    App.Log("Фон из галереи: " + local);
                }
                _settings.Wallpaper = "wallpapers/" + local;
                _settings.WallpaperOn = true;
                _settings.WallpaperDay = DateTime.Now.ToString("yyyy-MM-dd");
                _settings.Save();
                BroadcastStartData();
                core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "gallery-done", local, current = local }));
            }
            else if (type == "gallery-delete")
            {
                var local = Path.GetFileName(root.GetProperty("local").GetString() ?? "");
                if (!(local.StartsWith("gallery-") || local.StartsWith("custom-"))) return;   // стандартные не удаляются
                var path = Path.Combine(WallDir, local);
                if (File.Exists(path)) File.Delete(path);
                if (_settings.Wallpaper == "wallpapers/" + local) _settings.Wallpaper = "";
                _settings.Save();
                BroadcastStartData();
                core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "gallery-deleted", local, current = CurrentWallpaper().Replace("wallpapers/", "") }));
            }
            else if (type == "gallery-upload")
            {
                await Dispatcher.InvokeAsync(UploadWallpaper);
                core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "gallery-reload" }));
            }
        }
        catch (Exception ex)
        {
            App.Log("Галерея фонов: " + ex.Message);
            try { core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "gallery-error", message = ex.Message })); } catch { }
        }
    }
}
