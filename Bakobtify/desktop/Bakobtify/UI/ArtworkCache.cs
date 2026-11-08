using System.Drawing.Drawing2D;

namespace Bakobtify.UI;

internal sealed class ArtworkCache(HttpClient http, CancellationToken lifetime) : IDisposable
{
    private const int Capacity = 96;
    private const long DecodedByteBudget = 12 * 1024 * 1024;
    private long _decodedBytes;
    private readonly SemaphoreSlim _downloads = new(4);
    private readonly Dictionary<string, (Image? Image, LinkedListNode<string> Node)> _cache = new();
    private readonly LinkedList<string> _recent = new();
    private readonly Dictionary<string, HashSet<Control>> _pending = new();
    private bool _disposed;

    public void Draw(Control owner, Graphics g, Rectangle bounds, string? url, Color color,
        int radius = 10, bool heart = false)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var clip = Theme.Rounded(bounds, radius);
        g.SetClip(clip, CombineMode.Intersect);
        if (Get(url, owner) is { } image)
        {
            var side = Math.Min(image.Width, image.Height);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(image, bounds, (image.Width - side) / 2, (image.Height - side) / 2,
                side, side, GraphicsUnit.Pixel);
        }
        else
        {
            var tint = Color.FromArgb(25 + color.R / 13, 25 + color.G / 13, 28 + color.B / 13);
            Theme.Fill(g, bounds, tint);
            var size = Math.Min(bounds.Width, bounds.Height);
            var cx = bounds.Left + bounds.Width / 2f;
            var cy = bounds.Top + bounds.Height / 2f;
            using var pen = new Pen(Color.FromArgb(85, color), Math.Max(1, size / 110f));
            foreach (var scale in new[] { .40f, .60f, .81f })
            {
                var d = size * scale;
                g.DrawEllipse(pen, cx - d / 2, cy - d / 2, d, d);
            }
            var center = (int)(size * .28f);
            var disc = new Rectangle((int)cx - center / 2, (int)cy - center / 2, center, center);
            if (heart)
                Icons.Draw(g, "liked", Rectangle.Inflate(disc, center / 3, center / 3), color);
            else
            {
                Theme.Fill(g, disc, color, center / 2);
                var hole = Math.Max(2, center / 5);
                Theme.Fill(g, new Rectangle((int)cx - hole / 2, (int)cy - hole / 2, hole, hole), tint, hole);
            }
        }
        g.Restore(state);
    }

    private Image? Get(string? url, Control owner)
    {
        if (_disposed || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.Host.EndsWith(".sndcdn.com", StringComparison.OrdinalIgnoreCase))
            return null;
        if (_cache.TryGetValue(uri.AbsoluteUri, out var hit))
        {
            _recent.Remove(hit.Node);
            _recent.AddLast(hit.Node);
            return hit.Image;
        }
        if (_pending.TryGetValue(uri.AbsoluteUri, out var waiting))
            waiting.Add(owner);
        else
        {
            _pending.Add(uri.AbsoluteUri, [owner]);
            _ = LoadAsync(uri);
        }
        return null;
    }

    private async Task LoadAsync(Uri uri)
    {
        Image? thumbnail = null;
        try
        {
            await _downloads.WaitAsync(lifetime);
            try
            {
                thumbnail = await Task.Run(async () =>
                {
                    const int maxBytes = 4 * 1024 * 1024;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                    timeout.CancelAfter(TimeSpan.FromSeconds(40));
                    var ct = timeout.Token;
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > maxBytes) return null;
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    using var data = new MemoryStream();
                    var buffer = new byte[16 * 1024];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                    {
                        if (data.Length + read > maxBytes) return null;
                        data.Write(buffer, 0, read);
                    }
                    data.Position = 0;
                    using var source = Image.FromStream(data);
                    if (source.Width > 4096 || source.Height > 4096) return null;
                    var ratio = Math.Min(1d, 400d / Math.Max(source.Width, source.Height));
                    return new Bitmap(source, Math.Max(1, (int)(source.Width * ratio)),
                        Math.Max(1, (int)(source.Height * ratio)));
                }, lifetime);
            }
            finally { _downloads.Release(); }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException
            or ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException) { }

        _pending.Remove(uri.AbsoluteUri, out var waiting);
        if (_disposed || lifetime.IsCancellationRequested) { thumbnail?.Dispose(); return; }
        _cache[uri.AbsoluteUri] = (thumbnail, _recent.AddLast(uri.AbsoluteUri));
        _decodedBytes += BitmapBytes(thumbnail);
        while ((_cache.Count > Capacity || _decodedBytes > DecodedByteBudget) && _recent.First is { } first)
        {
            var evicted = _cache[first.Value].Image;
            _decodedBytes -= BitmapBytes(evicted);
            evicted?.Dispose();
            _cache.Remove(first.Value);
            _recent.RemoveFirst();
        }
        if (thumbnail is not null && waiting is not null)
            foreach (var owner in waiting)
                if (!owner.IsDisposed) owner.Invalidate();
    }

    private static long BitmapBytes(Image? image) => image is null ? 0 : (long)image.Width * image.Height * 4;

    public void Dispose()
    {
        _disposed = true;
        foreach (var item in _cache.Values) item.Image?.Dispose();
        _cache.Clear();
        _pending.Clear();
        _recent.Clear();
        _decodedBytes = 0;
    }
}
