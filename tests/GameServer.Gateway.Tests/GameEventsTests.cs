using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using GameServer.Modules.Chat;
using GameServer.Modules.CosmeticsAndPets;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class GameEventsTests
{
    static byte[] ChatPacket(string text) =>
        new LoginWriter().U16(InstanceChat.ChatId).WStr(text).U8(0).ToArray();

    sealed class TestModuleContext(string id) : IModuleContext, IDisposable
    {
        readonly ModuleLifetime _lifetime = new(id);
        public ModManifest Manifest => new(id, "1.0.0", ModKind.Feature, ["Game"], $"{id.ToLowerInvariant()}.dll");
        public IModuleLifetime Lifetime => _lifetime;
        public string Folder => "";
        public void Log(string message) { }
        public T Settings<T>() => throw new NotSupportedException();
        public void SaveSettings<T>(T settings) => throw new NotSupportedException();
        public void ProvideLeagueRules(Func<LeagueRules> rules) { }
        public void Dispose() => _lifetime.Stop(ex => throw ex);
    }

    [Fact]
    public async Task ClientMessage_and_ClientReady_dispatch_before_chat_and_release_cleanly()
    {
        const string owner = "test-game-events-msg-ready";
        const string chatOwner = "Chat";
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        using var chatContext = new TestModuleContext(chatOwner);
        new ChatModule().Load(chatContext);

        string name = "Events" + Guid.NewGuid().ToString("N");
        var layout = new AreaLayout(1, "1_1_1", 100, 100, 50, 50, (_, _) => true, [(1u, new AreaTransitionObject(1, 1, 1))]);
        var player = new Player(layout, 1, 1)
        {
            Instance = new(),
            Character = { Name = name },
            LeagueModule = new(new League("events-test", "Standard", false, false), new HashSet<string>([owner, chatOwner])),
        };
        player.Connection.Session = new("events-test", wire.Stream, wire.Ciphers);
        player.Connection.LockstepClient = false;
        using var reading = new CancellationTokenSource();
        var inbox = new ClientInbox(player.Connection.Performance);
        player.Frames.Attach(player, inbox, reading);

        List<byte[]> outbox = [];
        List<string> replies = [];
        player.Connection.Outbox = (bytes, _) => { outbox.Add(bytes); return Task.CompletedTask; };
        player.Commands = new ChatCommands
        {
            Send = (bytes, _) => { outbox.Add(bytes); return Task.CompletedTask; },
            Reply = text => { replies.Add(text); return Task.CompletedTask; },
        };
        Program.InGame.TryAdd(player, 0);

        List<ClientMessage> seenMessages = [];
        int readyCalls = 0;
        bool readySawPlayerSent = false;
        bool suppress = false;
        int commandRuns = 0;

        using var msgSub = GameEvents.ClientMessage.Add(owner, m =>
        {
            seenMessages.Add(m);
            return Task.FromResult(suppress);
        });
        using var readySub = GameEvents.ClientReady.Add(owner, p =>
        {
            readyCalls++;
            readySawPlayerSent = p.Connection.PlayerSent;
            return Task.CompletedTask;
        });
        using var cmdSub = GameEvents.Command(owner, "pingtest", "/pingtest", (_, _) =>
        {
            commandRuns++;
            return Task.CompletedTask;
        });
        using var lvlSub = GameEvents.LevelUp.Add(owner, (_, _, _) => Task.CompletedTask);
        using var cosSub = GameEvents.CosmeticChanged.Add(owner, _ => Task.CompletedTask);

        try
        {
            // 1. Plain chat pass-through -> ClientMessage sees once, ChatModule broadcasts 0x0017 once.
            await Game.OnClientMessages([(InstanceChat.ChatId, ChatPacket("hello world"))], [], player);
            Assert.Single(seenMessages);
            Assert.Equal("hello world", seenMessages[0].Text);
            Assert.False(seenMessages[0].IsCommand);
            Assert.Single(outbox);
            Assert.Equal(InstanceChat.ChatLineId, (ushort)(outbox[0][0] << 8 | outbox[0][1]));

            // 2. Plain chat suppressed -> ClientMessage consumes it, ChatModule does not broadcast.
            outbox.Clear();
            suppress = true;
            await Game.OnClientMessages([(InstanceChat.ChatId, ChatPacket("hidden"))], [], player);
            Assert.Equal(2, seenMessages.Count);
            Assert.Empty(outbox);

            // 3. Command pass-through vs suppression.
            suppress = false;
            await Game.OnClientMessages([(InstanceChat.ChatId, ChatPacket("/pingtest"))], [], player);
            Assert.Equal(3, seenMessages.Count);
            Assert.True(seenMessages[2].IsCommand);
            Assert.Equal(1, commandRuns);

            suppress = true;
            await Game.OnClientMessages([(InstanceChat.ChatId, ChatPacket("/pingtest"))], [], player);
            Assert.Equal(4, seenMessages.Count);
            Assert.Equal(1, commandRuns);

            // 4. ClientReady fires only on first 0x007d area entry.
            byte[] areaGen = new LoginWriter().U16(InstanceProtocol.AreaGeneratedId).U32(0).U32(0).ToArray();
            await player.Frames.Run(() => Game.OnClientMessages([(InstanceProtocol.AreaGeneratedId, areaGen)], [], player));
            Assert.Equal(1, readyCalls);
            Assert.True(readySawPlayerSent);
            await player.Frames.Run(() => Game.OnClientMessages([(InstanceProtocol.AreaGeneratedId, areaGen)], [], player));
            Assert.Equal(1, readyCalls);

            // 5. GameEventOwners.Release cleans up all 4 new GameEvents.
            Assert.Equal(5, GameEventOwners.Count(owner));
            GameEventOwners.Release(owner, GameEventOwners.ModuleRank);
            Assert.Equal(0, GameEventOwners.Count(owner));
            Assert.False(await GameEvents.AnyClientMessage(player, InstanceChat.ReadChat(ChatPacket("after release"))));
        }
        finally
        {
            player.Closed = true;
            inbox.Complete();
            reading.Cancel();
            player.Frames.Detach(player);
            player.Instance.RemovePlayer(player.Id);
            Program.InGame.TryRemove(player, out _);
            GameEventOwners.Release(owner, GameEventOwners.ModuleRank);
            GameEventOwners.Release(chatOwner, GameEventOwners.ModuleRank);
        }
    }

    [Fact]
    public async Task LevelUp_and_CosmeticChanged_fire_with_expected_payloads_and_post_update_state()
    {
        const string owner = "test-game-events-level-cosmetic";
        TestEnvironment.OpenOwnSaves("game-events");
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        LocalShopFeature.LoadData();

        string character = "EvChar" + Guid.NewGuid().ToString("N");
        var layout = new AreaLayout(1, "1_1_1", 100, 100, 50, 50, (_, _) => true, []);
        var player = new Player(layout, 1, 1)
        {
            Instance = new(),
            Character = { Name = character },
            LeagueModule = new(new League("events-test", "Standard", false, false), new HashSet<string>([owner])),
        };
        player.Connection.Session = new("events-cosmetic-test", wire.Stream, wire.Ciphers);
        player.Connection.PlayerSent = true;
        player.Connection.Outbox = (_, _) => Task.CompletedTask;
        player.Commands = new ChatCommands { Send = (_, _) => Task.CompletedTask, Reply = _ => Task.CompletedTask };

        List<(int Before, int After)> levelUps = [];
        List<(CosmeticChange Change, bool SavedSlot18Present)> cosmetics = [];
        using var levelSub = GameEvents.LevelUp.Add(owner, (_, prev, next) =>
        {
            levelUps.Add((prev, next));
            return Task.CompletedTask;
        });
        using var cosSub = GameEvents.CosmeticChanged.Add(owner, change =>
        {
            cosmetics.Add((change, CharacterSaves.Cosmetics(character).ContainsKey(18)));
            return Task.CompletedTask;
        });

        try
        {
            // LevelUp: 1 -> 3 fires once; re-applying 3 does not fire.
            await Game.ApplyLevel(player, 3);
            await Game.ApplyLevel(player, 3);
            Assert.Equal([(1, 3)], levelUps);

            // CosmeticChanged (slot 18 pet equip + clear; invalid slot 0 ignored).
            var pet = Game.Mtx.Owned.First(r => r.MtxTypeHash != 0);
            await CosmeticsFeature.PickCosmetic(player, (18, pet.Id, 2));
            await CosmeticsFeature.PickCosmetic(player, (18, 0, 0));
            await CosmeticsFeature.PickCosmetic(player, (0, pet.Id, 0));
            Assert.Equal(2, cosmetics.Count);
            Assert.Equal((byte)18, cosmetics[0].Change.Slot);
            Assert.Equal(pet.Id, cosmetics[0].Change.RecordId);
            Assert.Equal(pet.MtxTypeHash, cosmetics[0].Change.MtxTypeHash);
            Assert.Equal((byte)2, cosmetics[0].Change.Option);
            Assert.False(cosmetics[0].Change.IsSkillCosmetic);
            Assert.True(cosmetics[0].SavedSlot18Present);
            Assert.Equal((byte)18, cosmetics[1].Change.Slot);
            Assert.Equal(0u, cosmetics[1].Change.RecordId);
            Assert.Equal(0u, cosmetics[1].Change.MtxTypeHash);
            Assert.False(cosmetics[1].SavedSlot18Present);
            Assert.Empty(CharacterSaves.Cosmetics(character));

            // CosmeticChanged (skill cosmetic equip + clear).
            var helmet = Program.Items.All.First(t => t.Supported && t.Class == "Helmet" && t.Components.Contains("Sockets"));
            var fireball = Program.Items.Find("Metadata/Items/Gems/SkillGemFireball")!;
            var equipped = ItemCatalog.Create(helmet, [new(SocketColour.Blue, ItemCatalog.Create(fireball))], [1]);
            Game.ItemsOf(player).Add(PlayerInventories.Helmet, 0, 0, equipped);
            var skillSlot = Game.SkillSlots(player).First(s => s.GemHash == fireball.Hash);
            var skillMtx = Game.Mtx.Owned.First(r => Game.MtxEffects.FitsSkill(r.MtxTypeHash, fireball, skillSlot.SlotType));

            await CosmeticsFeature.PickSkillCosmetic(player, (skillSlot.Key, skillMtx.Id));
            await CosmeticsFeature.PickSkillCosmetic(player, (skillSlot.Key, 0));
            Assert.Equal(4, cosmetics.Count);
            Assert.Equal(skillSlot.SlotType, cosmetics[2].Change.Slot);
            Assert.Equal(skillMtx.Id, cosmetics[2].Change.RecordId);
            Assert.Equal(skillMtx.MtxTypeHash, cosmetics[2].Change.MtxTypeHash);
            Assert.Equal(fireball.Hash, cosmetics[2].Change.GemHash);
            Assert.True(cosmetics[2].Change.IsSkillCosmetic);
            Assert.Equal(0u, cosmetics[3].Change.RecordId);
            Assert.Equal(fireball.Hash, cosmetics[3].Change.GemHash);
            Assert.True(cosmetics[3].Change.IsSkillCosmetic);
        }
        finally
        {
            player.Closed = true;
            CosmeticsFeature.RemoveDepartedPets(player);
            PlayerInventories.Forget(character);
            GameEventOwners.Release(owner, GameEventOwners.ModuleRank);
            TestEnvironment.ReleaseOwnSaves();
        }
    }

    [Fact]
    public async Task CharacterCreated_handoff_seeds_save_by_SuppressStartKit_invariant_and_queries_league_characters()
    {
        const string owner = "test-game-events-char-created";
        TestEnvironment.OpenOwnSaves("char-created");
        using var runtime = new PartyFollowerTests.Runtime();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string skipName = "Skip" + suffix;
        string keepKitName = "Keep" + suffix;
        string accountName = "acc_" + suffix;
        var testLeague = new LeagueRuntime(new League("Standard", "Standard", false, false), new HashSet<string>([owner]));

        using var sub = GameEvents.CharacterCreated.Add(owner, ctx =>
        {
            if (ctx.CreationOptions.TryGetValue("skip_campaign", out var v) && v == "1")
            {
                CharacterSaves.ChangeQuestFlags(ctx.Name, ["EndGameAvailable"], on: true);
                CharacterSaves.AddWaypoints(ctx.Name, ["2_11_endgame_town"]);
                ctx.StartingAreaId = "2_11_endgame_town";
                ctx.StartingLevel = 68;
                ctx.SuppressStartKit = true;
            }
            else if (ctx.CreationOptions.TryGetValue("suppress_only", out var s) && s == "1")
            {
                ctx.SuppressStartKit = true;
            }
            else if (ctx.CreationOptions.TryGetValue("boost_level", out var lvlText) && int.TryParse(lvlText, out int lvl))
            {
                ctx.StartingLevel = lvl;
                ctx.SuppressStartKit = false;
            }
            return Task.CompletedTask;
        });

        try
        {
            // 1. SuppressStartKit = true + StartingLevel = 68 -> pre-seeds empty save (StartWeapon=null, Tutorial=null, XP=L68).
            var skipReply = await GatewayControl.HandleCharacterCreatedAsync(new ControlRequest
            {
                Op = "character_created",
                Account = accountName,
                CharacterId = Guid.NewGuid(),
                Character = skipName,
                Class = (int)GameServer.Core.Accounts.CharacterClass.Ranger,
                League = "Standard",
                CreationOptions = new() { ["skip_campaign"] = "1" },
            }, testLeague);
            Assert.True(skipReply.Ok);
            Assert.Equal("2_11_endgame_town", skipReply.StartingAreaId);
            Assert.Equal(68, skipReply.StartingLevel);
            Assert.True(skipReply.SuppressStartKit);

            var skipSave = CharacterSaves.Load(skipName);
            Assert.NotNull(skipSave);
            Assert.Null(skipSave!.StartWeapon);
            Assert.Null(skipSave.Tutorial);
            Assert.Equal(CharacterStats.ExperienceFor(68), skipSave.Experience);
            Assert.Contains("EndGameAvailable", skipSave.QuestFlags ?? []);
            Assert.Contains("2_11_endgame_town", skipSave.Waypoints ?? []);
            var skipInv = PlayerInventories.For(skipName, Program.Items);
            Assert.Null(skipInv.StartWeapon);
            Assert.Empty(skipInv.Items(PlayerInventories.MainInventory));
            Assert.Empty(skipInv.Items(PlayerInventories.Flask));

            // 2. SuppressStartKit = false + StartingLevel = 10 -> does NOT pre-seed save so StartKit still runs on first login.
            var keepReply = await GatewayControl.HandleCharacterCreatedAsync(new ControlRequest
            {
                Op = "character_created",
                Account = accountName,
                CharacterId = Guid.NewGuid(),
                Character = keepKitName,
                Class = (int)GameServer.Core.Accounts.CharacterClass.Witch,
                League = "Standard",
                CreationOptions = new() { ["boost_level"] = "10" },
            }, testLeague);
            Assert.True(keepReply.Ok);
            Assert.Equal(10, keepReply.StartingLevel);
            Assert.False(keepReply.SuppressStartKit);
            Assert.Null(CharacterSaves.Load(keepKitName));
            Assert.Equal(10, CharacterSaves.LevelOf(null, 10));

            // 2b. SuppressStartKit = true with no StartingLevel must not inherit stale in-memory UnsavedExperience.
            string recycledName = "Recyc" + suffix;
            CharacterSaves.AddExperience(recycledName, 0, _ => 999999u);
            var recycReply = await GatewayControl.HandleCharacterCreatedAsync(new ControlRequest
            {
                Op = "character_created",
                Account = accountName,
                CharacterId = Guid.NewGuid(),
                Character = recycledName,
                Class = (int)GameServer.Core.Accounts.CharacterClass.Witch,
                League = "Standard",
                CreationOptions = new() { ["suppress_only"] = "1" },
            }, testLeague);
            Assert.True(recycReply.Ok);
            Assert.Null(CharacterSaves.Load(recycledName)?.Experience);
            PlayerInventories.Forget(recycledName);
            CharacterSaves.ForgetExperience(recycledName);

            // 3. League/SSF/HC-death isolation on CharacterSaves.GetLeagueCharacters.
            var store = new GameServer.Storage.SqliteAccountStore(Saves.Db);
            var svc = new GameServer.Core.Accounts.AccountService(store);
            var acc = svc.Register(accountName, "pw1234").Value!;
            var stdAlive = svc.CreateCharacter(acc.Id, "StdAlive" + suffix, GameServer.Core.Accounts.CharacterClass.Witch, "Standard", 50).Value!;
            _ = svc.CreateCharacter(acc.Id, "SsfAlive" + suffix, GameServer.Core.Accounts.CharacterClass.Ranger, "SSF Standard", 60).Value!;
            var voidDead = svc.CreateCharacter(acc.Id, "VoidDead" + suffix, GameServer.Core.Accounts.CharacterClass.Marauder, "Void", 70).Value!;
            store.UpdateCharacter(voidDead with { HardcoreDeath = new("Hardcore", DateTimeOffset.UtcNow, PermanentlyDead: true) });

            var stdChars = CharacterSaves.GetLeagueCharacters(accountName, "Standard");
            Assert.Single(stdChars);
            Assert.Equal(stdAlive.Id, stdChars[0].Id);
            Assert.Equal(50, stdChars[0].Level);
            Assert.Single(CharacterSaves.GetLeagueCharacters(accountName, "SSF Standard"));
            Assert.Empty(CharacterSaves.GetLeagueCharacters(accountName, "Void", includeDead: false));
            Assert.Single(CharacterSaves.GetLeagueCharacters(accountName, "Void", includeDead: true));

            // 4. GameEventOwners.Release unsubscribes cleanly.
            Assert.Equal(1, GameEventOwners.Count(owner));
            GameEventOwners.Release(owner, GameEventOwners.ModuleRank);
            Assert.Equal(0, GameEventOwners.Count(owner));
        }
        finally
        {
            PlayerInventories.Forget(skipName);
            PlayerInventories.Forget(keepKitName);
            CharacterSaves.ForgetExperience(skipName);
            CharacterSaves.ForgetExperience(keepKitName);
            GameEventOwners.Release(owner, GameEventOwners.ModuleRank);
            TestEnvironment.ReleaseOwnSaves();
        }
    }

    [Fact]
    public async Task PlayerWorld_progression_facades_stash_affinity_vendor_quote_and_stash_orb_use()
    {
        const string owner = "test-player-world-facades";
        const string vendorsOwner = "Vendors";
        TestEnvironment.OpenOwnSaves("world-facades");
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        using var vendorsContext = new TestModuleContext(vendorsOwner);
        new GameServer.Modules.M_vendors.VendorsModule().Load(vendorsContext);

        string suffix = Guid.NewGuid().ToString("N")[..8];
        string accountName = "wacc_" + suffix;
        string character = "WorldHero" + suffix;
        var store = new GameServer.Storage.SqliteAccountStore(Saves.Db);
        var svc = new GameServer.Core.Accounts.AccountService(store);
        var acc = svc.Register(accountName, "pw1234").Value!;
        var created = svc.CreateCharacter(acc.Id, character, GameServer.Core.Accounts.CharacterClass.Witch, "Standard", 1).Value!;

        var layout = new AreaLayout(1, "1_1_town", 100, 100, 50, 50, (_, _) => true, []);
        var player = new Player(layout, 0, 1)
        {
            Instance = new(),
            Character = { Name = character, Id = created.Id, Account = accountName, League = "Standard", ClassRow = Program.Skills.RowOfClass((int)GameServer.Core.Accounts.CharacterClass.Witch) },
            LeagueModule = new(new League("Standard", "Standard", false, false), new HashSet<string>([owner, vendorsOwner])),
        };
        player.Connection.Session = new("world-facades-test", wire.Stream, wire.Ciphers);
        Program.PacketHistories.Add(wire.Ciphers, player.Connection.Session.Packets);
        player.Connection.PlayerSent = true;
        List<byte[]> outbox = [];
        player.Connection.Outbox = (bytes, _) => { outbox.Add(bytes); return Task.CompletedTask; };
        player.Commands = new ChatCommands { Send = (bytes, _) => { outbox.Add(bytes); return Task.CompletedTask; }, Reply = _ => Task.CompletedTask };

        List<(int Prev, int Next)> levelUps = [];
        using var lvlSub = GameEvents.LevelUp.Add(owner, (_, prev, next) => { levelUps.Add((prev, next)); return Task.CompletedTask; });

        try
        {
            var world = Program.WorldOf(player);

            // 1. Gold + 0x02aa sync.
            Assert.Equal(0u, world.Gold);
            Assert.Equal(250u, await world.AddGoldAsync(250, "test"));
            Assert.Equal(250u, world.Gold);
            Assert.Contains(player.Connection.Session.Packets.Lines(), line => line.Contains($"=> 0x{InstanceEntities.GoldId:x4}", StringComparison.OrdinalIgnoreCase));

            // 2. Quest flags strip UnearnedQuestFlags + LeagueQuestFlags.
            player.Character.UnearnedQuestFlags.Add("EndGameAvailable");
            Assert.False(world.HasQuestFlag("EndGameAvailable", earnedOnly: true));
            await world.SetQuestFlagsAsync(["EndGameAvailable"]);
            Assert.True(world.HasQuestFlag("EndGameAvailable", earnedOnly: true));
            await world.SetLeagueQuestFlagsAsync([1163]);
            Assert.Contains(1163, player.Character.LeagueQuestFlags);

            // 3. Level + XP + LevelUp event.
            Assert.True(await world.SetLevelAsync(5, "test"));
            Assert.Equal(5, player.Pools.Level);
            Assert.Equal(CharacterStats.ExperienceFor(5), player.Character.Experience);
            Assert.Equal([(1, 5)], levelUps);

            // 4. Waypoints, RefundPoints, Ascendancy awards, and LeagueCharacters.
            int addedWp = await world.GrantWaypointsAsync(["1_1_town", "1_1_2"]);
            Assert.True(addedWp >= 1);
            Assert.Equal((ushort)12, await world.SetRefundPointsAsync(12));
            Assert.Equal((ushort)12, world.RefundPoints);
            Assert.Equal(4, await world.AwardAscendancyAsync(["normal", "cruel"]));
            Assert.Single(world.GetLeagueCharacters());

            // 5. StashRules.AffinitiesOf + AccountStash.FindAffinityTab + Orb of Regret / Unmaking from stash tab.
            var stash = Game.PersonalStashOf(player);
            var regretType = Program.Items.Find("Metadata/Items/Currency/CurrencyPassiveRefund")!;
            var unmakingType = Program.Items.Find(CurrencyEffects.OrbOfUnmaking)!;
            var regretItem = ItemCatalog.Create(regretType) with { StackSize = 2 };
            Assert.NotEmpty(Program.Items.Stash.AffinitiesOf(regretType.Hash));
            int currencyTab = Enumerable.Range(0, stash.Count).First(i => stash.TypeOf(i) == "CurrencyStash");
            stash.SetAffinities(currencyTab, Program.Items.Stash.AffinitiesOf(regretType.Hash).Select(a => ((byte)a, true)));
            Assert.Equal(currencyTab, stash.FindAffinityTab(regretItem, Game.ShapeOf));

            var (placedRegret, _) = stash.Place(0, 0, 0, new InventoryItem(0, 0, 0, regretItem), Game.ShapeOf, out _)!.Value;
            var (placedUnmaking, _) = stash.Place(0, 1, 0, new InventoryItem(0, 0, 0, ItemCatalog.Create(unmakingType) with { StackSize = 2 }), Game.ShapeOf, out _)!.Value;
            await Game.UseItem(player, new InstanceItems.UseItemRequest(stash.InventoryOf(0), placedRegret.Id), "test");
            Assert.Equal((ushort)13, world.RefundPoints);
            Assert.Equal((ushort)1, stash.Inventory(0).Items.Single(i => i.Id == placedRegret.Id).Item.StackSize);
            ushort atlasBefore = Game.AtlasRespecPoints(player);
            await Game.UseItem(player, new InstanceItems.UseItemRequest(stash.InventoryOf(0), placedUnmaking.Id), "test");
            Assert.Equal((ushort)(atlasBefore + 1), Game.AtlasRespecPoints(player));
            Assert.Equal((ushort)1, stash.Inventory(0).Items.Single(i => i.Id == placedUnmaking.Id).Item.StackSize);

            // 6. GameEvents.VendorQuote wired by VendorsModule.
            var wisdom = Program.Items.Find("Metadata/Items/Currency/CurrencyIdentification")!;
            var quote = GameEvents.QuoteVendor(player, [new InventoryItem(99, 0, 0, ItemCatalog.Create(wisdom) with { StackSize = 3 })]);
            Assert.NotNull(quote);
            Assert.Null(quote!.Error);
            Assert.NotEmpty(quote.Rewards);
        }
        finally
        {
            player.Closed = true;
            PlayerInventories.Forget(character);
            CharacterSaves.ForgetExperience(character);
            AccountStash.Forget(AccountStash.Key(accountName, "Standard"));
            GameEventOwners.Release(owner, GameEventOwners.ModuleRank);
            GameEventOwners.Release(vendorsOwner, GameEventOwners.ModuleRank);
            TestEnvironment.ReleaseOwnSaves();
        }
    }
}
