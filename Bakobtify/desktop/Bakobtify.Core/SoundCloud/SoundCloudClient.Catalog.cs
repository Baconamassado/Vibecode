using System.Globalization;
using System.Text;
using System.Text.Json;
using Bakobtify.Data;

namespace Bakobtify.SoundCloud;

public sealed partial class SoundCloudClient
{
    private const string AudiusTracks = "https://api.audius.co/v1/tracks";

    private static bool IsExternalTrack(string id) =>
        id.StartsWith("audius:", StringComparison.Ordinal) || id.StartsWith("jamendo:", StringComparison.Ordinal);

    public async Task<(List<Track> Tracks, string[] Unavailable)> SearchCatalogAsync(
        string query, int limit = 40, CancellationToken ct = default, bool includeAudius = false)
    {
        query = string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (query.Length == 0) return ([], []);
        if (query.Length > 200) query = query[..(char.IsHighSurrogate(query[199]) ? 199 : 200)];
        limit = Math.Clamp(limit, 1, 50);
        var count = limit.ToString(CultureInfo.InvariantCulture);
        var soundCloud = SearchProviderAsync("SoundCloud", token => SearchAsync(query, limit, token), ct);
        var results = includeAudius
            ? await Task.WhenAll(soundCloud, SearchProviderAsync("Audius", token => SearchAudiusAsync(
                AddQuery(AddQuery(AudiusTracks + "/search", "query", query), "limit", count), token), ct))
                .ConfigureAwait(false)
            : new[] { await soundCloud.ConfigureAwait(false) };
        var unavailable = results.Where(result => result.Tracks is null).Select(result => result.Name).ToArray();
        var normalized = SearchText(query);
        var terms = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tracks = results.Where(result => result.Tracks is not null)
            .SelectMany(result => result.Tracks!.Select((track, index) => (track, index)))
            .DistinctBy(item => item.track.Id)
            .OrderByDescending(item => Relevance(item.track, normalized, terms))
            .ThenBy(item => item.index)
            .Take(limit)
            .Select(item => item.track)
            .ToList();
        return (tracks, unavailable);
    }

    private static async Task<(string Name, List<Track>? Tracks)> SearchProviderAsync(
        string name, Func<CancellationToken, Task<List<Track>>> search, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { return (name, await search(deadline.Token).ConfigureAwait(false)); }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            ex is SoundCloudException or HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            return (name, null);
        }
    }

    private static string SearchText(string value) => string.Join(' ', new string(value
        .Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
        .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray())
        .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static int Relevance(Track track, string query, string[] terms)
    {
        if (terms.Length == 0) return 0;
        var title = SearchText(track.Title);
        var artist = SearchText(track.Artist ?? "");
        if (title == query) return 5;
        if (title + " " + artist == query || artist + " " + title == query) return 5;
        if (artist == query) return 4;
        var words = (title + " " + artist).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.All(term => words.Contains(term, StringComparer.Ordinal))) return 3;
        if (title.Contains(query, StringComparison.Ordinal) || artist.Contains(query, StringComparison.Ordinal)) return 2;
        return terms.Any(term => words.Contains(term, StringComparer.Ordinal)) ? 1 : 0;
    }

    private async Task<List<Track>> SearchAudiusAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, ct).ConfigureAwait(false);
        using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new SoundCloudException("O provedor retornou uma resposta de catálogo inválida.");

        var tracks = new List<Track>();
        foreach (var row in rows.EnumerateArray().Take(50))
        {
            var id = Str(row, "id");
            var title = Str(row, "title");
            if (!ValidAudiusId(id) || string.IsNullOrWhiteSpace(title) || !AudiusPlayable(row)) continue;
            var user = row.TryGetProperty("user", out var artist) ? artist : default;
            var art = row.TryGetProperty("artwork", out var artwork) ? artwork : default;
            var permalink = Str(row, "permalink");
            if (permalink?.StartsWith('/') == true) permalink = "https://audius.co" + permalink;
            tracks.Add(new Track
            {
                Id = "audius:" + id,
                Title = title,
                Artist = Str(user, "name"),
                DurationMs = Math.Clamp(Long(row, "duration"), 0, int.MaxValue) * 1000,
                Artwork = CatalogUri(Str(art, "150x150"), "audius"),
                PermalinkUrl = CatalogUri(permalink, "audius"),
                Genre = Str(row, "genre"),
                PlaybackCount = Long(row, "play_count"),
            });
        }
        return tracks;
    }

    private static bool AudiusPlayable(JsonElement row) =>
        Bool(row, "is_streamable", false) && !Bool(row, "is_stream_gated", false);

    private static bool ValidAudiusId(string? id) =>
        id is { Length: > 0 and <= 32 } && id.All(char.IsAsciiLetterOrDigit);

    private static string? CatalogUri(string? value, string provider) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && SoundCloudTransport.Provider(uri) == provider
            ? uri.AbsoluteUri : null;

    private static StreamInfo ResolveCatalogStream(string trackId)
    {
        if (trackId.StartsWith("jamendo:", StringComparison.Ordinal))
            throw new PlaybackUnavailableException("O catálogo do Jamendo foi removido.");

        var id = trackId["audius:".Length..];
        if (!ValidAudiusId(id))
            throw new PlaybackUnavailableException("Áudio indisponível no Audius.");
        return new StreamInfo("progressive", AudiusTracks + "/" + id + "/stream", "audio/mpeg", null, 0);
    }
}
