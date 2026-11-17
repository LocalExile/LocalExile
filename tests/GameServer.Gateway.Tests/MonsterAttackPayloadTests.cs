namespace GameServer.Gateway.Tests;

public class MonsterAttackPayloadTests
{
    [Theory]
    [InlineData("MountainGoatmanLeapSlam_", "MeleeAtAnimationSpeed")]
    [InlineData("GoatmanLightningLeapSlamExtra", "GAGoatmanLeapSlamDamage")]
    public void Leap_goats_choose_their_ordinary_swing_only_if_they_have_one(string type, string expectedEffect)
    {
        var skills = new SkillCatalog();
        var goat = Assert.IsType<MonsterCatalog.Variety>(new MonsterCatalog().Find("Metadata/Monsters/Goatman/" + type));
        int effect = Game.MavenNucleus.CombatEffect(skills, goat);
        Assert.StartsWith(expectedEffect, skills.EffectId(effect));
    }
    [Fact]
    public async Task Ordinary_monsters_publish_native_weapons_and_keep_explicit_appearance_overrides()
    {
        using var f = new MinionUtilityTests.Fixture();
        foreach (string path in new[] { "Skeletons/SkeletonBowSplitArrow", "Bandits/BanditBowKraitynSpecific", "Bandits/BanditBowBurningArrow" })
        {
            var variety = Game.Monsters.Find("Metadata/Monsters/" + path)!;
            var bow = Assert.Single(variety.Weapons);
            Assert.Equal(0, bow.Slot);
            Assert.Equal((ushort)(path.StartsWith("Skeletons/", StringComparison.Ordinal) ? 26894 : 42952), bow.VisualIdentity);
            uint id = await Game.SpawnMonster("test", f.Stream, f.Ciphers, f.Conn, variety, 20,
                GameServer.Core.Protocol.Instance.MonsterRarity.Normal, 100, 100, unrisen: false);
            var origin = f.Conn.Instance.World.MonsterOrigins[id];
            Assert.Contains("Inventories", variety.ComponentsOrBase);
            Assert.Equal(variety.Weapons, origin.Object.Equipped);
            Assert.NotEmpty(origin.Object.Blob());

            uint empty = await Game.SpawnMonster("test", f.Stream, f.Ciphers, f.Conn, variety, 20,
                GameServer.Core.Protocol.Instance.MonsterRarity.Normal, 100, 100, unrisen: false, ownedLooks: []);
            Assert.Empty(f.Conn.Instance.World.MonsterOrigins[empty].Object.Equipped!);
            var custom = new[] { bow with { SkinVisual = (ushort)(bow.VisualIdentity == 26894 ? 42952 : 26894) } };
            uint overridden = await Game.SpawnMonster("test", f.Stream, f.Ciphers, f.Conn, variety, 20,
                GameServer.Core.Protocol.Instance.MonsterRarity.Normal, 100, 100, unrisen: false, ownedLooks: custom);
            Assert.Equal(custom, f.Conn.Instance.World.MonsterOrigins[overridden].Object.Equipped);
        }
    }
}
