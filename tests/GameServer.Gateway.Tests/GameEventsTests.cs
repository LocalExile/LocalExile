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
            await Game.OnClientMessages([(InstanceProtocol.AreaGeneratedId, areaGen)], [], player);
            Assert.Equal(1, readyCalls);
            Assert.True(readySawPlayerSent);
            await Game.OnClientMessages([(InstanceProtocol.AreaGeneratedId, areaGen)], [], player);
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
}
