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
    const int StartX = 100, StartY = 120;

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
        foreach (var sample in new[]
        {
            await UseBeforeLeaving(fixture, "party-remains", companionPresent: true, companionLeaves: false),
            await UseBeforeLeaving(fixture, "empty-before-warning", companionPresent: false, companionLeaves: false),
            await UseBeforeLeaving(fixture, "empty-after-warning", companionPresent: true, companionLeaves: true),
        })
        {
            bool warns = sample.Name != "empty-before-warning";
            bool hits = sample.Name == "party-remains";
            bool valid = sample.ChargeCost == 30 && sample.FlaskApplied && sample.LeavesBeforeWarning
                && sample.CompanionWarnings == (warns ? 1 : 0) && sample.OriginalCell == warns
                && sample.NativeClip == warns && sample.CompanionRemoval == hits
                && sample.NoEarlyDamage && (sample.CompanionDamage > 0) == hits
                && sample.OtherMapUntouched && sample.OwnerUntouched
                && sample.LateEntrantUntouched && sample.Retired;
            output.WriteLine("FLASK_METEOR_LIFETIME " + JsonSerializer.Serialize(new { sample, valid }));
            if (!valid) incorrect.Add(sample.Name);
        }
        var stopped = await StopWithArmedMeteor();
        bool stoppedValid = stopped.ChargeCost == 30 && stopped.WarningQueued && stopped.LeavesBeforeImpact
            && stopped.DriverStopped && stopped.Retired;
        output.WriteLine("FLASK_METEOR_DRIVER " + JsonSerializer.Serialize(new { sample = stopped, valid = stoppedValid }));
        if (!stoppedValid) incorrect.Add("empty-driver");
        Assert.True(incorrect.Count == 0, "Map flask meteor behavior differs at " + string.Join(", ", incorrect));
    }

    static AreaLayout MapLayout()
    {
        ushort hash = AreaTransitionCatalog.HashOf(AreaId)!.Value;
        return new(hash, AreaId, 512, 512, StartX, StartY,
            (x, y) => x >= 0 && x < 512 && y >= 0 && y < 512, []);
    }

    static Player MakePlayer(AreaLayout layout, InstanceState instance, SpellTotemExecutionTests.Wire wire) =>
        new(layout, 1, 144842)
        {
            Character = { Name = "FlaskMeteor" + Guid.NewGuid().ToString("N"), Experience = CharacterStats.ExperienceFor(100) },
            Instance = instance,
            Connection = { PlayerSent = true, Session = new("flask meteor", wire.Stream, wire.Ciphers) },
            LeagueModule = new(new League("flask-meteor", "Standard", false, false), new HashSet<string>(["Game"])),
            Pools = new(100, 10000, 10000, 0, 0),
            PlayerLife = 5000,
            PlayerMana = 10000,
        };

    static void OpenMap(Player player, bool modded)
    {
        var area = new MapDevice.MapArea(player.Layout.Hash, AreaId, "Abomination", 17) { Level = 84 };
        var mod = Program.Items.Mods.ById("MapUberModFlaskMeteor")!;
        int index = mod.Stats.ToList().FindIndex(s => s.Id == Stat.MapPlayerCreateEnemyMeteorDaemonOnFlaskUsePctChance);
        Assert.True(index >= 0);
        Assert.Equal(100, mod.RangeAt(index).Min);
        var modifiers = new Dictionary<uint, int>();
        if (modded) modifiers[Stat.MapPlayerCreateEnemyMeteorDaemonOnFlaskUsePctChance] = mod.RangeAt(index).Min;
        var mapMods = new MapMods(modifiers) { MapRarity = ItemRarity.Rare };
        player.Instance.OpenedWith(new MapDevice.Run(player.Character.Name!, area, mapMods));
        var instanceStats = Game.ComposeMapAreaStats(mapMods, area.Tier, new Dictionary<uint, int>(), new HashSet<string>(), 0);
        player.Instance.AreaStats(() => instanceStats);
    }

    const byte Slot = 2; // The starter kit occupies 0, 1 and 4.

    static (InventoryItem Flask, int Cost) Equip(Player player, bool recovery = false, bool charges = true, bool automatic = false)
    {
        var catalog = Program.Items;
        var type = catalog.Find(recovery ? "Small Life Flask" : "Granite Flask")!;
        var chargeRules = catalog.FlaskChargesOf(type)!;
        var item = ItemCatalog.Create(type, random: new Random(11)) with { Charges = charges ? chargeRules.MaxCharges : 0, Identified = true };
        if (automatic)
            item = item with { Mods = [catalog.Mods.ById("FlaskEnchantmentInjectorOnFullCharges__")!
                .Roll(ModSection.Enchant, new Random(2205))] };
        var inventory = Game.ItemsOf(player);
        Assert.DoesNotContain(inventory.Items(PlayerInventories.Flask), i => i.X == Slot);
        return (inventory.Add(PlayerInventories.Flask, Slot, 0, item), chargeRules.ChargesPerUse);
    }

    static int Held(Player player, InventoryItem flask) =>
        Game.ItemsOf(player).Items(PlayerInventories.Flask).Single(i => i.Id == flask.Id).Item.Charges;

    static void SetPools(Player player, bool fullLife)
    {
        player.Pools = Game.PoolsFor(player, 100);
        player.PlayerLife = fullLife ? player.PlayerMaxLife : player.PlayerMaxLife / 2;
        player.PlayerMana = player.Pools.Mana;
    }

    static async Task<Sample> Use(TriggeredSummonTests.Fixture fixture, string name, bool modded,
        bool automatic = false, bool recovery = false, bool charges = true, bool fullLife = false, int move = 0)
    {
        using var wire = new SpellTotemExecutionTests.Wire();
        var player = MakePlayer(MapLayout(), new(), wire);
        OpenMap(player, modded);
        var (flask, cost) = Equip(player, recovery, charges, automatic);
        SetPools(player, fullLife);
        var clock = new HazardClock();
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            uint warningId = player.NextObjectId;
            uint startingLife = player.PlayerLife;
            int held = Held(player, flask);
            if (automatic)
                await Game.DrainFlaskTriggers(player);
            else
                await Game.UseFlask(player, Slot);
            int charged = Held(player, flask);
            bool effect = recovery ? player.FlaskEffects.Any : player.UtilityFlaskEffects.Of(flask.Id) is not null;
            int warnings = (int)(player.NextObjectId - warningId);
            uint life = player.PlayerLife;
            if (!clock.TryTake(out var contact) && warnings > 0)
                contact = await clock.Take().WaitAsync(TimeSpan.FromSeconds(20));
            if (contact is null)
                return new(name, modded, effect, cost, held, charged, warnings,
                    0, false, startingLife == life, 0, false, DateTimeOffset.UtcNow);

            if (move > 0) player.Walk.Teleport(StartX + move, StartY, clock.Now(player));
            bool noEarlyDamage = player.PlayerLife == startingLife;
            clock.Release(contact);
            // A second wait retains the impact animation after the hit. The fake clock drives both stages.
            var cleanup = await clock.Take().WaitAsync(TimeSpan.FromSeconds(20));
            var history = Program.HistoryFor(wire.Ciphers).Lines();
            bool warned = history.Any(line => line.Contains($"02BB{warningId:X8}", StringComparison.Ordinal)
                && line.Contains("6400000078000000", StringComparison.Ordinal));
            int damage = (int)(life - player.PlayerLife);
            clock.Release(cleanup);
            return new(name, modded, effect, cost, held, charged, warnings,
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

    static async Task<LifetimeSample> UseBeforeLeaving(TriggeredSummonTests.Fixture fixture, string name,
        bool companionPresent, bool companionLeaves)
    {
        using var useWire = new SpellTotemExecutionTests.Wire();
        using var companionWire = new SpellTotemExecutionTests.Wire();
        using var otherWire = new SpellTotemExecutionTests.Wire();
        using var lateWire = new SpellTotemExecutionTests.Wire();
        var layout = MapLayout();
        var instance = new InstanceState();
        var user = MakePlayer(layout, instance, useWire);
        var companion = MakePlayer(layout, instance, companionWire);
        var late = MakePlayer(layout, instance, lateWire);
        var other = MakePlayer(layout, new InstanceState(), otherWire);
        OpenMap(user, modded: true);
        var area = user.Area;
        var (flask, _) = Equip(user);
        SetPools(user, fullLife: false);
        SetPools(companion, fullLife: true);
        SetPools(other, fullLife: true);
        SetPools(late, fullLife: true);
        uint life = user.PlayerLife, companionLife = companion.PlayerLife, otherLife = other.PlayerLife, lateLife = late.PlayerLife;
        uint id = user.NextObjectId;
        void Enter(Player player)
        {
            player.Instance.AddPlayer(player);
            Program.InGame.TryAdd(player, 0);
        }
        void Leave(Player player)
        {
            if (!player.LeavingArea)
                Assert.True(player.TryBeginAreaChange(new(0x7ad2, 777, false), out _));
            Program.InGame.TryRemove(player, out _);
        }
        string[] Warnings(SpellTotemExecutionTests.Wire wire) =>
            [.. Program.HistoryFor(wire.Ciphers).Lines().Where(line => line.Contains($"02BB{id:X8}", StringComparison.Ordinal))];
        bool CompanionPacket(string code) => Program.HistoryFor(companionWire.Ciphers).Lines()
            .Any(line => line.Contains($"{code}{id:X8}", StringComparison.Ordinal));
        double frameSeconds = Program.FrameMs / 1000d;
        long framesToImpact = (long)Math.Ceiling(NativeContactMilliseconds / (double)Program.FrameMs);
        async Task Step(long frame, Player? viewer)
        {
            double time = frame * frameSeconds;
            area.Step(frame, time);
            var delta = area.Delta.Take();
            if (viewer is not null)
            {
                Assert.True(await Program.EndWorldFrame(viewer, (time, time + frameSeconds), delta));
            }
        }
        var clock = new HazardClock();
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            Enter(user);
            Enter(other);
            if (companionPresent)
            {
                Enter(companion);
                await Program.BeginWorldFrames(companion, frameSeconds);
            }

            int held = Held(user, flask);
            var sendGate = Program.SendGates.GetValue(useWire.Ciphers, _ => new(1, 1));
            await sendGate.WaitAsync();
            Task use;
            bool leftBeforeWarning;
            int spent;
            bool applied;
            try
            {
                var previousOwner = GameArea.Own(area);
                try { use = Game.UseFlask(user, Slot); }
                finally { GameArea.Own(previousOwner); }
                // The charge transaction has committed, but A's connection has not written any world output.
                spent = held - Held(user, flask);
                applied = user.UtilityFlaskEffects.Of(flask.Id) is not null;
                leftBeforeWarning = Warnings(useWire).Length == 0 && Warnings(companionWire).Length == 0;
                Leave(user);
                user.Walk.Teleport(250, 250, clock.Now(user));
            }
            finally { sendGate.Release(); }
            await use;

            Player? viewer = companionPresent ? companion : null;
            await Step(1, viewer);
            var warning = Warnings(companionWire);
            bool originalCell = warning.Length == 1 && warning[0].Contains("6400000078000000", StringComparison.Ordinal);
            bool nativeClip = CompanionPacket("01B2");
            if (companionLeaves)
            {
                Leave(companion);
                viewer = null;
            }
            await Step(framesToImpact, viewer);
            bool noEarly = companion.PlayerLife == companionLife;
            await Step(framesToImpact + 1, viewer);
            int damage = (int)(companionLife - companion.PlayerLife);
            await Step(framesToImpact + 1 + (long)Math.Ceiling(600d / Program.FrameMs), viewer);
            bool retired = !area.Objects.Has(id);
            if (viewer is null)
            {
                Enter(late);
                await Program.BeginWorldFrames(late, frameSeconds);
                var previousOwner = GameArea.Own(area);
                try { area.Objects.Join(late); }
                finally { GameArea.Own(previousOwner); }
                await Step(area.Frame + 1, late);
            }
            return new(name, spent, applied, leftBeforeWarning, warning.Length, originalCell, nativeClip, CompanionPacket("02BD"), noEarly, damage,
                other.PlayerLife == otherLife && Warnings(otherWire).Length == 0,
                user.PlayerLife == life && Warnings(useWire).Length == 0,
                late.PlayerLife == lateLife && Warnings(lateWire).Length == 0, retired, DateTimeOffset.UtcNow);
        }
        finally
        {
            foreach (var player in new[] { user, companion, late, other })
            {
                Leave(player);
                player.Closed = true;
            }
            Game.AreaUpdateContext.Value = previous;
            PlayerInventories.Forget(user.Character.Name!);
            clock.ReleaseAll();
        }
    }

    sealed record LifetimeSample(string Name, int ChargeCost, bool FlaskApplied, bool LeavesBeforeWarning, int CompanionWarnings,
        bool OriginalCell, bool NativeClip, bool CompanionRemoval, bool NoEarlyDamage, int CompanionDamage, bool OtherMapUntouched,
        bool OwnerUntouched, bool LateEntrantUntouched, bool Retired, DateTimeOffset Utc);

    static async Task<DriverSample> StopWithArmedMeteor()
    {
        using var wire = new SpellTotemExecutionTests.Wire();
        var player = MakePlayer(MapLayout(), new(), wire);
        var instance = player.Instance;
        OpenMap(player, modded: true);
        var (flask, _) = Equip(player);
        SetPools(player, fullLife: true);
        var frames = instance.World.Frames;
        var area = player.Area;
        int held = Held(player, flask);
        uint life = player.PlayerLife;
        uint id = player.NextObjectId;
        try
        {
            instance.AddPlayer(player);
            await frames.Join("flask meteor driver", wire.Stream, wire.Ciphers, player);
            await frames.Run(() => Game.UseFlask(player, Slot)).WaitAsync(TimeSpan.FromSeconds(20));
            bool warning = false;
            Task stopped = Task.CompletedTask;
            long leavingFrame = 0;
            await frames.Run(() =>
            {
                warning = area.Objects.Has(id);
                leavingFrame = area.Frame;
                // The frame wait is released when the last member detaches, without advancing to the impact timer.
                stopped = frames.Wait(player, PlayerUpdateRole.MonsterAction, int.MaxValue, CancellationToken.None);
                Assert.True(player.TryBeginAreaChange(new(0x7ad2, 778, false), out _));
                frames.Detach(player);
            }).WaitAsync(TimeSpan.FromSeconds(20));
            await stopped.WaitAsync(TimeSpan.FromSeconds(20));
            return new(held - Held(player, flask), warning,
                player.PlayerLife == life && area.Frame - leavingFrame <= 1, !frames.Running, !area.Objects.Has(id));
        }
        finally
        {
            player.Closed = true;
            instance.RemovePlayer(player.Id);
            frames.Detach(player);
            PlayerInventories.Forget(player.Character.Name!);
        }
    }

    sealed record DriverSample(int ChargeCost, bool WarningQueued, bool LeavesBeforeImpact, bool DriverStopped, bool Retired);

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
