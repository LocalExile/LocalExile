using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Protocol.Instance;

/// <summary>A client's connection: its socket and ciphers, and what the network keeps of it (the bytes not yet framed,
/// the message history, the HNC carrier, the instance keys). Only the framework reaches it (<see cref="Player.Session"/>);
/// the rules send to the player (Program.Send).</summary>
public sealed class Session(string remote, NetworkStream stream, ConnectionCiphers ciphers)
{
    /// <summary>No connection: a player made outside a socket (a test's, a tool's).</summary>
    public static Session None => new("", null!, null!);

    public string Remote { get; } = remote;
    public NetworkStream Stream { get; } = stream;
    public ConnectionCiphers Ciphers { get; } = ciphers;
    public bool Connected => Stream is not null && Ciphers is not null;

    /// <summary>The bytes after the last whole message read (ClientInbox).</summary>
    public byte[] Carry = [];
    /// <summary>Reads in a row that framed nothing (ClientInbox.UnframedRun).</summary>
    public int UnframedRun;
    /// <summary>The connection's last messages, both directions (PacketHistory).</summary>
    public readonly PacketHistory Packets = new();
    /// <summary>0x1c's u32 is an HNC carrier and must decrypt to idle zero bytes, or the client throws "HNC Protocol Violated".</summary>
    public HncSender? Hnc;
    public InstanceKeyring Keyring = null!;

    public void Deconstruct(out string remote, out NetworkStream stream, out ConnectionCiphers ciphers) =>
        (remote, stream, ciphers) = (Remote, Stream, Ciphers);
}
