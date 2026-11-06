using System.Text;

namespace Bakobtify.Playback;

public sealed record WavInfo(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    long DataOffset,
    long DataLength)
{
    public int BlockAlign => Channels * BitsPerSample / 8;

    public int ByteRate => SampleRate * BlockAlign;

    public long FrameCount => BlockAlign > 0 ? DataLength / BlockAlign : 0;

    public TimeSpan Duration =>
        SampleRate > 0 ? TimeSpan.FromSeconds(FrameCount / (double)SampleRate) : TimeSpan.Zero;
}

public static class WavFile
{
    public static WavInfo ReadInfo(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        if (ReadTag(reader) != "RIFF") throw new InvalidDataException("Arquivo WAV inválido (RIFF).");
        reader.ReadUInt32();
        if (ReadTag(reader) != "WAVE") throw new InvalidDataException("Arquivo WAV inválido (WAVE).");

        var sampleRate = 0;
        var channels = 0;
        var bits = 0;
        long dataOffset = -1;
        long dataLength = 0;

        while (stream.Position + 8 <= stream.Length)
        {
            var id = ReadTag(reader);
            var size = reader.ReadUInt32();
            var payload = stream.Position;

            if (id == "fmt ")
            {
                reader.ReadUInt16(); // formato (PCM = 1)
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32(); // byte rate
                reader.ReadUInt16(); // block align
                bits = reader.ReadUInt16();
            }
            else if (id == "data")
            {
                dataOffset = payload;
                dataLength = size == uint.MaxValue ? stream.Length - payload : size;
                if (sampleRate > 0) break;
            }

            var next = payload + size + (size & 1);
            if (next <= payload) break;
            stream.Position = Math.Min(next, stream.Length);
        }

        if (sampleRate <= 0 || channels <= 0 || bits <= 0 || dataOffset < 0)
        {
            throw new InvalidDataException("WAV sem cabeçalho PCM reconhecível.");
        }

        if (dataOffset + dataLength > stream.Length)
        {
            dataLength = Math.Max(0, stream.Length - dataOffset);
        }

        return new WavInfo(sampleRate, channels, bits, dataOffset, dataLength);
    }

    private static string ReadTag(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        return bytes.Length == 4 ? Encoding.ASCII.GetString(bytes) : "";
    }
}
