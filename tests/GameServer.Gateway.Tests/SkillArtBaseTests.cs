namespace GameServer.Gateway.Tests;

// Holy Sweep's hammers exist only from SkillArtVariations sweep's Holy variation (2): the skill sends 2 with no effect
// worn, and a worn effect's value counts from it (Originator's 1 → Zana 3); other skills keep the effect's own value.
public sealed class SkillArtBaseTests
{
    [Fact]
    public void Holy_sweep_counts_its_art_variation_from_holy()
    {
        Assert.Equal(2, SkillArtBase.Variation("Sweep", 0));
        Assert.Equal(3, SkillArtBase.Variation("Sweep", 1));
        Assert.Equal(5, SkillArtBase.Variation("SweepAltX", 3));
        Assert.Equal(0, SkillArtBase.Variation("VaalSweep", 0));
        Assert.Equal(7, SkillArtBase.Variation("RighteousFire", 7));
        Assert.Equal(0, SkillArtBase.Variation(null, 0));
    }
}
