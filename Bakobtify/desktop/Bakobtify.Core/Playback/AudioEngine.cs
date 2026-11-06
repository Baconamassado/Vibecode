using Bakobtify.Data;
using Bakobtify.SoundCloud;

namespace Bakobtify.Playback;

public enum PlaybackState
{
    Idle,
    Loading,
    Playing,
    Paused,
    Error,
}

/// <summary>
/// Orquestra a reprodução: resolve o stream, baixa, decodifica para WAV e
/// entrega ao <see cref="WaveOutPlayer"/>. Trocar de faixa cancela tudo.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly SoundCloudClient _client;
    private readonly HttpClient _http;
    private readonly HlsDownloader _hls;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private WaveOutPlayer? _player;
    private string? _wavPath;
    private string? _retainTrackId;
    private Track? _track;
    private bool _paused;
    private double _volume = 0.8;
    private bool _muted;
    private int _generation;
    private sealed record PreparedTrack(string Id, string? LocalPath, CancellationTokenSource Cancel,
        Task<(string Path, bool Preview)> Audio);
    private PreparedTrack? _nextAudio;

    public event Action<PlaybackState>? StateChanged;
    public event Action<string, double>? ProgressChanged;
    public event Action<Track>? TrackChanged;
    public event Action<string>? Error;
    public event Action? Ended;
    public event Action<string, AudioPattern>? AudioAnalyzed;

    public AudioEngine(SoundCloudClient client, HttpClient? http = null)
    {
        _client = client;
        _http = http ?? client.Http;
        _hls = new HlsDownloader(_http);
    }

    public Track? CurrentTrack
    {
        get
        {
            lock (_gate) return _track;
        }
    }

    private volatile PlaybackState _state = PlaybackState.Idle;
    private volatile bool _isPreview;
    public PlaybackState State => _state;
    public bool IsPreview => _isPreview;
    public bool LocalAcceleration { get; set; } = true;

    private string? CachedPath(Track track, string? localPath) =>
        LocalAcceleration && localPath is null ? LocalAudioCache.PathFor(track.Id) : null;

    public TimeSpan Position
    {
        get
        {
            var player = PeekPlayer();
            return player?.Position ?? TimeSpan.Zero;
        }
    }

    public TimeSpan Duration
    {
        get
        {
            var player = PeekPlayer();
            return player?.Duration ?? TimeSpan.Zero;
        }
    }

    public double Volume
    {
        get
        {
            lock (_gate) return _volume;
        }
        set
        {
            lock (_gate)
            {
                _volume = Math.Clamp(value, 0, 1);
                if (_player is not null) _player.Volume = _volume;
            }
        }
    }

    public bool Muted
    {
        get
        {
            lock (_gate) return _muted;
        }
        set
        {
            lock (_gate)
            {
                _muted = value;
                if (_player is not null) _player.Muted = value;
            }
        }
    }

    public string GetPreparationStatus(Track track, string? localPath)
    {
        if (File.Exists(CachedPath(track, localPath))) return "Áudio disponível no cache local";
        if (_nextAudio is not { } next || next.Id != track.Id || next.LocalPath != localPath)
            return "Sem preparação antecipada";
        return next.Audio.Status switch
        {
            TaskStatus.RanToCompletion => "Áudio já preparado",
            TaskStatus.Faulted when next.Audio.Exception?.InnerException is
                PlaybackUnavailableException { ConfirmedByMetadata: true } => "SoundCloud informou que a faixa exige o player integrado",
            TaskStatus.Faulted or TaskStatus.Canceled => "Preparação antecipada não concluída",
            _ => "Preparação antecipada em andamento"
        };
    }

    public async Task PlayAsync(Track track, CancellationToken outer = default, string? localPath = null)
    {
        var pending = _nextAudio is { } next && next.Id == track.Id && next.LocalPath == localPath ? next : null;
        if (pending is not null) _nextAudio = null;
        Stop();

        CancellationTokenSource cts;
        CancellationToken token;
        int generation;
        lock (_gate)
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            token = cts.Token;
            _cts = cts;
            _generation++;
            generation = _generation;
            _track = track;
            _paused = false;
        }

        TrackChanged?.Invoke(track);
        SetState(PlaybackState.Loading, generation);
        Report(generation, localPath is null ? "Resolvendo stream…" : "Abrindo arquivo local…", 0);

        string? wavPath = null;

        try
        {
            var cachedPath = CachedPath(track, localPath);
            var info = cachedPath is not null && File.Exists(cachedPath) ? LocalAudioCache.Read(cachedPath) : null;
            (string Path, bool Preview) prepared;
            if (info is not null)
            {
                if (pending is not null) _ = DiscardAsync(pending);
                Report(generation, "Usando aceleração local…", 0);
                prepared = (cachedPath!, false);
            }
            else if (pending is null)
                prepared = await PrepareAsync(track, localPath, (phase, value) => Report(generation, phase, value), token)
                    .ConfigureAwait(false);
            else
            {
                using (pending.Cancel)
                using (token.Register(() => pending.Cancel.Cancel()))
                {
                    try { prepared = await pending.Audio.ConfigureAwait(false); }
                    catch (Exception ex) when (!token.IsCancellationRequested &&
                        ex is not PlaybackUnavailableException { ConfirmedByMetadata: true })
                    {
                        prepared = await PrepareAsync(track, localPath, (phase, value) => Report(generation, phase, value), token)
                            .ConfigureAwait(false);
                    }
                }
            }
            if (info is null) wavPath = prepared.Path;
            token.ThrowIfCancellationRequested();

            info ??= WavFile.ReadInfo(prepared.Path);
            WaveOutPlayer player;
            try { player = new WaveOutPlayer(prepared.Path, info); }
            catch (InvalidOperationException ex)
            {
                throw new PlaybackUnavailableException(ex.Message);
            }

            player.Ended += () => OnPlayerEnded(generation);
            player.Error += _ => OnError(generation, "A saída de áudio do Windows interrompeu a reprodução.");

            lock (_gate)
            {
                if (generation != _generation || token.IsCancellationRequested)
                {
                    player.Dispose();
                    return;
                }

                player.Volume = _volume;
                player.Muted = _muted;
                player.Play();
                _player = player;
                _wavPath = wavPath;
                _retainTrackId = wavPath is not null && localPath is null && !prepared.Preview && LocalAcceleration
                    ? track.Id : null;
                wavPath = null;
                _isPreview = prepared.Preview;
                _state = PlaybackState.Playing;
            }

            if (generation == Volatile.Read(ref _generation) && State == PlaybackState.Playing)
                StateChanged?.Invoke(PlaybackState.Playing);
            Report(generation, "Tocando", 1);
            if (!prepared.Preview) _ = Task.Run(() => Analyze(track.Id, prepared.Path, info, token), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // troca de faixa ou fechamento
        }
        catch (Exception ex)
        {
            var message = localPath is not null && ex is IOException
                ? "Não foi possível ler ou preparar o arquivo de áudio vinculado."
                : PlaybackMessage(ex, "conexão com o SoundCloud");
            OnError(generation, message);
        }
        finally
        {
            TryDelete(wavPath);
        }
    }

    private async Task<(string Path, bool Preview)> PrepareAsync(Track track, string? localPath,
        Action<string, double>? report, CancellationToken token)
    {
        string? inputPath = null;
        string? wavPath = null;

        try
        {
            string? lastFailure = null;
            var preview = false;
            if (localPath is not null)
            {
                if (!File.Exists(localPath))
                    throw new PlaybackUnavailableException("O arquivo vinculado não foi encontrado. Selecione o arquivo novamente.");
                wavPath = NewWavPath();
                await DecodeAsync(localPath, wavPath, report, token).ConfigureAwait(false);
            }
            else await foreach (var stream in _client.ResolveStreamsAsync(track.Id, token).ConfigureAwait(false))
            {
                var stage = "download do áudio";
                try
                {
                    var phase = stream.Kind == "hls" ? "Baixando HLS…" : "Baixando…";
                    report?.Invoke(phase, 0);
                    var progress = report is null ? null : new Progress<double>(value => report(phase, value));
                    if (stream.Kind == "hls")
                        inputPath = (await _hls.DownloadAsync(stream.Url!, progress, token, stream.Mime)
                            .ConfigureAwait(false)).FilePath;
                    else
                        inputPath = await DownloadAsync(stream.Url!, stream.Mime == "audio/mp4" ? ".m4a" : ".mp3",
                            progress, token).ConfigureAwait(false);
                    stage = "decodificação de áudio";
                    wavPath = NewWavPath();
                    await DecodeAsync(inputPath, wavPath, report, token).ConfigureAwait(false);
                    preview = stream.IsPreview;
                    break;
                }
                catch (Exception ex) when (!token.IsCancellationRequested && ex is SoundCloudException or
                    HttpRequestException or OperationCanceledException or IOException or
                    System.Runtime.InteropServices.COMException or System.Security.Cryptography.CryptographicException)
                {
                    lastFailure = PlaybackMessage(ex, stage);
                    TryDelete(wavPath);
                    wavPath = null;
                }
                finally
                {
                    TryDelete(inputPath);
                    inputPath = null;
                }
            }
            token.ThrowIfCancellationRequested();
            if (wavPath is null)
                throw new PlaybackUnavailableException(lastFailure ?? "O SoundCloud não forneceu áudio para esta faixa.");

            var result = (wavPath, preview);
            wavPath = null;
            return result;
        }
        finally
        {
            TryDelete(inputPath);
            TryDelete(wavPath);
        }
    }

    private static string NewWavPath() => Path.Combine(AppConfig.TempDirectory, $"audio-{Guid.NewGuid():N}.wav");

    private Task DecodeAsync(string input, string output, Action<string, double>? report, CancellationToken token)
    {
        report?.Invoke("Decodificando…", 0);
        return Task.Run(() => MfAudioDecoder.DecodeToWav(input, output,
            report is null ? null : new Progress<double>(value => report("Decodificando…", value)), token), token);
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_player is null || _paused) return;
            _paused = true;
            _player.Pause();
        }

        SetState(PlaybackState.Paused);
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_player is null || !_paused) return;
            _paused = false;
            _player.Resume();
        }

        SetState(PlaybackState.Playing);
    }

    public void TogglePause()
    {
        bool paused;
        lock (_gate)
        {
            if (_player is null) return;
            if (_paused) _player.Resume();
            else _player.Pause();
            _paused = !_paused;
            paused = _paused;
        }

        SetState(paused ? PlaybackState.Paused : PlaybackState.Playing);
    }

    public void Seek(TimeSpan position)
    {
        var player = PeekPlayer();
        player?.Seek(position);
    }

    public void Stop()
    {
        PrepareNext(null, null);
        WaveOutPlayer? player;
        string? wavPath;
        string? retainTrackId;
        int generation;

        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            generation = ++_generation;
            player = _player;
            _player = null;
            wavPath = _wavPath;
            _wavPath = null;
            retainTrackId = _retainTrackId;
            _retainTrackId = null;
            _track = null;
            _isPreview = false;
            _paused = false;
        }

        player?.Dispose();
        if (LocalAcceleration && wavPath is not null && retainTrackId is not null)
            LocalAudioCache.Store(wavPath, retainTrackId);
        TryDelete(wavPath);
        SetState(PlaybackState.Idle, generation);
    }

    public void PrepareNext(Track? track, string? localPath)
    {
        if (track is not null && File.Exists(CachedPath(track, localPath))) track = null;
        if (_nextAudio is { } existing)
        {
            if (track?.Id == existing.Id && localPath == existing.LocalPath) return;
            _nextAudio = null;
            _ = DiscardAsync(existing);
        }
        if (track is null) return;
        var cancel = new CancellationTokenSource();
        var token = cancel.Token;
        _nextAudio = new(track.Id, localPath, cancel,
            Task.Run(async () =>
            {
                var prepared = await PrepareAsync(track, localPath, null, token).ConfigureAwait(false);
                if (!prepared.Preview) Analyze(track.Id, prepared.Path, null, token);
                return prepared;
            }, token));
    }

    private void Analyze(string trackId, string path, WavInfo? info, CancellationToken token)
    {
        try
        {
            if (AudioAnalyzed is { } publish &&
                AudioPattern.Measure(path, info ?? WavFile.ReadInfo(path), token) is { } pattern &&
                !token.IsCancellationRequested)
                publish(trackId, pattern);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
            OperationCanceledException) { }
    }

    private static async Task DiscardAsync(PreparedTrack pending)
    {
        using var cancel = pending.Cancel;
        cancel.Cancel();
        try { TryDelete((await pending.Audio.ConfigureAwait(false)).Path); }
        catch (Exception) { }
    }

    private WaveOutPlayer? PeekPlayer()
    {
        lock (_gate) return _player;
    }

    private void OnPlayerEnded(int generation)
    {
        lock (_gate)
        {
            if (generation != _generation || _player is null) return;
        }

        if (SetState(PlaybackState.Idle, generation)) Ended?.Invoke();
    }

    private static string PlaybackMessage(Exception error, string stage) => error switch
    {
        SoundCloudException => error.Message,
        HttpRequestException { StatusCode: { } code } => $"Falha na etapa de {stage} (HTTP {(int)code}).",
        HttpRequestException or OperationCanceledException => $"A conexão foi interrompida durante a etapa de {stage}.",
        System.Runtime.InteropServices.COMException => $"O Windows recusou este formato de áudio (0x{error.HResult:X8}).",
        System.Security.Cryptography.CryptographicException => "O SoundCloud entregou um segmento de áudio criptografado inválido.",
        InvalidDataException => "O SoundCloud entregou um arquivo de áudio ou uma playlist inválida.",
        IOException or UnauthorizedAccessException => "O cache de áudio está inacessível ou não há espaço para a faixa.",
        _ => "A reprodução foi interrompida por um erro no player."
    };

    private void OnError(int generation, string message)
    {
        if (SetState(PlaybackState.Error, generation)) Error?.Invoke(message);
    }

    private void Report(int generation, string phase, double value)
    {
        if (generation != Volatile.Read(ref _generation)) return;
        ProgressChanged?.Invoke(phase, Math.Clamp(value, 0, 1));
    }

    private bool SetState(PlaybackState state, int? generation = null)
    {
        lock (_gate)
        {
            if (generation is not null && generation != _generation) return false;
            _state = state;
        }
        StateChanged?.Invoke(state);
        return true;
    }

    private async Task<string> DownloadAsync(
        string url,
        string extension,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        ct = timeout.Token;
        var path = Path.Combine(AppConfig.TempDirectory, $"in-{Guid.NewGuid():N}{extension}");

        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            if (total > HlsDownloader.MaxTrackBytes)
                throw new InvalidDataException("O áudio excedeu o limite de download.");
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);

            var buffer = new byte[64 * 1024];
            long written = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                written += read;
                if (written > HlsDownloader.MaxTrackBytes)
                    throw new InvalidDataException("O áudio excedeu o limite de download.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                if (total is > 0) progress?.Report(written / (double)total.Value);
            }

            progress?.Report(1);
            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // melhor esforço: o arquivo fica no cache de áudio
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
