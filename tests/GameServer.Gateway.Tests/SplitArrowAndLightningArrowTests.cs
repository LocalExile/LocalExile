namespace GameServer.Gateway.Tests;

public sealed class SplitArrowAndLightningArrowTests
{
    [Fact]
    public void SplitArrowAndLightningArrowResolveTheirProjectileProfiles()
    {
        var skills = new SkillCatalog();

        int splitArrow = skills.EffectRow("SplitArrow");
        Assert.True(splitArrow >= 0);
        var splitArrowProj = skills.ProjectileFor(splitArrow);
        Assert.NotNull(splitArrowProj);
        Assert.Equal("Metadata/Projectiles/SplitArrowDefault", splitArrowProj!.Value.Id);
        Assert.Equal(3260, splitArrowProj.Value.Speed);

        int lightningArrow = skills.EffectRow("LightningArrow");
        Assert.True(lightningArrow >= 0);
        var lightningArrowProj = skills.ProjectileFor(lightningArrow);
        Assert.NotNull(lightningArrowProj);

        var lightningArrowProfile = GatewayProjectileSkillProfiles.Resolve("LightningArrow", lightningArrowProj!.Value.Id,
            name => name == Stat.LightningArrowMaximumNumberOfExtraTargets ? 3 : 0, 3260, attack: true, arrows: true);
        Assert.NotNull(lightningArrowProfile.ContactTargets);
        Assert.Equal(3, lightningArrowProfile.ContactTargets!.Count);
        Assert.Equal(18, lightningArrowProfile.ContactTargets.Radius);

        var altProfile = GatewayProjectileSkillProfiles.Resolve("LightningArrowAltX", "Metadata/Projectiles/LightningArrow",
            _ => 0, 3260, attack: true, arrows: true);
        Assert.Null(altProfile.ContactTargets);
    }

    [Fact]
    public void Split_arrow_helmet_adds_arrows_and_splitting_targets()
    {
        static GameServer.Core.Projectiles.ProjectileProfile Resolve(string effect, int helmet)
        {
            bool splitting = effect == "SplitArrowAltX";
            var stats = new Dictionary<uint, int>
            {
                [Stat.BaseNumberOfArrows] = splitting ? 1 : 5,
                [Stat.ProjectileNumberToSplit] = splitting ? 3 : 0,
                [Stat.ModifiersToNumberOfProjectilesInsteadApplyToSplitting] = splitting ? 1 : 0,
                [Stat.SplitArrowNumOfAdditionalProjectiles] = helmet,
            };
            return GatewayProjectileSkillProfiles.Resolve(effect, "Metadata/Projectiles/SplitArrowDefault",
                name => stats.GetValueOrDefault(name), 3260, attack: true, arrows: true);
        }

        foreach (string effect in new[] { "SplitArrow", "SplitArrowAltY", "VaalSplitArrow" })
        {
            Assert.Equal(5, Resolve(effect, 0).Count);
            Assert.Equal(7, Resolve(effect, 2).Count);
        }
        var splittingPlain = Resolve("SplitArrowAltX", 0);
        Assert.Equal(1, splittingPlain.Count);
        Assert.Equal(3, splittingPlain.Split);
        var splittingHelmet = Resolve("SplitArrowAltX", 3);
        Assert.Equal(1, splittingHelmet.Count);
        Assert.Equal(6, splittingHelmet.Split);
    }
}
