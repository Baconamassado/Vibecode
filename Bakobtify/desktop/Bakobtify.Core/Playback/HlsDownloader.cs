using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Bakobtify.SoundCloud;

namespace Bakobtify.Playback;

public sealed record DownloadedHls(string FilePath, double DurationSeconds, bool IsFragmentedMp4);

/// <summary>
/// Baixa uma playlist HLS de áudio e grava os bytes (init + segmentos) em um
/// arquivo temporário. Se houver AES-128, descriptografa segmento a segmento.
/// </summary>
public sealed class HlsDownloader
{
    private readonly HttpClient _http;

    public HlsDownloader(HttpClient http) => _http = http;

    public async Task<DownloadedHls> DownloadAsync(
        string playlistUrl,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        string? mime = null)
    {
        var manifest = await GetManifestAsync(new Uri(playlistUrl), ct).ConfigureAwait(false);
        if (manifest.IsMaster)
        {
            var variant = manifest.Variants
                .OrderByDescending(item => item.Codecs?.Contains("mp4a", StringComparison.OrdinalIgnoreCase) == true)
                .ThenByDescending(item => item.Bandwidth).FirstOrDefault()
                ?? throw new SoundCloudException("Playlist HLS sem variantes de áudio.");
            manifest = await GetManifestAsync(new Uri(variant.Uri), ct).ConfigureAwait(false);
        }
        if (manifest.Segments.Count == 0)
            throw new SoundCloudException("Playlist HLS sem segmentos.");

        CheckKey(manifest.MapKey);
        foreach (var segment in manifest.Segments) CheckKey(segment.Key);
        if (manifest.MapKey is { Iv: null })
            throw new InvalidDataException("A inicialização HLS criptografada veio sem o vetor de inicialização.");

        var keyCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        async Task<byte[]> DecryptAsync(byte[] bytes, HlsKey key, byte[] iv)
        {
            var uri = key.Uri!;
            if (!keyCache.TryGetValue(uri, out var secret))
                keyCache[uri] = secret = await _http.GetByteArrayAsync(uri, ct).ConfigureAwait(false);
            return DecryptAes128(bytes, secret, iv);
        }

        string? path = null;
        try
        {
            var first = manifest.Segments[0];
            var firstBytes = await GetBytesAsync(first.Uri, first.RangeOffset, first.RangeLength, ct).ConfigureAwait(false);
            if (first.Key is { } firstKey)
                firstBytes = await DecryptAsync(firstBytes, firstKey, firstKey.Iv ?? BuildIv(manifest.MediaSequence))
                    .ConfigureAwait(false);
            var isFmp4 = manifest.MapUri is not null;
            var extension = isFmp4 ? ".mp4" : DetectExtension(first.Uri, firstBytes, mime);
            var outputPath = Path.Combine(AppConfig.TempDirectory, $"hls-{Guid.NewGuid():N}{extension}");
            await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                64 * 1024, useAsync: true);
            path = outputPath;
            if (manifest.MapUri is not null)
            {
                var map = await GetBytesAsync(manifest.MapUri, manifest.MapRangeOffset, manifest.MapRangeLength, ct)
                    .ConfigureAwait(false);
                if (manifest.MapKey is { } key)
                    map = await DecryptAsync(map, key, key.Iv!).ConfigureAwait(false);
                await output.WriteAsync(map, ct).ConfigureAwait(false);
            }
            await output.WriteAsync(firstBytes, ct).ConfigureAwait(false);
            progress?.Report(1d / manifest.Segments.Count);
            const int batchSize = 3;
            for (var start = 1; start < manifest.Segments.Count; start += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var count = Math.Min(batchSize, manifest.Segments.Count - start);
                var pending = new Task<byte[]>[count];
                for (var offset = 0; offset < count; offset++)
                {
                    var segment = manifest.Segments[start + offset];
                    pending[offset] = GetBytesAsync(segment.Uri, segment.RangeOffset, segment.RangeLength, ct);
                }
                var batch = await Task.WhenAll(pending).ConfigureAwait(false);
                for (var offset = 0; offset < count; offset++)
                {
                    var index = start + offset;
                    var bytes = batch[offset];
                    if (manifest.Segments[index].Key is { } key)
                        bytes = await DecryptAsync(bytes, key, key.Iv ?? BuildIv(manifest.MediaSequence + index))
                            .ConfigureAwait(false);
                    await output.WriteAsync(bytes, ct).ConfigureAwait(false);
                    progress?.Report((index + 1) / (double)manifest.Segments.Count);
                }
            }
            return new DownloadedHls(outputPath, manifest.TotalDuration, isFmp4);
        }
        catch
        {
            if (path is not null) TryDelete(path);
            throw;
        }
        finally
        {
            foreach (var key in keyCache.Values) CryptographicOperations.ZeroMemory(key);
        }
    }

    private async Task<HlsManifest> GetManifestAsync(Uri url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return HlsManifest.Parse(text, response.RequestMessage?.RequestUri ?? url);
    }

    private static void CheckKey(HlsKey? key)
    {
        if (key is null || key.Method == "AES-128" && key.Uri is not null) return;
        throw new PlaybackUnavailableException("O SoundCloud disponibilizou este áudio com proteção DRM.");
    }

    private static string DetectExtension(string url, ReadOnlySpan<byte> bytes, string? mime)
    {
        if (bytes.Length >= 8 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8)) return ".mp4";
        if (bytes.Length >= 377 && bytes[0] == 0x47 && bytes[188] == 0x47 && bytes[376] == 0x47) return ".ts";
        if (bytes.Length >= 2 && bytes[0] == 0xff && (bytes[1] & 0xf6) == 0xf0) return ".aac";
        var extension = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
        if (extension is ".mp4" or ".m4a" or ".aac" or ".ts" or ".mp3") return extension;
        return mime == "audio/mp4" ? ".aac" : ".mp3";
    }

    private async Task<byte[]> GetBytesAsync(
        string url,
        long? rangeOffset,
        long? rangeLength,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (rangeOffset is { } offset && rangeLength is { } length)
        {
            request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new SoundCloudException($"Falha ao baixar segmento HLS ({(int)response.StatusCode}).");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        if (rangeOffset is { } start && rangeLength is { } size)
        {
            if (response.StatusCode == HttpStatusCode.PartialContent && bytes.Length == size)
            {
                return bytes;
            }

            if (response.StatusCode == HttpStatusCode.OK && start >= 0 && size > 0 &&
                start <= bytes.Length && size <= bytes.Length - start)
            {
                return bytes.AsSpan((int)start, (int)size).ToArray();
            }
            throw new SoundCloudException("O CDN não entregou o intervalo de bytes solicitado.");
        }

        return bytes;
    }

    private static byte[] BuildIv(long sequence)
    {
        var iv = new byte[16];
        var value = (ulong)sequence;
        for (var i = 0; i < 8; i++)
        {
            iv[15 - i] = (byte)(value >> (8 * i));
        }

        return iv;
    }

    private static byte[] DecryptAes128(byte[] data, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;

        try
        {
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(data, 0, data.Length);
        }
        catch (CryptographicException)
        {
            aes.Padding = PaddingMode.None;
            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(data, 0, data.Length);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // arquivo temporário: melhor esforço
        }
    }
}
