using System.Globalization;

namespace Bakobtify.Playback;

public sealed record HlsVariant(string Uri, long Bandwidth, string? Codecs = null);

public sealed record HlsKey(string Method, string? Uri, byte[]? Iv);

public sealed class HlsSegment
{
    public required string Uri { get; init; }
    public double Duration { get; init; }
    public long? RangeOffset { get; init; }
    public long? RangeLength { get; init; }
    public HlsKey? Key { get; init; }
}

/// <summary>
/// Parser de playlists HLS (mestre e de mídia). Só o subconjunto usado pelo
/// SoundCloud: EXTINF, EXT-X-MAP, EXT-X-KEY (AES-128) e EXT-X-BYTERANGE.
/// </summary>
public sealed class HlsManifest
{
    public bool IsMaster { get; private set; }
    public List<HlsVariant> Variants { get; } = new();
    public List<HlsSegment> Segments { get; } = new();
    public string? MapUri { get; private set; }
    public long? MapRangeOffset { get; private set; }
    public long? MapRangeLength { get; private set; }
    public HlsKey? MapKey { get; private set; }
    public long MediaSequence { get; private set; }
    public bool EndList { get; private set; }
    public double TotalDuration => Segments.Sum(segment => segment.Duration);

    public static HlsManifest Parse(string text, Uri baseUri)
    {
        if (!text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("#EXTM3U", StringComparison.Ordinal))
            throw new InvalidDataException("O servidor não retornou uma playlist HLS válida.");
        var manifest = new HlsManifest();

        double? pendingDuration = null;
        long? pendingRangeLength = null;
        long? pendingRangeOffset = null;
        HlsKey? currentKey = null;
        long nextRangeOffset = 0;
        var expectVariantUri = false;
        long pendingBandwidth = 0;
        string? pendingCodecs = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                manifest.IsMaster = true;
                expectVariantUri = true;
                pendingBandwidth = ParseLong(GetAttribute(line, "BANDWIDTH")) ?? 0;
                pendingCodecs = GetAttribute(line, "CODECS");
                continue;
            }

            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                manifest.MediaSequence = ParseLong(line[22..]) ?? 0;
                continue;
            }

            if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal))
            {
                manifest.EndList = true;
                continue;
            }

            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var uri = GetAttribute(line, "URI");
                if (!string.IsNullOrWhiteSpace(uri))
                {
                    manifest.MapUri = Resolve(baseUri, uri);
                    manifest.MapKey = currentKey;
                    ParseByteRange(GetAttribute(line, "BYTERANGE"), out var mapLength, out var mapOffset);
                    manifest.MapRangeLength = mapLength;
                    manifest.MapRangeOffset = mapLength is null ? null : mapOffset ?? 0;
                }

                continue;
            }

            if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                var method = (GetAttribute(line, "METHOD") ?? "NONE").ToUpperInvariant();
                if (method == "NONE")
                {
                    currentKey = null;
                }
                else
                {
                    var uri = GetAttribute(line, "URI");
                    var keyFormat = GetAttribute(line, "KEYFORMAT");
                    currentKey = new HlsKey(
                        method,
                        method == "AES-128" && keyFormat is null or "identity" && !string.IsNullOrWhiteSpace(uri)
                            ? Resolve(baseUri, uri) : null,
                        ParseIv(GetAttribute(line, "IV")));
                }

                continue;
            }

            if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
            {
                ParseByteRange(line[17..], out pendingRangeLength, out pendingRangeOffset);
                if (pendingRangeOffset is null) pendingRangeOffset = nextRangeOffset;
                continue;
            }

            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var payload = line[8..];
                var comma = payload.IndexOf(',');
                if (comma >= 0) payload = payload[..comma];
                pendingDuration = ParseDouble(payload) ?? 0;
                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            // linha de URI
            if (expectVariantUri)
            {
                manifest.Variants.Add(new HlsVariant(Resolve(baseUri, line), pendingBandwidth, pendingCodecs));
                expectVariantUri = false;
                continue;
            }

            var segment = new HlsSegment
            {
                Uri = Resolve(baseUri, line),
                Duration = pendingDuration ?? 0,
                RangeLength = pendingRangeLength,
                RangeOffset = pendingRangeOffset,
                Key = currentKey,
            };

            if (pendingRangeLength is { } length && pendingRangeOffset is { } offset)
            {
                nextRangeOffset = offset + length;
            }

            manifest.Segments.Add(segment);
            pendingDuration = null;
            pendingRangeLength = null;
            pendingRangeOffset = null;
        }

        return manifest;
    }

    private static string Resolve(Uri baseUri, string value)
    {
        if (!Uri.TryCreate(baseUri, value, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("A playlist HLS contém um endereço de áudio inválido.");
        return uri.AbsoluteUri;
    }

    private static string? GetAttribute(string line, string name)
    {
        var index = line.IndexOf(name + "=", StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;

        var start = index + name.Length + 1;
        if (start >= line.Length) return "";

        if (line[start] == '"')
        {
            var end = line.IndexOf('"', start + 1);
            return end < 0 ? line[(start + 1)..] : line[(start + 1)..end];
        }

        var comma = line.IndexOf(',', start);
        return comma < 0 ? line[start..] : line[start..comma];
    }

    private static void ParseByteRange(string? value, out long? length, out long? offset)
    {
        length = null;
        offset = null;
        if (string.IsNullOrWhiteSpace(value)) return;

        var parts = value.Split('@', 2);
        length = ParseLong(parts[0]);
        if (parts.Length == 2) offset = ParseLong(parts[1]);
    }

    private static byte[]? ParseIv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var hex = value.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        if (hex.Length == 0) return null;

        if ((hex.Length & 1) != 0) hex = "0" + hex;
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return null;
            }
        }

        if (bytes.Length >= 16) return bytes[^16..];

        var full = new byte[16];
        Buffer.BlockCopy(bytes, 0, full, 16 - bytes.Length, bytes.Length);
        return full;
    }

    private static long? ParseLong(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static double? ParseDouble(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;
}
