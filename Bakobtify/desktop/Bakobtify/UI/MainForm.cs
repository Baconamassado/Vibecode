using System.Drawing.Drawing2D;
using Bakobtify.Data;
using Bakobtify.Playback;
using Bakobtify.SoundCloud;

namespace Bakobtify.UI;

internal sealed partial class MainForm : Form
{
    private enum Page { Home, Explore, Favorites, History, Playlist, Search, Settings, Diagnostics }
    private sealed record Route(Page Page, string Value = "", long PlaylistId = 0);

    private readonly Database _db;
    private readonly SoundCloudClient _client;
    private readonly AudioEngine _audio;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ArtworkCache _artwork;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 180 };
    private readonly ToolTip _tips = new() { OwnerDraw = true, InitialDelay = 450, ReshowDelay = 150, AutoPopDelay = 5000 };
    private readonly DiscordPresence _presence = new();
    private readonly Bitmap? _appMark;
    private readonly Surface _header = new(Theme.Background);
    private readonly Surface _sidebar = new(Theme.Sidebar);
    private readonly Surface _viewport = new(Theme.Background) { AutoScroll = true, TabStop = false };
    private readonly Surface _player = new(Theme.Sidebar);
    private readonly Surface _queuePane = new(Theme.Sidebar) { Visible = false };
    private readonly FlowLayoutPanel _playlistNav = new()
    {
        FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true
    };
    private readonly FlowLayoutPanel _queueItems = new()
    {
        FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true
    };
    private readonly Surface _searchBox = new(Theme.Background);
    private readonly TextBox _search = new()
    {
        BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text,
        Font = Theme.Font(14), MaxLength = 300
    };
    private readonly SoftButton _searchHint = new()
    {
        Text = "Buscar músicas, artistas...", Font = Theme.Font(14), ForeColor = Theme.Muted,
        HoverColor = Theme.Surface, AlignLeft = true, AccessibleName = "Buscar músicas ou artistas"
    };
    private readonly Slider _seek = new() { AccessibleName = "Posição da faixa", Enabled = false };
    private readonly Slider _volume = new() { AccessibleName = "Volume", Live = true };
    private readonly Label _nowTitle = Theme.Label("Nenhuma faixa", 14, bold: true);
    private readonly Label _nowArtist = Theme.Label("Escolha uma faixa", 13, Theme.Muted);
    private readonly Label _elapsed = Theme.Label("0:00", 12, Theme.Muted);
    private readonly Label _duration = Theme.Label("0:00", 12, Theme.Muted);
    private readonly Label _notice = Theme.Label("", 13);
    private readonly Label _status = Theme.Label("SoundCloud · em espera", 12, Theme.Muted);
    private readonly Dictionary<Page, SoftButton> _nav = new();
    private readonly Stack<Route> _back = new();
    private readonly Stack<Route> _forward = new();
    private readonly List<Track> _queue = new();
    private readonly List<int> _playedIndices = new();
    private Route _route = new(Page.Home);
    private List<PlaylistInfo> _playlists = [];
    private List<Track> _favorites = [];
    private List<Track> _history = [];
    private List<Track> _results = [];
    private List<Track> _discovery = [];
    private CancellationTokenSource? _request;
    private bool _busy;
    private bool _discovering;
    private string? _loadError;
    private int _index = -1;
    private int _repeat;
    private bool _shuffle;
    private bool _closing;
    private Track? _current;
    private PlaybackState _playState;
    private long _playRequest;
    private DateTime _noticeUntil;
    private string _phase = "";
    private Surface? _content;
    private float _scale = 1;
    private Label _libraryLabel = null!, _queueHeading = null!;
    private SoftButton _logo = null!, _addPlaylist = null!, _queueClose = null!;

    private SoftButton _backButton = null!, _forwardButton = null!, _settings = null!;
    private SoftButton _min = null!, _max = null!, _close = null!;
    private SoftButton _cover = null!, _like = null!, _play = null!, _prev = null!, _next = null!;
    private SoftButton _mute = null!, _showQueue = null!, _repeatButton = null!;
    private SoftButton _localAudio = null!;

    public MainForm(Database db, SoundCloudClient client, AudioEngine audio, long? appStarted = null,
        long? databaseElapsed = null)
    {
        var startup = appStarted ?? Environment.TickCount64;
        if (databaseElapsed is { } elapsed) LogDiagnostic("Aplicativo", $"Banco local aberto em {elapsed:N0} ms.");
        _db = db;
        _client = client;
        _audio = audio;
        _allowEmbeddedPlayer = _db.GetMeta(EmbeddedPlayerSetting)?.Value != "off";
        _disableEmbeddedGpu = _db.GetMeta(EmbeddedGpuSetting)?.Value == "off";
        LoadPresenceSettings();
        LoadShortcuts();
        LoadRecommendationCandidates();
        ConfigureToolTips();
        _artwork = new ArtworkCache(client.Http, _lifetime.Token, () =>
        {
            _content?.Invalidate(true);
            _cover?.Invalidate();
            if (_queuePane.Visible) _queuePane.Invalidate(true);
        });
        Text = "Bakobtify";
        if (Icon.ExtractAssociatedIcon(Application.ExecutablePath) is { } appIcon)
        {
            Icon = appIcon;
            _appMark = appIcon.ToBitmap();
        }
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        MinimumSize = new Size(850, 570);
        ClientSize = new Size(1280, 800);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        DoubleBuffered = true;
        KeyPreview = true;
        Controls.AddRange([_viewport, _sidebar, _header, _queuePane, _player, _notice]);
        BuildHeader();
        BuildSidebar();
        BuildPlayer();
        BuildQueue();
        _notice.BackColor = Theme.Hover;
        _notice.TextAlign = ContentAlignment.MiddleCenter;
        _notice.Visible = false;
        _notice.BringToFront();
        ReloadLibrary();
        Arrange();
        if (_content is null) Render();
        Resize += (_, _) => Arrange();
        Shown += (_, _) =>
        {
            LogDiagnostic("Aplicativo", "Janela exibida.", startup);
            _viewport.Select();
            _timer.Start();
            _ = LoadDiscoveryAsync();
        };
        _timer.Tick += (_, _) => UpdateClock();
        _audio.Error += AudioError;
        _audio.ProgressChanged += AudioProgress;
        _audio.Ended += AudioEnded;
        _audio.AudioAnalyzed += RememberAudioPattern;
    }

    private SoftButton ButtonAt(Control parent, string caption, string glyph, Action click, string? tip = null)
    {
        var button = new SoftButton
        {
            Text = caption, Glyph = glyph, AccessibleName = tip ?? caption, BackColor = parent.BackColor
        };
        parent.Controls.Add(button);
        button.Click += (_, _) => RunAction(click);
        if (tip is not null) _tips.SetToolTip(button, tip);
        return button;
    }

    private void RunAction(Action action)
    {
        try { action(); }
        catch (Exception) { Notify("Não foi possível concluir a ação. Verifique se o banco local está acessível."); }
    }

    private void BuildHeader()
    {
        _logo = ButtonAt(_header, "Bakobtify", "", () => Navigate(new(Page.Home)));
        _logo.DrawContent = (g, r) =>
        {
            if (_appMark is not null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(_appMark, new Rectangle(0, 5, 25, 25));
            }
            Theme.Write(g, "Bakobtify", new Rectangle(32, 0, r.Width - 32, r.Height), Theme.Medium(16), Theme.Text);
        };
        _backButton = ButtonAt(_header, "", "back", () => StepRoute(_back, _forward), "Voltar");
        _forwardButton = ButtonAt(_header, "", "forward", () => StepRoute(_forward, _back), "Avançar");
        _settings = ButtonAt(_header, "", "settings", () => Navigate(new(Page.Settings)), "Configurações");
        _min = ButtonAt(_header, "", "min", () => WindowState = FormWindowState.Minimized, "Minimizar");
        _max = ButtonAt(_header, "", "max", ToggleMaximize, "Maximizar ou restaurar");
        _close = ButtonAt(_header, "", "close", Close, "Fechar");
        _close.HoverColor = Color.FromArgb(139, 43, 50);
        foreach (var button in new[] { _backButton, _forwardButton, _settings, _min, _max, _close })
            button.ForeColor = Theme.Muted;
        foreach (var button in new[] { _min, _max, _close })
            button.BackColor = Theme.Surface;
        _header.Controls.Add(_searchBox);
        _searchBox.Controls.AddRange([_search, _searchHint]);
        _searchHint.BackColor = Theme.Surface;
        _searchHint.Cursor = Cursors.IBeam;
        _searchHint.Click += (_, _) => FocusSearch();
        _searchBox.Click += (_, _) => FocusSearch();
        void SyncSearchHint()
        {
            var showHint = !_search.Focused && _search.TextLength == 0;
            _search.Visible = !showHint;
            _searchHint.Visible = showHint;
            _searchBox.Invalidate();
        }
        _search.GotFocus += (_, _) => SyncSearchHint();
        _search.LostFocus += (_, _) => SyncSearchHint();
        _search.TextChanged += (_, _) => SyncSearchHint();
        SyncSearchHint();
        _search.AccessibleName = "Buscar músicas ou artistas";
        PaintDesign(_searchBox, (g, area) =>
        {
            var rect = new Rectangle(0, 0, area.Width - 1, area.Height - 1);
            Theme.Fill(g, rect, Theme.Surface, 11);
            Theme.Outline(g, rect, _search.Focused ? Color.FromArgb(105, 85, 68) : Theme.Border, 11);
            Icons.Draw(g, "search", new Rectangle(12, 8, 17, 17), Theme.Muted);
            Theme.Fill(g, new Rectangle(area.Width - 53, 6, 27, 20), Theme.Hover, 5);
            Theme.Fill(g, new Rectangle(area.Width - 23, 6, 17, 20), Theme.Hover, 5);
            Theme.Write(g, "Ctrl", new Rectangle(area.Width - 53, 6, 27, 20),
                Theme.Font(11), Theme.Muted, TextFormatFlags.HorizontalCenter);
            Theme.Write(g, "K", new Rectangle(area.Width - 23, 6, 17, 20),
                Theme.Font(11), Theme.Muted, TextFormatFlags.HorizontalCenter);
        });
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Search(_search.Text); }
        };
        _header.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && e.Clicks == 2) ToggleMaximize();
            else if (e.Button == MouseButtons.Left)
            {
                _header.Capture = false;
                Native.SendMessage(Handle, 0x00A1, 2, 0);
            }
        };
        PaintDesign(_header, (g, area) =>
        {
            using var pen = new Pen(Theme.Border);
            g.DrawLine(pen, 0, area.Height - 1, area.Width, area.Height - 1);
            var frame = new Rectangle(Units(_min.Left) - 4, 8, 114, 33);
            Theme.Fill(g, frame, Theme.Surface, 11);
            Theme.Outline(g, frame, Theme.Border, 11);
        });
    }

    private void BuildSidebar()
    {
        _sidebar.Controls.AddRange([_playlistNav, _status]);
        _libraryLabel = Theme.Label("PLAYLISTS", 11, Theme.Muted, true);
        _sidebar.Controls.Add(_libraryLabel);
        _addPlaylist = ButtonAt(_sidebar, "", "plus", () => NewPlaylist(), "Criar playlist");
        _addPlaylist.ForeColor = Theme.Muted;
        AddNav(Page.Home, "Início", "home");
        AddNav(Page.Explore, "Explorar", "explore");
        AddNav(Page.Favorites, "Músicas curtidas", "heart");
        AddNav(Page.History, "Histórico", "history");
        _sidebar.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, _sidebar.Width - 1, 0, _sidebar.Width - 1, _sidebar.Height);
        };
    }

    private void AddNav(Page page, string label, string glyph)
    {
        var button = ButtonAt(_sidebar, label, glyph, () => Navigate(new(page)));
        button.AlignLeft = true;
        button.Font = Theme.Medium(15);
        _nav[page] = button;
    }

    private void BuildPlayer()
    {
        _player.Controls.AddRange([_nowTitle, _nowArtist, _seek, _elapsed, _duration, _volume]);
        _cover = ButtonAt(_player, "", "", ToggleQueue, "Faixa atual");
        _localAudio = ButtonAt(_player, "Vincular áudio", "", () =>
        {
            if (_current is { } track) ChooseLocalAudio(track);
        });
        _localAudio.ForeColor = Theme.Accent;
        _localAudio.AlignLeft = true;
        _localAudio.Font = Theme.Medium(13);
        _cover.DrawContent = (g, r) => _artwork.Draw(g, r, _current?.Artwork, Theme.Accent, 8);
        _like = ButtonAt(_player, "", "heart", () => { if (_current is { } t) ToggleFavorite(t); }, "Curtir faixa");
        _prev = ButtonAt(_player, "", "prev", Previous, "Faixa anterior");
        _next = ButtonAt(_player, "", "next", () => Advance(false), "Próxima faixa");
        _play = ButtonAt(_player, "", "play", TogglePlayback, "Reproduzir ou pausar");
        _play.FillColor = Theme.Text;
        _play.HoverColor = Color.White;
        _play.ForeColor = Theme.Background;
        _play.Radius = 24;
        _repeatButton = ButtonAt(_player, "", "repeat", CycleRepeat, "Repetição");
        _showQueue = ButtonAt(_player, "", "queue", ToggleQueue, "Fila de reprodução");
        _mute = ButtonAt(_player, "", "volume", ToggleMute, "Silenciar ou reativar som");
        foreach (var button in new[] { _like, _prev, _next, _repeatButton, _showQueue, _mute })
            button.ForeColor = Theme.Muted;
        _seek.Committed += value => SeekPlayback(PlaybackDuration * value);
        _seek.PreviewChanged += UpdateClock;
        _volume.Value = _audio.Volume;
        _volume.Committed += SetVolume;
        _elapsed.TextAlign = ContentAlignment.MiddleRight;
        _player.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, _player.Width, 0);
        };
        UpdatePlayer();
    }

    private void BuildQueue()
    {
        _queueHeading = Theme.Label("Fila de reprodução", 19, bold: true);
        _queuePane.Controls.AddRange([_queueHeading, _queueItems]);
        _queueClose = ButtonAt(_queuePane, "", "close", ToggleQueue, "Fechar fila");
        _queuePane.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, 0, _queuePane.Height);
        };
    }

    private int Pixels(int units) => (int)Math.Round(units * _scale);
    private int Units(int pixels) => (int)Math.Round(pixels / _scale);

    private void Place(Control control, int x, int y, int width, int height)
    {
        ApplyPaintScale(control);
        var left = Pixels(x);
        var top = Pixels(y);
        control.SetBounds(left, top, Math.Max(0, Pixels(x + width) - left), Math.Max(0, Pixels(y + height) - top));
    }

    private void ApplyPaintScale(Control control)
    {
        if (control is SoftButton button) button.PaintScale = _scale;
        if (control is ThemeLabel label) label.PaintScale = _scale;
        if (control is Slider slider) slider.PaintScale = _scale;
    }

    private void PaintDesign(Control control, Action<Graphics, Rectangle> paint)
    {
        control.Paint += (_, e) =>
        {
            var state = e.Graphics.Save();
            var scale = control == _searchBox ? _scale * .95f : _scale;
            paint(e.Graphics, Theme.DesignArea(e.Graphics, control.ClientSize, scale));
            e.Graphics.Restore(state);
        };
    }

    private void Arrange()
    {
        if (ClientSize.Width < 400 || ClientSize.Height < 180) return;
        // Referência de 1280 x 800. A altura determina a escala dos elementos
        // e a barra lateral acompanha a largura da janela.
        var scale = Math.Clamp(Math.Min(ClientSize.Height / 800f, ClientSize.Width / 850f), .85f, 2.5f);
        var scaleChanged = Math.Abs(scale - _scale) > .001f;
        var oldScroll = scaleChanged ? Units(-_viewport.AutoScrollPosition.Y) : 0;
        _scale = scale;
        const int top = 50, bottom = 84;
        var w = Units(ClientSize.Width);
        var h = Units(ClientSize.Height);
        var side = Math.Clamp(w * 137 / 1000, 144, 252);
        var body = h - top - bottom;
        var queueWidth = _queuePane.Visible && w - side >= 930 ? 300 : 0;
        var bodyTop = Pixels(top);
        var bodyBottom = ClientSize.Height - Pixels(bottom);
        var sidebarWidth = Pixels(side);
        _header.SetBounds(0, 0, ClientSize.Width, bodyTop);
        _sidebar.SetBounds(0, bodyTop, sidebarWidth, bodyBottom - bodyTop);
        _viewport.SetBounds(sidebarWidth, bodyTop, ClientSize.Width - sidebarWidth - Pixels(queueWidth), bodyBottom - bodyTop);
        _queuePane.SetBounds(ClientSize.Width - Pixels(300), bodyTop, Pixels(300), bodyBottom - bodyTop);
        _player.SetBounds(0, bodyBottom, ClientSize.Width, ClientSize.Height - bodyBottom);
        Place(_notice, Math.Max(side + 12, w / 2 - 300), h - bottom - 50, Math.Min(620, w - side - 24), 39);

        Place(_libraryLabel, 28, 108, Math.Min(116, side - 80), 25);
        Place(_addPlaylist, side - 50, 106, 34, 30);
        Place(_nav[Page.Home], 16, 17, side - 32, 36);
        Place(_nav[Page.Explore], 16, 55, side - 32, 36);
        Place(_nav[Page.Favorites], 16, 143, side - 32, 36);
        _nav[Page.Favorites].Text = side < 185 ? "Curtidas" : "Músicas curtidas";
        Place(_nav[Page.History], 16, 181, side - 32, 36);
        Place(_playlistNav, 16, 221, side - 32, body - 253);
        Place(_status, 27, body - 29, side - 42, 20);
        SizePlaylistNav();

        void TopButton(SoftButton button, int x, int y, int width, int height)
        {
            Place(button, x + width / 40, y + height / 40,
                (int)Math.Round(width * .95), (int)Math.Round(height * .95));
            button.PaintScale = _scale * .95f;
        }
        TopButton(_logo, 15, 7, 125, 34);
        TopButton(_backButton, side, 8, 31, 33);
        TopButton(_forwardButton, side + 32, 8, 31, 33);
        TopButton(_settings, w - 180, 8, 34, 33);
        TopButton(_min, w - 116, 12, 35, 25);
        TopButton(_max, w - 81, 12, 35, 25);
        TopButton(_close, w - 46, 12, 34, 25);
        _max.Glyph = WindowState == FormWindowState.Maximized ? "restore" : "max";
        _max.Invalidate();
        var searchX = Math.Max(side + 75, (w - w * 28 / 100) / 2);
        var searchWidth = Math.Max(180, Math.Min(w * 28 / 100, w - 194 - searchX));
        var compactSearch = (int)Math.Round(searchWidth * .95);
        Place(_searchBox, searchX + (searchWidth - compactSearch) / 2, 10, compactSearch, 30);
        _search.Font = Theme.Font((int)Math.Round(Pixels(14) * .95));
        Place(_search, 35, (30 - Units(_search.PreferredHeight)) / 2, compactSearch - 91, 20);
        Place(_searchHint, 27, 4, compactSearch - 81, 22);
        _searchHint.PaintScale = _scale * .95f;

        Place(_cover, 21, 18, 48, 48);
        var info = Math.Clamp(w * 174 / 1000, 145, 285);
        Place(_nowTitle, 81, 20, info - 74, 22);
        Place(_nowArtist, 81, 41, info - 74, 18);
        Place(_localAudio, 73, 38, info - 68, 24);
        Place(_like, info + 7, 26, 32, 32);
        Place(_prev, w / 2 - 62, 12, 36, 38);
        Place(_play, w / 2 - 18, 12, 36, 36);
        Place(_next, w / 2 + 26, 12, 36, 38);
        var seekWidth = w * 30 / 100;
        var seekX = (w - seekWidth) / 2;
        Place(_seek, seekX, 54, seekWidth, 20);
        Place(_elapsed, seekX - 47, 54, 42, 20);
        Place(_duration, seekX + seekWidth + 5, 54, 42, 20);
        Place(_repeatButton, w - 219, 27, 32, 30);
        Place(_showQueue, w - 181, 27, 32, 30);
        Place(_mute, w - 142, 27, 31, 30);
        Place(_volume, w - 113, 30, 96, 24);
        Place(_queueHeading, 18, 11, 235, 38);
        Place(_queueClose, 252, 12, 30, 30);
        Place(_queueItems, 10, 54, 280, body - 60);
        if (scaleChanged && _content is not null)
        {
            Render(false);
            _viewport.AutoScrollPosition = new Point(0, Pixels(oldScroll));
            RenderQueue();
        }
        else ResizeContent();
    }

    private void ToggleMaximize()
    {
        MaximizedBounds = Screen.FromControl(this).WorkingArea;
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.Style |= 0x00020000 | 0x00010000 | 0x00080000;
            return parameters;
        }
    }

    private readonly TaskbarPlayback _taskbarPlayback = new();
    private bool _taskbarReady;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterShortcuts();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (IsHandleCreated) UnregisterShortcuts();
        else _registeredShortcuts.Clear();
        _taskbarReady = false;
        _taskbarPlayback.Dispose();
        base.OnHandleDestroyed(e);
    }

    private void UpdateTaskbar()
    {
        if (_taskbarReady && _play is not null)
            _taskbarPlayback.Update(Handle, _prev.Enabled, _play.Enabled, _next.Enabled, _play.Glyph);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && _registeredShortcuts.Contains(m.WParam.ToInt32() - FirstHotkey))
        {
            if (Enabled && !_closing) RunShortcut(m.WParam.ToInt32() - FirstHotkey);
            return;
        }
        if (_taskbarReady && _play is not null && Enabled && !_closing &&
            m.Msg == 0x0111 && ((long)m.WParam >> 16 & 0xffff) == 0x1800)
        {
            switch ((int)((long)m.WParam & 0xffff))
            {
                case TaskbarPlayback.Previous: if (_prev.Enabled) Previous(); return;
                case TaskbarPlayback.Toggle: if (_play.Enabled) TogglePlayback(); return;
                case TaskbarPlayback.Next: if (_next.Enabled) Advance(false); return;
            }
        }
        base.WndProc(ref m);
        if (TaskbarPlayback.CreatedMessage != 0 && m.Msg == TaskbarPlayback.CreatedMessage)
        {
            _taskbarReady = true;
            _taskbarPlayback.Reset();
            UpdateTaskbar();
        }
        if (m.Msg != 0x0084 || WindowState != FormWindowState.Normal) return;
        var raw = (long)m.LParam;
        var p = PointToClient(new Point((short)raw, (short)(raw >> 16)));
        var left = p.X < 6; var right = p.X >= ClientSize.Width - 6;
        var top = p.Y < 5; var bottom = p.Y >= ClientSize.Height - 6;
        m.Result = (left, right, top, bottom) switch
        {
            (true, _, true, _) => 13, (_, true, true, _) => 14,
            (true, _, _, true) => 16, (_, true, _, true) => 17,
            (true, _, _, _) => 10, (_, true, _, _) => 11,
            (_, _, true, _) => 12, (_, _, _, true) => 15, _ => m.Result
        };
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.K) || keyData == Keys.OemQuestion && !_search.Focused)
        {
            FocusSearch();
            _search.SelectAll();
            return true;
        }
        if (keyData == (Keys.Alt | Keys.Left)) { StepRoute(_back, _forward); return true; }
        if (keyData == (Keys.Alt | Keys.Right)) { StepRoute(_forward, _back); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void FocusSearch()
    {
        _search.Visible = true;
        _search.Focus();
    }

    private void Notify(string message, int seconds = 5)
    {
        _notice.Text = message;
        _noticeUntil = DateTime.Now.AddSeconds(seconds);
        _notice.Visible = true;
        _notice.BringToFront();
    }

    private void ReloadLibrary()
    {
        _favorites = _db.ListFavorites();
        _playlists = _db.ListPlaylists();
        _history = _db.ListHistory();
        _collectionCandidates = _db.ListRecentPlaylistTracks(CandidateLimit);
        ClearControls(_playlistNav);
        foreach (var playlist in _playlists)
        {
            var button = ButtonAt(_playlistNav, playlist.Name, "list",
                () => Navigate(new(Page.Playlist, PlaylistId: playlist.Id)));
            button.AlignLeft = true;
            button.Margin = new Padding(0, 0, 0, Pixels(2));
            button.Tag = playlist.Id;
        }
        SizePlaylistNav();
    }

    private void SizePlaylistNav()
    {
        var width = Math.Max(0, _playlistNav.Width - SystemInformation.VerticalScrollBarWidth);
        foreach (Control item in _playlistNav.Controls)
        {
            ApplyPaintScale(item);
            item.Size = new Size(width, Pixels(35));
            item.Margin = new Padding(0, 0, 0, Pixels(2));
        }
    }

    private static void ClearControls(Control parent)
    {
        while (parent.Controls.Count > 0) parent.Controls[^1].Dispose();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closing = true;
        _presence.Dispose();
        _appMark?.Dispose();
        CloseEmbeddedPlayer();
        _lifetime.Cancel();
        _request?.Cancel();
        _timer.Dispose();
        _tips.Dispose();
        _artwork.Dispose();
        _audio.Error -= AudioError;
        _audio.ProgressChanged -= AudioProgress;
        _audio.Ended -= AudioEnded;
        _audio.AudioAnalyzed -= RememberAudioPattern;
        base.OnFormClosed(e);
    }

    private static class Native
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    }
}
