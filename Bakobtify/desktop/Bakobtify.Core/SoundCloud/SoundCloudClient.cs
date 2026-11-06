using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Bakobtify.Data;

namespace Bakobtify.SoundCloud;

/// <summary>
/// Cliente mínimo da <c>api-v2</c>: busca, detalhes e resolução de stream.
/// Usa um único <see cref="HttpClient"/>, com o mesmo throttle da versão Python.
/// </summary>
public sealed class SoundCloudClient : IDisposable
{
    private const string Api = "https://api-v2.soundcloud.com";
    private const string Site = "https://soundcloud.com";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ClientIdProvider _ids;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(string Query, int Limit), (DateTime ExpiresAt, List<Track> Tracks)> _cache = new();
    private readonly object _cacheLock = new();

    private DateTime _lastCall = DateTime.MinValue;

    public ClientIdProvider ClientIds { get; }
    public ClientStatus Status => _ids.Status();
    public HttpClient Http => _http;

    public SoundCloudClient(Database database, HttpClient? http = null)
    {
        _http = http ?? CreateHttpClient();
        _ownsHttp = http is null;
        _ids = new ClientIdProvider(database, _http);
        ClientIds = _ids;
    }

    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };

        var client = new HttpClient(new SoundCloudTransport(handler))
        {
            Timeout = TimeSpan.FromSeconds(40),
            MaxResponseContentBufferSize = 16 * 1024 * 1024,
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppConfig.UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pt-BR,pt;q=0.9,en;q=0.8");
        client.DefaultRequestHeaders.Referrer = new Uri(Site + "/");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", Site);
        return client;
    }

    // -- busca ---------------------------------------------------------

    public async Task<List<Track>> SearchAsync(string query, int limit = 24, CancellationToken ct = default)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0) return new List<Track>();

        limit = Math.Clamp(limit, 1, 50);
        var key = (query.ToLowerInvariant(), limit);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var hit) && hit.ExpiresAt > DateTime.UtcNow)
            {
                return hit.Tracks;
            }
        }

        using var doc = await CallApiAsync(
            "/search/tracks",
            new Dictionary<string, string>
            {
                ["q"] = query,
                ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
                ["app_locale"] = "en",
                ["linked_partitioning"] = "1",
            },
            ct).ConfigureAwait(false);

        var tracks = ReadTracks(doc.RootElement, 50);

        lock (_cacheLock)
        {
            if (_cache.Count > AppConfig.SearchCacheMax) _cache.Clear();
            _cache[key] = (DateTime.UtcNow.AddSeconds(AppConfig.SearchCacheSeconds), tracks);
        }

        return tracks;
    }

    public async Task<List<Track>> GetRelatedTracksAsync(string trackId, CancellationToken ct = default)
    {
        const int limit = 20;
        using var doc = await CallApiAsync($"/tracks/{ValidateId(trackId)}/related",
            new() { ["limit"] = limit.ToString(CultureInfo.InvariantCulture), ["linked_partitioning"] = "1" },
            ct, refreshClientId: false, maxResponseBytes: 1_048_576).ConfigureAwait(false);
        return ReadTracks(doc.RootElement, limit);
    }

    private static List<Track> ReadTracks(JsonElement root, int limit)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("collection", out var collection))
            root = collection;
        var tracks = new List<Track>();
        if (root.ValueKind != JsonValueKind.Array) return tracks;
        foreach (var item in root.EnumerateArray())
        {
            var kind = Str(item, "kind");
            if ((kind is not null && kind != "track") || Long(item, "id") <= 0) continue;
            tracks.Add(Normalize(item));
            if (tracks.Count == limit) break;
        }
        return tracks;
    }

    public async Task<Track> GetTrackAsync(string trackId, CancellationToken ct = default)
    {
        using var doc = await CallApiAsync($"/tracks/{ValidateId(trackId)}", null, ct).ConfigureAwait(false);
        return NormalizeTrack(doc.RootElement);
    }

    /// <summary>Resolve uma URL pública do SoundCloud (permalink) em uma faixa.</summary>
    public async Task<Track> ResolvePermalinkAsync(string url, CancellationToken ct = default)
    {
        using var doc = await CallApiAsync(
            "/resolve",
            new Dictionary<string, string> { ["url"] = url },
            ct).ConfigureAwait(false);

        return NormalizeTrack(doc.RootElement);
    }

    // -- stream --------------------------------------------------------

    public async Task<StreamInfo> ResolveStreamAsync(string trackId, CancellationToken ct = default)
    {
        await foreach (var stream in ResolveStreamsAsync(trackId, ct).ConfigureAwait(false))
            return stream;
        return new StreamInfo("unavailable", null, null, "no_stream", 0);
    }

    public async IAsyncEnumerable<StreamInfo> ResolveStreamsAsync(string trackId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var doc = await CallApiAsync($"/tracks/{ValidateId(trackId)}", null, ct).ConfigureAwait(false);
        var raw = doc.RootElement;
        var permalink = Str(raw, "permalink_url");
        var duration = Long(raw, "duration");
        if (Str(raw, "access") == "blocked" || Str(raw, "policy") == "BLOCK")
            throw new PlaybackUnavailableException("O SoundCloud restringiu a reprodução desta faixa.", permalink)
                { ConfirmedByMetadata = true };

        var candidates = new List<(string Kind, Uri Endpoint, string Mime, int Priority, bool Preview)>();
        var previewOnly = Str(raw, "access") == "preview" || Str(raw, "policy") == "SNIP";
        var hasProtected = false;
        if (raw.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Object &&
            media.TryGetProperty("transcodings", out var transcodings) && transcodings.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in transcodings.EnumerateArray())
            {
                if (!Uri.TryCreate(Str(item, "url"), UriKind.Absolute, out var endpoint) ||
                    endpoint.Scheme != Uri.UriSchemeHttps || endpoint.Host != "api-v2.soundcloud.com") continue;
                var preview = previewOnly || Bool(item, "snipped", false) ||
                    endpoint.AbsolutePath.Contains("/preview/", StringComparison.Ordinal);
                var format = item.TryGetProperty("format", out var f) ? f : default;
                var protocol = Str(format, "protocol") ?? "";
                if (protocol.StartsWith("ctr-", StringComparison.Ordinal) ||
                    protocol.StartsWith("cbc-", StringComparison.Ordinal))
                {
                    hasProtected = true;
                    continue;
                }
                var mime = Str(format, "mime_type") ?? "";
                var preset = Str(item, "preset") ?? "";
                var aac = mime.StartsWith("audio/aac", StringComparison.OrdinalIgnoreCase) ||
                    mime.StartsWith("audio/mp4", StringComparison.OrdinalIgnoreCase) ||
                    mime.Contains("mp4a", StringComparison.OrdinalIgnoreCase) || preset.StartsWith("aac_", StringComparison.Ordinal);
                var mp3 = mime.StartsWith("audio/mpeg", StringComparison.OrdinalIgnoreCase) ||
                    preset.StartsWith("mp3_", StringComparison.Ordinal);
                var hls = protocol is "hls" or "encrypted-hls" ||
                    endpoint.AbsolutePath.Contains("/hls", StringComparison.Ordinal) ||
                    endpoint.AbsolutePath.Contains("/encrypted-hls", StringComparison.Ordinal);
                var genericPlaylist = hls && (preset.StartsWith("abr", StringComparison.OrdinalIgnoreCase) ||
                    mime.Contains("mpegurl", StringComparison.OrdinalIgnoreCase));
                if (!aac && !mp3 && !genericPlaylist) continue;
                if (!hls && protocol is not ("progressive" or "http")) continue;
                var priority = hls && aac ? (preset.Contains("96k", StringComparison.Ordinal) ? 1 : 0) :
                    !hls ? 2 : mp3 ? 3 : 4;
                candidates.Add((hls ? "hls" : "progressive", endpoint,
                    aac ? "audio/mp4" : mp3 ? "audio/mpeg" : "", priority + (preview ? 10 : 0), preview));
            }
        }

        if (candidates.Count == 0)
            throw new PlaybackUnavailableException(hasProtected
                ? "O SoundCloud disponibilizou apenas áudio protegido. Você pode vincular um arquivo de áudio da faixa."
                : "O SoundCloud não disponibilizou áudio compatível com o player nativo.", permalink)
                { ConfirmedByMetadata = true };

        var authorization = Str(raw, "track_authorization");
        SoundCloudException? lastFailure = null;
        var yielded = false;
        foreach (var (kind, endpoint, mime, _, preview) in candidates.Distinct().OrderBy(candidate => candidate.Priority))
        {
            var url = endpoint.AbsoluteUri;
            if (!string.IsNullOrEmpty(authorization) && !endpoint.Query.Contains("track_authorization=", StringComparison.Ordinal))
                url = AddQuery(url, "track_authorization", authorization);
            string? resolved;
            try
            {
                using var stream = await CallUrlAsync(url, ct, refreshClientId: false).ConfigureAwait(false);
                resolved = Str(stream.RootElement, "url");
            }
            catch (SoundCloudException ex) when (!ct.IsCancellationRequested)
            {
                lastFailure = ex;
                continue;
            }
            if (!Uri.TryCreate(resolved, UriKind.Absolute, out var cdn) || cdn.Scheme != Uri.UriSchemeHttps) continue;
            yielded = true;
            yield return new StreamInfo(kind, cdn.AbsoluteUri, mime, null, duration)
            {
                IsPreview = preview || cdn.AbsolutePath.Contains("/preview/", StringComparison.Ordinal)
            };
        }
        if (!yielded)
            throw new SoundCloudException(lastFailure is null
                ? "O SoundCloud não retornou o endereço do áudio."
                : $"O SoundCloud recusou o endereço do áudio. {lastFailure.Message}");
    }

    // -- HTTP ----------------------------------------------------------

    private async Task<JsonDocument> CallApiAsync(
        string path,
        Dictionary<string, string>? query,
        CancellationToken ct,
        bool refreshClientId = true,
        long? maxResponseBytes = null)
    {
        var builder = new StringBuilder(Api).Append(path);
        var first = true;
        foreach (var item in query ?? new Dictionary<string, string>())
        {
            builder.Append(first ? '?' : '&')
                .Append(Uri.EscapeDataString(item.Key))
                .Append('=')
                .Append(Uri.EscapeDataString(item.Value));
            first = false;
        }

        return await CallUrlAsync(builder.ToString(), ct, refreshClientId, maxResponseBytes).ConfigureAwait(false);
    }

    private async Task<JsonDocument> CallUrlAsync(string url, CancellationToken ct, bool refreshClientId = true,
        long? maxResponseBytes = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            var clientId = await _ids.GetAsync(ct).ConfigureAwait(false);
            var fullUrl = AddQuery(url, "client_id", clientId);

            HttpResponseMessage response;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var wait = AppConfig.MinRequestInterval - (DateTime.UtcNow - _lastCall);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
                _lastCall = DateTime.UtcNow;

                response = await _http.GetAsync(fullUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            using (response)
            {
                if (refreshClientId && attempt == 0 &&
                    response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    _ids.Invalidate(clientId);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new NotFoundException("Conteúdo não encontrado no SoundCloud.");
                }

                if ((int)response.StatusCode == 429)
                {
                    throw new SoundCloudException("Muitas requisições ao SoundCloud. Tente de novo em instantes.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new SoundCloudException($"SoundCloud respondeu {(int)response.StatusCode}.");
                }

                using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bodyTimeout.CancelAfter(TimeSpan.FromSeconds(40));
                var bodyToken = bodyTimeout.Token;
                await response.Content.LoadIntoBufferAsync(maxResponseBytes ?? 8 * 1024 * 1024, bodyToken)
                    .ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(bodyToken).ConfigureAwait(false);
                try
                {
                    return JsonDocument.Parse(text);
                }
                catch (JsonException ex)
                {
                    throw new SoundCloudException("Resposta inválida do SoundCloud.", ex);
                }
            }
        }
    }

    private static string AddQuery(string url, string key, string value)
    {
        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{url}{separator}{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
    }

    // -- normalização --------------------------------------------------

    private static Track NormalizeTrack(JsonElement raw)
    {
        if ((Str(raw, "kind") is { } kind && kind != "track") || Long(raw, "id") <= 0)
            throw new NotFoundException("Este endereço do SoundCloud não aponta para uma faixa.");
        return Normalize(raw);
    }

    public static Track Normalize(JsonElement raw)
    {
        var user = raw.TryGetProperty("user", out var u) ? u : default;
        var publisher = raw.TryGetProperty("publisher_metadata", out var metadata) ? metadata : default;

        var artwork = Str(raw, "artwork_url") ?? Str(user, "avatar_url");
        if (!string.IsNullOrEmpty(artwork))
        {
            artwork = artwork.Replace("-large.", "-t500x500.", StringComparison.Ordinal)
                .Replace("-crop.", "-t500x500.", StringComparison.Ordinal);
        }

        return new Track
        {
            Id = Long(raw, "id").ToString(CultureInfo.InvariantCulture),
            Title = Str(raw, "title") ?? "Sem título",
            Artist = Str(user, "username") ?? "Desconhecido",
            CreditedArtists = ReadCreditedArtists(publisher),
            ArtistUrl = Str(user, "permalink_url"),
            Artwork = artwork,
            DurationMs = Long(raw, "duration"),
            PermalinkUrl = Str(raw, "permalink_url"),
            Genre = Str(raw, "genre") ?? "",
            Tags = ReadTags(Str(raw, "tag_list")),
            Album = Str(publisher, "album_title"),
            PlaybackCount = Long(raw, "playback_count"),
            CreatedAt = Str(raw, "created_at"),
            Streamable = Bool(raw, "streamable", true),
        };
    }

    private static string[]? ReadTags(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096) return null;
        var tags = new List<string>();
        var quoted = false;
        var start = 0;
        for (var i = 0; i <= text.Length && tags.Count < 24; i++)
        {
            if (i < text.Length && text[i] == '"') quoted = !quoted;
            if (i < text.Length && (quoted || !char.IsWhiteSpace(text[i]))) continue;
            var tag = text[start..i].Trim().Trim('"').Trim();
            if (tag.Length is > 0 and <= 64 && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
            start = i + 1;
        }
        return tags.Count == 0 ? null : tags.ToArray();
    }

    private static string? ReadCreditedArtists(JsonElement publisher)
    {
        if (publisher.ValueKind != JsonValueKind.Object || !publisher.TryGetProperty("artist", out var artist))
            return null;
        IEnumerable<string?> names = artist.ValueKind switch
        {
            JsonValueKind.String => artist.GetString()!.Split(','),
            JsonValueKind.Array => artist.EnumerateArray().Where(name => name.ValueKind == JsonValueKind.String)
                .Select(name => name.GetString()),
            _ => []
        };
        var joined = string.Join(", ", names.Select(name => name?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase));
        return joined.Length > 0 ? joined : null;
    }

    private static string ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !id.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("track_id inválido", nameof(id));
        }

        return id;
    }

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.ToString(),
        };
    }

    private static long Long(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
    }

    private static bool Bool(JsonElement element, string name, bool fallback)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        _gate.Dispose();
    }
}
