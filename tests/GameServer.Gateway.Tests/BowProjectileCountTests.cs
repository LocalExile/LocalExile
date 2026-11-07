using GameServer.Core.Projectiles;
using GameServer.Modding;

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

        // A lockstep client draws its own wand bolt or arrow when the default attack's record has the count the server
        // simulates (a bow's arrow totals too, as GGG's bow records); the server then sends no object for it. A
        // predictive client's record and a melee one carry none.
        string path = Path.Combine(Path.GetTempPath(), $"localexile-ordinary-count-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        ClientState Holding(string character, string? weapon)
        {
            var conn = new ClientState(new(1, "ordinary-count", 1000, 1000, 100, 100, null, []), 1, 1)
            {
                Character = character, PlayerLife = 1000, PlayerMana = 1000, Instance = new(), LockstepClient = true,
                Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                LeagueModule = new(new GameServer.Core.Accounts.League("test", "Standard", false, false), new HashSet<string>(["Game"])),
            };
            if (weapon is not null) Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find(weapon)!));
            return conn;
        }
        var bowConn = Holding("OrdinaryBowChar", "Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1");
        var wandConn = Holding("OrdinaryWandChar", "Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1");
        var meleeConn = Holding("OrdinaryMeleeChar", null);
        try
        {
            var character = new Dictionary<uint, int> { [Program.Stats.Key("number_of_additional_arrows")!.Value] = 1 };
            Assert.Equal([(StatKeys.TotalProjectilesToFire, 2), (StatKeys.VirtualTotalProjectiles, 2),
                (StatKeys.TotalArrowsToFire, 2), (StatKeys.VirtualTotalArrows, 2)], Game.OrdinaryRangedCountStats(bowConn, character));
            Assert.Equal([(StatKeys.TotalProjectilesToFire, 1), (StatKeys.VirtualTotalProjectiles, 1)],
                Game.OrdinaryRangedCountStats(wandConn, character));
            Assert.Empty(Game.OrdinaryRangedCountStats(meleeConn, character));
            // The record holds each count once, over a stale value in the stats it is built from.
            var record = Assert.Single(Game.PlayerDefaultSkills(bowConn, [(StatKeys.TotalProjectilesToFire, 9)], character));
            foreach (uint key in new[] { StatKeys.TotalProjectilesToFire, StatKeys.VirtualTotalProjectiles,
                StatKeys.TotalArrowsToFire, StatKeys.VirtualTotalArrows })
                Assert.Equal(2, Assert.Single(record.Stats!, s => s.Key == key).Value);
            Assert.True(bowConn.ClientDrawnShot == OrdinaryRangedAttack.Arrow && bowConn.DefaultAttackRecordLockstep);
            Game.PlayerDefaultSkills(meleeConn, [], character);
            Assert.Null(meleeConn.ClientDrawnShot);
            // A drawn shot collides on the client's contact (the row's radii: DefaultArrow 1, WandProjectile 2 cells, plus
            // the target's size), not the server's 4-cell stand-in, and flies the client's 150 cells.
            int None(string _) => 0;
            var drawn = Game.ClientDrawnProfile(bowConn, new(OrdinaryRangedAttack.Arrow), None, 0x8001, new(1, 0));
            Assert.Equal((1, 150d), (drawn.ContactCells, drawn.Range));
            Assert.Equal(2, Game.ClientDrawnProfile(wandConn, new(OrdinaryRangedAttack.Wand), None, 0x8001, new(1, 0)).ContactCells);
            bowConn.LockstepClient = false;
            Assert.Empty(Game.OrdinaryRangedCountStats(bowConn, character));
            Assert.DoesNotContain(Assert.Single(Game.PlayerDefaultSkills(bowConn, [], character)).Stats ?? [],
                s => s.Key == StatKeys.VirtualTotalArrows);
            Assert.False(bowConn.ClientDrawnShot is not null || bowConn.DefaultAttackRecordLockstep);

            // The initial predictive list is cached. Mode confirmation must rebuild it with the same equipment,
            // stats and pools, even after SendLockstepDefaultAttack has latched its refresh request.
            var predictive = Game.PlayerSkills(bowConn);
            Assert.Same(predictive, Game.PlayerSkills(bowConn));
            Assert.DoesNotContain(Assert.Single(predictive, s => s.Id == SkillCatalog.FirstSkillId).Stats ?? [],
                s => s.Key == StatKeys.VirtualTotalArrows);
            bowConn.LockstepClient = true;
            bowConn.DefaultAttackRecordLockstep = true;
            var confirmed = Game.PlayerSkills(bowConn);
            Assert.NotSame(predictive, confirmed);
            var confirmedDefault = Assert.Single(confirmed, s => s.Id == SkillCatalog.FirstSkillId);
            foreach (uint key in new[] { StatKeys.TotalProjectilesToFire, StatKeys.VirtualTotalProjectiles,
                StatKeys.TotalArrowsToFire, StatKeys.VirtualTotalArrows })
                Assert.Equal(1, Assert.Single(confirmedDefault.Stats!, s => s.Key == key).Value);
            Assert.Equal(OrdinaryRangedAttack.Arrow, bowConn.ClientDrawnShot);
            Assert.True(bowConn.DefaultAttackRecordLockstep);
            Assert.Same(confirmed, Game.PlayerSkills(bowConn));
        }
        finally
        {
            foreach (var conn in new[] { bowConn, wandConn, meleeConn }) PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
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
