using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>The uniques' "when on low/full life" spelling of the life/mana state conditions, and the movement stat
/// those mods fold onto.</summary>
public class WhenOnLifeConditionTests
{
    [Fact]
    public void When_on_life_conditions_answer_and_fold_like_while_on_life()
    {
        var flags = new Dictionary<string, bool> { ["_when_on_low_life"] = true, ["_when_on_full_life"] = false };
        var subject = new StatConditions.Subject(Flags: flags);
        Assert.Equal(StatConditions.State.Holds, StatConditions.Of("damage_+%_when_on_low_life", subject));
        Assert.Equal(StatConditions.State.Fails, StatConditions.Of("movement_velocity_+%_when_on_full_life", subject));
        Assert.Equal(StatConditions.State.Holds, StatConditions.Of("spell_damage_taken_+%_when_on_low_mana",
            new StatConditions.Subject(Flags: new Dictionary<string, bool> { ["_when_on_low_mana"] = true })));
        Assert.Equal("damage_+%", StatConditions.Fold("damage_+%_when_on_low_life", _ => true));
        Assert.Equal("base_movement_velocity_+%", StatConditions.Fold("movement_velocity_+%_when_on_full_life", _ => true));
    }

    [Fact]
    public void Movement_velocity_stat_reaches_the_walk_speed_bonus()
    {
        var stats = new Dictionary<uint, int> { [Program.Stats.Key("movement_velocity_+%")!.Value] = 20 };
        var bonus = Game.PassiveBonus(stats, default, default);
        Assert.Equal(20, bonus.MovementVelocityPct);
    }
}
