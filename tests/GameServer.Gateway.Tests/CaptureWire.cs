using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

internal sealed class CaptureWire : IDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly TcpClient writer = new();
    readonly TcpClient reader;
    readonly byte[] key = Enumerable.Range(1, 64).Select(i => (byte)i).ToArray();
    public ConnectionCiphers ServerCiphers { get; }
    public NetworkStream Stream => writer.GetStream();

    public CaptureWire()
    {
        ServerCiphers = new ConnectionCiphers(key, isClient: false);
        listener.Start();
        var accepting = listener.AcceptTcpClientAsync();
        writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        reader = accepting.GetAwaiter().GetResult();
    }

    public byte[] FinishAndDecrypt()
    {
        writer.Client.Shutdown(SocketShutdown.Send);
        using var ms = new MemoryStream();
        reader.GetStream().CopyTo(ms);
        byte[] data = ms.ToArray();
        new ConnectionCiphers(key, isClient: true).Receive.Process(data);
        return data;
    }

    public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Stop(); }
}
