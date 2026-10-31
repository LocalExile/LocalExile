using System.Text;

namespace LocalExile;

/// <summary>The player's login: Auth holds the accounts (data\saves.db), and the password goes to it on standard input.</summary>
static class Accounts
{
    public static void EnsureOne(Options options, Output log)
    {
        log.Step("account", "checking your account ...");
        var (code, lines) = Tools.Capture(Tools.Auth, ["accounts"]);
        if (code == 0 && lines.Count > 0 && lines[^1] != "0") return;
        if (code != 0) throw new LauncherException(ExitCode.Unexpected, $"could not read the accounts:\n{string.Join('\n', lines)}");
        string? name = options.AccountName;
        if (name is null)
        {
            if (log.Json) throw log.NeedsInput("--account", "no account yet: give a name with --account and its password with --password-stdin");
            Console.WriteLine("Create your local account (3-32 letters, digits, _ or -). It stays on this PC; do not reuse a real password.");
            Console.Write("Account name: ");
            name = (Console.ReadLine() ?? "").Trim();
        }
        Create(name, options, log);
    }

    public static void Create(string name, Options options, Output log)
    {
        string password = Password(options, log);
        var (code, lines) = Tools.Capture(Tools.Auth, ["register", name], password);
        if (code != 0) throw new LauncherException(ExitCode.Usage, string.Join('\n', lines));
        log.Info($"account {name} created");
    }

    static string Password(Options options, Output log)
    {
        if (options.PasswordStdin) return Console.ReadLine() ?? "";
        if (log.Json) throw log.NeedsInput("--password-stdin", "give the account's password on standard input");
        string first = ReadHidden("Password (at least 4 characters): ");
        if (first != ReadHidden("Password again: ")) throw new LauncherException(ExitCode.Usage, "the passwords differ");
        return first;
    }

    static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; }
            else if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
        Console.WriteLine();
        return text.ToString();
    }
}

/// <summary>Only tells: a release never downloads anything by itself, and the game client is never updated.</summary>
static class UpdateCheck
{
    const string Latest = "https://api.github.com/repos/LocalExile/LocalExile/releases/latest";

    public static async Task RunAsync(string current, Output log)
    {
        if (!System.Version.TryParse(current.Split('+', '-')[0], out var mine)) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LocalExile");
            using var release = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync(Latest));
            string tag = release.RootElement.GetProperty("tag_name").GetString() ?? "";
            if (System.Version.TryParse(tag.TrimStart('v').Split('+', '-')[0], out var latest) && latest > mine)
                log.Info($"LocalExile {tag} is available: {release.RootElement.GetProperty("html_url").GetString()} (to update, replace these files with the new zip; data\\ is kept)");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException) { }
    }
}
