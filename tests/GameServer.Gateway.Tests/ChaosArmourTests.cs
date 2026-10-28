using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ChaosArmourTests
{
    static uint Key(string name) => Program.Stats.Key(name)!.Value;

    [Theory]
    [InlineData(1000, 50, 75, 1000, 355)]
    [InlineData(1000, 75, 75, 1000, 140)]
    [InlineData(1000, -100, 75, 1000, 1820)]
    [InlineData(1000, -999, 75, 1000, 2820)]
    [InlineData(1000, 999, 75, 1000, 140)]
    [InlineData(1000, 999, 999, 1000, 33)]
    [InlineData(1000, 75, 50, 1000, 355)]
    [InlineData(1000, 50, 75, 0, 500)]
    [InlineData(1000, 50, 75, -1000, 500)]
    [InlineData(1000, 50, 75, int.MaxValue, 50)]
    [InlineData(0, 50, 75, int.MaxValue, 0)]
    public void Chaos_hit_armour_reads_post_resistance_damage_and_keeps_the_existing_cap(
        int amount, int resistance, int maximum, int armour, int expected)
    {
        var pools = new CharacterStats.Pools(1, 100, 50, 0, 0, Armour: armour,
            ArmourAppliesToChaosDamage: true);
        int resisted = DamageModel.Mitigate(amount, resistance, maximum);
        Assert.Equal(expected, PlayerHitMitigation.AfterResistance(pools, DamageType.Chaos, resisted, 90, 90));
        // The denominator must read 500 at 50% resistance, not the raw 1000 (which would leave 415).
        Assert.Equal(armour, pools.Armour);
    }

    [Theory]
    [InlineData(-999)]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(999)]
    public void Boolean_off_is_identity_for_chaos_after_resistance(int resistance)
    {
        var pools = new CharacterStats.Pools(1, 100, 50, 0, 0, Armour: int.MaxValue);
        int resisted = DamageModel.Mitigate(1000, resistance);
        Assert.Equal(resisted, PlayerHitMitigation.AfterResistance(pools, DamageType.Chaos, resisted, 90, 90));
    }

    [Theory]
    [InlineData(DamageType.Physical, 430)]
    [InlineData(DamageType.Fire, 400)]
    [InlineData(DamageType.Cold, 400)]
    [InlineData(DamageType.Lightning, 400)]
    public void Other_types_keep_their_existing_reductions(DamageType type, int expected)
    {
        var ordinary = new CharacterStats.Pools(1, 100, 50, 0, 0, Armour: 1000);
        int resisted = type == DamageType.Physical ? 1000 : 500;
        Assert.Equal(expected, PlayerHitMitigation.AfterResistance(ordinary, type, resisted, 40, 20));
        Assert.Equal(expected, PlayerHitMitigation.AfterResistance(
            ordinary with { ArmourAppliesToChaosDamage = true }, type, resisted, 40, 20));
    }

    [Fact]
    public void The_flag_preserves_final_armour_conversion_and_Steel_Willed_offence()
    {
        var bonus = new CharacterStats.Bonus(Armour: 450, ArmourPct: 100, Evasion: 435, IronReflexes: true);
        var ordinary = CharacterStats.For(new(0, 0, 0, 0), 1, bonus);
        var enabled = CharacterStats.For(new(0, 0, 0, 0), 1,
            bonus with { ArmourAppliesToChaosDamage = true });
        Assert.Equal(ordinary, enabled with { ArmourAppliesToChaosDamage = false });
        using var game = new IncomingFixture();
        game.Conn.Passives.Add(2103); // Existing Steel Willed raw-source fixture.
        game.Conn.Pools = Program.PoolsFor(game.Conn, 100) with { Armour = 900 };
        var before = Program.CharacterPlayerStats(game.Conn);
        game.Conn.Pools = game.Conn.Pools with { ArmourAppliesToChaosDamage = true };
        var after = Program.CharacterPlayerStats(game.Conn);
        Assert.Equal(900, after[Key("physical_damage_reduction_rating")]);
        Assert.Equal(4, after[Key("attack_damage_+%")]);
        Assert.Equal(before.OrderBy(p => p.Key), after.OrderBy(p => p.Key));
    }

    [Fact]
    public void Rawnode29437_supplies_the_boolean_and_removal_rebuilds_it_without_admitting_Phrecia()
    {
        var tree = new PassiveTree();
        var node = Assert.IsType<PassiveTree.Node>(tree.Find(29437));
        Assert.Equal("Incorruptible", node.Name);
        Assert.True(node.IsAscendancy);
        Assert.Equal(12064u, Key(PlayerHitMitigation.ArmourAppliesToChaos));
        Assert.Contains((12064u, 1), node.Stats);
        using var game = new IncomingFixture();
        PlayerInventories.For(game.Conn.Character!, Program.Items).Add(PlayerInventories.BodyArmour, 0, 0,
            ItemCatalog.Create(Program.Items.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!));
        int ordinaryArmour = Program.PoolsFor(game.Conn, 100).Armour;
        Assert.True(ordinaryArmour > 0);
        // Inject only a raw source. This bypasses allocation and establishes no class/chooser support.
        game.Conn.Passives.Add(29437);
        game.Conn.Pools = Program.PoolsFor(game.Conn, 100);
        Assert.True(game.Conn.Pools.ArmourAppliesToChaosDamage);
        Assert.Equal(ordinaryArmour, game.Conn.Pools.Armour);
        Assert.Equal(1, Program.CharacterPlayerStats(game.Conn)[12064]);
        game.Conn.Passives.Remove(29437);
        game.Conn.Pools = Program.PoolsFor(game.Conn, 100);
        Assert.False(game.Conn.Pools.ArmourAppliesToChaosDamage);
        Assert.Equal(ordinaryArmour, game.Conn.Pools.Armour);
        game.Conn.Passives.Add(29437);
        game.Conn.Pools = Program.PoolsFor(game.Conn, 100);
        Assert.True(game.Conn.Pools.ArmourAppliesToChaosDamage);
        Assert.Contains((12064u, 1), new PassiveTree().Find(29437)!.Stats);
        Assert.Empty(game.Conn.MainAscendancy.Allocated);
    }

    [Fact]
    public async Task Parsed_chat_commands_replace_zero_and_remove_the_flag_then_fresh_connections_clear_dev_inputs()
    {
        using var game = new IncomingFixture();
        game.Conn.Passives.Add(29437);
        game.Conn.Pools = Program.PoolsFor(game.Conn, 100);
        await game.Command("/set_stat base_armour_applies_to_chaos_damage 0");
        Assert.False(game.Conn.Pools.ArmourAppliesToChaosDamage);
        Assert.Equal(0, Program.CharacterPlayerStats(game.Conn)[12064]);
        game.Defences();
        await game.Command("/hit chaos 1000");
        Assert.Equal(9500u, game.Conn.PlayerLife);
        await game.Command("/set_stat 12064 off");
        Assert.True(game.Conn.Pools.ArmourAppliesToChaosDamage);
        Assert.DoesNotContain(12064u, game.Conn.PlayerStats.Keys);
        Assert.Equal(1, Program.CharacterPlayerStats(game.Conn)[12064]);
        game.Defences();
        await game.Command("/hit chaos 1000");
        Assert.Equal(9145u, game.Conn.PlayerLife);
        game.Conn.Passives.Remove(29437);
        await game.Command("/set_stat 12064 1");
        Assert.True(game.Conn.Pools.ArmourAppliesToChaosDamage);
        await game.Command("/set_stat 12064 off");
        Assert.False(game.Conn.Pools.ArmourAppliesToChaosDamage);
        await game.Command("/set_stat 12064 1");
        using var reloaded = new IncomingFixture();
        Assert.False(reloaded.Conn.Pools.ArmourAppliesToChaosDamage);
        Assert.DoesNotContain(12064u, reloaded.Conn.PlayerStats.Keys);
        Assert.Contains("29% armour", await game.ReceivedText());
    }

    [Theory]
    [InlineData("/hit chaos 1000", false, false, 355)]
    [InlineData("/hit chaos 1000", true, false, 0)]
    [InlineData("/spellhit chaos 1000", false, true, 213)]
    [InlineData("/blockhit attack chaos 1000", false, false, 355)]
    public async Task Actual_commands_keep_immunity_suppression_and_chaos_ES_bypass(
        string command, bool immune, bool suppress, int expected)
    {
        using var game = new IncomingFixture();
        await game.Command("/set_stat 12064 1");
        if (immune) game.Set("chaos_immunity", 1);
        game.Set("base_additional_physical_damage_reduction_%", 90);
        game.Set("additional_elemental_damage_reduction_%", 90);
        game.Defences();
        if (suppress) game.Conn.Pools = game.Conn.Pools with { SpellSuppressionChance = 100 };
        await game.Command(command);
        Assert.Equal(10_000u - (uint)expected, game.Conn.PlayerLife);
        Assert.Equal(300u, game.Conn.EnergyShield.Current);
        Assert.Equal(1000, game.Conn.Pools.Armour);
        byte[] expectedLife = InstanceProtocol.LifeUpdate(Program.PlayerId, game.Conn.PlayerLife);
        Assert.True((await game.Received(expectedLife)).AsSpan().IndexOf(expectedLife) >= 0);
    }

    [Theory]
    [InlineData("/hit chaos 1000", 284)]
    [InlineData("/spellhit chaos 1000", 170)]
    [InlineData("/blockhit spell chaos 1000", 284)]
    public async Task Hit_commands_keep_Fortification_after_armour_and_before_suppression(string command, int expected)
    {
        using var game = new IncomingFixture();
        await game.Command("/set_stat 12064 1");
        Program.FortificationOf(game.Conn).State.GrantExplicitStacks(game.Conn.Clock.Elapsed.TotalSeconds, 20);
        game.Defences();
        game.Conn.Pools = game.Conn.Pools with { SpellSuppressionChance = 100 };
        await game.Command(command);
        Assert.Equal(10_000u - (uint)expected, game.Conn.PlayerLife);
        Assert.Equal(300u, game.Conn.EnergyShield.Current);
    }

    [Theory]
    [InlineData(false, false, 764, 0)]
    [InlineData(true, false, 648, 0)]
    [InlineData(true, true, 0, 300)]
    public async Task Real_monster_swing_composes_mixed_damage_Fortification_ES_and_block(
        bool enabled, bool blocked, int lifeLoss, uint esRemaining)
    {
        using var game = new IncomingFixture();
        if (enabled) await game.Command("/set_stat 12064 1");
        game.Set("base_additional_physical_damage_reduction_%", 40);
        game.Set("additional_elemental_damage_reduction_%", 20);
        Program.FortificationOf(game.Conn).State.GrantExplicitStacks(game.Conn.Clock.Elapsed.TotalSeconds, 20);
        game.Defences();
        if (blocked) game.Conn.Pools = game.Conn.Pools with { BlockChance = 90, AttackBlockLucky = true };
        var hit = DamagePacket.Of(DamageType.Physical, 1000, 1000)
            .Add(DamageType.Fire, new(1000, 1000)).Add(DamageType.Chaos, new(1000, 1000));
        for (int attempt = 0; attempt < 32; attempt++)
        {
            game.Conn.PlayerLife = 10_000;
            game.Conn.EnergyShield.SetMaximum(300, refill: true);
            await game.Swing(hit);
            if (!blocked || game.Conn.PlayerLife == 10_000) break;
        }
        // Physical: 430, fire: 400, chaos: 355/500. Fortification floors the whole sum and chaos share separately.
        // Enabled: 948 total, 284 bypass, 664 non-chaos; ES 300 absorbs first, leaving 648 life loss.
        Assert.Equal(10_000u - (uint)lifeLoss, game.Conn.PlayerLife);
        Assert.Equal(esRemaining, game.Conn.EnergyShield.Current);
    }

    [Fact]
    public void Chaos_damage_over_time_stays_on_the_resistance_only_model()
    {
        var pools = new CharacterStats.Pools(1, 100, 50, 0, 0, Armour: 1000,
            ChaosResistance: 50, ArmourAppliesToChaosDamage: true);
        var poison = new DamageOverTime(DamageType.Chaos, 1000, 1, Ailment.Poison);
        var resisted = poison with { DamagePerSecond = DamageModel.Mitigate(poison.DamagePerSecond,
            pools.ChaosResistance, pools.ChaosMaxResistance) };
        Assert.Equal(500, resisted.Tick(1).Damage);
        Assert.Equal(355, PlayerHitMitigation.AfterResistance(pools, DamageType.Chaos, 500));
    }

    [Theory]
    [InlineData(3, 50u)]
    [InlineData(4, 58u)]
    [InlineData(7, 58u)]
    [InlineData(8, 66u)]
    public void Chaos_armour_preserves_Strength_mana_groups_and_Chaos_Inoculation(int strength, uint mana)
    {
        var c = SkillCatalog.ClassInfo.Witch with { Strength = strength, Intelligence = 20 };
        var bonus = new CharacterStats.Bonus(ManaPerFourStrength: 8);
        var ordinary = CharacterStats.For(c, 1, bonus);
        var chaosArmour = CharacterStats.For(c, 1, bonus with { ArmourAppliesToChaosDamage = true });
        Assert.Equal(mana, chaosArmour.Mana);
        Assert.Equal(ordinary, chaosArmour with { ArmourAppliesToChaosDamage = false });
        var inoculated = CharacterStats.For(c, 1,
            bonus with { ArmourAppliesToChaosDamage = true, ChaosInoculation = true });
        Assert.Equal(1u, inoculated.Life);
        Assert.Equal(mana, inoculated.Mana);
        Assert.True(inoculated.ArmourAppliesToChaosDamage);
    }

    [Fact]
    public async Task Parsed_raw_source_changes_keep_mana_and_chaos_armour_independent_without_refilling()
    {
        using var game = new IncomingFixture();
        // Exercise shared raw consumers, without allocating or admitting a Phrecia class.
        game.Conn.MainAscendancy = new(Nodes: [38080, 29437]);
        game.Conn.Pools = Program.PoolsFor(game.Conn, 100);
        uint combinedMana = 135; // Witch: (650 + floor(14/4)*8)*.2, rounded.
        uint convertedMana = 130; // Removing only mana per Strength leaves conversion active.
        Assert.Equal(combinedMana, game.Conn.Pools.Mana);
        Assert.Equal(1078, game.Conn.Pools.Armour);
        Assert.True(game.Conn.Pools.ArmourAppliesToChaosDamage);
        game.Conn.PlayerMana = combinedMana;

        await game.Command("/set_stat X_mana_per_4_strength 0");
        Assert.Equal(convertedMana, game.Conn.Pools.Mana);
        Assert.Equal(convertedMana, game.Conn.PlayerMana);
        Assert.Equal(1040, game.Conn.Pools.Armour);
        Assert.True(game.Conn.Pools.ArmourAppliesToChaosDamage);
        byte[] expectedMana = InstanceProtocol.LifeUpdate(0x1000, convertedMana, 0, pool: 1);
        byte[] wire = await game.Received(expectedMana);
        Assert.True(wire.AsSpan().IndexOf(expectedMana) >= 0);

        await game.Command("/set_stat X_mana_per_4_strength off");
        Assert.Equal(combinedMana, game.Conn.Pools.Mana);
        Assert.Equal(convertedMana, game.Conn.PlayerMana); // Restoring a source never refills current Mana.
        Assert.True(game.Conn.Pools.ArmourAppliesToChaosDamage);
        await game.Command("/set_stat base_armour_applies_to_chaos_damage 0");
        Assert.False(game.Conn.Pools.ArmourAppliesToChaosDamage);
        Assert.Equal(combinedMana, game.Conn.Pools.Mana);
        await game.Command("/set_stat base_armour_applies_to_chaos_damage off");
        Assert.True(game.Conn.Pools.ArmourAppliesToChaosDamage);
        Assert.Equal(combinedMana, game.Conn.Pools.Mana);
        Assert.Equal(convertedMana, game.Conn.PlayerMana);
        Assert.Null(game.Conn.MainAscendancy.Selection);
    }

    sealed class IncomingFixture : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new() { NoDelay = true };
        readonly TcpClient reader;
        readonly ConnectionCiphers ciphers = new(new byte[64], false);
        readonly ConnectionCiphers receive = new(new byte[64], true);
        public ClientState Conn { get; }

        public IncomingFixture()
        {
            TestSaves.Ensure();
            Conn = new(new(ushort.MaxValue, "IncomingChaos", 128, 128, 32, 32, null, []), 0, 1)
            { Character = "ChaosArmour" + Guid.NewGuid().ToString("N"), ClassRow = 1,
                PlayerLife = 10_000, PlayerMana = 1000, PlayerSent = true };
            Conn.Pools = Program.PoolsFor(Conn, 100);
            Conn.Clock.Start();
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
            Conn.Commands = (ChatCommands)typeof(Program).GetMethod("Commands",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
                ["chaos-armour-test", writer.GetStream(), ciphers, Conn])!;
        }

        public void Set(string stat, int value) => Conn.PlayerStats[Key(stat)] = value;

        public void Defences()
        {
            // Fixed final defences isolate mitigation from equipment formulas; production never rewrites Armour.
            Conn.Pools = Program.PoolsFor(Conn, 100) with { Life = 10_000, Mana = 1000, Armour = 1000,
                FireResistance = 50, ChaosResistance = 50, EnergyShield = 300, Evasion = 0,
                BlockChance = 0, SpellBlockChance = 0 };
            Conn.EnergyShield.SetMaximum(300, refill: true);
        }

        public async Task Command(string line)
        {
            var input = new LoginWriter().U16(InstanceChat.ChatId).WStr(line).U8(0).ToArray();
            var messages = InstanceProtocol.SplitClientMessages(input, out var partial, out var unknown);
            Assert.Empty(partial); Assert.Empty(unknown); Assert.Single(messages);
            await Program.OnClientMessages("chaos-armour-test", writer.GetStream(), ciphers, messages, [], Conn)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async Task Swing(DamagePacket damage)
        {
            var monster = new SpawnedMonster("Chaos fixture", new(34, 32, 30), 0, 2, 100)
            { Attack = new(SkillCatalog.FirstSkillId, .002, 10, damage) };
            Conn.Monsters[42] = monster;
            Conn.EvasionEntropy = 100; // Force this contact to hit without changing the production RNG.
            Conn.LastEvasionAt = Conn.Clock.Elapsed.TotalSeconds;
            await ((Task)typeof(Program).GetMethod("MonsterSwing", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, ["chaos-armour-test", writer.GetStream(), ciphers, Conn, 42u, monster,
                    (34, 32), (32, 32), Conn.IncomingHitEpoch])!).WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async Task<byte[]> Received(byte[] expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var received = new MemoryStream();
            byte[] buffer = new byte[4096];
            while (true)
            {
                int count = await reader.GetStream().ReadAsync(buffer, timeout.Token);
                if (count == 0) throw new EndOfStreamException("Expected gateway message was not received");
                byte[] chunk = buffer[..count];
                receive.Receive.Process(chunk);
                received.Write(chunk);
                if (received.GetBuffer().AsSpan(0, (int)received.Length).IndexOf(expected) >= 0)
                    return received.ToArray();
            }
        }

        public async Task<string> ReceivedText() => System.Text.Encoding.ASCII.GetString(
            await Received(System.Text.Encoding.ASCII.GetBytes("29% armour")));

        public void Dispose()
        {
            Conn.Closed = true;
            writer.Dispose(); reader.Dispose(); listener.Stop();
            PlayerInventories.Forget(Conn.Character!);
        }
    }
}
