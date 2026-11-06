using System.Globalization;
using System.Text.Json;
using Bakobtify.Data;

namespace Bakobtify.SoundCloud;

public sealed record ArtistProfile(long Id, string Name, long? Followers, long? TrackCount);
public sealed record ArtistRelease(string Title, long TrackCount);
public sealed record ArtistTracks(IReadOnlyList<Track> Tracks, string? NextPage);

public sealed partial class SoundCloudClient
{
    public async Task<ArtistProfile> GetArtistAsync(Track track, CancellationToken ct = default)
    {
        var id = track.ArtistId;
        if (id <= 0)
            id = (await GetTrackAsync(track.Id, ct).ConfigureAwait(false)).ArtistId;
        if (id <= 0) throw new NotFoundException("Perfil do artista indisponível.");

        using var doc = await CallApiAsync(ArtistPath(id), null, ct).ConfigureAwait(false);
        var profile = doc.RootElement;
        return new ArtistProfile(id, Str(profile, "username") ?? track.ArtistOrUnknown,
            ReadCount(profile, "followers_count"), ReadCount(profile, "track_count"));
    }

    public async Task<ArtistTracks> GetArtistTracksAsync(long id, string? nextPage, CancellationToken ct = default)
    {
        var path = ArtistPath(id) + "/tracks";
        using var doc = nextPage is null
            ? await CallApiAsync(path, ArtistPageQuery(), ct).ConfigureAwait(false)
            : await CallUrlAsync(ArtistPageUrl(nextPage, path) ??
                throw new SoundCloudException("Página de faixas inválida."), ct).ConfigureAwait(false);
        var tracks = ArtistCollection(doc.RootElement).Where(item => Long(item, "id") > 0)
            .Select(Normalize).ToArray();
        return new ArtistTracks(tracks, ArtistPageUrl(Str(doc.RootElement, "next_href"), path));
    }

    public async Task<IReadOnlyList<ArtistRelease>> GetArtistReleasesAsync(long id, CancellationToken ct = default)
    {
        var path = ArtistPath(id) + "/playlists";
        using var doc = await CallApiAsync(path, ArtistPageQuery(), ct).ConfigureAwait(false);
        return ArtistCollection(doc.RootElement)
            .Where(item => Bool(item, "is_album", false) || Str(item, "set_type") is "album" or "ep" or "single")
            .Where(item => !string.IsNullOrWhiteSpace(Str(item, "title")))
            .Select(item => new ArtistRelease(Str(item, "title")!, Long(item, "track_count")))
            .ToArray();
    }

    private static string ArtistPath(long id)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        return "/users/" + id.ToString(CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, string> ArtistPageQuery() => new()
    {
        ["limit"] = "50",
        ["linked_partitioning"] = "true",
    };

    private static IEnumerable<JsonElement> ArtistCollection(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("collection", out var collection))
            root = collection;
        if (root.ValueKind != JsonValueKind.Array)
            throw new SoundCloudException("Catálogo do artista em formato inesperado.");
        return root.EnumerateArray();
    }

    private static string? ArtistPageUrl(string? value, string path)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !SoundCloudTransport.IsTrustedUri(uri) ||
            uri.Host != "api-v2.soundcloud.com" || uri.AbsolutePath != path)
            return null;

        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !Uri.UnescapeDataString(part.Split('=', 2)[0])
                .Equals("client_id", StringComparison.OrdinalIgnoreCase));
        return new UriBuilder(uri) { Query = string.Join("&", query), Fragment = "" }.Uri.AbsoluteUri;
    }

    private static long? ReadCount(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0 ? count : null;
}
