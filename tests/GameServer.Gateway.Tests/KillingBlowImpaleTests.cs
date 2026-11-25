using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public sealed class KillingBlowImpaleTests
{
    [Fact]
    public async Task Either_Impaling_contact_can_reflect_damage_from_its_kill()
    {
        int first = await Strike("Double Strike of Impaling", life: 40, expectedContacts: 1);
        int second = await Strike("Double Strike of Impaling", life: 75, expectedContacts: 2);
        int ordinary = await Strike("Double Strike", life: 40, expectedContacts: 1);
        Assert.Equal(40, first);
        Assert.Equal(40, second);
        Assert.Equal(0, ordinary);
    }

    static async Task<int> Strike(string gem, uint life, int expectedContacts)
    {
        using var scene = new Encounter(gem);
        var primary = scene.Spawn(901, 108, life);
        primary.EncounterStats = new Dictionary<uint, int> { [Stat.AvoidImpalePct] = 100 };
        Assert.True(primary.Impale.Apply(400, scene.Clock.At));
        var nearby = scene.Spawn(902, 131);
        Assert.True(nearby.Impale.Apply(300, scene.Clock.At));
        var outside = scene.Spawn(903, 132);
        var lifeSamples = new List<uint>();
        scene.Clock.BeforeAdvance = () => lifeSamples.Add(primary.Life);

        Assert.True(await Game.CastGemSkill(scene.Player, scene.Skill, (100, 100), (108, 100), targetId: 901));

        Assert.Equal(0u, primary.Life);
        Assert.Equal(expectedContacts, lifeSamples.Where(value => value > 0).Distinct().Count());
        Assert.Equal(outside.MaxLife, outside.Life);
        Assert.Equal(1, primary.Impale.Stacks);
        Assert.Equal(1, nearby.Impale.Stacks);
        return (int)(nearby.MaxLife - nearby.Life);
    }

    [Fact]
    public async Task Skewers_impales_with_the_killing_secondary_hit_and_reflects_its_damage()
    {
        using var scene = new Encounter("Bear Trap of Skewers");
        var nearby = scene.Spawn(902, 131);
        Assert.True(await Game.CastGemSkill(scene.Player, scene.Skill, (100, 100), (108, 100)));
        var trap = Assert.Single(scene.Player.Placed.All.Values);
        var primary = scene.Spawn(901, trap.At.X, life: 1, y: trap.At.Y);

        scene.Clock.At = trap.Armed + .01;
        scene.Session.AdvanceTo(ProjectileRuntime.Time(scene.Clock.At));
        await Game.PlayerMinionsTick(scene.Player, scene.Clock.At);

        Assert.Empty(scene.Player.Placed.All);
        Assert.Equal(0u, primary.Life);
        Assert.Equal(1, primary.Impale.Stacks);
        Assert.InRange(primary.Impale.Bonus, 1.6, 2.2);
        uint reflected = (uint)Game.MonsterImpaleDamage(nearby, primary.Impale.Bonus, scene.Clock.At);
        Assert.InRange(reflected, 1u, 2u);
        Assert.Equal(reflected, nearby.MaxLife - nearby.Life);
        Assert.Equal(0, nearby.Impale.Stacks);
    }

    sealed class Encounter : IDisposable
    {
        internal readonly Clock Clock = new();
        readonly TriggeredSummonTests.Fixture fixture;
        readonly PlayerUpdatesState? previous;
        readonly IDisposable sources;
        internal readonly ushort Skill;
        internal Player Player => fixture.Player;
        internal Game.ProjectileSession Session => fixture.Session;

        internal Encounter(string gem)
        {
            fixture = new(Clock);
            Player.Use(new(1, "killing-impale-test", 400, 400, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
            Player.Walk.Teleport(100, 100, Clock.At);
            Player.Connection.LockstepClient = false;
            Player.Connection.PlayerObjectCreated = true;
            Player.ProjectileRuntimeState = fixture.Session;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(Clock.At));
            sources = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (!ReferenceEquals(owner, Player)) return values;
                values[Stat.GlobalAlwaysHit] = 1;
                values[Stat.GlobalCannotCrit] = 1;
                values[Stat.ImpaleEffectPlusPctForImpalesInfictedBySpells] = 10000;
                return values;
            });
            Skill = Assert.Single(fixture.Equip((gem, 1)));
            previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, Clock);
        }

        internal SpawnedMonster Spawn(uint id, int x, uint life = 1000, int y = 100)
        {
            var monster = new SpawnedMonster("impale witness", new(x, y, 0), 0, 0, 1000)
                { ObjectId = id, Hostile = true, NoRewards = true, Life = life };
            Player.Area.Monsters[id] = monster;
            Player.Area.Grid.Set(new(id), (x, y));
            return monster;
        }

        public void Dispose()
        {
            Game.AreaUpdateContext.Value = previous;
            sources.Dispose();
            fixture.Dispose();
        }
    }

    sealed class Clock : IPlayerUpdateSchedule
    {
        public double At = 3;
        public Action? BeforeAdvance;
        public double Now(Player player) => At;
        public async Task Wait(int milliseconds, CancellationToken stopping)
        {
            BeforeAdvance?.Invoke();
            await Task.Yield();
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
        }
    }
}
