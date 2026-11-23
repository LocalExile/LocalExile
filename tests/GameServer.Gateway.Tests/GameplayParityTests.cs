using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class GameplayParityTests
{
    [Fact]
    public async Task Items_vendors_rewards_flasks_npc_repeats_and_skill_binds_keep_client_parity()
    {
        string database = Path.Combine(Path.GetTempPath(), "LocalExile-parity-" + Guid.NewGuid().ToString("N") + ".db");
        Saves.Open(database);
        try
        {
            var catalog = Program.Items;
            string name = "Parity" + Guid.NewGuid().ToString("N");
            var items = PlayerInventories.For(name, catalog);
            foreach (var starter in items.Items(PlayerInventories.MainInventory).ToArray())
                items.Remove(PlayerInventories.MainInventory, starter.Id);
            var stash = AccountStash.For(name, catalog);
            PlayerInventories.Shape? Shape(Item item) => catalog.ByHash(item.TypeHash) is { } t
                ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;

            // #4: 3x Normal Small Life Flask vendor recipe yields an identified Normal Medium Life Flask;
            // 3x Magic Small Life Flasks yield an unidentified Magic Medium Life Flask.
            var recipes = new VendorRecipes(catalog);
            var smallLife = catalog.Find("Small Life Flask")!;
            var normalFlasks = Enumerable.Range(1, 3)
                .Select(i => new InventoryItem((uint)i, (byte)i, 0, ItemCatalog.Create(smallLife)))
                .ToArray();
            var normalQuote = recipes.Evaluate(normalFlasks, new Random(1));
            var normalUpgraded = Assert.Single(normalQuote.Rewards);
            Assert.Equal("Medium Life Flask", catalog.ByHash(normalUpgraded.TypeHash)?.Name);
            Assert.Equal(ItemRarity.Normal, normalUpgraded.Rarity);
            Assert.True(normalUpgraded.Identified);

            var magicFlasks = Enumerable.Range(1, 3)
                .Select(i => new InventoryItem((uint)i, (byte)i, 0, ItemCatalog.Create(smallLife) with { Rarity = ItemRarity.Magic }))
                .ToArray();
            var magicQuote = recipes.Evaluate(magicFlasks, new Random(1));
            var magicUpgraded = Assert.Single(magicQuote.Rewards);
            Assert.Equal(ItemRarity.Magic, magicUpgraded.Rarity);
            Assert.False(magicUpgraded.Identified);

            // #3: Quest reward ClaimAt places at the exact requested (x, y) cell rather than the first free cell,
            // and vendor PurchaseVendor merges into a matching partial stack at (x, y) and supports ExpandedInventory.
            var wisdom = catalog.Find("Scroll of Wisdom")!;
            var fireball = catalog.Find("Fireball")!;
            var claimed = QuestRewardCatalog.ClaimAt(name, "parity-reward-1", null, items, ItemCatalog.Create(fireball),
                Shape, PlayerInventories.MainInventory, x: 5, y: 3)!;
            Assert.Equal((byte)5, claimed.Placed.X);
            Assert.Equal((byte)3, claimed.Placed.Y);

            var existingWisdom = items.Add(PlayerInventories.MainInventory, 4, 2, ItemCatalog.Create(wisdom) with { StackSize = 5 });
            var portal = catalog.Find("Portal Scroll")!;
            items.Add(PlayerInventories.MainInventory, 0, 0, ItemCatalog.Create(portal) with { StackSize = 10 });
            var wisdomOffer = ItemCatalog.Create(wisdom) with { Price = [(portal.Hash, 1)] };
            var mergedBuy = ItemMoves.PurchaseVendor(items, stash, stash, wisdomOffer, catalog, Shape, () => true,
                PlayerInventories.MainInventory, x: 4, y: 2)!;
            Assert.True(mergedBuy.Placed!.Merged);
            Assert.Equal(existingWisdom.Id, mergedBuy.Placed.Placed.Id);
            Assert.Equal((ushort)6, mergedBuy.Placed.Placed.Item.StackSize);
            Assert.Equal((byte)4, mergedBuy.Placed.Placed.X);
            Assert.Equal((byte)2, mergedBuy.Placed.Placed.Y);

            var expandedBuy = ItemMoves.PurchaseVendor(items, stash, stash, wisdomOffer, catalog, Shape, () => true,
                PlayerInventories.ExpandedInventory, x: 2, y: 1, expandedAllowed: true)!;
            Assert.Equal(PlayerInventories.ExpandedInventory, expandedBuy.Placed!.Row);
            Assert.Equal((byte)2, expandedBuy.Placed.Placed.X);
            Assert.Equal((byte)1, expandedBuy.Placed.Placed.Y);

            // #5: C->S 0x0089 (20 bytes fixed) parses cleanly and sockets quest-reward and vendor gems directly
            // into item sockets (moving any displaced gem onto the empty cursor).
            Assert.Equal(InstanceNpcs.BuyAndSocketLength, InstanceProtocol.KnownClientLength(InstanceNpcs.BuyAndSocketId));
            byte[] raw89 = new LoginWriter().U16(InstanceNpcs.BuyAndSocketId)
                .U32(0x11).U32(0x04000001).U32(0x22).U32(1).U8(0).U8(0).ToArray();
            var parsed89 = InstanceNpcs.ReadBuyAndSocket(raw89);
            Assert.Equal(new InstanceNpcs.BuyAndSocket(0x11, 1, 0x22, 1, 0, 0), parsed89);

            var sword = catalog.Find("Rusted Sword")!;
            var host = items.Add(PlayerInventories.MainInventory, 8, 0,
                ItemCatalog.Create(sword, [new(SocketColour.Blue), new(SocketColour.Blue)], [2]));
            var socketedReward = QuestRewardCatalog.ClaimIntoSocket(name, "parity-reward-2", null, items,
                ItemCatalog.Create(fireball), PlayerInventories.MainInventory, host.Id, 0, Shape, out string why);
            Assert.NotNull(socketedReward);
            Assert.Equal("", why);
            Assert.Null(socketedReward.Value.Displaced);
            Assert.Equal(fireball.Hash, socketedReward.Value.Item.Item.Sockets[0].Gem?.TypeHash);

            var freezingPulse = catalog.Find("Freezing Pulse")!;
            var gemOffer = ItemCatalog.Create(freezingPulse) with { Price = [(portal.Hash, 1)] };
            var socketedBuy = ItemMoves.PurchaseAndSocketVendor(items, stash, stash, gemOffer, catalog,
                PlayerInventories.MainInventory, host.Id, 0, Shape, () => true, out why)!;
            Assert.Equal("", why);
            Assert.Equal(freezingPulse.Hash, socketedBuy.Host.Item.Sockets[0].Gem?.TypeHash);
            Assert.NotNull(socketedBuy.Displaced);
            Assert.Equal(fireball.Hash, socketedBuy.Displaced!.Item.TypeHash);
            items.Remove(PlayerInventories.Cursor, socketedBuy.Displaced.Id);

            // #6: Moving or swapping flasks within the flask bar preserves FromRow == Flask (charges kept in hostile areas),
            // whereas equipping from MainInventory reports FromRow == MainInventory (charges emptied in hostile areas).
            foreach (var f in items.Items(PlayerInventories.Flask).ToArray()) items.Remove(PlayerInventories.Flask, f.Id);
            var manaBase = catalog.Find("Small Mana Flask")!;
            var lifeBase = catalog.Find("Small Life Flask")!;
            var manaEquipped = items.Add(PlayerInventories.Flask, 4, 0, ItemCatalog.Create(manaBase) with { Charges = 24 });
            var lifeEquipped = items.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(lifeBase) with { Charges = 21 });
            Assert.NotNull(items.PickUp(PlayerInventories.Flask, manaEquipped.Id));
            var movedEmpty = items.Place(PlayerInventories.Flask, 1, 0, Shape, out _)!;
            Assert.Equal(PlayerInventories.Flask, movedEmpty.FromRow);
            Assert.Equal((byte)24, movedEmpty.Placed.Item.Charges);
            Assert.NotNull(items.PickUp(PlayerInventories.Flask, movedEmpty.Placed.Id));
            var swappedBar = items.Place(PlayerInventories.Flask, 0, 0, Shape, out _)!;
            Assert.Equal(PlayerInventories.Flask, swappedBar.FromRow);
            Assert.Equal(lifeEquipped.Id, swappedBar.Displaced!.Id);
            var displacedBack = items.Place(PlayerInventories.Flask, 4, 0, Shape, out _)!;
            Assert.Equal(PlayerInventories.Flask, displacedBack.FromRow);
            var bagFlask = items.Add(PlayerInventories.MainInventory, 10, 0, ItemCatalog.Create(manaBase) with { Charges = 24 });
            Assert.NotNull(items.PickUp(PlayerInventories.MainInventory, bagFlask.Id));
            var fromBag = items.Place(PlayerInventories.Flask, 2, 0, Shape, out _)!;
            Assert.Equal(PlayerInventories.MainInventory, fromBag.FromRow);

            // #7 & #1: Held-LMB repeat (InitialPressFlag 0x0400 clear) on an NPC does not wipe an open quest reward
            // or emit 0x0084, and C->S 0x004e BindSkill echoes S->C 0x0053 so client skill bar cache stays synced.
            using var wire = new Wire(name);
            var player = wire.Player;
            var (px, py) = player.Walk.Position(player.Clock.Elapsed.TotalSeconds);
            var nessa = Game.Npcs.Find("Metadata/NPC/Act1/Nessa")!;
            lock (player.Npcs) player.Npcs[0x300] = new SpawnedNpc(nessa, px, py);
            player.NpcRewardOffer = 2;
            await Game.OnUseObject(player,
                new InstanceTransfer.UseSkillOnObject(0x300, 0x266, 1, 0x000a), player.Clock.Elapsed.TotalSeconds);
            Assert.Equal(2, player.NpcRewardOffer);

            byte[] bindMove = new LoginWriter().U16(InstanceProtocol.BindSkillId).U8(0).U16(0x2909).ToArray();
            await Game.OnClientMessages([(InstanceProtocol.BindSkillId, bindMove)], [], player);
            byte[] sent = wire.Drain();
            Assert.True(sent.Length >= 2);
            ushort sentId = (ushort)(sent[0] << 8 | sent[1]);
            Assert.Equal(InstanceProtocol.SkillBarId, sentId);
        }
        finally { TestEnvironment.ReleaseSaves(database); }
    }

    [GameFilesFact]
    public async Task Corpse_death_unblock_area_arrival_and_checkpoint_resurrect_fade_match_client()
    {
        // #9: Lockstep AreaLife 0x01b1 life=0 delivered through WritingWorldDelta immediately records CorpseDied
        // without waiting 1 s for CorpseDeathWaitSeconds.
        var player = new Player(new(1, "1_1_2", 200, 200, 100, 100, null, []), 1, 1)
        {
            Instance = new(),
            Connection = { LockstepClient = true, LockstepFrame = (0.0, 0.033) }
        };
        uint monsterId = 0x5000;
        var zombie = Game.Monsters.Find("Metadata/Monsters/Zombie/Zombie")!;
        var spawned = new SpawnedMonster("zombie", new PlayerWalk(100, 100, 20), 60, 10, 10)
        {
            Variety = zombie,
            Dead = true,
            Life = 0
        };
        player.Area.Monsters[monsterId] = spawned;
        Assert.True(player.RetainCorpseBody(monsterId, spawned, ticks: 5));
        Assert.True(player.CorpseAwaitsDeathOf(monsterId));
        var lifeZero = InstanceProtocol.LifeUpdate(monsterId, 0, pool: AreaLife.LifePool);
        Game.CorpseDeathsWritten(player, [new AreaDelta.Record(monsterId, lifeZero, "0x01b1 monster dies", default, AreaDelta.Kind.World)]);
        Assert.False(player.CorpseAwaitsDeathOf(monsterId));

        // #10: Entering The Coast from Lioneye's Watch arrives at the authored .arm entrance point rather than
        // 6 cells from the area transition portal.
        var coast = AreaLayouts.For(AreaLayouts.TheCoast, 1);
        var arrival = coast.Arrival(AreaLayouts.LioneyesWatch);
        var entrance = coast.EntranceFrom?.Invoke(AreaLayouts.LioneyesWatch, null);
        Assert.NotNull(entrance);
        Assert.True(entrance.Value.NamedPoint);
        Assert.Equal((entrance.Value.X, entrance.Value.Y), arrival);
        var townGate = coast.AreaTransitions.First(t => t.Transition.DestinationHash16 == AreaLayouts.LioneyesWatch).Transition;
        int dist = Math.Max(Math.Abs(arrival.X - townGate.GridX), Math.Abs(arrival.Y - townGate.GridY));
        Assert.True(dist > 10, $"Expected authored Coast entrance well clear of portal, got distance {dist}");

        // #2: Resurrect at Checkpoint sends 0x00cb transition fade while still dead before reviving and teleporting.
        string database = Path.Combine(Path.GetTempPath(), "LocalExile-resfade-" + Guid.NewGuid().ToString("N") + ".db");
        Saves.Open(database);
        try
        {
            using var wire = new Wire("ResFade" + Guid.NewGuid().ToString("N"));
            wire.Player.PlayerLife = 0;
            var resTask = Game.Resurrect(wire.Player, inTown: false);
            byte[] received = await wire.ReadUntilOpcodeAsync(InstanceProtocol.FadeMoveId);
            Assert.Equal(0u, wire.Player.PlayerLife);
            wire.Player.Closed = true;
            await resTask;
            Assert.NotEmpty(received);
        }
        finally { TestEnvironment.ReleaseSaves(database); }
    }

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        readonly TcpClient reader;
        readonly ConnectionCiphers decrypt = new(new byte[64], true);
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public readonly Player Player;
        public NetworkStream Stream => writer.GetStream();

        public Wire(string character)
        {
            Player = new(new(0, "1_1_2", 200, 200, 50, 50, null, []), 0, 1)
            {
                Instance = new(),
                PlayerLife = 50,
            };
            Player.Character.Name = character;
            Player.Character.ClassRow = 1;
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
            Player.Connection.Session = new(character, Stream, Ciphers);
        }

        public byte[] Drain()
        {
            writer.Client.Shutdown(SocketShutdown.Send);
            using var ms = new MemoryStream();
            var buf = new byte[4096];
            int n;
            while ((n = reader.GetStream().Read(buf, 0, buf.Length)) > 0)
            {
                decrypt.Receive.Process(buf.AsSpan(0, n));
                ms.Write(buf, 0, n);
            }
            return ms.ToArray();
        }

        public async Task<byte[]> ReadUntilOpcodeAsync(ushort opcode)
        {
            byte hi = (byte)(opcode >> 8), lo = (byte)(opcode & 0xff);
            var stream = reader.GetStream();
            var buf = new byte[4096];
            using var ms = new MemoryStream();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!cts.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buf, cts.Token);
                if (n <= 0) break;
                decrypt.Receive.Process(buf.AsSpan(0, n));
                ms.Write(buf, 0, n);
                var data = ms.ToArray();
                for (int i = 0; i + 1 < data.Length; i++)
                    if (data[i] == hi && data[i + 1] == lo) return data;
            }
            return [];
        }

        public void Dispose() { Player.Closed = true; reader.Dispose(); writer.Dispose(); listener.Stop(); }
    }
}
