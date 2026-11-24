using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>
/// A flask use grants Alchemist's Genius when the character rolls gain_alchemists_genius_on_flask_use_%: the flask
/// effect running at that moment takes the buff's increased effect, and past the buff's deadline it falls back.
/// The clock is the test's own; nothing waits on real time.
/// </summary>
public sealed class AlchemistsGeniusTests
{
    [Fact]
    public async Task A_flask_use_grants_the_buff_which_raises_the_running_flask_until_its_deadline()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var clock = new CastClock { At = 10 };
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        using var statSub = GameEvents.PlayerStatSources.Add("Game", (p, s) =>
            !ReferenceEquals(p, player) ? s
            : new Dictionary<uint, int>(s) { [Stat.GainAlchemistsGeniusOnFlaskUsePct] = 100 });
        try
        {
            var items = Game.ItemsOf(player);
            foreach (var existing in items.Items(PlayerInventories.Flask).ToArray())
                items.Remove(PlayerInventories.Flask, existing.Id);
            var type = Program.Items.Find("Granite Flask")!;
            var utility = Program.Items.UtilityFlaskOf(type)!;
            items.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(type) with { Charges = utility.MaxCharges });
            Game.RecomputePoolsAndSpeed(player);

            int Running() => player.UtilityFlaskEffects.All().Single().Stats.Sum(s => s.Value);

            await Game.UseFlask(player, 0);
            Assert.True(player.AlchemistsGenius.Active, "the use rolled 100% and should have granted the buff");
            int boosted = Running();

            clock.At = 10 + 5;
            Assert.True(Game.EndAlchemistsGeniusIfDue(player, clock.At, dead: false));
            Assert.False(player.AlchemistsGenius.Active);
            Assert.True(boosted > Running(), $"the running flask did not fall back: {boosted} -> {Running()}");
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    sealed class CastClock : IPlayerUpdateSchedule
    {
        public double At = 1;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }
}
