using System.Diagnostics;
using Dymka.Core;
using Microsoft.Win32;

namespace Dymka;

/// <summary>Установка «Дымки» в папку пользователя (%LOCALAPPDATA%\Programs\Dymka), как у Chrome и Telegram:
/// права администратора не нужны, обновления ставятся тихо. Ярлыки - на рабочем столе и в меню «Пуск»,
/// запись в «Установленных приложениях» Windows - для удаления.</summary>
static class AppInstall
{
    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Dymka");
    public static string InstalledExe => Path.Combine(InstallDir, "Dymka.exe");
    static string VersionFile => Path.Combine(InstallDir, "version.txt");
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Dymka";

    static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Дымка.lnk");
    static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Дымка.lnk");

    /// <summary>Запущена ли установленная копия.</summary>
    public static bool RunningInstalled =>
        Environment.ProcessPath is { } p && string.Equals(Path.GetFullPath(p), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    /// <summary>Версия установленной копии (null - не установлена).</summary>
    public static Version? InstalledVersion
    {
        get
        {
            if (!File.Exists(InstalledExe)) return null;
            try { if (File.Exists(VersionFile)) return UpdateCheck.ParseTag(File.ReadAllText(VersionFile).Trim()); } catch { }
            try
            {
                var fv = FileVersionInfo.GetVersionInfo(InstalledExe);
                if (fv.FileMajorPart > 0) return new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart);
            }
            catch { }
            return new Version(0, 0, 0);
        }
    }

    /// <summary>Установить можно только версию одним файлом (папочная версия - много файлов, её переносят сами).</summary>
    public static bool CanInstall => UpdateChecker.IsSingleFile && !string.IsNullOrEmpty(Environment.ProcessPath);

    /// <summary>Запущена не установленная копия, а установлена такая же или новее: открыть установленную.
    /// Так старый файл из «Загрузок» или Program Files не запускает устаревшую версию.</summary>
    public static bool ShouldForward(out Version? installed)
    {
        installed = InstalledVersion;
        return CanInstall && !RunningInstalled && installed != null && installed >= UpdateChecker.Current;
    }

    public static void Forward(string[] args)
    {
        var psi = new ProcessStartInfo(InstalledExe) { UseShellExecute = false, WorkingDirectory = InstallDir };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    /// <summary>Скопировать запущенный файл в папку установки, создать ярлыки и запись для удаления.
    /// null - успешно, иначе текст ошибки.</summary>
    public static string? Install()
    {
        try
        {
            var src = Environment.ProcessPath!;
            Directory.CreateDirectory(InstallDir);
            var tmp = InstalledExe + ".new";
            File.Copy(src, tmp, true);
            for (int i = 0; ; i++)
            {
                try { File.Move(tmp, InstalledExe, true); break; }
                catch (IOException) when (i < 10) { Thread.Sleep(400); }
                catch (UnauthorizedAccessException) when (i < 10) { Thread.Sleep(400); }
            }
            File.WriteAllText(VersionFile, UpdateChecker.Current.ToString());
            CreateShortcuts();
            Register();
            App.Log($"Установка: {src} → {InstalledExe} (версия {UpdateChecker.Current})");
            return null;
        }
        catch (Exception ex)
        {
            App.Log("Установка не удалась: " + ex);
            return ex is IOException or UnauthorizedAccessException
                ? "Файл в папке установки занят: закройте установленную «Дымку» и попробуйте ещё раз. (" + ex.Message + ")"
                : ex.Message;
        }
    }

    /// <summary>После обновления установленной копии - записать новую версию.</summary>
    public static void NoteRunningVersion()
    {
        try { if (RunningInstalled) File.WriteAllText(VersionFile, UpdateChecker.Current.ToString()); } catch { }
    }

    static void CreateShortcuts()
    {
        foreach (var link in new[] { DesktopLink, StartMenuLink })
        {
            try { MakeShortcut(link, InstalledExe); }
            catch (Exception ex) { App.Log("Ярлык " + link + ": " + ex.Message); }
        }
    }

    static void MakeShortcut(string link, string target)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell недоступен");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic sc = shell.CreateShortcut(link);
            sc.TargetPath = target;
            sc.WorkingDirectory = Path.GetDirectoryName(target);
            sc.IconLocation = target + ",0";
            sc.Description = "Дымка - лёгкий конфиденциальный браузер";
            sc.Save();
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sc);
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    static void Register()
    {
        using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
        k.SetValue("DisplayName", "Дымка");
        k.SetValue("DisplayVersion", UpdateChecker.Current.ToString());
        k.SetValue("Publisher", "Дымка");
        k.SetValue("DisplayIcon", InstalledExe + ",0");
        k.SetValue("InstallLocation", InstallDir);
        k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
    }

    /// <summary>Удаление: ярлыки, запись в Windows и папка программы (после выхода). Данные (настройки, пароли) остаются,
    /// если не попросили удалить и их.</summary>
    public static void Uninstall(string dataDir, bool removeData)
    {
        foreach (var link in new[] { DesktopLink, StartMenuLink })
            try { if (File.Exists(link)) File.Delete(link); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        var cmd = $"ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{InstallDir}\"" + (removeData ? $" & rmdir /s /q \"{dataDir}\"" : "");
        Process.Start(new ProcessStartInfo("cmd.exe", "/c " + cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        App.Log("Удаление «Дымки»" + (removeData ? " вместе с данными" : ""));
    }
}
