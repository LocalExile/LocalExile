using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using GameServer.Modules.M_vendors;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class LeagueApiExtensionsTests
{
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

    sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        DateTimeOffset _now = start;
        public void Advance(TimeSpan delta) => _now += delta;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    [Fact]
    public void Runtime_catalog_overlays_work_across_Monster_Buff_Npc_and_ObjectTypes_and_release_on_unload()
    {
        const string owner = "test-runtime-catalog-overlays";
        const string monsterId = "Metadata/Monsters/TestOverlay/ApexGuardian";
        const string buffId = "test_overlay_league_haste";
        const ushort buffRow = 61001;
        const string npcId = "Metadata/NPC/TestOverlay/Chronicler";
        const string objectId = "Metadata/MiscellaneousObjects/TestOverlay/EncounterAltar";

        try
        {
            long monRevBefore = MonsterCatalog.OverlayRevision;
            long buffRevBefore = BuffRows.OverlayRevision;
            long npcRevBefore = NpcCatalog.OverlayRevision;
            long otRevBefore = ObjectTypes.OverlayRevision;

            MonsterCatalog.RegisterOverlay(owner, new MonsterCatalog.Variety(
                Row: -1,
                Id: monsterId,
                Name: "Apex Guardian",
                BaseType: "Metadata/Monsters/Monster",
                AnimatedObject: null,
                Components: MonsterObject.BaseComponents,
                LifeMultiplier: 250,
                BossHealthBar: true));

            BuffRows.RegisterOverlay(owner, new BuffRows.Row(
                Index: buffRow,
                Id: buffId,
                IsRecovery: false,
                HasU82: false,
                GrantedStats: [Stat.BaseMovementVelocityPlusPct],
                MergeMode: 0,
                Limit: 1,
                Invisible: false));

            const int npcRow = 59001;
            NpcCatalog.RegisterOverlay(owner, new NpcCatalog.Npc(
                Row: npcRow,
                Id: npcId,
                Name: "Test Chronicler",
                Components: NpcObject.BaseComponents,
                StateMachine: null,
                TransitionState: 1));
            NpcCatalog.RegisterTalkOverlay(owner, new NpcCatalog.Talk(
                Row: 59501,
                Npc: npcRow,
                Index: 42,
                Option: "Open Archive",
                Category: "Gossip",
                Script: "",
                TextAudio: []));

            ObjectTypes.RegisterOverlay(owner, new ObjectTypes.TypeOverlay(
                Type: objectId,
                Components: ["Positioned", "Animated", "Transitionable"],
                TransitionStates: 3,
                ObjectSize: 4,
                Blocking: true,
                Model: "Metadata/MiscellaneousObjects/TestOverlay/Altar.ao"));

            Assert.True(MonsterCatalog.OverlayRevision > monRevBefore);
            Assert.True(BuffRows.OverlayRevision > buffRevBefore);
            Assert.True(NpcCatalog.OverlayRevision > npcRevBefore);
            Assert.True(ObjectTypes.OverlayRevision > otRevBefore);

            var monster = Game.Monsters.Find(monsterId);
            Assert.NotNull(monster);
            Assert.Equal("Apex Guardian", monster!.Name);
            Assert.Equal(monster, Game.Monsters.Find(monster.Row.ToString()));
            Assert.True(monster.Supported);

            Assert.Equal(buffRow, BuffRows.Find(buffId));
            Assert.NotNull(BuffRows.Of(buffRow));
            var spec = BuffRows.Spec(buffRow, source: 7, stats: null, seconds: 10);
            Assert.Equal(buffRow, spec.Row);
            Assert.NotNull(spec.U68);

            var npc = Game.Npcs.Find(npcId);
            Assert.NotNull(npc);
            Assert.Equal(npcRow, npc!.Row);
            var talk = Game.Npcs.TalkOf(npc, 42);
            Assert.NotNull(talk);
            Assert.Equal("Open Archive", talk!.Option);

            var objInfo = QuestObjectTypes.Find(objectId);
            Assert.NotNull(objInfo);
            Assert.True(objInfo!.Blocking);
            Assert.Equal(3, objInfo.Size);
            Assert.Equal(["Positioned", "Animated", "Transitionable"], objInfo.Components);

            // Reloading stock game tables must preserve active runtime overlays.
            BuffRows.LoadGameData();
            QuestObjectTypes.LoadGameData();
            Assert.Equal(buffRow, BuffRows.Find(buffId));
            Assert.NotNull(QuestObjectTypes.Find(objectId));

            // Releasing owner via GameEventOwners removes every registered overlay cleanly.
            GameEventOwners.Release(owner, GameEventOwners.ModuleRank);
            Assert.Null(Game.Monsters.Find(monsterId));
            Assert.Equal((ushort)0, BuffRows.Find(buffId));
            Assert.Null(BuffRows.Of(buffRow));
            Assert.Null(Game.Npcs.Find(npcId));
            Assert.Null(ObjectTypes.FindOverlay(objectId));
            Assert.Null(QuestObjectTypes.Find(objectId));
        }
        finally
        {
            GameEventOwners.Release(owner, GameEventOwners.ModuleRank);
        }
    }

    [Fact]
    public async Task Lifecycle_vendor_recipe_map_device_and_league_persistence_hooks_end_to_end()
    {
        const string owner = "test-league-apis-hooks";
        const string vendorsOwner = "Vendors";
        TestEnvironment.OpenOwnSaves("league-apis");
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        using var vendorsContext = new TestModuleContext(vendorsOwner);
        new VendorsModule().Load(vendorsContext);

        string suffix = Guid.NewGuid().ToString("N")[..8];
        string leagueA = "TestLeagueA_" + suffix;
        string leagueB = "TestLeagueB_" + suffix;
        string acc1 = "lacc1_" + suffix;
        string acc2 = "lacc2_" + suffix;
        string char1 = "LeagueOne" + suffix;
        string char2 = "LeagueTwo" + suffix;

        var layout = new AreaLayout(1, "1_1_1", 100, 100, 50, 50, (_, _) => true, []);
        Player MakePlayer(string account, string character, string league)
        {
            var p = new Player(layout, 1, 1)
            {
                Instance = new(),
                Character = { Name = character, Account = account, League = league, ClassRow = Program.Skills.RowOfClass((int)CharacterClass.Witch) },
                LeagueModule = new(new League(league, "Standard", false, false), new HashSet<string>([owner, vendorsOwner])),
            };
            p.Connection.Session = new("league-api-test", wire.Stream, wire.Ciphers);
            p.Connection.LockstepClient = false;
            p.Connection.PlayerSent = true;
            p.Connection.Outbox = (_, _) => Task.CompletedTask;
            p.Commands = new ChatCommands { Send = (_, _) => Task.CompletedTask, Reply = _ => Task.CompletedTask };
            return p;
        }

        var player1 = MakePlayer(acc1, char1, leagueA);
        var player2 = MakePlayer(acc2, char2, leagueA);
        var playerOtherLeague = MakePlayer(acc2, "Other" + suffix, leagueB);

        List<PlayerDiedContext> deaths = [];
        List<InstanceState> closedInstances = [];
        var customKeyType = Program.Items.Find("Metadata/Items/Currency/CurrencyIdentification")!;
        var chaosType = Program.Items.Find("Metadata/Items/Currency/CurrencyRerollRare")!;

        using var diedSub = GameEvents.PlayerDeath.Add(owner, ctx =>
        {
            deaths.Add(ctx);
            return Task.CompletedTask;
        });
        using var closeSub = InstanceEvents.InstanceClosing.Add(owner, closedInstances.Add);
        using var mapAcceptSub = GameEvents.MapDeviceAcceptsItem.Add(owner, (_, hash, _) => hash == customKeyType.Hash);
        using var vendorRecipeSub = GameEvents.VendorRecipe.Add(owner, ctx =>
        {
            // Custom recipe: 3 Scrolls of Wisdom -> 1 Chaos Orb (leaving any remainder to built-in pricing).
            var wisdoms = ctx.Offered.Where(i => i.Item.TypeHash == customKeyType.Hash && ctx.Remaining(i.Id) >= 3).ToList();
            foreach (var w in wisdoms)
            {
                while (ctx.TryConsume(w.Id, 3))
                    ctx.AddReward(ItemCatalog.Create(chaosType), "three wisdoms to chaos");
            }
        });

        try
        {
            // 1. GameEvents.PlayerDied fires after death presentation.
            player1.PlayerLife = 0;
            await Game.PlayerDied(player1, "test death");
            Assert.Single(deaths);
            Assert.Same(player1, deaths[0].Player);
            Assert.Equal(1, deaths[0].DeathCount);

            // 2. InstanceEvents.InstanceClosing fires deterministically outside the store lock on prune & remove.
            var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
            var store = new InstanceStore(clock, TimeSpan.FromMinutes(6));
            var inst1 = store.Enter(100, 1);
            store.Leave(inst1);
            bool reenteredLock = false;
            using var lockCheckSub = InstanceEvents.InstanceClosing.Add(owner, _ =>
            {
                // Calling Snapshot() acquires _instances lock; proves callback is invoked outside lock.
                var snapshot = store.Snapshot();
                Assert.NotNull(snapshot);
                reenteredLock = true;
            });
            clock.Advance(TimeSpan.FromMinutes(7));
            Assert.Equal(1, store.Prune());
            Assert.Contains(inst1, closedInstances);
            Assert.True(reenteredLock);

            // 3. GameEvents.VendorRecipe runs before built-in pricing and leaves unconsumed items to built-in rules.
            var quote = GameEvents.QuoteVendor(player1, [new InventoryItem(10, 0, 0, ItemCatalog.Create(customKeyType) with { StackSize = 4 })]);
            Assert.NotNull(quote);
            Assert.Null(quote!.Error);
            Assert.Contains("three wisdoms to chaos", quote.Recipes);
            Assert.Contains(quote.Rewards, r => r.TypeHash == chaosType.Hash);
            // 1 leftover Scroll of Wisdom falls through to normal Scroll Fragment pricing.
            Assert.True(quote.Rewards.Count >= 2);

            // 4. GameEvents.MapDeviceAcceptsItem extends MapDevice.AcceptsItem and PlayerInventories.AcceptsItem with league isolation.
            Assert.True(MapDevice.AcceptsItem(customKeyType.Hash, customKeyType.Class, player1));
            Assert.True(PlayerInventories.AcceptsItem(PlayerInventories.MapDevice, customKeyType.Hash, customKeyType.Class, player1));
            Assert.False(MapDevice.AcceptsItem(customKeyType.Hash, customKeyType.Class, playerOtherLeague));
            Assert.False(PlayerInventories.AcceptsItem(PlayerInventories.MapDevice, customKeyType.Hash, customKeyType.Class));

            // 5. IPlayerWorld.LeagueData is shared across accounts in the same league, isolated across leagues,
            //    and ISavedData.Keys / Remove work on LeagueData, AccountData, and CharacterData.
            var world1 = Program.WorldOf(player1);
            var world2 = Program.WorldOf(player2);
            var worldOther = Program.WorldOf(playerOtherLeague);

            Assert.True(world1.LeagueData.Change("test.counter", _ => "42"));
            Assert.True(world1.LeagueData.Change("test.leader", _ => char1));
            Assert.Equal("42", world2.LeagueData.Get("test.counter"));
            Assert.Equal(["test.counter", "test.leader"], world2.LeagueData.Keys("test."));
            Assert.Null(worldOther.LeagueData.Get("test.counter"));
            Assert.True(world2.LeagueData.Remove("test.leader"));
            Assert.Null(world1.LeagueData.Get("test.leader"));
            Assert.Equal(["test.counter"], world1.LeagueData.Keys("test."));

            Assert.True(world1.AccountData.Change("acc.flag", _ => "yes"));
            Assert.Equal(["acc.flag"], world1.AccountData.Keys("acc."));
            Assert.True(world1.AccountData.Remove("acc.flag"));
            Assert.Null(world1.AccountData.Get("acc.flag"));

            CharacterSaves.Write(char1, new CharacterSaves.Save([]));
            Assert.True(world1.CharacterData.Change("char.step", _ => "3"));
            Assert.Equal(["char.step"], world1.CharacterData.Keys("char."));
            Assert.True(world1.CharacterData.Remove("char.step"));
            Assert.Null(world1.CharacterData.Get("char.step"));
        }
        finally
        {
            player1.Closed = true;
            player2.Closed = true;
            playerOtherLeague.Closed = true;
            CharacterSaves.ForgetExperience(char1);
            AccountStash.Forget(AccountStash.Key(acc1, leagueA));
            AccountStash.Forget(AccountStash.Key(acc2, leagueA));
            AccountStash.Forget(AccountStash.Key(acc2, leagueB));
            AccountStash.Forget(AccountStash.LeagueKey(leagueA));
            AccountStash.Forget(AccountStash.LeagueKey(leagueB));
            GameEventOwners.Release(owner, GameEventOwners.ModuleRank);
            GameEventOwners.Release(vendorsOwner, GameEventOwners.ModuleRank);
            TestEnvironment.ReleaseOwnSaves();
        }
    }

    [Fact]
    public async Task World_encounter_facades_and_custom_standalone_portals_work_end_to_end()
    {
        const string owner = "test-world-encounter-facades";
        const string customObjectPath = "Metadata/MiscellaneousObjects/TestFacade/Obelisk";
        const string customMonsterId = "Metadata/Monsters/TestFacade/Sentinel";
        const ushort customBuffRow = 61055;

        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();

        string character = "Facade" + Guid.NewGuid().ToString("N")[..8];
        // Leave (50, 50) unwalkable to exercise FindOpenSpot radius & avoidance.
        var layout = new AreaLayout(1, "1_1_1", 100, 100, 50, 50, (x, y) => !(x == 50 && y == 50), []);
        const ulong subInstanceId = 987654321UL;
        var player = new Player(layout, 1, 1)
        {
            Instance = new(),
            InstanceNumber = subInstanceId,
            Character = { Name = character, League = "Standard", ClassRow = Program.Skills.RowOfClass((int)CharacterClass.Witch) },
            LeagueModule = new(new League("Standard", "Standard", false, false), new HashSet<string>([owner, "Game"])),
        };
        player.Connection.Session = new("facade-test", wire.Stream, wire.Ciphers);
        player.Connection.LockstepClient = false;
        player.Connection.PlayerSent = true;
        List<byte[]> outbox = [];
        player.Connection.Outbox = (bytes, _) => { outbox.Add(bytes); return Task.CompletedTask; };
        player.Commands = new ChatCommands { Send = (bytes, _) => { outbox.Add(bytes); return Task.CompletedTask; }, Reply = _ => Task.CompletedTask };

        ObjectTypes.RegisterOverlay(owner, new ObjectTypes.TypeOverlay(
            Type: customObjectPath,
            Components: ["Positioned", "Animated", "Transitionable"],
            ObjectSize: 3,
            Blocking: true,
            Model: "Metadata/MiscellaneousObjects/TestFacade/Obelisk.ao"));
        MonsterCatalog.RegisterOverlay(owner, new MonsterCatalog.Variety(
            Row: -1,
            Id: customMonsterId,
            Name: "Test Sentinel",
            BaseType: "Metadata/Monsters/Monster",
            AnimatedObject: null,
            Components: MonsterObject.BaseComponents));
        BuffRows.RegisterOverlay(owner, new BuffRows.Row(
            Index: customBuffRow,
            Id: "test_facade_empowered",
            IsRecovery: false,
            HasU82: false,
            GrantedStats: [Stat.BaseMovementVelocityPlusPct],
            MergeMode: 0,
            Limit: 1,
            Invisible: false));

        try
        {
            var world = Program.WorldOf(player);

            // 1. FindOpenSpot respects walkability, min/max radius, and avoidance list.
            var spot = world.FindOpenSpot(near: (50, 50), minRadius: 3, maxRadius: 10, avoid: [(53, 50)], minAvoidDistance: 2);
            Assert.NotNull(spot);
            Assert.NotEqual((50, 50), spot!.Value);
            Assert.True(Math.Abs(spot.Value.X - 53) >= 2 || Math.Abs(spot.Value.Y - 50) >= 2);

            // 2. SpawnWorldObjectAsync + RemoveWorldObjectAsync using runtime ObjectTypes overlay.
            uint? objId = await world.SpawnWorldObjectAsync(customObjectPath, spot.Value.X, spot.Value.Y);
            Assert.NotNull(objId);
            Assert.True(await world.RemoveWorldObjectAsync(objId!.Value, "cleanup test obelisk"));

            // 3. SpawnMonsterAsync using runtime MonsterCatalog overlay.
            uint? monsterId = await world.SpawnMonsterAsync(customMonsterId, 55, 55,
                new MonsterSpawnOptions(Level: 10, Rarity: MonsterRarity.Rare, Name: "Archon"));
            Assert.NotNull(monsterId);
            Assert.True(player.Area.Monsters.ContainsKey(monsterId!.Value));

            // 4. ApplyTimedBuffAsync wires both AreaBuffs visual and AreaStatBuffs stats for player & monster.
            var buffStats = new Dictionary<uint, int> { [Stat.BaseMovementVelocityPlusPct] = 25 };
            Assert.True(await world.ApplyTimedBuffAsync(player.Id, customBuffRow, buffStats, seconds: 5.0));
            Assert.True(player.Area.Buffs.HasRow(player.Id, customBuffRow));
            Assert.True(await world.ApplyTimedBuffAsync(monsterId.Value, customBuffRow, buffStats, seconds: 5.0));
            Assert.True(player.Area.Buffs.HasRow(monsterId.Value, customBuffRow));

            // 5. SpawnPortalAsync registers custom portal so SendSinglePortals keeps it alive, does not burn UsesLeft on
            //    a refused travel, and RemovePortalAsync cleans it up.
            uint? portalId = await world.SpawnPortalAsync(52, 52, "1_1_town",
                new PortalOptions(UsesLeft: 1, SpawnReturnPortal: true));
            Assert.NotNull(portalId);
            Assert.True(Game.HasCustomPortal(player, portalId!.Value));
            using (GameEvents.TravelRefusal.Add(owner, (_, _) => "refused by test"))
            {
                player.SetPosition(52, 52);
                Assert.True(await Game.TryUseCustomPortal(player, new InstanceTransfer.UseSkillOnObject(portalId.Value, 0x0266, 0), player.Area.Time));
                Assert.True(Game.HasCustomPortal(player, portalId.Value));
            }
            Assert.True(await Game.ResendMapPortals(player));
            Assert.True(Game.HasCustomPortal(player, portalId.Value));
            Assert.True(await world.RemovePortalAsync(portalId.Value));
            Assert.False(Game.HasCustomPortal(player, portalId.Value));

            // 6. Custom return-portal origin binds to the entered sub-area InstanceState and evicts on InstanceClosing.
            using var releaseSub = InstanceEvents.InstanceClosing.Add(owner, Game.ReleaseCustomReturnOrigin);
            Game.RecordCustomReturnOrigin(player.Layout.Hash, subInstanceId, originArea: 1, originInstance: 77, originX: 52, originY: 52);
            Assert.True(Game.HasCustomReturnOrigin(player.Layout.Hash, subInstanceId));
            await Game.SpawnCustomReturnPortal(player);
            InstanceEvents.RunInstanceClosing(player.Instance!);
            Assert.False(Game.HasCustomReturnOrigin(player.Layout.Hash, subInstanceId));
        }
        finally
        {
            player.Closed = true;
            GameEventOwners.Release(owner, GameEventOwners.ModuleRank);
        }
    }
}
