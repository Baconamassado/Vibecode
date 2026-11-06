using System.Net;

namespace Bakobtify.SoundCloud;

internal sealed class SoundCloudTransport(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    internal static bool IsTrustedUri(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 &&
        (uri.IdnHost is "soundcloud.com" or "sndcdn.com" ||
         uri.IdnHost.EndsWith(".soundcloud.com", StringComparison.OrdinalIgnoreCase) ||
         uri.IdnHost.EndsWith(".sndcdn.com", StringComparison.OrdinalIgnoreCase));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var current = request;
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                if (current.RequestUri is not { } uri || !IsTrustedUri(uri))
                    throw new HttpRequestException("Destino HTTP fora dos domínios do SoundCloud.");

                var response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);
                if (current.Method != HttpMethod.Get || response.StatusCode is not
                    (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
                     HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                    return response;

                using (response)
                {
                    if (redirects >= 5 || response.Headers.Location is not { } location ||
                        !Uri.TryCreate(uri, location, out var next) || !IsTrustedUri(next))
                        throw new HttpRequestException("Redirecionamento HTTP inválido do SoundCloud.");

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
