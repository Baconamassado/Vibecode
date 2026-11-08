using System.Net;

namespace Bakobtify.SoundCloud;

internal sealed class SoundCloudTransport(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    internal static bool IsTrustedUri(Uri uri) => Provider(uri) == "soundcloud";

    internal static string? Provider(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0) return null;

        var host = uri.IdnHost;
        if (IsDomain(host, "soundcloud.com") || IsDomain(host, "sndcdn.com")) return "soundcloud";
        if (IsDomain(host, "audius.co") || IsDomain(host, "audius.org")) return "audius";
        return null;
    }

    private static bool IsDomain(string host, string domain) =>
        host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var current = request;
        var provider = request.RequestUri is { } destination ? Provider(destination) : null;
        if (provider is null)
            throw new HttpRequestException("Destino HTTP fora dos domínios autorizados.");
        if (provider != "soundcloud")
        {
            request.Headers.Remove("Origin");
            request.Headers.Remove("Referer");
            request.Headers.Remove("Authorization");
            request.Headers.Remove("Cookie");
            request.Headers.Host = null;
        }
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                var uri = current.RequestUri!;

                var response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);
                if (current.Method != HttpMethod.Get || response.StatusCode is not
                    (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
                     HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                    return response;

                using (response)
                {
                    if (redirects >= 5 || response.Headers.Location is not { } location ||
                        !Uri.TryCreate(uri, location, out var next) || Provider(next) != provider)
                        throw new HttpRequestException("Redirecionamento HTTP fora dos domínios autorizados.");

                    if (current != request) current.Dispose();
                    current = new HttpRequestMessage(HttpMethod.Get, next);
                    foreach (var header in request.Headers)
                    {
                        if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                            header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                            header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                        current.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }
        }
        finally
        {
            if (current != request) current.Dispose();
        }
    }
}
