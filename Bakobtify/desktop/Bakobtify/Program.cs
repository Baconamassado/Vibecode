using Bakobtify.Data;
using Bakobtify.Playback;
using Bakobtify.SoundCloud;
using Bakobtify.UI;

namespace Bakobtify;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var started = Environment.TickCount64;
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);
        try
        {
            var databaseStarted = Environment.TickCount64;
            using var database = new Database();
            var databaseElapsed = Environment.TickCount64 - databaseStarted;
            using var client = new SoundCloudClient(database);
            using var audio = new AudioEngine(client);
            using var form = new MainForm(database, client, audio, started, databaseElapsed);
            Application.Run(form);
        }
        catch (Exception)
        {
            MessageBox.Show("Não foi possível iniciar o Bakobtify. Verifique se o banco local está acessível.",
                "Bakobtify", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
