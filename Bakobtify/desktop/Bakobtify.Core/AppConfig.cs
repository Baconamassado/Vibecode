namespace Bakobtify;

/// <summary>
/// Caminhos e constantes do aplicativo. O diretório de dados é compartilhado
/// com a versão anterior.
/// </summary>
public static class AppConfig
{
    public static string Root { get; } = FindRoot();

    public static string DataDirectory => Path.Combine(Root, "data");

    public static string DatabasePath { get; } =
        Environment.GetEnvironmentVariable("BAKOBTIFY_DB") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(DataDirectory, "bakobtify.db");

    private static readonly Lazy<string> AudioDirectory = new(EnsureTemp);
    public static string TempDirectory => AudioDirectory.Value;

    public const int ClientIdTtlHours = 24;

    /// <summary>Intervalo mínimo entre chamadas ao api-v2 (mesmo valor do Python).</summary>
    public static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(300);

    public const int SearchCacheSeconds = 60;
    public const int SearchCacheMax = 60;

    public static string UserAgent { get; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private static string EnsureTemp()
    {
        var dir = Path.Combine(DataDirectory, "cache", "audio");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string FindRoot()
    {
        var env = Environment.GetEnvironmentVariable("BAKOBTIFY_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
        {
            return Path.GetFullPath(env);
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "data", "bakobtify.db")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "desktop", "Bakobtify", "Bakobtify.csproj")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }
}
