using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public sealed class BowProjectileCountTests
{
    // Bow attacks read the native arrow alias in addition to the generic projectile additions. The arrow stats must
    // not leak into a wand/spell projectile or a melee projectile skill.
    [Fact]
    public void Bow_attacks_add_generic_and_arrow_projectiles()
    {
        var stats = new Dictionary<string, int>
        {
            ["base_number_of_projectiles"] = 1,
            ["number_of_additional_projectiles"] = 1,
            ["number_of_additional_arrows"] = 1,
        };
        int Get(string name) => stats.GetValueOrDefault(name);

        var bow = GatewayProjectileDefaults.Resolve("Metadata/Projectiles/DefaultArrow", Get, 1000, attack: true, arrows: true);
        Assert.Equal(3, bow.Count);
        var wand = GatewayProjectileDefaults.Resolve("Metadata/Projectiles/WandProjectile", Get, 1000, attack: true,
            arrows: false);
        Assert.Equal(2, wand.Count);   // the bow-only arrow term is absent

        stats["base_number_of_arrows"] = 2;
        stats["number_of_additional_projectiles"] = 0;
        stats["number_of_additional_arrows"] = 0;
        Assert.Equal(3, GatewayProjectileDefaults.Resolve("Metadata/Projectiles/DefaultArrow", Get, 1000,
            attack: true, arrows: true).Count);

        stats["main_hand_accuracy_rating"] = 3000;
        stats["number_of_additional_arrows_while_main_hand_accuracy_is_3000_or_more"] = 1;
        Assert.Equal(4, GatewayProjectileDefaults.Resolve("Metadata/Projectiles/DefaultArrow", Get, 1000,
            attack: true, arrows: true).Count);

        var skills = new SkillCatalog();
        int tornado = skills.EffectRow("TornadoShot");
        Assert.True(tornado >= 0);
        var panel = new Dictionary<uint, int>
        {
            [Program.Stats.Key("base_number_of_projectiles")!.Value] = 1,
            [Program.Stats.Key("number_of_additional_arrows")!.Value] = 1,
        };
        Game.ApplyProjectilePanelStats(tornado, panel);
        Assert.Equal(2, panel[Program.Stats.Key("total_number_of_projectiles_to_fire")!.Value]);
        Assert.Equal(2, panel[Program.Stats.Key("total_number_of_arrows_to_fire")!.Value]);

        var fireball = new Dictionary<uint, int>
        {
            [Program.Stats.Key("number_of_additional_arrows")!.Value] = 1,
        };
        Game.ApplyProjectilePanelStats(skills.EffectRow("Fireball"), fireball);
        Assert.Equal(1, fireball[Program.Stats.Key("total_number_of_projectiles_to_fire")!.Value]);
        // A native mouse-triggered Burning Arrow creates a client primary even though its existing simulation
        // does not take the spell/Spectral Throw geometry path. Do not generalize this to unverified attacks.
        Assert.True(Game.ClientDrawsPrimaryProjectile(false, skills.EffectId(skills.EffectRow("BurningArrow"))));
        Assert.False(Game.ClientDrawsPrimaryProjectile(false, skills.EffectId(skills.EffectRow("VaalBurningArrow"))));
        Assert.False(Game.ClientDrawsPrimaryProjectile(false, "Melee"));
        Assert.True(Game.ClientDrawsPrimaryProjectile(true, "Fireball"));
        Assert.True(Game.ClientDrawsPrimaryProjectile(true, "SpectralThrow"));
    }

    // Tornado Shot's primary arrow ends at the targeted location where the client releases its child group. It must
    // not keep flying to the generic range after that burst; ordinary projectiles keep their range.
    [Fact]
    public void Tornado_shot_primary_stops_at_the_target_location()
    {
        Assert.Equal(45, Game.PrimaryRange("TornadoShot", 150, 45), 6);
        Assert.Equal(45, Game.PrimaryRange("TornadoShotAltX", 150, 45), 6);
        Assert.Equal(150, Game.PrimaryRange("TornadoShot", 150, 200), 6);
        Assert.Equal(0.001, Game.PrimaryRange("TornadoShot", 150, 0), 6);
        Assert.Equal(150, Game.PrimaryRange("Fireball", 150, 45), 6);

        static int Get(string _) => 0;
        var fan = Game.NativeVolley(new ProjectileProfile("fan") { Count = 3 }, Get);
        Assert.NotNull(fan.NativeVolley);
        Assert.Equal(0, fan.NativeVolley!.Angle(1, 3, 100), 6);
        Assert.True(fan.NativeVolley.Angle(0, 3, 100) < 0);
        Assert.True(fan.NativeVolley.Angle(2, 3, 100) > 0);
    }
}
