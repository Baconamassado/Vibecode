using System.Drawing.Drawing2D;
using Bakobtify.Data;
using Bakobtify.SoundCloud;

namespace Bakobtify.UI;

internal sealed partial class MainForm
{
    private static readonly (string Name, string Query)[] Filters =
    [
        ("Tudo", ""), ("Para treinar", "workout"), ("Foco", "lofi beats"),
        ("Relaxar", "chillout"), ("Festa", "dance party"), ("Viagem", "road trip")
    ];

    private static readonly (string Name, string Query, Color Color)[] Genres =
    [
        ("Pop", "pop", Color.FromArgb(165, 102, 79)), ("Rock", "rock", Color.FromArgb(92, 132, 194)),
        ("Eletrônica", "electronic", Color.FromArgb(79, 150, 119)), ("MPB", "MPB", Color.FromArgb(162, 135, 63)),
        ("Jazz", "jazz", Color.FromArgb(155, 100, 144)), ("Hip-hop", "hip hop", Color.FromArgb(143, 140, 132)),
        ("Indie", "indie", Color.FromArgb(102, 138, 196)), ("Clássica", "classical music", Color.FromArgb(155, 121, 82))
    ];

    private static readonly Color[] Colors =
    [
        Theme.Accent, Color.FromArgb(125, 177, 235), Color.FromArgb(233, 199, 161),
        Color.FromArgb(155, 205, 111), Theme.Pink
    ];

    private static Color Palette(int index) => Colors[index % Colors.Length];

    private void Navigate(Route route)
    {
        if (_route == route) return;
        _back.Push(_route);
        _forward.Clear();
        OpenRoute(route);
    }

    private void StepRoute(Stack<Route> source, Stack<Route> destination)
    {
        if (!source.TryPop(out var route)) return;
        destination.Push(_route);
        OpenRoute(route);
    }

    private void OpenRoute(Route route)
    {
        _request?.Cancel();
        _request = null;
        _results = [];
        _loadError = null;
        _artistProfile = null;
        _artistTracks.Clear();
        _artistReleases = null;
        _artistNextPage = null;
        _showArtistReleases = false;
        _busy = route.Page is Page.Artist or Page.Search || route.Page == Page.Explore && route.Value.Length > 0;
        _route = route;
        _search.Text = route.Page == Page.Search ? route.Value : "";
        if (_search.Focused) ActiveControl = null;
        Render(false);
        if (route.ArtistTrack is not null) _ = LoadArtistAsync(route);
        else if (_busy) _ = LoadResultsAsync(route);
        else if (route.Page is Page.Home or Page.Explore && _discovery.Count == 0) _ = LoadDiscoveryAsync();
    }

    private void Search(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return;
        var route = new Route(Page.Search, query);
        if (_route == route) OpenRoute(route);
        else Navigate(route);
    }

    private async Task LoadResultsAsync(Route route)
    {
        var started = Environment.TickCount64;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _request = request;
        try
        {
            List<Track> tracks;
            if (route.Page == Page.Search && Uri.TryCreate(route.Value, UriKind.Absolute, out var url) &&
                url.Scheme == Uri.UriSchemeHttps && url.Host is "soundcloud.com" or "www.soundcloud.com" or "on.soundcloud.com")
                tracks = [await _client.ResolvePermalinkAsync(url.GetLeftPart(UriPartial.Path), request.Token)];
            else
                tracks = await _client.SearchAsync(route.Value, 40, request.Token);
            if (!request.IsCancellationRequested)
            {
                _results = tracks;
                RememberRecommendationCandidates(tracks);
                LogDiagnostic("Aplicativo", $"Consulta ao catálogo retornou {tracks.Count} faixas.", started);
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) when (ex is SoundCloudException or HttpRequestException or OperationCanceledException)
        {
            if (!request.IsCancellationRequested)
            {
                _loadError = SearchError(ex);
                LogDiagnostic("Aplicativo", "Falha na consulta ao catálogo.", started);
            }
        }
        finally
        {
            if (_request == request) _request = null;
            if (!_closing && !request.IsCancellationRequested)
            {
                _busy = false;
                UpdateStatus(_loadError);
                Render();
            }
        }
    }

    private async Task LoadDiscoveryAsync()
    {
        if (_closing || _discovering || _discovery.Count > 0) return;
        _discovering = true;
        var started = Environment.TickCount64;
        string? error = null;
        try
        {
            _discovery = await _client.SearchAsync("Brazilian music", 40, _lifetime.Token);
            LogDiagnostic("Aplicativo", $"Catálogo inicial carregado com {_discovery.Count} faixas.", started);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is SoundCloudException or HttpRequestException or OperationCanceledException)
        {
            error = SearchError(ex);
            LogDiagnostic("Aplicativo", "Catálogo inicial indisponível.", started);
        }
        finally
        {
            _discovering = false;
            if (!_closing)
            {
                UpdateStatus(error);
                if (_route.Page == Page.Home || _route == new Route(Page.Explore)) Render();
            }
        }
        if (error is null && !_closing && !_lifetime.IsCancellationRequested)
            _ = LoadRecommendationCatalogAsync();
    }

    private static string SearchError(Exception error) => error switch
    {
        DegradedException => "A conexão com o catálogo do SoundCloud está indisponível.",
        SoundCloudException => error.Message,
        _ => "Não foi possível consultar o SoundCloud. Verifique a conexão e tente novamente."
    };

    private void UpdateStatus(string? error = null)
    {
        var status = _client.Status;
        var available = error is null && status.State == "ok";
        _status.Text = available ? "●  SoundCloud conectado" :
            error is not null || status.State == "degraded" ? "●  SoundCloud indisponível" : "●  SoundCloud · em espera";
        _status.ForeColor = available ? Color.FromArgb(155, 205, 111) : Theme.Muted;
        _tips.SetToolTip(_status, error ?? _status.Text);
    }

    private void Render(bool preserveScroll = true)
    {
        var oldScroll = preserveScroll ? -_viewport.AutoScrollPosition.Y : 0;
        _viewport.SuspendLayout();
        try
        {
            _viewport.AutoScrollPosition = Point.Empty;
            ClearControls(_viewport);
            _content = new Surface(Theme.Background);
            _viewport.Controls.Add(_content);
            ResizeContent();
            switch (_route.Page)
            {
                case Page.Home: BuildHome(_content); break;
                case Page.Explore: BuildExplore(_content); break;
                case Page.Favorites: BuildCollection(_content, "Coleção", "Músicas curtidas", _favorites); break;
                case Page.History: BuildCollection(_content, "Biblioteca", "Histórico", _history); break;
                case Page.Playlist:
                    var playlist = _playlists.Find(p => p.Id == _route.PlaylistId);
                    if (playlist is not null)
                        BuildCollection(_content, "Playlist", playlist.Name, _db.PlaylistTracks(playlist.Id), playlist);
                    else
                        BuildCollection(_content, "Playlist", "Playlist indisponível", []);
                    break;
                case Page.Search: BuildSearch(_content); break;
                case Page.Settings: BuildSettings(_content); break;
                case Page.Diagnostics: BuildDiagnostics(_content); break;
                case Page.Artist: BuildArtist(_content); break;
            }
            ResizeContent();
        }
        finally { _viewport.ResumeLayout(true); }
        _viewport.AutoScrollPosition = new Point(0, oldScroll);
        UpdateNavigation();
    }

    private void ResizeContent()
    {
        if (_content is null || _content.IsDisposed) return;
        var padding = Pixels(Math.Clamp(36 + (Units(ClientSize.Width) - 1280) / 30, 30, 49));
        var scroll = _viewport.AutoScrollPosition;
        var usable = _viewport.Width - SystemInformation.VerticalScrollBarWidth - 2 * padding;
        _content.SetBounds(padding + scroll.X, Pixels(31) + scroll.Y, Math.Max(Pixels(100), usable), _content.Height);
        _viewport.AutoScrollMinSize = new Size(0, _content.Height + Pixels(51));
    }

    private void UpdateNavigation()
    {
        _backButton.Enabled = _back.Count > 0;
        _forwardButton.Enabled = _forward.Count > 0;
        foreach (var (page, button) in _nav)
            StyleNav(button, page == _route.Page);
        foreach (Control item in _playlistNav.Controls)
            if (item is SoftButton button && item.Tag is long id)
                StyleNav(button, _route.Page == Page.Playlist && id == _route.PlaylistId);
    }

    private static void StyleNav(SoftButton button, bool active)
    {
        button.FillColor = active ? Theme.Hover : Color.Transparent;
        button.ForeColor = active ? Theme.Text : Theme.Muted;
        button.GlyphColor = active ? Theme.Accent : Theme.Muted;
        button.Invalidate();
    }

    private void Span(Control parent, Control child, int x, int y, int height, int right = 0)
    {
        void Position() => Place(child, x, y, Math.Max(0, Units(parent.Width) - x - right), height);
        if (child.Parent != parent) parent.Controls.Add(child);
        parent.SizeChanged += (_, _) => Position();
        Position();
    }

    private void Grid(Control parent, Control child, int index, int columns, int y,
        int height, int horizontalGap, int verticalGap = 0)
    {
        void Position()
        {
            var width = Math.Max(1, (Units(parent.Width) - horizontalGap * (columns - 1)) / columns);
            Place(child, (index % columns) * (width + horizontalGap),
                y + (index / columns) * (height + verticalGap), width, height);
        }
        if (child.Parent != parent) parent.Controls.Add(child);
        parent.SizeChanged += (_, _) => Position();
        Position();
    }

    private void BuildHome(Surface page)
    {
        var hour = DateTime.Now.Hour;
        var greeting = hour < 12 ? "Bom dia" : hour < 18 ? "Boa tarde" : "Boa noite";
        Span(page, Theme.Label(greeting, 36, bold: true), 0, 0, 53);
        var recent = _history.DistinctBy(t => t.Id).ToList();
        var shortcuts = new List<(string Name, string? Art, Color Color, Action Click, bool Heart)>
        {
            ("Músicas curtidas", null, Theme.Pink, () => Navigate(new(Page.Favorites)), true)
        };
        foreach (var playlist in _playlists.Take(5))
        {
            shortcuts.Add((playlist.Name, _db.PlaylistTracks(playlist.Id).FirstOrDefault()?.Artwork,
                Palette(shortcuts.Count - 1), () => Navigate(new(Page.Playlist, PlaylistId: playlist.Id)), false));
        }
        foreach (var track in recent.Concat(_discovery).DistinctBy(t => t.Id).Take(6 - shortcuts.Count))
            shortcuts.Add((track.Title, track.Artwork, Palette(shortcuts.Count - 1), () => PlayList([track]), false));
        for (var i = 0; i < shortcuts.Count; i++)
        {
            var (name, cover, color, click, heart) = shortcuts[i];
            var button = ButtonAt(page, name, "", click);
            button.FillColor = Theme.Surface;
            button.DrawContent = (g, r) =>
            {
                _artwork.Draw(g, new Rectangle(0, 0, r.Height - 1, r.Height - 1), cover, color, 10, heart);
                Theme.Write(g, name, new Rectangle(r.Height + 13, 0, r.Width - r.Height - 21, r.Height),
                    Theme.Medium(14), Theme.Text);
            };
            Grid(page, button, i, 3, 66, 56, 12, 11);
        }
        var recentShelf = BuildTrackShelf("Continue ouvindo", recent, "As faixas que você ouvir aparecem aqui.");
        var discoverShelf = BuildTrackShelf(HasRecommendationProfile ? "Para você" : "Descubra no SoundCloud",
            RecommendTracks(),
            _discovering || _loadingRecommendationCatalog || _relatedRequest is not null ? "Buscando faixas…" :
            HasRecommendationProfile ? "Ainda não há sugestões com afinidade suficiente." :
            "Explore o catálogo para encontrar novas faixas.");
        page.Controls.AddRange([recentShelf, discoverShelf]);
        var shelfTop = 66 + (shortcuts.Count + 2) / 3 * 67 + 24;
        var laidOutWidth = -1;
        void PositionShelves()
        {
            if (page.Width == laidOutWidth) return;
            laidOutWidth = page.Width;
            var width = Math.Max(1, Units(page.Width) - 20);
            Place(recentShelf, 10, shelfTop, width, Units(recentShelf.Height));
            Place(discoverShelf, 10, Units(recentShelf.Bottom) + 29, width, Units(discoverShelf.Height));
            page.Height = discoverShelf.Bottom + Pixels(12);
        }
        page.SizeChanged += (_, _) => PositionShelves();
        PositionShelves();
    }

    private Surface BuildTrackShelf(string title, IReadOnlyList<Track> tracks, string empty)
    {
        var section = new Surface(Theme.Background) { Height = Pixels(80), AccessibleName = title };
        Span(section, Theme.Label(title, 23, bold: true), 0, 0, 39);
        if (tracks.Count == 0)
        {
            Span(section, Theme.Label(empty, 14, Theme.Muted), 0, 42, 36);
            return section;
        }
        var shelf = tracks.Take(5).ToArray();
        var cards = new List<SoftButton>();
        for (var i = 0; i < shelf.Length; i++)
        {
            var index = i;
            var track = shelf[index];
            var card = ButtonAt(section, track.Title, "", () => PlayRow(shelf, index));
            cards.Add(card);
            card.DrawContent = (g, r) =>
            {
                var side = r.Width - 2;
                _artwork.Draw(g, new Rectangle(1, 1, side, side), track.Artwork, Palette(index), 11);
                Theme.Write(g, track.Title, new Rectangle(1, side + 9, side, 18), Theme.Medium(14), Theme.Text);
                Theme.Write(g, track.ArtistOrUnknown, new Rectangle(1, side + 28, side, 17), Theme.Font(13), Theme.Muted);
            };
            card.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowTrackMenu(card, e.Location, track); };
        }
        var lastWidth = -1;
        void PositionCards()
        {
            if (lastWidth == section.Width) return;
            lastWidth = section.Width;
            var sectionWidth = Units(section.Width);
            var gap = Math.Clamp(sectionWidth / 33, 10, 38);
            var width = Math.Max(1, (sectionWidth - gap * 4) / 5);
            var height = width + 46;
            for (var i = 0; i < cards.Count; i++)
                Place(cards[i], i * (width + gap), 45, width, height);
            section.Height = Pixels(45 + height);
        }
        section.SizeChanged += (_, _) => PositionCards();
        PositionCards();
        return section;
    }

    private void BuildExplore(Surface page)
    {
        Span(page, Theme.Label("Explorar", 36, bold: true), 0, 0, 53);
        var chips = new List<(SoftButton Button, int Width)>();
        foreach (var (label, query) in Filters)
        {
            var chip = ButtonAt(page, label, "", () => Navigate(new(Page.Explore, query)));
            var selected = query == _route.Value;
            chip.Stroke = Theme.Border;
            chip.FillColor = selected ? Theme.Text : Theme.Background;
            chip.HoverColor = selected ? Color.White : Theme.Hover;
            chip.ForeColor = selected ? Theme.Background : Theme.Muted;
            chip.Radius = 18;
            chip.Font = Theme.Font(14);
            chips.Add((chip, TextRenderer.MeasureText(label, chip.Font).Width + 28));
        }

        var tracks = _route.Value.Length == 0 ? _discovery : _results;
        var featured = tracks.FirstOrDefault() ?? (_route.Value.Length == 0 ? _history.FirstOrDefault() : null);
        var heroColor = Color.FromArgb(23, 23, 27);
        var hero = new Surface(Theme.Background);
        page.Controls.Add(hero);
        PaintDesign(hero, (g, area) =>
        {
            var r = new Rectangle(0, 0, area.Width - 1, area.Height - 1);
            Theme.Fill(g, r, heroColor, 17);
            Theme.Outline(g, r, Theme.Border, 17);
            using var heroShape = Theme.Rounded(r, 17);
            var saved = g.Save();
            g.SetClip(heroShape, CombineMode.Intersect);
            var side = Math.Min(225, area.Height - 56);
            var artworkX = area.Width - side - 33;
            var centerX = artworkX + side / 2;
            var centerY = area.Height / 2;
            using var ring = new Pen(Color.FromArgb(55, 55, 61), 1.5f);
            for (var radius = side / 2 + 6; radius <= side / 2 + 70; radius += 32)
                g.DrawEllipse(ring, centerX - radius, centerY - radius, radius * 2, radius * 2);
            _artwork.Draw(g, new Rectangle(artworkX, centerY - side / 2, side, side), featured?.Artwork, Theme.Accent, 12);
            g.Restore(saved);
            var width = Math.Max(20, artworkX - 60);
            var textY = (area.Height - 216) / 2;
            Theme.Write(g, featured is null ? "SOUNDCLOUD" : "EM DESTAQUE", new Rectangle(39, textY + 27, width, 22),
                Theme.Font(12, FontStyle.Bold), Theme.Accent);
            Theme.Write(g, featured?.Title ?? (_busy ? "Carregando…" : "Descubra novas faixas"),
                new Rectangle(39, textY + 49, width, 56), Theme.Heading(39), Theme.Text);
            Theme.Write(g, featured?.ArtistOrUnknown ?? "Escolha um gênero ou busque por um artista",
                new Rectangle(40, textY + 109, width, 23), Theme.Font(14), Theme.Muted);
        });
        var listen = ButtonAt(hero, featured is null ? "Buscar" : "Ouvir", featured is null ? "search" : "play",
            () => { if (featured is not null) PlayList(tracks.Count > 0 ? tracks : [featured]); else FocusSearch(); });
        listen.BackColor = heroColor;
        StylePrimary(listen);
        SoftButton? add = null;
        if (featured is not null)
        {
            add = ButtonAt(hero, "", "plus", () => AddToQueue(featured), "Adicionar à fila");
            add.BackColor = heroColor;
        }

        var genreHeading = Theme.Label("Gêneros", 23, bold: true);
        page.Controls.Add(genreHeading);
        var tiles = new List<SoftButton>();
        for (var i = 0; i < Genres.Length; i++)
        {
            var (name, query, color) = Genres[i];
            var tile = ButtonAt(page, name, "", () => Search(query));
            tiles.Add(tile);
            tile.FillColor = Color.FromArgb(23 + color.R / 10, 23 + color.G / 10, 23 + color.B / 10);
            tile.HoverColor = Color.FromArgb(31 + color.R / 8, 31 + color.G / 8, 31 + color.B / 8);
            tile.DrawContent = (g, r) =>
            {
                var state = g.Save();
                using var clip = Theme.Rounded(r, 11);
                g.SetClip(clip, CombineMode.Intersect);
                var diameter = r.Height - 7;
                var disc = new Rectangle(r.Right - diameter + 15, r.Height / 3, diameter, diameter);
                Theme.Fill(g, disc, Color.FromArgb(85, color), diameter / 2);
                var hole = diameter / 3;
                Theme.Fill(g, Rectangle.Inflate(disc, -hole, -hole), tile.FillColor, hole / 2);
                g.Restore(state);
                Theme.Write(g, name, new Rectangle(15, 10, r.Width - 28, 28), Theme.Font(18, FontStyle.Bold), Theme.Text);
            };
        }
        Surface? results = null;
        if (_route.Value.Length > 0)
        {
            results = new Surface(Theme.Background);
            page.Controls.Add(results);
            results.Height = Pixels(BuildResults(results, 0, tracks));
        }
        var lastWidth = -1;
        void PositionExplore()
        {
            if (lastWidth == page.Width) return;
            lastWidth = page.Width;
            var widthUnits = Units(page.Width);
            const int chipGap = 8;
            var chipWidth = chips.Sum(chip => chip.Width) + chipGap * (chips.Count - 1);
            var trim = Math.Max(0, (chipWidth - widthUnits + chips.Count - 1) / chips.Count);
            var chipX = 0;
            foreach (var (chip, width) in chips)
            {
                Place(chip, chipX, 64, width - trim, 34);
                chipX += width - trim + chipGap;
            }
            var heroHeight = Math.Clamp(widthUnits * 216 / 940, 184, 216);
            var heroTextY = (heroHeight - 216) / 2;
            Place(hero, 0, 123, widthUnits, heroHeight);
            Place(listen, 40, heroTextY + 144, 94, 40);
            if (add is not null) Place(add, 146, heroTextY + 144, 39, 40);
            var heroBottom = 123 + heroHeight;
            Place(genreHeading, 0, heroBottom + 26, widthUnits, 41);
            var tileWidth = Math.Max(1, (widthUnits - 42) / 4);
            var tileHeight = Math.Clamp(widthUnits * 95 / 940, 76, 95);
            for (var i = 0; i < tiles.Count; i++)
                Place(tiles[i], i % 4 * (tileWidth + 14), heroBottom + 76 + i / 4 * (tileHeight + 13),
                    tileWidth, tileHeight);
            var bottom = tiles[^1].Bottom;
            if (results is not null) Place(results, 0, Units(bottom) + 27, widthUnits, Units(results.Height));
            page.Height = results is null ? bottom + Pixels(12) : results.Bottom;
        }
        page.SizeChanged += (_, _) => PositionExplore();
        PositionExplore();
    }

    private void BuildSearch(Surface page)
    {
        Span(page, Theme.Label("Busca", 36, bold: true), 0, 0, 53);
        Span(page, Theme.Label($"Resultados para “{_route.Value}”", 16, Theme.Muted), 1, 55, 33);
        page.Height = Pixels(BuildResults(page, 102, _results));
    }

    private int BuildResults(Surface page, int y, IReadOnlyList<Track> tracks)
    {
        if (_busy || _loadError is not null || tracks.Count == 0)
        {
            var text = _busy ? "Buscando no SoundCloud…" : _loadError ?? "Nenhuma faixa encontrada.";
            Span(page, Theme.Label(text, 15, Theme.Muted), 0, y, 40);
            if (_loadError is not null)
            {
                var retry = ButtonAt(page, "Tentar novamente", "", () => OpenRoute(_route));
                StylePrimary(retry);
                Place(retry, 0, y + 47, 156, 37);
            }
            return y + 99;
        }
        return AddTrackRows(page, y, tracks);
    }

    private void BuildCollection(Surface page, string kind, string title, List<Track> tracks, PlaylistInfo? playlist = null)
    {
        var art = new Surface(Theme.Background);
        page.Controls.Add(art);
        Place(art, 0, 0, 188, 188);
        PaintDesign(art, (g, area) => _artwork.Draw(g, area, tracks.FirstOrDefault()?.Artwork,
            _route.Page == Page.Favorites ? Theme.Pink : Theme.Accent, 15, _route.Page == Page.Favorites));
        Span(page, Theme.Label(kind, 13, Theme.Muted), 215, 9, 24);
        Span(page, Theme.Label(title, 49, bold: true), 213, 34, 63);
        var minutes = tracks.Sum(track => Math.Max(0, track.DurationMs)) / 60_000;
        var count = tracks.Count == 1 ? "1 música" : $"{tracks.Count} músicas";
        Span(page, Theme.Label($"{count} · {minutes} min", 15, Theme.Muted), 216, 99, 25);
        var play = ButtonAt(page, "", "play", () =>
        {
            if (_current is not null && (playlist is not null ? _activePlaylistId == playlist.Id :
                _activePlaylistId is null && _queue.SequenceEqual(tracks, TrackIdComparer.Instance)) &&
                _playState is Playback.PlaybackState.Playing or Playback.PlaybackState.Paused)
                TogglePlayback();
            else StartQueue(tracks, _shuffle && tracks.Count > 0 ? Random.Shared.Next(tracks.Count) : 0, playlist?.Id);
        }, "Tocar coleção");
        Place(play, 215, 136, 48, 48);
        play.Enabled = tracks.Count > 0;
        StylePrimary(play);
        play.DrawContent = (g, r) =>
        {
            var active = _current is not null && _playState == Playback.PlaybackState.Playing &&
                (playlist is not null ? _activePlaylistId == playlist.Id :
                    _activePlaylistId is null && _queue.SequenceEqual(tracks, TrackIdComparer.Instance));
            Icons.Draw(g, active ? "pause" : "play", new Rectangle(14, 14, 20, 20), Theme.Background);
        };
        var shuffle = ButtonAt(page, "", "shuffle", () =>
        {
            _shuffle = !_shuffle;
            Notify(_shuffle ? "Modo aleatório ligado" : "Modo aleatório desligado");
            Render();
            UpdatePlayer();
        }, "Modo aleatório");
        shuffle.ForeColor = _shuffle ? Theme.Accent : Theme.Muted;
        Place(shuffle, 278, 140, 38, 40);
        if (playlist is not null || _route.Page == Page.History && tracks.Count > 0)
        {
            var options = ButtonAt(page, "", "more", () => { }, "Opções da coleção");
            Place(options, 328, 140, 39, 40);
            options.Click += (_, _) =>
            {
                var menu = CreateMenu();
                if (playlist is not null)
                {
                    var enabled = PlaylistContinuationEnabled(playlist.Id);
                    MenuItem(menu.Items, enabled ? "Faixas parecidas · Ligado" : "Faixas parecidas · Desligado",
                        () => TogglePlaylistContinuation(playlist.Id), "repeat");
                    menu.Items.Add(new ToolStripSeparator());
                    MenuItem(menu.Items, "Excluir playlist…", () => DeletePlaylist(playlist), "close", destructive: true);
                }
                else MenuItem(menu.Items, "Limpar histórico…", ClearHistory, "history", destructive: true);
                menu.Show(options, new Point(0, options.Height));
            };
        }
        if (tracks.Count > 0) page.Height = Pixels(AddTrackRows(page, 212, tracks, playlist?.Id));
        else
        {
            var empty = _route.Page switch
            {
                Page.Favorites => "As músicas que você curtir aparecem aqui.",
                Page.History => "As faixas que você ouvir aparecem aqui.",
                _ => "Adicione músicas pelo menu de uma faixa."
            };
            Span(page, Theme.Label(empty, 15, Theme.Muted), 0, 219, 51);
            page.Height = Pixels(280);
        }
    }

    private int AddTrackRows(Surface page, int y, IReadOnlyList<Track> tracks, long? playlistId = null)
    {
        var artistPage = _route.Page == Page.Artist;
        var columns = new Surface(Theme.Background);
        Span(page, columns, 0, y, 34);
        PaintDesign(columns, (g, area) =>
        {
            var w = area.Width;
            var font = Theme.Medium(11);
            Theme.Write(g, "#", new Rectangle(13, 0, 26, 31), font, Theme.Muted);
            Theme.Write(g, "TÍTULO", new Rectangle(61, 0, 110, 31), font, Theme.Muted);
            var detailHeading = _route.Page switch
            {
                Page.Search => "GÊNERO",
                Page.Artist => "REPRODUÇÕES",
                _ => "ÁLBUM"
            };
            Theme.Write(g, detailHeading, new Rectangle(w * 54 / 100, 0, w * 46 / 100 - 103, 31),
                font, Theme.Muted);
            Theme.Write(g, "TEMPO", new Rectangle(w - 96, 0, 57, 31), font, Theme.Muted, TextFormatFlags.Right);
            using var pen = new Pen(Theme.Border);
            g.DrawLine(pen, 0, 33, w, 33);
        });
        for (var i = 0; i < tracks.Count; i++)
        {
            var index = i;
            var track = tracks[index];
            var linkArtist = !artistPage && CanInspectArtist(track);
            var row = new Surface(Theme.Background);
            Color RowColor() => _current?.Id == track.Id ? Theme.Surface :
                row.ClientRectangle.Contains(row.PointToClient(Cursor.Position)) ? Theme.Hover : row.BackColor;
            void RepaintRow(object? sender, EventArgs args) => row.Invalidate(true);

            Span(page, row, 0, y + 38 + i * 53, 51);
            var button = ButtonAt(row, track.Title, "", () => PlayRow(tracks, index, playlistId));
            button.AccessibleDescription = track.ArtistOrUnknown;
            button.HoverColor = Color.Transparent;
            button.MouseEnter += RepaintRow;
            button.MouseLeave += RepaintRow;
            Span(row, button, 0, 0, 51);
            button.DrawContent = (g, r) =>
            {
                var playing = _current?.Id == track.Id;
                Theme.Fill(g, new Rectangle(0, 0, r.Width - 1, r.Height - 1), RowColor(), 10);
                if (playing)
                {
                    Icons.Draw(g, "logo", new Rectangle(11, 16, 17, 17), Theme.Accent);
                }
                else
                    Theme.Write(g, (index + 1).ToString(), new Rectangle(13, 0, 25, 51), Theme.Font(14), Theme.Muted);
                if (_route.Page == Page.Search)
                    _artwork.Draw(g, new Rectangle(32, 12, 25, 25), track.Artwork, Palette(index), 4);
                var genreX = r.Width * 54 / 100;
                Theme.Write(g, track.Title, new Rectangle(62, 6, genreX - 72, 21),
                    Theme.Medium(14), playing ? Theme.Accent : Theme.Text);
                if (!linkArtist)
                    Theme.Write(g, track.ArtistOrUnknown, new Rectangle(62, 27, genreX - 72, 17), Theme.Font(13), Theme.Muted);
                var detail = _route.Page switch
                {
                    Page.Search => track.Genre,
                    Page.Artist => track.PlaybackCount.ToString("N0"),
                    _ => track.Album
                };
                Theme.Write(g, string.IsNullOrWhiteSpace(detail) ? "—" : detail,
                    new Rectangle(genreX, 0, r.Width - genreX - 112, 51), Theme.Font(13), Theme.Muted);
                Theme.Write(g, track.DurationText, new Rectangle(r.Width - 96, 0, 57, 51), Theme.Font(13),
                    Theme.Muted, TextFormatFlags.Right);
            };
            button.MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Right) ShowTrackMenu(button, e.Location, track, playlistId);
            };
            if (linkArtist)
            {
                var artist = ButtonAt(row, track.ArtistOrUnknown, "", () => OpenArtist(track),
                    "Ver artista: " + track.ArtistOrUnknown);
                artist.HoverColor = Color.Transparent;
                artist.PaintBackground = RowColor;
                artist.DrawContent = (g, r) =>
                    Theme.Write(g, track.ArtistOrUnknown, r, Theme.Font(13), Theme.Accent);
                artist.MouseEnter += RepaintRow;
                artist.MouseLeave += RepaintRow;
                artist.MouseUp += (_, e) =>
                {
                    if (e.Button == MouseButtons.Right) ShowTrackMenu(artist, e.Location, track, playlistId);
                };
                void AlignArtist() => Place(artist, 62, 27, Math.Max(1, Units(row.Width) * 54 / 100 - 72), 17);
                row.SizeChanged += (_, _) => AlignArtist();
                AlignArtist();
                artist.BringToFront();
            }
            var more = ButtonAt(row, "", "more", () => { }, "Opções de " + track.Title);
            more.ForeColor = Theme.Muted;
            more.PaintBackground = RowColor;
            more.HoverColor = Theme.Border;
            void AlignMenu() => Place(more, Units(row.Width) - 36, 8, 32, 34);
            row.SizeChanged += (_, _) => AlignMenu();
            AlignMenu();
            more.BringToFront();
            more.MouseEnter += RepaintRow;
            more.MouseLeave += RepaintRow;
            more.Click += (_, _) => ShowTrackMenu(more, new Point(0, more.Height), track, playlistId);
        }
        return y + 43 + tracks.Count * 53;
    }

    private static void StylePrimary(SoftButton button)
    {
        button.FillColor = Theme.Text;
        button.HoverColor = Color.White;
        button.ForeColor = Theme.Background;
        button.Radius = 25;
    }

    private sealed class TrackIdComparer : IEqualityComparer<Track>
    {
        public static readonly TrackIdComparer Instance = new();
        public bool Equals(Track? x, Track? y) => x?.Id == y?.Id;
        public int GetHashCode(Track obj) => obj.Id.GetHashCode(StringComparison.Ordinal);
    }
}
