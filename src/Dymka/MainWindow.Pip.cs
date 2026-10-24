using Dymka.Core;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Dymka;

/// <summary>Мини-окно с видео («картинка в картинке»): видео со звуком при уходе со вкладки продолжается
/// в окне движка поверх всех окон, при возвращении на вкладку - возвращается на страницу.</summary>
public partial class MainWindow
{
    // выбирается самое большое видео, которое сейчас играет со звуком
    const string PipEnterJs = @"(async()=>{try{
if(!document.pictureInPictureEnabled)return 'off';
if(document.pictureInPictureElement)return 'already';
var vs=Array.prototype.slice.call(document.querySelectorAll('video')).filter(function(v){
 return !v.paused&&!v.ended&&v.readyState>2&&!v.disablePictureInPicture&&v.videoWidth>0&&(__ANYSOUND__||(!v.muted&&v.volume>0));});
if(!vs.length)return 'none';
vs.sort(function(a,b){return b.clientWidth*b.clientHeight-a.clientWidth*a.clientHeight;});
var v=vs[0];
if(!v.__dymkaPip){v.__dymkaPip=1;v.addEventListener('leavepictureinpicture',function(){
 try{chrome.webview.postMessage({__dymka:'pip',on:false});}catch(e){}});}
await v.requestPictureInPicture();
return 'ok';}catch(e){return 'err:'+(e&&e.message);}})()";

    const string PipExitJs = @"(async()=>{try{if(document.pictureInPictureElement){await document.exitPictureInPicture();return 'ok';}return 'none';}catch(e){return 'err:'+(e&&e.message);}})()";

    /// <summary>Для самопроверки: видео без звука тоже уходит в мини-окно.</summary>
    public bool PipAnySoundForTest { get; set; }

    /// <summary>Последний ответ страницы при попытке открыть мини-окно (для журнала и самопроверки).</summary>
    public string LastPipResult { get; private set; } = "";

    /// <summary>Вызывается при переходе с вкладки old на вкладку tab.</summary>
    void PipOnSwitch(BrowserTab? old, BrowserTab tab)
    {
        if (tab.InPip) _ = PipExitAsync(tab);
        if (old == null || old == tab || !_settings.AutoPip || old.Core == null || InSplit(old)) return;
        if (!old.IsPlayingAudio && !PipAnySoundForTest) return;
        if (old.IsMuted && !PipAnySoundForTest) return;
        _ = PipEnterAsync(old);
    }

    async Task PipEnterAsync(BrowserTab tab)
    {
        var res = await EvalWithGestureAsync(tab.Core, PipEnterJs.Replace("__ANYSOUND__", PipAnySoundForTest ? "true" : "false"));
        LastPipResult = res;
        if (res == "ok")
        {
            // если вы уже вернулись на эту вкладку, мини-окно не нужно
            if (tab == _active) { await EvalWithGestureAsync(tab.Core, PipExitJs); return; }
            tab.InPip = true;
            App.Log("Мини-окно с видео: " + UrlTools.HostOf(tab.Url));
        }
        else if (res.StartsWith("err") || res == "off")
            App.Log($"Мини-окно с видео не открылось ({UrlTools.HostOf(tab.Url)}): {res}");
    }

    async Task PipExitAsync(BrowserTab tab)
    {
        tab.InPip = false;
        await EvalWithGestureAsync(tab.Core, PipExitJs);
    }

    /// <summary>Выполняет скрипт на странице так, как будто это действие пользователя
    /// (мини-окно видео браузер открывает только по действию пользователя).</summary>
    static async Task<string> EvalWithGestureAsync(CoreWebView2? core, string js)
    {
        if (core == null) return "no-view";
        try
        {
            var args = JsonSerializer.Serialize(new { expression = js, userGesture = true, awaitPromise = true, returnByValue = true });
            var json = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("value", out var v))
                return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
            return "?";
        }
        catch (Exception ex)
        {
            return "err:" + ex.Message;
        }
    }

    /// <summary>Сообщение страницы: мини-окно закрыли.</summary>
    void OnPipMessage(BrowserTab tab, JsonElement root)
    {
        if (root.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.False) tab.InPip = false;
    }
}
