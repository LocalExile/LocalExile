using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public sealed class InfernalBlowTests
{
    [Fact]
    public async Task Infernal_Blow_detonates_an_expired_charge_around_the_enemy()
    {
        await CheckCharges("Infernal Blow", (130, 100), reachCap: false);
        await CheckCharges("Infernal Blow", (130, 100), reachCap: true);
        await CheckCharges("Infernal Blow", (108, 130), reachCap: false, lethal: true);
        await CheckCharges("Infernal Blow", (108, 127), reachCap: false, lethal: true, splash: true);
    }

    [Fact]
    public async Task Infernal_Blow_of_Immolation_detonates_an_expired_charge_around_the_attacker()
    {
        await CheckCharges("Infernal Blow of Immolation", (77, 102), reachCap: false);
        await CheckCharges("Infernal Blow of Immolation", (77, 102), reachCap: true);
    }

    static async Task CheckCharges(string name, (int X, int Y) witness, bool reachCap, bool lethal = false, bool splash = false)
    {
        var clock = new Clock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.Use(new(1, "infernal-blow-test", 400, 400, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
        player.Walk.Teleport(100, 100, clock.At);
        player.Connection.LockstepClient = false;
        player.Connection.PlayerSent = player.Connection.PlayerObjectCreated = true;
        player.ProjectileRuntimeState = fixture.Session;
        fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (!ReferenceEquals(owner, player)) return values;
            values[Stat.GlobalAlwaysHit] = 1;
            values[Stat.GlobalCannotCrit] = 1;
            values[Stat.AttackMinimumAddedPhysicalDamage] = values[Stat.AttackMaximumAddedPhysicalDamage] = 1600;
            return values;
        });

        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            ushort skill = Assert.Single(splash ? fixture.Equip((name, 1), ("Melee Splash Support", 1)) : fixture.Equip((name, 1)));
            var primary = Target(901, 108, 100, lethal && !splash ? 1_000u : 100_000u);
            var close = Target(902, witness.X, witness.Y);
            var chain = lethal ? Target(904, 108, splash ? 112 : 116, 1_000, splash ? 800u : null) : null;
            var outside = Target(903, 108, 144);
            int strikes = reachCap ? 6 : 1;
            for (int i = 0; i < strikes; i++)
            {
                Advance(Math.Max(0, player.NextAttackAt - clock.At) + .01);
                uint previousLife = primary.Life;
                Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (108, 100), 901));
                for (int step = 0; step < 20 && primary.Life == previousLife; step++) Advance(.05);
                Assert.True(primary.Life < previousLife, "The strike should land through the ordinary skill path.");
                if (!lethal && (!reachCap || i < strikes - 1)) Assert.Equal(close.MaxLife, close.Life);
            }
            Advance(reachCap || lethal ? .01 : 4);
            if (lethal)
            {
                Assert.NotNull(chain);
                Assert.Equal(!splash, primary.CorpseConsumed);
                Assert.True(chain.CorpseConsumed);
                Assert.Equal(splash ? 48u : 60u, close.MaxLife - close.Life);
            }
            Assert.True(close.Life < close.MaxLife, "Expiry or the sixth charge should hit the witness.");
            if (reachCap) Assert.True(close.MaxLife - close.Life > 8_000, "A full stack should deal six charges.");
            Assert.Equal(outside.MaxLife, outside.Life);
            uint life = close.Life;
            Advance(4);
            Assert.Equal(life, close.Life);
        }
        finally { Game.AreaUpdateContext.Value = previous; }

        SpawnedMonster Target(uint id, int x, int y, uint life = 100_000, uint? soloLife = null)
        {
            var monster = new SpawnedMonster("infernal blow witness", new(x, y, 0), 0, 0, life)
                { ObjectId = id, Hostile = true, NoRewards = true, MaxLifeWithoutParty = soloLife };
            player.Area.Monsters[id] = monster;
            player.Area.Grid.Set(new(id), (x, y));
            return monster;
        }

        void Advance(double seconds)
        {
            clock.At += seconds;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
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
