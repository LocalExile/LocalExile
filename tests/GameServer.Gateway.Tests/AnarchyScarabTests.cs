using GameServer.Core;
using League = GameServer.Core.Accounts.League;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using GameServer.Modules.Anarchy;

namespace GameServer.Gateway.Tests;

public sealed class AnarchyScarabTests : IDisposable
{
    readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
    readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
    readonly ModuleLifetime game = new("Game");
    readonly Context context = new();
    static readonly AreaLayout Layout = new(1, "MapWorldsStrand", 300, 300, 100, 100,
        (x, y) => x is > 0 and < 300 && y is > 0 and < 300, []);

    public AnarchyScarabTests()
    {
        Game.CombatLifetime = game;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(game));
    }

    public void Dispose()
    {
        GameEventOwners.Release("Anarchy", GameEventOwners.ModuleRank);
        context.Dispose();
        game.Stop(ex => throw ex);
        Game.CombatLifetime = previousLifetime;
        Program.SetGameLogic(previousLogic!);
    }

    static void Enable(Player player) => player.LeagueModule = new(new League("anarchy-test", "Standard", false, false),
        new HashSet<string>(["Game", "Anarchy"]));

    [Fact]
    public void Anarchy_scarabs_report_the_enabled_provider_and_retain_disabled_diagnostics()
    {
        var player = new Player(Layout, 1, 505) { Instance = new() };
        Enable(player);
        var catalog = MapRunCatalog.Load("data");
        var area = new MapDevice.MapArea(1, "MapWorldsStrand", "Strand Map", 3) { Level = 70 };
        new AnarchyModule().Load(context);
        foreach (string leaf in new[] { "ScarabAnarchy1", "ScarabAnarchy2", "ScarabAnarchy3", "ScarabAnarchy4" })
        {
            var type = Program.Items.Find("Metadata/Items/Scarabs/" + leaf)!;
            MapDevice.Slot slot = new(type.Hash, type.Class, type.Name, type.Row, [], 0) { BaseId = type.Id };
            var request = new MapOpening(player, [slot], false, new(area, catalog.Of([slot], Program.Items.Mods)), "");
            MapScarabContent.Describe(request);
            Assert.Contains(request.UnhandledContent, s => s.Contains("no enabled provider"));
            foreach (var opening in GameEvents.MapOpening.For(player)) opening(request);
            Assert.Empty(request.UnhandledContent);
        }
        var disabled = new MapOpening(player, [], false, new(area, new(new Dictionary<uint, int> { [Stat.MapNoExiles] = 1 })), "");
        foreach (var opening in GameEvents.MapOpening.For(player)) opening(disabled);
        Assert.Contains(disabled.UnhandledContent, s => s.Contains("map_no_exiles"));
        var withoutModule = new Player(Layout, 1, 505) { Instance = new() };
        withoutModule.LeagueModule = new(new League("no-anarchy", "Standard", false, false), new HashSet<string>(["Game"]));
        Assert.Empty(MapStrongboxes.RogueExiles.For(withoutModule));
    }

    [Fact]
    public async Task Strongbox_exiles_use_area_modifiers_and_exact_retained_identity_for_another_killers_rewards()
    {
        new AnarchyModule().Load(context);
        var mods = new MapMods(new Dictionary<uint, int>
        {
            [Stat.MapDisableRogueExilesFromChance] = 1,
            [Stat.MapRogueExilesAreDoubled] = 1,
            [Stat.MapRogueExilesChanceToDropAdditionalCurrencyPct] = 100,
            [Stat.MapRogueExilesMaximumLifePlusPct] = 50,
        });
        var instance = new InstanceState();
        instance.OpenedWith(new("AnarchyQa", new(1, "MapWorldsStrand", "Strand Map", 3) { Level = 70 }, mods));
        await using var owner = await ScarabEncounterTests.Wire.Open(instance, Layout, 505);
        await using var peer = await ScarabEncounterTests.Wire.Open(instance, Layout with { SpawnX = 102 }, 505);
        Enable(owner.Player); Enable(peer.Player);
        var spawn = Assert.Single(MapStrongboxes.RogueExiles.For(owner.Player));
        var ids = await spawn(owner.Player, (100, 100), 1);
        Assert.Equal(2, ids.Count);
        Assert.Equal(2, ids.Distinct().Count());
        Assert.All(ids, id =>
        {
            var monster = instance.World.Area.Monsters[id];
            Assert.Equal(MonsterRarity.Unique, monster.Rarity);
            Assert.Equal(70, monster.Level);
            Assert.True(monster.Aggro);
            Assert.True(monster.Effects[Stat.MaximumLifePlusPct] >= 50);
        });
        var first = instance.World.Area.Monsters[ids[0]];
        MonsterInfo Info(uint id, string? variety = null) => new(variety ?? first.Variety!.Id, "exile", 70, MonsterRarity.Unique, 112, 100) { ObjectId = id };
        Assert.Empty(GameEvents.RunDrops(owner.Player, Info(ids[0] + 10000))!.Items);
        Assert.Empty(GameEvents.RunDrops(owner.Player, Info(ids[0], "unrelated-variety"))!.Items);
        owner.Player.Closed = true;
        Assert.Single(GameEvents.RunDrops(peer.Player, Info(ids[0]))!.Items);
        Assert.Empty(GameEvents.RunDrops(peer.Player, Info(ids[0]))!.Items);
        Assert.Empty(GameEvents.RunDrops(peer.Player, Info(0))!.Items);
        var other = new Player(Layout, 1, 505) { Instance = new() };
        Enable(other);
        Assert.Empty(GameEvents.RunDrops(other, Info(ids[1]))!.Items);
        context.Dispose();
        Assert.Empty(await spawn(peer.Player, (100, 100), 1));
        Assert.Equal(2, instance.World.Area.Monsters.Count);
    }

    sealed class Context : IModuleContext, IDisposable
    {
        readonly ModuleLifetime lifetime = new("Anarchy");
        public ModManifest Manifest => new("Anarchy", "1.0.0", ModKind.Feature, ["Game"], "anarchy.dll");
        public IModuleLifetime Lifetime => lifetime;
        public string Folder => "";
        public void Log(string message) { }
        public T Settings<T>() => throw new NotSupportedException();
        public void SaveSettings<T>(T settings) => throw new NotSupportedException();
        public void ProvideLeagueRules(Func<LeagueRules> rules) { }
        public void Dispose() => lifetime.Stop(ex => throw ex);
    }
}
