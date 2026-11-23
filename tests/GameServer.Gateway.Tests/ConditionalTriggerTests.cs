using GameServer.Core;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class ConditionalTriggerTests
{
    [Fact]
    public async Task Manaforged_fires_frenzy_only_after_actual_bow_spending_exceeds_the_linked_cost()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        var clock = new Clock();
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(f.Lifetime, clock);
        player.ProjectileRuntimeState = f.Session;
        try
        {
            f.Equip(("Frenzy", 1), ("Manaforged Arrows Support", 1), ("Greater Multiple Projectiles Support", 1),
                ("Added Cold Damage Support", 1), ("Faster Attacks Support", 1));
            var items = Game.ItemsOf(player);
            var prior = Assert.Single(items.Items(PlayerInventories.Weapon1));
            var bow = ItemCatalog.Create(Program.Items.Find("Crude Bow")!, random: new Random(11));
            items.Change(PlayerInventories.Weapon1, prior.Id, item => bow with
            {
                ItemLevel = 80,
                Sockets = item.Sockets,
                Links = item.Links,
            });
            var splitArrow = Program.Items.FindGem("Split Arrow")!.Value;
            var robe = ItemCatalog.Create(Program.Items.Find("Simple Robe")!, random: new Random(11)) with
            {
                Sockets = [new(SocketColour.White, ItemCatalog.Create(splitArrow.Type) with { GemVariant = splitArrow.Variant.Index })],
                Links = [1],
            };
            items.Add(PlayerInventories.BodyArmour, 0, 0, robe);
            player.Pools = Game.BuildPoolsFor(player, 100);
            player.PlayerMana = (uint)Game.UnreservedMana(player);
            var driver = Game.GemSkillEffects(player).Single(g => g.Name == "Split Arrow");
            var payload = Game.GemSkillEffects(player).Single(g => g.Name == "Frenzy");
            int driverCost = Game.SupportedStats(player, driver.Id).GetValueOrDefault(Stat.ManaCost);
            int payloadCost = Game.SupportedStats(player, payload.Id).GetValueOrDefault(Stat.ManaCost);
            int threshold = Game.SupportedSkill(player, payload.Id).Stats.GetValueOrDefault(Stat.SupportManaforgedArrowsManaCostPctThreshold);
            Assert.Equal(300, threshold);
            Assert.True(driverCost > 0 && payloadCost > 0);
            int uses = payloadCost * threshold / (100 * driverCost) + 1;
            Assert.InRange(uses, 2, 50);
            var from = player.Walk.Position(clock.At);
            var aim = (from.X + 60, from.Y);

            Assert.False(await Game.CastGemSkill(player, payload.Id, from, aim));
            for (int i = 1; i <= uses; i++)
            {
                player.NextAttackAt = 0;
                uint before = player.PlayerMana;
                Assert.True(await Game.CastGemSkill(player, driver.Id, from, aim));
                f.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                bool released = f.Session.Runtime.Projectiles.Any(arrow => arrow.Use.Payload is ProjectileOffense p && p.SourceSkill == payload.Id);
                Assert.Equal(i == uses, released);
                uint spent = before - player.PlayerMana;
                Assert.True(spent == (uint)(driverCost + (i == uses ? payloadCost : 0)),
                    $"Driver={driverCost}; payload={payloadCost}; attempt={i}/{uses}; mana spent={spent}.");
            }
        }
        finally
        {
            Game.AreaUpdateContext.Value = previous;
        }
    }

    [Fact]
    public async Task Cast_when_Stunned_uses_the_native_chance_and_releases_the_linked_golem_once()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        ushort skill = Assert.Single(f.Equip(("Summon Stone Golem", 1), ("Cast when Stunned Support", 1)));
        player.ProjectileRuntimeState = f.Session;
        player.Pools = Game.BuildPoolsFor(player, 100);
        player.PlayerMana = (uint)Game.UnreservedMana(player);
        Assert.Equal(0u, player.Pools.EnergyShield);
        int cost = Game.SupportedStats(player, skill).GetValueOrDefault(Stat.ManaCost);
        Assert.True(cost > 0);
        uint mana = player.PlayerMana;

        Game.RecordDamageTakenTriggers(player, (int)player.PlayerMaxLife, 0);
        Game.ReleaseDamageTakenTriggers(player, f.Session);
        Assert.Equal(mana, player.PlayerMana);
        player.IncomingHitRandom = new Chance(99);
        Assert.NotNull(Game.CommitPlayerHitRecoveryLocked(player, (int)player.PlayerMaxLife, 1));
        Game.ReleaseDamageTakenTriggers(player, f.Session);
        Assert.Equal(mana, player.PlayerMana);
        Assert.Empty(player.Golems.All);

        player.IncomingHitRandom = new Chance(0);
        Assert.NotNull(Game.CommitPlayerHitRecoveryLocked(player, (int)player.PlayerMaxLife, 2));
        Game.ReleaseDamageTakenTriggers(player, f.Session);
        Assert.True(player.PlayerMana == mana - (uint)cost, $"Expected one cost of {cost}; paid {mana - player.PlayerMana}.");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Session.Enqueue(() => { done.SetResult(); return Task.CompletedTask; });
        await done.Task;
        Assert.Equal(skill, Assert.Single(player.Golems.All.Values).Source);
        Game.ReleaseDamageTakenTriggers(player, f.Session);
        Assert.Equal(mana - (uint)cost, player.PlayerMana);
    }

    sealed class Chance(int roll) : Random
    {
        public override int Next(int minValue, int maxValue) => Math.Clamp(roll, minValue, maxValue - 1);
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
