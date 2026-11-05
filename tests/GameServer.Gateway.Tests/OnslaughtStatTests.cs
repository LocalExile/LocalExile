namespace GameServer.Gateway.Tests;

// Onslaught is Path of Building's Speed modifier with the Attack and Cast flags plus MovementSpeed: all three reach
// the character's stat buckets, and attack and movement also reach the pools that time swings and the walk.
public sealed class OnslaughtStatTests
{
    [Fact]
    public void Onslaught_grants_attack_cast_and_movement_speed()
    {
        var sources = UtilityFlasks.OnslaughtSources(Program.Items.Onslaught!, 1.0)
            .Select(s => (s.Stat, s.Value)).ToList();

        var stats = new Dictionary<uint, int>();
        UtilityFlasks.AddRawStats(stats, sources, Program.Stats);
        Assert.Equal(20, stats[Program.Stats.Key("attack_speed_+%")!.Value]);
        Assert.Equal(20, stats[Program.Stats.Key("cast_speed_+%")!.Value]);

        var bonus = UtilityFlasks.Apply(CharacterStats.Bonus.None, sources);
        Assert.Equal(20, bonus.AttackSpeedPct);
        Assert.Equal(20, bonus.MovementVelocityPct);
    }
}
