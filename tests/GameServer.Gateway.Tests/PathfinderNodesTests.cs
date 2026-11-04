using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class PathfinderNodesTests
{
    sealed class FixedRolls(int value) : Random
    {
        public override int Next(int minValue, int maxValue) => Math.Clamp(value, minValue, maxValue - 1);
        public override int Next(int maxValue) => Math.Clamp(value, 0, maxValue - 1);
    }

    // Master Toxicist's 20% chance for 100% more poison damage only runs during a flask effect; the small node's
    // base_poison_duration_+% lengthens every poison; Master Surgeon's final -50% halves life-flask recovery.
    [Fact]
    public void Poison_duration_toxicist_and_surgeon_follow_their_stats()
    {
        Func<string, int> stats = name => name switch
        {
            "base_poison_duration_+%" => 20,
            "pathfinder_poison_damage_+100%_final_chance_during_flask_effect" => 20,
            _ => 0,
        };
        var duringFlask = MonsterPoisons.Settings.From([], "main_hand", stats, flaskEffect: true);
        Assert.Equal(2.4, duringFlask.Seconds, 6);   // 2 s x (1 + 20 / 100)
        Assert.Equal(20, duringFlask.MoreChance);
        Assert.Equal(0, MonsterPoisons.Settings.From([], "main_hand", stats, flaskEffect: false).MoreChance);

        var flask = new Flasks.Flask(100, 0, 2, 1, 3);
        var item = new Item(1, [], 0);
        var full = FlaskRecovery.For(flask, item, new Dictionary<string, int>(), _ => 0, 500, 1000);
        Assert.Equal(100, full.Life, 6);
        var surgeon = FlaskRecovery.For(flask, item, new Dictionary<string, int>(),
            name => name == "pathfinder_flask_life_to_recover_+%_final" ? -50 : 0, 500, 1000);
        Assert.Equal(50, surgeon.Life, 6);
    }

    // Master Distiller consumes 3 charges from each matching bar flask that holds enough charges and grants one bonus
    // per paid type; a type with too few charges pays nothing. Master Alchemist's free-use roll is one chance per use.
    [Fact]
    public void Distiller_plan_maps_paid_flask_types_and_alchemist_rolls_one_use()
    {
        Func<string, int> stats = name => name switch
        {
            "pathfinder_skills_consume_x_charges_from_a_bismuth_diamond_or_amethyst_flask" => 3,
            "pathfinder_skills_penetrate_elemental_resistances_%_if_charges_consumed_from_bismuth_flask" => 25,
            "pathfinder_skills_critical_strike_chance_+%_if_charges_consumed_from_diamond_flask" => 250,
            "pathfinder_skills_physical_damage_%_to_add_as_chaos_if_charges_consumed_from_amethyst_flask" => 37,
            _ => 0,
        };
        var plan = Game.Pathfinder.PlanDistiller(
        [
            (1u, "Bismuth Flask", 5),
            (2u, "Diamond Flask", 2),
            (3u, "Amethyst Flask", 9),
            (4u, "Quicksilver Flask", 10),
        ], stats);
        Assert.NotNull(plan);
        Assert.Equal(3, plan.Cost);
        Assert.Equal(new uint[] { 1, 3 }, plan.Flasks.Select(f => f.Id));

        var both = plan.BonusesFor([1, 3]);
        Assert.NotNull(both);
        Assert.Equal(25, both.ElementalPenetration);
        Assert.Equal(0, both.CriticalChanceIncrease);
        Assert.Equal(37, both.PhysicalAsChaos);
        var mapped = both.AdditionalStats();
        Assert.Equal(25, mapped[Program.Stats.Key("base_penetrate_elemental_resistances_%")!.Value]);
        Assert.Equal(37, mapped[Program.Stats.Key("physical_damage_%_to_add_as_chaos")!.Value]);
        Assert.Equal(0, plan.BonusesFor([1])!.CriticalChanceIncrease);

        Assert.Null(Game.Pathfinder.PlanDistiller([(1u, "Diamond Flask", 2)], stats));
        Assert.Null(Game.Pathfinder.PlanDistiller([(1u, "Bismuth Flask", 3)], _ => 0));

        Assert.True(Game.Pathfinder.FlaskUseIsFree(50, new FixedRolls(0)));
        Assert.False(Game.Pathfinder.FlaskUseIsFree(50, new FixedRolls(50)));
    }
}
