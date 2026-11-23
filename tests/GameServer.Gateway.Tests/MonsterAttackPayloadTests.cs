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
            bool skeleton = path.StartsWith("Skeletons/", StringComparison.Ordinal);
            Assert.Equal(3, variety.Weapons.Count);
            var arrow = Assert.Single(variety.Weapons, w => w.Slot == 0);
            var bow = Assert.Single(variety.Weapons, w => w.Slot == 1);
            var quiver = Assert.Single(variety.Weapons, w => w.Slot == 7);
            Assert.Equal((ushort)(skeleton ? 1118 : 45814), arrow.VisualIdentity);
            Assert.Equal((byte)14, arrow.Type);
            Assert.Equal((byte)5, arrow.Stance);
            Assert.Equal((ushort)(skeleton ? 26894 : 42952), bow.VisualIdentity);
            Assert.Equal(BowSetup.NoWeaponType, bow.Type);
            Assert.Equal(BowSetup.NoStance, bow.Stance);
            Assert.Equal((ushort)(skeleton ? 3763 : 28915), quiver.VisualIdentity);
            Assert.Equal(BowSetup.NoWeaponType, quiver.Type);
            Assert.Equal(BowSetup.NoStance, quiver.Stance);
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

        // Bow monsters with an integral bow mesh only carry Weapon1 (arrow); slot 0 must still carry the Bow stance.
        var integral = Game.Monsters.Find("Metadata/Monsters/DemonBosses/DemonBoss3/DemonBoss3")!;
        var integralArrow = Assert.Single(integral.Weapons);
        Assert.Equal(0, integralArrow.Slot);
        Assert.Equal((ushort)45814, integralArrow.VisualIdentity);
        Assert.Equal((byte)14, integralArrow.Type);
        Assert.Equal((byte)5, integralArrow.Stance);
    }
}
