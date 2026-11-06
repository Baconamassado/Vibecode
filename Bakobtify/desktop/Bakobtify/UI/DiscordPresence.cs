using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Bakobtify.Data;

namespace Bakobtify.UI;

internal sealed class DiscordPresence(string pipePrefix = "discord-ipc-") : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private string? _connectedId;
    private (string Id, string? Track)? _sent;
    private long _retryAt;
    private int _failedAttempts;
    private long _renewAt;
    private long _sentStart;
    private long _sentDuration;
    private bool _busy;
    private bool _disposed;

    public void Update(string? applicationId, Track? track, TimeSpan position, TimeSpan duration)
    {
        if (_disposed || _busy) return;
        if (_pipe is null && applicationId is null)
        {
            _failedAttempts = 0;
            _retryAt = 0;
            return;
        }
        var desired = (Id: applicationId ?? _connectedId!, Track: track?.Id);
        var length = track is null ? 0 : (long)Math.Ceiling(Math.Max(0, duration.TotalSeconds));
        var start = length == 0 ? 0 : DateTimeOffset.UtcNow.ToUnixTimeSeconds() -
            (long)Math.Clamp(position.TotalSeconds, 0, duration.TotalSeconds);
        if (_sent == desired && applicationId is not null && _sentDuration == length &&
            Math.Abs(_sentStart - start) < 3 && Environment.TickCount64 < _renewAt) return;
        if (applicationId is not null && Environment.TickCount64 < _retryAt) return;
        _busy = true;
        _ = PublishAsync(applicationId, track, desired, start, length);
    }

    private async Task PublishAsync(string? applicationId, Track? track, (string Id, string? Track) desired,
        long start, long length)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            if (_pipe is not null && applicationId != _connectedId)
            {
                if (_sent?.Track is not null)
                    await SendActivityAsync(_pipe, null, timeout.Token);
                Disconnect();
            }
            if (applicationId is null) return;
            if (_pipe is null)
            {
                await ConnectAsync(applicationId);
                _failedAttempts = 0;
                _retryAt = 0;
                // O próximo tick usa a faixa e o relógio atuais após o handshake.
                return;
            }
            await SendActivityAsync(_pipe, track, timeout.Token, start, length);
            if (!_disposed)
            {
                _sent = desired;
                _sentStart = start;
                _sentDuration = length;
                _renewAt = Environment.TickCount64 + 30_000;
                _failedAttempts = 0;
                _retryAt = 0;
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or
            UnauthorizedAccessException or JsonException or ObjectDisposedException)
        {
            Disconnect();
            _retryAt = Environment.TickCount64 + (500 << Math.Min(_failedAttempts++, 3));
        }
        finally { _busy = false; }
    }

    private async Task ConnectAsync(string applicationId)
    {
        var handshake = JsonSerializer.SerializeToUtf8Bytes(new { v = 1, client_id = applicationId });
        for (var index = 0; index < 10 && !_disposed; index++)
        {
            var candidate = new NamedPipeClientStream(".", $"{pipePrefix}{index}", PipeDirection.InOut,
                PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
                await candidate.ConnectAsync(50, deadline.Token);
                await WriteAsync(candidate, 0, handshake, deadline.Token);
                await ReadUntilAsync(candidate, null, deadline.Token);
                if (_disposed) return;
                _connectedId = applicationId;
                _pipe = candidate;
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or
                UnauthorizedAccessException or JsonException or ObjectDisposedException) { }
            finally
            {
                if (_pipe != candidate) candidate.Dispose();
            }
        }
        if (!_disposed) throw new IOException("Discord IPC indisponível.");
    }

    private static async Task SendActivityAsync(Stream pipe, Track? track, CancellationToken token,
        long start = 0, long length = 0)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var art = track?.Artwork;
        var image = Uri.TryCreate(art, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host.EndsWith(".sndcdn.com", StringComparison.OrdinalIgnoreCase) ? uri.AbsoluteUri : null;
        Dictionary<string, object>? activity = null;
        if (track is not null)
        {
            activity = new Dictionary<string, object>
            {
                ["type"] = 2, ["status_display_type"] = 1, ["details"] = Limit(track.Title),
                ["state"] = Limit(track.CreditedArtists ?? track.ArtistOrUnknown)
            };
            if (image is not null) activity["assets"] = new { large_image = image, large_text = "Escutando no Bakobtify" };
            if (length > 0) activity["timestamps"] = new { start, end = start + length };
        }
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            cmd = "SET_ACTIVITY", args = new { pid = Environment.ProcessId, activity }, nonce
        });
        await WriteAsync(pipe, 1, payload, token);
        await ReadUntilAsync(pipe, nonce, token);
    }

    private static string Limit(string text) =>
        string.Concat(text.EnumerateRunes().Take(64).Select(rune => rune.ToString()));

    private static async Task WriteAsync(Stream pipe, int opcode, byte[] data, CancellationToken token)
    {
        var frame = new byte[8 + data.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), data.Length);
        data.CopyTo(frame, 8);
        await pipe.WriteAsync(frame, token);
    }

    private static async Task ReadUntilAsync(Stream pipe, string? nonce, CancellationToken token)
    {
        var header = new byte[8];
        while (true)
        {
            await pipe.ReadExactlyAsync(header, token);
            var opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (length is < 0 or > 65536 || opcode == 2) throw new IOException("Resposta IPC inválida.");
            var payload = new byte[length];
            await pipe.ReadExactlyAsync(payload, token);
            if (opcode == 3) { await WriteAsync(pipe, 4, payload, token); continue; }
            if (opcode != 1) continue;
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) continue;
            var evt = root.TryGetProperty("evt", out var eventValue) ? eventValue.ToString() : "";
            if (evt == "ERROR") throw new IOException("Discord recusou a atividade.");
            if (nonce is null ? evt == "READY" :
                root.TryGetProperty("nonce", out var nonceValue) && nonceValue.ToString() == nonce) return;
        }
    }

    private void Disconnect()
    {
        _pipe?.Dispose();
        _pipe = null;
        _connectedId = null;
        _sent = null;
    }

    public void Dispose()
    {
        _disposed = true;
        Disconnect();
    }
}
