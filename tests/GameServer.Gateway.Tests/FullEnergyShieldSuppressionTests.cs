using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class FullEnergyShieldSuppressionTests : IDisposable
{
    const string BaseChance = "base_spell_suppression_chance_%";
    const string FullESChance = "spell_suppression_chance_%_while_on_full_energy_shield";
    const string FlatES = "base_maximum_energy_shield";
    const string Conversion = "gain_maximum_life_instead_of_maximum_es_from_armour";
    readonly string _name = "FullEsSuppression" + Guid.NewGuid().ToString("N");

    public FullEnergyShieldSuppressionTests() => TestSaves.Ensure();

    PlayerInventories Inventory => PlayerInventories.For(_name, Program.Items);
    static uint Key(string stat) => Program.Stats.Key(stat)!.Value;

    static ItemMod Mod(string stat, int value)
    {
        var row = Program.Items.Mods.All.First(m => m.Stats.Any(s => s.Id == stat));
        return new(ModSection.Explicit, row.Hash, row.Stats.Select(s => s.Id == stat ? value : 0).ToArray());
    }

    static Item Ring(params (string Stat, int Value)[] mods) => ItemCatalog.Create(Program.Items.Find("Iron Ring")!) with
        { Mods = mods.Select(m => Mod(m.Stat, m.Value)).ToArray() };

    static Item Body100ES(bool prohibited = false)
    {
        var type = Program.Items.Find("Simple Robe")!;
        int basis = Program.Items.Defences(type)!.Value.AtRoll(0).EnergyShield;
        var mods = new List<ItemMod> { Mod("local_energy_shield", 100 - basis), Mod("local_no_attribute_requirements", 1) };
        if (prohibited) mods.Add(Mod("local_no_energy_shield", 1));
        return ItemCatalog.Create(type, defenceBaseRoll: 0) with { Quality = 0, Mods = mods };
    }

    ClientState Character(int baseChance = 90, bool body = true, bool revenge = false)
    {
        var conn = new ClientState(new(0, "FullEsSuppression", 128, 128, 32, 32, null, []), 0, 1)
            { Character = _name, ClassRow = SkillCatalog.WitchRow };
        conn.Passives.Add(48698); // Real Void Barrier:12% ES,18% EV,+10 suppression while full ES.
        if (revenge) conn.Passives.Add(53114); // Real Revenge of the Hunted includes +10 while full Life.
        Inventory.Add(6, 0, 0, Ring((BaseChance, baseChance)));
        if (body) Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Body100ES());
        Refresh(conn);
        Heal(conn);
        return conn;
    }

    static void Refresh(ClientState conn) => conn.Pools = Program.PoolsFor(conn, 100);
    static void Heal(ClientState conn)
    {
        conn.PlayerLife = conn.PlayerMaxLife;
        conn.EnergyShield.SetMaximum(conn.Pools.EnergyShield, refill: true);
    }

    static (int Chance, int Effect, bool Lucky, bool Unlucky) Runtime(ClientState conn) =>
        ((int, int, bool, bool))typeof(Program).GetMethod("Suppression",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [conn])!;

    static Dictionary<uint, int> Published(ClientState conn) =>
        ((List<(uint, int)>)typeof(Program).GetMethod("PlayerStatsWithCleared",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [conn])!)
        .ToDictionary(s => s.Item1, s => s.Item2);

    static void AssertPublishedChance(ClientState conn, int expected)
    {
        var characterStats = Program.CharacterPlayerStats(conn);
        var sent = Published(conn);
        Assert.Equal(expected, Runtime(conn).Chance);
        Assert.NotEmpty(Program.SuppressionChanceKeys);
        foreach (uint key in Program.SuppressionChanceKeys)
        {
            Assert.Equal(expected, characterStats[key]);
            Assert.Equal(expected, sent[key]);
        }
    }

    [Fact]
    public void Real_Void_Barrier_maps_its_native_source_into_runtime_and_every_published_chance()
    {
        var conn = Character();
        Assert.Equal(15406u, Key(FullESChance));
        Assert.Equal(10, Program.Passives.Stats([48698])[Key(FullESChance)]);
        Assert.Equal(10, conn.Pools.SpellSuppressionWhileFullEnergyShield);
        Assert.Equal(90, conn.Pools.SpellSuppressionChanceUncapped);
        Assert.Equal(115u, conn.Pools.EnergyShield); //100*(1+.03 Intelligence+.12 Void Barrier).
        Assert.True(conn.EnergyShield.Full);
        AssertPublishedChance(conn, 100);

        Assert.Equal(0, conn.EnergyShield.Damage(1, bypass: false, now: 0));
        Assert.False(conn.EnergyShield.Full);
        AssertPublishedChance(conn, 90);
        Heal(conn);
        AssertPublishedChance(conn, 100);
    }

    [Fact]
    public void A_zero_maximum_does_not_activate_the_full_ES_bonus_even_with_full_Life()
    {
        var conn = Character(body: false);
        Assert.Equal(0u, conn.EnergyShield.Maximum);
        Assert.Equal(0u, conn.EnergyShield.Current);
        Assert.Equal(conn.PlayerMaxLife, conn.PlayerLife);
        Assert.False(conn.EnergyShield.Full);
        AssertPublishedChance(conn, 90);
    }

    [Fact]
    public void A_dead_character_cannot_activate_full_ES_even_before_the_resource_tick_empties_it()
    {
        var conn = Character();
        conn.PlayerLife = 0;
        Assert.True(conn.EnergyShield.Full); //The next dead-resource tick has deliberately not run.
        AssertPublishedChance(conn, 90);
    }

    [Theory]
    [InlineData(true, true, 100)]
    [InlineData(true, false, 90)]
    [InlineData(false, true, 90)]
    [InlineData(false, false, 80)]
    public void Real_full_ES_and_full_Life_passives_compose_independently(bool fullES, bool fullLife, int expected)
    {
        var conn = Character(baseChance: 80, revenge: true);
        if (!fullES) conn.EnergyShield.Damage(1, bypass: false, now: 0);
        if (!fullLife) conn.PlayerLife--;
        Assert.Equal(10, conn.Pools.SpellSuppressionWhileFullLife);
        Assert.Equal(10, conn.Pools.SpellSuppressionWhileFullEnergyShield);
        AssertPublishedChance(conn, expected);
    }

    [Fact]
    public void The_uncapped_base_is_retained_until_active_conditional_penalties_have_been_added()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100,
            new CharacterStats.Bonus(SpellSuppressionChance: 120, SpellSuppressionWhileFullEnergyShield: -30));
        Assert.Equal(100, pools.SpellSuppressionChance);
        Assert.Equal(120, pools.SpellSuppressionChanceUncapped);
        Assert.Equal(100, CharacterStats.SuppressionChance(pools, false, false, false));
        Assert.Equal(90, CharacterStats.SuppressionChance(pools, false, false, false, fullEnergyShield: true));
    }

    [Theory]
    [InlineData(-20, 30, 10)]
    [InlineData(-20, 10, 0)]
    [InlineData(int.MinValue, int.MaxValue, 0)]
    public void Negative_base_sources_are_preserved_before_the_final_lower_bound(int basis, int conditional, int expected)
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100,
            new CharacterStats.Bonus(SpellSuppressionChance: basis, SpellSuppressionWhileFullEnergyShield: conditional));
        Assert.Equal(0, pools.SpellSuppressionChance);
        Assert.Equal(basis, pools.SpellSuppressionChanceUncapped);
        Assert.Equal(expected, CharacterStats.SuppressionChance(pools, false, false, false, fullEnergyShield: true));
    }

    [Theory]
    [InlineData(int.MaxValue, 100)]
    [InlineData(int.MinValue, 0)]
    public void Wide_sums_bound_all_active_condition_sources_without_integer_wraparound(int value, int expected)
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 100, new CharacterStats.Bonus(
            SpellSuppressionChance: value, SpellSuppressionWhileFullEnergyShield: value,
            SpellSuppressionWhileFullLife: value, SpellSuppressionWhileMoving: value, SpellSuppressionIfHitRecently: value));
        Assert.Equal(expected, CharacterStats.SuppressionChance(pools, true, true, true, fullEnergyShield: true));
    }

    [Fact]
    public void Hand_constructed_legacy_pools_fall_back_to_their_existing_base_chance()
    {
        var pools = new CharacterStats.Pools(100, 100, 100, 14, 0,
            SpellSuppressionChance: 70, SpellSuppressionWhileFullEnergyShield: 10);
        Assert.Null(pools.SpellSuppressionChanceUncapped);
        Assert.Equal(70, CharacterStats.SuppressionChance(pools, false, false, false));
        Assert.Equal(80, CharacterStats.SuppressionChance(pools, false, false, false, fullEnergyShield: true));
    }

    [Fact]
    public void Fractional_recovery_below_the_whole_resource_cap_does_not_count_as_full()
    {
        var es = new EnergyShield();
        Assert.False(es.Full);
        es.SetMaximum(100, refill: true);
        Assert.True(es.Full);
        es.Damage(1, bypass: false, now: 0);
        int Get(string name) => name switch
        {
            "base_energy_shield_regeneration_rate_per_minute" => 60,
            "cannot_recharge_energy_shield" => 1,
            _ => 0,
        };

        es.Advance(0, .999, Get, alive: true);
        Assert.Equal(99u, es.Current); //99 whole plus .999 carried recovery remains below100.
        Assert.False(es.Full);
        es.Advance(.999, 1.001, Get, alive: true);
        Assert.Equal(100u, es.Current);
        Assert.True(es.Full);
        es.SetMaximum(0);
        Assert.False(es.Full);
    }

    [Fact]
    public void Damage_delay_and_completed_recharge_change_the_same_runtime_and_panel_condition()
    {
        var conn = Character();
        conn.EnergyShield.Damage(1, bypass: false, now: 0);
        AssertPublishedChance(conn, 90);
        conn.EnergyShield.Advance(0, 1.99, _ => 0, alive: true);
        Assert.False(conn.EnergyShield.Recharging);
        AssertPublishedChance(conn, 90);
        conn.EnergyShield.Advance(1.99, 2.01, _ => 0, alive: true);
        Assert.Equal(114u, conn.EnergyShield.Current); //Recharge has started but supplied less than 1 ES.
        Assert.True(conn.EnergyShield.Recharging);
        AssertPublishedChance(conn, 90);
        conn.EnergyShield.Advance(2.01, 2.1, _ => 0, alive: true);
        Assert.Equal(115u, conn.EnergyShield.Current);
        Assert.True(conn.EnergyShield.Full);
        AssertPublishedChance(conn, 100);
    }

    [Fact]
    public void Raising_maximum_ES_does_not_refill_or_preserve_fullness_and_reducing_it_clamps_current()
    {
        var conn = Character();
        var independent = Inventory.Add(7, 0, 0, Ring((FlatES, 100)));
        Refresh(conn);
        Assert.Equal(230u, conn.EnergyShield.Maximum); //200*(1+.03 Intelligence+.12 Void Barrier).
        Assert.Equal(115u, conn.EnergyShield.Current);
        Assert.False(conn.EnergyShield.Full);
        AssertPublishedChance(conn, 90);

        Heal(conn);
        AssertPublishedChance(conn, 100);
        Assert.NotNull(Inventory.Remove(7, independent.Id));
        Refresh(conn);
        Assert.Equal(115u, conn.EnergyShield.Maximum);
        Assert.Equal(115u, conn.EnergyShield.Current);
        AssertPublishedChance(conn, 100);
    }

    [Fact]
    public void Removing_the_last_ES_body_clears_the_condition_and_re_equipping_does_not_instantly_refill()
    {
        var conn = Character();
        var body = Assert.Single(Inventory.Items(PlayerInventories.BodyArmour));
        AssertPublishedChance(conn, 100);
        Assert.NotNull(Inventory.Remove(PlayerInventories.BodyArmour, body.Id));
        Refresh(conn);
        Assert.Equal(0u, conn.EnergyShield.Maximum);
        Assert.Equal(0u, conn.EnergyShield.Current);
        AssertPublishedChance(conn, 90);

        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, body.Item);
        Refresh(conn);
        Assert.Equal(115u, conn.EnergyShield.Maximum);
        Assert.Equal(0u, conn.EnergyShield.Current);
        AssertPublishedChance(conn, 90);
        Heal(conn);
        AssertPublishedChance(conn, 100);
    }

    [Fact]
    public void A_local_no_ES_override_cannot_supply_phantom_fullness_but_independent_ES_can()
    {
        var conn = Character(body: false);
        Inventory.Add(PlayerInventories.BodyArmour, 0, 0, Body100ES(prohibited: true));
        Refresh(conn);
        Heal(conn);
        Assert.Equal(0u, conn.EnergyShield.Maximum);
        AssertPublishedChance(conn, 90);

        Inventory.Add(7, 0, 0, Ring((FlatES, 100)));
        Refresh(conn);
        Heal(conn);
        Assert.Equal(115u, conn.EnergyShield.Maximum);
        AssertPublishedChance(conn, 100);
    }

    [Theory]
    [InlineData(true, 100)]
    [InlineData(false, 50)]
    public void Armour_ES_conversion_uses_the_remaining_effective_ES_pool(bool fullConversion, uint addedLife)
    {
        var conn = Character();
        uint originalLife = conn.Pools.Life;
        string conversion = fullConversion ? Conversion : "gain_maximum_life_instead_of_maximum_es_from_armour_at_x%_of_the_value";
        Inventory.Add(7, 0, 0, Ring((conversion, fullConversion ? 1 : 50)));
        Refresh(conn);
        Heal(conn);
        Assert.Equal(originalLife + addedLife, conn.Pools.Life);
        Assert.Equal(0u, conn.EnergyShield.Maximum); //Both conversion forms consume all local item ES.
        AssertPublishedChance(conn, 90);

        var conversionRing = Assert.Single(Inventory.Items(7));
        Assert.NotNull(Inventory.Change(7, conversionRing.Id, i => i with { Mods = [.. i.Mods, Mod(FlatES, 100)] }));
        Refresh(conn);
        Heal(conn);
        Assert.Equal(115u, conn.EnergyShield.Maximum);
        AssertPublishedChance(conn, 100);
    }

    [Fact]
    public void Independent_Discipline_ES_can_enable_fullness_and_aura_removal_clears_it()
    {
        var conn = Character(body: false);
        var aura = new AuraCatalog.Aura(0x0103, Program.Skills.EffectRow("Discipline"), "Discipline", "Discipline",
            1, 148, 0, 3500, 1, 0, SourceBuff: 2);
        conn.Auras[aura.SkillId] = aura;
        Refresh(conn);
        Assert.Equal(69u, conn.EnergyShield.Maximum); //60 independent ES*(1+.03+.12)=69.
        Assert.Equal(0u, conn.EnergyShield.Current);
        AssertPublishedChance(conn, 90);
        Heal(conn);
        AssertPublishedChance(conn, 100);

        conn.Auras.Clear();
        Refresh(conn);
        Assert.Equal(0u, conn.EnergyShield.Maximum);
        AssertPublishedChance(conn, 90);
    }

    [Fact]
    public void Removing_the_real_source_clears_every_previously_published_conditional_and_final_key()
    {
        var conn = Character(baseChance: 0);
        var before = Published(conn);
        Assert.Equal(10, before[Key(FullESChance)]);
        AssertPublishedChance(conn, 10);
        Assert.Contains(Key(FullESChance), conn.SentStatKeys);
        conn.Passives.Clear();
        Refresh(conn);
        var after = Published(conn);

        Assert.Equal(0, after[Key(FullESChance)]);
        Assert.DoesNotContain(Key(FullESChance), conn.SentStatKeys);
        AssertPublishedChance(conn, 0);
    }

    [Theory]
    [InlineData(true, false, 75d, 64d)]
    [InlineData(false, true, 25d, 16d)]
    public void Lucky_and_unlucky_consumers_use_the_same_condition_adjusted_chance_as_the_panel(
        bool lucky, bool unlucky, double fullEffective, double damagedEffective)
    {
        var conn = Character(baseChance: 40);
        conn.Pools = conn.Pools with { SpellSuppressionLucky = lucky, SpellSuppressionUnlucky = unlucky };
        var before = Runtime(conn);
        Assert.Equal(lucky, before.Lucky);
        Assert.Equal(unlucky, before.Unlucky);
        AssertPublishedChance(conn, 50);
        Assert.Equal(fullEffective, DamageModel.EffectiveSuppressionChance(before.Chance, before.Lucky, before.Unlucky), 8);

        conn.EnergyShield.Damage(1, bypass: false, now: 0);
        var after = Runtime(conn);
        AssertPublishedChance(conn, 40);
        Assert.Equal(damagedEffective, DamageModel.EffectiveSuppressionChance(after.Chance, after.Lucky, after.Unlucky), 8);
    }

    [Fact]
    public void A_guaranteed_suppressed_spell_hit_consumes_ES_then_removes_the_full_ES_bonus_for_the_next_hit()
    {
        var conn = Character();
        var before = Runtime(conn);
        Assert.True(DamageModel.Suppresses(before.Chance, 99, before.Lucky, before.Unlucky));
        int dealt = DamageModel.Suppress(100, before.Effect);
        Assert.Equal(60, dealt);
        Assert.Equal(0, conn.EnergyShield.Damage(dealt, bypass: false, now: 0));
        Assert.Equal(55u, conn.EnergyShield.Current); //115 ES minus the same suppressed 60 damage.
        Assert.Equal(conn.PlayerMaxLife, conn.PlayerLife);
        AssertPublishedChance(conn, 90);
    }

    public void Dispose()
    {
        PlayerInventories.Forget(_name);
        Program.InactiveItems.TryRemove(_name, out _);
        Program.GemSkillIdsOf.TryRemove(_name, out _);
    }
}
