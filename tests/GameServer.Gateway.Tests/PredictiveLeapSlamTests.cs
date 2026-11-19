using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public sealed class PredictiveLeapSlamTests
{
    [Fact]
    public void Only_the_predictive_owners_positive_sequence_leap_omits_the_corrective_start_echo()
    {
        var conn = Connection(new());
        foreach (bool lockstep in new[] { false, true })
        foreach (string? effect in new[] { "LeapSlam", "LeapSlamAltX", "ShieldCharge", "HeavyStrike", null })
        foreach (ushort sequence in new ushort[] { 0, 0x1038, 0x7fff, 0x8000, 0xfffe, 0xffff })
        {
            conn.LockstepClient = lockstep;
            Assert.Equal(!lockstep && effect == "LeapSlam" && sequence < 0x8000,
                Game.PredictsLeapSlam(conn, effect, sequence));
        }
    }

    [Fact]
    public async Task An_admitted_leap_sends_one_owner_ack_and_keeps_both_observers_full_independent_actions()
    {
        var instance = new InstanceState();
        var owner = Connection(instance);
        var peers = new[] { Connection(instance), Connection(instance) };
        var received = peers.Select(_ => new List<byte[]>()).ToArray();
        for (int i = 0; i < peers.Length; i++)
        {
            var peer = peers[i];
            var messages = received[i];
            peer.Outbox = (bytes, _) =>
            {
                messages.Add((byte[])bytes.Clone());
                bytes.AsSpan().Fill(0xcc); // One recipient's cipher must not corrupt the other recipient's records.
                return Task.CompletedTask;
            };
            peer.SeenPlayers[owner.PeerId] = new(owner.PeerId, 7, new("Owner", 1, 1041, 651, 50, 40), 1041, 651, 50, owner);
            instance.PublishPlayer(new(peer.PeerId, 7, new("Peer", 1, 1041, 651, 50, 40), 1041, 651, 50, peer));
        }
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var writer = new TcpClient();
        var accepting = listener.AcceptTcpClientAsync();
        await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var reader = await accepting;
        reader.ReceiveTimeout = 5000;
        var ciphers = new ConnectionCiphers(new byte[64], false);
        var decrypt = new ConnectionCiphers(new byte[64], true);
        owner.Session = new("leap-ack-test", writer.GetStream(), ciphers);
        var outgoing = Program.Outgoing;
        Program.Outgoing = Game.MirrorSharedMonsters;
        Program.InGame[owner] = 0;
        try
        {
            // A canceled action waiting at the send gate must neither acknowledge nor appear to observers.
            var gate = Program.SendGates.GetValue(ciphers, _ => new(1, 1));
            await gate.WaitAsync();
            bool current = true, prepared = false;
            Task canceled;
            try
            {
                canceled = Program.Send(owner, [], null, still: () => current,
                    atGate: () => { prepared = true; return Game.LeapSlamAcknowledgement(owner, []); });
                current = false;
            }
            finally { gate.Release(); }
            await canceled;
            Assert.False(prepared);
            Assert.All(received, Assert.Empty);

            foreach (uint? target in new uint?[] { null, 0x08000055 })
            {
                foreach (var messages in received) messages.Clear();
                byte[] full = [.. InstanceProtocol.ActionStarted(Program.PlayerId, 1041, 651, 1032, 760,
                    0x010c, 0x1038, 0x0409, targetObjectId: target), .. InstanceProtocol.ActionEnded(Program.PlayerId)];
                var unchanged = (byte[])full.Clone();
                await Program.Send(owner, [], null,
                    atGate: () => Game.LeapSlamAcknowledgement(owner, full));
                var ack = new byte[12];
                reader.GetStream().ReadExactly(ack);
                decrypt.Receive.Process(ack);
                Assert.Equal(InstanceProtocol.ActionEnded(Program.PlayerId), ack);
                Assert.Equal(unchanged, full);
                foreach (var peer in peers) await peer.Mirror(() => Task.CompletedTask);
                foreach (var messages in received)
                {
                    Assert.Equal(2, messages.Count);
                    Assert.Equal(InstanceProtocol.ActionStarted(owner.PeerId, 1041, 651, 1032, 760,
                        0x010c, 0x1038, 0x0409, targetObjectId: target), messages[0]);
                    Assert.Equal(InstanceProtocol.ActionEnded(owner.PeerId), messages[1]);
                }
                Assert.False(reader.GetStream().DataAvailable); // No second owner acknowledgement or source-cell echo.
            }
        }
        finally
        {
            Program.Outgoing = outgoing;
            Program.InGame.TryRemove(owner, out _);
            Program.SendGates.Remove(ciphers);
            foreach (var peer in peers) { peer.Closed = true; instance.RemovePlayer(peer.PeerId); }
            owner.Closed = true;
        }
    }

    static ClientState Connection(InstanceState instance) =>
        new(new(0, "Delve_Main", 2048, 2048, 1041, 651, null, []), 0, 1) { LockstepClient = false, Instance = instance, PlayerLife = 50 };
}
