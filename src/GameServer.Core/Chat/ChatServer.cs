using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace GameServer.Core.Chat;

/// <summary>The TCP side of a <see cref="ChatHub"/>: one connection is one character. It transmits and stores nothing. The
/// Chat module runs one on loopback for its own players, and GameServer.Chat runs one for everyone.</summary>
public sealed class ChatServer(IPAddress bind, int port, int maxConnections = 2000, int maxPerAddress = int.MaxValue) : IDisposable
{
    static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    readonly TcpListener _listener = new(bind, port);
    readonly ChatHub _hub = new();
    readonly CancellationTokenSource _stop = new();
    readonly Dictionary<IPAddress, int> _perAddress = [];
    int _connections;

    /// <summary>The port it listens on, the system's choice when it was given 0.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _ = AcceptAsync();
    }

    async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            var address = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
            if (!Admit(address))
            {
                tcp.Dispose();
                continue;
            }
            _ = ServeAsync(tcp, address);
        }
    }

    bool Admit(IPAddress address)
    {
        lock (_perAddress)
        {
            _perAddress.TryGetValue(address, out int mine);
            if (_connections >= maxConnections || mine >= maxPerAddress) return false;
            _perAddress[address] = mine + 1;
            _connections++;
            return true;
        }
    }

    void Forget(IPAddress address)
    {
        lock (_perAddress)
        {
            _connections--;
            if (--CollectionsMarshal.GetValueRefOrNullRef(_perAddress, address) == 0) _perAddress.Remove(address);
        }
    }

    async Task ServeAsync(TcpClient tcp, IPAddress address)
    {
        using var serving = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var outgoing = ChatWire.Queue();
        Task? pump = null;
        ChatMember? member = null;
        try
        {
            using (tcp)
            {
                tcp.NoDelay = true;
                tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                var stream = tcp.GetStream();
                if (await HelloAsync(stream, serving.Token) is not { } hello) return;
                // A member too slow for its full queue is dropped, and reconnects.
                member = _hub.Enter(hello.Name, frame => { if (!outgoing.Writer.TryWrite(frame)) serving.Cancel(); });
                if (member is null) return;
                pump = ChatWire.PumpAsync(stream, outgoing.Reader, serving.Token);
                outgoing.Writer.TryWrite(new ChatFrame(ChatFrame.Welcome, member.Name));
                while (await ChatWire.ReadAsync(stream, serving.Token) is { } frame) _hub.Handle(member, frame);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            if (member is not null) _hub.Leave(member);
            outgoing.Writer.TryComplete();
            await serving.CancelAsync();
            if (pump is not null) await pump;
            Forget(address);
        }
    }

    async Task<ChatFrame?> HelloAsync(Stream stream, CancellationToken cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(HelloTimeout);
        return await ChatWire.ReadAsync(stream, deadline.Token) is { Type: ChatFrame.Hello } hello ? hello : null;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
