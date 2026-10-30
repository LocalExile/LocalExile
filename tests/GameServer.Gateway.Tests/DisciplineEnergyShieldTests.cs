using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class DisciplineEnergyShieldTests : IDisposable
{
    const string FlatES = "base_maximum_energy_shield";
    const string RechargeIncrease = "energy_shield_recharge_rate_+%";
    const string RechargeDisplay = "energy_shield_recharge_rate_per_minute";
    readonly string _name = "DisciplineES" + Guid.NewGuid().ToString("N");

    public DisciplineEnergyShieldTests() => TestSaves.Ensure();

    ClientState Character() => new(new(0, "DisciplineES", 128, 128, 32, 32, null, []), 0, 1)
        { Character = _name, ClassRow = SkillCatalog.WitchRow };

    PlayerInventories Inventory => PlayerInventories.For(_name, Program.Items);
    static uint Key(string stat) => Program.Stats.Key(stat)!.Value;

    static AuraCatalog.Aura Discipline(int level = 1, int quality = 0) =>
        new(0x0103, Program.Skills.EffectRow("Discipline"), "Discipline", "Discipline", 1, 148,
            0, 3500, level, quality, SourceBuff: 2);

    static ItemMod Mod(string stat, int value)
    {
        var row = Program.Items.Mods.All.First(m => m.Stats.Any(s => s.Id == stat));
        return new(ModSection.Explicit, row.Hash, row.Stats.Select(s => s.Id == stat ? value : 0).ToArray());
    }

    static Item Ring(params (string Stat, int Value)[] mods) => ItemCatalog.Create(Program.Items.Find("Iron Ring")!) with
        { Mods = mods.Select(m => Mod(m.Stat, m.Value)).ToArray() };

    static Item Shield100ES() => ItemCatalog.Create(Program.Items.Find("Twig Spirit Shield")!, defenceBaseRoll: 0) with
        { Mods = [Mod("local_energy_shield", 94)] }; // Native minimum base 6 + local flat 94.

    static void Activate(ClientState conn, AuraCatalog.Aura? aura = null)
    {
        aura ??= Discipline();
        conn.Auras[aura.SkillId] = aura;
    }

    static void Refresh(ClientState conn) => conn.Pools = Program.PoolsFor(conn, 100);

    static Dictionary<uint, int> Published(ClientState conn) =>
        ((List<(uint, int)>)typeof(Program).GetMethod("PlayerStatsWithCleared",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [conn])!)
        .ToDictionary(s => s.Item1, s => s.Item2);

    [Theory]
    [InlineData(1, 0, 60, 62)]
    [InlineData(20, 0, 217, 224)]
    [InlineData(20, 20, 217, 224)]
    public void Native_gem_levels_and_quality_supply_ES_once_to_the_pool_and_panel(
        int level, int quality, int flat, uint maximum)
    {
        var conn = Character();
        var aura = Discipline(level, quality);
        Activate(conn, aura);

        var bonus = AuraCatalog.Bonus([aura], 0, Program.Skills, Program.Stats);
        var raw = AuraCatalog.RawStats([aura], 0, Program.Skills, Program.Stats).ToDictionary(s => s.Key, s => s.Value);
        Refresh(conn);

        Assert.Equal(flat, bonus.EnergyShield);
        Assert.Equal(flat, raw[Key(FlatES)]);
        Assert.Equal(30, raw[Key(RechargeIncrease)]);
        Assert.Equal(maximum, conn.Pools.EnergyShield); // Witch Intelligence 32 adds 3%, then the total rounds halfway up.
        Assert.Equal(flat, Program.CharacterPlayerStats(conn)[Key(FlatES)]);
        Assert.Equal((int)maximum, Published(conn)[Key("maximum_energy_shield")]);
        // The native Q20 bonus is 40% skill area, not ES or recharge.
        var own = Program.Skills.EffectStats(aura.Effect, level, quality).ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Equal(quality * 2, own.GetValueOrDefault(Key("base_skill_area_of_effect_+%")));
    }

    [Fact]
    public void Aura_effect_scales_flat_ES_and_recharge_once_without_changing_reservation()
    {
        var conn = Character();
        Inventory.Add(6, 0, 0, Ring(("non_curse_aura_effect_+%", 50)));
        Activate(conn);
        Refresh(conn);

        var stats = Program.CharacterPlayerStats(conn);
        Assert.Equal(50, conn.Pools.AuraEffectPct);
        Assert.Equal(90, stats[Key(FlatES)]); // 60 * 1.5, then ordinary Intelligence scaling acts on the pool.
        Assert.Equal(93u, conn.Pools.EnergyShield); // 90 * 1.03 = 92.7.
        Assert.Equal(45, stats[Key(RechargeIncrease)]);
        Assert.Equal(228, AuraCatalog.RequiredMana(conn.Pools, conn.Auras.Values)); // ceil(650 * 35%).
    }

    [Theory]
    [InlineData(20, 50, 325, 335, 45)]
    [InlineData(1, 5, 63, 65, 31)]
    public void Fractional_aura_stats_truncate_before_the_global_ES_total_is_rounded(
        int level, int effect, int flat, uint maximum, int recharge)
    {
        var conn = Character();
        Inventory.Add(6, 0, 0, Ring(("non_curse_aura_effect_+%", effect)));
        Activate(conn, Discipline(level));
        Refresh(conn);

        var stats = Program.CharacterPlayerStats(conn);
        Assert.Equal(flat, stats[Key(FlatES)]);
        Assert.Equal(recharge, stats[Key(RechargeIncrease)]);
        Assert.Equal(maximum, conn.Pools.EnergyShield);
        // L20: truncate(217 * 1.5) = 325, then round_half_up(325 * 1.03) = 335.
        // L1: truncate(30 recharge * 1.05) = 31, and round_half_up(63 flat * 1.03) = 65.
    }

    [Fact]
    public void Independent_flat_ES_and_aura_ES_share_global_scaling_without_counting_raw_stats_twice()
    {
        var conn = Character();
        Inventory.Add(6, 0, 0, Ring((FlatES, 100), ("maximum_energy_shield_+%", 100)));
        Activate(conn);
        Refresh(conn);

        Assert.Equal(160, Program.CharacterPlayerStats(conn)[Key(FlatES)]);
        Assert.Equal(325u, conn.Pools.EnergyShield); // (100 from equipment + 60 aura) * 2.03 = 324.8.
        Assert.Equal(325, Published(conn)[Key("maximum_energy_shield")]);
    }

    [Fact]
    public void Intelligence_and_global_defences_apply_after_the_aura_grants_flat_ES()
    {
        var conn = Character();
        Inventory.Add(6, 0, 0, Ring(("additional_intelligence", 100), ("global_defences_+%", 50)));
        Activate(conn);
        Refresh(conn);

        Assert.Equal(132, conn.Pools.Intelligence);
        Assert.Equal(63, conn.Pools.EnergyShieldPct);
        Assert.Equal(98u, conn.Pools.EnergyShield); // 60 * (1 + .13 Intelligence + .50 global) = 97.8.
    }

    [Fact]
    public void Shield_only_increases_do_not_scale_Discipline_or_other_flat_ES()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.Offhand1, 0, 0, Shield100ES());
        Inventory.Add(6, 0, 0, Ring(("shield_armour_+%", 100), (FlatES, 30)));
        Activate(conn);
        Refresh(conn);

        // Shield 100 * 2.03 + (30 independent + 60 Discipline) * 1.03 = 295.7.
        Assert.Equal(296u, conn.Pools.EnergyShield);
        Assert.Equal(90, Program.CharacterPlayerStats(conn)[Key(FlatES)]);
    }

    [Fact]
    public void Shield_defence_exclusion_preserves_Discipline_and_independent_ES()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.Offhand1, 0, 0, Shield100ES());
        Inventory.Add(6, 0, 0, Ring(("minions_get_shield_stats_instead_of_you", 1), (FlatES, 30)));
        Activate(conn);
        Refresh(conn);

        Assert.Equal(93u, conn.Pools.EnergyShield); // Only (30 independent + 60 aura) * 1.03 remains.
    }

    [Theory]
    [InlineData(true, 0, 100)]
    [InlineData(false, 50, 50)]
    public void Armour_ES_to_life_never_consumes_Discipline_ES(bool full, int percent, uint convertedLife)
    {
        var conn = Character();
        Refresh(conn);
        uint bareLife = conn.Pools.Life;
        Inventory.Add(PlayerInventories.Offhand1, 0, 0, Shield100ES());
        Inventory.Add(6, 0, 0, Ring(("gain_maximum_life_instead_of_maximum_es_from_armour", full ? 1 : 0),
            ("gain_maximum_life_instead_of_maximum_es_from_armour_at_x%_of_the_value", percent),
            ("shield_armour_+%", 100)));
        Inventory.Add(7, 0, 0, Ring((FlatES, 30), ("maximum_energy_shield_+%", 100)));
        Activate(conn);
        Refresh(conn);

        Assert.Equal(bareLife + convertedLife, conn.Pools.Life);
        Assert.Equal(183u, conn.Pools.EnergyShield); // (30 independent + 60 aura) * 2.03 = 182.7.
        Assert.Equal(183, Published(conn)[Key("maximum_energy_shield")]);
    }

    [Fact]
    public void Removing_Discipline_clears_maximum_current_and_published_ES_without_leaving_a_recharge_bonus()
    {
        var conn = Character();
        Activate(conn);
        Refresh(conn);
        conn.EnergyShield.SetMaximum(conn.Pools.EnergyShield, refill: true);
        var before = Published(conn);
        Assert.Equal(62u, conn.EnergyShield.Current);
        Assert.Equal(62, before[Key("maximum_energy_shield")]);
        Assert.Equal(1612, before[Key(RechargeDisplay)]); // 62 * 20 per minute * 1.3.
        Assert.Equal(30, before[Key(RechargeIncrease)]);

        conn.Auras.Clear();
        Refresh(conn);
        var after = Published(conn);

        Assert.Equal(0u, conn.Pools.EnergyShield);
        Assert.Equal(0u, conn.EnergyShield.Current);
        Assert.Equal(0, after[Key("maximum_energy_shield")]);
        Assert.Equal(0, after[Key(RechargeDisplay)]);
        Assert.Equal(0, after[Key(RechargeIncrease)]);
    }

    [Fact]
    public void Activating_Discipline_changes_capacity_without_refilling_current_ES()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0,
            ItemCatalog.Create(Program.Items.Find("Simple Robe")!, defenceBaseRoll: 0));
        Refresh(conn);
        conn.EnergyShield.SetMaximum(conn.Pools.EnergyShield, refill: true);
        Assert.Equal(14u, conn.EnergyShield.Current); // 14 local ES * 1.03 rounds to 14.

        Activate(conn);
        Refresh(conn);
        Assert.Equal(76u, conn.EnergyShield.Maximum); // (14 item + 60 aura) * 1.03 = 76.22.
        Assert.Equal(14u, conn.EnergyShield.Current);
        conn.EnergyShield.SetMaximum(conn.Pools.EnergyShield, refill: true);
        conn.Auras.Clear();
        Refresh(conn);
        Assert.Equal(14u, conn.EnergyShield.Current); // Removing the aura clamps a previously full larger pool.
    }

    [Fact]
    public void Recharge_uses_the_Discipline_maximum_and_existing_aura_rate_after_the_delay()
    {
        var conn = Character();
        Activate(conn);
        Refresh(conn);
        var stats = Program.CharacterPlayerStats(conn);
        int Get(string name) => stats.GetValueOrDefault(Key(name));
        conn.EnergyShield.SetMaximum(conn.Pools.EnergyShield, refill: true);
        Assert.Equal(0, conn.EnergyShield.Damage(62, bypass: false, now: 0)); // Empty the corrected 62 maximum exactly.

        conn.EnergyShield.Advance(0, 1.99, Get, alive: true);
        Assert.Equal(0u, conn.EnergyShield.Current);
        Assert.False(conn.EnergyShield.Recharging);
        conn.EnergyShield.Advance(1.99, 3, Get, alive: true);

        Assert.Equal(26u, conn.EnergyShield.Current); // 1612 per minute for one second, carrying its fractional remainder.
        Assert.True(conn.EnergyShield.Recharging);
        Assert.Equal(1612, EnergyShield.PredictionPerMinute(conn.Pools.EnergyShield, Get, recharging: true));
        Assert.Equal(0, EnergyShield.RegenPerMinute(conn.Pools.EnergyShield, Get));
    }

    [Fact]
    public void Suppressing_aura_effect_to_zero_does_not_create_negative_ES_or_change_reserved_mana()
    {
        var conn = Character();
        Inventory.Add(6, 0, 0, Ring(("non_curse_aura_effect_+%", -200)));
        Activate(conn);
        Refresh(conn);

        Assert.Equal(0u, conn.Pools.EnergyShield);
        Assert.Equal(0, Program.CharacterPlayerStats(conn)[Key(FlatES)]);
        Assert.Equal(0, Program.CharacterPlayerStats(conn)[Key(RechargeIncrease)]);
        Assert.Equal(228, AuraCatalog.RequiredMana(conn.Pools, conn.Auras.Values));
    }

    [Fact]
    public void Town_suppression_preserves_saved_intent_and_restores_the_ES_pool_outside_town()
    {
        var conn = Character();
        CharacterSaves.SetAuraActive(_name, "Discipline", true);
        var saved = CharacterSaves.Load(_name)!.ActiveAuras!;
        var available = new[] { Discipline() };

        Assert.Empty(AuraCatalog.Restore(saved, available, conn.Pools, inTown: true));
        Refresh(conn);
        Assert.Equal(0u, conn.Pools.EnergyShield);
        Assert.Equal(new[] { "Discipline" }, CharacterSaves.Load(_name)!.ActiveAuras);
        foreach (var aura in AuraCatalog.Restore(saved, available, conn.Pools)) Activate(conn, aura);
        Refresh(conn);
        Assert.Equal(62u, conn.Pools.EnergyShield);
    }

    [Fact]
    public void A_fresh_connection_restores_saved_Discipline_from_its_real_socketed_gem_once()
    {
        var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemDiscipline")!);
        var host = ItemCatalog.Create(Program.Items.Find("Plate Vest")!, defenceBaseRoll: 0) with
            { Sockets = [new(SocketColour.Blue, gem)] };
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, host);
        Inventory.Add(6, 0, 0, Ring(("additional_intelligence", 100)));
        CharacterSaves.SetAuraActive(_name, "Discipline", true);
        var conn = Character();
        Refresh(conn);
        Assert.Equal(0u, conn.Pools.EnergyShield); // Remembered intent has no effect before runtime restoration.

        var restore = typeof(Program).GetMethod("RestoreRememberedAuras", BindingFlags.Static | BindingFlags.NonPublic)!;
        var saved = CharacterSaves.Load(_name)!.ActiveAuras!;
        var restored = (IReadOnlyList<AuraCatalog.Aura>)restore.Invoke(null, [conn, saved])!;

        Assert.Single(restored);
        Assert.Equal("Discipline", restored[0].EffectId);
        Assert.Equal(68u, conn.Pools.EnergyShield); // 60 aura ES * 1.13 from 132 Intelligence = 67.8.
        Assert.Equal(60, Program.CharacterPlayerStats(conn)[Key(FlatES)]);
        Assert.Empty((IReadOnlyList<AuraCatalog.Aura>)restore.Invoke(null, [conn, saved])!);
        Assert.Equal(68u, conn.Pools.EnergyShield);
    }

    [Fact]
    public void Adding_aura_ES_to_a_large_independent_source_saturates_instead_of_wrapping()
    {
        var conn = Character();
        Inventory.Add(6, 0, 0, Ring((FlatES, int.MaxValue)));
        Activate(conn);
        Refresh(conn);

        Assert.Equal((uint)int.MaxValue, conn.Pools.EnergyShield);
        Assert.Equal(int.MaxValue, Program.CharacterPlayerStats(conn)[Key(FlatES)]);
        Assert.Equal(int.MaxValue, Published(conn)[Key("maximum_energy_shield")]);
    }

    public void Dispose()
    {
        PlayerInventories.Forget(_name);
        Program.InactiveItems.TryRemove(_name, out _);
        Program.GemSkillIdsOf.TryRemove(_name, out _);
    }
}
