using System.Net;
using System.Net.Sockets;
using System.Text;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol;
using GameServer.Core.World;

namespace GameServer.Host;

/// <summary>TCP listener speaking the JSON-lines dev protocol; shares one AccountService/InstanceManager across connections.</summary>
public sealed class GameHost(AccountService accounts, InstanceManager instances, IPAddress? bind = null, int port = 7000) : IAsyncDisposable
{
    private readonly TcpListener _listener = new(bind ?? IPAddress.Loopback, port);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _loop = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
            // DisposeAsync cancels and then stops the listener. An accept that starts between the two, or is cut off
            // by the stop, fails with the socket's own exception instead of the cancellation: either way, shutting down.
            catch (Exception) when (_cts.IsCancellationRequested) { break; }
            _ = Serve(c);
        }
    }

    private async Task Serve(TcpClient client)
    {
        var session = new GameSession(accounts, instances);
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                while (await reader.ReadLineAsync(_cts.Token) is { } line)
                {
                    if (line.Length > 8192) break;
                    await writer.WriteLineAsync(session.Handle(line));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { session.Disconnect(); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        if (_loop is not null) await _loop;
    }
}
