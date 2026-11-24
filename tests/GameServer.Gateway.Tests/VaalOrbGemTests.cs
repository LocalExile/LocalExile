namespace GameServer.Gateway.Tests;

/// <summary>A Vaal Orb on a gem rolls one of the outcomes the client's rows settle — one level higher,
/// quality to 23%, or its Vaal variant — and corrupts the gem. A corrupted or mirrored gem, or a gem
/// with no outcome left, is refused.</summary>
public sealed class VaalOrbGemTests
{
    [Fact]
    public void Vaal_on_a_gem_produces_one_settled_outcome_and_refuses_a_corrupted_mirrored_or_spent_gem()
    {
        var catalog = Program.Items;
        var rolls = new ItemRolls(catalog);
        var fireball = catalog.Find("Metadata/Items/Gems/SkillGemFireball")!;
        var vaalFireball = catalog.Find("Metadata/Items/Gems/SkillGemVaalFireball")!;
        var seen = new HashSet<string>();
        for (int seed = 0; seed < 1000; seed++)
        {
            var gem = ItemCatalog.Create(fireball);
            var result = rolls.Vaal(gem, fireball, (0, 0, 0), new Random(seed));
            Assert.NotNull(result);
            Assert.True(result.Corrupted);
            Assert.False(gem.Corrupted);
            int level = catalog.GemLevel(result);
            bool levelUp = result.TypeHash == fireball.Hash && result.Quality == 0 && level == catalog.GemLevel(gem) + 1;
            bool qualityUp = result.TypeHash == fireball.Hash && result.Quality == ItemRolls.VaalGemQuality && level == catalog.GemLevel(gem);
            bool variant = result.TypeHash == vaalFireball.Hash && result.Quality == 0 && level == catalog.GemLevel(gem);
            Assert.Equal(1, (levelUp ? 1 : 0) + (qualityUp ? 1 : 0) + (variant ? 1 : 0));
            if (levelUp) seen.Add("level");
            if (qualityUp) seen.Add("quality");
            if (variant) seen.Add("variant");
        }
        Assert.Equal(3, seen.Count);

        // Corruption is one-way: a corrupted gem is refused.
        Assert.Null(rolls.Vaal(ItemCatalog.Create(fireball) with { Corrupted = true }, fireball, (0, 0, 0), new Random(7)));

        // A mirrored gem is refused the same way.
        Assert.Null(rolls.Vaal(ItemCatalog.Create(fireball) with { Mirrored = true }, fireball, (0, 0, 0), new Random(7)));

        // And a gem with no outcome left — a Vaal gem already at 23% quality and above its cap — is refused.
        var spent = ItemCatalog.Create(vaalFireball) with { Quality = ItemRolls.VaalGemQuality, GemExperience = -1 };
        Assert.Null(rolls.Vaal(spent, vaalFireball, (0, 0, 0), new Random(7)));
    }
}
