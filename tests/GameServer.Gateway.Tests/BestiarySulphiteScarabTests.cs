using System.Text.Json.Nodes;
using GameServer.Core;
using League = GameServer.Core.Accounts.League;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using GameServer.Modules.Bestiary;
using GameServer.Modules.Delve;

namespace GameServer.Gateway.Tests;

public sealed class BestiarySulphiteScarabTests
{
    static readonly ushort Hash = (ushort)ClientTables.Table("WorldAreas").First(r => r.Text("Id") == "MapWorldsStrand").Int("HASH16");
    static readonly AreaLayout Layout = new(Hash, "MapWorldsStrand", 800, 800, 100, 100,
        (x, y) => x is > 0 and < 800 && y is > 0 and < 800, []);
    static readonly MapDevice.MapArea Map = new(Hash, "MapWorldsStrand", "Strand Map", 3) { Level = 70 };
    static MapDevice.Slot Slot(string leaf)
    {
        var type = Program.Items.Find("Metadata/Items/Scarabs/" + leaf)!;
        return new(type.Hash, type.Class, type.Name, type.Row, [], 0) { BaseId = type.Id };
    }
    static void Enable(Player player) => player.LeagueModule = new(new League("scarab-test", "Standard", false, false),
        new HashSet<string>(["Game", "Bestiary", "Delve"]));
    static JsonObject State(BestiaryEncounter encounter, Player player)
    {
        var json = new JsonObject();
        encounter.Describe(player, json);
        return json["bestiary"]!.AsObject();
    }

    [Fact]
    public async Task Bestiary_catalog_effects_capture_once_and_resume_the_shared_mission_after_reload()
    {
        using var game = new TriggeredSummonTests.Fixture();
        var catalog = MapRunCatalog.Load("data");
        var ownerName = "BeastOwner" + Guid.NewGuid().ToString("N");
        var instance = new InstanceState();
        var slots = new[] { Slot("ScarabBeastsNew1"), Slot("ScarabBeastsNew2"), Slot("ScarabBeastsNew3"), Slot("ScarabBeastsNew4") };
        var mods = catalog.Of(slots, Program.Items.Mods);
        instance.OpenedWith(new(ownerName, Map, mods));
        instance.AreaStats(() => Game.ComposeMapAreaStats(mods, 3, new Dictionary<uint, int>(), new HashSet<string>(), -1));
        await using var owner = await ScarabEncounterTests.Wire.Open(instance, Layout, 505, ownerName);
        await using var peer = await ScarabEncounterTests.Wire.Open(instance, Layout, 505);
        Enable(owner.Player);
        Enable(peer.Player);
        Program.InGame[owner.Player] = 0;
        Program.InGame[peer.Player] = 0;
        peer.Player.Walk.Teleport(700, 700, 0);
        instance.World.Runner = owner.Player;
        try
        {
            var tables = ClientTables.Names.Select(ClientTables.Table).ToArray();
            try
            {
                foreach (var encounters in new[]
                {
                    ClientTable.FromRows("BestiaryEncounters", []),
                    ClientTable.FromRows("BestiaryEncounters", [new Dictionary<string, object?> { ["Id"] = "unrelated" }]),
                })
                    using (ClientTables.Use([.. tables.Where(t => t.Name != "BestiaryEncounters"), encounters]))
                    {
                        BestiaryModule.LoadData();
                        Assert.Null(BestiaryTables.BlackMorrigan);
                    }
            }
            finally { BestiaryModule.LoadData(); }
            foreach (string bossArea in new[] { "MapWorldsShapersRealm", "MapAtziri1" })
            {
                var input = Slot("ScarabBeastsNew1");
                var request = new MapOpening(owner.Player, [input], false,
                    new(Map with { Id = bossArea }, catalog.Of([input], Program.Items.Mods)), "");
                MapScarabContent.Describe(request);
                BestiaryTables.Opening(request);
                Assert.DoesNotContain("Bestiary", request.HandledContent);
                Assert.Contains(request.UnhandledContent, message => message.Contains("content=Bestiary"));
            }
            string[] names = ["ScarabBeasts1", "ScarabBeasts2", "ScarabBeasts3", "ScarabBeasts4", "ScarabBeasts4a",
                "ScarabBeasts4b", "ScarabBeasts4c", "ScarabBeasts4d", "ScarabBeastsNew1", "ScarabBeastsNew2",
                "ScarabBeastsNew3", "ScarabBeastsNew4"];
            string[] families = ["Mammals", "BirdsAndReptiles", "InsectsAndArachnids", "SeaCreatures"];
            for (int i = 0; i < names.Length; i++)
            {
                var input = Slot(names[i]);
                var effect = catalog.Of([input], Program.Items.Mods);
                var request = new MapOpening(owner.Player, [input], false, new(Map, effect), "");
                MapScarabContent.Describe(request);
                BestiaryTables.Opening(request);
                Assert.Empty(request.UnhandledContent);
                var plan = BestiaryTables.PlanFor(Map.Id, 70, new Random(1), effect.Stats)!;
                int extra = i < 4 ? new[] { 1, 2, 3, 5 }[i] : i < 8 ? 4 : i == 9 ? 5 : i == 11 ? 1 : 0;
                Assert.Equal(1 + extra, plan.Reds.Count);
                Assert.Equal(i < 9, BestiaryTables.ForcesEncounter(effect.Stats));
                if (i is >= 4 and < 8) Assert.All(plan.Reds.Skip(1), r => Assert.Equal(families[i - 4], r.Family));
                Assert.All(plan.Reds, r => Assert.True(r.CapturableRow >= 0));
            }
            Assert.NotEmpty(BestiaryTables.RedKinds);
            Assert.All(BestiaryTables.RedKinds, r => { Assert.True(r.CapturableRow >= 0); Assert.False(string.IsNullOrEmpty(r.Family)); });
            var morrigan = BestiaryTables.BlackMorrigan!;
            Assert.True(morrigan.Unique);
            Assert.DoesNotContain(BestiaryTables.RedKinds, r => r.Row == morrigan.Row);
            Assert.False(BestiaryEncounter.Rolled(Map.Id, new Dictionary<uint, int>
                { [Stat.MapAdditionalRedBeasts] = 5, [Stat.MapExtraContentHasNoBaseChanceToSpawn] = 1 }, true, new Random(1)));

            var encounter = new BestiaryEncounter();
            await encounter.Place(new(owner.Player, 70, 505, []));
            var placed = State(encounter, owner.Player);
            var beasts = placed["beasts"]!.AsArray();
            Assert.Equal(7, beasts.Count(b => b!["red"]!.GetValue<bool>()));
            uint target = beasts.First(b => b!["variety"]!.GetValue<ushort>() == morrigan.Row)!["id"]!.GetValue<uint>();
            var monster = owner.Player.Area.Monsters[target];
            Assert.Equal(MonsterRarity.Unique, monster.Rarity);
            monster.Dead = true; monster.Life = 0;
            var kill = new KillContext(owner.Player, monster, monster.Variety!.Id, (100, 100));
            encounter.Killed(kill);
            encounter.Killed(kill);
            var account = Program.WorldOf(owner.Player).AccountData;
            var captures = BestiaryCaptures.Load(account);
            Assert.Equal(2, captures.Count);
            Assert.Equal(2, captures.Select(b => b.Id).Distinct().Count());
            Assert.All(captures, b => Assert.Equal(morrigan.CapturableRow, b.CapturableRow));
            string key = placed["key"]!.GetValue<string>();
            int monsterCount = owner.Player.Area.Monsters.Count;
            encounter.Clear();
            var resumed = new BestiaryEncounter();
            await resumed.Place(new(owner.Player, 70, 505, []));
            await resumed.Entered(owner.Player);
            resumed.Killed(kill);
            Assert.Equal(monsterCount, owner.Player.Area.Monsters.Count);
            var restored = State(resumed, owner.Player);
            Assert.Equal(key, restored["key"]!.GetValue<string>());
            Assert.Single(restored["beasts"]!.AsArray(), b => b!["captured"]!.GetValue<bool>());
            Assert.Equal(2, BestiaryCaptures.Load(account).Count);
            Assert.Empty(BestiaryCaptures.Load(Program.WorldOf(peer.Player).AccountData));
            await resumed.Entered(peer.Player);
            Assert.Equal(key, State(resumed, peer.Player)["key"]!.GetValue<string>());
            Assert.True(restored["received"]!.GetValue<bool>());
            Assert.False(restored["complete"]!.GetValue<bool>());
            resumed.Clear();
        }
        finally
        {
            Program.InGame.TryRemove(owner.Player, out _);
            Program.InGame.TryRemove(peer.Player, out _);
            PlayerInventories.Forget(ownerName);
            PlayerInventories.Forget(peer.Player.Character.Name!);
        }
    }

    [Fact]
    public async Task Sulphite_catalog_yields_shared_claims_capacity_fumes_and_reload_preserve_the_same_deposits()
    {
        using var game = new TriggeredSummonTests.Fixture();
        using var context = new Context("Delve");
        var catalog = MapRunCatalog.Load("data");
        string ownerName = "SulphiteOwner" + Guid.NewGuid().ToString("N");
        var instance = new InstanceState();
        var effects = catalog.Of([Slot("ScarabSulphiteNew1"), Slot("ScarabSulphiteNew2"), Slot("ScarabSulphiteNew3")], Program.Items.Mods);
        instance.OpenedWith(new(ownerName, Map, effects));
        await using var owner = await ScarabEncounterTests.Wire.Open(instance, Layout, 505, ownerName);
        await using var peer = await ScarabEncounterTests.Wire.Open(instance, Layout, 505);
        Enable(owner.Player);
        Enable(peer.Player);
        Program.InGame[owner.Player] = 0;
        instance.World.Runner = owner.Player;
        try
        {
            new DelveModule().Load(context);
            string[] names = ["ScarabSulphite1", "ScarabSulphite2", "ScarabSulphite3", "ScarabSulphite4",
                "ScarabSulphiteNew1", "ScarabSulphiteNew2", "ScarabSulphiteNew3"];
            int basic = ClientTables.Table("DelveResourcePerLevel").First(r => r.Int("AreaLevel") == 70).Int("Sulphite");
            for (int i = 0; i < names.Length; i++)
            {
                var slot = Slot(names[i]);
                var mods = catalog.Of([slot], Program.Items.Mods);
                var request = new MapOpening(owner.Player, [slot], false, new(Map, mods), "");
                MapScarabContent.Describe(request);
                foreach (var opening in GameEvents.MapOpening.For(owner.Player)) opening(request);
                Assert.Empty(request.UnhandledContent);
                int bonus = new[] { 20, 40, 60, 100, 0, 150, 0 }[i];
                Assert.Equal(basic * (100 + bonus) / 100, MapSulphite.Yield(70, mods, true));
                Assert.Equal(basic, MapSulphite.Yield(70, mods, false));
                Assert.Equal((int)decimal.Floor(basic * (100 + bonus) / 100m * .75m), MapSulphite.Yield(70, mods, true, 2));
            }
            Assert.False(MapSulphite.Eligible("MapWorldsShapersRealm"));
            Assert.False(MapSulphite.Present(new Player(Layout, 1, 1) { Instance = new() }, "MapWorldsShapersRealm", effects));
            Assert.Equal((ushort)500, MapSulphite.Credit(490, int.MaxValue, 500));
            Assert.Equal((ushort)600, MapSulphite.Credit(600, 100, 500));
            await GameEvents.RunPlayerEntered(owner.Player);
            JsonObject Read()
            {
                var json = new JsonObject();
                GameEvents.RunStateBuilding(owner.Player, json);
                return JsonNode.Parse(json.ToJsonString())!["sulphite"]!.AsObject();
            }
            var initial = Read();
            var deposits = initial["deposits"]!.AsArray();
            Assert.Equal(3, deposits.Count);
            Assert.Equal(24, owner.Player.Area.Monsters.Count(m => m.Value.Variety!.Id.Contains("DelveSpiderPacks")));
            var deposit = deposits[0]!;
            uint id = deposit["id"]!.GetValue<uint>();
            int x = deposit["x"]!.GetValue<int>(), y = deposit["y"]!.GetValue<int>();
            owner.Player.Walk.Teleport(x + 6, y, 0);
            peer.Player.Walk.Teleport(x + 10, y, 0);
            Program.InGame[peer.Player] = 0;
            await GameEvents.RunPlayerEntered(peer.Player);
            var guard = owner.Player.Area.Monsters.First(m => MapSulphite.Distance(m.Value.Walk.Position(0), (x, y)) < 60);
            guard.Value.EncounterStats = new Dictionary<uint, int> { [MapSulphite.EnragedQuantity] = 1200, [Stat.MaximumLifePlusPct] = 17 };
            var use = new InstanceTransfer.UseSkillOnObject(id, 0, 0, 0);
            await GameEvents.AnyObjectUse(owner.Player, use, 0);
            var mined = Read();
            int balance = mined["amount"]!.GetValue<int>();
            int capacity = mined["capacity"]!.GetValue<int>();
            Assert.Equal(Math.Min(capacity, MapSulphite.Yield(70, effects, true, 2)), balance);
            Assert.Equal(MapSulphite.Yield(70, effects, false, 2).ToString(), Program.WorldOf(peer.Player).CharacterData.Get(DelveModule.SulphiteKey));
            Assert.True(mined["deposits"]![0]!["mined"]!.GetValue<bool>());
            Assert.Equal(6200, guard.Value.EncounterStats[MapSulphite.EnragedQuantity]);
            // A full character still consumes the shared deposit; an absent peer receives no further share.
            peer.Player.LeavingArea = true;
            var second = deposits[1]!;
            owner.Player.Walk.Teleport(second["x"]!.GetValue<int>() + 6, second["y"]!.GetValue<int>(), 0);
            Program.WorldOf(owner.Player).CharacterData.Change(DelveModule.SulphiteKey, _ => (capacity - 1).ToString());
            await GameEvents.AnyObjectUse(owner.Player, new(second["id"]!.GetValue<uint>(), 0, 0, 0), 0);
            Assert.Equal(capacity, Read()["amount"]!.GetValue<int>());
            var third = deposits[2]!;
            owner.Player.Walk.Teleport(third["x"]!.GetValue<int>() + 6, third["y"]!.GetValue<int>(), 0);
            await GameEvents.AnyObjectUse(owner.Player, new(third["id"]!.GetValue<uint>(), 0, 0, 0), 0);
            Assert.Equal(capacity, Read()["amount"]!.GetValue<int>());
            Assert.All(Read()["deposits"]!.AsArray(), d => Assert.True(d!["mined"]!.GetValue<bool>()));
            balance = capacity;
            Assert.Equal(17, guard.Value.EncounterStats[Stat.MaximumLifePlusPct]);
            await GameEvents.AnyObjectUse(owner.Player, use, 0);
            Assert.Equal(balance, Read()["amount"]!.GetValue<int>());
            Assert.Equal(6200, guard.Value.EncounterStats[MapSulphite.EnragedQuantity]);
            context.Dispose();
            GameEventOwners.Release("Delve", GameEventOwners.ModuleRank);
            using var reload = new Context("Delve");
            new DelveModule().Load(reload);
            await GameEvents.RunPlayerEntered(owner.Player);
            await GameEvents.AnyObjectUse(owner.Player, use, 0);
            Assert.Equal(balance, Read()["amount"]!.GetValue<int>());
            Assert.Equal(deposits.Select(d => d!["id"]!.GetValue<uint>()), Read()["deposits"]!.AsArray().Select(d => d!["id"]!.GetValue<uint>()));
            Assert.Equal(24, owner.Player.Area.Monsters.Count(m => m.Value.Variety!.Id.Contains("DelveSpiderPacks")));
        }
        finally
        {
            GameEventOwners.Release("Delve", GameEventOwners.ModuleRank);
            Program.InGame.TryRemove(owner.Player, out _);
            Program.InGame.TryRemove(peer.Player, out _);
            PlayerInventories.Forget(ownerName);
            PlayerInventories.Forget(peer.Player.Character.Name!);
        }
    }

    sealed class Context(string id) : IModuleContext, IDisposable
    {
        readonly ModuleLifetime lifetime = new(id);
        public ModManifest Manifest => new(id, "1.0.0", ModKind.Feature, ["Game"], id.ToLowerInvariant() + ".dll");
        public IModuleLifetime Lifetime => lifetime;
        public string Folder => "";
        public void Log(string message) { }
        public T Settings<T>() => throw new NotSupportedException();
        public void SaveSettings<T>(T settings) => throw new NotSupportedException();
        public void ProvideLeagueRules(Func<LeagueRules> rules) { }
        public void Dispose() => lifetime.Stop(ex => throw ex);
    }
}
