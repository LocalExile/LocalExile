using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class StrengthManaTests
{
    [Theory]
    [InlineData(3, 8, 0L)]
    [InlineData(4, 8, 8L)]
    [InlineData(7, 8, 8L)]
    [InlineData(8, 8, 16L)]
    [InlineData(7, -8, -8L)]
    [InlineData(8, -8, -16L)]
    [InlineData(-4, 8, 0L)]
    [InlineData(int.MinValue, -8, 0L)]
    [InlineData(int.MaxValue, 0, 0L)]
    [InlineData(int.MaxValue, int.MaxValue, 1152921501922492417L)]
    [InlineData(int.MaxValue, int.MinValue, -1152921502459363328L)]
    public void Complete_nonnegative_Strength_groups_keep_the_signed_raw_value(int strength, int raw, long expected)
        => Assert.Equal(expected, StrengthMana.Flat(strength, raw));

    [Theory]
    [InlineData(40, 7, 1, 25, 51u)]
    [InlineData(40, 4, 8, 50, 72u)]
    [InlineData(40, 4, -100, 100, 0u)]
    [InlineData(40, 4, -100, -200, 0u)]
    [InlineData(-100, 3, 8, -200, 0u)]
    [InlineData(40, 4, 8, int.MinValue, 0u)]
    [InlineData(40, int.MaxValue, int.MaxValue, int.MaxValue, (uint)int.MaxValue)]
    [InlineData(int.MaxValue, 4, int.MinValue, 0, 0u)]
    [InlineData(int.MaxValue, 4, -2147483600, 100, 94u)]
    public void Maximum_saturates_after_signed_cancellation_and_never_resurrects_a_negative_flat_total(
        long ordinaryFlat, int strength, int raw, int increase, uint expected)
        => Assert.Equal(expected, StrengthMana.Maximum(ordinaryFlat, strength, raw, increase));

    [Theory]
    [InlineData(8, 0, 33, 85u)]
    [InlineData(-100, 0, -200, 0u)]
    [InlineData(int.MaxValue, 0, int.MaxValue, (uint)int.MaxValue)]
    [InlineData(int.MaxValue, int.MinValue, 100, 110u)]
    public void Pool_calculation_keeps_odd_intelligence_rounding_and_saturates_only_the_active_mana_consumer(
        int raw, int flat, int increase, uint expected)
    {
        var c = SkillCatalog.ClassInfo.Witch with { Strength = 4, Intelligence = 33 };
        var pools = CharacterStats.For(c, 1, new(Mana: flat, ManaPct: increase, ManaPerFourStrength: raw));
        Assert.Equal(expected, pools.Mana);
        Assert.Equal(52u, pools.Life);
    }

    [Theory]
    [InlineData(33, 0, 0, 56u)]
    [InlineData(33, 0, 25, 70u)]
    [InlineData(33, -56, 0, 0u)]
    [InlineData(-33, 0, 0, 40u)] // Final attributes are now nonnegative before the unchanged Intelligence / 2.
    [InlineData(-1, 0, 50, 60u)]
    [InlineData(1, -41, -200, 1u)]
    [InlineData(33, -100, -150, 22u)]
    public void An_absent_or_zero_source_preserves_ordinary_mana_rounding_and_native_bytes(
        int intelligence, int flat, int increase, uint expected)
    {
        var c = SkillCatalog.ClassInfo.Witch with { Intelligence = intelligence };
        var bonus = new CharacterStats.Bonus(Mana: flat, ManaPct: increase);
        var absent = CharacterStats.For(c, 1, bonus);
        var zero = CharacterStats.For(c, 1, bonus with { ManaPerFourStrength = 0 });
        Assert.Equal(expected, absent.Mana);
        Assert.Equal(absent, zero);
        Assert.Equal(NativeBytes(absent), NativeBytes(zero));
    }

    static byte[] NativeBytes(CharacterStats.Pools pools)
    {
        var attributes = (IEnumerable<(uint, int)>)Method("ClassAttributes").Invoke(null, [1, pools])!;
        return InstanceEntities.StatsUpdate(1, attributes.ToArray());
    }

    [Fact]
    public void The_real_node_combines_mana_per_Strength_with_conversion_and_source_removal()
    {
        Assert.Equal(4993u, Program.Stats.Key(StrengthMana.PerFourStrength));
        var node = Program.Passives.Find(38080)!;
        Assert.Equal("Mental Conditioning", node.Name);
        Assert.Contains((4993u, 8), node.Stats);
        Assert.Contains((9548u, 1), node.Stats);
        Assert.Contains((21159u, 1), node.Stats);
        var conn = Conn();
        try
        {
            var ordinary = Program.PoolsFor(conn, 1);
            // Exercise production aggregation without claiming Phrecia selection/admission support.
            conn.MainAscendancy = new(Nodes: [38080]);
            conn.Pools = Program.PoolsFor(conn, 1);
            Assert.Equal(16u, conn.Pools.Mana); // Witch: (56 + three groups * 8) * .2.
            Assert.Equal(128, conn.Pools.Armour);
            Assert.Equal(conn.Pools, Program.PoolsFor(conn, 1));
            conn.PlayerStats[4993] = 0;
            Assert.Equal(11u, Program.PoolsFor(conn, 1).Mana);
            Assert.Equal(90, Program.PoolsFor(conn, 1).Armour);
            conn.PlayerStats.Remove(4993);
            Assert.Equal(16u, Program.PoolsFor(conn, 1).Mana);
            conn.MainAscendancy = new();
            Assert.Equal(ordinary.Mana, Program.PoolsFor(conn, 1).Mana);
        }
        finally { PlayerInventories.Forget(conn.Character!); }
    }

    [Theory]
    [InlineData(3, 48u)]
    [InlineData(4, 50u)]
    [InlineData(7, 50u)]
    [InlineData(8, 52u)]
    public void Production_sources_add_flat_mana_before_additive_increases_and_feed_shared_consumers(
        int strength, uint expected)
    {
        var conn = Conn();
        try
        {
            ItemMod Mod(string name, int value)
            {
                var mod = Program.Items.Mods.All.Where(m => m.Domain == 1 && m.Stats.Count == 1
                    && m.Stats[0].Id == name && Program.Items.Mods.ByHash(m.Hash)?.Id == m.Id)
                    .OrderBy(m => m.Level).First();
                return new(ModSection.Explicit, mod.Hash, [value]);
            }
            var ring = ItemCatalog.Create(Program.Items.Find("Iron Ring")!) with
            {
                Mods = [Mod("additional_strength", strength - 14), Mod("base_maximum_mana", 9),
                    Mod("maximum_mana_+%", 25)]
            };
            PlayerInventories.For(conn.Character!, Program.Items).Add(6, 0, 0, ring);
            conn.Passives.Add(34906); // Ordinary Mana: another 8% increased, additive with the item's 25%.
            conn.MainAscendancy = new(Nodes: [38080]);
            conn.Pools = Program.PoolsFor(conn, 20);
            Assert.Equal(strength, conn.Pools.Strength);
            Assert.Equal(expected, conn.Pools.Mana); // (170 + 9 + floor(Strength / 4) * 8) * 1.33 * .2, rounded.
            Assert.Equal(conn.Pools, Program.PoolsFor(conn, 20));
            conn.PlayerStats[Program.Stats.Key(DamageStats.MaxManaAddedLightning)!.Value] = 6;
            var player = Program.CharacterPlayerStats(conn);
            Assert.Equal((int)expected, player[Program.Stats.Key("maximum_mana")!.Value]);
            var hit = Program.AttackHit(new SkillCatalog.Unarmed(833, 2, 5, 8), player, melee: true);
            Assert.Equal(expected * 0.06, hit[DamageType.Lightning].Min, 7);
            Assert.Equal(expected * 0.06, hit[DamageType.Lightning].Max, 7);
            Assert.Equal((int)(expected * 0.06), player[Program.Stats.Key("main_hand_maximum_lightning_damage")!.Value]);
            var aura = new AuraCatalog.Aura(0x103, 0, "test", "test", 1, 0, 0, 5000);
            Assert.Equal((int)Math.Ceiling(expected / 2.0), AuraCatalog.ReservedMana(conn.Pools, [aura]));
            Assert.Equal((int)expected / 2, AuraCatalog.UnreservedMana(conn.Pools, [aura]));
            conn.MainAscendancy = new();
            Assert.Equal(238u, Program.PoolsFor(conn, 20).Mana);
        }
        finally { PlayerInventories.Forget(conn.Character!); }
    }

    [Fact]
    public async Task Actual_commands_replace_clear_and_reload_the_transient_source_without_using_display_Strength()
    {
        var conn = Conn();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var writer = new TcpClient();
        listener.Start();
        var accepting = listener.AcceptTcpClientAsync();
        await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var reader = await accepting;
        try
        {
            conn.Pools = Program.PoolsFor(conn, 1);
            var ciphers = new ConnectionCiphers(new byte[64], isClient: false);
            conn.Commands = (ChatCommands)Method("Commands").Invoke(null,
                ["strength-mana-test", writer.GetStream(), ciphers, conn])!;
            async Task Set(string name, int value, uint mana)
            {
                Assert.True(await conn.Commands.RunAsync($"/set_stat {name} {value}").WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(mana, conn.Pools.Mana);
                Assert.Equal((int)mana, Program.CharacterPlayerStats(conn)[Program.Stats.Key("maximum_mana")!.Value]);
            }
            await Set("9548", 1, 11); // Conversion alone uses no mana-per-Strength source.
            Assert.Equal(90, conn.Pools.Armour);
            Assert.True(await conn.Commands.RunAsync("/set_stat 9548 off").WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(56u, conn.Pools.Mana);
            await Set("21159", 1, 56);
            await Set(StrengthMana.PerFourStrength, 8, 80);
            await Set("4993", -8, 32);
            await Set(StrengthMana.PerFourStrength, 0, 56);
            await Set(StrengthMana.PerFourStrength, 8, 80);
            await Set("strength", 100000, 80);
            Assert.Equal(14, conn.Pools.Strength);
            Assert.Equal(100000, Program.CharacterPlayerStats(conn)[Program.Stats.Key("strength")!.Value]);
            conn.Pools = Program.PoolsFor(conn, 1);
            Assert.Equal(80u, conn.Pools.Mana);
            Assert.Equal(8, conn.PlayerStats[4993]); // The derived mana never becomes a raw source.

            var reloaded = new ClientState(conn.Layout, 0, 1) { Character = conn.Character, ClassRow = 1 };
            Assert.Equal(1, (int)Method("LoadProgress").Invoke(null, [reloaded, 1])!);
            Assert.Empty(reloaded.PlayerStats);
            Assert.Equal(56u, reloaded.Pools.Mana);
            Assert.Equal(14, reloaded.Pools.Strength);
        }
        finally { PlayerInventories.Forget(conn.Character!); }
    }

    [Fact]
    public async Task Actual_commands_publish_reservation_changes_with_zero_regeneration_and_clamp_a_decreased_cap()
    {
        var conn = Conn();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var writer = new TcpClient();
        listener.Start();
        var accepting = listener.AcceptTcpClientAsync();
        await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var reader = await accepting;
        using var capture = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reading = reader.GetStream().CopyToAsync(capture, timeout.Token);
        var ciphers = new ConnectionCiphers(new byte[64], isClient: false);
        Task? recovery = null;
        try
        {
            conn.Auras[0x103] = new(0x103, Program.Skills.EffectRow("Haste"), "Haste", "Haste",
                1, AuraCatalog.BuffRow("Haste"), 0, 5000);
            conn.Pools = Program.PoolsFor(conn, 1);
            conn.PlayerLife = conn.Pools.Life;
            conn.PlayerMana = 20;
            conn.Commands = (ChatCommands)Method("Commands").Invoke(null,
                ["strength-mana-reservation-test", writer.GetStream(), ciphers, conn])!;
            async Task Command(string command) => Assert.True(await conn.Commands.RunAsync(command)
                .WaitAsync(TimeSpan.FromSeconds(5)));
            await Command("/set_stat mana_regeneration_rate_+% -100");
            Assert.Equal(0d, (double)Method("ManaRegenPerSecond").Invoke(null, [conn])!);
            conn.Clock.Start();
            recovery = (Task)Method("RunResourceRecovery").Invoke(null,
                ["strength-mana-reservation-test", writer.GetStream(), ciphers, conn])!;
            await Task.Delay(300); // Let the real recovery loop settle before changing the raw source.
            await Command("/set_stat 4993 0");
            await Command("/set_stat 4993 8");
            await Task.Delay(300); // Recovery observes the new maximum without changing current mana.
            Assert.Equal(80u, conn.Pools.Mana);
            Assert.Equal(20u, conn.PlayerMana);
            await Command("/set_stat 4993 0");
            Assert.Equal(20u, conn.PlayerMana); // Reservation decreases even though current mana still does not move.
            await Command("/set_stat 4993 8");
            await Command("/heal");
            Assert.Equal(40u, conn.PlayerMana);
            await Command("/set_stat 4993 0");
            Assert.Equal(56u, conn.Pools.Mana);
            Assert.Equal(28u, conn.PlayerMana); // The command itself clamps to the new unreserved cap.
            Assert.Equal(0d, (double)Method("ManaRegenPerSecond").Invoke(null, [conn])!);
        }
        finally
        {
            conn.Closed = true;
            if (recovery is not null) await recovery.WaitAsync(TimeSpan.FromSeconds(5));
            writer.Client.Shutdown(SocketShutdown.Send);
            await reading;
            PlayerInventories.Forget(conn.Character!);
        }
        var bytes = capture.ToArray();
        new ConnectionCiphers(new byte[64], isClient: true).Receive.Process(bytes);
        var expected = new[] { Mana(20, 28), Mana(20, 40), Mana(20, 28), Mana(20, 40), Mana(40, 40), Mana(28, 28) };
        Assert.Equal(expected.SelectMany(p => p), ManaPackets(bytes).SelectMany(p => p));
    }

    [Fact]
    public async Task A_queued_mana_update_samples_current_mana_and_reservation_after_acquiring_the_send_gate()
    {
        var conn = Conn();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var writer = new TcpClient();
        listener.Start();
        var accepting = listener.AcceptTcpClientAsync();
        await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var reader = await accepting;
        try
        {
            conn.Auras[0x103] = new(0x103, 0, "test", "test", 1, 0, 0, 5000);
            conn.Pools = Program.PoolsFor(conn, 1);
            conn.PlayerMana = 20;
            var ciphers = new ConnectionCiphers(new byte[64], isClient: false);
            var gate = Program.SendGates.GetValue(ciphers, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            var pending = (Task)Method("SendPlayerMana").Invoke(null,
                ["strength-mana-gate-test", writer.GetStream(), ciphers, conn, null])!;
            try
            {
                Assert.False(pending.IsCompleted);
                lock (conn.Monsters)
                {
                    conn.PlayerStats[4993] = 8;
                    conn.Pools = Program.PoolsFor(conn, 1);
                    conn.PlayerMana = 13;
                }
            }
            finally { gate.Release(); }
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            var bytes = new byte[Mana(13, 40).Length];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await reader.GetStream().ReadExactlyAsync(bytes, timeout.Token);
            new ConnectionCiphers(new byte[64], isClient: true).Receive.Process(bytes);
            Assert.Equal(Mana(13, 40), bytes); // Neither the old 20 current nor the old 28 reservation may be queued.
        }
        finally { PlayerInventories.Forget(conn.Character!); }
    }

    static byte[] Mana(uint current, uint reserved) =>
        InstanceProtocol.LifeUpdate(Program.PlayerId, current, reserved, pool: Program.ManaPool);

    static List<byte[]> ManaPackets(byte[] bytes)
    {
        var header = Mana(0, 0).AsSpan(0, 12).ToArray();
        var packets = new List<byte[]>();
        for (int i = 0; i <= bytes.Length - 24; i++)
            if (bytes.AsSpan(i).StartsWith(header) && bytes[i + 20] == Program.ManaPool)
            {
                packets.Add(bytes.AsSpan(i, 24).ToArray());
                i += 23;
            }
        return packets;
    }

    static MethodInfo Method(string name) => typeof(Program).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

    static ClientState Conn()
    {
        TestSaves.Ensure();
        string character = "StrengthMana" + Guid.NewGuid().ToString("N");
        CharacterSaves.SetExperience(character, 0); // An empty saved inventory avoids implicit starter modifiers.
        return new(new(ushort.MaxValue, "StrengthMana", 128, 128, 32, 32, null, []), 0, 1)
        { Character = character, ClassRow = 1 };
    }
}
