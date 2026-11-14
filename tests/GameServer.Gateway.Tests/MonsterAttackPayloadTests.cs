namespace GameServer.Gateway.Tests;

public class MonsterAttackPayloadTests
{
    [Theory]
    [InlineData("MountainGoatmanLeapSlam_", "MeleeAtAnimationSpeed")]
    [InlineData("GoatmanLightningLeapSlamExtra", "GAGoatmanLeapSlamDamage")]
    public void Leap_goats_choose_their_ordinary_swing_only_if_they_have_one(string type, string expectedEffect)
    {
        var skills = new SkillCatalog();
        var goat = Assert.IsType<MonsterCatalog.Variety>(new MonsterCatalog().Find("Metadata/Monsters/Goatman/" + type));
        int effect = Game.MavenNucleus.CombatEffect(skills, goat);
        Assert.StartsWith(expectedEffect, skills.EffectId(effect));
    }
}
