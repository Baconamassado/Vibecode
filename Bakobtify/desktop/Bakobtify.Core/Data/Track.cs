using System.Text.Json.Serialization;

namespace Bakobtify.Data;

/// <summary>
/// Formato enxuto da faixa — exatamente os campos gravados pela versão Python
/// no SQLite (snake_case), para que o banco existente continue válido.
/// </summary>
public sealed class Track
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("artist")] public string? Artist { get; set; }
    [JsonPropertyName("credited_artists")] public string? CreditedArtists { get; set; }
    [JsonPropertyName("artist_url")] public string? ArtistUrl { get; set; }
    [JsonPropertyName("artist_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long ArtistId { get; set; }
    [JsonPropertyName("artwork")] public string? Artwork { get; set; }
    [JsonPropertyName("duration_ms")] public long DurationMs { get; set; }
    [JsonPropertyName("permalink_url")] public string? PermalinkUrl { get; set; }
    [JsonPropertyName("genre")] public string? Genre { get; set; }
    [JsonPropertyName("tags")] public string[]? Tags { get; set; }
    [JsonPropertyName("album")] public string? Album { get; set; }
    [JsonPropertyName("playback_count")] public long PlaybackCount { get; set; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    [JsonPropertyName("streamable")] public bool Streamable { get; set; } = true;

    /// <summary>Preenchido apenas pelo histórico.</summary>
    [JsonPropertyName("played_at")] public double? PlayedAt { get; set; }

    public string ArtistOrUnknown => string.IsNullOrWhiteSpace(Artist) ? "Desconhecido" : Artist;

    [JsonIgnore]
    public string Provider => Id.StartsWith("audius:", StringComparison.Ordinal) ? "Audius" :
        Id.StartsWith("jamendo:", StringComparison.Ordinal) ? "Jamendo" : "SoundCloud";

    public string DurationText
    {
        get
        {
            var seconds = Math.Max(0, DurationMs) / 1000;
            return $"{seconds / 60}:{seconds % 60:00}";
        }
    }
}

public sealed class PlaylistInfo
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("track_count")] public long TrackCount { get; set; }
    [JsonPropertyName("created_at")] public double CreatedAt { get; set; }
}
