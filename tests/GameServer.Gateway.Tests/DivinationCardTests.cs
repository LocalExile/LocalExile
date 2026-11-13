using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using System.Reflection;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class DivinationCardTests
{
    [Fact]
    public void Named_rewards_match_current_card_sets_and_never_substitute_unrelated_items()
    {
        var items = Program.Items;
        var uniques = new UniqueCatalog(items);
        var rolls = new ItemRolls(items);
        Assert.Equal(470, DivinationRewards.All.Count);
        var doctor = DivinationRewards.Find(items.Find("The Doctor")!.Id)!;
        Assert.Equal(8, doctor.Stack);
        var belt = DivinationRewards.Create(doctor, items, uniques, rolls, new Random(499));
        Assert.NotNull(belt);
        Assert.Equal(uniques.Create("Headhunter", new Random(1))!.NameWords, belt.NameWords);
        Assert.Equal(ItemRarity.Unique, belt.Rarity);
        var scholar = DivinationRewards.Find(items.Find("The Scholar")!.Id)!;
        var wisdom = DivinationRewards.Create(scholar, items, uniques, rolls, new Random(1))!;
        Assert.Equal(items.Find("Scroll of Wisdom")!.Hash, wisdom.TypeHash);
        Assert.Equal(40, wisdom.StackSize);
        Assert.Null(DivinationRewards.Create(doctor with { Name = "No such unique" }, items, uniques, rolls, new Random(1)));
        Assert.Null(DivinationRewards.Create(doctor with { Enabled = false }, items, uniques, rolls, new Random(1)));
        Assert.Null(DivinationRewards.Create(doctor with { Kind = "void" }, items, uniques, rolls, new Random(1)));
        // Two corruption implicits replace ordinary base implicits, rather than keeping one and adding one.
        var doubleImplicit = doctor with { Name = "Astramentis", TwoImplicit = true, Corrupted = true, ItemLevel = 100 };
        var amulet = DivinationRewards.Create(doubleImplicit, items, uniques, rolls, new Random(1))!;
        Assert.True(amulet.Corrupted);
        var implicits = amulet.Mods.Where(m => m.Section == ModSection.Implicit).ToArray();
        Assert.Equal(2, implicits.Length);
        Assert.All(implicits, m => Assert.Equal(5, items.Mods.ByHash(m.Hash)!.GenerationType));
        var humble = DivinationRewards.Find(items.Find("Humility")!.Id)!;
        var robe = DivinationRewards.Create(humble, items, uniques, rolls, new Random(1))!;
        Assert.Equal(6, robe.Sockets.Count);
        Assert.Equal(6, Assert.Single(robe.Links!));
        Assert.All(robe.Sockets, s => Assert.Equal(SocketColour.White, s.Colour));
        foreach (var (name, level, corrupted) in new[] { ("The Artist", 4, true), ("The Dragon's Heart", 4, true),
            ("The Enlightened", 3, false), ("Wealth and Power", 4, true) })
        {
            var definition = DivinationRewards.Find(items.Find(name)!.Id)!;
            var gem = DivinationRewards.Create(definition, items, uniques, rolls, new Random(499))!;
            Assert.Equal(level, items.GemLevel(gem));
            Assert.Equal(corrupted, gem.Corrupted);
            Assert.EndsWith(" Support", items.GemName(gem));
        }
        foreach (var reward in DivinationRewards.All.Values)
        {
            var card = items.Find(reward.Card)!;
            Assert.Equal("DivinationCard", card.Class);
            Assert.Equal(card.MaxStack, reward.Stack);
        }
        var unresolved = DivinationRewards.All.Values.Where(r => r.Enabled
            && DivinationRewards.Create(r, items, uniques, rolls, new Random(499)) is null).Select(r => r.Card).ToArray();
        Assert.True(unresolved.Length == 0, string.Join("\n", unresolved));
    }

    [Fact]
    public async Task Only_an_open_real_NPC_window_exchanges_a_complete_set_and_the_reward_stays_saved_in_its_slot()
    {
        using var wire = new Wire();
        TestEnvironment.OpenOwnSaves("divination");
        var previous = Program.GameLogicOrNull;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var lifetime = new ModuleLifetime("divination-test");
        var context = DispatchProxy.Create<IModuleContext, HookContext>();
        ((HookContext)(object)context).Lifetime = lifetime;
        DivinationFeature.Load(context);
        var conn = wire.Conn;
        conn.Wire = ("cards", wire.Stream, wire.Ciphers);
        var items = Game.ItemsOf(conn);
        try
        {
            var cardType = Program.Items.Find("The Doctor")!;
            var card = items.Add(0, 0, 0, ItemCatalog.Create(cardType) with { StackSize = 8 });
            // A normal inventory click remains a pickup, even when the set is complete.
            await Game.PickUpItem("cards", wire.Stream, wire.Ciphers, conn,
                new InstanceItems.PickUpItem(1, card.Id, 0, 0, 0));
            Assert.Equal(cardType.Hash, Assert.Single(items.Items(PlayerInventories.Cursor)).Item.TypeHash);
            Assert.Null(DivinationFeature.TurnIn(conn));
            var tasuni = Game.Npcs.Value.Find("Metadata/NPC/Act4/Tasuni")!;
            var lilly = Game.Npcs.Value.Find("Metadata/NPC/Epilogue/LillyHideout")!;
            Assert.Contains(Game.Npcs.Value.TalksOf(tasuni), t => t.Category == "TradeDivinationCards");
            Assert.Contains(Game.Npcs.Value.TalksOf(lilly), t => t.Category == "TradeDivinationCards");
            conn.TalkingTo = 499;
            conn.Npcs[499] = new(tasuni, 10, 10);
            byte option = (byte)Game.Npcs.Value.TalksOf(tasuni).Single(t => t.Category == "TradeDivinationCards").Index;
            await Game.ChooseNpcOption("cards", wire.Stream, wire.Ciphers, conn, option);
            Assert.True(DivinationFeature.Current(conn));
            Assert.Equal(2, InstanceProtocol.KnownClientLength(0x19a));
            Assert.Equal(0x0c, InstanceDivination.Open()[2]);
            Assert.NotNull(items.Place(InstanceDivination.InventoryRow, 0, 0, Game.ShapeOf, out _));
            items.SetStackSize(InstanceDivination.InventoryRow, card.Id, 7);
            Assert.Null(DivinationFeature.TurnIn(conn));
            items.SetStackSize(InstanceDivination.InventoryRow, card.Id, 8);
            // A stale NPC/session must not consume the set.
            var npc = conn.Npcs[499];
            conn.Npcs[499] = new(tasuni, 10, 10);
            Assert.Null(DivinationFeature.TurnIn(conn));
            conn.Npcs[499] = npc;
            var wisdom = ItemCatalog.Create(Program.Items.Find("Scroll of Wisdom")!);
            for (byte y = 0; y < 5; y++)
            for (byte x = 0; x < 12; x++) items.Add(0, x, y, wisdom);
            Assert.True(await GameEvents.AnyPacket(conn, 0x19a, [1, 0x9a]));
            var reward = Assert.Single(items.Items(InstanceDivination.InventoryRow));
            Assert.NotNull(reward);
            Assert.Equal(ItemRarity.Unique, reward.Item.Rarity);
            Assert.Null(DivinationFeature.TurnIn(conn));
            Assert.Equal(60, items.Items(0).Count);
            Assert.Empty(items.Items(PlayerInventories.Cursor));
            PlayerInventories.Forget(conn.Character!);
            items = Game.ItemsOf(conn);
            Assert.Equal(reward.Item.TypeHash, Assert.Single(items.Items(InstanceDivination.InventoryRow)).Item.TypeHash);
            Assert.NotNull(items.PickUp(InstanceDivination.InventoryRow, reward.Id));
            Assert.Null(items.Place(0, 0, 0, Game.ShapeOf, out _));
            Assert.Equal(reward.Id, Assert.Single(items.Items(PlayerInventories.Cursor)).Id);
            foreach (var remove in items.Items(0).Where(i => i.Y == 0 && i.X < 2).ToArray()) items.Remove(0, remove.Id);
            Assert.NotNull(items.Place(0, 0, 0, Game.ShapeOf, out _));
            var unknown = items.Add(InstanceDivination.InventoryRow, 0, 0,
                ItemCatalog.Create(Program.Items.Find("The Void")!));
            Assert.True(await GameEvents.AnyPacket(conn, 0x19a, [1, 0x9a]));
            Assert.Equal(unknown, Assert.Single(items.Items(InstanceDivination.InventoryRow)));
            items.Remove(InstanceDivination.InventoryRow, unknown.Id);
            foreach (var remove in items.Items(0).ToArray()) items.Remove(0, remove.Id);
            Assert.Equal(8, InstanceProtocol.KnownClientLength(0xea));
            byte[] MovePacket(uint id, bool into = true) => [0, 0xea, (byte)(id >> 24), (byte)(id >> 16),
                (byte)(id >> 8), (byte)id, 0, (byte)(into ? 1 : 0)];
            var scholarType = Program.Items.Find("The Scholar")!;
            var first = items.Add(0, 0, 0, ItemCatalog.Create(scholarType) with { StackSize = 2 });
            Assert.Empty(DivinationFeature.Move(conn, new(first.Id, true, true)));
            Assert.True(await GameEvents.AnyPacket(conn, 0xea, MovePacket(first.Id)));
            Assert.Equal(2, Assert.Single(items.Items(27)).Item.StackSize);
            Assert.Null(DivinationFeature.TurnIn(conn));
            var last = items.Add(0, 0, 0, ItemCatalog.Create(scholarType));
            Assert.True(await GameEvents.AnyPacket(conn, 0xea, MovePacket(last.Id)));
            Assert.Equal(3, Assert.Single(items.Items(27)).Item.StackSize);
            // Moving is separate from the native listener's subsequent automatic exchange request.
            Assert.Equal(scholarType.Hash, Assert.Single(items.Items(27)).Item.TypeHash);
            Assert.Empty(DivinationFeature.Move(conn, new(last.Id, false, true)));
            Assert.True(await GameEvents.AnyPacket(conn, 0x19a, [1, 0x9a]));
            Assert.Equal(40, Assert.Single(items.Items(27)).Item.StackSize);
            var next = items.Add(0, 0, 0, ItemCatalog.Create(scholarType) with { StackSize = 3 });
            for (byte y = 0; y < 5; y++)
            for (byte x = 0; x < 12; x++) if (x != 0 || y != 0) items.Add(0, x, y, wisdom with { StackSize = 40 });
            // Ctrl-click takes the prior reward out using the source card's freed cell, even in a full backpack.
            Assert.NotEmpty(DivinationFeature.Move(conn, new(next.Id, false, true)));
            Assert.Equal(scholarType.Hash, Assert.Single(items.Items(27)).Item.TypeHash);
            Assert.Equal(2400, items.Items(0).Sum(i => (int)i.Item.StackSize));
            Assert.NotNull(DivinationFeature.TurnIn(conn));
            Assert.Empty(DivinationFeature.Move(conn, new(next.Id, false, false)));
            foreach (var remove in items.Items(0).ToArray()) items.Remove(0, remove.Id);
            Assert.True(await GameEvents.AnyPacket(conn, 0xea, MovePacket(next.Id, false)));
            Assert.Empty(items.Items(27));
            Assert.Equal(40, Assert.Single(items.Items(0)).Item.StackSize);
            items.Remove(0, items.Items(0)[0].Id);
            uint expanded = Program.Stats.Key(Game.ExpandedInventoryStat)!.Value;
            conn.PlayerStats[expanded] = 1;
            var extra = items.Add(PlayerInventories.ExpandedInventory, 0, 0,
                ItemCatalog.Create(scholarType) with { StackSize = 3 });
            Assert.NotEmpty(DivinationFeature.Move(conn, new(extra.Id, true, true)));
            Assert.Empty(items.Items(PlayerInventories.ExpandedInventory));
            Assert.NotEmpty(DivinationFeature.Move(conn, new(extra.Id, false, false)));
            items.Remove(0, items.Items(0)[0].Id);
            conn.PlayerStats.Remove(expanded);
            var retained = items.Add(27, 0, 0, new UniqueCatalog(Program.Items).Create("Headhunter", new Random(1))!);
            var blocked = items.Add(0, 0, 0, ItemCatalog.Create(cardType) with { StackSize = 8 });
            for (byte y = 0; y < 5; y++)
            for (byte x = 0; x < 12; x++) if (x != 0 || y != 0) items.Add(0, x, y, wisdom);
            Assert.Empty(DivinationFeature.Move(conn, new(blocked.Id, false, true)));
            Assert.Equal(retained, Assert.Single(items.Items(27)));
            Assert.Equal(blocked, items.Items(0).Single(i => i.Id == blocked.Id));
            // Ordinary vendors have no exchange capability.
            conn.Npcs[499] = new(Game.Npcs.Value.Find("Metadata/NPC/Act1/Nessa")!, 10, 10);
            await DivinationFeature.Open(conn);
            Assert.False(DivinationFeature.Current(conn));
            Assert.Empty(DivinationFeature.Move(conn, new(blocked.Id, false, true)));
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            GameEventOwners.Release("divination-test", GameEventOwners.ModuleRank);
            lifetime.Stop(ex => throw ex);
            Program.SetGameLogic(previous!);
            TestEnvironment.ReleaseOwnSaves();
        }
    }

    public class HookContext : DispatchProxy
    {
        public IModuleLifetime Lifetime = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_Lifetime" => Lifetime,
            "get_Manifest" => new ModManifest(Lifetime.Id, "0", ModKind.Feature, [], Lifetime.Id + ".dll"),
            _ => throw new NotSupportedException(method.Name),
        };
    }

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        readonly TcpClient reader;
        public NetworkStream Stream => writer.GetStream();
        public ConnectionCiphers Ciphers { get; } = new(new byte[64], false);
        public ClientState Conn { get; } = new(new(0, "1_1_town", 32, 32, 10, 10, null, []), 0, 1)
            { Instance = new(), PlayerLife = 50, Character = "Cards" + Guid.NewGuid().ToString("N"),
              LeagueModule = new(new League("cards", "Standard", false, false), new HashSet<string>(["Game", "divination-test"])) };
        public Wire()
        {
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
        }
        public void Dispose() { Conn.Closed = true; reader.Dispose(); writer.Dispose(); listener.Stop(); }
    }
}
