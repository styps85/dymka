namespace Dymka.Core;

/// <summary>Куда сохранять скачанный файл.</summary>
public static class DownloadPaths
{
    /// <summary>Свободное имя в папке: «файл.pdf», «файл (1).pdf», «файл (2).pdf»… - как в Chrome.</summary>
    public static string Unique(string folder, string fileName, Func<string, bool> exists)
    {
        fileName = Safe(fileName);
        var path = Path.Combine(folder, fileName);
        if (!exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 1; i < 1000; i++)
        {
            path = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!exists(path)) return path;
        }
        return Path.Combine(folder, $"{stem} ({DateTime.Now:yyyyMMdd-HHmmss}){ext}");
    }

    /// <summary>Имя файла без недопустимых в Windows знаков.</summary>
    public static string Safe(string? fileName)
    {
        // только имя после последней черты; Path.GetFileName не подходит - в Windows он принимает «a:» за диск
        var name = fileName ?? "";
        int cut = name.LastIndexOfAny(new[] { '/', '\\' });
        if (cut >= 0) name = name[(cut + 1)..];
        foreach (var c in "<>:\"/\\|?*") name = name.Replace(c, '_');
        name = new string(name.Where(ch => ch >= 32).ToArray()).Trim().TrimEnd('.');
        return name.Length == 0 ? "download" : name;
    }

    /// <summary>Спрашивать ли место для скачивания своим окном «Сохранить как». Не спрашиваем, если место уже выбрали
    /// в окне самого движка: Ctrl+S, «Сохранить как» или кнопка сохранения во встроенном просмотрщике PDF -
    /// иначе окно появляется дважды, и отмена второго отменяет скачивание.</summary>
    public static (bool ask, string why) ShouldAsk(bool askSetting, string? resultPath, string? engineDefaultFolder,
        bool saveAsJustShown, string? downloadUri, string? tabUrl, string? mimeType)
    {
        if (!askSetting) return (false, "в настройках выбрано не спрашивать");
        if (saveAsJustShown) return (false, "место уже выбрано в окне «Сохранить как» движка");
        var dir = DirOf(resultPath);
        if (dir.Length > 0 && Norm(engineDefaultFolder).Length > 0 && !string.Equals(dir, Norm(engineDefaultFolder), StringComparison.OrdinalIgnoreCase))
            return (false, "место уже выбрано (не папка загрузок по умолчанию)");
        bool pdf = (mimeType ?? "").Contains("pdf", StringComparison.OrdinalIgnoreCase)
                   || NoFragment(downloadUri).EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        if (pdf && NoFragment(downloadUri).Length > 0 && NoFragment(downloadUri) == NoFragment(tabUrl))
            return (false, "сохранение из просмотрщика PDF - место выбрано в его окне");
        return (true, "");
    }

    static string Norm(string? path) => (path ?? "").Replace('/', '\\').TrimEnd('\\');

    static string DirOf(string? path)
    {
        var p = Norm(path);
        int cut = p.LastIndexOf('\\');
        return cut > 0 ? p[..cut] : "";
    }

    static string NoFragment(string? url)
    {
        var u = url ?? "";
        int h = u.IndexOf('#');
        return h >= 0 ? u[..h] : u;
    }
}
