using System.Net;
using System.Text.RegularExpressions;
using Bakobtify.Data;

namespace Bakobtify.SoundCloud;

public sealed record ClientStatus(string State, string? Source, int ExpiresIn, string? Error);

/// <summary>
/// Obtém o <c>client_id</c> público do SoundCloud sem aplicativo registrado
/// e sem navegador. Estratégia: cache SQLite → scraping estático do bundle JS.
/// </summary>
public sealed class ClientIdProvider
{
    private const string Site = "https://soundcloud.com";
    private const string Api = "https://api-v2.soundcloud.com";
    private const string MetaKey = "sc_client_id";

    private static readonly Regex InlineRe = new(
        """\bclient_id["']?\s*[:=]\s*["']([0-9a-zA-Z]{32})["']""",
        RegexOptions.Compiled);

    private static readonly Regex QueryRe = new(
        @"client_id=([0-9a-zA-Z]{32})",
        RegexOptions.Compiled);

    private static readonly Regex ScriptRe = new(
        """<script\b[^>]*\s+src\s*=\s*["']([^"']+)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Database _db;
    private readonly HttpClient _http;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _refresh = new(1);

    private string? _clientId;
    private double _expiresAt;
    private string? _source;
    private string _state = "unknown";
    private string? _lastError;

    public ClientIdProvider(Database db, HttpClient http)
    {
        _db = db;
        _http = http;
    }

    public ClientStatus Status()
    {
        lock (_lock)
        {
            var remaining = (int)Math.Max(0, _expiresAt - Database.Now());
            return new ClientStatus(_state, _source, remaining, _lastError);
        }
    }

    public async Task<string> GetAsync(CancellationToken ct = default)
    {
        await _refresh.WaitAsync(ct).ConfigureAwait(false);
        try { return await GetOrRefreshAsync(ct).ConfigureAwait(false); }
        finally { _refresh.Release(); }
    }

    private async Task<string> GetOrRefreshAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_clientId is { Length: > 0 } cached && Database.Now() < _expiresAt)
            {
                return cached;
            }
        }

        var meta = _db.GetMeta(MetaKey);
        if (meta is { } row && row.Value.Length > 0 && Database.Now() < row.ExpiresAt)
        {
            if (await ValidateAsync(row.Value, ct).ConfigureAwait(false))
            {
                Install(row.Value, "cache", Math.Max(0, row.ExpiresAt - Database.Now()));
                return row.Value;
            }
        }

        var (cid, source) = await ExtractAsync(ct).ConfigureAwait(false);
        if (cid is null)
        {
            Fail("Não foi possível obter um client_id (scraping estático falhou).");
            throw new DegradedException(_lastError ?? "client_id indisponível.");
        }

        var ttl = AppConfig.ClientIdTtlHours * 3600.0;
        Install(cid, source!, ttl);
        _db.SetMeta(MetaKey, cid, _expiresAt);
        return cid;
    }

    public void Invalidate(string? rejected = null)
    {
        lock (_lock)
        {
            if (rejected is not null && _clientId != rejected) return;
            _clientId = null;
            _expiresAt = 0;
            _state = "unknown";
            _db.DeleteMeta(MetaKey);
        }
    }

    private void Install(string clientId, string source, double ttlSeconds)
    {
        lock (_lock)
        {
            _clientId = clientId;
            _expiresAt = Database.Now() + Math.Max(0, ttlSeconds);
            _source = source;
            _state = "ok";
            _lastError = null;
        }
    }

    private void Fail(string message)
    {
        lock (_lock)
        {
            _clientId = null;
            _expiresAt = 0;
            _state = "degraded";
            _lastError = message;
        }
    }

    public async Task<bool> ValidateAsync(string clientId, CancellationToken ct)
    {
        try
        {
            var url = $"{Api}/search/tracks?q=a&limit=1&app_locale=en&client_id={Uri.EscapeDataString(clientId)}";
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<(string? ClientId, string? Source)> ExtractAsync(CancellationToken ct)
    {
        try
        {
            var html = await _http.GetStringAsync(Site, ct).ConfigureAwait(false);
            var inline = InlineRe.Match(html);
            if (inline.Success) return (inline.Groups[1].Value, "static");

            var candidates = new List<string>();
            foreach (Match match in ScriptRe.Matches(html))
            {
                var src = WebUtility.HtmlDecode(match.Groups[1].Value);
                if (!Uri.TryCreate(new Uri(Site), src, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                    !uri.Host.EndsWith(".sndcdn.com", StringComparison.OrdinalIgnoreCase) ||
                    !uri.AbsolutePath.StartsWith("/assets/", StringComparison.Ordinal) ||
                    !uri.AbsolutePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) continue;
                candidates.Add(uri.AbsoluteUri);
            }

            foreach (var url in candidates.AsEnumerable().Reverse().Take(8))
            {
                string js;
                try
                {
                    js = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    continue;
                }

                var found = InlineRe.Match(js);
                if (!found.Success) found = QueryRe.Match(js);
                if (found.Success) return (found.Groups[1].Value, "static");
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // cai no retorno nulo
        }

        return (null, null);
    }
}
