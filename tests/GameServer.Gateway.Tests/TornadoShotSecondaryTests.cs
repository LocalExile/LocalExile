using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public sealed class TornadoShotSecondaryTests
{
    sealed class Arena(bool wall) : IProjectileWorld
    {
        public readonly List<ProjectileActor> Targets = [];
        public readonly List<(long Arrow, uint Target)> Hits = [];
        public readonly List<long> Redirects = [];
        public readonly List<ProjectileEntity> Flights = [];
        public (int X, int Y)? PlacementWall { get; init; }
        public IReadOnlyList<ProjectileActor> Actors(long from, long until) => Targets;
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) =>
            PlacementWall is { } cell ? ProjectileGeometry.TraceGrid(from, to, (x, y) => (x, y) == cell)
            : wall && from.X < 60 && to.X >= 60 ? new((60 - from.X) / (to.X - from.X), new(-1, 0)) : null;
        public ProjectileHitResult Hit(ProjectileDamage damage)
        {
            Hits.Add((damage.Projectile.Id, damage.Target.Id));
            return ProjectileHitResult.Hit;
        }
        public void Publish(ProjectileEvent value)
        {
            if (value.Kind == ProjectileEventKind.Redirect) Redirects.Add(value.Projectile.Id);
            if (value.Kind == ProjectileEventKind.Spawn) Flights.Add(value.Projectile);
        }
    }

    // Tornado Shot's primary arrow pierces and releases three secondary arrows at its endpoint. The server simulates
    // them for damage but sends no objects because the client draws the same arrows itself, and one secondary per set
    // may hit an enemy.
    [Fact]
    public void Tornado_shot_primary_pierces_and_releases_hidden_secondaries()
    {
        static int Get(string _) => 0;
        var profile = GatewayProjectileSkillProfiles.Resolve("TornadoShot",
            "Metadata/Projectiles/TornadoShotArrow", Get, 200, attack: true);

        Assert.True(profile.Pierce < 0);
        Assert.True(profile.HideVisual);
        Assert.NotNull(profile.EndEmission);
        Assert.True(profile.EndEmission!.Radial);
        Assert.NotNull(profile.EndEmission.BaseDirection);
        Assert.Equal(3, profile.EndEmission.Child.Count);
        Assert.Equal("Metadata/Projectiles/TornadoShotSecondaryArrow", profile.EndEmission.Child.Id);
        Assert.True(profile.EndEmission.Child.HideVisual);
        Assert.Equal(ProjectileRuntime.Time(.3), profile.EndEmission.Child.Lifetime);
        Assert.Equal(ProjectileHitScope.Emission, profile.EndEmission.Child.HitRule.Scope);
        Assert.Equal(ProjectileRepeat.Once, profile.EndEmission.Child.HitRule.Repeat);
        Assert.Null(profile.EndEmission.Child.EndEmission);

        // The client's terminal RNG vector [1,2,4096,0,0,0,0,0] samples 5185 (phi bits 0x4094ede8) and aims
        // along (sin phi, -cos phi); the server must produce the same base ray.
        var baseDirection = GatewayProjectileSkillProfiles.TornadoShotBaseDirection(1, 2, 4096, 0);
        Assert.Equal(-0.9982982277870178, baseDirection.X, 6);
        Assert.Equal(0.05831458792090416, baseDirection.Y, 6);

        foreach (int extra in new[] { 0, 4 })
        {
            int CloudburstStat(string key) => key switch
            {
                "projectiles_rain" or "projectiles_nova" => 1,
                "base_number_of_arrows" => 9,
                "number_of_additional_projectiles" => extra,
                "tornado_shot_num_of_secondary_projectiles" => 2,
                _ => 0
            };
            var cloudburst = GatewayProjectileSkillProfiles.Resolve("TornadoShotAltX", profile.Id,
                CloudburstStat, 200, attack: true, arrows: true) with { Range = 30 };
            Assert.Equal(1, cloudburst.Count);
            Assert.False(cloudburst.DirectDamage);
            var arrows = cloudburst.EndEmission!.Child;
            Assert.Equal(9 + extra, arrows.Count);
            Assert.Equal(profile.EndEmission.Child.Id, arrows.Id);
            Assert.Null(arrows.EndEmission);

            var arena = new Arena(false);
            var landing = new ProjectileVector(30, 0);
            for (int i = 0; i < arrows.Count; i++)
            {
                var target = landing + new ProjectileVector(24, 0).Rotate(Math.Tau * i / arrows.Count);
                arena.Targets.Add(new(new((uint)i + 10), 2, _ => target, 0));
            }
            var runtime = new ProjectileRuntime(arena);
            runtime.Release(new ProjectileUse(cloudburst, new(1), new(1), 1, new(0, 0), landing));
            long payload = Assert.Single(runtime.Projectiles).Id;
            runtime.AdvanceTo(ProjectileRuntime.Time(1));
            Assert.DoesNotContain(arena.Hits, hit => hit.Arrow == payload);
            Assert.Equal(9 + extra, arena.Hits.Select(hit => hit.Target).Distinct().Count());
            Assert.Equal(9 + extra, arena.Hits.Select(hit => hit.Arrow).Distinct().Count());
        }
        CheckArrowNovaPrimaries(sweep: false);
        CheckArrowNovaPrimaries(sweep: true);
        CheckVolleyLandingPoints();
        CheckRandomLandingDirections();
        CheckRainGroundTargets();
    }

    static void CheckRainGroundTargets()
    {
        using var battle = new ArrowRainAndMirageTests.Battle("Split Arrow", "Arrow Nova Support", "Returning Projectiles Support");
        var conn = battle.Conn;
        conn.PlayerStats[Program.Stats.Key("projectile_maximum_range_override")!.Value] = 37;
        var gem = Game.GemSkillEffects(conn).Single();
        (int X, int Y) source = (100, 100), aim = (149, 101), wall = (127, 101);
        ((int, int)? Walk, bool Sight, (int X, int Y)? Landing)[] cases =
        [
            (null, false, aim), (null, true, aim), (wall, false, aim), (wall, true, (126, 101)),
            (aim, false, (148, 101)), (source, false, aim), (source, true, null), ((101, 100), true, source)
        ];
        foreach (var (walk, sight, landing) in cases)
        {
            conn.Barrier = walk.HasValue ? new HashSet<(int, int)> { walk.Value } : new HashSet<(int, int)>();
            conn.ProjectileWalls.Clear();
            if (sight) conn.ProjectileWalls.Add(wall);
            var use = Game.CaptureGemProjectile(conn, battle.Session, gem.Id, gem, aim, new(0), releaseDelay: .2);
            // The flight sees a wall even when the action admitted a clear line before release.
            var arena = new Arena(false) { PlacementWall = wall };
            arena.Targets.Add(new(use.Owner, use.Team, _ => use.LaunchSource, 0));
            var runtime = new ProjectileRuntime(arena);
            runtime.Release(use);
            if (landing is not { } cell)
            {
                runtime.AdvanceTo(ProjectileRuntime.Time(1));
                Assert.Empty(arena.Flights);
                continue;
            }
            var rig = Assert.Single(arena.Flights);
            long due = ProjectileRuntime.Time(.215);
            runtime.AdvanceTo(due - 1);
            Assert.Same(rig, Assert.Single(runtime.Projectiles));
            runtime.AdvanceTo(due);
            Assert.Empty(runtime.Projectiles);
            Assert.Equal(new ProjectileVector(cell.X, cell.Y), rig.Position);
            Assert.False(rig.Profile.Returns);
            runtime.AdvanceTo(due + ProjectileRuntime.Time(.221));
            var arrows = arena.Flights.Where(p => p.ParentId == rig.Id).ToArray();
            Assert.Equal(use.Profile.EndEmission!.Child.Count, arrows.Length);
            Assert.All(arrows, p =>
            {
                Assert.True(p.Profile.Returns);
                Assert.Equal(37, p.Profile.ReturnOvershoot);
            });
            Assert.DoesNotContain(rig.Id, arena.Redirects);
        }
    }

    static void CheckRandomLandingDirections()
    {
        // Split Arrow extends the sampled ray to its configured range. A lone arrow uses its own action byte.
        foreach (ProjectileVector[] ends in new ProjectileVector[][]
        {
            [new(24, 11)],
            [new(23, 12), new(281, 163), new(104, 241), new(201, 237), new(37, -6)]
        })
        {
            int Get(string key) => key switch
            {
                "projectiles_rain" or "projectiles_nova" or "random_projectile_direction" => 1,
                "projectile_return_%_chance" => 50,
                "number_of_additional_projectiles" => ends.Length - 1,
                _ => 0
            };
            var profile = GatewayProjectileSkillProfiles.Resolve("SplitArrow", "Metadata/Projectiles/SplitArrowDefault",
                Get, 200, attack: true, arrows: true);
            var arena = new Arena(false);
            var runtime = new ProjectileRuntime(arena);
            runtime.Release(new ProjectileUse(profile, new(4096), new(4096), 1, new(100, 100), new(147, 100)) { Sequence = 19 });
            runtime.AdvanceTo(ProjectileRuntime.Time(2));
            var carrier = Assert.Single(arena.Flights, p => p.ParentId == 0);
            var arrows = arena.Flights.Where(p => p.ParentId == carrier.Id).ToArray();
            Assert.Equal(new ProjectileVector(147, 100), carrier.Use.Aim);
            Assert.All(arrows, p => Assert.Equal(new ProjectileVector(146, 97), p.Use.Origin));
            Assert.Equal(ends, arrows.Select(p => p.Use.Aim));
            // Sequence 19 and owner 4096 give 58, 78, 35, 10 and 44 at the five landing arrows' ordinals.
            Assert.Equal(new[] { false, false, true, true, true }.Take(arrows.Length), arrows.Select(p => p.Profile.Returns));
        }
    }

    static void CheckVolleyLandingPoints()
    {
        int Get(string key) => key switch
        {
            "projectiles_rain" or "projectiles_nova" => 1,
            "support_parallel_projectile_number_of_points_per_side" => 2,
            "virtual_additional_projectiles_fire_parallel_x_dist" => 80,
            "number_of_additional_projectiles" => 6,
            _ => 0
        };
        ProjectileVector[] starts = [new(146, 97), new(139, 99), new(139, 99), new(154, 96), new(154, 96), new(132, 101), new(161, 94)];
        ProjectileVector[] tornadoEnds = [new(134, 42), new(127, 44), new(152, 154), new(141, 40), new(166, 151), new(120, 45), new(148, 39)];
        ProjectileVector[] splitEnds = [new(114, -49), new(106, -47), new(172, 245), new(121, -51), new(186, 242), new(99, -46), new(128, -52)];
        foreach (bool tornado in new[] { true, false })
        foreach (bool moved in new[] { false, true })
        {
            var profile = GatewayProjectileSkillProfiles.Resolve(tornado ? "TornadoShot" : "SplitArrow",
                "Metadata/Projectiles/" + (tornado ? "TornadoShotArrow" : "SplitArrowDefault"), Get, 200, attack: true, arrows: true);
            var arena = new Arena(false) { PlacementWall = (130, 99) };
            var runtime = new ProjectileRuntime(arena);
            runtime.Release(new ProjectileUse(profile, new(4096), new(4096), 1, new(100, 100), new(147, 100))
            {
                Sequence = 19, LaunchSourceAt = moved ? _ => new(169, 111) : null
            });
            runtime.AdvanceTo(ProjectileRuntime.Time(3));
            var carrier = Assert.Single(arena.Flights, p => p.ParentId == 0);
            var arrows = arena.Flights.Where(p => p.ParentId == carrier.Id).ToArray();
            // The wall crosses only the line from the original actor cell to the first positive point.
            int[] admitted = moved ? [0, 1, 2, 3, 4, 5, 6] : [0, 3, 4, 5, 6];
            Assert.Equal(admitted.Select(i => starts[i]), arrows.Select(p => p.Use.Origin));
            Assert.Equal(admitted.Select(i => (tornado ? tornadoEnds : splitEnds)[i]), arrows.Select(p => p.Use.Aim));
            Assert.Equal(Enumerable.Range(1, arrows.Length), arrows.Select(p => p.SpawnOrdinal));
            Assert.Single(arrows.Select(p => p.EmissionId).Distinct());
        }
    }

    static void CheckArrowNovaPrimaries(bool sweep)
    {
        int Get(string key) => key switch
        {
            "projectiles_rain" or "projectiles_nova" => 1,
            "projectiles_barrage" => sweep ? 1 : 0,
            "number_of_additional_projectiles" => 4,
            "tornado_shot_num_of_secondary_projectiles" => 2,
            _ => 0
        };
        var profile = GatewayProjectileSkillProfiles.Resolve("TornadoShot",
            "Metadata/Projectiles/TornadoShotArrow", Get, 200, attack: true, arrows: true);
        var arena = new Arena(false);
        // Clear-ground 3.29.3c launch and destination cells for sequence 1, owner 4096, landing (147,100).
        ProjectileVector[] origins = sweep
            ? [new(150, 100), new(147, 97), new(144, 100), new(147, 103), new(150, 100)]
            : Enumerable.Repeat(new ProjectileVector(148, 97), 5).ToArray();
        ProjectileVector[] ends = sweep
            ? [new(208, 101), new(148, 40), new(88, 101), new(148, 161), new(208, 101)]
            : [new(163, 43), new(204, 95), new(167, 150), new(103, 132), new(101, 66)];
        for (int i = 0; i < ends.Length; i++)
        {
            var target = origins[i] + (ends[i] - origins[i]) * .4;
            arena.Targets.Add(new(new((uint)(10 + i)), 2, _ => target, 0));
        }
        var runtime = new ProjectileRuntime(arena);
        runtime.Release(new ProjectileUse(profile, new(4096), new(4096), 1, new(100, 100), new(147, 100))
            { Sequence = 1 });
        runtime.AdvanceTo(ProjectileRuntime.Time(2));
        var carrier = Assert.Single(arena.Flights, p => p.ParentId == 0);
        Assert.DoesNotContain(arena.Hits, hit => hit.Arrow == carrier.Id);
        var primaries = arena.Flights.Where(p => p.ParentId == carrier.Id).ToArray();
        Assert.Equal(origins, primaries.Select(p => p.Use.Origin));
        Assert.Equal(ends, primaries.Select(p => p.Use.Aim));
        Assert.Equal(sweep ? 5 : 1, primaries.Select(p => p.EmissionId).Distinct().Count());
        Assert.Equal(ProjectileRuntime.Time(sweep ? .4 : 0), primaries[^1].CreatedAt - primaries[0].CreatedAt);
        int[] ordinals = sweep ? [1, 2, 3, 6, 9] : [1, 2, 3, 4, 5];
        Assert.Equal(ordinals, primaries.Select(p => p.SpawnOrdinal));
        foreach (var primary in primaries)
        {
            Assert.Contains(arena.Hits, hit => hit.Arrow == primary.Id);
            var group = arena.Flights.Where(p => p.ParentId == primary.Id).ToArray();
            Assert.Equal(2, group.Length);
            var baseRay = GatewayProjectileSkillProfiles.TornadoShotBaseDirection(1, (uint)(2 + primary.SpawnOrdinal), 4096, 0);
            Assert.InRange((group[0].Direction - baseRay).Length, 0, .000001);
            Assert.All(group, arrow =>
            {
                Assert.InRange((arrow.Use.Origin - primary.Use.Aim).Length, 0, .002);
                Assert.Null(arrow.Profile.EndEmission);
            });
        }
        Assert.Equal(16, arena.Flights.Count);
    }

    // Point Blank/Far Shot measure the whole continuation: a child starts with the primary's travelled distance and
    // adds its own, while its fixed lifetime means projectile speed scales how far it gets.
    [Fact]
    public void Tornado_shot_children_continue_the_primary_distance()
    {
        foreach (var (chains, wall, grant, forbid, redirects, hits) in new[]
        {
            (1, false, 0, 0, 1, 2), (1, false, 1, 0, 1, 4), (1, false, 1, 1, 1, 2),
            (1, true, 0, 0, 0, 1), (2, true, 0, 0, 1, 2), (3, true, 0, 0, 2, 3),
            (1, true, 1, 0, 0, 4), (2, true, 1, 1, 1, 2)
        })
        {
            int Get(string key) => key switch
            {
                "number_of_chains" => chains,
                "projectile_chain_from_terrain_chance_%" => wall ? 100 : 0,
                "arrows_always_pierce_after_forking" => grant,
                "virtual_projectiles_cannot_pierce" => forbid,
                _ => 0
            };
            var arena = new Arena(wall);
            if (wall) arena.Targets.Add(new(new(50), 2, _ => new(40, 20), 0));
            var runtime = new ProjectileRuntime(arena);
            var profile = GatewayProjectileSkillProfiles.Resolve("TornadoShot",
                "Metadata/Projectiles/TornadoShotArrow", Get, 200, attack: true, arrows: true) with { Range = 100 };
            runtime.Release(new ProjectileUse(profile, new(999), new(999), 1, new(0, 0), new(100, 0)) { Sequence = 1 });
            var primary = Assert.Single(runtime.Projectiles);
            double sample = wall ? .95 : .65;
            runtime.AdvanceTo(ProjectileRuntime.Time(sample));

            var children = runtime.Projectiles.Where(p => p.ParentId != 0).OrderBy(p => p.Id).ToArray();
            Assert.Equal(3, children.Length);
            double expectedDistance = wall ? 190 : 130;
            Assert.All(children, child => Assert.InRange(child.Distance, expectedDistance - .1, expectedDistance + .1));
            if (wall)
            {
                Assert.Equal([primary.Id], arena.Redirects);
                Assert.Equal([(primary.Id, 50u)], arena.Hits);
                arena.Redirects.Clear();
                arena.Hits.Clear();
                arena.Targets.Clear();
            }
            // The primary already hit target 50 at the wall. The secondary group has a fresh target ledger.
            for (int i = 0; i < children.Length; i++)
            for (int rank = 0; rank < 4; rank++)
            {
                var point = children[i].Position + children[i].Direction * ((rank + 1) * 5);
                arena.Targets.Add(new(new((uint)(50 + i * 10 + rank)), 2, _ => point, 0));
            }
            runtime.AdvanceTo(ProjectileRuntime.Time(sample + .3));
            for (int i = 0; i < children.Length; i++)
            {
                long id = children[i].Id;
                Assert.Equal(redirects, arena.Redirects.Count(arrow => arrow == id));
                Assert.Equal(Enumerable.Range(50 + i * 10, hits).Select(n => (uint)n),
                    arena.Hits.Where(h => h.Arrow == id).Select(h => h.Target));
            }
        }
    }
}
