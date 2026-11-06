using System.Buffers.Binary;

namespace Bakobtify.Playback;

public sealed record AudioPattern(double Energy, double Dynamics, double Brightness, double Pulse)
{
    internal static AudioPattern? Measure(string path, WavInfo info, CancellationToken token)
    {
        if (info.BitsPerSample != 16 || info.Channels is < 1 or > 8 ||
            info.SampleRate is < 8000 or > 192000) return null;
        const int windowsPerSecond = 50;
        var frames = info.SampleRate / windowsPerSecond;
        var count = (int)Math.Min(24 * windowsPerSecond, info.FrameCount / frames);
        if (count < 100) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        stream.Position = info.DataOffset + Math.Min(info.SampleRate, info.FrameCount - count * (long)frames) * info.BlockAlign;
        var bytes = new byte[frames * info.BlockAlign];
        var envelope = new double[count];
        double high = 0, total = 0;
        var previous = 0;
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            stream.ReadExactly(bytes);
            double power = 0;
            for (var offset = 0; offset < bytes.Length; offset += info.BlockAlign)
            {
                var sample = (int)BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset));
                power += (double)sample * sample;
                var difference = sample - previous;
                high += (double)difference * difference;
                previous = sample;
            }
            total += power;
            envelope[i] = Math.Sqrt(power / frames) / 32768;
        }
        if (total == 0) return null;
        var mean = envelope.Average();
        var variance = envelope.Sum(value => Math.Pow(value - mean, 2));
        double pulse = 0;
        if (variance > 1e-8)
        {
            for (var lag = 17; lag <= 38; lag++)
            {
                double correlation = 0;
                for (var i = lag; i < count; i++)
                    correlation += (envelope[i] - mean) * (envelope[i - lag] - mean);
                pulse = Math.Max(pulse, correlation / variance);
            }
        }
        return new(Math.Clamp(Math.Sqrt(mean), 0, 1),
            Math.Clamp(Math.Sqrt(variance / count) / Math.Max(mean, .001), 0, 1),
            Math.Clamp(Math.Sqrt(high / total) * Math.Sqrt(info.SampleRate / 44100d), 0, 1), pulse);
    }

    public double Similarity(AudioPattern other) => 1 - Math.Clamp(
        (Math.Abs(Energy - other.Energy) + Math.Abs(Dynamics - other.Dynamics) +
         Math.Abs(Brightness - other.Brightness) + Math.Abs(Pulse - other.Pulse)) / 2, 0, 1);
}
