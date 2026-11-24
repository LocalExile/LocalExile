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
            // Slot 0: Magic Jade Flask permanently applied by Mageblood (leftmost 1); Slot 1: Normal Granite Flask usable directly.
            var jadeType = Program.Items.Find("Jade Flask")!;
            var jadeUtility = Program.Items.UtilityFlaskOf(jadeType)!;
            items.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(jadeType) with { Rarity = ItemRarity.Magic, Charges = jadeUtility.MaxCharges });
            var graniteType = Program.Items.Find("Granite Flask")!;
            var graniteUtility = Program.Items.UtilityFlaskOf(graniteType)!;
            items.Add(PlayerInventories.Flask, 1, 0, ItemCatalog.Create(graniteType) with { Charges = graniteUtility.MaxCharges });
            var mageblood = new UniqueCatalog(Program.Items).Create("Mageblood", new Random(5))!;
            var leftmost = Program.Items.Mods.ById("MagicUtilityFlasksAlwaysApplyUnique__1")!;
            var belt = mageblood with { Mods = [.. mageblood.Mods.Select(m => m.Hash == leftmost.Hash ? leftmost.With(m.Section, [1], Random.Shared) : m)] };
            foreach (var existing in items.Items(PlayerInventories.Belt).ToArray())
                items.Remove(PlayerInventories.Belt, existing.Id);
            items.Add(PlayerInventories.Belt, 0, 0, belt);
            Game.RecomputePoolsAndSpeed(player);

            int Running() => player.UtilityFlaskEffects.All().Single().Stats.Sum(s => s.Value);
            int ChargesGained() => Game.CharacterPlayerStats(player).GetValueOrDefault(Stat.ChargesGainedPlusPct);
            int MagebloodJade() => Game.MagebloodSources(player, key => Game.CharacterStatSources(player).Stats.GetValueOrDefault(key))
                .Where(s => s.Effect == $"buff {jadeUtility.BuffRow}").Max(s => s.Value);

            int baseChargesGained = ChargesGained();
            int baseMageblood = MagebloodJade();
            int basePoolEvasion = player.Pools.Evasion;

            await Game.UseFlask(player, 1);
            Assert.True(player.AlchemistsGenius.Active, "the use rolled 100% and should have granted the buff");
            int boosted = Running();
            Assert.Equal(baseChargesGained + 20, ChargesGained());
            Assert.True(MagebloodJade() > baseMageblood, $"Mageblood permanent flask did not gain Alchemist's Genius: {baseMageblood} -> {MagebloodJade()}");
            Assert.True(player.Pools.Evasion > basePoolEvasion, $"Pools.Evasion did not reflect Mageblood + Alchemist's Genius: {basePoolEvasion} -> {player.Pools.Evasion}");

            clock.At = 10 + 5;
            Assert.True(Game.EndAlchemistsGeniusIfDue(player, clock.At, dead: false));
            Assert.False(player.AlchemistsGenius.Active);
            Assert.True(boosted > Running(), $"the running flask did not fall back: {boosted} -> {Running()}");
            Assert.Equal(baseChargesGained, ChargesGained());
            Assert.Equal(baseMageblood, MagebloodJade());
            Assert.Equal(basePoolEvasion, player.Pools.Evasion);
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
