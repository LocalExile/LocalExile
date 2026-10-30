using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public partial class VaalSoulGatewayTests
{
    [Theory]
    [InlineData("full")]
    public void Vaal_preflight_does_not_disable_the_same_gems_ordinary_partner(string cost)
    {
        using var f = new Fixture();
        if (cost == "free") Set(f.Conn, VaalSoulCost.NoSouls, 1);
        if (cost == "reduced") Set(f.Conn, VaalSoulCost.FinalPercent, -100);
        var skills = (IReadOnlyList<GrantedSkill>)Call("PlayerSkills", f.Conn);
        var ordinary = skills.Single(s => s.EffectsPerLevelRow == 0);
        Assert.False(ordinary.VaalResourcesEnabled);
        bool started = false;
        Assert.True(Program.TryBeginSkillCooldownUse(f.Conn, ordinary.Id, Program.Skills.EffectRow("Fireball"),
            null, false, () => started = true, out var ticket));
        Assert.True(started); Assert.Null(ticket);
    }

    public static IEnumerable<object[]> UnsupportedVaalInputs()
    {
        foreach (string cost in new[] { "full", "free", "reduced" })
            foreach (var (gem, route) in new[]
            {
                ("SkillGemVaalFireball", "target"), ("SkillGemVaalGroundSlam", "approach"),
                ("SkillGemVaalFireball", "ground"), ("SkillGemVaalGroundSlam", "attack-move"),
                ("SkillGemVaalHaste", "self"), ("SkillGemVaalHaste", "instant"),
                ("SkillGemVaalHaste", "instant-location")
            }) yield return [gem, route, cost];
    }

}
