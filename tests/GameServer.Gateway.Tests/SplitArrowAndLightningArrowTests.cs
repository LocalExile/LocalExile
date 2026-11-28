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
    public void Skill_specific_helmet_enchant_adds_projectiles()
    {
        static GameServer.Core.Projectiles.ProjectileProfile Resolve(string effect, int helmet)
        {
            bool split = effect is "SplitArrow" or "SplitArrowAltX" or "SplitArrowAltY" or "VaalSplitArrow";
            bool splitting = effect == "SplitArrowAltX";
            bool spark = effect is "Spark" or "SparkAltX" or "SparkAltY" or "VaalSpark";
            var stats = new Dictionary<uint, int>
            {
                [Stat.BaseNumberOfArrows] = split ? splitting ? 1 : 5 : 0,
                [Stat.BaseNumberOfProjectiles] = spark ? 4 : 0,
                [Stat.ProjectileNumberToSplit] = splitting ? 3 : 0,
                [Stat.ModifiersToNumberOfProjectilesInsteadApplyToSplitting] = splitting ? 1 : 0,
                [Stat.SplitArrowNumOfAdditionalProjectiles] = split ? helmet : 0,
                [Stat.SparkNumOfAdditionalProjectiles] = spark ? helmet : 0,
            };
            return GatewayProjectileSkillProfiles.Resolve(effect,
                spark ? "Metadata/Projectiles/Spark" : "Metadata/Projectiles/SplitArrowDefault",
                name => stats.GetValueOrDefault(name), 3260, attack: split, arrows: split);
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

        Assert.Equal(4, Resolve("Spark", 0).Count);
        Assert.Equal(6, Resolve("Spark", 2).Count);
        Assert.Equal(7, Resolve("SparkAltX", 3).Count);
        Assert.Equal(6, Resolve("VaalSpark", 2).Count);
    }
}
