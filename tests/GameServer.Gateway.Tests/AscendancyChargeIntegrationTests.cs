using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    static void ChargeInput(GatewayCharacter game, string name, int value) =>
        game.State.PlayerStats[Program.Stats.Key(name)!.Value] = value;

    static SpawnedMonster ChargeTarget(GatewayCharacter game, uint id, int x, int y, uint life = 10000)
    {
        var target = new SpawnedMonster("charge integration target", new WalkEstimator(x, y, 0), 0, 0, life);
        game.State.Monsters[id] = target;
        return target;
    }

    static Task<(string? Error, byte[] Bytes)> ChargeExchange(GatewayCharacter game,
        Func<NetworkStream, ConnectionCiphers, Task<string?>> work) =>
        (Task<(string? Error, byte[] Bytes)>)typeof(GatewayCharacter)
            .GetMethod("Exchange", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(game, [work])!;

    static Task<(string? Error, byte[] Bytes)> ChargeArea(GatewayCharacter game, string name) =>
        ChargeExchange(game, async (stream, cipher) =>
        {
            var gem = game.GemEffects().Single(g => g.Name == name);
            await ProgramValue<Task>("AreaSkillHit", "charge integration", stream, cipher, game.State,
                gem.Id, gem, (32, 32), (32, 32), true, (ushort)0, null, null);
            return null;
        });

    [Theory]
    [InlineData(3, 2, "Whisperer", 2266, 0, 4, 4)]
    [InlineData(6, 5, "Polytheist", 1830, 3, 3, 3)]
    public async Task Real_minimum_charge_nodes_allocate_reload_and_refund_through_the_authoritative_passive_path(
        int classRow, byte baseClass, string choice, ushort node, int endurance, int frenzy, int power)
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter(classRow: classRow, baseClass: baseClass, choice: choice,
            refundPoints: 60, level: 30);
        var definition = Program.Ascendancies.Find(game.State.MainAscendancy.Selection!)!;
        Assert.Null((await game.Change(PathTo(definition, node), allocate: true)).Error);
        Check(game.State, endurance, frenzy, power);
        var buffs = Program.ChargePresentation(game.State);
        Assert.Equal(new[] { endurance, frenzy, power }.Count(n => n > 0), buffs.Count);
        Assert.All(buffs, b => Assert.Null(b.Remaining));
        Check(game.Reload(), endurance, frenzy, power);
        Assert.Null((await game.Change([node], allocate: false, refundPoints: true)).Error);
        Check(game.State, 0, 0, 0);
        Check(game.Reload(), 0, 0, 0);

        static void Check(ClientState conn, int e, int f, int p)
        {
            conn.Pools = Program.PoolsFor(conn, conn.Pools.Level);
            var actual = Program.CharacterPlayerStats(conn);
            foreach (var (name, expected) in new[] { ("endurance", e), ("frenzy", f), ("power", p) })
            {
                Assert.Equal(expected, actual[Program.Stats.Key($"current_{name}_charges")!.Value]);
                Assert.Equal(expected, actual[Program.Stats.Key($"minimum_{name}_charges")!.Value]);
                Assert.Equal(0, actual[Program.Stats.Key($"current_removable_{name}_charges")!.Value]);
            }
        }
    }

    [Fact]
    public async Task Actual_Fireball_projectile_commits_a_hit_and_one_kill_without_double_awarding_the_async_kill()
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter(level: 30);
        EquipBlueGems(game, "Fireball");
        ChargeInput(game, "global_cannot_crit", 1);
        ChargeInput(game, "gain_power_charge_on_non_critical_strike_%", 100);
        ChargeInput(game, "endurance_charge_on_kill_%", 100);
        game.State.Pools = Program.PoolsFor(game.State, 30);
        var target = ChargeTarget(game, 0x4000, 48, 32, life: 1);
        Assert.True((await game.Cast("Fireball")).Accepted);
        Assert.True(target.Dead);
        Assert.Equal(0u, target.Life);
        Assert.Equal(1, game.PlayerStat("current_power_charges"));
        Assert.Equal(1, game.PlayerStat("current_endurance_charges"));
        Assert.Equal(0, game.PlayerStat("current_frenzy_charges"));
    }

    [Fact]
    public async Task Fireball_explosion_keeps_its_conversion_snapshot_when_the_first_target_grants_a_charge_on_kill()
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter(level: 30);
        EquipBlueGems(game, "Fireball");
        ChargeInput(game, "global_cannot_crit", 1);
        ChargeInput(game, "endurance_charge_on_kill_%", 100);
        ChargeInput(game, "fire_damage_%_to_add_as_chaos_per_endurance_charge", 100);
        ChargeInput(game, "spell_minimum_added_fire_damage", 500);
        ChargeInput(game, "spell_maximum_added_fire_damage", 500);
        game.State.Pools = Program.PoolsFor(game.State, 30);
        var primary = ChargeTarget(game, 0x4000, 48, 32, life: 1);
        var secondary = ChargeTarget(game, 0x4001, 49, 32);
        Assert.True((await game.Cast("Fireball")).Accepted);
        Assert.True(primary.Dead);
        Assert.Equal(1, game.PlayerStat("current_endurance_charges"));
        // Level1 Fireball is9..14 +500 addedfire *3 effectiveness. The killing primary cannot add chaos to a later
        // target in its same explosion; the next skill use receives that100% gain-as-chaos from the held charge.
        Assert.InRange(secondary.MaxLife - secondary.Life, 1509u, 1514u);
        uint before = secondary.Life;
        Assert.True((await game.Cast("Fireball")).Accepted);
        Assert.InRange(before - secondary.Life, 3018u, 3028u);
        Assert.Equal(1, game.PlayerStat("current_endurance_charges"));
    }

    [Fact]
    public async Task Frenzy_area_contact_grants_once_per_use_and_cannot_strengthen_later_targets_of_that_contact()
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter(level: 30);
        var frenzy = Program.Items.FindGem("Frenzy")!.Value;
        game.Items.Add(PlayerInventories.Helmet, 0, 0, ItemCatalog.Create(Program.Items.Find("Iron Hat")!) with
        {
            Sockets = [new ItemSocket(SocketColour.Green,
                ItemCatalog.Create(frenzy.Type) with { GemVariant = frenzy.Variant.Index })], Links = [0],
        });
        game.Items.Add(PlayerInventories.Weapon1, 0, 0,
            ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/OneHandMaces/OneHandMace1")!));
        ChargeInput(game, "global_cannot_crit", 1);
        ChargeInput(game, "active_skill_base_area_of_effect_radius", 10);
        ChargeInput(game, "attack_minimum_added_physical_damage", 1000);
        ChargeInput(game, "attack_maximum_added_physical_damage", 1000);
        game.State.Pools = Program.PoolsFor(game.State, 30);
        var targets = new[] { ChargeTarget(game, 0x4000, 32, 32), ChargeTarget(game, 0x4001, 33, 32),
            ChargeTarget(game, 0x4002, 32, 33) };
        Assert.Null((await ChargeArea(game, "Frenzy")).Error);
        int[] first = targets.Select(t => (int)(t.MaxLife - t.Life)).ToArray();
        Assert.All(first, damage => Assert.True(damage > 1000));
        Assert.InRange(first.Max() - first.Min(), 0, 20); // Weapon variation only, no new 4% multiplier mid-contact.
        Assert.Equal(1, game.PlayerStat("current_frenzy_charges"));
        uint[] before = targets.Select(t => t.Life).ToArray();
        Assert.Null((await ChargeArea(game, "Frenzy")).Error);
        int[] second = targets.Select((t, i) => (int)(before[i] - t.Life)).ToArray();
        Assert.All(second, damage => Assert.True(damage > first.Max() * 1.03));
        Assert.Equal(2, game.PlayerStat("current_frenzy_charges"));
    }

    [Fact]
    public async Task Power_charges_and_Sea_Legs_feed_attack_and_spell_critical_chance_once_without_a_generic_override()
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter(classRow: 5, baseClass: 6, choice: "Surfcaster", level: 30);
        game.Items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Row(384)!));
        ChargeInput(game, ResistanceOvercap.LightningCritFlag, 1);
        ChargeInput(game, "base_lightning_damage_resistance_%", 120);
        game.State.Pools = Program.PoolsFor(game.State, 30);
        Assert.False(game.State.PlayerStats.ContainsKey(Program.Stats.Key("critical_strike_chance_+%")!.Value));
        Assert.Null((await game.Command("/charges power 3")).Error);
        Assert.Equal(3, game.PlayerStat("current_power_charges"));
        Assert.Equal(45, game.State.Pools.LightningOvercapCritPct);
        Assert.Equal(195, game.State.Pools.CriticalStrikeChancePct);
        Assert.Equal(2449, game.State.Pools.AttackCriticalStrikeChance); // 830 * (1 + 150% + 45%), half-up once.
        Assert.Equal(1475, game.State.Pools.SpellCriticalStrikeChance);
        Assert.Equal(195, game.PlayerStat("critical_strike_chance_+%"));
    }

    [Fact]
    public async Task Live_charge_counts_scale_complete_attacks_and_spells_but_RF_keeps_only_generic_Frenzy_more()
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter(level: 30);
        ChargeInput(game, "object_inherent_spell_damage_+%_final_per_power_charge", 10);
        ChargeInput(game, "attack_damage_+%_final_from_rage", 30);
        ChargeInput(game, "attack_minimum_added_fire_damage", 100);
        ChargeInput(game, "attack_maximum_added_fire_damage", 100);
        game.State.Pools = Program.PoolsFor(game.State, 30);
        var rf = new RighteousFireState(1, 2, null, 3, 6000, 5400, 18);
        var before = Program.CharacterPlayerStats(game.State);
        var weapon = new SkillCatalog.Unarmed(1000, 100, 100, 8);
        var attackBefore = Program.AttackHit(weapon, before, melee: true);
        var spellBase = Program.Skills.HitDamage(Program.Skills.EffectRow("Fireball"), 1);
        int Before(string name) => before.GetValueOrDefault(Program.Stats.Key(name)!.Value);
        var spellBefore = SpellDamage.Packet(spellBase, Before);
        double rfBefore = Program.RighteousFireDamagePerSecond(game.State, rf);
        Assert.Null((await game.Command("/charges frenzy 2")).Error);
        Assert.Null((await game.Command("/charges power 3")).Error);
        var after = Program.CharacterPlayerStats(game.State);
        int After(string name) => after.GetValueOrDefault(Program.Stats.Key(name)!.Value);
        var attackAfter = Program.AttackHit(weapon, after, melee: true);
        Assert.Equal(attackBefore[DamageType.Physical].Min * 1.08, attackAfter[DamageType.Physical].Min, 8);
        Assert.Equal(attackBefore[DamageType.Fire].Min * 1.08, attackAfter[DamageType.Fire].Min, 8);
        Assert.Equal(spellBefore.MinTotal * 1.08 * 1.3, SpellDamage.Packet(spellBase, After).MinTotal, 8);
        Assert.Equal(rfBefore * 1.08, Program.RighteousFireDamagePerSecond(game.State, rf), 8);
    }

    [Fact]
    public async Task Endurance_reduction_adds_to_physical_armour_without_leaking_into_Incorruptible_chaos_armour()
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter(classRow: 0, baseClass: 1, choice: "Behemoth", refundPoints: 60, level: 30);
        game.Items.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(Program.Items.Find("Plate Vest")!));
        var definition = Program.Ascendancies.Find(game.State.MainAscendancy.Selection!)!;
        Assert.Null((await game.Change(PathTo(definition, 29437), allocate: true)).Error);
        async Task<uint> Damage(string type)
        {
            await game.Command("/heal");
            game.State.Pools = game.State.Pools with { ChaosResistance = 50 };
            uint before = game.State.PlayerLife;
            await game.Command($"/hit {type} 100");
            return before - game.State.PlayerLife;
        }
        uint ordinaryPhysical = await Damage("physical"), ordinaryChaos = await Damage("chaos");
        Assert.Null((await game.Command("/charges endurance 3")).Error);
        Assert.Equal(3, game.PlayerStat("current_endurance_charges"));
        Assert.Equal(ordinaryPhysical - 12, await Damage("physical"));
        Assert.Equal(ordinaryChaos, await Damage("chaos"));
        await game.Command("/heal");
        game.State.Pools = game.State.Pools with { FireResistance = 50, Ward = 2 };
        uint life = game.State.PlayerLife;
        await game.Command("/hit fire 7");
        Assert.Equal(2u, life - game.State.PlayerLife); // 7 -> resistance rounds to4 -> 12% reduction rounds to4 -> Ward absorbs2.
        Assert.True(game.State.Ward.Broken);
    }

    [Fact]
    public async Task Actual_incoming_hit_spellhit_and_blockhit_commands_enqueue_only_their_committed_event_type()
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter(level: 30);
        ChargeInput(game, "chance_to_gain_endurance_charge_when_hit_%", 100);
        ChargeInput(game, "chance_to_gain_frenzy_charge_on_block_attack_%", 100);
        ChargeInput(game, "power_charge_on_spell_block_%_chance", 100);
        game.State.Pools = Program.PoolsFor(game.State, 30);
        await game.Command("/hit physical 10");
        Assert.Equal(1, game.PlayerStat("current_endurance_charges"));
        game.State.Pools = game.State.Pools with { BlockChance = 100, SpellBlockChance = 100 };
        uint life = game.State.PlayerLife;
        await game.Command("/blockhit attack physical 100");
        // The real engine caps block below 100. Assert against the committed life result, without making this test
        // depend on Random.Shared choosing a block; the deterministic adapter cases cover both block branches too.
        bool attackBlocked = life == game.State.PlayerLife;
        int endurance = attackBlocked ? 1 : 2;
        Assert.Equal(attackBlocked ? 1 : 0, game.PlayerStat("current_frenzy_charges"));
        Assert.Equal(endurance, game.PlayerStat("current_endurance_charges"));
        life = game.State.PlayerLife;
        await game.Command("/blockhit spell fire 100");
        bool spellBlocked = life == game.State.PlayerLife;
        endurance += spellBlocked ? 0 : 1;
        Assert.Equal(spellBlocked ? 1 : 0, game.PlayerStat("current_power_charges"));
        Assert.Equal(endurance, game.PlayerStat("current_endurance_charges"));
        await game.Command("/charges endurance 0");
        Assert.Equal(0, game.PlayerStat("current_endurance_charges"));
        await game.Command("/spellhit fire 10");
        Assert.Equal(1, game.PlayerStat("current_endurance_charges"));
        Assert.Equal(attackBlocked ? 1 : 0, game.PlayerStat("current_frenzy_charges"));
        Assert.Equal(spellBlocked ? 1 : 0, game.PlayerStat("current_power_charges"));
    }

    [Fact]
    public async Task Actual_RF_kill_grants_on_kill_charges_without_fabricating_a_noncritical_hit()
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter(level: 30);
        ChargeInput(game, "add_frenzy_charge_on_kill_%_chance", 100);
        ChargeInput(game, "gain_power_charge_on_non_critical_strike_%", 100);
        var target = ChargeTarget(game, 0x4000, 32, 32, life: 1);
        var rf = new RighteousFireState(1, 2, null, 3, 6000, 5400, 18);
        var result = await ChargeExchange(game, async (stream, cipher) =>
        {
            await ProgramValue<Task>("BurnAround", "charge integration", stream, cipher, game.State, rf, .1);
            return null;
        });
        Assert.Null(result.Error);
        Assert.True(target.Dead);
        Assert.Equal(0u, target.Life);
        Assert.Equal(1, game.PlayerStat("current_frenzy_charges"));
        Assert.Equal(0, game.PlayerStat("current_power_charges"));
    }
}
