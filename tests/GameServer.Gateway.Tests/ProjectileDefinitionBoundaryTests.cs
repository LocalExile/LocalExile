using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ProjectileDefinitionBoundaryTests
{
    public ProjectileDefinitionBoundaryTests() => TestSaves.Ensure();

    [Fact]
    public async Task Synthetic_source_supplies_animation_and_event_effects_without_a_registered_skill()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var writer = new TcpClient();
        var accepting = listener.AcceptTcpClientAsync();
        await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var reader = await accepting;
        var ciphers = new ConnectionCiphers(new byte[64], false);
        var conn = new ClientState(new AreaLayout(1, "synthetic-presentation", 300, 300, 10, 10, null, []), 1, 1)
            { PlayerLife = 100, Character = $"SyntheticProjectile{Guid.NewGuid():N}" };
        var session = new Program.ProjectileSession("synthetic", writer.GetStream(), ciphers, conn);
        Assert.Null(Program.ProjectileDefinition("synthetic-source"));
        var gate = Program.SendGates.GetValue(ciphers, _ => new(1, 1));
        await gate.WaitAsync();
        try
        {
            var events = new List<ProjectileEventKind>();
            session.Schedule(0, () => true, () => new(new("synthetic-projectile") { Speed = 10, Range = 100 },
                new(1), new(1), 1, new(10, 10), new(110, 10))
            {
                Payload = new ProjectileOffense(DamagePacket.Empty, true, 0, 0, 150, "synthetic")
                {
                    Presentation = new()
                    {
                        FlightVisual = "synthetic-flight.ao", FlightClip = 7,
                        EventEffect = value => { events.Add(value.Kind); return value.Kind == ProjectileEventKind.Spawn
                            ? "synthetic-event.ao" : null; }
                    }
                }
            });
            session.AdvanceTo(0);
            Assert.Contains(ProjectileEventKind.Spawn, events);
            Assert.Equal(0, reader.Available);
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Enqueue(() => { drained.SetResult(); return Task.CompletedTask; });
            gate.Release();
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
            writer.Client.Shutdown(SocketShutdown.Send);
            using var output = new MemoryStream();
            await reader.GetStream().CopyToAsync(output).WaitAsync(TimeSpan.FromSeconds(5));
            byte[] bytes = output.ToArray();
            new ConnectionCiphers(new byte[64], true).Receive.Process(bytes);
            Assert.True(bytes.AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes("synthetic-flight.ao")) >= 0);
            Assert.True(bytes.AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes("synthetic-event.ao")) >= 0);
            // Locate the actual animation packet: object IDs are allocated by the generic host.
            bool found = false;
            for (int i = 0; i <= bytes.Length - 21; i++)
                if (BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(i)) == 0x1b2
                    && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i + 13)) == 7)
                    found = true;
            Assert.True(found);
        }
        finally
        {
            conn.Closed = true; session.Invalidate();
            if (gate.CurrentCount == 0) gate.Release();
            await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
