using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public class DefaultObjectCriticalResistanceTests
{
    [Theory]
    [InlineData(false, false, false, false, 220)]
    [InlineData(true, false, false, false, 220)]
    [InlineData(false, true, false, false, 330)]
    [InlineData(true, true, false, false, 330)]
    [InlineData(false, true, true, false, 300)]
    [InlineData(true, true, true, false, 300)]
    [InlineData(false, true, true, true, 240)]
    [InlineData(true, true, true, true, 240)]
    public async Task Same_connection_C24_reads_set_stat_ignore_after_a_prior_hit_for_both_hostilities(
        bool hostile, bool critical, bool ignore, bool prohibit, int damage)
    {
        TestSaves.Ensure();
        string character = "DefaultCrit" + Guid.NewGuid().ToString("N");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var writer = new TcpClient();
        listener.Start();
        var accepting = listener.AcceptTcpClientAsync();
        await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var reader = await accepting;
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "CriticalRoute", 128, 128, 32, 32, null, []), 0, 1)
            { Character = character, ClassRow = 1, PlayerLife = 100, PlayerSent = true };
            void Set(string name, int value) => conn.PlayerStats[Program.Stats.Key(name)!.Value] = value;
            Set("global_cannot_crit", 1);
            Set(AttackBaseCriticalStrike.WeaponOverrideStat, 10000);
            Set("critical_strikes_ignore_elemental_resistances", 0);
            Assert.Equal(3626u, Program.Stats.Key("critical_strikes_ignore_elemental_resistances"));
            Set("cannot_penetrate_or_ignore_elemental_resistances", 0);
            Set("base_reduce_enemy_fire_resistance_%", 40);
            Set("base_reduce_enemy_cold_resistance_%", 20);
            Set("attack_minimum_added_fire_damage", 100);
            Set("attack_maximum_added_fire_damage", 100);
            Set("attack_minimum_added_cold_damage", 100);
            Set("attack_maximum_added_cold_damage", 100);
            Set("physical_damage_+%", -100);
            Set("melee_physical_damage_+%", 0);
            Set("attack_speed_+%", 1000);
            PlayerInventories.For(character, Program.Items).Add(PlayerInventories.Weapon1, 0, 0,
                ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!));
            conn.Pools = Program.PoolsFor(conn, 1) with { Accuracy = int.MaxValue };
            Assert.Equal(0, conn.Pools.AttackCriticalStrikeChance);
            Assert.Equal(150, conn.Pools.CriticalStrikeMultiplier);
            var variety = Program.Monsters.Find("Metadata/Monsters/Skeletons/SkeletonCasterFire")!;
            var target = new SpawnedMonster("SkeletonCasterFire@68", new(34, 32, 30), 0, 2, 5146)
            { Variety = variety, Level = 68, Hostile = hostile };
            conn.Monsters.Add(42, target);
            conn.Clock.Start();
            var bytes = new LoginWriter().U16(0x24).U32(42).U16(SkillCatalog.FirstSkillId).U16(0).U16(0x408).ToArray();
            var use = InstanceTransfer.ReadUseSkillOnObject(bytes);
            Assert.Equal((ushort)0x102, use.SkillId);
            var method = typeof(Program).GetMethod("OnUseObject", BindingFlags.NonPublic | BindingFlags.Static)!;
            var ciphers = new ConnectionCiphers(new byte[64], isClient: false);
            Task Use() => ((Task)method.Invoke(null,
                ["default-crit-route-test", writer.GetStream(), ciphers, conn, use, conn.Clock.Elapsed.TotalSeconds])!)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await Use();
            Assert.Equal(4926u, target.Life); // Same connection and target first take the ordinary 220 penetration hit.
            var commandFactory = typeof(Program).GetMethod("Commands", BindingFlags.NonPublic | BindingFlags.Static)!;
            conn.Commands = (ChatCommands)commandFactory.Invoke(null,
                ["default-crit-route-test", writer.GetStream(), ciphers, conn])!;
            Assert.True(await conn.Commands.RunAsync($"/set_stat critical_strikes_ignore_elemental_resistances {(ignore ? 1 : 0)}"));
            Assert.Equal(ignore ? 1 : 0, conn.PlayerStats[3626]);
            Assert.True(await conn.Commands.RunAsync($"/set_stat global_cannot_crit {(critical ? 0 : 1)}"));
            Assert.True(await conn.Commands.RunAsync("/set_stat override_weapon_base_critical_strike_chance 10000"));
            Assert.True(await conn.Commands.RunAsync($"/set_stat cannot_penetrate_or_ignore_elemental_resistances {(prohibit ? 1 : 0)}"));
            Assert.Equal(ignore ? 1 : 0, conn.PlayerStats[3626]);
            Assert.Equal(ignore ? 1 : 0, Program.CharacterPlayerStats(conn).GetValueOrDefault(3626u));
            Assert.Equal(critical ? 10000 : 0, conn.Pools.AttackCriticalStrikeChance);
            conn.Pools = conn.Pools with { Accuracy = int.MaxValue }; // Keep the unrelated accuracy roll deterministic.
            await Task.Delay(100); // Respect the actual fast attack's next-input deadline; do not reset action state.
            await Use();
            Assert.Equal(4926u - (uint)damage, target.Life);
            // A completed server write need not be available on the receiving socket yet.
            byte[] expected = InstanceProtocol.LifeUpdate(42, target.Life);
            var received = new List<byte>();
            var decrypt = new ConnectionCiphers(new byte[64], isClient: true);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            byte[] buffer = new byte[4096];
            while (received.ToArray().AsSpan().IndexOf(expected) < 0)
            {
                int count = await reader.GetStream().ReadAsync(buffer, deadline.Token);
                Assert.True(count > 0);
                byte[] chunk = buffer[..count];
                decrypt.Receive.Process(chunk);
                received.AddRange(chunk);
            }
        }
        finally { PlayerInventories.Forget(character); }
    }
}
