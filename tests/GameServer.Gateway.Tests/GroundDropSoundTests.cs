using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class GroundDropSoundTests
{
    [Fact]
    public async Task New_drops_sound_once_for_existing_observers_but_replays_restores_and_stack_remainders_are_quiet()
    {
        var item = new Item(0x11223344, [], 0);
        var old = new WorldItemObject(110, 100, item);
        byte[] replay = old.Blob(), drop = old.Blob(justDropped: true);
        Assert.Equal(replay.Length, drop.Length);
        Assert.Equal(0, replay[23]); Assert.Equal(1, drop[23]);
        Assert.Equal(replay.Where((_, i) => i != 23), drop.Where((_, i) => i != 23));
        var ground = new InstanceGround();
        uint earlier = ground.Add(old);
        object observer = new(), other = new();
        ground.Observe(observer); ground.Observe(other);
        Assert.False(ground.IsNewFor(observer, earlier));
        uint fresh = ground.Add(old);
        Assert.True(ground.IsNewFor(observer, fresh));
        Assert.True(ground.IsNewFor(other, fresh));
        ground.Observe(observer); // Reconciliation/reload does not reset an admitted observer's baseline.
        Assert.True(ground.IsNewFor(observer, fresh));
        object later = new(); ground.Observe(later);
        Assert.False(ground.IsNewFor(later, fresh));
        ground.Take(fresh); ground.Restore(fresh, old);
        Assert.False(ground.IsNewFor(observer, fresh));
        uint remainder = ground.Add(old, null, shared: true, justDropped: false);
        Assert.False(ground.IsNewFor(observer, remainder));

        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        using var lateWire = new PartyFollowerTests.Wire();
        var instance = new InstanceState();
        var viewer = runtime.Connection(instance, wire);
        var joining = runtime.Connection(instance, lateWire);
        instance.Ground.Observe(viewer);
        var freshObject = new WorldItemObject(111, 100, item);
        uint id = instance.Ground.Add(freshObject);
        string sound = Convert.ToHexString(InstanceEntities.CreateObject(id,
            InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), freshObject.Blob(justDropped: true)));
        int Count(PartyFollowerTests.Wire w, string hex) => Program.HistoryFor(w.Ciphers).Lines()
            .Count(line => line.Contains(hex[..Math.Min(hex.Length, PacketHistory.KeptBytes * 2)], StringComparison.Ordinal));
        await Game.SyncSharedGround("sound-observer", wire.Stream, wire.Ciphers, viewer);
        Assert.Equal(1, Count(wire, sound));
        await Game.SyncSharedGround("sound-observer-repeat", wire.Stream, wire.Ciphers, viewer);
        Assert.Equal(1, Count(wire, sound));
        await Game.SendGroundItems("sound-reentry", lateWire.Stream, lateWire.Ciphers, joining);
        await Game.SendGroundItems("sound-reentry-replay", lateWire.Stream, lateWire.Ciphers, joining);
        Assert.Equal(0, Count(lateWire, sound));
        Assert.Equal(1, Count(lateWire, Convert.ToHexString(InstanceEntities.CreateObject(id,
            InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), freshObject.Blob()))));
        uint privateId = instance.Ground.Add(old, joining, shared: false);
        await Game.SyncSharedGround("sound-private", wire.Stream, wire.Ciphers, viewer);
        Assert.DoesNotContain(privateId, viewer.SeenGround);

        // A tutorial gem: its first recipient gets one create and its wake, as world records of the new object. The
        // shared ground knows that recipient, and a connection that comes later sees the item as an old one.
        string path = Path.Combine(Path.GetTempPath(), $"localexile-tutorial-gem-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        var gifted = new ClientState(new(1, "tutorial-gem", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = $"TutorialGem{Guid.NewGuid():N}", ClassRow = 0, Instance = new(),
            LeagueModule = new(new GameServer.Core.Accounts.League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            Game.ValidateSavedProgress(gifted, null);
            Game.PrepareCharacterForArea("tutorial-gem", gifted, 1);
            foreach (bool support in new[] { false, true })
            {
                var existingObserver = new object();
                gifted.Instance.Ground.Observe(existingObserver);
                var packets = QuestobjectsRules.CommitTutorialGem(gifted, support, 100, 100);
                Assert.Equal(2, packets.Count);
                Assert.Equal(InstanceEntities.CreateObjectId, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packets[0].Packet));
                uint gem = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packets[0].Packet.AsSpan(2));
                Assert.Equal(InstanceProtocol.EnterWakeZone(gem), packets[1].Packet);
                Assert.True(packets[0].CreatesObject);
                Assert.Equal(gem, packets[0].WorldId);
                Assert.Equal(gem, packets[1].WorldId);
                var origin = gifted.Instance.Ground.Origin(gem);
                Assert.Same(gifted, origin.By);
                Assert.True(origin.Shared);
                var placed = Assert.IsType<WorldItemObject>(gifted.Instance.Ground.Get(gem));
                Assert.Equal(InstanceEntities.CreateObject(gem, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath),
                    placed.Blob(justDropped: true)), packets[0].Packet);
                Assert.True(gifted.Instance.Ground.IsNewFor(existingObserver, gem));
                var laterObserver = new object();
                gifted.Instance.Ground.Observe(laterObserver);
                Assert.False(gifted.Instance.Ground.IsNewFor(laterObserver, gem));
                Assert.Empty(QuestobjectsRules.CommitTutorialGem(gifted, support, 100, 100));
            }
        }
        finally
        {
            PlayerInventories.Forget(gifted.Character!);
            GameServer.Gateway.Tests.TestEnvironment.ReleaseSaves(path);
        }
        await VerifyGoldLifecycle(runtime, instance, wire, lateWire, viewer, joining);
        await VerifyLockstepGroundOutput(runtime, old);
    }
    static async Task VerifyGoldLifecycle(PartyFollowerTests.Runtime runtime, InstanceState instance,
        PartyFollowerTests.Wire wire, PartyFollowerTests.Wire otherWire, ClientState player, ClientState other)
    {
        player = runtime.Connection(instance, wire, $"gold-first-{Guid.NewGuid():N}");
        other = runtime.Connection(instance, otherWire, $"gold-other-{Guid.NewGuid():N}");
        instance.Ground.Observe(player);
        using var database = new GoldDatabase();
        var stash = Game.PersonalStashOf(player); var otherStash = Game.PersonalStashOf(other);
        uint before = stash.Gold, otherBefore = otherStash.Gold;
        var goldType = Game.ItemsFor(player).Find(ItemDrops.GoldId)!;
        WorldItemObject Gold(int x, int y, ushort amount) => new(x, y, ItemCatalog.Create(goldType) with { StackSize = amount });
        int Count(string bytes) => Program.HistoryFor(wire.Ciphers).Lines().Count(line => line.Contains(bytes[..Math.Min(bytes.Length, PacketHistory.KeptBytes * 2)], StringComparison.Ordinal));
        void Queue(uint id) => player.Minions.OutputQueue.Add(new([], "delayed ground drop", TargetId: id, Kind: PlayerMinions.OutputKind.GroundItem));
        var previous = SystemSettings.Default.Values;
        SystemSettings.Default.Replace(new Dictionary<string, double>(previous) { ["gold_auto_loot_radius"] = 20 });
        try
        {
            // Reproduce the actual race: the gold is committed before its kill-output queue is drained.
            var pile = Gold(119, 100, 87); uint id = instance.Ground.Add(pile, player); Queue(id);
            string create = Convert.ToHexString(InstanceEntities.CreateObject(id,
                InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), pile.Blob(justDropped: true)));
            await Game.PickUpNearbyGold("gold-auto", wire.Stream, wire.Ciphers, player);
            Assert.Null(instance.Ground.Get(id)); Assert.Equal(before + 87, stash.Gold);
            await Game.PublishMinionOutcomes("gold-late-create", wire.Stream, wire.Ciphers, player);
            Assert.Equal(0, Count(create)); Assert.True(instance.Ground.WasTaken(id));
            Assert.False(instance.Ground.WasTaken(InstanceGround.FirstItemId - 1));
            Assert.False(instance.Ground.WasTaken(uint.MaxValue));
            // An already-visible stale label is removable without another gold credit.
            string remove = Convert.ToHexString(InstanceProtocol.RemoveObject(id)); int removals = Count(remove);
            await Game.OnUseObject("gold-stale-click", wire.Stream, wire.Ciphers, player, new(id, 0x266, 0, 0x408), player.Clock.Elapsed.TotalSeconds);
            Assert.Equal(removals + 1, Count(remove)); Assert.Equal(before + 87, stash.Gold);
            // Collect every pile, the full ushort stack included, at the boundary; preserve outside/private loot.
            uint edge = instance.Ground.Add(Gold(120, 100, 13), player);
            uint large = instance.Ground.Add(Gold(100, 120, ushort.MaxValue), player);
            uint outside = instance.Ground.Add(Gold(121, 100, 11), player);
            uint privateGold = instance.Ground.Add(Gold(110, 100, 19), other, shared: false);
            await Game.PickUpNearbyGold("gold-all-piles", wire.Stream, wire.Ciphers, player);
            Assert.Null(instance.Ground.Get(edge)); Assert.Null(instance.Ground.Get(large));
            Assert.NotNull(instance.Ground.Get(outside)); Assert.NotNull(instance.Ground.Get(privateGold));
            Assert.Equal(before + 100 + ushort.MaxValue, stash.Gold);
            // Two observers/manual-auto contenders can only award a shared pile once.
            var shared = Gold(100, 100, 17); uint sharedId = instance.Ground.Add(shared);
            await Task.WhenAll(Game.PickUpGold("gold-first", wire.Stream, wire.Ciphers, player, sharedId, shared, "picked up"),
                Game.PickUpGold("gold-second", otherWire.Stream, otherWire.Ciphers, other, sharedId, shared, "picked up"));
            Assert.Equal((ulong)before + otherBefore + 117 + ushort.MaxValue, (ulong)stash.Gold + otherStash.Gold);
            // A queued item still present publishes create+wake; a stale snapshot cannot credit a restored stack.
            var old = Gold(130, 100, 87); uint replaced = instance.Ground.Add(old, player); Queue(replaced);
            var restored = Gold(130, 100, 23); instance.Ground.Restore(replaced, restored);
            before = stash.Gold;
            await Game.PickUpGold("gold-old-snapshot", wire.Stream, wire.Ciphers, player, replaced, old, "picked up");
            Assert.Equal(before, stash.Gold); Assert.Same(restored, instance.Ground.Get(replaced));
            await Game.PublishMinionOutcomes("gold-restored-create", wire.Stream, wire.Ciphers, player);
            Assert.Contains(replaced, player.SeenGround);
            string restoredCreate = Convert.ToHexString(InstanceEntities.CreateObject(replaced,
                InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), restored.Blob()));
            Assert.Equal(1, Count(restoredCreate));
        }
        finally { SystemSettings.Default.Replace(previous); }
    }

    sealed class GoldDatabase : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), $"gold-pickup-{Guid.NewGuid():N}.db");
        public GoldDatabase() => Saves.Open(path);
        public void Dispose() => TestEnvironment.ReleaseSaves(path);
    }

    static async Task VerifyLockstepGroundOutput(PartyFollowerTests.Runtime runtime, WorldItemObject item)
    {
        using var wire = new PartyFollowerTests.Wire();
        var player = runtime.Connection(new InstanceState(), wire, "lockstep-ground");
        player.LockstepClient = true;
        await Program.BeginWorldFrames(player, .033);
        void Queue(uint id) => player.Minions.OutputQueue.Add(new([], "deferred lockstep drop", TargetId: id,
            Kind: PlayerMinions.OutputKind.GroundItem));
        uint stale = player.Instance.Ground.Add(item, player);
        Queue(stale); player.Instance.Ground.Take(stale);
        int before = Program.HistoryFor(wire.Ciphers).Lines().Count();
        await Game.PublishMinionOutcomes("stale-lockstep-drop", wire.Stream, wire.Ciphers, player);
        Assert.Empty(player.Minions.OutputQueue);
        Assert.Equal(before, Program.HistoryFor(wire.Ciphers).Lines().Count());
        uint live = player.Instance.Ground.Add(item, player); Queue(live);
        var publishing = Game.PublishMinionOutcomes("live-lockstep-drop", wire.Stream, wire.Ciphers, player);
        Assert.False(publishing.IsCompleted);
        await Program.EndWorldFrame(player, (.033, .066));
        await publishing;
        await Program.EndWorldFrame(player, (.066, .099));
        Assert.Empty(player.Minions.OutputQueue);
        Assert.Contains(live, player.SeenGround);
        Assert.Contains(Program.HistoryFor(wire.Ciphers).Lines(), line => line.Contains($"02BB{live:X8}", StringComparison.Ordinal));
    }

}
