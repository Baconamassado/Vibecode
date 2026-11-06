using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bakobtify.Data;

/// <summary>
/// Persistência local. Mesmo schema, mesmas consultas e o mesmo arquivo
/// (<c>data/bakobtify.db</c>) da versão Python.
/// </summary>
public sealed class Database : IDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS meta (
            key        TEXT PRIMARY KEY,
            value      TEXT NOT NULL,
            expires_at REAL NOT NULL
        );

        CREATE TABLE IF NOT EXISTS favorites (
            track_id   TEXT PRIMARY KEY,
            payload    TEXT NOT NULL,
            created_at REAL NOT NULL
        );

        CREATE TABLE IF NOT EXISTS playlists (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            name       TEXT NOT NULL UNIQUE,
            created_at REAL NOT NULL
        );

        CREATE TABLE IF NOT EXISTS playlist_tracks (
            playlist_id INTEGER NOT NULL,
            track_id    TEXT NOT NULL,
            position    INTEGER NOT NULL,
            payload     TEXT NOT NULL,
            added_at    REAL NOT NULL,
            PRIMARY KEY (playlist_id, track_id),
            FOREIGN KEY (playlist_id) REFERENCES playlists(id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS history (
            id        INTEGER PRIMARY KEY AUTOINCREMENT,
            track_id  TEXT NOT NULL,
            payload   TEXT NOT NULL,
            played_at REAL NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_history_played ON history(played_at DESC);

        CREATE TABLE IF NOT EXISTS local_audio (
            track_id TEXT PRIMARY KEY,
            path     TEXT NOT NULL
        );
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly object _gate = new();
    private readonly SqliteConnection _db;

    public Database(string? path = null)
    {
        _db = SqliteConnection.Open(path ?? AppConfig.DatabasePath);
        lock (_gate)
        {
            _db.Execute("PRAGMA journal_mode=WAL");
            _db.Execute("PRAGMA foreign_keys=ON");
            _db.Execute(Schema);
        }
    }

    public static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    // -- meta ----------------------------------------------------------

    public (string Value, double ExpiresAt)? GetMeta(string key)
    {
        lock (_gate)
        {
            using var stmt = _db.Prepare("SELECT value, expires_at FROM meta WHERE key = ?");
            stmt.Bind(1, key);
            if (!stmt.Step()) return null;
            return (stmt.GetString(0) ?? "", stmt.GetDouble(1));
        }
    }

    public void SetMeta(string key, string value, double expiresAt)
    {
        lock (_gate)
        {
            using var stmt = _db.Prepare(
                "INSERT INTO meta(key, value, expires_at) VALUES(?, ?, ?) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value, expires_at = excluded.expires_at");
            stmt.Bind(1, key).Bind(2, value).Bind(3, expiresAt);
            stmt.Step();
        }
    }

    public void DeleteMeta(string key)
    {
        lock (_gate)
        {
            using var stmt = _db.Prepare("DELETE FROM meta WHERE key = ?");
            stmt.Bind(1, key);
            stmt.Step();
        }
    }

    public string? GetLocalAudio(string trackId)
    {
        lock (_gate)
        {
            using var stmt = _db.Prepare("SELECT path FROM local_audio WHERE track_id = ?");
            return stmt.Bind(1, trackId).Step() ? stmt.GetString(0) : null;
        }
    }

    public void SetLocalAudio(string trackId, string? path)
    {
        lock (_gate)
        {
            using var stmt = _db.Prepare(path is null
                ? "DELETE FROM local_audio WHERE track_id = ?"
                : "INSERT INTO local_audio(track_id, path) VALUES(?, ?) " +
                    "ON CONFLICT(track_id) DO UPDATE SET path = excluded.path");
            stmt.Bind(1, trackId);
            if (path is not null) stmt.Bind(2, path);
            stmt.Step();
        }
    }

    // -- favoritos -----------------------------------------------------

    public List<Track> ListFavorites()
    {
        lock (_gate)
        {
            var result = new List<Track>();
            using var stmt = _db.Prepare("SELECT payload FROM favorites ORDER BY created_at DESC");
            while (stmt.Step())
            {
                var track = Deserialize(stmt.GetString(0));
                if (track is not null) result.Add(track);
            }

            return result;
        }
    }

    public void AddFavorite(Track track)
    {
        lock (_gate)
        {
            using var stmt = _db.Prepare(
                "INSERT INTO favorites(track_id, payload, created_at) VALUES(?, ?, ?) " +
                "ON CONFLICT(track_id) DO UPDATE SET payload = excluded.payload");
            stmt.Bind(1, track.Id).Bind(2, Serialize(track)).Bind(3, Now());
            stmt.Step();
        }
    }

    public void RemoveFavorite(string trackId)
    {
        lock (_gate)
        {
            using var stmt = _db.Prepare("DELETE FROM favorites WHERE track_id = ?");
            stmt.Bind(1, trackId);
            stmt.Step();
        }
    }

    // -- playlists -----------------------------------------------------

    public List<PlaylistInfo> ListPlaylists()
    {
        lock (_gate)
        {
            var result = new List<PlaylistInfo>();
            using var stmt = _db.Prepare(
                "SELECT p.id, p.name, p.created_at, " +
                "  (SELECT COUNT(*) FROM playlist_tracks t WHERE t.playlist_id = p.id) AS track_count " +
                "FROM playlists p ORDER BY p.created_at ASC");
            while (stmt.Step())
            {
                result.Add(new PlaylistInfo
                {
                    Id = stmt.GetInt64(0),
                    Name = stmt.GetString(1) ?? "",
                    CreatedAt = stmt.GetDouble(2),
                    TrackCount = stmt.GetInt64(3),
                });
            }

            return result;
        }
    }

    public PlaylistInfo CreatePlaylist(string name, Track? initialTrack = null)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException("O nome da playlist não pode ser vazio.");

        lock (_gate)
        {
            _db.Execute("BEGIN IMMEDIATE");
            try
            {
                var created = Now();
                using (var stmt = _db.Prepare("INSERT INTO playlists(name, created_at) VALUES(?, ?)"))
                    stmt.Bind(1, name).Bind(2, created).Step();
                var id = _db.LastInsertRowId;
                if (initialTrack is not null) AddToPlaylist(id, initialTrack);
                _db.Execute("COMMIT");
                return new PlaylistInfo { Id = id, Name = name, TrackCount = initialTrack is null ? 0 : 1, CreatedAt = created };
            }
            catch
            {
                _db.Execute("ROLLBACK");
                throw;
            }
        }
    }

    public bool DeletePlaylist(long playlistId)
    {
        lock (_gate)
        {
            using var stmt = _db.Prepare("DELETE FROM playlists WHERE id = ?");
            stmt.Bind(1, playlistId);
            stmt.Step();
            return _db.Changes > 0;
        }
    }

    public bool PlaylistExists(long playlistId)
    {
        lock (_gate)
        {
            using var stmt = _db.Prepare("SELECT 1 FROM playlists WHERE id = ?");
            stmt.Bind(1, playlistId);
            return stmt.Step();
        }
    }

    public List<Track> PlaylistTracks(long playlistId)
    {
        lock (_gate)
        {
            var result = new List<Track>();
            using var stmt = _db.Prepare(
                "SELECT payload FROM playlist_tracks WHERE playlist_id = ? ORDER BY position ASC");
            stmt.Bind(1, playlistId);
            while (stmt.Step())
            {
                var track = Deserialize(stmt.GetString(0));
                if (track is not null) result.Add(track);
            }

            return result;
        }
    }

    public void AddToPlaylist(long playlistId, Track track)
    {
        lock (_gate)
        {
            long position;
            using (var max = _db.Prepare(
                       "SELECT COALESCE(MAX(position), -1) AS m FROM playlist_tracks WHERE playlist_id = ?"))
            {
                max.Bind(1, playlistId);
                position = max.Step() ? max.GetInt64(0) + 1 : 1;
            }

            using var stmt = _db.Prepare(
                "INSERT INTO playlist_tracks(playlist_id, track_id, position, payload, added_at) " +
                "VALUES(?, ?, ?, ?, ?) " +
                "ON CONFLICT(playlist_id, track_id) DO UPDATE SET payload = excluded.payload");
            stmt.Bind(1, playlistId).Bind(2, track.Id).Bind(3, position)
                .Bind(4, Serialize(track)).Bind(5, Now());
            stmt.Step();
        }
    }

    public void RemoveFromPlaylist(long playlistId, string trackId)
    {
        lock (_gate)
        {
            using var stmt = _db.Prepare(
                "DELETE FROM playlist_tracks WHERE playlist_id = ? AND track_id = ?");
            stmt.Bind(1, playlistId).Bind(2, trackId);
            stmt.Step();
        }
    }

    // -- histórico -----------------------------------------------------

    public List<Track> ListHistory(int limit = 100)
    {
        lock (_gate)
        {
            var result = new List<Track>();
            using var stmt = _db.Prepare(
                "SELECT payload, played_at FROM history ORDER BY played_at DESC LIMIT ?");
            stmt.Bind(1, Math.Max(0, limit));
            while (stmt.Step())
            {
                var track = Deserialize(stmt.GetString(0));
                if (track is null) continue;
                track.PlayedAt = stmt.GetDouble(1);
                result.Add(track);
            }

            return result;
        }
    }

    public List<Track> ListRecentPlaylistTracks(int limit)
    {
        lock (_gate)
        {
            var result = new List<Track>();
            using var stmt = _db.Prepare("""
                SELECT payload, MAX(added_at) AS latest FROM playlist_tracks
                GROUP BY track_id ORDER BY latest DESC LIMIT ?
                """);
            stmt.Bind(1, Math.Max(0, limit));
            while (stmt.Step())
                if (Deserialize(stmt.GetString(0)) is { } track) result.Add(track);
            return result;
        }
    }

    public void AddHistory(Track track)
    {
        lock (_gate)
        {
            using (var stmt = _db.Prepare(
                       "INSERT INTO history(track_id, payload, played_at) VALUES(?, ?, ?)"))
            {
                stmt.Bind(1, track.Id).Bind(2, Serialize(track)).Bind(3, Now());
                stmt.Step();
            }

            _db.Execute(
                "DELETE FROM history WHERE id NOT IN " +
                "(SELECT id FROM history ORDER BY played_at DESC LIMIT 500)");
        }
    }

    public void ClearHistory()
    {
        lock (_gate) _db.Execute("DELETE FROM history");
    }

    public void Dispose()
    {
        lock (_gate) _db.Dispose();
    }

    private static string Serialize(Track track) => JsonSerializer.Serialize(track, JsonOptions);

    private static Track? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<Track>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
