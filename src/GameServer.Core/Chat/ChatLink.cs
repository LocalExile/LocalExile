using System.Net.Sockets;
using System.Threading.Channels;

namespace GameServer.Core.Chat;

/// <summary>One character's connection to a chat server. Frames arrive one at a time on the link's own task, and
/// <c>closed</c> runs once when it ends, by either side.</summary>
public sealed class ChatLink : IDisposable
{
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    readonly TcpClient _tcp;
    readonly CancellationTokenSource _stop = new();
    readonly Channel<ChatFrame> _outgoing = ChatWire.Queue();
    int _disposed;

    ChatLink(TcpClient tcp, string name)
    {
        _tcp = tcp;
        Name = name;
    }

    /// <summary>The name the server gave the character: its own, with a number when another member has it.</summary>
    public string Name { get; }

    /// <summary>Connects and introduces the character.</summary>
    /// <exception cref="IOException">The server cannot be reached or did not accept the name.</exception>
    public static async Task<ChatLink> ConnectAsync(string host, int port, string name, Func<ChatFrame, Task> received, Action<ChatLink> closed, CancellationToken cancel)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            deadline.CancelAfter(ConnectTimeout);
            await tcp.ConnectAsync(host, port, deadline.Token);
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            var stream = tcp.GetStream();
            await ChatWire.WriteAsync(stream, new ChatFrame(ChatFrame.Hello, name), deadline.Token);
            var answer = await ChatWire.ReadAsync(stream, deadline.Token);
            if (answer is not { Type: ChatFrame.Welcome, Name: var given } || !ChatWire.ValidMember(given)) throw new IOException("the chat server did not answer");
            var link = new ChatLink(tcp, given!);
            _ = ChatWire.PumpAsync(stream, link._outgoing.Reader, link._stop.Token);
            _ = link.ReadAsync(stream, received, closed);
            return link;
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or InvalidDataException)
        {
            tcp.Dispose();
            throw e as IOException ?? new IOException(e.Message, e);
        }
    }

    async Task ReadAsync(Stream stream, Func<ChatFrame, Task> received, Action<ChatLink> closed)
    {
        try
        {
            while (await ChatWire.ReadAsync(stream, _stop.Token) is { } frame) await received(frame);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException) { }
        Dispose();
        closed(this);
    }

    /// <summary>Queues a frame; a link that is gone or too far behind drops it.</summary>
    public void Send(ChatFrame frame) => _outgoing.Writer.TryWrite(frame);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _outgoing.Writer.TryComplete();
        _stop.Cancel();
        _tcp.Dispose();
    }
}
