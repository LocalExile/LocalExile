using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class SunderWaveTests
{
    [Fact]
    public async Task Waves_advance_with_variant_timing_and_cap_their_overlapping_shockwaves()
    {
        foreach (var (name, shockwaves, delay) in new[] { ("Sunder", 5, .4), ("Sunder of Earthbreaking", 3, .1) })
        {
            using var trial = new Trial(name);
            var emitters = Enumerable.Range(0, 7).Select(i => trial.Target((uint)(900 + i), 107 + i % 3, 108)).ToArray();
            var lateral = trial.Target(910, 107, 121);
            var later = trial.Target(911, 135, 100);
            var end = trial.Target(912, 180, 100);
            var contacts = new List<(uint Id, double At)>();
            var shockDamage = new List<double>();
            double firstDirect = 0;
            using var observe = GameEvents.OutgoingDamage.Add("Game", (player, _, monster, damage) =>
            {
                if (ReferenceEquals(player, trial.Player))
                {
                    if (monster == lateral) shockDamage.Add(damage[DamageType.Physical].Min);
                    else if (firstDirect == 0 && monster == emitters[0]) firstDirect = damage[DamageType.Physical].Min;
                }
                return damage;
            });

            await trial.Cast(0x8001, (180, 100), id => contacts.Add((id, trial.Clock.At)));

            Assert.All(emitters, monster => Assert.NotEqual(-1, monster.AttackEvasion.Value));
            Assert.Equal(-1, lateral.AttackEvasion.Value);
            Assert.True(firstDirect > 0);
            Assert.Equal(shockwaves, shockDamage.Count);
            Assert.All(shockDamage, damage => Assert.InRange(damage / firstDirect, .59, .61));
            double first = contacts.First(c => c.Id == emitters[0].ObjectId).At;
            double delayed = Assert.Single(contacts, c => c.Id == later.ObjectId).At;
            Assert.InRange(delayed - first, 2 * delay - .02, 2 * delay + .02);
            Assert.True(end.Life < end.MaxLife);
        }
    }

    [Fact]
    public async Task Recast_honours_the_minimum_and_Trarthus_turns_at_enemies_or_terrain()
    {
        foreach (var (name, continues) in new[] { ("Sunder", false), ("Sunder of Earthbreaking", true) })
        {
            using var trial = new Trial(name);
            var trigger = trial.Target(900, 103, 100);
            var second = trial.Target(901, 122, 100);
            var distant = trial.Target(902, 145, 100);
            Task? recast = null;
            await trial.Cast(0x8001, (180, 100), id =>
            {
                if (id == trigger.ObjectId && recast is null) recast = trial.Cast(0x8002, (100, 180));
            });
            Assert.NotNull(recast);
            await recast!;
            Assert.True(second.Life < second.MaxLife);
            Assert.Equal(continues, distant.Life < distant.MaxLife);
        }

        foreach (bool obstructed in new[] { false, true })
        foreach (var (name, redirects) in new[] { ("Sunder", false), ("Sunder of Trarthus", true) })
        {
            using var trial = new Trial(name, obstructed ? (x, _) => x != 108 : null);
            SpawnedMonster? trigger = obstructed ? null : trial.Target(900, 103, 100);
            var turnTarget = trial.Target(901, 107, 131);
            await trial.Cast(0x8001, (180, 100));
            if (trigger is not null) Assert.True(trigger.Life < trigger.MaxLife);
            Assert.Equal(redirects, turnTarget.Life < turnTarget.MaxLife);
        }
    }

    sealed class Trial : IDisposable
    {
        static readonly (int X, int Y) Origin = (100, 100);
        public readonly ViperStrikeMambaTests.AttackClock Clock = new();
        readonly TriggeredSummonTests.Fixture fixture;
        readonly IDisposable stats;
        readonly PlayerUpdatesState? previous;
        readonly ushort skill;

        public Player Player => fixture.Player;

        public Trial(string name, Func<int, int, bool>? walkable = null)
        {
            fixture = new(Clock);
            previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, Clock);
            Player.Use(new(1, "sunder-test", 400, 400, Origin.X, Origin.Y, walkable ?? ((_, _) => true), []) { Sight = (_, _) => 2 });
            Player.Walk.Teleport(Origin.X, Origin.Y, Clock.At);
            Player.ProjectileRuntimeState = fixture.Session;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(Clock.At));
            var items = Game.ItemsOf(Player);
            var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
            items.Change(PlayerInventories.Weapon1, host.Id, _ => ItemCatalog.Create(Program.Items.Find("Driftwood Club")!));
            skill = Assert.Single(fixture.Equip((name, 1)));
            Player.Pools = Player.Pools with { Accuracy = 10_000 };
            stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (ReferenceEquals(owner, Player))
                {
                    values[Stat.GlobalCannotCrit] = 1;
                    values[Stat.AttackMinimumAddedPhysicalDamage] = values[Stat.AttackMaximumAddedPhysicalDamage] = 10_000;
                }
                return values;
            });
        }

        public SpawnedMonster Target(uint id, int x, int y)
        {
            var monster = new SpawnedMonster("Sunder witness", new(x, y, 0), 0, 0, 1_000_000)
                { ObjectId = id, Hostile = true, NoRewards = true };
            Player.Area.Monsters[id] = monster;
            Player.Area.Grid.Set(new(id), (x, y));
            return monster;
        }

        public Task Cast(ushort sequence, (int X, int Y) aim, Action<uint>? onHit = null)
        {
            var gem = Game.GemSkillEffects(Player).Single(g => g.Id == skill);
            return Game.AreaSkillHit(Player, skill, gem, Origin, aim, sequence: sequence, onHit: onHit);
        }

        public void Dispose()
        {
            stats.Dispose();
            Game.AreaUpdateContext.Value = previous;
            fixture.Dispose();
        }
    }
}
