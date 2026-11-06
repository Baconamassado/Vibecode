using Bakobtify.Data;
using Bakobtify.Playback;
using Bakobtify.SoundCloud;

namespace Bakobtify.UI;

internal sealed partial class MainForm
{
    private ArtistProfile? _artistProfile;
    private readonly List<Track> _artistTracks = [];
    private IReadOnlyList<ArtistRelease>? _artistReleases;
    private string? _artistNextPage;
    private bool _showArtistReleases;

    private static bool CanInspectArtist(Track track) =>
        track.ArtistId > 0 || track.Id.Length > 0 && track.Id.All(char.IsAsciiDigit);

    private bool CanInspectNowArtist => _current is { } track && CanInspectArtist(track) &&
        _playState is not (PlaybackState.Loading or PlaybackState.Error) &&
        _embeddedPlayer is null && _parkedEmbedded is null;

    private void OpenArtist(Track track)
    {
        if (CanInspectArtist(track))
            Navigate(new Route(Page.Artist, ArtistTrack: track));
    }

    private async Task LoadArtistAsync(Route route, bool more = false)
    {
        if (route.ArtistTrack is not { } track || _request is not null) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _request = request;
        var ct = request.Token;
        _busy = true;
        _loadError = null;
        Render();
        try
        {
            var profile = _artistProfile ?? await _client.GetArtistAsync(track, ct);
            ct.ThrowIfCancellationRequested();
            _artistProfile = profile;
            var next = more ? _artistNextPage : null;
            var page = await _client.GetArtistTracksAsync(profile.Id, next, ct);
            ct.ThrowIfCancellationRequested();
            var known = _artistTracks.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            _artistTracks.AddRange(page.Tracks.Where(item => known.Add(item.Id)));
            _artistNextPage = page.NextPage == next ? null : page.NextPage;

            if (!more)
            {
                try
                {
                    var releases = await _client.GetArtistReleasesAsync(profile.Id, ct);
                    ct.ThrowIfCancellationRequested();
                    _artistReleases = releases;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested &&
                    ex is SoundCloudException or HttpRequestException or OperationCanceledException)
                {
                    // As faixas continuam disponíveis se a consulta de álbuns falhar.
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is SoundCloudException or HttpRequestException or OperationCanceledException)
        {
            if (!ct.IsCancellationRequested) _loadError = SearchError(ex);
        }
        finally
        {
            if (_request == request)
            {
                _request = null;
                _busy = false;
                if (!_closing && !ct.IsCancellationRequested) Render();
            }
        }
    }

    private void BuildArtist(Surface page)
    {
        var route = _route;
        var profile = _artistProfile;
        Span(page, Theme.Label(profile?.Name ?? route.ArtistTrack?.ArtistOrUnknown ?? "Artista",
            32, bold: true), 0, 0, 52);
        var stats = new List<string>();
        if (profile?.Followers is { } followers) stats.Add($"{followers:N0} seguidores");
        if (profile?.TrackCount is { } count) stats.Add($"{count:N0} faixas");
        Span(page, Theme.Label(stats.Count == 0 ? "Perfil no SoundCloud" : string.Join(" · ", stats),
            14, Theme.Muted), 0, 52, 32);

        AddTab("Faixas", 0, false);
        AddTab("Álbuns", 116, true);
        const int top = 146;
        if (_showArtistReleases)
        {
            var releases = _artistReleases;
            var fromTracks = releases is not { Count: > 0 };
            if (fromTracks)
                releases = _artistTracks.Where(item => !string.IsNullOrWhiteSpace(item.Album))
                    .GroupBy(item => item.Album!, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new ArtistRelease(group.Key, group.Count())).ToArray();

            if (releases is { Count: > 0 })
            {
                var y = top;
                Span(page, Theme.Label(fromTracks ? "Álbuns nas faixas carregadas" : "Lançamentos do perfil",
                    14, Theme.Muted), 0, y, 32);
                y += 35;
                foreach (var release in releases)
                {
                    var label = release.TrackCount > 0
                        ? $"{release.Title} · {release.TrackCount:N0} {(fromTracks ? "faixas carregadas" : "faixas")}"
                        : release.Title;
                    Span(page, Theme.Label(label, 15), 0, y, 35);
                    y += 38;
                }
                page.Height = Pixels(y + 20);
                return;
            }
        }
        else if (_artistTracks.Count > 0)
        {
            var bottom = AddTrackRows(page, top, _artistTracks.ToArray());
            if (_artistNextPage is not null)
            {
                var load = ButtonAt(page, _busy ? "Carregando…" : "Carregar mais faixas", "",
                    () => _ = LoadArtistAsync(route, more: true));
                load.Enabled = !_busy;
                load.FillColor = Theme.Surface;
                Place(load, 0, bottom + 8, 200, 36);
                bottom += 52;
            }
            if (_loadError is { } error)
            {
                Span(page, Theme.Label(error, 14, Theme.Muted), 0, bottom, 40);
                bottom += 45;
            }
            page.Height = Pixels(bottom + 20);
            return;
        }

        var message = _busy ? "Carregando catálogo…" : _loadError ??
            (_showArtistReleases
                ? _artistReleases is null ? "Não foi possível consultar os álbuns." : "Nenhum álbum encontrado."
                : "Nenhuma faixa pública encontrada.");
        Span(page, Theme.Label(message, 15, Theme.Muted), 0, top, 45);
        if (!_busy && (_loadError is not null || _showArtistReleases && _artistReleases is null))
        {
            var retry = ButtonAt(page, "Tentar novamente", "", () => _ = LoadArtistAsync(route));
            retry.FillColor = Theme.Surface;
            Place(retry, 0, top + 50, 160, 36);
        }
        page.Height = Pixels(top + 105);

        void AddTab(string title, int x, bool releases)
        {
            var tab = ButtonAt(page, title, "", () =>
            {
                _showArtistReleases = releases;
                Render(false);
            });
            tab.FillColor = Theme.Surface;
            tab.ForeColor = _showArtistReleases == releases ? Theme.Accent : Theme.Text;
            Place(tab, x, 93, 105, 36);
        }
    }
}
