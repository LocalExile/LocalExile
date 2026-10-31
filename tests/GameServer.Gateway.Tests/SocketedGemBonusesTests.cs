using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class SocketedGemBonusesTests
{
    static readonly SkillCatalog Skills = new();
    [Theory]
    [InlineData("SkillGemFireball", "local_socketed_fire_gem_level_+", 3, 0)]
    [InlineData("SkillGemFireball", "local_socketed_cold_gem_level_+", 0, 0)]
    [InlineData("SkillGemFireball", "local_socketed_fire_gem_quality_+", 0, 3)]
    [InlineData("SkillGemFireball", "local_socketed_support_gem_level_+", 0, 0)]
    [InlineData("SupportGemAdditionalLevel", "local_socketed_support_gem_level_+", 3, 0)]
    [InlineData("SupportGemAdditionalLevel", "local_socketed_active_skill_gem_quality_+", 0, 0)]
    public void Local_bonuses_follow_the_native_gem_family_and_active_support_scope(string id, string stat,
        int expectedLevel, int expectedQuality)
    {
        var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + id)!);
        var host = ItemCatalog.Create(Program.Items.Find("Plate Vest")!) with
            { Sockets = [new(SocketColour.White, gem)] };
        var result = SocketedGemBonuses.For(host, 0, gem, Program.Items, Skills,
            new Dictionary<string, int> { [stat] = 3 }, 100);
        Assert.Equal((expectedLevel, expectedQuality), result);
    }
}
