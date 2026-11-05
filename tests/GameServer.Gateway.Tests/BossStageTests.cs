namespace GameServer.Gateway.Tests;

public sealed class BossStageTests
{
    [Fact]
    public void Extraction_uses_its_animation_contact_independently_of_recovery()
    {
        const string hillock = "Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal";
        var clip = new SkillCatalog().MonsterContact(hillock, "Throw", 0);
        Assert.NotNull(clip);
        Assert.Equal("extract", clip.Animation);
        Assert.Equal(1833, clip.LengthMs);
        Assert.Equal(458, clip.ContactMs);
        Assert.InRange(3 * clip.ContactFraction!.Value, .74, .76);
    }
}
