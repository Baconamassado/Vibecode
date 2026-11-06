using System.Runtime.InteropServices;

namespace Bakobtify.Playback;

/// <summary>
/// Saída de áudio por <c>waveOut</c> (winmm.dll). Lê PCM de um WAV temporário,
/// aplica volume em software e permite seek por offset de amostras.
/// Sem pacote, sem COM, sem buffer grande: ~100 KB de RAM.
/// </summary>
public sealed class WaveOutPlayer : IDisposable
{
    private const int BufferCount = 6;
    private const int BufferMilliseconds = 80;
    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint CallbackNull = 0;
    private const uint WhdrDone = 0x00000001;
    private const uint TimeSamples = 0x0002;

    private readonly object _gate = new();
    private readonly List<Slot> _slots = new();
    private readonly WavInfo _info;
    private FileStream? _file;
    private IntPtr _device;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _paused;
    private bool _readComplete;
    private bool _endedRaised;
    private long _readPos;
    private long _baseFrames;
    private double _volume = 0.8;
    private bool _muted;
    private bool _disposed;

    public event Action? Ended;
    public event Action<string>? Error;

    public TimeSpan Duration { get; }

    public WaveOutPlayer(string wavPath, WavInfo info)
    {
        _info = info;
        Duration = info.Duration;
        _file = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024);
        _readPos = info.DataOffset;

        try
        {
            OpenDevice();
            AllocateSlots();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool IsPlaying => _thread is { IsAlive: true } && !_paused;

    public double Volume
    {
        get
        {
            lock (_gate) return _volume;
        }
        set
        {
            lock (_gate) _volume = Math.Clamp(value, 0, 1);
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
            lock (_gate) _muted = value;
        }
    }

    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                if (_device == IntPtr.Zero) return TimeSpan.Zero;
                var time = new MmTime { wType = TimeSamples };
                long elapsedFrames = 0;
                if (WaveOutGetPosition(_device, ref time, Marshal.SizeOf<MmTime>()) == 0)
                {
                    elapsedFrames = time.wType switch
                    {
                        TimeSamples => time.value,
                        0x0004 => time.value / _info.BlockAlign, // TIME_BYTES
                        0x0001 => (long)time.value * _info.SampleRate / 1000, // TIME_MS
                        _ => 0
                    };
                }
                return TimeSpan.FromSeconds(Math.Min(Duration.TotalSeconds,
                    (_baseFrames + elapsedFrames) / (double)_info.SampleRate));
            }
        }
    }

    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null) return;

        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "Bakobtify.Audio",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_device == IntPtr.Zero) return;
            _paused = true;
            WaveOutPause(_device);
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_device == IntPtr.Zero) return;
            _paused = false;
            WaveOutRestart(_device);
        }
    }

    public void Seek(TimeSpan position)
    {
        var seconds = Math.Clamp(position.TotalSeconds, 0, Duration.TotalSeconds);
        var frame = seconds >= Duration.TotalSeconds
            ? _info.DataLength / _info.BlockAlign
            : (long)(seconds * _info.SampleRate);

        lock (_gate)
        {
            if (_device == IntPtr.Zero) return;
            WaveOutReset(_device);
            foreach (var slot in _slots) Unprepare(slot);
            if (_paused) WaveOutPause(_device);
            _baseFrames = frame;
            _readPos = _info.DataOffset + frame * _info.BlockAlign;
            _readComplete = false;
            _endedRaised = false;
        }
    }

    private void Pump()
    {
        try
        {
            while (!_stop)
            {
                bool ended;
                var didWork = false;
                lock (_gate)
                {
                    foreach (var slot in _slots)
                    {
                        ref var header = ref slot.Header[0];
                        if (slot.Prepared && (Volatile.Read(ref header.dwFlags) & WhdrDone) == 0) continue;
                        Unprepare(slot);
                        if (_paused || _readComplete) continue;
                        var bytes = Fill(slot.Data);
                        if (bytes <= 0)
                        {
                            _readComplete = true;
                            continue;
                        }
                        header.dwBufferLength = (uint)bytes;
                        header.dwFlags = 0;
                        var rc = WaveOutPrepareHeader(_device, ref header, Marshal.SizeOf<WaveHdr>());
                        if (rc != 0) throw new InvalidOperationException($"waveOutPrepareHeader falhou ({rc}).");
                        slot.Prepared = true;
                        rc = WaveOutWrite(_device, ref header, Marshal.SizeOf<WaveHdr>());
                        if (rc != 0) throw new InvalidOperationException($"waveOutWrite falhou ({rc}).");
                        didWork = true;
                    }
                    ended = _readComplete && !_paused && !_endedRaised && _slots.All(slot => !slot.Prepared);
                    if (ended) _endedRaised = true;
                }
                if (ended) Ended?.Invoke();
                if (!didWork) Thread.Sleep(5);
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex.Message);
        }
    }

    private int Fill(byte[] destination)
    {
        if (_file is null) return 0;

        var dataEnd = _info.DataOffset + _info.DataLength;
        if (_readPos >= dataEnd) return 0;

        var remaining = dataEnd - _readPos;
        var wanted = (int)Math.Min(destination.Length, remaining);
        wanted -= wanted % Math.Max(1, _info.BlockAlign);
        if (wanted <= 0) return 0;

        _file.Position = _readPos;
        var read = _file.Read(destination, 0, wanted);
        read -= read % Math.Max(1, _info.BlockAlign);
        _readPos += read;

        ApplyVolume(destination, read);
        return read;
    }

    private void ApplyVolume(byte[] data, int length)
    {
        var gain = _muted ? 0f : (float)Math.Clamp(_volume, 0, 1);
        if (gain >= 0.999f) return;

        for (var i = 0; i + 1 < length; i += 2)
        {
            var sample = (short)(data[i] | (data[i + 1] << 8));
            var scaled = (int)(sample * gain);
            if (scaled > short.MaxValue) scaled = short.MaxValue;
            else if (scaled < short.MinValue) scaled = short.MinValue;
            data[i] = (byte)scaled;
            data[i + 1] = (byte)(scaled >> 8);
        }
    }

    private void Unprepare(Slot slot)
    {
        if (!slot.Prepared) return;
        WaveOutUnprepareHeader(_device, ref slot.Header[0], Marshal.SizeOf<WaveHdr>());
        slot.Prepared = false;
    }

    private void OpenDevice()
    {
        var format = new WaveFormatEx
        {
            wFormatTag = 1,
            nChannels = (ushort)_info.Channels,
            nSamplesPerSec = (uint)_info.SampleRate,
            nAvgBytesPerSec = (uint)_info.ByteRate,
            nBlockAlign = (ushort)_info.BlockAlign,
            wBitsPerSample = 16,
            cbSize = 0,
        };

        var rc = WaveOutOpen(out _device, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, CallbackNull);
        if (rc != 0 || _device == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Não foi possível abrir a saída de áudio (waveOut {rc}).");
        }
    }

    private void AllocateSlots()
    {
        var size = Math.Max(4096, _info.ByteRate * BufferMilliseconds / 1000);
        size -= size % Math.Max(1, _info.BlockAlign);

        for (var i = 0; i < BufferCount; i++)
        {
            // O driver continua acessando os buffers e cabeçalhos após waveOutWrite.
            var data = GC.AllocateArray<byte>(size, pinned: true);
            var header = GC.AllocateArray<WaveHdr>(1, pinned: true);
            unsafe
            {
                fixed (byte* pointer = data)
                    header[0].lpData = (IntPtr)pointer;
            }
            _slots.Add(new Slot { Data = data, Header = header });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop = true;
        if (_thread != Thread.CurrentThread) _thread?.Join();

        lock (_gate)
        {
            if (_device != IntPtr.Zero)
            {
                WaveOutReset(_device);
                foreach (var slot in _slots) Unprepare(slot);
                WaveOutClose(_device);
                _device = IntPtr.Zero;
            }
            _slots.Clear();
            _file?.Dispose();
            _file = null;
        }
    }

    private sealed class Slot
    {
        public required byte[] Data { get; init; }
        public required WaveHdr[] Header { get; init; }
        public bool Prepared;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential, Size = 12)]
    private struct MmTime
    {
        public uint wType;
        public uint value;
    }

    [DllImport("winmm.dll", ExactSpelling = true, EntryPoint = "waveOutOpen")]
    private static extern int WaveOutOpen(
        out IntPtr hwo,
        uint uDeviceID,
        ref WaveFormatEx pwfx,
        IntPtr dwCallback,
        IntPtr dwInstance,
        uint fdwOpen);

    [DllImport("winmm.dll", ExactSpelling = true, EntryPoint = "waveOutClose")]
    private static extern int WaveOutClose(IntPtr hwo);

    [DllImport("winmm.dll", ExactSpelling = true, EntryPoint = "waveOutPrepareHeader")]
    private static extern int WaveOutPrepareHeader(IntPtr hwo, ref WaveHdr pwh, int cbwh);

    [DllImport("winmm.dll", ExactSpelling = true, EntryPoint = "waveOutUnprepareHeader")]
    private static extern int WaveOutUnprepareHeader(IntPtr hwo, ref WaveHdr pwh, int cbwh);

    [DllImport("winmm.dll", ExactSpelling = true, EntryPoint = "waveOutWrite")]
    private static extern int WaveOutWrite(IntPtr hwo, ref WaveHdr pwh, int cbwh);

    [DllImport("winmm.dll", ExactSpelling = true, EntryPoint = "waveOutReset")]
    private static extern int WaveOutReset(IntPtr hwo);

    [DllImport("winmm.dll", ExactSpelling = true, EntryPoint = "waveOutPause")]
    private static extern int WaveOutPause(IntPtr hwo);

    [DllImport("winmm.dll", ExactSpelling = true, EntryPoint = "waveOutRestart")]
    private static extern int WaveOutRestart(IntPtr hwo);

    [DllImport("winmm.dll", ExactSpelling = true, EntryPoint = "waveOutGetPosition")]
    private static extern int WaveOutGetPosition(IntPtr hwo, ref MmTime pmmt, int cbmmt);
}
