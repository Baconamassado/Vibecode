using System.Text.Json;
using Bakobtify.Data;
using Bakobtify.Playback;

namespace Bakobtify.UI;

internal sealed partial class MainForm
{
    private sealed record PlaylistFeedback(Track Track, bool Accepted);
    private long? _activePlaylistId;
    private IReadOnlyList<Track> _playlistSeeds = [];
    private List<PlaylistFeedback> _playlistFeedback = [];
    private readonly HashSet<string> _playlistVisited = new();
    private readonly HashSet<string> _automaticTracks = new();
    private bool _continuePlaylist;
    private Task? _relatedLoad;
    private long _lastPlaylistFeedback = -1;

    private static string ContinuationKey(long id) => $"desktop_playlist_continue_{id}";
    private static string FeedbackKey(long id) => $"desktop_playlist_feedback_{id}";
    private bool PlaylistContinuationEnabled(long id) => _db.GetMeta(ContinuationKey(id))?.Value != "off";
    private bool CanContinuePlaylist => _activePlaylistId is not null && _continuePlaylist && _repeat == 0;

    private void SetPlaylistContext(long? id, IReadOnlyList<Track> tracks)
    {
        _activePlaylistId = id;
        _playlistSeeds = id is null ? [] : tracks.ToArray();
        _playlistVisited.Clear();
        _automaticTracks.Clear();
        _playlistFeedback = [];
        _continuePlaylist = id is { } value && PlaylistContinuationEnabled(value);
        if (id is not { } playlistId || _db.GetMeta(FeedbackKey(playlistId))?.Value is not { Length: <= 128_000 } saved)
            return;
        try
        {
            _playlistFeedback = (JsonSerializer.Deserialize<List<PlaylistFeedback>>(saved) ?? [])
                .Where(entry => entry?.Track is not null).Take(40).ToList();
        }
        catch (JsonException) { }
    }

    private void TogglePlaylistContinuation(long id)
    {
        var enabled = !PlaylistContinuationEnabled(id);
        _db.SetMeta(ContinuationKey(id), enabled ? "on" : "off", double.MaxValue);
        if (_activePlaylistId == id)
        {
            _continuePlaylist = enabled;
            _shuffledNext = -1;
            UpdatePlayer();
        }
        Notify(enabled ? "A playlist vai continuar com faixas parecidas." : "A continuação desta playlist foi desligada.");
    }

    private void RecordPlaylistFeedback(bool ended)
    {
        if (!_continuePlaylist || _activePlaylistId is not { } id || _current is not { } track ||
            _playState is not (PlaybackState.Playing or PlaybackState.Paused) ||
            !ended && _lastPlaylistFeedback == _playRequest) return;
        _lastPlaylistFeedback = _playRequest;
        var duration = PlaybackDuration.TotalSeconds;
        var threshold = duration > 0 ? Math.Min(25, duration * .25) : 25;
        var accepted = ended || PlaybackPosition.TotalSeconds >= threshold;
        _playlistFeedback.RemoveAll(entry => entry.Track.Id == track.Id);
        _playlistFeedback.Insert(0, new(track, accepted));
        if (_playlistFeedback.Count > 40) _playlistFeedback.RemoveAt(_playlistFeedback.Count - 1);
        RunAction(() => _db.SetMeta(FeedbackKey(id), JsonSerializer.Serialize(_playlistFeedback), double.MaxValue));
        LogDiagnostic("Algoritmo", accepted ? "Playlist registrou uma faixa ouvida." : "Playlist registrou um salto cedo.");
    }

    private Track? NextPlaylistTrack() => CanContinuePlaylist ? RankRecommendations(playlist: true).FirstOrDefault() : null;

    private void AppendPlaylistTrack(Track track)
    {
        if (_automaticTracks.Count >= 80)
        {
            var oldest = _queue.FindIndex(item => _automaticTracks.Contains(item.Id) && item.Id != _current?.Id);
            if (oldest >= 0)
            {
                _automaticTracks.Remove(_queue[oldest].Id);
                _queue.RemoveAt(oldest);
                if (_index > oldest) _index--;
                _playedIndices.RemoveAll(index => index == oldest);
                for (var i = 0; i < _playedIndices.Count; i++)
                    if (_playedIndices[i] > oldest) _playedIndices[i]--;
            }
        }
        _automaticTracks.Add(track.Id);
        _queue.Add(track);
        LogDiagnostic("Algoritmo", "Continuação adicionou uma faixa parecida à fila da playlist.");
        PlayAt(_queue.Count - 1);
    }
}
