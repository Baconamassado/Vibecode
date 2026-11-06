using System.Security.Cryptography;
using System.Text;

namespace Bakobtify.Playback;

internal static class LocalAudioCache
{
    private const long MaxBytes = 512L * 1024 * 1024;
    private const int MaxTracks = 32;

    public static string PathFor(string trackId)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trackId)));
        return Path.Combine(AppConfig.DataDirectory, "cache", "replay", key + ".wav");
    }

    public static WavInfo? Read(string path)
    {
        try
        {
            var info = WavFile.ReadInfo(path);
            if (info.FrameCount == 0) return null;
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return info;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Store(string source, string trackId)
    {
        try
        {
            var incoming = new FileInfo(source).Length;
            if (incoming > MaxBytes) return;
            var target = PathFor(trackId);
            var directory = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var existing = directory.EnumerateFiles("*.wav")
                .Where(file => file.FullName != target)
                .OrderByDescending(file => file.LastWriteTimeUtc);
            long total = incoming;
            var count = 1;
            foreach (var file in existing)
            {
                if (count >= MaxTracks || file.Length > MaxBytes - total)
                {
                    file.Delete();
                    continue;
                }
                total += file.Length;
                count++;
            }
            File.Move(source, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
