using GameServer.Core.Accounts;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class MinionProjectileAndLosTests
{
    sealed class Clock : IPlayerUpdateSchedule
    {
        internal double Time;
        public double Now(ClientState conn) => Time;
        public Task Wait(int milliseconds, CancellationToken stopping) => Task.CompletedTask;
    }

    [Fact]
    public void SkillCatalog_loads_spiral_nova_metadata_on_monster_spell_projectiles()
    {
        var skills = Program.Skills;
        int bossEffect = skills.EffectRow("RevenantBossSpellProjectile");
        var boss = skills.MonsterProjectile(bossEffect);
        Assert.NotNull(boss);
        Assert.False(boss!.Lobbed);
        Assert.Equal(0, boss.Variation);
        Assert.Equal(9, boss.Count);
        Assert.Equal(750, boss.SpiralTimeMs);
        Assert.Equal(50, boss.SpiralAngle);
        Assert.Equal(-20, boss.SpiralStartAngle);

        int spectreEffect = skills.EffectRow("RevenantSpellProjectileSpectre");
        var spectre = skills.MonsterProjectile(spectreEffect);
        Assert.NotNull(spectre);
        Assert.False(spectre!.Lobbed);
        Assert.Equal(3, spectre.Count);
        Assert.Equal(150, spectre.SpiralTimeMs);
        Assert.Equal(20, spectre.SpiralAngle);
        Assert.Equal(-10, spectre.SpiralStartAngle);
    }

    [Fact]
    public async Task Golem_step_respects_line_of_sight_clamps_pursuit_and_unstucks_wedged_minions()
    {
        using var f = new MinionUtilityTests.Fixture();
        var clock = new Clock { Time = 1 };
        var outer = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, clock);
        try
        {
            f.Conn.Use(new AreaLayout(1, "minion-los-test", 200, 200, 100, 100,
                (x, y) => x >= 10 && x < 190 && y >= 10 && y < 190, [])
            {
                Sight = (x, _) => x == 110 ? 0 : 1,
            });
            f.Conn.Walk.Teleport(90, 100, 0);

            ushort spectreSource = f.Equip(("Raise Spectre", 20)).Single();
            var revenant = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Revenant/RevenantBoss"));
            uint spectreId = await Game.SummonFighter("test", f.Stream, f.Ciphers, f.Conn, spectreSource,
                Game.SummonData.Find("RaiseSpectre")!, (100, 100), restoredVariety: revenant.Id);
            var golem = f.Conn.Summons.Fighters[spectreId];
            var spellAbility = Assert.Single(golem.Abilities, a => a.Projectile is not null && !a.Projectile.Lobbed);
            Assert.True(spellAbility.Attack.Ranged);
            Assert.True(spellAbility.Attack.Range > 10);

            var enemyVariety = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie"));
            var enemy = new SpawnedMonster("Target", new(120, 100, 20), 60, 2, 5000)
            {
                Hostile = true,
                Variety = enemyVariety,
                Level = 70,
                Walk = f.Conn.NewMonsterWalk(6001, 120, 100, 20, 2, false),
            };
            f.Conn.Monsters[6001] = enemy;

            // 1. Wall at x=110 blocks LOS: ranged spectre must not reserve its projectile spell through the wall;
            //    instead it walks toward the target.
            Game.GolemStepLocked("test", f.Stream, f.Ciphers, f.Conn, golem, 1.0);
            Assert.Null(golem.Pending);
            Assert.Equal((120, 100), golem.Monster.LastTarget);

            // 2. Clear LOS, but all abilities are on cooldown while distance (20) < range - 2:
            //    pursuit must clamp f <= 1.0 and never push the minion backward past its current position (x < 100).
            f.Conn.Use(new AreaLayout(1, "minion-clear-test", 200, 200, 100, 100,
                (x, y) => x >= 10 && x < 190 && y >= 10 && y < 190, [])
            {
                Sight = (_, _) => 1,
            });
            f.Conn.Walk.Teleport(90, 100, 2.0);
            foreach (var a in golem.Abilities)
                golem.Charges[a.Effect] = new Queue<double>(Enumerable.Repeat(20.0, a.Uses));
            golem.NextMove = 0;
            golem.Monster.Walk.Teleport(100, 100, 2.0);
            golem.Monster.LastTarget = null;
            clock.Time = 2.0;
            Game.GolemStepLocked("test", f.Stream, f.Ciphers, f.Conn, golem, 2.0);
            Assert.Null(golem.Pending);
            Assert.NotNull(golem.Monster.LastTarget);
            Assert.InRange(golem.Monster.LastTarget!.Value.X, 100, 120);

            // 3. Wedged stationary minion (> 10 cells from owner) teleports back next to owner after 0.75s without progress.
            f.Conn.Use(new AreaLayout(1, "minion-wedged-test", 200, 200, 50, 100,
                (x, y) => x >= 10 && x < 190 && y >= 10 && y < 190
                    && !(Math.Abs(x - 100) <= 2 && Math.Abs(y - 100) <= 2 && (x != 100 || y != 100)), [])
            {
                Sight = (_, _) => 1,
            });
            f.Conn.Walk.Teleport(50, 100, 3.0);
            golem.Monster.Walk.Teleport(100, 100, 3.0);
            golem.NextMove = 0;
            golem.LastProgressPos = (100, 100);
            golem.StuckSince = double.NaN;
            clock.Time = 3.0;
            Game.GolemStepLocked("test", f.Stream, f.Ciphers, f.Conn, golem, 3.0);
            Assert.Equal(3.0, golem.StuckSince);
            clock.Time = 3.8;
            Game.GolemStepLocked("test", f.Stream, f.Ciphers, f.Conn, golem, 3.8);
            Assert.True(Game.Dist(golem.Monster.Walk.Position(3.8), f.Conn.Walk.Position(3.8)) <= 12);
        }
        finally
        {
            Game.AreaUpdateContext.Value = outer;
        }
    }

    [Fact]
    public async Task Golem_launch_schedules_spiral_nova_bolts_and_returning_projectile_damage()
    {
        using var f = new MinionUtilityTests.Fixture();
        var clock = new Clock { Time = 0 };
        var outer = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, clock);
        try
        {
            var session = new Game.ProjectileSession("test", f.Stream, f.Ciphers, f.Conn);
            f.Conn.ProjectileRuntimeState = session;

            ushort spectreSource = f.Equip(("Raise Spectre", 20)).Single();
            var revenant = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Revenant/RevenantBoss"));
            uint spectreId = await Game.SummonFighter("test", f.Stream, f.Ciphers, f.Conn, spectreSource,
                Game.SummonData.Find("RaiseSpectre")!, (100, 100), restoredVariety: revenant.Id);
            var golem = f.Conn.Summons.Fighters[spectreId];
            golem.Monster.Mods = new([], [], new MonsterModEffects(new Dictionary<string, int>
            {
                ["projectiles_return"] = 1,
                ["support_return_returning_projectiles_damage_+%_final"] = -50,
                ["always_pierce"] = 1,
            }));
            var ability = Assert.Single(golem.Abilities, a => a.Projectile is { SpiralAngle: > 0 });

            var enemyVariety = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie"));
            var enemy = new SpawnedMonster("Dummy", new(130, 100, 20), 60, 2, 10_000)
            {
                Hostile = true,
                Variety = enemyVariety,
                Level = 70,
                Walk = f.Conn.NewMonsterWalk(6002, 130, 100, 0, 2, false),
            };
            f.Conn.Monsters[6002] = enemy;

            Game.GolemAbilityContactLocked("test", f.Stream, f.Ciphers, f.Conn, golem,
                new(ability, 6002, enemy, (130, 100), 0, golem.Monster.ActionGeneration), 0);

            // Spiral nova schedules all 9 individual bolts across the sweep window (up to 0.7s); advancing to 0.8s spawns all 9.
            session.AdvanceTo(ProjectileRuntime.Time(0.8));
            var active = session.Runtime.Projectiles.ToArray();
            Assert.Equal(9, active.Length);
            Assert.All(active, p =>
            {
                Assert.True(p.Profile.Returns);
                Assert.Equal(150, p.Profile.ReturnOvershoot);
                Assert.Equal(ProjectileHitScope.Projectile, p.Profile.HitRule.Scope);
            });

            // Advance long enough for outward flight, turnaround/return, and returning hit scaled by 0.5x (-50% final).
            session.AdvanceTo(ProjectileRuntime.Time(6.0));
            Assert.True(enemy.Life < 10_000);
        }
        finally
        {
            Game.AreaUpdateContext.Value = outer;
        }
    }
}
