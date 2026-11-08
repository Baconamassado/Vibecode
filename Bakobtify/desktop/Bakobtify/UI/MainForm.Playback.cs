using System.Globalization;
using Bakobtify.Data;
using Bakobtify.Playback;

namespace Bakobtify.UI;

internal sealed partial class MainForm
{
    private EmbeddedSoundCloud? _embeddedPlayer;
    private EmbeddedSoundCloud? _warmEmbeddedPlayer;
    private bool _embeddedLogged;
    private const string EmbeddedPlayerSetting = "desktop_embedded_player";
    private const string EmbeddedGpuSetting = "desktop_embedded_gpu";
    private const string EmbeddedIdleCloseSetting = "desktop_embedded_idle_close";
    private const string VolumeSetting = "desktop_volume";
    private bool _disableEmbeddedGpu;
    private bool _allowEmbeddedPlayer;
    private bool _closeIdleEmbeddedPlayer;
    private long? _embeddedPausedAt;
    private (TimeSpan Position, TimeSpan Duration)? _parkedEmbedded;
    private bool _playingLocal;
    private int _shuffledNext = -1;
    private TimeSpan PlaybackPosition => _embeddedPlayer?.Position ?? _parkedEmbedded?.Position ?? _audio.Position;
    private TimeSpan PlaybackDuration => _embeddedPlayer?.Duration ?? _parkedEmbedded?.Duration ?? _audio.Duration;

    private void PlayList(IReadOnlyList<Track> tracks, int index = 0) => StartQueue(tracks, index, null);

    private void StartQueue(IReadOnlyList<Track> tracks, int index, long? playlistId)
    {
        if (index < 0 || index >= tracks.Count) return;
        SetPlaylistContext(playlistId, tracks);
        _queue.Clear();
        _queue.AddRange(tracks);
        _playedIndices.Clear();
        PlayAt(index, false);
    }

    private void PlayRow(IReadOnlyList<Track> tracks, int index, long? playlistId = null)
    {
        if (_activePlaylistId == playlistId && _current?.Id == tracks[index].Id &&
            _playState is PlaybackState.Playing or PlaybackState.Paused)
            TogglePlayback();
        else
            StartQueue(tracks, index, playlistId);
    }

    private void PlayAt(int index, bool remember = true)
    {
        if (_closing || index < 0 || index >= _queue.Count) return;
        _relatedRequest?.Cancel();
        if (_embeddedPlayer is { } active)
        {
            _embeddedPlayer = null;
            _embeddedPausedAt = null;
            active.SuspendTrack();
            if (active.UsesDisabledGpu == _disableEmbeddedGpu) _warmEmbeddedPlayer = active;
            else active.Dispose();
        }
        _parkedEmbedded = null;
        if (remember && _index >= 0 && index != _index) _playedIndices.Add(_index);
        _index = index;
        _shuffledNext = -1;
        _current = _queue[index];
        _playlistVisited.Add(_current.Id);
        _switchStartedAt = Environment.TickCount64;
        _lastLoggedPhase = null;
        LogPlayback($"Carregando faixa {index + 1} de {_queue.Count}");
        _playState = PlaybackState.Loading;
        _phase = "Preparando áudio…";
        _notice.Visible = false;
        var request = ++_playRequest;
        _ = PlayCurrentAsync(_current, request);
        UpdatePlayer();
        RenderQueue();
        _content?.Invalidate(true);
    }

    private async Task PlayCurrentAsync(Track track, long request)
    {
        string? localPath;
        _playingLocal = false;
        try { localPath = _db.GetLocalAudio(track.Id); }
        catch (Exception)
        {
            _audio.PrepareNext(null, null);
            AudioError("Não foi possível consultar o arquivo vinculado no banco local.");
            return;
        }
        _playingLocal = localPath is not null;
        LogPlayback(_audio.GetPreparationStatus(track, localPath));
        await _audio.PlayAsync(track, _lifetime.Token, localPath);
        if (_closing || request != _playRequest) return;
        _playState = _audio.State;
        UpdatePlayer();
        UpdateStatus();
        if (_playState != PlaybackState.Playing) return;
        LogPlayback("Reprodução iniciada · player nativo");
        CloseEmbeddedPlayer();
        RecordPlayback(track);
    }

    private void RecordPlayback(Track track) => RunAction(() =>
        {
            _db.AddHistory(track);
            _history = _db.ListHistory();
            if (_route.Page is Page.History or Page.Home) Render();
            _relatedLoad = LoadRelatedRecommendationsAsync(track);
        });

    private void TogglePlayback()
    {
        if (_embeddedPlayer is { } widget)
        {
            if (!widget.Playing) _embeddedPausedAt = null;
            widget.Toggle();
            return;
        }
        if (_parkedEmbedded is { } parked && _current is { } track && OfficialTrackUri(track) is { } uri)
        {
            _parkedEmbedded = null;
            StartEmbeddedPlayer(uri, parked);
            return;
        }
        switch (_playState)
        {
            case PlaybackState.Playing:
            case PlaybackState.Paused:
                _audio.TogglePause();
                _playState = _audio.State;
                break;
            case PlaybackState.Loading:
                LogPlayback("Carregamento cancelado");
                ++_playRequest;
                _audio.Stop();
                CloseEmbeddedPlayer();
                _playState = PlaybackState.Idle;
                break;
            default:
                PlayAt(Math.Max(0, _index));
                break;
        }
        UpdatePlayer();
        _content?.Invalidate(true);
    }

    private long? _playlistWaitRequest;
    private bool _playlistEndedWhileWaiting;

    private async void Advance(bool ended)
    {
        if (_playlistWaitRequest == _playRequest)
        {
            _playlistEndedWhileWaiting |= ended;
            return;
        }
        if (_index < 0)
        {
            PlayAt(0);
            return;
        }
        RecordPlaylistFeedback(ended);
        if (ended && _repeat == 2) { PlayAt(_index); return; }
        var next = NextIndex();
        if (next >= 0) { PlayAt(next); return; }
        if (CanContinuePlaylist)
        {
            var request = _playRequest;
            if (NextPlaylistTrack() is null && _relatedLoad is { IsCompleted: false } loading)
            {
                _playlistWaitRequest = request;
                _playlistEndedWhileWaiting = false;
                try { await loading.WaitAsync(TimeSpan.FromSeconds(5), _lifetime.Token); }
                catch (TimeoutException) { }
                catch (OperationCanceledException) when (_closing) { }
                finally
                {
                    if (_playlistWaitRequest == request) _playlistWaitRequest = null;
                }
                if (_closing || request != _playRequest) return;
                if (_playlistEndedWhileWaiting)
                {
                    ended = true;
                    RecordPlaylistFeedback(true);
                }
                next = NextIndex();
                if (next >= 0) { PlayAt(next); return; }
            }
            if (NextPlaylistTrack() is { } track)
            {
                AppendPlaylistTrack(track);
                return;
            }
            if (!ended) Notify("Ainda não há faixas parecidas para continuar a playlist.");
        }
        if (ended)
        {
            CloseEmbeddedPlayer();
            _playState = PlaybackState.Idle;
            _audio.Stop();
            UpdatePlayer();
            _content?.Invalidate(true);
        }
    }

    private void Previous()
    {
        if (_playedIndices.Count > 0)
        {
            var previous = _playedIndices[^1];
            _playedIndices.RemoveAt(_playedIndices.Count - 1);
            PlayAt(previous, false);
        }
        else if (_index > 0) PlayAt(_index - 1, false);
    }

    private int NextIndex()
    {
        if (_shuffle && CanContinuePlaylist)
        {
            if (_shuffledNext >= 0 && !_playlistVisited.Contains(_queue[_shuffledNext].Id)) return _shuffledNext;
            var unplayed = Enumerable.Range(0, _queue.Count).Where(i => !_playlistVisited.Contains(_queue[i].Id)).ToArray();
            return _shuffledNext = unplayed.Length == 0 ? -1 : unplayed[Random.Shared.Next(unplayed.Length)];
        }
        if (_shuffle && _queue.Count > 1)
        {
            if (_shuffledNext < 0)
            {
                var next = Random.Shared.Next(_queue.Count - 1);
                _shuffledNext = next >= _index ? next + 1 : next;
            }
            return _shuffledNext;
        }
        _shuffledNext = -1;
        if (_index + 1 < _queue.Count) return _index + 1;
        return _repeat != 0 && _queue.Count > 0 ? 0 : -1;
    }

    private void PrepareNextTrack()
    {
        var index = NextIndex();
        var track = index < 0 ? NextPlaylistTrack() : index == _index ? null : _queue[index];
        if (track is null)
        {
            _audio.PrepareNext(null, null);
            return;
        }
        string? localPath;
        try { localPath = _db.GetLocalAudio(track.Id); }
        catch (Exception) { return; }
        _audio.PrepareNext(track, localPath);
    }

    private void SeekPlayback(TimeSpan position)
    {
        if (_embeddedPlayer is { } widget) widget.Seek(position);
        else if (_parkedEmbedded is { } parked)
            _parkedEmbedded = (TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, parked.Duration.Ticks)), parked.Duration);
        else _audio.Seek(position);
    }

    private void AudioProgress(string phase, double progress) => OnUi(() =>
    {
        if (_playState != PlaybackState.Loading) return;
        if (_lastLoggedPhase != phase)
        {
            _lastLoggedPhase = phase;
            LogPlayback(phase);
        }
        _phase = progress > 0 ? $"{phase} {progress:P0}" : phase;
        _nowArtist.Text = _phase;
    });

    private void AudioError(string message) => OnUi(() =>
    {
        ++_playRequest;
        _audio.Stop();
        if (!_playingLocal && _allowEmbeddedPlayer && EmbeddedSoundCloud.Available &&
            _current is { } track && OfficialTrackUri(track) is { } uri)
        {
            StartEmbeddedPlayer(uri);
            return;
        }
        _playState = PlaybackState.Error;
        LogPlayback("Falha na reprodução nativa");
        CloseEmbeddedPlayer();
        UpdatePlayer();
        Notify(message, seconds: 9);
    });

    private void StartEmbeddedPlayer(Uri uri, (TimeSpan Position, TimeSpan Duration)? resume = null)
    {
        var widget = _warmEmbeddedPlayer ?? CreateEmbeddedPlayer();
        LogPlayback(_warmEmbeddedPlayer is null ? "Abrindo player integrado" : "Reutilizando player integrado");
        _warmEmbeddedPlayer = null;
        _embeddedPlayer = widget;
        _playState = PlaybackState.Paused;
        _embeddedLogged = resume is not null;
        widget.SetVolume(_audio.Muted ? 0 : _audio.Volume);
        UpdatePlayer();
        _ = widget.LoadAsync(uri, resume?.Position ?? TimeSpan.Zero);
    }

    private EmbeddedSoundCloud CreateEmbeddedPlayer()
    {
        var widget = new EmbeddedSoundCloud(_disableEmbeddedGpu);
        widget.Changed += () =>
        {
            if (_embeddedPlayer != widget) return;
            _embeddedPausedAt = widget.Playing ? null : _embeddedLogged ? Environment.TickCount64 : null;
            _playState = widget.Playing ? PlaybackState.Playing : PlaybackState.Paused;
            UpdatePlayer();
            _content?.Invalidate(true);
            if (!_embeddedLogged && widget.Playing && _current is { } track)
            {
                _embeddedLogged = true;
                LogPlayback("Reprodução iniciada · player integrado");
                RecordPlayback(track);
            }
        };
        widget.Ended += () => OnUi(() => { if (_embeddedPlayer == widget) Advance(true); });
        widget.Failed += reason => OnUi(() =>
        {
            if (_embeddedPlayer != widget) return;
            CloseEmbeddedPlayer();
            _playState = PlaybackState.Error;
            LogPlayback("Falha no player integrado");
            UpdatePlayer();
            Notify(reason, seconds: 9);
        });
        _player.Controls.Add(widget);
        widget.SendToBack();
        return widget;
    }

    private void CloseEmbeddedPlayer()
    {
        var widget = _embeddedPlayer;
        var warm = _warmEmbeddedPlayer;
        _embeddedPlayer = null;
        _warmEmbeddedPlayer = null;
        _embeddedPausedAt = null;
        widget?.Dispose();
        warm?.Dispose();
    }

    private void ReleasePausedEmbeddedPlayer()
    {
        if (!_closeIdleEmbeddedPlayer || _embeddedPlayer is not { Playing: false } widget ||
            _embeddedPausedAt is not { } pausedAt ||
            Environment.TickCount64 - pausedAt < 30_000) return;
        _parkedEmbedded = (widget.Position, widget.Duration);
        CloseEmbeddedPlayer();
        UpdatePlayer();
    }

    private void ToggleEmbeddedIdleClose()
    {
        var enabled = !_closeIdleEmbeddedPlayer;
        _db.SetMeta(EmbeddedIdleCloseSetting, enabled ? "on" : "off", double.MaxValue);
        _closeIdleEmbeddedPlayer = enabled;
        if (enabled && _embeddedPausedAt.HasValue) _embeddedPausedAt = Environment.TickCount64;
    }

    private void ToggleEmbeddedPlayerSetting()
    {
        var enabled = !_allowEmbeddedPlayer;
        _db.SetMeta(EmbeddedPlayerSetting, enabled ? "on" : "off", double.MaxValue);
        _allowEmbeddedPlayer = enabled;
        if (!enabled && _warmEmbeddedPlayer is not null) CloseEmbeddedPlayer();
        if (!enabled && (_embeddedPlayer is not null || _parkedEmbedded is not null))
        {
            CloseEmbeddedPlayer();
            _parkedEmbedded = null;
            _playState = PlaybackState.Idle;
            UpdatePlayer();
        }
    }

    private void ToggleEmbeddedGpuSetting()
    {
        var disableGpu = !_disableEmbeddedGpu;
        _db.SetMeta(EmbeddedGpuSetting, disableGpu ? "off" : "on", double.MaxValue);
        _disableEmbeddedGpu = disableGpu;
        _warmEmbeddedPlayer?.Dispose();
        _warmEmbeddedPlayer = null;
        Notify(disableGpu
            ? "GPU desligada na próxima faixa que abrir o player web."
            : "GPU ligada na próxima faixa que abrir o player web.");
    }

    private void AudioEnded() => OnUi(() =>
    {
        if (_audio.State == PlaybackState.Idle && _playState == PlaybackState.Playing) Advance(true);
    });

    private void OnUi(Action action)
    {
        var request = Interlocked.Read(ref _playRequest);
        if (!IsHandleCreated || IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                if (!_closing && request == _playRequest) action();
            });
        }
        catch (InvalidOperationException) { }
    }

    private void UpdatePlayer()
    {
        _nowTitle.Text = _current?.Title ?? "Nenhuma faixa";
        var offerLocalAudio = _playState == PlaybackState.Error && _current is not null;
        _localAudio.Visible = offerLocalAudio;
        _nowArtist.Visible = !offerLocalAudio;
        _nowArtist.Cursor = CanInspectNowArtist ? Cursors.Hand : Cursors.Default;
        _nowArtist.ForeColor = CanInspectNowArtist ? Theme.Accent : Theme.Muted;
        _nowArtist.Text = _playState switch
        {
            PlaybackState.Loading => _phase,
            PlaybackState.Error => "Faixa indisponível",
            _ when _audio.IsPreview => $"Prévia · {_current?.ArtistOrUnknown}",
            _ when _embeddedPlayer is not null => "SoundCloud integrado",
            _ when _parkedEmbedded is not null => "Pausado · player liberado",
            _ => _current?.ArtistOrUnknown ?? "Escolha uma faixa"
        };
        _play.Glyph = _playState switch
        {
            PlaybackState.Playing => "pause",
            PlaybackState.Loading => "close",
            _ => "play"
        };
        _play.Enabled = _queue.Count > 0;
        _prev.Enabled = _index > 0 || _playedIndices.Count > 0;
        _next.Enabled = _index + 1 < _queue.Count || (_index >= 0 && (_repeat != 0 || _shuffle && _queue.Count > 1 || CanContinuePlaylist));
        _like.Enabled = _current is not null;
        _like.Glyph = _current is { } track && IsFavorite(track) ? "liked" : "heart";
        _like.ForeColor = _like.Glyph == "liked" ? Theme.Accent : Theme.Muted;
        _mute.Glyph = _audio.Muted ? "mute" : "volume";
        _repeatButton.Glyph = _repeat == 2 ? "repeat1" : "repeat";
        _repeatButton.ForeColor = _repeat > 0 ? Theme.Accent : Theme.Muted;
        foreach (var control in new[] { _play, _cover, _like, _mute, _repeatButton }) control.Invalidate();
        UpdateTaskbar();
        if (_playState == PlaybackState.Playing) PrepareNextTrack();
        UpdateClock();
    }

    private void UpdateClock()
    {
        ReleasePausedEmbeddedPlayer();
        var duration = _playState is PlaybackState.Playing or PlaybackState.Paused ? PlaybackDuration : TimeSpan.Zero;
        var position = duration > TimeSpan.Zero ? PlaybackPosition : TimeSpan.Zero;
        _presence.Update(_presenceEnabled ? DiscordApplicationId : null,
            _presenceEnabled && _playState == PlaybackState.Playing ? _current : null, position, duration);
        _seek.Enabled = duration > TimeSpan.Zero;
        _seek.Value = duration > TimeSpan.Zero ? position / duration : 0;
        _elapsed.Text = FormatTime(_seek.Dragging ? duration * _seek.Preview : position);
        _duration.Text = duration > TimeSpan.Zero ? FormatTime(duration) : _current?.DurationText ?? "0:00";
        if (_notice.Visible && DateTime.Now > _noticeUntil) _notice.Visible = false;
    }

    private static string FormatTime(TimeSpan time) => $"{(long)time.TotalMinutes}:{time.Seconds:00}";

    private void LoadVolumeSettings()
    {
        if (_db.GetMeta(VolumeSetting)?.Value is not { } saved) return;
        var parts = saved.Split(':', 2);
        if (parts.Length != 2 || parts[0] is not ("on" or "off") ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var volume) ||
            !double.IsFinite(volume) || volume is < 0 or > 1)
            return;

        _audio.Volume = volume;
        _audio.Muted = parts[0] == "off";
    }

    private void SaveVolumeSettings() => RunAction(() =>
    {
        var sound = _audio.Muted ? "off" : "on";
        var volume = _audio.Volume.ToString("R", CultureInfo.InvariantCulture);
        _db.SetMeta(VolumeSetting, $"{sound}:{volume}", double.MaxValue);
    });

    private void SetVolume(double value)
    {
        _audio.Volume = value;
        _audio.Muted = false;
        _embeddedPlayer?.SetVolume(_audio.Volume);
        _volume.Value = _audio.Volume;
        _mute.Glyph = "volume";
        _mute.Invalidate();
        SaveVolumeSettings();
    }

    private void ToggleMute()
    {
        _audio.Muted = !_audio.Muted;
        _embeddedPlayer?.SetVolume(_audio.Muted ? 0 : _audio.Volume);
        _volume.Value = _audio.Muted ? 0 : _audio.Volume;
        UpdatePlayer();
        SaveVolumeSettings();
    }

    private void CycleRepeat()
    {
        _repeat = (_repeat + 1) % 3;
        UpdatePlayer();
        Notify(_repeat switch { 1 => "Repetir a fila", 2 => "Repetir esta faixa", _ => "Repetição desligada" });
    }

    private void ToggleQueue()
    {
        _queuePane.Visible = !_queuePane.Visible;
        _showQueue.ForeColor = _queuePane.Visible ? Theme.Accent : Theme.Muted;
        if (_queuePane.Visible)
        {
            _queuePane.BringToFront();
            _notice.BringToFront();
            RenderQueue();
        }
        Arrange();
    }

    private void RenderQueue()
    {
        if (!_queuePane.Visible) return;
        _queueClear.Enabled = _queue.Count > 0;
        var scroll = _queueItems.AutoScrollPosition;
        ClearControls(_queueItems);
        if (_queue.Count == 0)
        {
            var empty = Theme.Label("A fila está vazia.", 14, Theme.Muted);
            ApplyPaintScale(empty);
            empty.Size = new Size(Pixels(252), Pixels(38));
            _queueItems.Controls.Add(empty);
            return;
        }
        for (var i = 0; i < _queue.Count; i++)
        {
            var index = i;
            var track = _queue[i];
            var item = ButtonAt(_queueItems, $"{track.Title} — {track.ArtistOrUnknown}", "",
                () => { if (_index == index) TogglePlayback(); else PlayAt(index); });
            ApplyPaintScale(item);
            item.Size = new Size(Pixels(250), Pixels(46));
            item.FillColor = index == _index ? Theme.Surface : Color.Transparent;
            item.DrawContent = (g, r) =>
            {
                _artwork.Draw(item, g, new Rectangle(5, 5, 36, 36), track.Artwork, Palette(index), 5);
                Theme.Write(g, track.Title, new Rectangle(49, 5, r.Width - 54, 19),
                    Theme.Medium(14), index == _index ? Theme.Accent : Theme.Text);
                Theme.Write(g, track.ArtistOrUnknown, new Rectangle(49, 23, r.Width - 54, 17), Theme.Font(12), Theme.Muted);
            };
            item.MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Right)
                    ShowTrackMenu(item, e.Location, track, removeFromQueue: () => RemoveFromQueue(index, track));
            };
        }
        _queueItems.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
    }

    private void AddToQueue(Track track)
    {
        _queue.Add(track);
        RenderQueue();
        UpdatePlayer();
        Notify("Faixa adicionada à fila.");
    }

    private void ClearQueue()
    {
        if (_queue.Count == 0) return;
        _queue.Clear();
        _playedIndices.Clear();
        _shuffledNext = -1;
        SetPlaylistContext(null, []);
        StopQueuePlayback();
        RefreshQueue();
    }

    private void RemoveFromQueue(int index, Track track)
    {
        if (index >= _queue.Count || !ReferenceEquals(_queue[index], track)) return;
        _queue.RemoveAt(index);
        _shuffledNext = -1;
        _playedIndices.RemoveAll(previous => previous == index);
        for (var i = 0; i < _playedIndices.Count; i++)
            if (_playedIndices[i] > index) _playedIndices[i]--;
        if (_queue.All(item => item.Id != track.Id)) _automaticTracks.Remove(track.Id);

        if (index == _index)
        {
            StopQueuePlayback();
            if (index < _queue.Count)
            {
                PlayAt(index, false);
                return;
            }
        }
        else if (index < _index) _index--;

        if (_queue.Count == 0) SetPlaylistContext(null, []);
        if (_playState != PlaybackState.Playing) _audio.PrepareNext(null, null);
        RefreshQueue();
    }

    private void StopQueuePlayback()
    {
        ++_playRequest;
        _relatedRequest?.Cancel();
        _relatedLoad = null;
        _playlistEndedWhileWaiting = false;
        _audio.Stop();
        CloseEmbeddedPlayer();
        _parkedEmbedded = null;
        _index = -1;
        _current = null;
        _playState = PlaybackState.Idle;
    }

    private void RefreshQueue()
    {
        UpdatePlayer();
        RenderQueue();
        _content?.Invalidate(true);
    }
}
