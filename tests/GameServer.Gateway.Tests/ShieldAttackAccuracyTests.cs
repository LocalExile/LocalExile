using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class ShieldAttackAccuracyTests
{
    [Theory]
    [InlineData("Shield Crush")]
    [InlineData("Spectral Shield Throw")]
    public void A_shield_skill_does_not_use_the_main_hand_weapons_local_accuracy(string name)
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        fixture.Equip((name, 1));
        var items = Game.ItemsOf(player);
        var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
        items.Remove(PlayerInventories.Weapon1, host.Id);
        var shield = ItemCatalog.Create(Program.Items.Find("Splintered Tower Shield")!) with
        {
            Sockets = host.Item.Sockets,
            Links = host.Item.Links
        };
        Assert.NotNull(items.Add(PlayerInventories.Offhand1, 0, 0, shield));
        var weapon = items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
        Assert.NotNull(weapon);
        ushort skill = Assert.Single(Game.GemSkillEffects(player)).Id;
        Assert.NotNull(Game.ShieldWeapon(player, skill));
        player.Pools = Game.PoolsFor(player, 100);
        var baseAccuracy = Game.SupportedAccuracy(player, skill);
        int normalAttack = Game.PlayerAccuracy(player);

        var local = Program.Items.Mods.ById("LocalIncreasedAccuracy1")!;
        var cannotEvade = Program.Items.Mods.ById("JunMasterAlwaysHits1")!;
        items.Change(PlayerInventories.Weapon1, weapon.Id, item => item with
        {
            Mods = [.. item.Mods, new(ModSection.Explicit, local.Hash, [15]), new(ModSection.Explicit, cannotEvade.Hash, [1])]
        });
        player.Pools = Game.PoolsFor(player, 100);
        Assert.True(Game.PlayerAccuracy(player) > normalAttack);
        Assert.True(Game.MainHandLocalSources(player)!.GetValueOrDefault(Stat.LocalAlwaysHit) != 0);
        Assert.Equal(baseAccuracy, Game.SupportedAccuracy(player, skill));
        Assert.False(baseAccuracy.CannotBeEvaded);

        var panel = Game.PlayerSkills(player).Single(s => s.Id == skill).Stats!.ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal(baseAccuracy.Rating, panel[Stat.MainHandAccuracyRating]);
        Assert.Equal(baseAccuracy.Rating, panel[Stat.OffHandAccuracyRating]);
        Assert.Equal(0, panel.GetValueOrDefault(Stat.OffHandAlwaysHit));
        if (name == "Spectral Shield Throw")
        {
            var gem = Assert.Single(Game.GemSkillEffects(player));
            var use = Game.CaptureGemProjectile(player, fixture.Session, skill, gem, (140, 100), new(1));
            var shot = Assert.IsType<ProjectileOffense>(use.Payload);
            Assert.Equal(baseAccuracy, shot.AccuracyProfile);
            Assert.Equal(baseAccuracy.Rating, shot.Accuracy);
        }

        using var global = GameEvents.PlayerStatSources.Add("Game", (owner, stats) => ReferenceEquals(owner, player)
            ? new Dictionary<uint, int>(stats) { [Stat.GlobalAlwaysHit] = 1 }
            : stats);
        var globalAccuracy = Game.SupportedAccuracy(player, skill);
        Assert.Equal(baseAccuracy.Rating, globalAccuracy.Rating);
        Assert.True(globalAccuracy.CannotBeEvaded);
        var globalPanel = Game.PlayerSkills(player).Single(s => s.Id == skill).Stats!.ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal(1, globalPanel[Stat.OffHandAlwaysHit]);
    }
}
