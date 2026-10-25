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
            bool can = t != null && t != _active && !t.InPip && t != _mediaDismissed && !InSplit(t)
                       && (IsActive || MediaTestTab != null) && WindowState != WindowState.Minimized && !_fullscreen;
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

    /// <summary>Нижний правый угол страницы; на новой вкладке - выше её кнопки настроек.</summary>
    void PositionMediaBar()
    {
        if (!MediaBar.IsOpen || MediaBar.Child is not FrameworkElement card) return;
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var sz = card.DesiredSize;
        double bottom = _active?.IsStart == true ? 64 : 4;   // кнопка настроек новой вкладки: 20 + 44 точки от низа
        double x = Math.Max(0, ContentHost.ActualWidth - sz.Width - 8);
        double y = Math.Max(0, ContentHost.ActualHeight - sz.Height - bottom);
        // при перемещении окна смещение не меняется - чуть сдвигаем, чтобы окно мини-плеера переехало вместе с браузером
        _mediaNudge = !_mediaNudge;
        MediaBar.HorizontalOffset = x + (_mediaNudge ? 0.01 : 0);
        MediaBar.VerticalOffset = y;
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
