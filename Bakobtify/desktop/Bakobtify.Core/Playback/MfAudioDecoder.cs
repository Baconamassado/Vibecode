using System.Runtime.InteropServices;
using System.Text;

namespace Bakobtify.Playback;

public sealed record DecodedAudio(
    string WavPath,
    int SampleRate,
    int Channels,
    long Frames,
    TimeSpan Duration)
{
    public int BlockAlign => Channels * 2;

    public int ByteRate => SampleRate * BlockAlign;
}

/// <summary>
/// Decodifica MP3/AAC/fMP4 para PCM 16-bit usando o Media Foundation que já
/// existe no Windows (Source Reader + decodificador MFT). Zero dependências.
/// </summary>
public static class MfAudioDecoder
{
    private const uint AllStreams = MfGuids.MF_SOURCE_READER_ALL_STREAMS;
    private const uint FirstAudio = MfGuids.MF_SOURCE_READER_FIRST_AUDIO_STREAM;

    public static DecodedAudio DecodeToWav(
        string inputPath,
        string wavPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Arquivo de áudio não encontrado.", inputPath);
        }

        MfInterop.EnsureStarted();

        IMFSourceReader? reader = null;
        try
        {
            MfInterop.Check(MfInterop.MFCreateSourceReaderFromURL(
                Path.GetFullPath(inputPath), null, out reader));

            MfInterop.Check(reader.SetStreamSelection(AllStreams, false));
            MfInterop.Check(reader.SetStreamSelection(FirstAudio, true));

            var rate = 44100u;
            var channels = 2u;
            if (TryGetNativeFormat(reader, out var nativeRate, out var nativeChannels))
            {
                rate = nativeRate;
                channels = nativeChannels;
            }

            ConfigurePcm(reader, rate, channels);
            var durationHns = TryGetDuration(reader);

            using var output = new FileStream(
                wavPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 64 * 1024);

            WriteWavHeader(output, 0, (int)rate, (int)channels);

            long dataBytes = 0;
            var buffer = new byte[64 * 1024];

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var hr = reader.ReadSample(FirstAudio, 0, out _, out var flags, out var timestamp, out var sample);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                if ((flags & MfGuids.MF_SOURCE_READERF_ERROR) != 0)
                {
                    if (sample is not null) Marshal.ReleaseComObject(sample);
                    throw new InvalidDataException("O Windows não conseguiu decodificar o áudio.");
                }

                if (sample is null)
                {
                    if ((flags & MfGuids.MF_SOURCE_READERF_ENDOFSTREAM) != 0) break;
                    continue;
                }

                try
                {
                    var length = CopySample(sample, ref buffer);
                    if (length > 0)
                    {
                        output.Write(buffer, 0, length);
                        dataBytes += length;
                    }

                    if (durationHns > 0)
                    {
                        progress?.Report(Math.Clamp(timestamp / (double)durationHns, 0, 1));
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(sample);
                }

                if ((flags & MfGuids.MF_SOURCE_READERF_ENDOFSTREAM) != 0) break;
            }

            output.Flush();
            output.Seek(0, SeekOrigin.Begin);
            WriteWavHeader(output, dataBytes, (int)rate, (int)channels);
            output.Flush();

            var blockAlign = (int)channels * 2;
            var frames = blockAlign > 0 ? dataBytes / blockAlign : 0;
            if (frames == 0) throw new InvalidDataException("O stream não contém amostras de áudio.");
            var duration = rate > 0 ? TimeSpan.FromSeconds(frames / (double)rate) : TimeSpan.Zero;

            progress?.Report(1);
            return new DecodedAudio(wavPath, (int)rate, (int)channels, frames, duration);
        }
        finally
        {
            if (reader is not null) Marshal.ReleaseComObject(reader);
        }
    }

    private static bool TryGetNativeFormat(IMFSourceReader reader, out uint rate, out uint channels)
    {
        rate = 0;
        channels = 0;

        IMFMediaType? type = null;
        try
        {
            var hr = reader.GetCurrentMediaType(FirstAudio, out type);
            if (hr < 0 || type is null)
            {
                if (type is not null)
                {
                    Marshal.ReleaseComObject(type);
                    type = null;
                }

                hr = reader.GetNativeMediaType(FirstAudio, 0, out type);
            }

            if (hr < 0 || type is null) return false;

            type.GetUINT32(MfGuids.MF_MT_AUDIO_SAMPLES_PER_SECOND, out rate);
            type.GetUINT32(MfGuids.MF_MT_AUDIO_NUM_CHANNELS, out channels);
            return rate > 0 && channels > 0;
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            if (type is not null) Marshal.ReleaseComObject(type);
        }
    }

    private static void ConfigurePcm(IMFSourceReader reader, uint rate, uint channels)
    {
        MfInterop.Check(MfInterop.MFCreateMediaType(out var pcm));
        try
        {
            pcm.SetGUID(MfGuids.MF_MT_MAJOR_TYPE, MfGuids.MFAudioFormat_Audio);
            pcm.SetGUID(MfGuids.MF_MT_SUBTYPE, MfGuids.MFAudioFormat_PCM);
            pcm.SetUINT32(MfGuids.MF_MT_AUDIO_NUM_CHANNELS, channels);
            pcm.SetUINT32(MfGuids.MF_MT_AUDIO_SAMPLES_PER_SECOND, rate);
            pcm.SetUINT32(MfGuids.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            pcm.SetUINT32(MfGuids.MF_MT_AUDIO_BLOCK_ALIGNMENT, channels * 2);
            pcm.SetUINT32(MfGuids.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, rate * channels * 2);

            MfInterop.Check(reader.SetCurrentMediaType(FirstAudio, IntPtr.Zero, pcm));
        }
        finally
        {
            Marshal.ReleaseComObject(pcm);
        }
    }

    private static long TryGetDuration(IMFSourceReader reader)
    {
        var hr = reader.GetPresentationAttribute(
            MfGuids.MF_SOURCE_READER_MEDIASOURCE, MfGuids.MF_PD_DURATION, out var value);

        if (hr < 0) return 0;
        if (value.vt is MfGuids.VT_I8 or MfGuids.VT_UI8) return value.hVal;
        return 0;
    }

    private static int CopySample(IMFSample sample, ref byte[] buffer)
    {
        MfInterop.Check(sample.ConvertToContiguousBuffer(out var mediaBuffer));
        try
        {
            MfInterop.Check(mediaBuffer.Lock(out var pointer, out _, out var currentLength));
            try
            {
                var length = (int)currentLength;
                if (length <= 0) return 0;
                if (buffer.Length < length) buffer = new byte[length];

                Marshal.Copy(pointer, buffer, 0, length);
                return length;
            }
            finally
            {
                mediaBuffer.Unlock();
            }
        }
        finally
        {
            Marshal.ReleaseComObject(mediaBuffer);
        }
    }

    private static void WriteWavHeader(Stream stream, long dataBytes, int sampleRate, int channels)
    {
        var blockAlign = channels * 2;
        var byteRate = sampleRate * blockAlign;

        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write((uint)(36 + dataBytes));
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16u);
        writer.Write((ushort)1);
        writer.Write((ushort)channels);
        writer.Write((uint)sampleRate);
        writer.Write((uint)byteRate);
        writer.Write((ushort)blockAlign);
        writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write((uint)dataBytes);
        writer.Flush();
    }
}
