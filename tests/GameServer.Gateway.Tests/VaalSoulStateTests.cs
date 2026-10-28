using GameServer.Core.Protocol.Instance;
using System.Text.Json;

namespace GameServer.Gateway.Tests;

public class VaalSoulStateTests
{
    [Fact]
    public void Sorted_distinct_nonfull_rows_share_one_soul_per_kill_and_stop_at_capacity()
    {
        var state = new VaalSoulState();
        state.Reconcile([(9, 2), (3, 1), (9, 2), (20, 0)]);
        Assert.True(state.Gain()); Assert.Equal(new VaalSoulBudget(3, 1, 1), state.Read()[0]);
        Assert.True(state.Gain()); Assert.True(state.Gain()); Assert.False(state.Gain());
        Assert.Equal(new VaalSoulBudget[] { new(3, 1, 1), new(9, 2, 2), new(20, 0, 0) }, state.Read());
    }

    [Fact]
    public void Maximum_source_capacity_shrinks_on_removal_and_never_mints_on_changes_or_readding()
    {
        var state = new VaalSoulState(); state.Reconcile([(87, 3), (87, 8)]);
        for (int i = 0; i < 7; i++) Assert.True(state.Gain());
        state.Reconcile([(87, 3)]); Assert.Equal(new VaalSoulBudget(87, 3, 3), Assert.Single(state.Read()));
        state.Reconcile([(87, 8), (87, 3)]); Assert.Equal(3u, Assert.Single(state.Read()).Current);
        state.Reconcile([(87, 8)]); Assert.Equal(3u, Assert.Single(state.Read()).Current);
        state.Reconcile([]); Assert.Empty(state.Read());
        state.Reconcile([(87, 8)]); Assert.Equal(0u, Assert.Single(state.Read()).Current);
    }

    [Fact]
    public void Atomic_consumption_is_all_or_nothing_and_zero_cost_does_not_require_positive_capacity()
    {
        var state = new VaalSoulState(); state.Reconcile([(87, 3), (88, 0)]);
        state.Gain(); state.Gain();
        Assert.False(state.TryConsume(87, 3)); Assert.Equal(2u, state.Read()[0].Current);
        Assert.True(state.TryConsume(87, 2)); Assert.False(state.TryConsume(87, 1));
        Assert.True(state.TryConsume(88, 0)); Assert.False(state.TryConsume(999, 0));
    }

    [Fact]
    public void Carry_retires_old_balance_preserves_round_robin_and_death_resets_it()
    {
        var old = new VaalSoulState(); old.Reconcile([(1, 5), (2, 5)]); old.Gain();
        var carried = old.Carry(); Assert.Empty(old.Read()); Assert.False(old.Gain());
        var next = new VaalSoulState(); next.Restore(carried); next.Gain();
        Assert.Equal(1u, next.Read()[0].Current); Assert.Equal(1u, next.Read()[1].Current);
        next.ClearBalance(); next.Gain(); Assert.Equal(1u, next.Read()[0].Current); Assert.Equal(0u, next.Read()[1].Current);
    }

    [Theory]
    [InlineData(30, 0, 0, 30)]
    [InlineData(30, 25, 25, 46)]
    [InlineData(30, -25, -25, 18)]
    [InlineData(30, -101, 0, 0)]
    [InlineData(30, 0, -101, 0)]
    [InlineData(30, -200, -200, 30)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue)]
    [InlineData(int.MaxValue, int.MaxValue, int.MinValue, 0)]
    public void Native_cost_has_two_truncations_toward_zero_and_explicit_saturation(int basis, int multiplier,
        int final, int expected)
    {
        int Get(string name) => name == VaalSoulCost.NonAuraMultiplier ? multiplier
            : name == VaalSoulCost.FinalPercent ? final : 0;
        Assert.Equal(expected, VaalSoulCost.Required(basis, Get));
    }

    [Fact]
    public void Aura_selector_and_signed_nonzero_no_souls_flag_use_proven_keys()
    {
        int Get(string name) => name == VaalSoulCost.Aura ? 1 : name == VaalSoulCost.AuraMultiplier ? 100 : 0;
        Assert.Equal(60, VaalSoulCost.Required(30, Get));
        Assert.Equal(0, VaalSoulCost.Required(30, n => n == VaalSoulCost.NoSouls ? -1 : Get(n)));
        Assert.Equal((uint)int.MaxValue, VaalSoulCost.Capacity(int.MaxValue, int.MaxValue));
        Assert.Equal(0u, VaalSoulCost.Capacity(0, 3));
    }

    [Fact]
    public void Real_Vaal_Fireball_catalog_uses_ActiveSkills_87_GE3366_GEPL532_and_raw_stored_uses()
    {
        var catalog = new SkillCatalog();
        int effect = catalog.EffectRow("VaalFireball"); Assert.Equal(3366, effect);
        Assert.Equal(532u, catalog.FindByRow(0x103, effect, 1)!.EffectsPerLevelRow);
        Assert.Equal(new SkillCatalog.VaalDefinition(87, 30, 1), catalog.VaalBudget(effect, 1));
        Assert.Null(catalog.VaalBudget(catalog.EffectRow("Fireball"), 1));
        Assert.Null(catalog.VaalBudget(-1, 1)); Assert.Null(catalog.VaalBudget(int.MaxValue, 1));
        Assert.Null(new SkillCatalog("data/no-such-vaal-catalog").VaalBudget(3366, 1));
        foreach (var (name, key) in new[] { (VaalSoulCost.NonAuraMultiplier, 19748u),
            (VaalSoulCost.AuraMultiplier, 19749u), (VaalSoulCost.FinalPercent, 14089u),
            (VaalSoulCost.NoSouls, 14270u), (VaalSoulCost.Aura, 15801u), ("has_soul_gain_prevention", 8855u) })
            Assert.Equal(key, new StatCatalog().Key(name));
    }

    [Theory]
    [InlineData(-1, 30, 1, true)]
    [InlineData(1, 30, 1, true)]
    [InlineData(0, -1, 1, true)]
    [InlineData(0, 30, -1, true)]
    [InlineData(0, 30, 1, false)]
    public void Invalid_or_missing_public_catalog_references_never_fabricate_a_soul_budget(int active, int cost,
        int uses, bool activeTable)
    {
        string folder = Path.Combine(Path.GetTempPath(), "localexile-vaal-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "tables"));
        void Table(string name, object rows) => File.WriteAllText(Path.Combine(folder, "tables", name + ".json"),
            JsonSerializer.Serialize(rows));
        Table("GrantedEffects", new[] { new { Id = "test", ActiveSkill = active, StatSet1 = -1 } });
        Table("GrantedEffectsPerLevel", new[] { new { GrantedEffect = 0, Level = 1, VaalSouls = cost, VaalStoredUses = uses } });
        Table("GrantedEffectStatSetsPerLevel", Array.Empty<object>());
        if (activeTable) Table("ActiveSkills", new[] { new { Id = "test" } });
        Assert.Null(new SkillCatalog(folder).VaalBudget(0, 1));
    }
}
