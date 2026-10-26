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
}
