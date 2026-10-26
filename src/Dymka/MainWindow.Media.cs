using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dymka.Core;
using Microsoft.Web.WebView2.Core;

namespace Dymka;

/// <summary>Мини-плеер: когда на другой вкладке играет музыка (не видео), внизу справа - полупрозрачная панель
/// с названием трека и кнопками «предыдущий / пауза / следующий / без звука».</summary>
public partial class MainWindow
{
    /// <summary>Скрипт страницы: запоминает обработчики Media Session (так сайт сам переключает треки)
    /// и отвечает на команды мини-плеера. Только верхний документ, ничего не отправляет сам.</summary>
    const string MediaScript = @"(function(){try{
if(window.top!==window||window.__dymkaMedia)return;
var ms=navigator.mediaSession,H={};
if(ms&&ms.setActionHandler){var orig=ms.setActionHandler;
 ms.setActionHandler=function(a,h){try{if(h)H[a]=h;else delete H[a];}catch(e){}return orig.apply(ms,arguments);};}
function els(){return Array.prototype.slice.call(document.querySelectorAll('audio,video'));}
function on(){return els().filter(function(m){return !m.paused&&!m.ended;});}
function playing(){var st=ms?ms.playbackState:'none';return st==='playing'||(st!=='paused'&&on().length>0);}
function call(a){if(H[a]){try{H[a]({action:a});return true;}catch(e){}}return false;}
window.__dymkaMedia=function(cmd){
 var was=playing(),res=null;
 if(cmd==='toggle')cmd=was?'pause':'play';
 if(cmd==='play'){if(!call('play')){var m=els()[0];if(m)m.play();}res=true;}
 else if(cmd==='pause'){if(!call('pause'))on().forEach(function(m){m.pause();});res=false;}
 else if(cmd==='prev')call('previoustrack');
 else if(cmd==='next')call('nexttrack');
 var md=ms&&ms.metadata,art='';
 if(md&&md.artwork&&md.artwork.length){var best=md.artwork[md.artwork.length-1];
  for(var i=0;i<md.artwork.length;i++){var sz=parseInt(String(md.artwork[i].sizes||'0').split('x')[0],10)||0;if(sz>=96){best=md.artwork[i];break;}}
  try{art=new URL(best.src,location.href).href;}catch(e){}}
 var vid=on().some(function(m){return m.tagName==='VIDEO'&&m.videoWidth>0&&!m.muted&&m.volume>0;});
 return JSON.stringify({has:!!(md||els().length||Object.keys(H).length),playing:res===null?playing():res,
  title:md?md.title||'':'',artist:md?md.artist||'':'',art:art,video:vid,prev:!!H.previoustrack,next:!!H.nexttrack});
};}catch(e){}})();";

    readonly DispatcherTimer _mediaTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    BrowserTab? _mediaTab;           // вкладка, которой управляет мини-плеер
    BrowserTab? _mediaDismissed;     // для неё мини-плеер скрыли крестиком
    MediaState? _mediaState;
    BrowserTab? _mediaPausedHere;   // паузу нажали в мини-плеере
    bool _mediaBusy;
    string _mediaArtUrl = "";
    bool _mediaNudge;

    /// <summary>Для самопроверки: считать эту вкладку звучащей (на сборочном сервере нет звуковой карты).</summary>
    public BrowserTab? MediaTestTab { get; set; }
    public bool MediaBarShownForTest => MediaBar.IsOpen;
    public string MediaBarTitleForTest => MediaBar.IsOpen ? MediaTitle.Text : "";
    public void MediaCommandForTest(string cmd) => _ = MediaCommandAsync(cmd);

    void InitMediaBar()
    {
        ApplyMediaStyle();
        _mediaTimer.Tick += async (_, _) => await MediaTickAsync();
        _mediaTimer.Start();
        Deactivated += (_, _) => MediaBar.IsOpen = false;
        Activated += async (_, _) => await MediaTickAsync();
        LocationChanged += (_, _) => PositionMediaBar();
        SizeChanged += (_, _) => PositionMediaBar();
        ContentHost.SizeChanged += (_, _) => PositionMediaBar();
    }

    /// <summary>Вкладка заиграла: если для неё скрывали мини-плеер, а потом была тишина дольше 5 секунд - снова показываем.</summary>
    void OnTabAudioChanged(BrowserTab tab)
    {
        if (tab.IsPlayingAudio && _mediaDismissed == tab && tab.AudioSince - tab.AudioStoppedAt > TimeSpan.FromSeconds(5))
            _mediaDismissed = null;
        if (tab.IsPlayingAudio && tab != _active) _ = MediaTickAsync();
    }

    BrowserTab? PickMediaTab()
    {
        if (_mediaTab != null && (!_tabs.Contains(_mediaTab) || _mediaTab.Core == null || _mediaTab.IsSleeping)) _mediaTab = null;
        if (MediaTestTab != null && _tabs.Contains(MediaTestTab)) return _mediaTab = MediaTestTab;
        // самая недавно заигравшая вкладка; остановленная на паузу остаётся, чтобы её можно было продолжить
        foreach (var t in _tabs)
            if (t.IsPlayingAudio && t.Core != null && (_mediaTab == null || !_mediaTab.IsPlayingAudio || t.AudioSince > _mediaTab.AudioSince))
                _mediaTab = t;
        return _mediaTab;
    }

    async Task MediaTickAsync()
    {
        if (_mediaBusy) return;
        _mediaBusy = true;
        try
        {
            var t = PickMediaTab();
            bool can = t != null && (_settings.MiniPlayer || t == MediaTestTab) && t != _active && !t.InPip && t != _mediaDismissed && !InSplit(t)
                       && (IsActive || MediaTestTab != null) && WindowState != WindowState.Minimized && !_fullscreen;
            if (_mediaDragging) return;
            if (!can) { HideMediaBar(); return; }
            var st = await QueryMediaAsync(t!, "state", gesture: false);
            // на паузе панель остаётся только у настоящего плеера (сайт сообщил название трека) или если паузу нажали в мини-плеере;
            // короткий звук уведомления на обычном сайте панель не вызывает
            bool keep = st != null && st.WantsBar && (st.Playing || st.Title.Length > 0 || _mediaPausedHere == t);
            if (!keep)
            {
                if (st == null || !st.Playing) { if (!t!.IsPlayingAudio && t != MediaTestTab) _mediaTab = null; }
                HideMediaBar();
                return;
            }
            ShowMediaBar(t!, st);
        }
        catch (Exception ex) { App.Log("Мини-плеер: " + ex.Message); HideMediaBar(); }
        finally { _mediaBusy = false; }
    }

    async Task<MediaState?> QueryMediaAsync(BrowserTab tab, string cmd, bool gesture)
    {
        var core = tab.Core;
        if (core == null) return null;
        var js = "window.__dymkaMedia?window.__dymkaMedia(" + JsonSerializer.Serialize(cmd) + "):null";
        // команды - как действие пользователя (иначе сайт может не дать включить звук), опрос - обычным вызовом
        var res = gesture ? await EvalWithGestureAsync(core, js) : await core.ExecuteScriptAsync(js);
        return MediaState.Parse(res);
    }

    void ShowMediaBar(BrowserTab tab, MediaState st)
    {
        _mediaState = st;
        MediaTitle.Text = st.Title.Length > 0 ? st.Title : tab.Title;
        var host = UrlTools.HostOf(tab.Url);
        if (host.StartsWith("www.")) host = host[4..];
        MediaSub.Text = st.Artist.Length > 0 ? st.Artist + " · " + host : host;
        MediaPlayGlyph.Data = (Geometry)FindResource(st.Playing ? "IcPause" : "IcPlay");
        MediaPlayBtn.ToolTip = st.Playing ? "Пауза" : "Продолжить";
        MediaPrevBtn.IsEnabled = st.CanPrev;
        MediaNextBtn.IsEnabled = st.CanNext;
        MediaMuteGlyph.Data = (Geometry)FindResource(tab.IsMuted ? "IcSpeakerOff" : "IcSpeaker");
        MediaMuteBtn.ToolTip = tab.IsMuted ? "Включить звук вкладки" : "Выключить звук вкладки";
        SetMediaArt(st.Art);
        if (!MediaBar.IsOpen) { MediaBar.IsOpen = true; App.Log("Мини-плеер: " + host); }
        PositionMediaBar();
    }

    void HideMediaBar()
    {
        if (MediaBar.IsOpen) MediaBar.IsOpen = false;
    }

    void SetMediaArt(string url)
    {
        if (url == _mediaArtUrl) return;
        _mediaArtUrl = url;
        MediaArt.Background = null;
        if (url.Length == 0) return;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(url);
            img.DecodePixelWidth = 96;
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            MediaArt.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        }
        catch { }
    }

    // ================================================================ где стоит мини-плеер

    const double MediaMargin = 12;   // поле вокруг карточки внутри окна мини-плеера (для тени)
    const double MediaGap = 12;      // отступ карточки от края страницы (окно плеера не выходит за страницу - Windows не сдвинет его у края экрана)
    const double MediaSnap = 36;     // ближе этого к краю - прилипает
    bool _mediaVertical;

    /// <summary>Вид: горизонтальный (обложка, название, кнопки) или вертикальный - только кнопки (у левого и правого края).</summary>
    void SetMediaVertical(bool v)
    {
        if (v == _mediaVertical && MediaCard.Width is double w && (v ? double.IsNaN(w) : w > 0)) return;
        _mediaVertical = v;
        MediaGoBtn.Visibility = v ? Visibility.Collapsed : Visibility.Visible;
        foreach (var b in new FrameworkElement[] { MediaCloseBtn, MediaMuteBtn, MediaNextBtn, MediaPlayBtn, MediaPrevBtn })
            System.Windows.Controls.DockPanel.SetDock(b, v ? System.Windows.Controls.Dock.Bottom : System.Windows.Controls.Dock.Right);
        MediaCard.Width = v ? double.NaN : 384;
        MediaCard.Padding = v ? new Thickness(7, 8, 7, 7) : new Thickness(7, 7, 8, 7);
        MediaCloseBtn.Margin = v ? new Thickness(0, 4, 0, 0) : new Thickness(2, 0, 0, 0);
        MediaPlayBtn.Margin = v ? new Thickness(0, 3, 0, 3) : new Thickness(0);
    }

    Size MediaSize()
    {
        MediaCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var d = MediaCard.DesiredSize;   // вместе с полями
        return d;
    }

    (double minX, double maxX, double minY, double maxY) MediaRange(Size sz)
    {
        double W = ContentHost.ActualWidth, H = ContentHost.ActualHeight;
        double minX = MediaGap - MediaMargin, minY = MediaGap - MediaMargin;
        double maxX = Math.Max(minX, W - sz.Width + MediaMargin - MediaGap);
        double maxY = Math.Max(minY, H - sz.Height + MediaMargin - MediaGap);
        return (minX, maxX, minY, maxY);
    }

    static double Frac(double v, double a, double b) => b - a < 1 ? 0 : Math.Clamp((v - a) / (b - a), 0, 1);
    static double Lerp(double f, double a, double b) => a + Math.Clamp(f, 0, 1) * (b - a);

    /// <summary>Ставит мини-плеер по сохранённому положению: угол страницы (по умолчанию), край или свободное место.
    /// Положение - относительно области страницы, поэтому при смене вида вкладок (сверху / сбоку) и размера окна
    /// плеер остаётся у того же края и не заходит на список вкладок.</summary>
    void PositionMediaBar()
    {
        if (!MediaBar.IsOpen || _mediaDragging) return;
        var edge = _settings.MiniPlayerEdge;
        SetMediaVertical(edge is "left" or "right");
        var sz = MediaSize();
        var (minX, maxX, minY, maxY) = MediaRange(sz);
        double W = ContentHost.ActualWidth, H = ContentHost.ActualHeight;
        bool start = _active?.IsStart == true;
        double x, y;
        switch (edge)
        {
            case "left": x = minX; y = Lerp(_settings.MiniPlayerY, minY, maxY); break;
            case "right": x = maxX; y = Lerp(_settings.MiniPlayerY, minY, maxY); break;
            case "top": y = minY; x = Lerp(_settings.MiniPlayerX, minX, maxX); break;
            case "bottom": y = maxY; x = Lerp(_settings.MiniPlayerX, minX, maxX); break;
            case "free": x = Lerp(_settings.MiniPlayerX, minX, maxX); y = Lerp(_settings.MiniPlayerY, minY, maxY); break;
            default:
                // угол: на новой вкладке - в нижнем ряду левее шестерёнки
                x = Math.Max(0, W - sz.Width - (start ? 64 : 8));
                y = Math.Max(0, H - sz.Height - (start ? 3 : 4));
                break;
        }
        if (start && edge.Length > 0) (x, y) = AvoidStartButtons(x, y, sz, W, H);
        PlaceMediaBar(x, y);
    }

    /// <summary>Не закрывать кнопки новой вкладки: шестерёнку (внизу справа) и щит (внизу слева), 44 точки в 20 от краёв.</summary>
    (double, double) AvoidStartButtons(double x, double y, Size sz, double W, double H)
    {
        Rect card = new(x + MediaMargin, y + MediaMargin, sz.Width - 2 * MediaMargin, sz.Height - 2 * MediaMargin);
        foreach (var btn in new[] { new Rect(W - 64, H - 64, 44, 44), new Rect(20, H - 64, 44, 44) })
        {
            if (!card.IntersectsWith(btn)) continue;
            if (_mediaVertical) y = btn.Top - 8 - (sz.Height - MediaMargin);
            else if (btn.Left > W / 2) x = btn.Left - 8 - (sz.Width - MediaMargin);
            else x = btn.Right + 8 - MediaMargin;
            card = new(x + MediaMargin, y + MediaMargin, card.Width, card.Height);
        }
        return (x, y);
    }

    double _mediaPlacedX, _mediaPlacedY;

    /// <summary>Ставит окно плеера в точку (x, y) области страницы - в экранных координатах, чтобы положение
    /// не зависело от того, как Windows пересчитывает всплывающие окна при смене вида вкладок.</summary>
    void PlaceMediaBar(double x, double y)
    {
        _mediaPlacedX = x;
        _mediaPlacedY = y;
        var origin = new Point(0, 0);
        try
        {
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget != null)
                origin = src.CompositionTarget.TransformFromDevice.Transform(ContentHost.PointToScreen(new Point(0, 0)));
        }
        catch { }
        MediaBar.Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute;
        // то же значение окно не двигает - при перемещении браузера чуть сдвигаем
        _mediaNudge = !_mediaNudge;
        MediaBar.HorizontalOffset = origin.X + x + (_mediaNudge ? 0.01 : 0);
        MediaBar.VerticalOffset = origin.Y + y;
    }

    /// <summary>К какому краю ближе карточка (или "free"), если до него меньше MediaSnap.</summary>
    /// <param name="preferVertical">в углу (рядом два края) - край под текущий вид, чтобы плеер не менял форму</param>
    string MediaEdgeAt(double x, double y, Size sz, bool? preferVertical = null)
    {
        double W = ContentHost.ActualWidth, H = ContentHost.ActualHeight;
        double l = x + MediaMargin, t = y + MediaMargin, r = W - (x + sz.Width - MediaMargin), b = H - (y + sz.Height - MediaMargin);
        double side = Math.Min(l, r), tb = Math.Min(t, b);
        if (preferVertical == true && side <= MediaSnap) return l <= r ? "left" : "right";
        if (preferVertical == false && tb <= MediaSnap) return t <= b ? "top" : "bottom";
        double best = Math.Min(side, tb);
        if (best > MediaSnap) return "free";
        if (l == best) return "left";
        if (r == best) return "right";
        return t == best ? "top" : "bottom";
    }

    /// <summary>Отпустили мини-плеер в точке (x, y) области страницы: прилипание к краю и запоминание.</summary>
    internal void MediaDropAt(double x, double y, bool? preferVertical = null)
    {
        var edge = MediaEdgeAt(x, y, MediaSize(), preferVertical);
        SetMediaVertical(edge is "left" or "right");
        var sz = MediaSize();
        var (minX, maxX, minY, maxY) = MediaRange(sz);
        _settings.MiniPlayerEdge = edge;
        _settings.MiniPlayerX = Frac(x, minX, maxX);
        _settings.MiniPlayerY = Frac(y, minY, maxY);
        _settings.Save();
        App.Log("Мини-плеер: " + (edge == "free" ? "перенесён" : "прилип к краю " + edge));
        PositionMediaBar();
    }

    void ResetMediaPosition()
    {
        _settings.MiniPlayerEdge = "";
        _settings.MiniPlayerX = _settings.MiniPlayerY = -1;
        _settings.Save();
        PositionMediaBar();
    }

    // ---- перетаскивание мышью
    Point _mediaDragStart;
    double _mediaX0, _mediaY0;
    bool _mediaDragPending, _mediaDragging;

    void MediaCard_Down(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ResetMediaPosition(); e.Handled = true; return; }
        _mediaDragPending = true;
        _mediaDragStart = e.GetPosition(ContentHost);
        _mediaX0 = _mediaPlacedX;
        _mediaY0 = _mediaPlacedY;
    }

    void MediaCard_Move(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_mediaDragPending && !_mediaDragging) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) { EndMediaDrag(false); return; }
        var p = e.GetPosition(ContentHost);
        var d0 = p - _mediaDragStart;
        if (!_mediaDragging)
        {
            if (Math.Abs(d0.X) < 5 && Math.Abs(d0.Y) < 5) return;
            _mediaDragging = true;
            MediaCard.CaptureMouse();   // кнопка под курсором отпускается и не сработает
        }
        MediaDragTo(p);
        e.Handled = true;
    }

    /// <summary>Курсор с плеером в точке p области страницы.</summary>
    void MediaDragTo(Point p)
    {
        var d = p - _mediaDragStart;
        // Вид - по положению курсора, а не карточки: при смене вида карточка меняет размер, и по ней у угла
        // вид переключался бы туда-обратно (плеер «моргал»). У левого/правого края - вертикальный, у верхнего/нижнего
        // и в середине - горизонтальный, в углу - не меняется.
        double W = ContentHost.ActualWidth, H = ContentHost.ActualHeight;
        // С запасом (гистерезисом): вертикальным плеер становится в 56 точках от бокового края, а горизонтальным
        // снова - только когда курсор отошёл дальше 96, поэтому дрожание мыши на границе не переключает вид.
        bool sideIn = p.X < 56 || p.X > W - 56, sideOut = p.X < 96 || p.X > W - 96, tbIn = p.Y < 56 || p.Y > H - 56;
        bool vertical = _mediaVertical ? sideOut : sideIn && !tbIn;
        if (vertical != _mediaVertical)
        {
            // карточка меняет форму вокруг курсора: точка, за которую держат, остаётся под ним
            var old = MediaSize();
            double fx = Math.Clamp((p.X - _mediaPlacedX - MediaMargin) / Math.Max(1, old.Width - 2 * MediaMargin), 0, 1);
            double fy = Math.Clamp((p.Y - _mediaPlacedY - MediaMargin) / Math.Max(1, old.Height - 2 * MediaMargin), 0, 1);
            SetMediaVertical(vertical);
            var nsz = MediaSize();
            _mediaX0 = p.X - MediaMargin - fx * (nsz.Width - 2 * MediaMargin);
            _mediaY0 = p.Y - MediaMargin - fy * (nsz.Height - 2 * MediaMargin);
            _mediaDragStart = p;
            d = new Vector(0, 0);
        }
        var (minX, maxX, minY, maxY) = MediaRange(MediaSize());
        PlaceMediaBar(Math.Clamp(_mediaX0 + d.X, minX, maxX), Math.Clamp(_mediaY0 + d.Y, minY, maxY));
    }

    /// <summary>Самопроверка: «перетащить» плеер по точкам (координаты области страницы) и отпустить;
    /// возвращает, сколько раз по пути менялся вид.</summary>
    public int MediaDragPathForTest(IEnumerable<Point> path)
    {
        _mediaDragStart = new Point(_mediaPlacedX + 30, _mediaPlacedY + 30);
        _mediaX0 = _mediaPlacedX;
        _mediaY0 = _mediaPlacedY;
        _mediaDragging = true;
        int flips = 0;
        bool v = _mediaVertical;
        foreach (var p in path)
        {
            MediaDragTo(p);
            if (_mediaVertical != v) { flips++; v = _mediaVertical; }
        }
        _mediaDragging = false;
        MediaDropAt(_mediaPlacedX, _mediaPlacedY, _mediaVertical);
        return flips;
    }

    void MediaCard_Up(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_mediaDragging) e.Handled = true;
        EndMediaDrag(true);
    }

    void MediaCard_LostCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_mediaDragging) EndMediaDrag(true);
    }

    void EndMediaDrag(bool drop)
    {
        bool was = _mediaDragging;
        _mediaDragPending = false;
        _mediaDragging = false;
        if (MediaCard.IsMouseCaptured) MediaCard.ReleaseMouseCapture();
        if (was && drop) MediaDropAt(_mediaPlacedX, _mediaPlacedY, _mediaVertical);
    }

    // ---- оформление: тёмный или светлый плеер
    void ApplyMediaStyle()
    {
        var mode = _settings.MiniPlayerStyle;
        bool light = mode == "light" || (mode == "theme" && !ThemeManager.IsDark);
        void Set(string key, string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
            b.Freeze();
            MediaCard.Resources[key] = b;
        }
        if (light)
        {
            Set("MediaBg", "#F5FFFFFF"); Set("MediaFg", "#1B2230"); Set("MediaSub", "#5B6475"); Set("MediaFaint", "#7A8394");
            Set("MediaHover", "#14000000"); Set("MediaPress", "#24000000"); Set("MediaPlayBg", "#0F000000");
            Set("MediaArtBg", "#EEF1F6"); Set("MediaNote", "#7A8394");
            MediaShadow.Opacity = 0.18;
        }
        else
        {
            bool darkTheme = mode == "theme" && ThemeManager.IsDark;
            Set("MediaBg", darkTheme ? "#F22A2F38" : "#EB1B2230"); Set("MediaFg", "#FFFFFF"); Set("MediaSub", "#AEB9CB"); Set("MediaFaint", "#B9C3D3");
            Set("MediaHover", "#2EFFFFFF"); Set("MediaPress", "#4DFFFFFF"); Set("MediaPlayBg", "#26FFFFFF");
            Set("MediaArtBg", "#33FFFFFF"); Set("MediaNote", "#C9D3E3");
            MediaShadow.Opacity = 0.25;
        }
        MediaLight = light;
    }

    public bool MediaLight { get; private set; }
    public void ApplyLayoutForTest() => ApplyLayout();
    public bool MediaVerticalForTest => _mediaVertical;
    public void MediaDropForTest(double x, double y) => MediaDropAt(x, y);
    public void MediaResetForTest() => ResetMediaPosition();
    public void MediaStyleForTest(string style) { _settings.MiniPlayerStyle = style; ApplyMediaStyle(); }
    /// <summary>Прямоугольник карточки и области страницы на экране (в точках экрана).</summary>
    public (Rect card, Rect page) MediaRectsForTest()
    {
        var c = new Rect(MediaCard.PointToScreen(new Point(0, 0)), MediaCard.PointToScreen(new Point(MediaCard.ActualWidth, MediaCard.ActualHeight)));
        var pg = new Rect(ContentHost.PointToScreen(new Point(0, 0)), ContentHost.PointToScreen(new Point(ContentHost.ActualWidth, ContentHost.ActualHeight)));
        return (c, pg);
    }

    async Task MediaCommandAsync(string cmd)
    {
        var t = _mediaTab;
        if (t == null) return;
        try
        {
            if (cmd == "toggle") _mediaPausedHere = _mediaState?.Playing == true ? t : null;
            var st = await QueryMediaAsync(t, cmd, gesture: true);
            if (st != null && st.Has) ShowMediaBar(t, st);
            // название следующего трека сайт обновляет не сразу
            await Task.Delay(700);
            await MediaTickAsync();
        }
        catch (Exception ex) { App.Log("Мини-плеер, команда " + cmd + ": " + ex.Message); }
    }

    void MediaPlay_Click(object sender, RoutedEventArgs e) => _ = MediaCommandAsync("toggle");
    void MediaPrev_Click(object sender, RoutedEventArgs e) => _ = MediaCommandAsync("prev");
    void MediaNext_Click(object sender, RoutedEventArgs e) => _ = MediaCommandAsync("next");

    void MediaMute_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaTab == null) return;
        ToggleMute(_mediaTab);
        if (_mediaState != null) ShowMediaBar(_mediaTab, _mediaState);
    }

    void MediaGo_Click(object sender, RoutedEventArgs e)
    {
        var t = _mediaTab;
        HideMediaBar();
        if (t != null) ReturnToTab(t);
    }

    void MediaClose_Click(object sender, RoutedEventArgs e)
    {
        _mediaDismissed = _mediaTab;
        HideMediaBar();
    }
}
