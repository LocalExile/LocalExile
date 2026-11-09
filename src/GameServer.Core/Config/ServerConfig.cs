using System.Net;

namespace GameServer.Core;

/// <summary>
/// The owner's network settings: <c>Config/localexile.conf</c> under the working directory (or <c>Game_CONF</c>), in the
/// TrinityCore format (<c>Key = Value</c>, <c>#</c> comments, a value optionally in double quotes). Not committed: the
/// first server to read it writes <see cref="ServerConfigDefaults.Text"/> when it is missing. Each setting's environment
/// variable wins over the file (live.ps1 -PortBase sets them), and the file over the default. Only addresses, ports and
/// the client folder are here: game settings are world.db content (Agents/docs/dev/databases.md).
/// </summary>
public static class ServerConfig
{
    public const string FileName = "Config/localexile.conf";

    /// <summary>Config/localexile.conf, or <c>Game_CONF</c>.</summary>
    public static string Path => Environment.GetEnvironmentVariable("Game_CONF") is { Length: > 0 } path ? path : FileName;

    static readonly Lazy<IReadOnlyDictionary<string, string>> File = new(() =>
    {
        if (Path == FileName) WriteDefaults(FileName);
        return Read(Path);
    });

    /// <summary>Writes the defaults to <paramref name="path"/> when nothing is there (several servers may start at once:
    /// the first one's file stays). A folder it cannot write leaves the defaults in effect without the file.</summary>
    public static void WriteDefaults(string path)
    {
        if (System.IO.File.Exists(path)) return;
        string temp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            System.IO.File.WriteAllText(temp, ServerConfigDefaults.Text.ReplaceLineEndings());
            System.IO.File.Move(temp, path, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { try { System.IO.File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    public static string PatchWebUrl => Text("Game_PATCH_URL", "Patch.WebUrl", "http://127.0.0.1:8080");
    public static int PatchPort => Port("Game_PATCH_TCP_PORT", "Patch.Port", 12995);
    public static IPAddress LoginBind => Address("Game_BIND", "Login.BindAddress", "127.0.0.1");
    public static int LoginPort => Port("Game_LOGIN_PORT", "Login.Port", 20493);
    public static int ControlPort => Port("Game_AUTH_CONTROL_PORT", "AuthControl.Port", 20495);
    public static IPAddress GatewayBind => Address("Game_BIND", "Gateway.BindAddress", "127.0.0.1");
    public static int GatewayPort => Port("Game_GATEWAY_PORT", "Gateway.Port", 6112);
    public static string GatewayPublicAddress => Text("Game_GATEWAY_HOST", "Gateway.PublicAddress", "127.0.0.1");
    public static IPAddress ChatBind => Address("Game_CHAT_BIND", "Chat.BindAddress", "0.0.0.0");
    public static int ChatPort => Port("Game_CHAT_PORT", "Chat.Port", 6113);
    public static string ClientFolder => Text("Game_CLIENT_FOLDER", "Client.Folder", "Client");

    /// <summary>The setting: its environment variable, else the file's key, else the default.</summary>
    public static string Text(string variable, string key, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value
        : File.Value.TryGetValue(key, out string? configured) && configured.Length > 0 ? configured
        : fallback;

    static int Port(string variable, string key, int fallback)
    {
        string text = Text(variable, key, fallback.ToString());
        return int.TryParse(text, out int port) && port is > 0 and < 65536 ? port
            : throw new InvalidOperationException($"{key} ({variable}) is \"{text}\", not a port 1..65535 ({Path})");
    }

    static IPAddress Address(string variable, string key, string fallback)
    {
        string text = Text(variable, key, fallback);
        return IPAddress.TryParse(text, out var address) ? address
            : throw new InvalidOperationException($"{key} ({variable}) is \"{text}\", not an IP address ({Path})");
    }

    /// <summary>The file's settings by key (case-insensitive); empty when there is no file. A line that is not
    /// <c>Key = Value</c> stops the server, as a typo would otherwise be a silent default.</summary>
    public static IReadOnlyDictionary<string, string> Read(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!System.IO.File.Exists(path)) return values;
        int number = 0;
        foreach (string raw in System.IO.File.ReadLines(path))
        {
            number++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int equals = line.IndexOf('=');
            if (equals <= 0) throw new InvalidOperationException($"{path}:{number}: expected Key = Value, got \"{line}\"");
            string value = line[(equals + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            values[line[..equals].Trim()] = value;
        }
        return values;
    }
}
