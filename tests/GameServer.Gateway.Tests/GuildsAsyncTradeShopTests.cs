using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modules.CosmeticsAndPets;
using GameServer.Modules.Social;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public class GuildsAsyncTradeShopTests
{
    sealed class DrainingWire : IDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly TcpClient _writer = new();
        readonly TcpClient _reader;
        readonly Task _drain;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => _writer.GetStream();

        public DrainingWire()
        {
            _listener.Start();
            var accepting = _listener.AcceptTcpClientAsync();
            _writer.Connect(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);
            _reader = accepting.GetAwaiter().GetResult();
            _drain = Task.Run(async () =>
            {
                try
                {
                    var buf = new byte[65536];
                    var stream = _reader.GetStream();
                    while (await stream.ReadAsync(buf) > 0) { }
                }
                catch { }
            });
        }

        public void Dispose()
        {
            _writer.Dispose();
            _reader.Dispose();
            _listener.Stop();
            try { _drain.GetAwaiter().GetResult(); } catch { }
        }
    }
    static (List<byte[]> Messages, Func<byte[], string?, Task> Outbox) Capture()
    {
        var messages = new List<byte[]>();
        Task Outbox(byte[] bytes, string? log)
        {
            lock (messages) messages.Add((byte[])bytes.Clone());
            return Task.CompletedTask;
        }
        return (messages, Outbox);
    }

    static ushort Id(byte[] m) => BinaryPrimitives.ReadUInt16BigEndian(m);
    static List<byte[]> WithId(List<byte[]> messages, ushort id)
    {
        lock (messages) return [.. messages.Where(m => m.Length >= 2 && Id(m) == id)];
    }

    static List<byte[]> SentSince(ConnectionCiphers ciphers, ref int cursor)
    {
        var lines = Program.HistoryFor(ciphers).Lines();
        var result = new List<byte[]>();
        for (int i = cursor; i < lines.Count; i++)
        {
            string line = lines[i];
            int arrow = line.IndexOf(" => ", StringComparison.Ordinal);
            if (arrow < 0) continue;
            int bPos = line.IndexOf(" B ", arrow, StringComparison.Ordinal);
            if (bPos < 0) continue;
            string hex = line[(bPos + 3)..].TrimEnd('…');
            result.Add(Convert.FromHexString(hex));
        }
        cursor = lines.Count;
        return result;
    }

    [Fact]
    public async Task Guild_lifecycle_chat_hideout_and_stash_reorder_broadcast_work()
    {
        string saves = Path.Combine(Path.GetTempPath(), $"localexile-guilds-{Guid.NewGuid():N}.db");
        Player? alpha = null, beta = null;
        IDisposable? subKey = null, subPeers = null, subHo = null, subTag = null, subManage = null;
        try
        {
            Saves.Open(saves);
            using var runtime = new PartyFollowerTests.Runtime();
            var instance = new InstanceState();
            using var wire = new DrainingWire();
            var store = new SqliteAccountStore(Saves.Db);
            var accA = new Account(Guid.NewGuid(), "GuildAccA", "", "", DateTimeOffset.UtcNow);
            var accB = new Account(Guid.NewGuid(), "GuildAccB", "", "", DateTimeOffset.UtcNow);
            store.AddAccount(accA);
            store.AddAccount(accB);
            store.AddCharacter(new(Guid.NewGuid(), accA.Id, "GuildAlpha", CharacterClass.Witch, 20, "Standard",
                AccountService.StartingArea, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            store.AddCharacter(new(Guid.NewGuid(), accB.Id, "GuildBeta", CharacterClass.Ranger, 20, "Standard",
                AccountService.StartingArea, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

            subKey = GameEvents.GuildStorageKey.Add("Social", GuildFeature.StorageKeyOf);
            subPeers = GameEvents.GuildPeers.Add("Social", GuildFeature.PeersOf);
            subHo = GameEvents.GuildHideoutHash.Add("Social", GuildFeature.HideoutHashOf);
            subTag = GameEvents.GuildTagQuery.Add("Social", GuildFeature.TagOf);
            subManage = GameEvents.GuildCanManageStash.Add("Social", GuildFeature.CanManageStash);

            var layout = new AreaLayout(0x235c, "1_1_town", 300, 300, 100, 100, null, []);
            alpha = new Player(layout, 1, 1) { Instance = instance, Character = { Account = accA.Name, Name = "GuildAlpha", League = "Standard" } };
            beta = new Player(layout, 1, 1) { Instance = instance, Character = { Account = accB.Name, Name = "GuildBeta", League = "Standard" } };
            alpha.PlayerLife = beta.PlayerLife = 1000;
            alpha.Connection.PlayerSent = beta.Connection.PlayerSent = true;
            alpha.Connection.Session = new("guild-alpha", wire.Stream, wire.Ciphers);
            beta.Connection.Session = new("guild-beta", wire.Stream, wire.Ciphers);
            Program.InGame.TryAdd(alpha, 0);
            Program.InGame.TryAdd(beta, 0);

            var (alphaMsgs, alphaOutbox) = Capture();
            var (betaMsgs, betaOutbox) = Capture();
            alpha.Connection.Outbox = alphaOutbox;
            beta.Connection.Outbox = betaOutbox;
            alpha.Commands = new() { Send = (_, _) => Task.CompletedTask, Reply = _ => Task.CompletedTask };
            beta.Commands = new() { Send = (_, _) => Task.CompletedTask, Reply = _ => Task.CompletedTask };

            await GuildFeature.AfterEntry(alpha);
            await GuildFeature.AfterEntry(beta);

            // 1. Create guild (0x0140), update status/rename/tag (0x014d/0x014e/0x014f).
            alphaMsgs.Clear();
            await GuildFeature.Create(alpha, "Dawnbreakers");
            var createdInfo = Assert.Single(WithId(alphaMsgs, InstanceGuilds.GuildInfoId));
            var r = new LoginReader(createdInfo);
            Assert.Equal(InstanceGuilds.GuildInfoId, r.U16());
            Assert.Equal("Dawnbreakers", r.WStr());

            alphaMsgs.Clear();
            await GuildFeature.SetTag(alpha, "DAWN");
            await GuildFeature.SetStatus(alpha, "Running maps");
            await GuildFeature.Rename(alpha, "Dawnbreakers II");
            Assert.Equal(3, WithId(alphaMsgs, InstanceGuilds.DetailsChangedId).Count);
            Assert.Equal("DAWN", GuildFeature.TagOf(alpha));

            // 2. Invite (0x0141), decline (0x0143), re-invite and accept (0x0142), promote to Officer (0x0144).
            betaMsgs.Clear();
            await GuildFeature.Invite(alpha, "GuildBeta");
            var invitePkt = Assert.Single(WithId(betaMsgs, InstanceGuilds.InviteNotifyId));
            ushort inviteIdx = BinaryPrimitives.ReadUInt16BigEndian(invitePkt.AsSpan(2));
            betaMsgs.Clear();
            await GuildFeature.DeclineInvite(beta, inviteIdx);
            Assert.Single(WithId(betaMsgs, InstanceGuilds.InviteExpiredId));

            betaMsgs.Clear();
            await GuildFeature.Invite(alpha, "GuildBeta");
            inviteIdx = BinaryPrimitives.ReadUInt16BigEndian(Assert.Single(WithId(betaMsgs, InstanceGuilds.InviteNotifyId)).AsSpan(2));
            alphaMsgs.Clear();
            betaMsgs.Clear();
            await GuildFeature.AcceptInvite(beta, inviteIdx);
            Assert.Single(WithId(betaMsgs, InstanceGuilds.GuildInfoId));
            Assert.Single(WithId(alphaMsgs, InstanceGuilds.MemberUpsertId));
            Assert.Equal(GuildFeature.StorageKeyOf(alpha), GuildFeature.StorageKeyOf(beta));

            alphaMsgs.Clear();
            betaMsgs.Clear();
            await GuildFeature.SetMemberRank(alpha, accB.Name, InstanceGuilds.RankOfficer);
            Assert.Single(WithId(alphaMsgs, InstanceGuilds.MemberUpsertId));
            Assert.Single(WithId(betaMsgs, InstanceGuilds.MemberUpsertId));

            // 3. '&' guild chat reaches both online guild members with "&GuildAlpha" sender and guild tag.
            alphaMsgs.Clear();
            betaMsgs.Clear();
            Assert.True(await GuildFeature.DeliverChat(alpha, "Hello guild!", []));
            var betaChat = Assert.Single(WithId(betaMsgs, InstanceChat.ChatLineId));
            var cr = new LoginReader(betaChat);
            Assert.Equal(InstanceChat.ChatLineId, cr.U16());
            Assert.Equal("&GuildAlpha", cr.WStr());
            Assert.Equal("Hello guild!", cr.WStr());
            Assert.Equal("DAWN", cr.WStr());

            // 4. Guild stash tab reorder (C->S 0x00cc): requires GuildStashOpen + Officer/Leader rank, broadcasts S->C 0x00cc.
            betaMsgs.Clear();
            byte[] reorderMsg = InstanceStash.EncodeReorderTabs(InstanceStash.Guild, [(1, 0), (0, 1)]);
            alpha.GuildStashOpen = false;
            await Game.OnClientMessages([(InstanceStash.ReorderTabsId, reorderMsg)], [], alpha);
            Assert.Empty(WithId(betaMsgs, InstanceStash.ReorderTabsId));
            alpha.GuildStashOpen = true;
            await Game.OnClientMessages([(InstanceStash.ReorderTabsId, reorderMsg)], [], alpha);
            var broadcast = Assert.Single(WithId(betaMsgs, InstanceStash.ReorderTabsId));
            Assert.Equal(reorderMsg, broadcast);

            // 5. Guild Hideouts list (C->S 0x1f2 GuildHideouts -> S->C 0x1f5 with guild bit 1).
            int wireCursor = Program.HistoryFor(wire.Ciphers).Lines().Count;
            await CosmeticsFeature.AnswerMtxRequest(alpha, InstanceMicrotransactions.Lists.GuildHideouts);
            var hideoutList = Assert.Single(WithId(SentSince(wire.Ciphers, ref wireCursor), InstanceMicrotransactions.HideoutsId));
            Assert.Equal((byte)1, hideoutList[2]); // guild bit set

            // 6. Persistence across stash cache eviction and leave/kick (0x0145 -> 0x0149 / 0x014b).
            AccountStash.Forget("__guilds__");
            Assert.Contains("Dawnbreakers II", AccountStash.For("__guilds__", Program.Items).ModuleData("social.guilds.v1") ?? "");
            alphaMsgs.Clear();
            betaMsgs.Clear();
            await GuildFeature.LeaveOrKick(alpha, accB.Name);
            Assert.Single(WithId(betaMsgs, InstanceGuilds.GuildLeftId));
            Assert.Single(WithId(alphaMsgs, InstanceGuilds.MemberRemoveId));
            Assert.Null(GuildFeature.StorageKeyOf(beta));

            // 7. Guild Hideout selection (C->S 0x254 bit 15) persists hideout hash on guild.
            var pickedHideout = HideoutCatalog.Hideouts[0];
            Assert.True(await GuildFeature.SelectGuildHideout(alpha, pickedHideout.Row));
            Assert.Equal(pickedHideout.Hash16, GuildFeature.HideoutHashOf(alpha));
        }
        finally
        {
            subKey?.Dispose();
            subPeers?.Dispose();
            subHo?.Dispose();
            subTag?.Dispose();
            subManage?.Dispose();
            foreach (var c in new[] { alpha, beta })
            {
                if (c is null) continue;
                c.Closed = true;
                Program.InGame.TryRemove(c, out _);
            }
            AccountStash.Forget("__guilds__");
            TestEnvironment.ReleaseSaves(saves);
        }
    }

    [Fact]
    public async Task Async_trade_stash_and_offline_local_shop_boxes_work()
    {
        string saves = Path.Combine(Path.GetTempPath(), $"localexile-trade-shop-{Guid.NewGuid():N}.db");
        Player? player = null;
        string account = "TradeShopAcc" + Guid.NewGuid().ToString("N")[..8];
        string character = "TradeShopHero" + Guid.NewGuid().ToString("N")[..6];
        try
        {
            Saves.Open(saves);
            using var runtime = new PartyFollowerTests.Runtime();
            LocalShopFeature.LoadData();
            var instance = new InstanceState();
            using var wire = new DrainingWire();
            var layout = new AreaLayout(0x235c, "1_1_town", 300, 300, 100, 100, null, []);
            player = new Player(layout, 1, 1)
            {
                Instance = instance,
                Character = { Account = account, Name = character, League = "Standard" },
            };
            player.PlayerLife = 1000;
            player.Connection.PlayerSent = true;
            player.Connection.Session = new("trade-shop", wire.Stream, wire.Ciphers);
            player.Commands = new() { Send = (_, _) => Task.CompletedTask, Reply = _ => Task.CompletedTask };

            var catalog = Program.Items;
            var chaosType = catalog.Find("Metadata/Items/Currency/CurrencyRerollRare")!;
            var inv = PlayerInventories.For(character, catalog);
            int cursor = 0;

            // 1. Open async-trade stash (0x7b 0x63) -> 0x0136 kind 2, 0x02ae earnings, 0x007b kind 0x63.
            await Game.OpenAsyncTradeStash(player);
            Assert.True(player.TradeStashOpen);
            var openSent = SentSince(wire.Ciphers, ref cursor);
            var tabList = Assert.Single(WithId(openSent, InstanceStash.TabListId));
            Assert.Equal(InstanceStash.Trade, tabList[2]);
            Assert.Single(WithId(openSent, InstanceAsyncTrade.EarningsId));
            var openUi = Assert.Single(WithId(openSent, InstanceWaypoints.OpenUiId));
            Assert.Equal(InstanceStash.OpenAsyncTradeStashUi, openUi[2]);

            // 2. C->S 0x00c7 kind 2 loads tab 0 (0x00c0 manager 0 + 0x00c8 kind 2).
            byte[] reqTab = new LoginWriter().U16(InstanceStash.RequestTabId).U16(0).U8(InstanceStash.Trade).ToArray();
            await Game.OnClientMessages([(InstanceStash.RequestTabId, reqTab)], [], player);
            var tabSent = SentSince(wire.Ciphers, ref cursor);
            Assert.Single(WithId(tabSent, 0x00c0));
            var loaded = Assert.Single(WithId(tabSent, InstanceStash.TabLoadedId));
            Assert.Equal(InstanceStash.Trade, loaded[8]);

            // 3. C->S 0x01a1 with Flag == 1 prices held cursor item before placement into trade tab.
            var cursorItem = inv.Put(PlayerInventories.Cursor,
                new InventoryItem(inv.NewItemId(), 0, 0, ItemCatalog.Create(chaosType)));
            byte[] priceCursor = new LoginWriter()
                .U16(InstanceItems.SetItemNoteId)
                .U32(PlayerInventories.InventoryId(PlayerInventories.Cursor))
                .U32(cursorItem.Id | 0x80000000u)
                .WStr("~price 5 chaos")
                .ToArray();
            await Game.OnClientMessages([(InstanceItems.SetItemNoteId, priceCursor)], [], player);
            Assert.Single(WithId(SentSince(wire.Ciphers, ref cursor), InstanceItems.InventoryChangeId));
            Assert.Equal("~price 5 chaos", inv.Items(PlayerInventories.Cursor).Single(i => i.Id == cursorItem.Id).Item.Note);

            // 4. C->S 0x02ac -> S->C 0x02ad full history snapshot (trailing byte 0) and C->S 0x02b0 withdraw earnings.
            var tradeStash = Game.TradeStashOf(player);
            tradeStash.RecordTradeSale(1700000000ul, chaosType.Hash, 12u, sold: true, ItemCatalog.Create(chaosType));
            tradeStash.AddTradeEarnings(chaosType.Hash, 15u);

            byte[] reqHist = new LoginWriter().U16(InstanceAsyncTrade.RequestHistoryId).ToArray();
            await Game.OnClientMessages([(InstanceAsyncTrade.RequestHistoryId, reqHist)], [], player);
            var histPkt = Assert.Single(WithId(SentSince(wire.Ciphers, ref cursor), InstanceAsyncTrade.HistoryId));
            Assert.Equal((byte)1, histPkt[2]);   // 1 sale entry
            Assert.Equal((byte)0, histPkt[^1]);  // full snapshot (isIncremental = 0)

            byte[] takeEarn = new LoginWriter().U16(InstanceAsyncTrade.TakeEarningsId)
                .U32(chaosType.Hash).U32(10u).U8(0).U8(0).ToArray();
            await Game.OnClientMessages([(InstanceAsyncTrade.TakeEarningsId, takeEarn)], [], player);
            var earnSent = SentSince(wire.Ciphers, ref cursor);
            Assert.Single(WithId(earnSent, InstanceItems.InventoryChangeId));
            Assert.Single(WithId(earnSent, InstanceAsyncTrade.EarningsId));
            Assert.Equal(5u, tradeStash.TradeEarnings().Single(b => b.CurrencyTypeHash == chaosType.Hash).Amount);
            Assert.Equal(10, inv.Items(PlayerInventories.MainInventory).Where(i => i.Item.TypeHash == chaosType.Hash).Sum(i => i.Item.StackSize));

            // 5. Offline local MTX shop: reload (0x12e), shipping quote (0x130), checkout refused (0x138),
            //    buy mystery box (0x12c) -> open mystery box (0x206) -> open salvage box (0x26e).
            await LocalShopFeature.HandleReload(player);
            var reloadSent = SentSince(wire.Ciphers, ref cursor);
            Assert.Single(WithId(reloadSent, InstanceShop.PointsId));
            Assert.Single(WithId(reloadSent, InstanceShop.PaymentPackagesResultId));
            Assert.Equal(2, WithId(reloadSent, InstanceMicrotransactions.AddedId).Count);

            await LocalShopFeature.HandleShippingQuote(player, new("US", "", "", ""));
            var quote = Assert.Single(WithId(SentSince(wire.Ciphers, ref cursor), InstanceShop.ShippingQuoteId));
            Assert.Equal("{\"cost\":0}", Encoding.UTF8.GetString(quote, 6, BinaryPrimitives.ReadUInt16BigEndian(quote.AsSpan(4))));

            await LocalShopFeature.HandlePlaceOrder(player, new("a", "b", "c", "d"));
            var orderSent = SentSince(wire.Ciphers, ref cursor);
            Assert.Single(WithId(orderSent, InstanceShop.JsonErrorId));
            Assert.Single(WithId(orderSent, InstanceShop.OrderResultId));

            await LocalShopFeature.HandleBuy(player, new("MysteryPackChaosVsOrderBox", "", 1, 0));
            var buySent = SentSince(wire.Ciphers, ref cursor);
            var grantedBoxPkt = Assert.Single(WithId(buySent, InstanceMicrotransactions.RecordUpsertId));
            Assert.Single(WithId(buySent, InstanceShop.BuyResultId));
            uint grantedBoxId = BinaryPrimitives.ReadUInt32BigEndian(grantedBoxPkt.AsSpan(9));

            await LocalShopFeature.HandleOpenMysteryBox(player, grantedBoxId);
            var openBoxSent = SentSince(wire.Ciphers, ref cursor);
            var removedBox = Assert.Single(WithId(openBoxSent, InstanceMicrotransactions.RecordRemoveId));
            Assert.Equal(grantedBoxId, BinaryPrimitives.ReadUInt32BigEndian(removedBox.AsSpan(4)));
            var rewardPkt = Assert.Single(WithId(openBoxSent, InstanceMicrotransactions.RecordUpsertId));
            Assert.Equal((byte)1, rewardPkt[^1]); // justReceived = 1
            uint rewardId = BinaryPrimitives.ReadUInt32BigEndian(rewardPkt.AsSpan(9));
            var leagueCharacter = new Player(layout, 2, 2)
            {
                Character = { Account = account, Name = "AnotherHero", League = "Hardcore" },
            };
            Assert.Contains(LocalShopFeature.LocalRecordsOf(leagueCharacter), r => r.Id == rewardId);

            await LocalShopFeature.HandleBuy(player, new("MysteryPackSalvageBox", "", 1, 0));
            uint salvageBoxId = BinaryPrimitives.ReadUInt32BigEndian(
                Assert.Single(WithId(SentSince(wire.Ciphers, ref cursor), InstanceMicrotransactions.RecordUpsertId)).AsSpan(9));

            // Bogus / unowned consumed IDs are rejected with 0x0282 without minting rewards.
            await LocalShopFeature.HandleOpenSalvageBox(player, new(salvageBoxId, [0x999999u]));
            var bogusSent = SentSince(wire.Ciphers, ref cursor);
            Assert.Single(WithId(bogusSent, InstanceShop.JsonErrorId));
            Assert.Empty(WithId(bogusSent, InstanceMicrotransactions.RecordUpsertId));

            await LocalShopFeature.HandleOpenSalvageBox(player, new(salvageBoxId, [rewardId]));
            var salvageSent = SentSince(wire.Ciphers, ref cursor);
            Assert.Equal(2, WithId(salvageSent, InstanceMicrotransactions.RecordRemoveId).Count);
            Assert.Single(WithId(salvageSent, InstanceMicrotransactions.RecordUpsertId));
        }
        finally
        {
            if (player is not null) player.Closed = true;
            AccountStash.Forget(account);
            AccountStash.Forget("trade:" + account);
            TestEnvironment.ReleaseSaves(saves);
        }
    }

    [Fact]
    public async Task Ctrl_click_with_a_price_lists_into_a_merchant_tab_saved_before_tabs_were_public()
    {
        string saves = Path.Combine(Path.GetTempPath(), $"localexile-merchant-price-{Guid.NewGuid():N}.db");
        Player? player = null;
        string account = "MerchantPriceAcc" + Guid.NewGuid().ToString("N")[..8];
        string character = "MerchantPriceHero" + Guid.NewGuid().ToString("N")[..6];
        string tradeKey = "trade:" + AccountStash.Key(account, "Standard");
        try
        {
            Saves.Open(saves);
            using var runtime = new PartyFollowerTests.Runtime();
            using var wire = new DrainingWire();
            player = new Player(new AreaLayout(0x235c, "1_1_town", 300, 300, 100, 100, null, []), 1, 1)
            {
                Instance = new InstanceState(),
                Character = { Account = account, Name = character, League = "Standard" },
            };
            player.PlayerLife = 1000;
            player.Connection.PlayerSent = true;
            player.Connection.Session = new("merchant-price", wire.Stream, wire.Ciphers);
            player.Commands = new() { Send = (_, _) => Task.CompletedTask, Reply = _ => Task.CompletedTask };

            // A merchant stash saved without the Public flag (older saves): it comes back Public, and stays so.
            Game.TradeStashOf(player);
            var old = Saves.LoadStash(tradeKey)!;
            var modules = new Dictionary<string, string>(old.ModuleData ?? []);
            modules.Remove("stash.public-tabs.v1");
            Saves.PutStash(tradeKey, old with { Tabs = [.. old.Tabs.Select(t => t with { Public = false })], ModuleData = modules });
            AccountStash.Forget(tradeKey);
            var trade = Game.TradeStashOf(player);
            Assert.True(trade.IsPublic(0));
            Assert.False(trade.SetPublic(0, false));
            Assert.True(trade.IsPublic(0));

            // The live message frames to its 28 bytes.
            byte[] live = Convert.FromHexString("01A2004000000D000D7E622F6F203120646976696E6500000A009086");
            var split = InstanceProtocol.SplitClientMessages(live, out var partial, out var unknown);
            Assert.Equal(InstanceItems.ListWithPriceId, Assert.Single(split).Id);
            Assert.Equal(28, split[0].Bytes.Length);
            Assert.Equal(new InstanceItems.ListWithPrice(false, 0x4000000d, "~b/o 1 divine", 0xa00, 0x90, 0x86),
                InstanceItems.ReadListWithPrice(split[0].Bytes));
            Assert.Empty(partial);
            Assert.Empty(unknown);

            await Game.OpenAsyncTradeStash(player);
            var inv = PlayerInventories.For(character, Program.Items);
            var chaos = inv.Put(PlayerInventories.MainInventory, new InventoryItem(inv.NewItemId(), 0, 0,
                ItemCatalog.Create(Program.Items.Find("Metadata/Items/Currency/CurrencyRerollRare")!)));
            byte[] Listing(uint inventoryId) => new LoginWriter().U16(InstanceItems.ListWithPriceId).U8(0).U32(chaos.Id)
                .WStr("~b/o 1 divine").U32(inventoryId).U8(0x90).U8(0x86).ToArray();

            // Not a merchant tab: nothing moves, the item keeps no note.
            await Game.OnClientMessages([(InstanceItems.ListWithPriceId, Listing(0x1234))], [], player);
            Assert.Null(inv.Items(PlayerInventories.MainInventory).Single(i => i.Id == chaos.Id).Item.Note);

            // Ctrl + click with the price: the item lies in the first merchant tab with its note.
            await Game.OnClientMessages([(InstanceItems.ListWithPriceId, Listing(trade.InventoryOf(0)))], [], player);
            Assert.DoesNotContain(inv.Items(PlayerInventories.MainInventory), i => i.Id == chaos.Id);
            var listed = Assert.Single(trade.Inventory(0).Items);
            Assert.Equal("~b/o 1 divine", listed.Item.Note);
        }
        finally
        {
            if (player is not null) player.Closed = true;
            AccountStash.Forget(tradeKey);
            TestEnvironment.ReleaseSaves(saves);
        }
    }
}
