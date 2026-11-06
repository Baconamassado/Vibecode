using System.Globalization;
using System.Text;
using System.Text.Json;
using Bakobtify.Data;
using Bakobtify.Playback;
using Bakobtify.SoundCloud;

namespace Bakobtify.UI;

internal sealed partial class MainForm
{
    private const int CandidateLimit = 160;
    private const string CandidateSetting = "desktop_recommendation_candidates";
    private const string PatternSetting = "desktop_audio_patterns_v1";
    private const string RelatedSetting = "desktop_related_tracks_v1";
    private const int PatternLimit = 256;
    private static readonly HashSet<string> BroadStyles =
    [
        "music", "musica", "song", "track", "original", "new music", "various", "other",
        "brasil", "brazil", "brasileira", "brazilian", "musica brasileira", "brazilian music",
        "pop", "rock", "alternative", "alternativo", "electronic", "eletronica", "dance",
        "hip hop", "hip hop rap", "rap", "funk", "latin", "latino",
        "mix", "remix", "viral", "trending", "soundcloud"
    ];
    private Dictionary<string, AudioPattern> _audioPatterns = new();
    private List<Track> _recommendationCandidates = [];
    private List<Track> _collectionCandidates = [];
    private List<Track> _recommendationCatalog = [];
    private bool _loadingRecommendationCatalog;
    private Dictionary<string, List<Track>> _relatedRecommendations = new();
    private CancellationTokenSource? _relatedRequest;
    private int _relatedBackfillBudget = 4;
    private string? _lastRecommendationReport;
    private bool HasRecommendationProfile => _history.Count > 0 || _favorites.Count > 0;

    private async Task LoadRelatedRecommendationsAsync(Track track)
    {
        var references = new[] { track }.Concat(CanContinuePlaylist ? _playlistSeeds : _history.Take(8))
            .DistinctBy(item => item.Id).Where(item =>
                long.TryParse(item.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
                id > 0 && !_relatedRecommendations.ContainsKey(item.Id))
            .Where(item => item.Id == track.Id || _relatedBackfillBudget > 0).Take(3).ToArray();
        if (references.Length == 0) return;
        _relatedRequest?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _relatedRequest = request;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), request.Token);
            if (_playState != Playback.PlaybackState.Playing || _current?.Id != track.Id) return;
            foreach (var reference in references)
            {
                if (reference.Id != track.Id && _relatedBackfillBudget-- <= 0)
                {
                    _relatedBackfillBudget = 0;
                    break;
                }
                var started = Environment.TickCount64;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                var related = await _client.GetRelatedTracksAsync(reference.Id, timeout.Token);
                if (request.IsCancellationRequested) return;
                _relatedRecommendations[reference.Id] = CandidateTracks(related);
                _relatedRecommendations = _relatedRecommendations.TakeLast(12).ToDictionary();
                RunAction(() =>
                {
                    var data = JsonSerializer.Serialize(_relatedRecommendations);
                    if (data.Length <= 1_000_000) _db.SetMeta(RelatedSetting, data, double.MaxValue);
                });
                LogDiagnostic("Algoritmo", $"SoundCloud retornou {_relatedRecommendations[reference.Id].Count} faixas relacionadas à referência.", started);
                UpdatePlayer();
                if (_route.Page == Page.Home) Render();
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) when (ex is SoundCloudException or HttpRequestException or OperationCanceledException)
        {
            LogDiagnostic("Algoritmo", "Faixas relacionadas indisponíveis. Comparação local mantida.");
        }
        finally
        {
            if (_relatedRequest == request)
            {
                _relatedRequest = null;
                if (!_closing)
                {
                    UpdatePlayer();
                    if (_route.Page == Page.Home) Render();
                }
            }
        }
    }

    private async Task LoadRecommendationCatalogAsync()
    {
        if (_loadingRecommendationCatalog || _recommendationCatalog.Count > 0) return;
        _loadingRecommendationCatalog = true;
        var started = Environment.TickCount64;
        try
        {
            _recommendationCatalog = await _client.SearchAsync("new music", 40, _lifetime.Token);
            LogDiagnostic("Algoritmo", $"Catálogo complementar retornou {_recommendationCatalog.Count} faixas.", started);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is SoundCloudException or HttpRequestException or OperationCanceledException)
        {
            LogDiagnostic("Algoritmo", "Catálogo complementar indisponível.", started);
        }
        finally
        {
            _loadingRecommendationCatalog = false;
            if (!_closing && _route.Page == Page.Home) Render();
        }
    }

    private void LoadRecommendationCandidates()
    {
        if (_db.GetMeta(RelatedSetting)?.Value is { Length: <= 1_000_000 } related)
        {
            try
            {
                var savedRelated = JsonSerializer.Deserialize<Dictionary<string, List<Track>>>(related);
                if (savedRelated is not null)
                    _relatedRecommendations = savedRelated.TakeLast(12)
                        .ToDictionary(entry => entry.Key, entry => CandidateTracks(entry.Value ?? []).Take(20).ToList());
            }
            catch (JsonException) { }
        }
        if (_db.GetMeta(PatternSetting)?.Value is { Length: <= 128_000 } patterns)
        {
            try { _audioPatterns = JsonSerializer.Deserialize<Dictionary<string, AudioPattern>>(patterns) ?? new(); }
            catch (JsonException) { }
        }
        if (_db.GetMeta(CandidateSetting)?.Value is not { Length: <= 512_000 } saved) return;
        try
        {
            _recommendationCandidates = CandidateTracks(JsonSerializer.Deserialize<List<Track>>(saved) ?? []);
        }
        catch (JsonException) { }
    }

    private void RememberAudioPattern(string trackId, AudioPattern pattern) => OnUi(() =>
    {
        if (_audioPatterns.GetValueOrDefault(trackId) == pattern) return;
        _audioPatterns[trackId] = pattern;
        if (_audioPatterns.Count > PatternLimit)
            _audioPatterns = _audioPatterns.TakeLast(PatternLimit).ToDictionary();
        RunAction(() => _db.SetMeta(PatternSetting, JsonSerializer.Serialize(_audioPatterns), double.MaxValue));
        LogDiagnostic("Algoritmo", "Áudio analisado localmente (energia, dinâmica, brilho e pulsação).");
        if (_route.Page == Page.Home) Render();
    });

    private void RememberRecommendationCandidates(IReadOnlyList<Track> tracks)
    {
        _recommendationCandidates = CandidateTracks(tracks.Concat(_recommendationCandidates));
        try
        {
            var data = JsonSerializer.Serialize(_recommendationCandidates);
            if (data.Length <= 512_000) _db.SetMeta(CandidateSetting, data, double.MaxValue);
        }
        catch (Exception)
        {
            LogDiagnostic("Algoritmo", "Não foi possível salvar os candidatos locais.");
        }
    }

    private static List<Track> CandidateTracks(IEnumerable<Track> tracks) => tracks
        .Where(track => track is { Streamable: true, Id.Length: > 0 }).DistinctBy(track => track.Id)
        .Take(CandidateLimit).ToList();

    private IReadOnlyList<Track> RecommendTracks() => RankRecommendations();

    private IReadOnlyList<Track> RankRecommendations(bool playlist = false)
    {
        var started = Environment.TickCount64;
        var heard = _history.Select(track => track.Id).ToHashSet();
        var liked = _favorites.Select(track => track.Id).ToHashSet();
        var collected = _collectionCandidates.Select(track => track.Id).ToHashSet();
        var skipped = playlist ? _playlistFeedback.Where(entry => !entry.Accepted).Select(entry => entry.Track.Id).ToHashSet() : [];
        var playlistSeeds = _playlistSeeds.Where(track => !skipped.Contains(track.Id)).Take(18).ToArray();
        var seeds = (playlist
            ? _playlistFeedback.Where(entry => entry.Accepted).Take(6).Select(entry => entry.Track)
                .Concat(playlistSeeds.Length == 0 ? _playlistSeeds.Take(18) : playlistSeeds)
            : _history.Take(60).Concat(_favorites.Take(6))).DistinctBy(track => track.Id).Take(24).ToArray();
        var relatedBySeed = seeds.Where(track => _relatedRecommendations.ContainsKey(track.Id)).ToDictionary(
            track => track.Id, track => _relatedRecommendations[track.Id]);
        var related = relatedBySeed.Values.SelectMany(tracks => tracks).DistinctBy(track => track.Id).ToArray();
        var relatedRank = relatedBySeed.ToDictionary(entry => entry.Key,
            entry => entry.Value.Select((track, index) => (track.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index));
        var catalog = playlist || HasRecommendationProfile
            ? related.Concat(_recommendationCandidates).Concat(_collectionCandidates).Concat(_discovery).Concat(_recommendationCatalog)
            : _discovery;
        var candidates = catalog.Where(track => track.Streamable && track.Id.Length > 0)
            .GroupBy(track => track.Id).Select(group => group.OrderByDescending(track => track.Tags?.Length ?? 0).First())
            .ToArray();
        var metadata = candidates.ToDictionary(track => track.Id);
        var references = seeds.Select((track, index) =>
        {
            var latest = metadata.GetValueOrDefault(track.Id);
            var weight = playlist ? (_playlistSeeds.Any(seed => seed.Id == track.Id) ? 1d : 1.25) :
                (liked.Contains(track.Id) ? 1.2 : 1) / (1 + index * .07);
            return (Track: track, Genre: GenreKey(latest?.Genre ?? track.Genre),
                Tags: StyleTags(latest?.Tags ?? track.Tags).ToHashSet(), Artists: ArtistKeys(track),
                Pattern: _audioPatterns.GetValueOrDefault(track.Id), Weight: weight);
        }).ToArray();
        var excluded = playlist
            ? _queue.Select(track => track.Id).Concat(_playlistFeedback.Select(entry => entry.Track.Id)).ToHashSet()
            : heard.Concat(liked).ToHashSet();
        var rejected = playlist ? _playlistFeedback.Where(entry => !entry.Accepted).Select(entry =>
            (Genre: GenreKey(entry.Track.Genre), Tags: StyleTags(entry.Track.Tags), Artists: ArtistKeys(entry.Track))).ToArray() : [];
        var available = candidates.Where(track => !excluded.Contains(track.Id)).ToArray();
        var ranked = available.Select(track =>
        {
            var artists = ArtistKeys(track);
            var genre = GenreKey(track.Genre);
            var tags = StyleTags(track.Tags);
            var pattern = _audioPatterns.GetValueOrDefault(track.Id);
            var best = 0d;
            var relatedMatches = 0;
            var reason = "descoberta";
            foreach (var reference in references)
            {
                var shared = tags.Count(reference.Tags.Contains);
                var sameGenre = genre.Length > 0 && genre == reference.Genre;
                var specificGenre = sameGenre && !BroadStyles.Contains(genre);
                var sameArtist = artists.Intersect(reference.Artists).Any();
                var audio = pattern is not null && reference.Pattern is not null
                    ? Math.Max(0, pattern.Similarity(reference.Pattern) - .72) : 0;
                var direct = relatedRank.TryGetValue(reference.Track.Id, out var ranks) && ranks.TryGetValue(track.Id, out var position)
                    ? 2.5 / (1 + position * .07) : 0;
                var conflictingGenre = genre.Length > 0 && reference.Genre.Length > 0 &&
                    !sameGenre && !BroadStyles.Contains(genre) && !BroadStyles.Contains(reference.Genre);
                if (conflictingGenre && shared == 0 && !sameArtist) direct = 0;
                if (direct > 0) relatedMatches++;
                if (direct == 0 && !specificGenre && shared == 0 && !(sameArtist && !conflictingGenre))
                    continue;

                var tagScore = shared == 0 ? 0 : 2.5 * shared / Math.Sqrt(tags.Length * reference.Tags.Count);
                var score = (direct + (specificGenre ? 2.6 : sameGenre ? .35 : 0) + tagScore +
                    (sameArtist ? 1.1 : 0) + audio * 3) * reference.Weight;
                if (reference.Track.DurationMs > 0 && track.DurationMs > 0 &&
                    Math.Max(reference.Track.DurationMs, track.DurationMs) / (double)Math.Min(reference.Track.DurationMs, track.DurationMs) > 4)
                    score *= .7;
                if (score <= best) continue;
                best = score;
                var signals = new List<string>(5);
                if (direct > 0) signals.Add("relacionada a uma faixa de referência");
                if (specificGenre) signals.Add("gênero");
                if (shared > 0) signals.Add("tags específicas");
                if (sameArtist) signals.Add("artista");
                if (audio > 0) signals.Add("áudio");
                reason = string.Join(" + ", signals);
            }
            var penalty = rejected.Select(item =>
                (genre.Length > 0 && genre == item.Genre && !BroadStyles.Contains(genre) ? .45 : 0) +
                (artists.Intersect(item.Artists).Any() ? .8 : 0) +
                (tags.Intersect(item.Tags).Any() ? .75 : 0)).DefaultIfEmpty().Max();
            var totalScore = best + Math.Min(3, Math.Max(0, relatedMatches - 1)) * .55 - penalty;
            return (Track: track, Artist: artists.FirstOrDefault(), Score: totalScore,
                Order: totalScore + (totalScore > 0 && collected.Contains(track.Id) ? .15 : 0), Reason: reason);
        }).Where(item => !(playlist || HasRecommendationProfile) || item.Score > 0)
            .OrderByDescending(item => item.Order).ToArray();

        if (playlist) return ranked.Take(1).Select(item => item.Track).ToArray();
        var selected = new List<Track>(5);
        var reasons = new List<string>(5);
        var artistCounts = new Dictionary<string, int>();
        var deferred = new List<(Track Track, string Reason)>();
        foreach (var item in ranked)
        {
            if (selected.Count == 5) break;
            if (HasRecommendationProfile && item.Artist is { } key)
            {
                var count = artistCounts.GetValueOrDefault(key);
                if (count >= 2)
                {
                    deferred.Add((item.Track, item.Reason));
                    continue;
                }
                artistCounts[key] = count + 1;
            }
            selected.Add(item.Track);
            reasons.Add(item.Reason);
        }
        foreach (var (track, reason) in deferred.Take(5 - selected.Count))
        {
            selected.Add(track);
            reasons.Add(reason);
        }

        var report = $"{_history.Count} reproduções, {liked.Count} curtidas, {candidates.Length} candidatas, " +
            $"{related.Length} relacionadas de {relatedBySeed.Count} referências, {references.Count(reference => reference.Pattern is not null)} referências com áudio analisado, " +
            $"{candidates.Length - available.Length} já ouvidas ou curtidas, {available.Length - ranked.Length} sem afinidade, {selected.Count} exibidas" +
            (reasons.Count == 0 ? "." : $" ({string.Join(", ", reasons)}).");
        var signature = report + string.Join("|", selected.Select(track => track.Id));
        if (_lastRecommendationReport != signature)
        {
            _lastRecommendationReport = signature;
            LogDiagnostic("Algoritmo", report, started);
        }
        return selected;
    }

    private static string[] StyleTags(string[]? tags) => (tags ?? []).Take(24)
        .Select(GenreKey).Where(tag => tag.Length > 0 && !BroadStyles.Contains(tag) &&
            !tag.All(char.IsDigit)).Distinct().ToArray();

    private static string GenreKey(string? text) => NormalizeSignal(text) switch
    {
        "baile funk" or "funk brasileiro" or "funk carioca" or "brazilian funk" => "funk brasileiro",
        "musica popular brasileira" => "mpb",
        var value => value
    };

    private static string[] ArtistKeys(Track track) =>
        (string.IsNullOrWhiteSpace(track.CreditedArtists) ? new[] { track.Artist } : track.CreditedArtists.Split(','))
        .Select(NormalizeSignal).Where(key => key.Length > 0).Distinct().ToArray();

    private static string NormalizeSignal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var characters = value.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        return string.Join(" ", new string(characters.ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
