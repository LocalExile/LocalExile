using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public sealed class PestilentStrikeTests
{
    [Fact]
    public async Task Pestilent_strike_spreads_the_remaining_poison_only_from_a_marked_death()
    {
        await CheckSpread(secondPoison: false);
        await CheckSpread(secondPoison: true);
        await CheckSpread(secondPoison: true, expired: true);
        await CheckSpread(secondPoison: true, marked: false);
        await CheckSpread(secondPoison: true, resistant: true);
    }

    [Fact]
    public Task A_lethal_poison_tick_uses_the_stacks_remaining_at_the_lethal_contact() =>
        CheckSpread(secondPoison: false, poisonDeath: true);

    static async Task CheckSpread(bool secondPoison, bool expired = false, bool marked = true,
        bool poisonDeath = false, bool resistant = false)
    {
        var clock = new Clock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.Use(new(1, "pestilent-strike-test", 400, 400, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
        player.Walk.Teleport(100, 100, clock.At);
        player.Connection.LockstepClient = false;
        player.Connection.PlayerObjectCreated = true;
        player.ProjectileRuntimeState = fixture.Session;
        fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
        var items = Game.ItemsOf(player);
        var dagger = ItemCatalog.Create(Program.Items.Find("Glass Shank")!);
        var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
        items.Change(PlayerInventories.Weapon1, host.Id, _ => dagger);
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (!ReferenceEquals(owner, player)) return values;
            values[Stat.GlobalAlwaysHit] = 1;
            values[Stat.GlobalCannotCrit] = 1;
            values[Stat.NeverDamagingAilments] = 1;
            values[Stat.ChaosDamagePlusPct] = 500;
            return values;
        });

        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            ushort skill = Assert.Single(fixture.Equip(("Pestilent Strike", 1)));
            var primary = Target(901, 108);
            var witness = Target(902, 134);
            var outside = Target(903, 136);
            if (resistant) witness.EncounterStats = new Dictionary<uint, int> { [Stat.BaseChaosDamageResistancePct] = 25 };
            if (marked)
            {
                Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (108, 100), 901));
                for (int i = 0; i < 20 && primary.Life == primary.MaxLife; i++) Advance(.05);
                Assert.True(primary.Life < primary.MaxLife);
            }
            if (expired) Advance(1.25);
            Assert.Equal(witness.MaxLife, witness.Life);
            double death = clock.At;
            if (poisonDeath)
            {
                primary.Life = 50;
                Assert.True(primary.Poisons.Add(death, 100, 2));
                player.Area.ActivePoisonMonsters.Add(901);
                Advance(.75);
                await Game.TickPoisons(player);
            }
            else
            {
                Assert.True(primary.Poisons.Add(death - 2, 500, 1));
                Assert.True(primary.Poisons.Add(death - .5, 100, 2.5));
                if (secondPoison) Assert.True(primary.Poisons.Add(death - .25, 120, 1.25));
                var hit = Game.ApplyMonsterHitLocked(player, primary, int.MaxValue);
                Assert.Equal(0u, hit?.Life);
                await Game.MonsterKilled(player, 901, primary);
            }
            Assert.Equal(0u, primary.Life);
            Assert.Equal(witness.MaxLife, witness.Life);
            Advance(1.25);
            await Game.TickContagions(player);
            uint expected = expired || !marked ? 0u : poisonDeath ? 75u : secondPoison ? 160u : 100u;
            if (resistant) expected = expected * 3 / 4;
            Assert.Equal(expected, witness.MaxLife - witness.Life);
            Assert.Equal(expected > 0, primary.CorpseConsumed);
            Assert.Equal(0, witness.Poisons.Stacks(clock.At));
            Assert.Equal(outside.MaxLife, outside.Life);
            Advance(1.25);
            await Game.TickContagions(player);
            Assert.Equal(expected, witness.MaxLife - witness.Life);
        }
        finally { Game.AreaUpdateContext.Value = previous; }

        SpawnedMonster Target(uint id, int x)
        {
            var target = new SpawnedMonster("pestilent strike witness", new(x, 100, 0), 0, 0, 10_000)
                { ObjectId = id, Hostile = true, NoRewards = true };
            player.Area.Monsters[id] = target;
            player.Area.Grid.Set(new(id), (x, 100));
            return target;
        }

        void Advance(double seconds)
        {
            clock.At += seconds;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
        }
    }

    sealed class Clock : IPlayerUpdateSchedule
    {
        public double At = 3;
        public double Now(Player player) => At;
        public async Task Wait(int milliseconds, CancellationToken stopping)
        {
            await Task.Yield();
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
        }
    }
}
