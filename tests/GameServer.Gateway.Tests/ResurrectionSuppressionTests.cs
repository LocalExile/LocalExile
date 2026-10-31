using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public sealed class ResurrectionSuppressionTests
{
    sealed class ObservedStream(Socket socket) : NetworkStream(socket, ownsSocket: false)
    {
        readonly ConnectionCiphers observer = new(new byte[64], isClient: true);
        public readonly ConcurrentQueue<byte[]> Writes = new();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            byte[] plain = buffer.ToArray();
            observer.Receive.Process(plain);
            await base.WriteAsync(buffer, cancellationToken);
            Writes.Enqueue(plain);
        }
    }

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        public readonly TcpClient Writer = new(), Reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], isClient: false);
        public readonly ObservedStream Stream;

        public Wire()
        {
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            Writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            Reader = accepting.GetAwaiter().GetResult();
            Stream = new(Writer.Client);
        }

        public void Dispose() { Stream.Dispose(); Writer.Dispose(); Reader.Dispose(); listener.Stop(); }

        // Read the actual published 0x1b3 values, keeping the last value of each key as the native client does.
        public Dictionary<uint, int> Published()
        {
            var result = new Dictionary<uint, int>();
            foreach (byte[] packet in Stream.Writes)
            {
                if (packet.Length < 13 || BinaryPrimitives.ReadUInt16BigEndian(packet) != InstanceEntities.StatsUpdateId
                    || BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(2)) != 0x1000) continue;
                int at = 12;
                uint count = Compact(packet, ref at, out _);
                for (uint n = 0; n < count; n++)
                {
                    uint key = Compact(packet, ref at, out _);
                    uint raw = Compact(packet, ref at, out int bits);
                    int value = bits == 32 ? unchecked((int)raw)
                        : (raw & (1u << (bits - 1))) != 0 ? unchecked((int)(raw | (uint.MaxValue << bits))) : (int)raw;
                    result[key] = value;
                }
            }
            return result;
        }

        static uint Compact(byte[] packet, ref int at, out int bits)
        {
            byte first = packet[at++];
            int extra;
            uint value;
            if (first < 0x80) { extra = 0; bits = 7; value = first; }
            else if (first < 0xc0) { extra = 1; bits = 14; value = (uint)(first & 0x3f); }
            else if (first < 0xe0) { extra = 2; bits = 21; value = (uint)(first & 0x1f); }
            else if (first < 0xf0) { extra = 3; bits = 28; value = (uint)(first & 0x0f); }
            else { extra = 4; bits = 32; value = 0; }
            for (int n = 0; n < extra; n++) value = (value << 8) | packet[at++];
            return value;
        }
    }

    static Task Call(string name, Wire wire, ClientState conn, params object[] tail) =>
        (Task)typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { "resurrection-suppression-test", wire.Stream, wire.Ciphers, conn }.Concat(tail).ToArray())!;

    static int RuntimeChance(ClientState conn) =>
        (((int Chance, int Effect, bool Lucky, bool Unlucky))typeof(Program)
            .GetMethod("Suppression", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [conn])!).Chance;

    static ItemMod Mod(string stat, int value)
    {
        var row = Program.Items.Mods.All.First(m => m.Stats.Any(s => s.Id == stat));
        return new(ModSection.Explicit, row.Hash, row.Stats.Select(s => s.Id == stat ? value : 0).ToArray());
    }

    [Fact]
    public async Task Checkpoint_resurrection_publishes_restored_full_ES_after_aura_death_stats_even_if_tick_cached_the_same_chance()
    {
        TestSaves.Ensure();
        string character = "SuppressionRevive" + Guid.NewGuid().ToString("N");
        using var wire = new Wire();
        var conn = new ClientState(new(ushort.MaxValue, "SuppressionRevive", 128, 128, 32, 32, null, []), 0, 1)
            { Character = character, ClassRow = SkillCatalog.WitchRow };
        Task? ticker = null;
        try
        {
            var inventory = PlayerInventories.For(character, Program.Items);
            var robe = Program.Items.Find("Simple Robe")!;
            int localBase = Program.Items.Defences(robe)!.Value.AtRoll(0).EnergyShield;
            inventory.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(robe, defenceBaseRoll: 0) with
            {
                Quality = 0,
                Mods = [Mod("local_energy_shield", 100 - localBase), Mod("local_no_attribute_requirements", 1)],
            });
            inventory.Add(6, 0, 0, ItemCatalog.Create(Program.Items.Find("Iron Ring")!) with
                { Mods = [Mod("base_spell_suppression_chance_%", 90)] });
            conn.Passives.Add(48698); // Native Void Barrier: 12% ES and +10 suppression while full ES.
            conn.Auras[0x103] = new AuraCatalog.Aura(0x103, Program.Skills.EffectRow("Discipline"),
                "Discipline", "Discipline", 1, 148, 0, 3500, SourceBuff: 2);
            conn.Pools = Program.PoolsFor(conn, 100);
            conn.PlayerLife = conn.PlayerMaxLife;
            conn.EnergyShield.SetMaximum(conn.Pools.EnergyShield, refill: true);
            conn.Clock.Start();
            Assert.Equal(184u, conn.EnergyShield.Maximum); //(100 body+60 Discipline)*(1+.03 Int+.12 Void Barrier).
            Assert.Equal(100, RuntimeChance(conn));

            // The ticker reads 100 synchronously before reaching its first delay; it must not be relied on to notice
            // the 100 -> 90 -> 100 publication sequence while its dead-player branch skips the comparison.
            ticker = Call("RunPlayerTick", wire, conn);
            // Hold periodic pollers so a separate context refresh cannot mask a missing resurrection snapshot.
            conn.Clock.Stop();
            lock (conn.Monsters) conn.PlayerLife = 0; // A lethal chaos hit may leave ES intact until death handling.
            await Call("PlayerDied", wire, conn, "suppression resurrection test").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(conn.Auras);
            Assert.Equal(90, RuntimeChance(conn));
            foreach (uint key in Program.SuppressionChanceKeys) Assert.Equal(90, wire.Published()[key]);

            await Call("Resurrect", wire, conn, false).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(conn.PlayerMaxLife, conn.PlayerLife);
            Assert.Equal(115u, conn.EnergyShield.Maximum); //100 body*(1+.03+.12); Discipline ended on death.
            Assert.Equal(conn.EnergyShield.Maximum, conn.EnergyShield.Current);
            Assert.Equal(100, RuntimeChance(conn));
            var published = wire.Published();
            foreach (uint key in Program.SuppressionChanceKeys) Assert.Equal(100, published[key]);
        }
        finally
        {
            conn.Closed = true;
            if (ticker is not null) await ticker.WaitAsync(TimeSpan.FromSeconds(5));
            PlayerInventories.Forget(character);
            Program.InactiveItems.TryRemove(character, out _);
            Program.GemSkillIdsOf.TryRemove(character, out _);
        }
    }
}
