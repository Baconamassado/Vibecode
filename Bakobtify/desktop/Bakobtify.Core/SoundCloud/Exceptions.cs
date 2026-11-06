namespace Bakobtify.SoundCloud;

public class SoundCloudException : Exception
{
    public SoundCloudException(string message) : base(message) { }
    public SoundCloudException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Não há client_id válido disponível.</summary>
public sealed class DegradedException : SoundCloudException
{
    public DegradedException(string message) : base(message) { }
}

/// <summary>Faixa/consulta inexistente.</summary>
public sealed class NotFoundException : SoundCloudException
{
    public NotFoundException(string message) : base(message) { }
}

/// <summary>A faixa não pode ser tocada sem o embed oficial (proteção/DRM).</summary>
public sealed class PlaybackUnavailableException : SoundCloudException
{
    public string? Permalink { get; }
    internal bool ConfirmedByMetadata { get; init; }

    public PlaybackUnavailableException(string message, string? permalink = null) : base(message)
    {
        Permalink = permalink;
    }
}

public sealed record StreamInfo(
    string Kind,
    string? Url,
    string? Mime,
    string? Reason,
    long DurationMs)
{
    public bool IsPlayable => Kind is "progressive" or "hls";
    public bool IsPreview { get; init; }
}
