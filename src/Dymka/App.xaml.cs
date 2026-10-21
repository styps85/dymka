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
        catch { }
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
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log("Фоновая ошибка: " + ev.Exception); ev.SetObserved(); };

        var settings = AppSettings.Load(Path.Combine(dataDir, "settings.json"));
        if (url != null && File.Exists(url)) url = new Uri(Path.GetFullPath(url)).AbsoluteUri;

        var w = new MainWindow(settings, dataDir, selfTest, url) { MemTestFile = memTest };
        MainWindow = w;
        w.Show();
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
