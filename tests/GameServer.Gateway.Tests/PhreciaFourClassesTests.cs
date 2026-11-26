namespace GameServer.Gateway.Tests;

/// <summary>The reversed chill a Phrecia node gives, as the game's ailments apply it, which needs no mod; the classes
/// are the phreciaascendancies mod's (LocalExile-Mods), with their tests.</summary>
public sealed class PhreciaFourClassesTests
{
    // One action-speed bucket (main) with Changing Seasons and the base-speed ceiling (this PR): a reversed 20% chill
    // speeds the player up, the character's own action_speed_-% 10 still slows, and Juggernaut's
    // not_affected_by_action_speed_modifiers caps the result at base speed.
    [Fact]
    public void A_reversed_chill_and_the_characters_action_speed_share_one_bucket()
    {
        Func<uint, int> Stats(params (string Name, int Value)[] stats) =>
            name => stats.FirstOrDefault(s => Stat.Of(s.Name) == name).Value;
        double Factor(params (string, int)[] stats)
        {
            var ailments = new PlayerElementalAilments();
            var get = Stats(stats);
            ailments.Reconcile(get);
            ailments.Inflict(PlayerElementalAilments.Kind.Chill, 20, 2, 0, get, new Random(1));
            return ailments.TotalActionFactor(1, 10);
        }
        Assert.Equal(.70, Factor(), 6);
        Assert.Equal(1.10, Factor(("chill_effect_is_reversed", 1)), 6);
        Assert.Equal(1, Factor(("chill_effect_is_reversed", 1), ("not_affected_by_action_speed_modifiers", 1)), 6);
    }
}
