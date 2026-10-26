using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Dymka.Core;

namespace Dymka;

public partial class App : Application
{
    static string _logPath = "";

    public static string VersionText
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "1.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static void Log(string message)
    {
        try
        {
            if (_logPath.Length == 0) return;
            var fi = new FileInfo(_logPath);
            if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
            File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            // папка данных недоступна для записи - пишем во временную папку Windows, чтобы было что разбирать
            LastWriteError = ex.Message;
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "Dymka-log.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}"); }
            catch { }
        }
    }

    /// <summary>Последняя ошибка записи в папку данных (пусто - запись работает).</summary>
    public static string LastWriteError { get; private set; } = "";

    /// <summary>Проверяет, что в папку данных можно писать; возвращает текст ошибки или пусто.</summary>
    public static string CheckDataFolderWritable(string dataDir)
    {
        try
        {
            var probe = Path.Combine(dataDir, "write-test.tmp");
            File.WriteAllText(probe, DateTime.Now.ToString("O"));
            File.Delete(probe);
            return "";
        }
        catch (Exception ex)
        {
            LastWriteError = ex.Message;
            return ex.Message;
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? selfTest = null, dataDir = null, url = null, memTest = null;
        var args = e.Args;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--selftest" && i + 1 < args.Length) selfTest = args[++i];
            else if (args[i] == "--data" && i + 1 < args.Length) dataDir = args[++i];
            else if (args[i] == "--memtest" && i + 1 < args.Length) memTest = args[++i];
            else if (!args[i].StartsWith("--")) url = args[i];
        }

        dataDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dymka");
        Directory.CreateDirectory(dataDir);
        _logPath = Path.Combine(dataDir, "log.txt");

        DispatcherUnhandledException += OnUnhandled;
        // ошибки вне окна (фоновые потоки, обратные вызовы движка) - иначе браузер закрывается молча
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
            Log("Аварийное завершение: " + ev.ExceptionObject);
        if (selfTest == null && memTest == null)
            CrashDiagnostics.Start(Path.Combine(dataDir, "running.flag"));
        Log($"Запуск Дымки {VersionText}, движок {EngineVersionSafe()}, папка данных {dataDir}, процесс {Environment.ProcessId}, файл {Environment.ProcessPath}");
        DataWriteError = CheckDataFolderWritable(dataDir);
        if (DataWriteError.Length > 0) Log("Папка данных недоступна для записи: " + DataWriteError);
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log("Фоновая ошибка: " + ev.Exception); ev.SetObserved(); };

        var settings = AppSettings.Load(Path.Combine(dataDir, "settings.json"));
        ThemeManager.Apply(settings.Theme);
        Shared.Vault = new Vault(Path.Combine(dataDir, "vault.dat"), new Dpapi());
        // заблокировали Windows - закрываем хранилище с главным паролем
        Microsoft.Win32.SystemEvents.SessionSwitch += (_, ev) =>
        {
            if (ev.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect)
                Dispatcher.BeginInvoke(new Action(() => Shared.Vault.Lock()));
        };
        if (url != null && File.Exists(url)) url = new Uri(Path.GetFullPath(url)).AbsoluteUri;

        var w = new MainWindow(settings, dataDir, selfTest, url) { MemTestFile = memTest };
        MainWindow = w;
        w.Show();
    }

    /// <summary>Ошибка записи в папку данных при запуске (пусто - всё в порядке).</summary>
    public static string DataWriteError { get; private set; } = "";

    static string EngineVersionSafe()
    {
        try { return Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch { return "?"; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log("Выход из браузера");
        CrashDiagnostics.Stop();
        base.OnExit(e);
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("Ошибка: " + e.Exception);
        e.Handled = true;
        if (Environment.GetCommandLineArgs().Contains("--selftest"))
        {
            SelfTest.Fail(e.Exception);
            return;
        }
        try
        {
            Dialogs.Show(MainWindow, "Что-то пошло не так",
                "Браузер продолжит работу. Подробности записаны в журнал log.txt в папке данных браузера.\n\n" + e.Exception.Message,
                new[] { "Понятно" });
        }
        catch { }
    }
}
