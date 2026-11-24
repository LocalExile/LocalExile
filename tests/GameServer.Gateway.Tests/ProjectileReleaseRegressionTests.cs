using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public sealed class ProjectileReleaseRegressionTests
{
    [Fact]
    public async Task Artillery_ballista_shapes_resolve_after_paid_placement()
    {
        foreach (var (name, crossEnchant, hitsLateralEnemy) in new[]
        {
            ("Artillery Ballista", false, false),
            ("Artillery Ballista", true, true),
            ("Artillery Ballista of Cross Strafe", false, true),
            ("Artillery Ballista of Focus Fire", false, true),
        })
        {
            var clock = new Clock();
            using var fixture = new TriggeredSummonTests.Fixture(clock);
            var player = Prepare(fixture, clock.At, "Crude Bow");
            using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (!ReferenceEquals(owner, player)) return values;
                values[Stat.GlobalAlwaysHit] = 1;
                values[Stat.AttackMinimumAddedPhysicalDamage] = values[Stat.AttackMaximumAddedPhysicalDamage] = 500;
                if (crossEnchant) values[Stat.ArtilleryBallistaCrossStrafePattern] = 1;
                return values;
            });
            var previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
            try
            {
                ushort skill = Assert.Single(fixture.Equip((name, 1)));
                var centre = Target(811, 130, 100);
                var side = Target(812, 130, 119);
                var distant = Target(813, 250, 100);
                uint mana = player.PlayerMana;
                var placed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Placed() => placed.TrySetResult();
                player.Totems.Changed += Placed;
                try
                {
                    Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (110, 100)));
                    await placed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally { player.Totems.Changed -= Placed; }

                uint afterPlacement = player.PlayerMana;
                Assert.True(afterPlacement < mana);
                var totem = Assert.Single(player.Totems.Living(clock.At));
                Assert.True(totem.Ballista);
                fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                Game.TickSpellTotemsLocked(player, []);
                Assert.True(totem.NextCast > clock.At);
                clock.At = totem.NextCast + .2;
                fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                Assert.Equal(centre.MaxLife, centre.Life);
                clock.At = totem.NextCast + 1;
                fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                Assert.True(centre.Life < centre.MaxLife);
                Assert.Equal(hitsLateralEnemy, side.Life < side.MaxLife);
                Assert.Equal(distant.MaxLife, distant.Life);
                Assert.Equal(afterPlacement, player.PlayerMana);

                SpawnedMonster Target(uint id, int x, int y)
                {
                    var target = new SpawnedMonster("ballista target", new(x, y, 0), 0, 0, 100_000)
                        { ObjectId = id, Hostile = true, NoRewards = true };
                    player.Area.Monsters.Add(id, target);
                    player.Area.Grid.Set(new(id), (x, y));
                    return target;
                }
            }
            finally { Game.AreaUpdateContext.Value = previous; }
        }
    }

    [Fact]
    public void Split_arrow_variant_splits_at_terrain_and_at_range_end()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        Prepare(fixture, 1, "Crude Bow");
        foreach (var (name, variant) in new[] { ("Split Arrow of Splitting", true), ("Split Arrow", false) })
        {
            ushort skill = Assert.Single(fixture.Equip((name, 17)));
            var gem = Assert.Single(Game.GemSkillEffects(fixture.Player), gem => gem.Id == skill);
            Assert.True(Game.ProjectilePathAvailable(fixture.Player, skill, gem.Effect));
            var use = Game.CaptureGemProjectile(
                fixture.Player, fixture.Session, skill, gem, (195, 100), new SkillCriticalRoll(0));
            use = use with { Profile = use.Profile with { Count = 1, Range = 75 } };
            foreach (bool wall in new[] { true, false })
            {
                var arena = new Arena(wall);
                var runtime = new ProjectileRuntime(arena);
                runtime.Release(use);
                runtime.AdvanceTo(ProjectileRuntime.Time(2));

                if (variant) Assert.True(use.Profile.Split > 0);
                Assert.Equal(variant ? use.Profile.Split : 0, arena.Children.Count);
                foreach (var child in arena.Children)
                {
                    double expectedX = wall ? 124.5 : use.Origin.X + use.Profile.Range;
                    Assert.InRange(Math.Abs(expectedX - child.Position.X), 0, .1);
                    if (wall) Assert.True(child.Projectile.Direction.X <= 0);
                }
            }
        }

        // Locus Mine respects modifiers_to_projectile_count_do_not_apply, scales child volleys, and Arrow Nova children inherit split flags.
        var locked = GatewayProjectileSkillProfiles.Resolve("IceShotAltX", "Metadata/Projectiles/DefaultArrow", s => s switch
        {
            Stat.BaseNumberOfArrows => 5,
            Stat.ModifiersToProjectileCountDoNotApply => 1,
            Stat.NumberOfProjectilesPlusPctFinalFromLocusMineSupport => -30,
            _ => 0
        }, 100, attack: true, arrows: true);
        Assert.Equal(5, locked.Count);
        var cloudburstMine = GatewayProjectileSkillProfiles.Resolve("TornadoShotAltX", "Metadata/Projectiles/TornadoShotArrow", s => s switch
        {
            Stat.BaseNumberOfArrows => 9,
            Stat.ProjectilesRain => 1,
            Stat.NumberOfProjectilesPlusPctFinalFromLocusMineSupport => -30,
            Stat.ProjectilesCanSplitFromTerrain => 1,
            Stat.ProjectilesCanSplitAtEndOfRange => 1,
            _ => 0
        }, 100, attack: true, arrows: true);
        Assert.Equal(6, cloudburstMine.EndEmission!.Child.Count);
        Assert.True(cloudburstMine.EndEmission.Child.SplitOnTerrain);
        Assert.True(cloudburstMine.EndEmission.Child.SplitAtEndOfRange);
    }

    static Player Prepare(TriggeredSummonTests.Fixture fixture, double now, string weapon)
    {
        var player = fixture.Player;
        player.Use(new(1, "projectile-regression", 350, 350, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
        player.Walk.Teleport(100, 100, now);
        player.Connection.LockstepClient = false;
        player.Connection.PlayerSent = player.Connection.PlayerObjectCreated = true;
        player.ProjectileRuntimeState = fixture.Session;
        var items = Game.ItemsOf(player);
        items.Change(PlayerInventories.Weapon1, Assert.Single(items.Items(PlayerInventories.Weapon1)).Id,
            _ => ItemCatalog.Create(Program.Items.Find(weapon)!, random: new(19)));
        fixture.Session.AdvanceTo(ProjectileRuntime.Time(now));
        return player;
    }

    sealed class Arena(bool wall) : IProjectileWorld
    {
        public readonly List<ProjectileEvent> Children = [];
        public IReadOnlyList<ProjectileActor> Actors(long from, long until) => [];
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) =>
            wall ? ProjectileGeometry.TraceGrid(from, to, (x, _) => x == 125) : null;
        public ProjectileHitResult Hit(ProjectileDamage hit) => ProjectileHitResult.Hit;
        public void Publish(ProjectileEvent value)
        {
            if (value.Kind == ProjectileEventKind.Spawn && value.Projectile.ParentId != 0) Children.Add(value);
        }
    }

    sealed class Clock : IPlayerUpdateSchedule
    {
        public double At = 1;
        public double Now(Player player) => At;
        public async Task Wait(int milliseconds, CancellationToken stopping)
        {
            await Task.Yield();
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
        }
    }
}
