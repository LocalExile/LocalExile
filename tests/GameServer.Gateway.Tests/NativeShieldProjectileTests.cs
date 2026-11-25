using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class NativeShieldProjectileTests
{
    [Fact]
    public void Shattering_uses_shield_evasion_and_its_own_critical_chance()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        player.Use(new(1, "ShieldProjectile", 350, 350, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
        player.Walk.Teleport(100, 100, 1);
        fixture.Equip(("Spectral Shield Throw of Shattering", 1));
        var items = Game.ItemsOf(player);
        var shield = ItemCatalog.Create(Program.Items.Find("Goathide Buckler")!, random: new Random(11));
        var equipped = items.Add(PlayerInventories.Offhand1, 0, 0, shield);
        var gem = Assert.Single(Game.GemSkillEffects(player), row => row.Name == "Spectral Shield Throw of Shattering");

        Assert.Null(Game.WeaponRestricted(player, gem.Effect));
        Assert.True(Game.ProjectilePathAvailable(player, gem.Id, gem.Effect));
        var launch = Game.CaptureGemProjectile(player, fixture.Session, gem.Id, gem, (118, 100), new(0));
        var shot = Assert.IsType<ProjectileOffense>(launch.Payload);
        Assert.Equal(500, shot.CriticalChance);
        Assert.True(shot.Hit[DamageType.Cold].Min > 69);
        Assert.True(shot.Hit[DamageType.Physical].IsZero);
        Assert.Equal(1, launch.Profile.Count);
        Assert.NotNull(launch.Profile.EndEmission);
        Assert.Equal(7, launch.Profile.EndEmission!.Child.Count);
        Assert.True(launch.Profile.EndEmission.EmitOnContinueContact);
        Assert.True(launch.Profile.CannotPierce);

        items.Remove(PlayerInventories.Offhand1, equipped.Id);
        Assert.NotNull(Game.WeaponRestricted(player, gem.Effect));
    }
}
