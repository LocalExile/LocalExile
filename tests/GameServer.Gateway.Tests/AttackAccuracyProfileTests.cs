using GameServer.Core.World;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class AttackAccuracyProfileTests
{
    [Theory]
    [InlineData(false, false, 37)]
    [InlineData(true, false, 51)]
    [InlineData(false, true, 55)]
    [InlineData(true, true, 77)]
    public void Target_more_modifiers_multiply_before_the_existing_hit_chance_formula(bool unique, bool close, int rating)
    {
        var accuracy = new AttackAccuracyProfile(37, 40, 50);
        Assert.Equal(DamageModel.AccuracyHitChance(100, rating), accuracy.Chance(100, unique, close));
    }

    [Fact]
    public void Contact_uses_target_evasion_modifiers_and_current_blind()
    {
        var conn = new ClientState(new(0, "AccuracyContact", 256, 256, 100, 100, null, []), 0, 1);
        var target = new SpawnedMonster("evasive", new(100, 100, 10), 0, 0, 100)
        { Mods = new([], [], new(new Dictionary<string, int> { ["evasion_rating_+%"] = 200 })) };
        var accuracy = new AttackAccuracyProfile(20);
        Assert.Equal(DamageModel.AccuracyHitChance(201, 20), Program.AttackHitChanceAt(conn, 42, target, 0, accuracy));
        conn.FlaskGrounds.SetBlinded(42, true, () => 1);
        Assert.Equal(DamageModel.AccuracyHitChance(160, 20), Program.AttackHitChanceAt(conn, 42, target, 0, accuracy));
        conn.FlaskGrounds.SetBlinded(42, false, () => 2);
        Assert.Equal(DamageModel.AccuracyHitChance(201, 20), Program.AttackHitChanceAt(conn, 42, target, 0, accuracy));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bow_release_freezes_raw_accuracy_conditions_and_next_release_observes_removal(bool local)
    {
        var sources = new Dictionary<uint, int>
        {
            [Program.Stats.Key("accuracy_rating_+%_final_vs_unique_enemies_from_mastery")!.Value] = 40,
            [Program.Stats.Key("accuracy_rating_+%_final_vs_enemies_in_close_range_from_mastery")!.Value] = 50,
            [Program.Stats.Key("global_always_hit")!.Value] = local ? 0 : -1,
        };
        var weaponSources = new Dictionary<string, int> { ["local_always_hit"] = local ? -1 : 0 };
        var weapon = new SkillCatalog.Unarmed(1000, 10, 10, 120) { LocalSources = weaponSources };
        var before = OrdinaryRangedShot.Capture(weapon, sources, [], Program.Stats, 12, 0, 150);
        sources.Clear(); weaponSources.Clear();
        var after = OrdinaryRangedShot.Capture(weapon, sources, [], Program.Stats, 25, 0, 150);
        Assert.Equal(new AttackAccuracyProfile(12, 40, 50, true), before.AccuracyProfile);
        Assert.Equal(new AttackAccuracyProfile(25), after.AccuracyProfile);
        Assert.Equal(100, before.AccuracyProfile!.Value.Chance(100000, true, true));
        Assert.Equal(5, after.AccuracyProfile!.Value.Chance(100000, true, true));
    }

    [Fact]
    public void Final_aliases_and_unscoped_local_actor_keys_cannot_grant_always_hit()
    {
        var sources = new Dictionary<uint, int>();
        foreach (string name in new[] { "main_hand_always_hit", "virtual_global_attacks_always_hit", "local_always_hit" })
            sources[Program.Stats.Key(name)!.Value] = 1;
        var pools = new CharacterStats.Pools(1, 100, 100, 0, 0);
        Assert.False(AttackAccuracyProfile.From(pools, sources, Program.Stats).CannotBeEvaded);
        var own = new Dictionary<uint, int> { [Program.Stats.Key("global_always_hit")!.Value] = -1 };
        Assert.True(AttackAccuracyProfile.From(pools, sources, Program.Stats, own).CannotBeEvaded);
    }
}
