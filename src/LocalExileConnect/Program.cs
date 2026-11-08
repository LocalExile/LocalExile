using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GameServer.Core.Protocol.Patch;
using GameServer.Data.Ggpk;

namespace LocalExileConnect;

/// <summary>
/// The other machines' one stop: point a client's GGPK at the host, put the original back, check the host answers, and
/// start the game. A player needs no repository and no .NET: publish it self-contained
/// (<c>scripts/dev/build-connect-tool.ps1</c>) and hand over the single exe beside <c>Content.ggpk</c>.
/// Agents/docs/dev/lan-play.md is the user-facing description.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0) return Cli(args);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>The command line form, for a script or a friend who prefers a shortcut: --check, --set, --restore.</summary>
    private static int Cli(string[] args)
    {
        // A WinExe has no console of its own: borrow the parent's so the output is visible when it is run from one.
        AttachConsole(-1);
        try
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { /* no console to re-encode */ }
            string gameDir = GameDir(args);
            switch (args[0])
            {
                case "--check":
                {
                    if (args.Length < 2) { Console.WriteLine("usage: --check <host> [loginPort] [patchPort]"); return 2; }
                    foreach (var line in Client.Check(args[1], Port(args, 2, 20493), Port(args, 3, 12995)))
                        Console.WriteLine(line);
                    return 0;
                }
                case "--set":
                {
                    if (args.Length < 2) { Console.WriteLine("usage: --set <host:loginPort>"); return 2; }
                    Console.WriteLine(Client.Set(gameDir, args[1]));
                    return 0;
                }
                case "--restore":
                    Console.WriteLine(Client.Restore(gameDir));
                    return 0;
                default:
                    Console.WriteLine("usage: LocalExileConnect.exe [--check <host> [login] [patch]] [--set <host:port>] [--restore] [--dir <game folder>]");
                    return 2;
            }
        }
        finally { FreeConsole(); }
    }

    private static ushort Port(string[] args, int index, ushort fallback) =>
        args.Length > index && ushort.TryParse(args[index], out var p) && p > 0 ? p : fallback;

    internal static string GameDir(string[] args)
    {
        int at = Array.IndexOf(args, "--dir");
        if (at >= 0 && at + 1 < args.Length) return Path.GetFullPath(args[at + 1]);
        return AppContext.BaseDirectory;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
}

/// <summary>What the buttons do, without any UI so the command line and the form share it.</summary>
internal static class Client
{
    public const ushort DefaultLoginPort = 20493, DefaultPatchPort = 12995, DefaultGatewayPort = 6112;

    static string Ggpk(string gameDir) => Path.Combine(gameDir, "Content.ggpk");
    static string Exe(string gameDir) => Path.Combine(gameDir, "Game.patched.exe");
    static string Backup(string gameDir) => Path.Combine(gameDir, "gateway_list.original.txt");
    static string Constants(string gameDir) => Path.Combine(gameDir, "client-constants.json");

    /// <summary>
    /// Points every gateway of the game's GGPK at <c>host:loginPort</c>, keeping the original in
    /// <c>gateway_list.original.txt</c> beside the game. The host runs the same write with the same text, which is what
    /// keeps both GGPKs' root hash equal (the patch welcome compares it).
    /// </summary>
    public static string Set(string gameDir, string hostPort)
    {
        string ggpk = Ggpk(gameDir);
        if (!File.Exists(ggpk)) return $"找不到 {ggpk}：把这个程序放到客户端文件夹里（与 Content.ggpk 同目录）。";
        if (Running()) return "游戏正在运行：请先关闭游戏客户端，再配置（客户端启动时会独占 Content.ggpk）。";
        try
        {
            string? entry = GatewayList.SetLogin(ggpk, hostPort, Backup(gameDir));
            if (entry is null) return "这个 Content.ggpk 里没有 gateway_list.txt，无法配置（客户端版本不对？）";
            SaveRootHash(gameDir);
            return $"已配置：{entry} 指向 {hostPort}。原文件备份在 gateway_list.original.txt。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"写入失败：{ex.Message}（游戏正在运行，或文件被占用？）";
        }
    }

    /// <summary>Puts the saved original back, so the client talks to the official list again.</summary>
    public static string Restore(string gameDir)
    {
        string ggpk = Ggpk(gameDir), backup = Backup(gameDir);
        if (!File.Exists(ggpk)) return $"找不到 {ggpk}：把这个程序放到客户端文件夹里（与 Content.ggpk 同目录）。";
        if (!File.Exists(backup)) return "没有找到备份 gateway_list.original.txt：这个 GGPK 没有被本程序改过。";
        if (Running()) return "游戏正在运行：请先关闭游戏客户端，再还原。";
        try
        {
            string? entry = GatewayList.Find(ggpk);
            if (entry is null) return "这个 Content.ggpk 里没有 gateway_list.txt，无法还原。";
            GgpkMaintenance.ReplaceFile(ggpk, entry, File.ReadAllBytes(backup));
            SaveRootHash(gameDir);
            return $"已还原：{entry} 恢复成 gateway_list.original.txt 的内容。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"还原失败：{ex.Message}（游戏正在运行，或文件被占用？）";
        }
    }

    /// <summary>
    /// Whether the host answers. The patching server is the player's own now, so the two ports that must be tunnelled
    /// are checked: login and instance. A non-zero <paramref name="patchPort"/> also asks the host's patching protocol
    /// (for a host that still publishes one through the tunnel).
    /// </summary>
    public static IReadOnlyList<string> Check(string host, ushort loginPort = DefaultLoginPort, ushort patchPort = 0, ushort gatewayPort = DefaultGatewayPort)
    {
        var lines = new List<string>();
        lines.Add(Connect(host, loginPort, "登录服务器"));
        lines.Add(Connect(host, gatewayPort, "实例服务器"));
        if (patchPort != 0) lines.Add(Patch(host, patchPort));
        return lines;
    }

    static string Patch(string host, ushort port)
    {
        try
        {
            using var tcp = new TcpClient();
            if (!tcp.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(6))) return $"补丁服务器 {host}:{port}：连接超时";
            var stream = tcp.GetStream();
            stream.Write([0x01, 0x07], 0, 2);
            stream.Flush();
            tcp.ReceiveTimeout = 6000;
            byte[] buf = new byte[64];
            int n = 0;
            var until = DateTime.UtcNow.AddSeconds(6);
            while (n < 34 && DateTime.UtcNow < until)
            {
                if (!stream.DataAvailable) { Thread.Sleep(50); continue; }
                n += stream.Read(buf, n, buf.Length - n);
            }
            if (n >= 34 && buf[0] == 0x02)
                return $"补丁服务器 {host}:{port}：正常（根哈希 {Convert.ToHexString(buf, 1, 32).ToLowerInvariant()}）";
            return $"补丁服务器 {host}:{port}：有响应但不是补丁协议（检查端口/穿透）";
        }
        catch (Exception ex) { return $"补丁服务器 {host}:{port}：连不上（{ex.GetBaseException().Message}）"; }
    }

    static string Connect(string host, ushort port, string what)
    {
        try
        {
            using var tcp = new TcpClient();
            if (!tcp.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(6))) return $"{what} {host}:{port}：连接超时";
            return $"{what} {host}:{port}：可连接";
        }
        catch (Exception ex) { return $"{what} {host}:{port}：连不上（{ex.GetBaseException().Message}）"; }
    }

    /// <summary>
    /// Starts the game through a patching server on this PC. The player's GGPK was written to the same bytes as the
    /// host's, so its own root hash is the right answer: the client reads "No patching required" and never needs the
    /// host's patching port through the tunnel (Agents/docs/dev/lan-play.md).
    /// </summary>
    public static string Launch(string gameDir)
    {
        string exe = Exe(gameDir);
        if (!File.Exists(exe)) return $"找不到 {exe}：把这个程序放到客户端文件夹里（与 Game.patched.exe 同目录）。";
        if (!File.Exists(Ggpk(gameDir))) return $"找不到 {Ggpk(gameDir)}：把这个程序放到客户端文件夹里。";
        try
        {
            byte[] hash = RootHashBytes(gameDir);
            int port = LocalPatchServer.Start(hash);
            Process.Start(new ProcessStartInfo(exe, $"--patching-server 127.0.0.1:{port}") { WorkingDirectory = gameDir });
            return $"已启动游戏（本机补丁握手在 127.0.0.1:{port}，根哈希 {Convert.ToHexString(hash).ToLowerInvariant()}）。";
        }
        catch (Exception ex) { return $"启动失败：{ex.Message}"; }
    }

    /// <summary>The GGPK's root hash: the one saved beside the game, else computed once and saved.</summary>
    static byte[] RootHashBytes(string gameDir)
    {
        string file = Constants(gameDir);
        try
        {
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("GgpkRootHash", out var p) && p.GetString() is { Length: 64 } hex)
                    return Convert.FromHexString(hex);
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or IOException) { /* recompute below */ }
        byte[] hash = GgpkMaintenance.RootHash(Ggpk(gameDir));
        try { File.WriteAllText(file, $"{{\"GgpkRootHash\":\"{Convert.ToHexString(hash)}\"}}", Encoding.UTF8); } catch (IOException) { }
        return hash;
    }

    static bool Running() =>
        Process.GetProcessesByName("Game.patched").Length > 0 || Process.GetProcessesByName("Game").Length > 0;

    /// <summary>
    /// Saves the modified GGPK's root hash beside the game, for a player who also runs the patch server locally. The host
    /// keeps its own copy in data/client-constants.json; a client only compares, so this file is optional there.
    /// </summary>
    static void SaveRootHash(string gameDir)
    {
        try
        {
            string hash = Convert.ToHexString(GgpkMaintenance.RootHash(Ggpk(gameDir)));
            File.WriteAllText(Constants(gameDir), $"{{\"GgpkRootHash\":\"{hash}\"}}", Encoding.UTF8);
        }
        catch (IOException) { /* informational only */ }
    }
}

/// <summary>
/// The patching handshake on this PC, so a tunnel only needs the login and instance ports. The player's GGPK was written
/// to the same bytes as the host's, so answering with its own root hash makes the client say "No patching required".
/// The client only asks once, when it starts; the listener lives as long as this tool runs.
/// </summary>
internal static class LocalPatchServer
{
    /// <summary>Starts a loopback listener on a free port; returns that port.</summary>
    public static int Start(byte[] hash)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        new Thread(() => Serve(listener, hash)) { IsBackground = true, Name = "local patch" }.Start();
        return port;
    }

    static void Serve(TcpListener listener, byte[] hash)
    {
        // A web root that is never fetched: the hash always matches, so this client never patches.
        byte[] welcome = PatchProtocol.EncodeWelcome(hash, "http://127.0.0.1:1/", "http://127.0.0.1:1/");
        while (true)
        {
            try
            {
                using var tcp = listener.AcceptTcpClient();
                var stream = tcp.GetStream();
                byte[] buf = new byte[256];
                int have = 0;
                while (PatchProtocol.TryReadHello(buf.AsSpan(0, have)) is null)
                {
                    int n = stream.Read(buf, have, buf.Length - have);
                    if (n == 0) throw new IOException("closed before hello");
                    have += n;
                }
                stream.Write(welcome);
                stream.Flush();
                // Anything else this client sends is a patch request; it never happens when the hashes match.
                while (stream.Read(buf, 0, buf.Length) > 0) { }
            }
            catch (Exception) { /* one connection's failure never stops the listener */ }
        }
    }
}