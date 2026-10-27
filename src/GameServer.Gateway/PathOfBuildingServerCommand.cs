using System.Text.Json;

static class PathOfBuildingServerCommand
{
    public static async Task<int> RunAsync(string[] args, bool tokenCommand, TextWriter output, TextWriter error)
    {
        try
        {
            if (args.Any(a => a is "--help" or "-h"))
            {
                output.WriteLine("""
                    --pob-server --config config/pob-server.json
                    --pob-token --config config/pob-server.json --account ACCOUNT
                    --pob-token --config config/pob-server.json --account ACCOUNT --revoke

                    The server is a separate receiver for evryBuild's Forge Character button.
                    Folder paths belong to its administrator configuration; builds cannot choose server files.
                    Issuing a token rotates the account's previous PoB token. The new secret is printed once.
                    Tokens authorize only new-character imports for that account; game login passwords are unchanged.
                    """);
                return 0;
            }
            string path = "config/pob-server.json";
            string? account = null;
            bool revoke = false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i++)
            {
                string option = args[i];
                if (!seen.Add(option)) throw new ArgumentException($"{option} was supplied more than once.");
                if (option == "--revoke" && tokenCommand) { revoke = true; continue; }
                if (option != "--config" && !(tokenCommand && option == "--account"))
                    throw new ArgumentException($"Unknown receiver option '{option}'. Use --help.");
                if (++i == args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"{option} needs a value.");
                if (option == "--config") path = args[i]; else account = args[i];
            }
            var config = PathOfBuildingServerConfig.Load(path);
            if (tokenCommand)
            {
                if (string.IsNullOrWhiteSpace(account)) throw new ArgumentException("--account is required to issue or revoke a PoB token.");
                string token = PathOfBuildingServerTokens.Issue(config.DatabasePath, account, revoke);
                if (revoke) output.WriteLine(JsonSerializer.Serialize(new { ok = true, account, revoked = true }));
                else output.WriteLine(JsonSerializer.Serialize(new { ok = true, account, token,
                    next = "Copy this token into PoB's LocalExile dialog. It is not a game login password. Keep it private." }));
                return 0;
            }
            await using var server = new PathOfBuildingServer(config);
            await server.StartAsync();
            output.WriteLine($"[pob] listening on {server.Address}; database: {config.DatabasePath}");
            await server.WaitForShutdownAsync();
            return 0;
        }
        catch (Exception e) when (e is ArgumentException or IOException or JsonException or UnauthorizedAccessException
            or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            error.WriteLine(JsonSerializer.Serialize(new { ok = false, error = e.Message }));
            return 1;
        }
    }
}
