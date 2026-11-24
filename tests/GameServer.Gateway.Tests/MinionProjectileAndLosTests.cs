using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class MinionProjectileAndLosTests
{
    sealed class Clock : IPlayerUpdateSchedule
    {
        internal double Time;
        public double Now(Player player) => Time;
        public Task Wait(int milliseconds, CancellationToken stopping) => Task.CompletedTask;
    }

    [Fact]
    public async Task Golem_step_respects_line_of_sight_ignores_undamageable_and_unstucks_wedged_minions()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var clock = new Clock { Time = 1 };
        var outer = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, clock);
        try
        {
            f.Player.Use(new AreaLayout(1, "minion-los-test", 200, 200, 100, 100,
                (x, y) => x >= 10 && x < 190 && y >= 10 && y < 190, [])
            {
                Sight = (x, _) => x == 110 ? 0 : 1,
            });
            f.Player.Walk.Teleport(90, 100, 0);

            ushort spectreSource = f.Equip(("Raise Spectre", 20)).Single();
            var revenant = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Revenant/RevenantMapBossStandalone_AtlasUber"));
            uint spectreId = await Game.SummonFighter(f.Player, spectreSource,
                Game.SummonData.Find("RaiseSpectre")!, (100, 100), restoredVariety: revenant.Id);
            var golem = f.Player.Summons.Fighters[spectreId];
            var spellAbility = Assert.Single(golem.Abilities, a => a.Projectile is not null && !a.Projectile.Lobbed);
            Assert.True(spellAbility.Attack.Ranged);
            Assert.True(spellAbility.Attack.Range > 10);

            var enemyVariety = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie"));
            // 0. An enemy with BaseCannotBeDamaged (encased essence / Divine Shrine) must be ignored by summon targeting.
            var undamageable = new SpawnedMonster("EncasedPreview", new(102, 100, 20), 60, 2, 5000)
            {
                Hostile = true,
                Variety = enemyVariety,
                Level = 70,
                Walk = f.Player.NewMonsterWalk(6000, 102, 100, 20, 2, false),
                EncounterStats = new Dictionary<uint, int> { [Stat.BaseCannotBeDamaged] = 1 },
            };
            f.Player.Area.Monsters[6000] = undamageable;
            Game.GolemStepLocked(f.Player, golem, 1.0);
            Assert.Null(golem.Pending);
            Assert.Equal(0u, golem.Target);
            f.Player.Area.Monsters.Remove(6000);
            golem.NextMove = 0;
            golem.Monster.LastTarget = null;

            var enemy = new SpawnedMonster("Target", new(120, 100, 20), 60, 2, 5000)
            {
                Hostile = true,
                Variety = enemyVariety,
                Level = 70,
                Walk = f.Player.NewMonsterWalk(6001, 120, 100, 20, 2, false),
            };
            f.Player.Area.Monsters[6001] = enemy;

            // 1. Wall at x=110 blocks LOS: ranged spectre must not reserve its projectile spell through the wall;
            //    instead it walks toward the target.
            Game.GolemStepLocked(f.Player, golem, 1.0);
            Assert.Null(golem.Pending);
            Assert.Equal((120, 100), golem.Monster.LastTarget);

            // 2. Clear LOS, but all abilities are on cooldown while distance (20) < range - 2:
            //    pursuit must clamp f <= 1.0 and never push the minion backward past its current position (x < 100).
            f.Player.Use(new AreaLayout(1, "minion-clear-test", 200, 200, 100, 100,
                (x, y) => x >= 10 && x < 190 && y >= 10 && y < 190, [])
            {
                Sight = (_, _) => 1,
            });
            f.Player.Walk.Teleport(90, 100, 2.0);
            foreach (var a in golem.Abilities)
                golem.Charges[a.Effect] = new Queue<double>(Enumerable.Repeat(20.0, a.Uses));
            golem.NextMove = 0;
            golem.Monster.Walk.Teleport(100, 100, 2.0);
            golem.Monster.LastTarget = null;
            clock.Time = 2.0;
            Game.GolemStepLocked(f.Player, golem, 2.0);
            Assert.Null(golem.Pending);
            Assert.NotNull(golem.Monster.LastTarget);
            Assert.InRange(golem.Monster.LastTarget!.Value.X, 100, 120);
        }
        finally
        {
            Game.AreaUpdateContext.Value = outer;
        }
    }

    [Fact]
    public async Task Returning_projectiles_support_grants_return_to_defiler_spectre()
    {
        using var f = new TriggeredSummonTests.Fixture();
        ushort spectreSource = f.Equip(("Raise Spectre", 20), ("Returning Projectiles Support", 20)).First();
        var revenant = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Revenant/RevenantMapBossStandalone_AtlasUber"));
        uint spectreId = await Game.SummonFighter(f.Player, spectreSource,
            Game.SummonData.Find("RaiseSpectre")!, (100, 100), restoredVariety: revenant.Id);
        var golem = f.Player.Summons.Fighters[spectreId];
        var spell = Assert.Single(golem.Abilities, a => a.Id == "RevenantBossSpellProjectile");
        var mods = golem.Monster.Mods.Effects;
        var profile = GatewayProjectileModifiers.Resolve(
            GatewayProjectileDefaults.Create(spell.Projectile!.Metadata, k => mods[k], spell.Projectile.Speed),
            k => mods[k], attack: false);
        Assert.True(profile.Returns);
        Assert.True(profile.PierceAllOnReturn);
    }
}
