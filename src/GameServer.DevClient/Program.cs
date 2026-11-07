using System.Net.Sockets;
using System.Text;

// Interactive dev client. Type "op key=value ..." e.g.:  register name=bob password=pw12   login name=bob password=pw12
//   create_character name=Bob class=witch   enter_world name=Bob   enter_area area=1_1_2   who   characters
// Or pass raw JSON lines. Args: [host] [port]
string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 ? int.Parse(args[1]) : 7000;

using var client = new TcpClient(host, port);
var stream = client.GetStream();
using var reader = new StreamReader(stream, Encoding.UTF8);
using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
Console.WriteLine($"connected to {host}:{port}. Ctrl+Z/empty line to quit.");

while (Console.ReadLine() is { } raw && raw.Trim('\uFEFF', ' ', '\t') is { Length: > 0 } input)
{
    // Piped input (e.g. from PowerShell) can start with a byte-order mark; it is ignored like surrounding blanks.
    string json = input.TrimStart().StartsWith('{') ? input : ToJson(input);
    await writer.WriteLineAsync(json);
    Console.WriteLine(await reader.ReadLineAsync());
}

static string ToJson(string line)
{
    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var obj = new System.Text.Json.Nodes.JsonObject { ["op"] = parts[0] };
    foreach (var kv in parts.Skip(1))
    {
        int i = kv.IndexOf('=');
        if (i <= 0) continue;
        string k = kv[..i], v = kv[(i + 1)..];
        obj[k] = int.TryParse(v, out var n) && k is "monster_level" or "level" ? n : v;
    }
    return obj.ToJsonString();
}
