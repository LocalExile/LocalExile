using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class RuthlessGemRulesTests
{
    [Theory]
    [InlineData("SkillGemFrostblink", "FrostblinkHardMode")]
    public void Explicit_unavailable_rules_cover_regular_alternate_and_hardmode_ids(string basis, string effect)
    {
        Assert.False(RuthlessGemRules.AllowsBase("Metadata/Items/Gems/" + basis));
        Assert.False(RuthlessGemRules.AllowsBase("Metadata/Items/GemAlternateEffects/" + basis + "AltX"));
        Assert.False(RuthlessGemRules.AllowsEffect(effect));
        Assert.False(RuthlessGemRules.AllowsEffect(effect + "AltX"));
    }

}
