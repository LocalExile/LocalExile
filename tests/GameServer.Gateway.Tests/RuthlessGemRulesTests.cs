using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class RuthlessGemRulesTests
{
    [Theory]
    [InlineData("SkillGemFrostblink", "FrostblinkHardMode")]
    [InlineData("SkillGemFlameDash", "FlameDash")]
    [InlineData("SkillGemCorpseWarp", "Bodyswap")]
    [InlineData("SkillGemNewPhaseRun", "PhaseRun")]
    [InlineData("SkillGemVenomGyre", "VenomGyre")]
    [InlineData("SupportGemItemRarity", "SupportItemRarity")]
    public void Explicit_unavailable_rules_cover_regular_alternate_and_hardmode_ids(string basis, string effect)
    {
        Assert.False(RuthlessGemRules.AllowsBase("Metadata/Items/Gems/" + basis));
        Assert.False(RuthlessGemRules.AllowsBase("Metadata/Items/GemAlternateEffects/" + basis + "AltX"));
        Assert.False(RuthlessGemRules.AllowsEffect(effect));
        Assert.False(RuthlessGemRules.AllowsEffect(effect + "AltX"));
    }

    [Fact]
    public void Other_movement_skills_and_normal_supports_remain_available()
    {
        foreach (string gem in new[] { "SkillGemQuickstep", "SkillGemFireball", "SupportGemAddedColdDamage", "SkillGemFlickerStrike" })
            Assert.True(RuthlessGemRules.AllowsBase("Metadata/Items/Gems/" + gem));
        Assert.True(RuthlessGemRules.AllowsEffect("Quickstep"));
        Assert.True(RuthlessGemRules.AllowsEffect("Fireball"));
    }

    [Fact]
    public void Actual_Ruthless_drop_pool_excludes_the_unavailable_gems_but_keeps_found_supports()
    {
        string data = BenchCraftingTests.Data();
        var catalog = new ItemCatalog(data, ruthless: true);
        var drops = new ItemDrops(catalog, folder: data, ruthless: true);
        var found = new HashSet<string>();
        var rng = new Random(7654);
        for (int n = 0; n < 5000; n++)
            foreach (var drop in drops.Roll(100, MonsterRarity.Unique, rng))
                if (catalog.Gem(drop.Type) is not null) found.Add(drop.Type.Id);
        Assert.NotEmpty(found);
        Assert.All(found, id => Assert.True(RuthlessGemRules.AllowsBase(id), id));
        Assert.Contains(found, id => catalog.Gem(catalog.Find(id)!)?.IsSupport == true);
    }
}
