namespace GameServer.Gateway.Tests;

public class DominusCombatEffectTests
{
    // Dominus' first granted effect, DominusChillingBond, links its action to a second entity the server never names:
    // used as his swing it crashed the client at 0x14012946a (live 2026-11-05). He swings DominusLightningSlam.
    [Fact]
    public void Dominus_swings_his_slam_not_his_bond()
    {
        var skills = new SkillCatalog();
        var dominus = new MonsterCatalog().Find("Metadata/Monsters/Pope/Pope");
        Assert.NotNull(dominus);

        int effect = Game.MavenNucleus.CombatEffect(skills, dominus!);

        Assert.Equal("DominusLightningSlam", skills.EffectId(effect));
    }
}
