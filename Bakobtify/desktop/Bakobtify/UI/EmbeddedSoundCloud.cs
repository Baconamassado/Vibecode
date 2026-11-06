using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
#if BAKOBTIFY_WEBVIEW2
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
#endif

namespace Bakobtify.UI;

internal sealed class EmbeddedSoundCloud : Panel
{
    internal const string PageUrl = "https://bakobtify.invalid/soundcloud";
#if BAKOBTIFY_WEBVIEW2
    public static bool Available => true;
    private readonly WebView2 _view = new() { Dock = DockStyle.Fill };
    private Task? _initialization;
    private string? _documentUrl;
    private byte[]? _document;
    private ulong? _navigation;
#else
    public static bool Available => false;
#endif
    public TimeSpan Position { get; private set; }
    public TimeSpan Duration { get; private set; }
    public bool Playing { get; private set; }
    public event Action? Changed;
    public event Action? Ended;
    public event Action<string>? Failed;
    private int _volume = 80;
    private bool _wantsPlayback = true;
    private bool _ready;
    private TimeSpan _initialPosition;
    private readonly System.Windows.Forms.Timer _startup = new() { Interval = 20_000 };

    private readonly bool _disableGpu;
    public bool UsesDisabledGpu => _disableGpu;

    public EmbeddedSoundCloud(bool disableGpu = false)
    {
        _disableGpu = disableGpu;
        BackColor = Theme.Sidebar;
        Bounds = new Rectangle(2, 3, 2, 2);
        TabStop = false;
        _startup.Tick += (_, _) =>
        {
            _startup.Stop();
            if (!_ready) Failed?.Invoke("O player do SoundCloud não respondeu ao iniciar.");
        };
#if BAKOBTIFY_WEBVIEW2
        _view.TabStop = false;
        Controls.Add(_view);
#endif
    }

    public async Task LoadAsync(Uri track, TimeSpan position = default)
    {
#if BAKOBTIFY_WEBVIEW2
        SuspendTrack();
        _wantsPlayback = true;
        Position = _initialPosition = position;
        var url = PageUrl + "?track=" + Guid.NewGuid().ToString("N");
        _documentUrl = url;
        _document = Encoding.UTF8.GetBytes(BuildPage(track, _volume));
        _startup.Start();
        try
        {
            await (_initialization ??= InitializeAsync());
            if (!IsDisposed && _documentUrl == url) _view.CoreWebView2.Navigate(url);
        }
        catch (Exception) when (!IsDisposed && _documentUrl == url)
        {
            Failed?.Invoke("Não foi possível inicializar o WebView2.");
        }
        catch (Exception) when (IsDisposed || _documentUrl != url) { }
#else
        await Task.CompletedTask;
        Failed?.Invoke("O WebView2 está indisponível.");
#endif
    }

#if BAKOBTIFY_WEBVIEW2
    private async Task InitializeAsync()
    {
        var profile = Path.Combine(AppConfig.DataDirectory, "cache",
            _disableGpu ? "webview2-audio-gpu-off" : "webview2-audio");
        var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required" +
            (_disableGpu ? " --disable-gpu" : ""));
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile, options: options);
        if (IsDisposed) return;
        await _view.EnsureCoreWebView2Async(environment);
        if (IsDisposed) return;
        var core = _view.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.IsMuted = true;
        core.AddWebResourceRequestedFilter(PageUrl + "*", CoreWebView2WebResourceContext.Document);
        core.WebResourceRequested += (_, args) =>
        {
            if (args.Request.Uri != _documentUrl || _document is not { } page) return;
            args.Response = environment.CreateWebResourceResponse(new MemoryStream(page, writable: false), 200, "OK",
                "Content-Type: text/html; charset=utf-8");
        };
        core.NavigationStarting += (_, args) =>
        {
            args.Cancel = args.Uri != _documentUrl || _navigation is not null;
            if (args.Cancel) return;
            _navigation = args.NavigationId;
        };
        core.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess && args.NavigationId == _navigation)
                Failed?.Invoke("O WebView2 não carregou a página local do player.");
        };
        core.WebMessageReceived += (_, args) =>
        {
            if (args.Source == _documentUrl)
                Receive(args.WebMessageAsJson);
        };
    }
#endif

    public void SuspendTrack()
    {
        _startup.Stop();
#if BAKOBTIFY_WEBVIEW2
        _documentUrl = null;
        _document = null;
        _navigation = null;
        if (_view.CoreWebView2 is { } core) core.IsMuted = true;
#endif
        Run("window.setPlayback(false)");
        _ready = false;
        Playing = false;
        Position = Duration = _initialPosition = TimeSpan.Zero;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _startup.Dispose();
        base.Dispose(disposing);
    }

    public void Toggle()
    {
        _wantsPlayback = !Playing;
        if (_wantsPlayback) SetLowMemory(false);
        Run(_wantsPlayback ? "window.setPlayback(true)" : "window.setPlayback(false)");
    }
    public void Seek(TimeSpan position)
    {
        if (!_ready)
        {
            Position = _initialPosition = position;
            return;
        }
        Run($"player.seekTo({Math.Max(0, (long)position.TotalMilliseconds)})");
    }

    private void SetLowMemory(bool low)
    {
#if BAKOBTIFY_WEBVIEW2
        try
        {
            if (!IsDisposed && _view.CoreWebView2 is { } core)
                core.MemoryUsageTargetLevel = low
                    ? CoreWebView2MemoryUsageTargetLevel.Low
                    : CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch (InvalidOperationException) { }
        catch (System.Runtime.InteropServices.COMException) { }
        catch (NotImplementedException) { }
#endif
    }
    public void SetVolume(double volume)
    {
        _volume = (int)Math.Round(Math.Clamp(volume, 0, 1) * 100);
        Run($"player.setVolume({_volume})");
    }

    private async void Run(string command)
    {
#if BAKOBTIFY_WEBVIEW2
        try
        {
            if (!IsDisposed && _view.CoreWebView2 is { } core)
                await core.ExecuteScriptAsync($"if (window.player) {command}");
        }
        catch (InvalidOperationException) { }
        catch (System.Runtime.InteropServices.COMException) { }
#else
        await Task.CompletedTask;
#endif
    }

    private void Receive(string json)
    {
        if (json.Length > 512) return;
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
        }
        catch (JsonException) { return; }
        using var doc = parsed;
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return;
        if (root.TryGetProperty("ms", out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var ms) && ms >= 0)
        {
            if (type.GetString() == "duration") Duration = TimeSpan.FromMilliseconds(ms);
            if (type.GetString() == "position") Position = TimeSpan.FromMilliseconds(ms);
        }
        switch (type.GetString())
        {
            case "play":
                if (!Playing) SetLowMemory(false);
                Playing = true;
                Changed?.Invoke();
                break;
            case "pause":
                if (Playing) SetLowMemory(true);
                Playing = false;
                Changed?.Invoke();
                break;
            case "finish": Playing = false; Ended?.Invoke(); break;
            case "ready":
                _startup.Stop();
                _ready = true;
#if BAKOBTIFY_WEBVIEW2
                _view.CoreWebView2.IsMuted = false;
#endif
                if (_initialPosition > TimeSpan.Zero) Seek(_initialPosition);
                Run($"player.setVolume({_volume}); window.setPlayback({(_wantsPlayback ? "true" : "false")})");
                break;
            case "error": Failed?.Invoke("O SoundCloud recusou a reprodução no WebView2."); break;
            case "timeout": Failed?.Invoke("O SoundCloud não iniciou o áudio no WebView2."); break;
        }
    }

    internal static string BuildPage(Uri track, int volume)
    {
        var source = WebUtility.HtmlEncode("https://w.soundcloud.com/player/?url=" +
            Uri.EscapeDataString(track.GetLeftPart(UriPartial.Path)) +
            "&auto_play=false&enable_api=true&visual=false&hide_related=true&show_comments=false&show_reposts=false");
        return $$"""
            <!doctype html><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; frame-src https://w.soundcloud.com; script-src 'unsafe-inline' https://w.soundcloud.com; style-src 'unsafe-inline'">
            <style>html,body{margin:0;height:100%;overflow:hidden;background:#111113}iframe{width:2px;height:2px;border:0;opacity:.01;pointer-events:none}</style>
            <iframe id="track" src="{{source}}" allow="autoplay; encrypted-media" title="Player do SoundCloud"></iframe>
            <script src="https://w.soundcloud.com/player/api.js"></script>
            <script>
              const send=(type,ms)=>chrome.webview.postMessage({type,ms});
              if(window.SC){
                window.player=SC.Widget(document.getElementById('track'));
                const e=SC.Widget.Events;
                let started=false, retries=0, retryTimer;
                window.setPlayback=play=>{
                  clearInterval(retryTimer);
                  if(!play){player.pause();return;}
                  retries=0;
                  retryTimer=setInterval(()=>{
                    if(++retries>=15){clearInterval(retryTimer);send('timeout');return;}
                    player.play();
                  },800);
                  player.play();
                };
                const readyTimer=setTimeout(()=>{if(!started)send('timeout');},15000);
                player.bind(e.PLAY,()=>{clearInterval(retryTimer);send('play');});
                player.bind(e.PAUSE,()=>send('pause'));
                player.bind(e.FINISH,()=>send('finish'));
                player.bind(e.ERROR,()=>send('error'));
                player.bind(e.PLAY_PROGRESS,p=>send('position',Math.round(p.currentPosition)));
                player.bind(e.SEEK,p=>send('position',Math.round(p.currentPosition)));
                player.bind(e.READY,()=>{
                  started=true;
                  clearTimeout(readyTimer);
                  player.setVolume({{volume.ToString(CultureInfo.InvariantCulture)}});
                  player.getDuration(ms=>send('duration',Math.round(ms)));
                  send('ready');
                });
              }else send('error');
            </script>
            """;
    }
}
