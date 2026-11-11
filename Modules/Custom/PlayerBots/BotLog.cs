using System.Text;

namespace GameServer.Modules.PlayerBots;

/// <summary>The bots' log in the module's logs/: one line per event, "date time | account | character | what", a file a
/// day, and status.txt, rewritten every few seconds with where every bot is and what it is doing.</summary>
sealed class BotLog(string folder)
{
    readonly Lock _gate = new();

    public void Write(string account, string character, string what)
    {
        var now = DateTime.Now;
        string line = $"{now:yyyy-MM-dd} {now:HH:mm:ss} | {account} | {character} | {what}";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, $"bots-{now:yyyy-MM-dd}.log"), line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException) { }
        }
    }

    public void Error(string account, string character, Exception error) =>
        Write(account, character, $"ОШИБКА: {error.GetType().Name}: {error.Message}");

    public void Status(IEnumerable<string> lines)
    {
        var text = new StringBuilder($"Боты на {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
        foreach (string line in lines) text.AppendLine(line);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "status.txt"), text.ToString(), Encoding.UTF8);
            }
            catch (IOException) { }
        }
    }
}
