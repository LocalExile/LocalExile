using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

[CollectionDefinition("Attack base critical Gateway", DisableParallelization = true)]
public sealed class AttackBaseCriticalGatewayCollection;

[Collection("Attack base critical Gateway")]
public sealed class AttackBaseCriticalGatewayTests : IDisposable
{
    readonly string _name = "CritBase" + Guid.NewGuid().ToString("N");
    static MethodInfo Method(string name) => typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    public AttackBaseCriticalGatewayTests()
    {
        TestSaves.Ensure();
    }

    ClientState Character(bool armed)
    {
        var conn = new ClientState(new(0, "CritTest", 128, 128, 32, 32, null, []), 0, 1)
        { Character = _name, ClassRow = 1 };
        if (armed)
            PlayerInventories.For(_name, Program.Items).Add(PlayerInventories.Weapon1, 0, 0,
                ItemCatalog.Create(Program.Items.Row(384)!));
        return conn;
    }

    static void Set(ClientState conn, string name, int value) => conn.PlayerStats[Program.Stats.Key(name)!.Value] = value;
    static CharacterStats.Pools Pools(ClientState conn) =>
        (CharacterStats.Pools)Method("PoolsFor").Invoke(null, [conn, 20])!;

    [Theory]
    [InlineData(true, 800)]
    [InlineData(false, 1000)]
    public void Actual_pools_apply_grants_and_raw_increases_then_clear_them(bool armed, int chance)
    {
        var conn = Character(armed);
        Set(conn, armed ? AttackBaseCriticalStrike.WeaponOverrideStat : AttackBaseCriticalStrike.UnarmedBaseStat, chance);
        Set(conn, "critical_strike_chance_+%", 100);
        conn.Pools = Pools(conn);
        Assert.Equal(chance * 2, conn.Pools.AttackCriticalStrikeChance);
        Assert.Equal(100, conn.Pools.CriticalStrikeChancePct);
        Assert.Equal(100, conn.Pools.SpellCriticalStrikeChancePct);
        Assert.Equal(1000, conn.Pools.SpellCriticalStrikeChance);
        var shown = (Dictionary<uint, int>)Method("CharacterPlayerStats").Invoke(null, [conn])!;
        Assert.Equal(chance * 2, shown[Program.Stats.Key("main_hand_critical_strike_chance")!.Value]);
        Assert.Equal(100, shown[Program.Stats.Key("critical_strike_chance_+%")!.Value]);
        Assert.Equal(conn.Pools.SpellCriticalStrikeChance,
            SpellCriticalStrike.Chance(500, Program.Stats, shown, new Dictionary<uint, int>(),
                conn.Pools.SpellCriticalStrikeChancePct));
        Set(conn, armed ? AttackBaseCriticalStrike.WeaponOverrideStat : AttackBaseCriticalStrike.UnarmedBaseStat, 0);
        Assert.Equal(armed ? 1660 : 0, Pools(conn).AttackCriticalStrikeChance);
    }

    [Theory]
    [InlineData("global_cannot_crit")]
    [InlineData("virtual_global_cannot_crit")]
    [InlineData("cannot_critical_strike_with_attacks")]
    public void Actual_pools_enforce_cannot_crit_even_with_a_positive_weapon_override(string prohibition)
    {
        var conn = Character(true);
        Set(conn, AttackBaseCriticalStrike.WeaponOverrideStat, 900);
        Set(conn, prohibition, 1);
        conn.Pools = Pools(conn);
        Assert.Equal(0, conn.Pools.AttackCriticalStrikeChance);
        int expectedSpell = prohibition == "cannot_critical_strike_with_attacks" ? 500 : 0;
        Assert.Equal(expectedSpell, conn.Pools.SpellCriticalStrikeChance);
        var shown = Program.CharacterPlayerStats(conn);
        Assert.Equal(expectedSpell, shown[Program.Stats.Key("spell_critical_strike_chance")!.Value]);
        Assert.Equal(expectedSpell, SpellCriticalStrike.Chance(500, Program.Stats, shown,
            new Dictionary<uint, int>(), conn.Pools.SpellCriticalStrikeChancePct));
        Set(conn, prohibition, 0);
        Assert.Equal(900, Pools(conn).AttackCriticalStrikeChance);
    }

    [Fact]
    public void A_debug_base_override_replaces_a_passive_value_instead_of_adding_to_it()
    {
        var conn = Character(true);
        conn.Passives.Add(17315); // Overwhelm: supplies 800 through the real passive stat catalog.
        Assert.Equal(800, Pools(conn).AttackCriticalStrikeChance);
        Set(conn, AttackBaseCriticalStrike.WeaponOverrideStat, 900);
        Assert.Equal(900, Pools(conn).AttackCriticalStrikeChance);
        Set(conn, AttackBaseCriticalStrike.WeaponOverrideStat, 0);
        Assert.Equal(830, Pools(conn).AttackCriticalStrikeChance);
        conn.PlayerStats.Clear();
        Assert.Equal(800, Pools(conn).AttackCriticalStrikeChance);
    }

    [Theory]
    [InlineData(0, 0, 1204, 725)]
    [InlineData(100, 40, 2034, 1425)]
    public void Debug_increases_replace_complete_aura_inputs_then_add_Sea_Legs_once(
        int generic, int spell, int expectedAttack, int expectedSpell)
    {
        var conn = Character(true);
        var skills = Program.Skills;
        conn.Auras[0x103] = new(0x103, skills.EffectRow("Precision"), "Precision", "Precision", 1, 1293, 22, 0);
        conn.Auras[0x104] = new(0x104, skills.EffectRow("Zealotry"), "Zealotry", "Zealotry", 2, 0, 0, 5000);
        Set(conn, ResistanceOvercap.LightningCritFlag, 1);
        Set(conn, "base_lightning_damage_resistance_%", 120);
        var baseline = Pools(conn);
        Assert.True(baseline.CriticalStrikeChancePct > 45);
        Assert.True(baseline.SpellCriticalStrikeChancePct > baseline.CriticalStrikeChancePct);

        Set(conn, "critical_strike_chance_+%", generic);
        Set(conn, "spell_critical_strike_chance_+%", spell);
        conn.Pools = Pools(conn);
        Assert.Equal(45, conn.Pools.LightningOvercapCritPct);
        Assert.Equal(generic + 45L, conn.Pools.CriticalStrikeChancePct);
        Assert.Equal(generic + spell + 45L, conn.Pools.SpellCriticalStrikeChancePct);
        Assert.Equal(expectedAttack, conn.Pools.AttackCriticalStrikeChance);
        Assert.Equal(expectedSpell, conn.Pools.SpellCriticalStrikeChance);
        var shown = Program.CharacterPlayerStats(conn);
        Assert.Equal(generic + 45, shown[Program.Stats.Key("critical_strike_chance_+%")!.Value]);
        Assert.Equal(expectedAttack, shown[Program.Stats.Key("main_hand_critical_strike_chance")!.Value]);
        Assert.Equal(expectedSpell, SpellCriticalStrike.Chance(500, Program.Stats, shown,
            new Dictionary<uint, int>(), conn.Pools.SpellCriticalStrikeChancePct));
    }

    [Theory]
    [InlineData(int.MaxValue, 0, 0, 50, 2147483647L, 2147483697L, 10000, 10000)]
    [InlineData(int.MaxValue, 0, 50, 0, 2147483697L, 2147483647L, 10000, 10000)]
    [InlineData(int.MinValue, 0, -50, -50, -2147483698L, -2147483698L, 0, 0)]
    [InlineData(int.MaxValue, 50, -int.MaxValue, int.MinValue, 50L, -1L, 1350, 495)]
    [InlineData(int.MinValue, -50, int.MaxValue, int.MaxValue, -51L, -1L, 441, 495)]
    public void Actual_pools_preserve_signed_critical_sums_until_the_final_chance(
        int generic, int attack, int wand, int spell, long attackIncrease, long spellIncrease,
        int expectedAttack, int expectedSpell)
    {
        var conn = Character(true);
        Set(conn, AttackBaseCriticalStrike.WeaponOverrideStat, 900);
        Set(conn, "critical_strike_chance_+%", generic);
        Set(conn, "attack_critical_strike_chance_+%", attack);
        Set(conn, "wand_critical_strike_chance_+%", wand);
        Set(conn, "spell_critical_strike_chance_+%", spell);
        conn.Pools = Pools(conn);
        Assert.Equal(attackIncrease, conn.Pools.CriticalStrikeChancePct);
        Assert.Equal(spellIncrease, conn.Pools.SpellCriticalStrikeChancePct);
        Assert.Equal(expectedAttack, conn.Pools.AttackCriticalStrikeChance);
        Assert.Equal(expectedSpell, conn.Pools.SpellCriticalStrikeChance);
        var shown = Program.CharacterPlayerStats(conn);
        Assert.Equal(expectedAttack, shown[Program.Stats.Key("main_hand_critical_strike_chance")!.Value]);
        Assert.Equal(expectedSpell, SpellCriticalStrike.Chance(500, Program.Stats, shown,
            new Dictionary<uint, int>(), conn.Pools.SpellCriticalStrikeChancePct));
    }

    [Fact]
    public void Sea_Legs_extreme_opposing_inputs_cancel_before_the_native_raw_stat_is_saturated()
    {
        var conn = Character(true);
        Set(conn, AttackBaseCriticalStrike.WeaponOverrideStat, 900);
        Set(conn, ResistanceOvercap.LightningCritFlag, 1);
        Set(conn, "base_lightning_damage_resistance_%", 120);
        Set(conn, "critical_strike_chance_+%", int.MaxValue);
        Set(conn, "wand_critical_strike_chance_+%", int.MinValue);
        Set(conn, "spell_critical_strike_chance_+%", int.MinValue);
        conn.Pools = Pools(conn);
        Assert.Equal(44, conn.Pools.CriticalStrikeChancePct);
        Assert.Equal(44, conn.Pools.SpellCriticalStrikeChancePct);
        Assert.Equal(1296, conn.Pools.AttackCriticalStrikeChance);
        Assert.Equal(720, conn.Pools.SpellCriticalStrikeChance);
        var shown = Program.CharacterPlayerStats(conn);
        Assert.Equal(int.MaxValue, shown[Program.Stats.Key("critical_strike_chance_+%")!.Value]);
        Assert.Equal(720, SpellCriticalStrike.Chance(500, Program.Stats, shown,
            new Dictionary<uint, int>(), conn.Pools.SpellCriticalStrikeChancePct));
        var skill = new Dictionary<uint, int> { [Program.Stats.Key("critical_strike_chance_+%")!.Value] = 50 };
        Assert.Equal(970, SpellCriticalStrike.Chance(500, Program.Stats, shown, skill,
            conn.Pools.SpellCriticalStrikeChancePct));
    }

    [Fact]
    public void A_weapon_in_the_inactive_set_does_not_make_the_active_empty_hand_armed()
    {
        var conn = Character(false);
        var items = PlayerInventories.For(_name, Program.Items);
        items.Add(PlayerInventories.Weapon2, 0, 0, ItemCatalog.Create(Program.Items.Row(384)!));
        Set(conn, AttackBaseCriticalStrike.WeaponOverrideStat, 900);
        Set(conn, "wand_critical_strike_chance_+%", 100);
        Assert.Equal(0, Pools(conn).AttackCriticalStrikeChance);
        Set(conn, AttackBaseCriticalStrike.UnarmedBaseStat, 1000);
        var unarmed = Pools(conn);
        Assert.Equal(1000, unarmed.AttackCriticalStrikeChance);
        Assert.Equal(0, unarmed.CriticalStrikeChancePct);
        items.SwapWeaponSets();
        var armed = Pools(conn);
        Assert.Equal(1800, armed.AttackCriticalStrikeChance);
        Assert.Equal(100, armed.CriticalStrikeChancePct);
    }

    [Fact]
    public void Sea_Legs_increase_is_applied_once_with_the_active_weapon_base_and_spell_sources()
    {
        var conn = Character(true);
        Set(conn, ResistanceOvercap.LightningCritFlag, 1);
        Set(conn, "base_lightning_damage_resistance_%", 120);
        conn.Pools = Pools(conn);
        Assert.Equal(45, conn.Pools.LightningOvercapCritPct);
        Assert.Equal(1204, conn.Pools.AttackCriticalStrikeChance); // 830 * 1.45, rounded once.
        var shown = (Dictionary<uint, int>)Method("CharacterPlayerStats").Invoke(null, [conn])!;
        Assert.Equal(45, shown[Program.Stats.Key("critical_strike_chance_+%")!.Value]);
        Set(conn, AttackBaseCriticalStrike.WeaponOverrideStat, 900);
        Assert.Equal(1305, Pools(conn).AttackCriticalStrikeChance);
        PlayerInventories.For(_name, Program.Items).SwapWeaponSets();
        Assert.Equal(0, Pools(conn).AttackCriticalStrikeChance);
        Set(conn, AttackBaseCriticalStrike.UnarmedBaseStat, 1000);
        Assert.Equal(1450, Pools(conn).AttackCriticalStrikeChance);
    }

    public void Dispose()
    {
        PlayerInventories.Forget(_name);
    }
}
