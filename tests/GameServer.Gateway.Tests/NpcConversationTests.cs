using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using GameServer.Modules.Atlas;

namespace GameServer.Gateway.Tests;

public sealed class NpcConversationTests
{
    [Fact]
    public void Automatic_conversations_use_catalog_priority_flags_and_visited_rows()
    {
        // Hot reload adds state to existing connections without rerunning their constructors.
        var existing = (Player)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Player));
        Assert.Empty(existing.AutomaticNpcTalkRows);
        existing.AutomaticNpcTalkRows.Add(7);
        Assert.Contains(7, existing.AutomaticNpcTalkRows);
        var catalog = NpcCatalog.Load();
        Assert.Null(catalog.Error);
        foreach (string name in new[] { "Nessa", "Bestel", "Tarkleigh" })
        {
            var npc = catalog.Find(name)!;
            Assert.NotNull(npc);
            var flags = new HashSet<string>(StringComparer.Ordinal);
            var first = catalog.NextConversationOf(npc, flags, new HashSet<int>());
            Assert.NotNull(first);
            Assert.Equal("Gossip", first.Category);
            Assert.True(first.Introduction && first.Automatic);
            Assert.NotNull(first.StartFlag);
            flags.Add(first.StartFlag);
            Assert.NotEqual(first.Row, catalog.NextConversationOf(npc, flags, new HashSet<int>())?.Row);
            flags.Clear();
            foreach (string excluded in first.ExcludedFlags) flags.Add(excluded);
            if (first.ExcludedFlags.Count > 0)
                Assert.NotEqual(first.Row, catalog.NextConversationOf(npc, flags, new HashSet<int>())?.Row);
        }
    }
    [Fact]
    public void Quest_continuations_revalidate_flags_and_respect_visited_rows()
    {
        var catalog = NpcCatalog.Load();
        foreach (var (name, expected) in new[] { ("Nessa", "Lioneye's Watch"), ("Bestel", "The Medicine Chest") })
        {
            var npc = catalog.Find(name)!;
            var flags = new HashSet<string>();
            var intro = catalog.NextConversationOf(npc, flags, new HashSet<int>())!;
            flags.Add(intro.StartFlag!);
            var visited = new HashSet<int> { intro.Row };
            if (name == "Bestel")
            {
                Assert.Null(catalog.NextConversationOf(npc, flags, visited));
                flags.Add("a1q5-ReceivedQuest");
            }
            var next = catalog.NextConversationOf(npc, flags, visited);
            Assert.NotNull(next);
            Assert.Equal(expected, next.Option);
            visited.Add(next.Row);
            Assert.NotEqual(next.Row, catalog.NextConversationOf(npc, flags, visited)?.Row);
            visited.Clear();
            flags.Add(next.StartFlag!);
            Assert.NotEqual(next.Row, catalog.NextConversationOf(npc, flags, visited)?.Row);
        }
        var tarkleigh = catalog.Find("Tarkleigh")!;
        var rewardFlags = new HashSet<string> { "TarkleighIntroduced" };
        Assert.Null(catalog.NextConversationOf(tarkleigh, rewardFlags, new HashSet<int>()));
        rewardFlags.Add("a1q1-KilledHillock");
        var reward = catalog.NextConversationOf(tarkleigh, rewardFlags, new HashSet<int>());
        Assert.NotNull(reward);
        Assert.Equal("QuestReward", reward.Category);
        Assert.Equal(0, reward.RewardOffer);
        rewardFlags.Add("a1q1-QuestRewardTaken");
        Assert.Null(catalog.NextConversationOf(tarkleigh, rewardFlags, new HashSet<int>()));
    }

    [Fact]
    public void Ctrl_and_alt_pick_the_option_the_client_tables_give_each_shortcut()
    {
        var catalog = NpcCatalog.Load();
        Assert.Null(catalog.Error);
        string? Pick(string npc, bool ctrl, bool alt, params string[] flags) =>
            catalog.ShortcutOf(catalog.Find(npc)!, NpcCatalog.ShortcutFor(ctrl, alt), flags.ToHashSet())?.Category;
        const string nessa = "Metadata/NPC/Act1/Nessa", niko = "Metadata/NPC/League/Delve/DelveMinerHideout";
        // A campaign vendor: Ctrl sells, Alt buys, both identify once the row's own flag allows it.
        Assert.Equal("SellItems", Pick(nessa, true, false));
        Assert.Equal("BuyItems", Pick(nessa, false, true));
        Assert.Null(Pick(nessa, true, true));
        Assert.Equal("IdentifyItems", Pick(nessa, true, true, "Visited-1_11"));
        Assert.Null(Pick(nessa, false, false));
        // The shortcut is the row's, not the category's: GGG's 3.23 notes name Siosa (buy) and Tasuni (cards) for Ctrl.
        Assert.Null(Pick("Metadata/NPC/Act3/Siosa", true, false));
        Assert.Equal("BuyItems", Pick("Metadata/NPC/Act3/Siosa", true, false, "a3q12-DeliveredPages"));
        Assert.Equal("TradeDivinationCards", Pick("Metadata/NPC/Act4/Tasuni", true, false));
        Assert.Equal("SellItems", Pick("Metadata/NPC/Act4/Tasuni", true, true));
        Assert.Equal("BuyItems", Pick("Metadata/NPC/Epilogue/Kirac", true, false));
        Assert.Equal("SellItems", Pick("Metadata/NPC/Epilogue/Kirac", false, true));
        Assert.Equal("CurrencyExchange", Pick("Metadata/NPC/League/Kalguur/VillageFaustusHideout", true, false));
        // Rows that share a shortcut differ in their flags: the first one the character meets.
        Assert.Equal("SellItems", Pick(niko, true, false));
        Assert.Equal("Quests", Pick(niko, true, false, "ThisIsMyHideout"));
        // An NPC with no shortcut rows.
        foreach (var (ctrl, alt) in new[] { (true, false), (false, true), (true, true) })
            Assert.Null(Pick("Metadata/NPC/Act2/Eramir", ctrl, alt));
    }

    [Fact]
    public async Task A_shortcut_click_runs_its_option_and_lists_the_options_when_nothing_answers()
    {
        var kirac = Game.Npcs.Find("Metadata/NPC/Epilogue/Kirac")!;
        var seen = new List<string>();
        bool answers = true;
        using var subscription = GameEvents.NpcOption.Add("Game", option => { seen.Add(option.Category); return Task.FromResult(answers); });
        async Task<byte[]?> Click(ushort flags, NpcCatalog.Npc? npc = null)
        {
            using var wire = new Wire();
            var player = wire.Player;
            var (x, y) = player.Walk.Position(player.Clock.Elapsed.TotalSeconds);
            lock (player.Npcs) player.Npcs[0x300] = new SpawnedNpc(npc ?? kirac, x, y);
            await Game.OnUseObject(player,
                new InstanceTransfer.UseSkillOnObject(0x300, 0x266, 1, flags), player.Clock.Elapsed.TotalSeconds);
            Assert.Equal(0x300u, player.TalkingTo);
            return wire.Sent(InstanceNpcs.Talk(0x300).Length);
        }
        byte[] options = InstanceNpcs.Talk(0x300);

        // A plain click lists the options and picks none.
        Assert.Equal(options, await Click(0x0408));
        Assert.Empty(seen);
        // Ctrl, Alt and both run the row with that shortcut; the option list is not opened over its window.
        Assert.Null(await Click(0x0408 | InstanceTransfer.CtrlFlag));
        Assert.Null(await Click(0x0408 | InstanceTransfer.AltFlag));
        Assert.Null(await Click(0x0408 | InstanceTransfer.CtrlFlag | InstanceTransfer.AltFlag));
        Assert.Equal(["BuyItems", "SellItems", "RespecAtlasPassivePointsForGold"], seen);
        // An option nothing answers (a league without its module) falls back to the list.
        answers = false;
        Assert.Equal(options, await Click(0x0408 | InstanceTransfer.CtrlFlag));
        // So does a row whose script would only end a talk that never began: Einhar's View Bestiary names a screen of a
        // system the server does not have.
        var einhar = Game.Npcs.Find("Metadata/NPC/League/Bestiary/EinharHideout")!;
        Assert.Equal(options, await Click(0x0408 | InstanceTransfer.CtrlFlag, einhar));
        await VerifyLillyGemIntroduction();
    }

    [Fact]
    public async Task Kirac_gives_first_Atlas_choices_before_late_automatic_dialogue()
    {
        using var wire = new Wire("2_11_endgame_town");
        var player = wire.Player;
        var kirac = Game.Npcs.Find("Metadata/NPC/Epilogue/Kirac")!;
        var (x, y) = player.Walk.Position(0);
        var npc = new SpawnedNpc(kirac, x, y);
        const uint id = 0x300;
        player.Npcs[id] = npc;
        player.Character.LeagueQuestFlags.UnionWith(AtlasFeature.UnlockLeagueFlags);
        var heard = new List<int>();
        var shortcuts = new List<string>();
        using var leagueFlags = GameEvents.NpcUsesLeagueFlags.Add("Game", (_, who) => who == kirac);
        using var automatic = GameEvents.NpcQuestConversation.Add("Game", AtlasFeature.KiracStarterConversation);
        using var options = GameEvents.NpcOption.Add("Game", async option =>
        {
            if (option.Category is "BuyItems" or "SellItems" or "RespecAtlasPassivePointsForGold")
            {
                shortcuts.Add(option.Category);
                return true;
            }
            heard.Add(option.Row);
            if (option.RewardOffer is int offer) return await Game.OfferNpcReward(player, offer);
            player.InNpcTalk = option.HasText;
            return option.HasText;
        });

        Assert.Null(await AtlasFeature.KiracStarterConversation(player, id, npc));
        player.Character.QuestFlags.Add(Quests.EndGameAvailable);
        await Game.OnUseObject(player, new InstanceTransfer.UseSkillOnObject(id, 0x266, 1, 0x0408),
            player.Clock.Elapsed.TotalSeconds);
        Assert.Equal([2257], heard);
        await Game.CloseNpcDialogue(player);
        Assert.Equal([2257], heard);
        Assert.Contains(1035, player.Character.LeagueQuestFlags);
        Assert.Null(player.NpcRewardOffer);
        foreach (ushort modifier in new[] { InstanceTransfer.CtrlFlag, InstanceTransfer.AltFlag,
                     (ushort)(InstanceTransfer.CtrlFlag | InstanceTransfer.AltFlag) })
            await Game.OnUseObject(player, new InstanceTransfer.UseSkillOnObject(id, 0x266, 1, (ushort)(0x0408 | modifier)),
                player.Clock.Elapsed.TotalSeconds);
        Assert.Equal(["BuyItems", "SellItems", "RespecAtlasPassivePointsForGold"], shortcuts);
        await Game.ChooseNpcOption(player, 43);
        Assert.Null(player.NpcRewardOffer);
        await Game.ChooseNpcOption(player, 4);
        await Game.CloseNpcDialogue(player);
        Assert.Equal([2257, 2258], heard);
        await Game.ChooseNpcOption(player, 43);
        Assert.Equal(70, player.NpcRewardOffer);
        Assert.Equal("Metadata/Items/Maps/MapKeyTier1", Program.Items.ByHash(Assert.Single(player.NpcRewardItems.Values).TypeHash)!.Id);
        player.Character.LeagueQuestFlags.Add(216);
        Assert.Null(await AtlasFeature.KiracStarterConversation(player, id, npc));
        player.Character.LeagueQuestFlags.Remove(216);
        player.Character.LeagueQuestFlags.Add(1037);
        Assert.Null(await AtlasFeature.KiracStarterConversation(player, id, npc));
    }

    static async Task VerifyLillyGemIntroduction()
    {
        using var f = new MinionUtilityTests.Fixture();
        var conn = f.Player;
        conn.Connection.Session = new("lilly-test", f.Stream, f.Ciphers);
        var npc = new SpawnedNpc(Game.Npcs.Find("Metadata/NPC/Act6/Lilly")!, 100, 100);
        conn.Npcs[500] = npc; conn.TalkingTo = 500;
        using var text = GameEvents.NpcOption.Add("Game", option =>
        {
            if (!option.HasText) return Task.FromResult(false);
            conn.InNpcTalk = true;
            return Task.FromResult(true);
        });
        Assert.False(await VendorsFeature.BeginLillyGemIntroduction(conn, 500, npc));
        conn.Character.QuestFlags.Add("a6q4-QuestRewardSeen");
        Assert.True(await VendorsFeature.BeginLillyGemIntroduction(conn, 500, npc));
        Assert.True(conn.Character.HasQuestFlag(GemVendorStock.LillyUnlock));
        Assert.DoesNotContain(VendorsFeature.LillyGemShopLeagueFlag, conn.Character.LeagueQuestFlags);
        await VendorsFeature.FinishLillyGemIntroduction(conn);
        Assert.Contains(VendorsFeature.LillyGemShopLeagueFlag, conn.Character.LeagueQuestFlags);
        Assert.Contains(VendorsFeature.LillyGemShopLeagueFlag, Game.PersonalStashOf(conn).LeagueQuestFlags);
        Assert.False(await VendorsFeature.BeginLillyGemIntroduction(conn, 500, npc));
        conn.Character.QuestFlags.Remove(GemVendorStock.LillyUnlock);
        var gems = GemVendorStock.For(Program.Items, Game.Npcs, Program.Skills);
        Assert.Empty(gems.Stock(npc.Npc.Id, conn.Character.ClassRow, conn.Character.QuestFlagsSnapshot()));
        Assert.NotEmpty(gems.Stock(npc.Npc.Id, conn.Character.ClassRow, VendorsFeature.VendorQuestFlags(conn)));
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
        public Wire(string area = "1_1_town")
        {
            Player = new(new(0, area, 32, 32, 10, 10, null, []), 0, 1) { Instance = new(), PlayerLife = 50 };
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
            Player.Connection.Session = new("test", Stream, Ciphers);
        }
        /// <summary>The next <paramref name="count"/> bytes the server wrote, or null when it wrote nothing.</summary>
        public byte[]? Sent(int count)
        {
            writer.Client.Shutdown(SocketShutdown.Send);
            var bytes = new byte[count];
            if (reader.GetStream().ReadAtLeast(bytes, count, throwOnEndOfStream: false) < count) return null;
            decrypt.Receive.Process(bytes);
            return bytes;
        }
        public void Dispose() { Player.Closed = true; reader.Dispose(); writer.Dispose(); listener.Stop(); }
    }
}
