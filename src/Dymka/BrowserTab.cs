using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Dymka.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Dymka;

/// <summary>Вкладка браузера. Пока вкладка спит, у неё нет WebView2 - только адрес и заголовок.</summary>
public sealed class BrowserTab : INotifyPropertyChanged
{
    static int _nextId;

    public int Id { get; } = Interlocked.Increment(ref _nextId);
    public string Container { get; }

    public BrowserTab(string container) => Container = container;

    public WebView2? View { get; set; }

    /// <summary>Движок страницы. После падения процесса движка обращение к View.CoreWebView2 бросает исключение,
    /// поэтому всюду используется это свойство: оно возвращает null для «мёртвой» страницы.</summary>
    public CoreWebView2? Core
    {
        get
        {
            try { return View?.CoreWebView2; }
            catch { return null; }
        }
    }

    // --- перевод страницы
    bool _isForeign;
    /// <summary>Страница не на русском - в адресной строке предлагается перевод.</summary>
    public bool IsForeign { get => _isForeign; set => Set(ref _isForeign, value); }

    TranslateState _translateState;
    public TranslateState TranslateState { get => _translateState; set => Set(ref _translateState, value); }

    /// <summary>Сайт, который в этой вкладке переводится сам при переходах (после того как вы перевели одну его страницу).</summary>
    public string TranslateSticky { get; set; } = "";
    public string SourceLang { get; set; } = "";
    public int TranslatePending { get; set; }
    public int TranslatedBatches { get; set; }
    public string TranslateError { get; set; } = "";

    /// <summary>Последняя загрузка страницы завершилась ошибкой (сайт недоступен, нет сети и т.п.).</summary>
    public bool LoadFailed { get; set; }

    /// <summary>Хост текущей страницы (для правил блокировки) и адрес, который сейчас загружается.</summary>
    public string PageHost { get; set; } = "";
    public string PendingUrl { get; set; } = "";

    public DateTime LastActive { get; set; } = DateTime.Now;

    /// <summary>Для самопроверки: какие хосты запрашивала страница и какие из них заблокированы.</summary>
    public List<string>? SeenHosts { get; set; }
    public bool IsPlayingAudio { get; set; }

    string _title = "Новая вкладка";
    public string Title
    {
        get => _title;
        set { if (Set(ref _title, string.IsNullOrWhiteSpace(value) ? "Без названия" : value)) Raise(nameof(Initial)); }
    }

    string _url = "";
    public string Url
    {
        get => _url;
        set { if (Set(ref _url, value ?? "")) { Raise(nameof(Initial)); Raise(nameof(IsStart)); Raise(nameof(InitialBg)); Raise(nameof(InitialFg)); } }
    }

    /// <summary>Вкладка открыта страницей (ссылка в новом окне, window.open) - её страница может сама её закрыть.</summary>
    public bool OpenedByPage { get; set; }
    /// <summary>Вкладка, со страницы которой открыта эта.</summary>
    public BrowserTab? Opener { get; set; }
    /// <summary>Сколько файлов сейчас скачивается со страницы этой вкладки.</summary>
    public int ActiveDownloads { get; set; }
    /// <summary>Страница попросила закрыть вкладку, но идёт скачивание - закроем, когда оно закончится.</summary>
    public bool CloseAfterDownloads { get; set; }

    public bool IsStart => string.IsNullOrEmpty(_url) || UrlTools.IsStartPage(_url);

    public string Initial => IsStart ? "" : UrlTools.InitialFor(_title, _url);

    static readonly (string bg, string fg)[] Palette =
    {
        ("#E6ECFB", "#2F4FB8"), ("#E3F3EA", "#1C7F53"), ("#FBE9E3", "#A5401F"),
        ("#EFEAFB", "#5B43C4"), ("#E4F1F7", "#1E6FA8"), ("#F6EFDF", "#8A5A00"),
    };

    (string bg, string fg) InitialColors
    {
        get
        {
            var host = UrlTools.RegistrableDomain(UrlTools.HostOf(_url));
            int h = 0;
            foreach (var ch in host) h = unchecked(h * 31 + ch);
            return Palette[(h & 0x7fffffff) % Palette.Length];
        }
    }

    public Brush InitialBg => BrushCache.Get(InitialColors.bg);
    public Brush InitialFg => BrushCache.Get(InitialColors.fg);

    bool _isPinned;
    public bool IsPinned { get => _isPinned; set => Set(ref _isPinned, value); }

    bool _isActive;
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    bool _isSleeping;
    public bool IsSleeping { get => _isSleeping; set => Set(ref _isSleeping, value); }

    bool _isLoading;
    public bool IsLoading { get => _isLoading; set => Set(ref _isLoading, value); }

    int _blocked;
    public int Blocked { get => _blocked; set => Set(ref _blocked, value); }

    bool _canGoBack, _canGoForward;
    public bool CanGoBack { get => _canGoBack; set => Set(ref _canGoBack, value); }
    public bool CanGoForward { get => _canGoForward; set => Set(ref _canGoForward, value); }

    ImageSource? _favicon;
    public ImageSource? Favicon
    {
        get => _favicon;
        set { if (Set(ref _favicon, value)) Raise(nameof(HasFavicon)); }
    }
    public bool HasFavicon => _favicon != null;

    public event PropertyChangedEventHandler? PropertyChanged;

    void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public enum TranslateState { None, Working, Done }

static class BrushCache
{
    static readonly Dictionary<string, Brush> Cache = new();

    public static Brush Get(string hex)
    {
        if (!Cache.TryGetValue(hex, out var b))
        {
            b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            Cache[hex] = b;
        }
        return b;
    }
}
