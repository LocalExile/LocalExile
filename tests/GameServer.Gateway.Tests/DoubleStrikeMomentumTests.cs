using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class DoubleStrikeMomentumTests
{
    [Fact]
    public async Task Momentum_counts_pairs_even_without_hits_and_movement_clears_the_stages()
    {
        using var swing = new SwingFixture(multistrike: false);
        var player = swing.Player;
        double baseline = Game.SupportedAttackSeconds(player, swing.Skill);
        double ordinary = Game.SupportedAttackSeconds(player, swing.Ordinary);
        await swing.Use();
        Assert.Equal(baseline / 1.1, Game.SupportedAttackSeconds(player, swing.Skill), 3);
        Assert.True(player.Area.Buffs.Has(player.Id, 2325, player.Id));
        Assert.Equal(baseline / 1.1, Game.SupportedAttackSeconds(player, swing.SecondCopy), 3);
        Assert.Equal(ordinary, Game.SupportedAttackSeconds(player, swing.Ordinary), 3);
        for (int pair = 0; pair < 11; pair++) await swing.Use();
        Assert.Equal(baseline / 2, Game.SupportedAttackSeconds(player, swing.Skill), 3);
        player.Walk.Teleport(101, 100, swing.Clock.At);
        Assert.Equal(baseline, Game.SupportedAttackSeconds(player, swing.Skill), 3);
        Assert.False(player.Area.Buffs.Has(player.Id, 2325, player.Id));
        await swing.Use();
        Assert.Equal(baseline / 1.1, Game.SupportedAttackSeconds(player, swing.Skill), 3);
    }

    [Fact]
    public async Task Multistrike_counts_one_stage_per_started_pair_and_a_refused_use_gives_none()
    {
        using var swing = new SwingFixture(multistrike: true);
        var player = swing.Player;
        double baseline = Game.SupportedAttackSeconds(player, swing.Skill);
        uint mana = player.PlayerMana;
        player.PlayerMana = 0;
        Assert.False(await swing.Cast());
        Assert.Equal(baseline, Game.SupportedAttackSeconds(player, swing.Skill), 3);
        player.PlayerMana = mana;
        swing.Clock.BeforeWait = () =>
        {
            swing.Clock.BeforeWait = null;
            double at = swing.Clock.At;
            Game.CancelMeleeRepeats(player, at);
            Assert.Equal(baseline / 1.1, player.NextAttackAt - at, 2);
            Interlocked.Increment(ref player.ActionCount);
        };
        await swing.Use();
        Assert.Equal(baseline / 1.1, Game.SupportedAttackSeconds(player, swing.Skill), 2);
        player.Walk.Teleport(101, 100, swing.Clock.At);
        await swing.Use();
        Assert.Equal(baseline / 1.3, Game.SupportedAttackSeconds(player, swing.Skill), 2);
    }

    sealed class SwingFixture : IDisposable
    {
        internal readonly Clock Clock = new();
        readonly TriggeredSummonTests.Fixture fixture;
        readonly PlayerUpdatesState? previous;
        internal readonly ushort Skill, Ordinary, SecondCopy;
        internal Player Player => fixture.Player;

        internal SwingFixture(bool multistrike)
        {
            fixture = new(Clock);
            Player.Use(new(1, "double-strike-momentum-test", 400, 400, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
            Player.Walk.Teleport(100, 100, Clock.At);
            Player.Connection.LockstepClient = false;
            Player.Connection.PlayerObjectCreated = true;
            Player.ProjectileRuntimeState = fixture.Session;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(Clock.At));
            if (multistrike)
                Skill = Assert.Single(fixture.Equip(("Double Strike of Momentum", 1), ("Multistrike Support", 1)));
            else
            {
                fixture.Equip(("Double Strike of Momentum", 1), ("Double Strike of Momentum", 1), ("Double Strike", 1));
                var gems = Game.GemSkillEffects(Player).ToArray();
                var momentum = gems.Where(gem => gem.Name == "Double Strike of Momentum").ToArray();
                Assert.Equal(2, momentum.Length);
                Skill = momentum[0].Id;
                SecondCopy = momentum[1].Id;
                Ordinary = Assert.Single(gems, gem => gem.Name == "Double Strike").Id;
            }
            previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, Clock);
        }

        internal Task<bool> Cast()
        {
            Clock.At = Math.Max(Clock.At + .02, Player.NextAttackAt + .01);
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(Clock.At));
            var at = Player.Walk.Position(Clock.At);
            return Game.CastGemSkill(Player, Skill, at, (at.X + 6, at.Y), sequence: Player.NextActionSequence(),
                actionToken: Interlocked.Increment(ref Player.ActionCount));
        }

        internal async Task Use() => Assert.True(await Cast());

        public void Dispose()
        {
            Game.AreaUpdateContext.Value = previous;
            fixture.Dispose();
        }
    }

    sealed class Clock : IPlayerUpdateSchedule
    {
        internal double At = 3;
        internal Action? BeforeWait;
        public double Now(Player player) => At;
        public async Task Wait(int milliseconds, CancellationToken stopping)
        {
            BeforeWait?.Invoke();
            await Task.Yield();
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
        }
    }
}
