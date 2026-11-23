using GameServer.Modules.Survivors;
namespace GameServer.Gateway.Tests;

// The survivors run's clock scale goes on top of a monster's rolled rarity mods (extraMods in SpawnMonster), so a magic
// or rare member of the horde gets the same +75% life / +90% damage as a normal one. The boss solves its own
// maximum_life_+% so the rolled unique's rarity factor lands it on the reference ladder.
public sealed class MonsterModEffectsTests
{
    static MonsterModEffects Effects(params (string Name, int Value)[] stats) =>
        new(stats.ToDictionary(s => Stat.Of(s.Name), s => s.Value));

    public void Extra_effects_add_to_a_rolled_monster_s_own_mods()
    {
        var rolled = Effects(("monster_life_+%_final_from_rarity", 100), ("damage_+%", 20));
        var clock = Effects(("maximum_life_+%", 75), ("damage_+%", 90));

        var merged = rolled.Plus(clock);

        Assert.Equal(75, merged[Stat.MaximumLifePlusPct]);
        Assert.Equal(110, merged[Stat.DamagePlusPct]);
        Assert.Equal(100, merged[Stat.MonsterLifePlusPctFinalFromRarity]);
        // The roll is not mutated: the same shared magic-monster set stays valid for the next spawn.
        Assert.Equal(0, rolled[Stat.MaximumLifePlusPct]);
        Assert.Equal(20, rolled[Stat.DamagePlusPct]);
    }

    public void Adding_no_effects_keeps_the_same_set()
    {
        var rolled = Effects(("damage_+%", 20));
        Assert.Same(rolled, rolled.Plus(MonsterModEffects.None));
    }

    public void A_single_stat_can_be_replaced_by_its_solved_value()
    {
        var rolled = Effects(("maximum_life_+%", 10), ("damage_+%", 20));
        var solved = rolled.Plus(Stat.MaximumLifePlusPct, 40);
        Assert.Equal(50, solved[Stat.MaximumLifePlusPct]);
        Assert.Equal(20, solved[Stat.DamagePlusPct]);
    }
}
