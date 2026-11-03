using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Dymka.Core;

namespace Dymka;

/// <summary>Заготовка новой вкладки (как в Chrome): скрытая готовая стартовая страница текущего контейнера.
/// По «Новая вкладка» / Ctrl+T она показывается сразу, а следующая готовится в фоне. Одна на весь браузер.</summary>
public partial class MainWindow
{
    BrowserTab? _spare;
    DispatcherTimer? _spareTimer;

    /// <summary>Окно, где сейчас лежит заготовка (она одна на все окна).</summary>
    static MainWindow? _spareHolder;

    public bool SpareReadyForTest(string container) =>
        _spare is { } s && s.Container == container && s.Core != null && !s.IsLoading && UrlTools.IsStartPage(s.Url);

    /// <summary>Подготовить заготовку через delay (повторный вызов переносит срок).</summary>
    void ScheduleSpare(TimeSpan delay)
    {
        if (!_settings.PreloadNewTab) return;
        _spareTimer ??= new DispatcherTimer();
        _spareTimer.Stop();
        _spareTimer.Interval = delay;
        _spareTimer.Tick -= SpareTick;
        _spareTimer.Tick += SpareTick;
        _spareTimer.Start();
    }

    void SpareTick(object? sender, EventArgs e)
    {
        _spareTimer?.Stop();
        _ = PrepareSpareAsync();
    }

    async Task PrepareSpareAsync()
    {
        if (!_settings.PreloadNewTab || _closingForGood || _cleanupDone || _recovering || !IsLoaded || !IsVisible) return;
        if (_env == null || !Shared.StartupDone) { ScheduleSpare(TimeSpan.FromSeconds(3)); return; }
        if (_spare != null && _spare.Container == _container) return;
        // пока открывается другая вкладка, заготовку не делаем - не мешаем ей
        if (_creating.Count > 0 || _tabs.Any(t => t.IsLoading)) { ScheduleSpare(TimeSpan.FromSeconds(2)); return; }
        DisposeSpare();
        if (_spareHolder != null && _spareHolder != this) _spareHolder.DisposeSpare();
        _spareHolder = this;
        var tab = new BrowserTab(_container);
        _spare = tab;
        var sw = Stopwatch.StartNew();
        await EnsureViewAsync(tab, UrlTools.StartUrl, navigate: true);
        if (_spare != tab) { DisposeView(tab); return; }
        if (tab.Core == null) { _spare = null; return; }
        App.Log($"Заготовка новой вкладки [{tab.Container}] готова за {sw.ElapsedMilliseconds} мс");
    }

    void DisposeSpare()
    {
        var s = _spare;
        if (s == null) return;
        _spare = null;
        if (_spareHolder == this) _spareHolder = null;
        DisposeView(s);
    }

    /// <summary>Забрать заготовку для новой вкладки: она уже загружена и сразу показывается. null - заготовки нет.</summary>
    BrowserTab? TakeSpare(string container)
    {
        var s = _spare;
        if (s == null || s.Container != container || s.Core == null || s.View == null || !UrlTools.IsStartPage(s.Url)) return null;
        _spare = null;
        if (_spareHolder == this) _spareHolder = null;
        try { SendStartData(s, s.Core); } catch (Exception ex) { App.Log("Заготовка вкладки: " + ex.Message); }
        return s;
    }
}
