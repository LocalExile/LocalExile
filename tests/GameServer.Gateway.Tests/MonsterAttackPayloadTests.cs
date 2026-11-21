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
            // A bow is held as a player's is: the arrow in the right hand (slot 0), the bow in the left (slot 1).
            Assert.Contains(variety.Weapons, w => w.Slot == 0);
            var bow = Assert.Single(variety.Weapons, w => w.Slot == 1);
            Assert.Equal((ushort)(path.StartsWith("Skeletons/", StringComparison.Ordinal) ? 26894 : 42952), bow.VisualIdentity);
            uint id = await Game.SpawnMonster(f.Player, variety, 20,
                GameServer.Core.Protocol.Instance.MonsterRarity.Normal, 100, 100, unrisen: false);
            var origin = f.Player.Instance.World.MonsterOrigins[id];
            Assert.Contains("Inventories", variety.ComponentsOrBase);
            Assert.Equal(variety.Weapons, origin.Object.Equipped);
            Assert.NotEmpty(origin.Object.Blob());

            uint empty = await Game.SpawnMonster(f.Player, variety, 20,
                GameServer.Core.Protocol.Instance.MonsterRarity.Normal, 100, 100, unrisen: false, ownedLooks: []);
            Assert.Empty(f.Player.Instance.World.MonsterOrigins[empty].Object.Equipped!);
            var custom = new[] { bow with { SkinVisual = (ushort)(bow.VisualIdentity == 26894 ? 42952 : 26894) } };
            uint overridden = await Game.SpawnMonster(f.Player, variety, 20,
                GameServer.Core.Protocol.Instance.MonsterRarity.Normal, 100, 100, unrisen: false, ownedLooks: custom);
            Assert.Equal(custom, f.Player.Instance.World.MonsterOrigins[overridden].Object.Equipped);
        }
    }
}
