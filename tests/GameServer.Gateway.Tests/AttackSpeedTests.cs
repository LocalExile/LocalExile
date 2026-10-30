namespace GameServer.Gateway.Tests;

public class AttackSpeedTests
{
    [Fact]
    public void Pob_increased_and_more_attack_speed_combine_into_the_client_percentage()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1,
            new CharacterStats.Bonus(AttackSpeedPct: 75, AttackSpeedMorePct: 10));

        // (1 + 75 / 100) * (1 + 10 / 100) = 1.925, i.e. the equivalent 93% increased attack speed.
        Assert.Equal(93, pools.AttackSpeedPct);
    }

}
