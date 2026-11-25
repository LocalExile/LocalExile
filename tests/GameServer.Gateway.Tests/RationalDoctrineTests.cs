using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class RationalDoctrineTests
{
    static int Stat(Item item, string id) =>
        item.Mods.Single(m => Program.Items.Mods.ByHash(m.Hash)!.Id == id).Values[0];

    [Fact]
    public void Rational_Doctrine_exposes_its_ground_mods_and_attribute_rules()
    {
        var jewel = Game.CreateUniqueJewel("Rational Doctrine");
        Assert.NotNull(jewel);
        Assert.Equal(18, Stat(jewel!, "ConsecratedGroundStationarySTRHighestUnique__1"));
        Assert.Equal(25, Stat(jewel!, "ProfaneGroundCriticalStrikeINTHighestUnique__1"));
        Assert.Equal(4000, Stat(jewel!, "ConsecratedGroundLingersUnique__1"));
        Assert.Equal(4000, Stat(jewel!, "ProfaneGroundLingersUnique__1"));

        var stats = new Dictionary<uint, int>
        {
            [global::Stat.Strength] = 100, [global::Stat.Dexterity] = 50, [global::Stat.Intelligence] = 99,
            [FlaskGrounds.ProfaneChanceStat] = 25,
        };
        Assert.Equal(0, FlaskGrounds.ProfaneChance(name => stats.GetValueOrDefault(name)));
        stats[global::Stat.Intelligence] = 100;
        Assert.Equal(25, FlaskGrounds.ProfaneChance(name => stats.GetValueOrDefault(name)));
        stats[global::Stat.Intelligence] = 101;
        Assert.Equal(25, FlaskGrounds.ProfaneChance(name => stats.GetValueOrDefault(name)));

        var player = new Player(new(1, "rational", 100, 100, 100, 100, null, []), 0, 1);
        player.FlaskGrounds.SetProfane(7, inside: true, alive: true, now: 1, () => 99);
        Assert.Equal(900, global::Game.CriticalChanceOn(player, 7, 500, null, new(900, null, null)));
        Assert.Equal(500, global::Game.CriticalChanceOn(player, 8, 500, null, new(900, null, null)));
    }

    [Fact]
    public async Task Profane_ground_applies_to_monsters_and_lingers_after_they_leave()
    {
        Assert.True(FlaskGrounds.LoadArt("data").ContainsKey(FlaskGrounds.Kind.ProfaneGround));
        Assert.Equal((ushort)1569, FlaskGrounds.ProfaneBuffRow);
        var made = FlaskGrounds.ProfaneGround((3, 4), now: 10);
        Assert.Equal(FlaskGrounds.Kind.ProfaneGround, made.Kind);
        Assert.Equal(10, made.Radius);
        Assert.Equal(14, made.Ends);
        var state = new FlaskGrounds.State();
        state.Configure(stationaryRadius: 0, lingerMs: 0, profaneLingerMs: 4000);
        const double now = 10;
        Assert.True(state.Add(new(FlaskGrounds.Kind.ProfaneGround, 10, 10, 10, now + 4)));

        var added = state.SetProfane(1, inside: true, alive: true, now, () => 42);
        Assert.NotNull(added);
        Assert.Equal((ushort)42, added!.Value.Buff);
        Assert.True(added.Value.Added);
        Assert.True(state.Profane(1));
        Assert.Null(state.SetProfane(1, inside: false, alive: true, now + 3.9, () => 43));
        Assert.True(state.Profane(1));
        var removed = state.SetProfane(1, inside: false, alive: true, now + 4, () => 43);
        Assert.NotNull(removed);
        Assert.Equal((ushort)42, removed!.Value.Buff);
        Assert.False(removed.Value.Added);
        Assert.False(state.Profane(1));

        var second = state.SetProfane(2, inside: true, alive: true, now, () => 44);
        Assert.NotNull(second);
        Assert.True(second!.Value.Added);
        var dead = state.SetProfane(2, inside: false, alive: false, now, () => 45);
        Assert.NotNull(dead);
        Assert.Equal((ushort)44, dead!.Value.Buff);
        Assert.False(dead.Value.Added);

        var consecrated = new FlaskGrounds.State();
        consecrated.Configure(stationaryRadius: 18, lingerMs: 4000);
        Assert.True(consecrated.Add(new(FlaskGrounds.Kind.ConsecratedGround, 10, 10, 18, 100)));
        Assert.True(consecrated.OnConsecrated(alive: true, (10, 10), now));
        Assert.True(consecrated.OnConsecrated(alive: true, (100, 100), now + 3.9));
        Assert.False(consecrated.OnConsecrated(alive: true, (100, 100), now + 4));

        var overlap = new FlaskGrounds.State();
        overlap.Add(new(FlaskGrounds.Kind.ConsecratedGround, 10, 10, 10, 100)
        {
            AdditionalLingerMs = 2000,
            GrantsCurseImmunity = true,
        });
        overlap.Add(new(FlaskGrounds.Kind.ConsecratedGround, 24, 10, 10, 100));
        bool CoveredAt(bool alive, (int X, int Y) at, double when) =>
            Game.OnConsecratedGround(overlap, alive, overlap.Grounds.Where(g => g.Covers(at)), when);
        Assert.True(CoveredAt(alive: true, (17, 10), now));
        Assert.True(CoveredAt(alive: true, (24, 10), now + 1));
        Assert.True(CoveredAt(alive: true, (100, 100), now + 2.5));
        Assert.True(overlap.CurseImmune);
        Assert.False(CoveredAt(alive: true, (100, 100), now + 3));
        Assert.True(CoveredAt(alive: true, (10, 10), now + 4));
        Assert.False(CoveredAt(alive: false, (100, 100), now + 5));
        Assert.False(CoveredAt(alive: true, (100, 100), now + 5.5));
        Assert.False(overlap.CurseImmune);

        await CheckpointResurrectionClearsConsecratedLinger();
    }

    static async Task CheckpointResurrectionClearsConsecratedLinger()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var clock = new ResurrectClock { At = 10 };
        var updates = new PlayerUpdatesState(fixture.Lifetime, clock);
        player.PlayerUpdatesState = updates;
        Assert.True(player.PlayerUpdatesWorker.TryStart(player, updates.Rules, null, updates, out updates.Generation));
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = updates;
        try
        {
            var instance = player.Instance;
            var checkpoint = player.Layout.Checkpoint(waypointActivated: false);
            var ground = new FlaskGrounds.Ground(FlaskGrounds.Kind.ConsecratedGround, checkpoint.X + 20, checkpoint.Y + 20,
                5, clock.At + 8) { AdditionalLingerMs = 2000, GrantsCurseImmunity = true };
            player.FlaskGrounds.Add(ground);
            player.Walk.Teleport(ground.X, ground.Y, clock.At);
            Game.AdvanceFlaskGrounds(player, updates);
            Assert.True(player.FlaskGrounds.Consecrated);
            Assert.True(player.FlaskGrounds.CurseImmune);

            clock.At += .25;
            player.Walk.Teleport(ground.X + 20, ground.Y, clock.At);
            Assert.False(ground.Covers(player.Walk.Position(clock.At)));
            Game.AdvanceFlaskGrounds(player, updates);
            Assert.True(player.FlaskGrounds.Consecrated);
            Assert.True(player.FlaskGrounds.CurseImmune);

            player.PlayerLife = 0;
            await Game.Resurrect(player, inTown: false);
            Assert.True(player.PlayerLife > 0);
            Assert.Same(instance, player.Instance);
            Assert.Equal(checkpoint, player.Walk.Position(clock.At));
            Assert.False(ground.Covers(checkpoint));
            Assert.True(clock.At < 12.25);
            Assert.Same(ground, Assert.Single(player.FlaskGrounds.Grounds));
            Console.WriteLine($"Checkpoint resurrect: consecrated {player.FlaskGrounds.Consecrated}, curse immune {player.FlaskGrounds.CurseImmune}");
            Assert.False(player.FlaskGrounds.Consecrated);
            Assert.False(player.FlaskGrounds.CurseImmune);
            Assert.Empty(player.FlaskGrounds.PlayerStats());
            Assert.False(player.Area.Buffs.Has(player.Id, FlaskGrounds.ConsecratedBuffRow, player.Id));
        }
        finally
        {
            Game.AreaUpdateContext.Value = previous;
            player.PlayerUpdatesWorker.Complete(updates.Rules, null, updates, updates.Generation);
            player.RetirePlayerUpdates();
        }
    }

    sealed class ResurrectClock : IPlayerUpdateSchedule
    {
        public double At;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }
}
