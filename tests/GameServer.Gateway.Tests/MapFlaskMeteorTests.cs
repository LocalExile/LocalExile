using System.Text.Json;
using System.Threading.Channels;
using GameServer.Core;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using Xunit.Abstractions;

namespace GameServer.Gateway.Tests;

public sealed class MapFlaskMeteorTests(ITestOutputHelper output)
{
    const string AreaId = "MapWorldsAbomination";
    const int NativeContactMilliseconds = 1821;

    [Fact]
    public async Task Paid_flasks_schedule_the_warned_map_meteor_and_failed_uses_do_not()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var samples = new[]
        {
            await Use(fixture, "clean-map", modded: false),
            await Use(fixture, "manual", modded: true),
            await Use(fixture, "automatic", modded: true, automatic: true),
            await Use(fixture, "recovery", modded: true, recovery: true),
            await Use(fixture, "undercharged", modded: true, charges: false),
            await Use(fixture, "automatic-undercharged", modded: true, automatic: true, charges: false),
            await Use(fixture, "recovery-undercharged", modded: true, recovery: true, charges: false),
            await Use(fixture, "recovery-full", modded: true, recovery: true, fullLife: true),
            await Use(fixture, "edge-hit", modded: true, move: 15),
            await Use(fixture, "dodge", modded: true, move: 16),
        };

        var incorrect = new List<string>();
        foreach (var sample in samples)
        {
            bool paid = !sample.Name.EndsWith("undercharged", StringComparison.Ordinal) && sample.Name != "recovery-full";
            bool meteor = paid && sample.Modded;
            bool shouldHit = meteor && sample.Name != "dodge";
            bool valid = sample.Cost > 0 && sample.Effect == paid
                && sample.AfterCharges == sample.BeforeCharges - (paid ? sample.Cost : 0)
                && sample.Warnings == (meteor ? 1 : 0)
                && sample.ContactMilliseconds == (meteor ? NativeContactMilliseconds : 0)
                && sample.WarnedAtOriginalCell == meteor && sample.NoEarlyDamage
                && (sample.LifeLostAtImpact > 0) == shouldHit && sample.CleanupScheduled == meteor;
            output.WriteLine("FLASK_METEOR " + JsonSerializer.Serialize(new { sample, valid }));
            if (!valid) incorrect.Add(sample.Name);
        }
        Assert.True(incorrect.Count == 0, "Map flask meteor behavior differs at " + string.Join(", ", incorrect));
    }

    static async Task<Sample> Use(TriggeredSummonTests.Fixture fixture, string name, bool modded,
        bool automatic = false, bool recovery = false, bool charges = true, bool fullLife = false, int move = 0)
    {
        using var wire = new SpellTotemExecutionTests.Wire();
        const int startX = 100, startY = 120;
        ushort hash = AreaTransitionCatalog.HashOf(AreaId)!.Value;
        var area = new MapDevice.MapArea(hash, AreaId, "Abomination", 17) { Level = 84 };
        var layout = new AreaLayout(hash, AreaId, 512, 512, startX, startY,
            (x, y) => x >= 0 && x < 512 && y >= 0 && y < 512, []);
        var player = new Player(layout, 1, 144842)
        {
            Character = { Name = "FlaskMeteor" + Guid.NewGuid().ToString("N"), Experience = CharacterStats.ExperienceFor(100) },
            Instance = new(),
            Connection = { PlayerSent = true, Session = new("flask meteor", wire.Stream, wire.Ciphers) },
            LeagueModule = new(new League("flask-meteor", "Standard", false, false), new HashSet<string>(["Game"])),
            Pools = new(100, 10000, 10000, 0, 0),
            PlayerLife = fullLife ? 10000u : 5000u,
            PlayerMana = 10000,
        };
        var catalog = Program.Items;
        var mod = catalog.Mods.ById("MapUberModFlaskMeteor")!;
        int index = mod.Stats.ToList().FindIndex(s => s.Id == Stat.MapPlayerCreateEnemyMeteorDaemonOnFlaskUsePctChance);
        Assert.True(index >= 0);
        Assert.Equal(100, mod.RangeAt(index).Min);
        var modifiers = new Dictionary<uint, int>();
        if (modded) modifiers[Stat.MapPlayerCreateEnemyMeteorDaemonOnFlaskUsePctChance] = mod.RangeAt(index).Min;
        var mapMods = new MapMods(modifiers) { MapRarity = ItemRarity.Rare };
        player.Instance.OpenedWith(new MapDevice.Run(player.Character.Name!, area, mapMods));
        var instanceStats = Game.ComposeMapAreaStats(mapMods, area.Tier, new Dictionary<uint, int>(), new HashSet<string>(), 0);
        player.Instance.AreaStats(() => instanceStats);

        var type = catalog.Find(recovery ? "Small Life Flask" : "Granite Flask")!;
        var chargeRules = catalog.FlaskChargesOf(type)!;
        var item = ItemCatalog.Create(type, random: new Random(11)) with { Charges = charges ? chargeRules.MaxCharges : 0, Identified = true };
        if (automatic)
            item = item with { Mods = [catalog.Mods.ById("FlaskEnchantmentInjectorOnFullCharges__")!
                .Roll(ModSection.Enchant, new Random(2205))] };
        var inventory = Game.ItemsOf(player);
        const byte slot = 2; // The starter kit occupies 0, 1 and 4.
        Assert.DoesNotContain(inventory.Items(PlayerInventories.Flask), i => i.X == slot);
        var flask = inventory.Add(PlayerInventories.Flask, slot, 0, item);
        player.Pools = Game.PoolsFor(player, 100);
        player.PlayerLife = fullLife ? player.PlayerMaxLife : player.PlayerMaxLife / 2;
        player.PlayerMana = player.Pools.Mana;
        var clock = new HazardClock();
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            uint warningId = player.NextObjectId;
            uint startingLife = player.PlayerLife;
            int held = Game.ItemsOf(player).Items(PlayerInventories.Flask).Single(i => i.Id == flask.Id).Item.Charges;
            if (automatic)
                await Game.DrainFlaskTriggers(player);
            else
                await Game.UseFlask(player, slot);
            int charged = Game.ItemsOf(player).Items(PlayerInventories.Flask).Single(i => i.Id == flask.Id).Item.Charges;
            bool effect = recovery ? player.FlaskEffects.Any : player.UtilityFlaskEffects.Of(flask.Id) is not null;
            int warnings = (int)(player.NextObjectId - warningId);
            uint life = player.PlayerLife;
            if (!clock.TryTake(out var contact) && warnings > 0)
                contact = await clock.Take().WaitAsync(TimeSpan.FromSeconds(20));
            if (contact is null)
                return new(name, modded, effect, chargeRules.ChargesPerUse, held, charged, warnings,
                    0, false, startingLife == life, 0, false, DateTimeOffset.UtcNow);

            if (move > 0) player.Walk.Teleport(startX + move, startY, clock.Now(player));
            bool noEarlyDamage = player.PlayerLife == startingLife;
            clock.Release(contact);
            // A second wait retains the impact animation after the hit. The fake clock drives both stages.
            var cleanup = await clock.Take().WaitAsync(TimeSpan.FromSeconds(20));
            var history = Program.HistoryFor(wire.Ciphers).Lines();
            bool warned = history.Any(line => line.Contains($"02BB{warningId:X8}", StringComparison.Ordinal)
                && line.Contains("6400000078000000", StringComparison.Ordinal));
            int damage = (int)(life - player.PlayerLife);
            clock.Release(cleanup);
            return new(name, modded, effect, chargeRules.ChargesPerUse, held, charged, warnings,
                contact.Milliseconds, warned, noEarlyDamage, damage, true, DateTimeOffset.UtcNow);
        }
        finally
        {
            player.Closed = true;
            Game.AreaUpdateContext.Value = previous;
            PlayerInventories.Forget(player.Character.Name!);
            clock.ReleaseAll();
        }
    }

    sealed record Sample(string Name, bool Modded, bool Effect, int Cost, int BeforeCharges, int AfterCharges, int Warnings,
        int ContactMilliseconds, bool WarnedAtOriginalCell, bool NoEarlyDamage, int LifeLostAtImpact,
        bool CleanupScheduled, DateTimeOffset Utc);

    sealed class HazardClock : IPlayerUpdateSchedule
    {
        internal sealed record Delay(int Milliseconds, TaskCompletionSource Complete);
        readonly Channel<Delay> waits = Channel.CreateUnbounded<Delay>();
        double time = 10;
        public double Now(Player player) => time;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            if (stopping.IsCancellationRequested) return Task.FromCanceled(stopping);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waits.Writer.TryWrite(new(milliseconds, completion));
            return completion.Task;
        }
        internal bool TryTake(out Delay? delay) => waits.Reader.TryRead(out delay);
        internal Task<Delay> Take() => waits.Reader.ReadAsync().AsTask();
        internal void Release(Delay delay)
        {
            time += delay.Milliseconds / 1000d;
            delay.Complete.TrySetResult();
        }
        internal void ReleaseAll()
        {
            while (TryTake(out var wait)) Release(wait!);
        }
    }
}
