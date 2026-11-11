using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class KineticBoltTests
{
    sealed class TestWorld : IProjectileWorld
    {
        public List<ProjectileActor> LiveActors { get; } = [];
        public HashSet<(int X, int Y)> Walls { get; } = [];
        public List<(long Id, uint Target, ProjectileVector At)> Hits { get; } = [];
        public List<ProjectileEvent> Events { get; } = [];

        public IReadOnlyList<ProjectileActor> Actors(long from, long until) => LiveActors;
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) =>
            ProjectileGeometry.TraceGrid(from, to, (x, y) => Walls.Contains((x, y)));
        public ProjectileHitResult Hit(ProjectileDamage damage)
        {
            Hits.Add((damage.Projectile.Id, damage.Target.Id, damage.Position));
            return ProjectileHitResult.Hit;
        }
        public void Publish(ProjectileEvent value) => Events.Add(value);
    }

    [Fact]
    public void Kinetic_bolt_maps_projectile_converts_forks_to_zig_zags_and_publishes_panel_stats()
    {
        var skills = new SkillCatalog();
        int effect = skills.EffectRow("KineticBolt");
        Assert.True(effect >= 0 && skills.IsProjectile(effect));
        var projectile = skills.ProjectileFor(effect);
        Assert.NotNull(projectile);
        Assert.Equal("Metadata/Projectiles/ZigZagWandProjectile", projectile.Value.Id);
        Assert.Equal(2000, projectile.Value.Speed);
        Assert.True(SkillHitPlan.ForSkill(skills, effect, 1).CanHit);

        var altX = skills.ProjectileFor(skills.EffectRow("KineticBoltAltX"));
        Assert.Equal("Metadata/Projectiles/ZigZagWandProjectile", altX?.Id);

        string path = Path.Combine(Path.GetTempPath(), $"localexile-kbolt-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "kbolt", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "KineticBoltChar", PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            var wand = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1")
                ?? Program.Items.Find("Driftwood Wand")!);
            var bolt = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemKineticBolt")!);
            var fork = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SupportGemFork")!);
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, wand with
            {
                Sockets = [new ItemSocket(SocketColour.White, bolt), new ItemSocket(SocketColour.White, fork)], Links = [2],
            });
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                "virtual_intelligence_for_requirements" }) conn.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;

            var granted = Game.PlayerSkills(conn).Single(s => Game.SkillEffectId(conn, s.Id) == "KineticBolt");
            Assert.NotNull(granted.Stats);
            var map = granted.Stats!.ToDictionary(kv => kv.Item1, kv => kv.Item2);
            Assert.Equal(6, map[Program.Stats.Key("kinetic_bolt_number_of_zig_zags")!.Value]);
            Assert.Equal(1, map[Program.Stats.Key("theoretical_number_of_forks")!.Value]);
            Assert.Equal(1, map[Program.Stats.Key("virtual_projectiles_cannot_fork")!.Value]);
            Assert.Equal(0, map[Program.Stats.Key("virtual_number_of_forks_for_projectiles_final")!.Value]);
            Assert.True(Game.ProjectilePathAvailable("test", conn, granted.Id, effect));
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    [Fact]
    public void Kinetic_bolt_runtime_zig_zags_on_end_enemy_and_wall_with_shared_history()
    {
        // 1. Open-field zig-zag geometry & Return after N turns (sequence=2 -> even parity -> sign=-1 on turn 1).
        var world = new TestWorld();
        var runtime = new ProjectileRuntime(world);
        var profile = GatewayProjectileSkillProfiles.Resolve("KineticBolt",
            "Metadata/Projectiles/ZigZagWandProjectile", name => name switch
            {
                "kinetic_wand_base_number_of_zig_zags" => 2,
                "kinetic_bolt_forks_apply_to_zig_zags" => 1,
                "projectiles_return" => 1,
                _ => 0
            }, 100, attack: true);
        profile = GatewayProjectileSkillProfiles.WithKineticBoltLegs(profile, sequence: 2);
        var owner = new ProjectileActor(new(10), 1, _ => new(0, 0), 0);
        world.LiveActors.Add(owner);
        runtime.Release(new ProjectileUse(profile, owner.Key, owner.Key, 1, new(0, 0), new(50, 0))
            { ReturnAnchor = owner.Key, Sequence = 2 });

        // First leg ends at 15 cells (t = 0.15s); secondaries are hidden when predicted and never Return.
        runtime.AdvanceTo(ProjectileRuntime.Time(0.16));
        var primary = Assert.Single(runtime.Projectiles, p => p.ParentId == 0);
        var firstSecondary = Assert.Single(runtime.Projectiles, p => p.ParentId != 0);
        Assert.Equal("Metadata/Projectiles/ZigZagSecondaryWandProjectile", firstSecondary.Profile.Id);
        Assert.True(firstSecondary.Profile.CannotReturn);
        Assert.True(firstSecondary.Profile.HidePredictedVisual);
        // Primary turned -45 deg (NE: x>0, y<0); secondary went +45 deg (SE: x>0, y>0).
        Assert.True(primary.Direction.X > 0 && primary.Direction.Y < 0);
        Assert.True(firstSecondary.Direction.X > 0 && firstSecondary.Direction.Y > 0);
        Assert.InRange(primary.Remaining, 8.5, 10.1);

        // Turn 2 triggers 10 cells later (at ~0.25s), flying 20 cells, then after 20 more cells (t=0.45s) primary Returns!
        runtime.AdvanceTo(ProjectileRuntime.Time(0.46));
        Assert.Equal(2, runtime.Projectiles.Count(p => p.ParentId != 0));
        Assert.Equal(1, primary.Phase);

        // 2. Enemy hit & wall hit before turns run out: shared EmissionId blocks secondary from hitting trigger enemy,
        //    and wall hit does not stall or lose remaining zig-zags.
        var hitWorld = new TestWorld();
        var hitRuntime = new ProjectileRuntime(hitWorld);
        var hitProfile = GatewayProjectileSkillProfiles.Resolve("KineticBolt",
            "Metadata/Projectiles/ZigZagWandProjectile", name => name switch
            {
                "kinetic_wand_base_number_of_zig_zags" => 2,
                "kinetic_bolt_forks_apply_to_zig_zags" => 1,
                "projectile_number_of_targets_to_pierce" => 1,
                _ => 0
            }, 100, attack: true);
        hitProfile = GatewayProjectileSkillProfiles.WithKineticBoltLegs(hitProfile, sequence: 3);
        var targetA = new ProjectileActor(new(100), 2, _ => new(8, 0), 1);
        hitWorld.LiveActors.Add(targetA);
        hitWorld.Walls.Add((9, 6));
        hitRuntime.Release(new ProjectileUse(hitProfile, new(1), new(1), 1, new(0, 0), new(50, 0)) { Sequence = 3 });
        // Turn 1 at targetA contact (3, 0) -> primary turns +45 deg (SE) and hits wall (9, 6) around t=0.115s -> Turn 2 turns -90 deg (NE).
        hitRuntime.AdvanceTo(ProjectileRuntime.Time(0.13));

        Assert.Single(hitWorld.Hits); // targetA hit once by primary; secondary shares history and never re-hits
        Assert.Equal(1, hitWorld.Events.Count(e => e.Kind == ProjectileEventKind.Spawn && e.Projectile.ParentId != 0 && e.At <= ProjectileRuntime.Time(0.1)));
        Assert.Equal(2, hitWorld.Events.Count(e => e.Kind == ProjectileEventKind.Spawn && e.Projectile.ParentId != 0));

        // After all 2 zig-zag turns are spent, primary still retains its 1 Pierce to hit targetB and pierce to targetC.
        var primaryAfterWalls = Assert.Single(hitRuntime.Projectiles, p => p.ParentId == 0);
        var pos = primaryAfterWalls.Position;
        var dir = primaryAfterWalls.Direction;
        var targetB = new ProjectileActor(new(101), 2, _ => pos + dir * 4, 1);
        var targetC = new ProjectileActor(new(102), 2, _ => pos + dir * 9, 1);
        hitWorld.LiveActors.Add(targetB);
        hitWorld.LiveActors.Add(targetC);
        hitRuntime.AdvanceTo(ProjectileRuntime.Time(0.30));

        Assert.Contains(hitWorld.Hits, h => h.Id == primaryAfterWalls.Id && h.Target == 101);
        Assert.Contains(hitWorld.Hits, h => h.Id == primaryAfterWalls.Id && h.Target == 102);
    }
}
