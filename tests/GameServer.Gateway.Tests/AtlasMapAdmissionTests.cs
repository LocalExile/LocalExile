using GameServer.Core;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Terrain.Generation;

namespace GameServer.Gateway.Tests;

public sealed class AtlasMapAdmissionTests
{
    [Fact]
    public void Atlas_map_admission_enforces_ordinary_and_special_map_keys()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var items = Program.Items;
        var atlas = new AtlasCatalog(items);
        var device = MapDevice.Load("data", row => items.Row(row)?.Hash, items);
        string name = f.Player.Character.Name!;

        MapDevice.Slot Slot(ItemCatalog.BaseType item) =>
            new(item.Hash, item.Class, item.Name, item.Row, [], 0) { Rarity = ItemRarity.Rare, BaseId = item.Id };

        var ordinary = atlas.Find("MapWorldsStrand")!;
        var nightmare = atlas.Find("MapWorldsAbomination")!;
        var tier16 = Slot(items.ByHash(atlas.KeyOfTier(16)!.Value)!);
        var nightmareKey = Slot(items.Find("Metadata/Items/Maps/MapKeyNightmareBoss")!);

        atlas.Select(name, ordinary);
        var strandOpen = device.Check([tier16], name, items.Mods, out _);
        Assert.NotNull(strandOpen);
        Assert.Equal(ordinary.Hash16, strandOpen.Area.Hash16);
        Assert.Equal(83, strandOpen.Area.Level);

        Assert.Null(device.Check([nightmareKey], name, items.Mods, out string wrongForOrdinary));
        Assert.Contains("cannot be used", wrongForOrdinary);

        atlas.Select(name, nightmare);
        Assert.Null(device.Check([tier16], name, items.Mods, out string wrongForNightmare));
        Assert.Contains("requires its own map item", wrongForNightmare);

        var nightmareOpen = device.Check([nightmareKey], name, items.Mods, out _);
        Assert.NotNull(nightmareOpen);
        Assert.Equal(nightmare.Hash16, nightmareOpen.Area.Hash16);
        Assert.Equal(84, nightmareOpen.Area.Level);
    }

    [Fact]
    public void MapSulphite_present_applies_native_chance_modifiers_no_base_chance_and_exclusions()
    {
        var atlas = new AtlasCatalog(Program.Items);
        var strand = atlas.Find("MapWorldsStrand")!;
        var map = new MapDevice.MapArea(strand.Hash16, strand.Id, strand.Name, 16) { Level = 83 };

        uint hitSeed = Enumerable.Range(1, 10_000).Select(i => (uint)i)
            .First(s => new LevelRandom(s, false).Next(100u) < 2);
        uint midSeed = Enumerable.Range(1, 10_000).Select(i => (uint)i)
            .First(s => new LevelRandom(s, false).Next(100u) is >= 2 and < 50);

        var layout = new AreaLayout(strand.Hash16, strand.Id, 100, 100, 50, 50, null, []);
        var standard = new LeagueRuntime(new League("sulphite-test", "Standard", false, false), new HashSet<string>(["Game"]));
        var ruthless = new LeagueRuntime(new League("sulphite-ruthless", "Ruthless", false, false) { DataSet = "HardMode" }, new HashSet<string>(["Game"]));

        bool PresentFor(uint seed, Dictionary<uint, int> stats, LeagueRuntime? league = null)
        {
            var instance = new InstanceState();
            instance.AreaStats(() => stats);
            var player = new Player(layout, 1, seed) { Instance = instance, LeagueModule = league ?? standard };
            var mods = new MapMods(stats);
            bool first = MapSulphite.Present(player, map, mods);
            Assert.Equal(first, MapSulphite.Present(player, map, mods));
            return first;
        }

        Assert.True(PresentFor(hitSeed, []));
        Assert.False(PresentFor(hitSeed, [], ruthless));
        Assert.False(PresentFor(midSeed, []));

        Assert.False(PresentFor(hitSeed, new Dictionary<uint, int>
        {
            [Stat.MapExtraContentHasNoBaseChanceToSpawn] = 1,
        }));

        Assert.True(PresentFor(midSeed, new Dictionary<uint, int>
        {
            [Stat.MapMasterMissionNikoPctChance] = 48,
        }));

        Assert.True(PresentFor(midSeed, new Dictionary<uint, int>
        {
            [Stat.MapMasterMissionNikoPctChance] = 25,
            [Stat.MapMasterMissionNikoChancePlusPct] = 100,
        }));

        Assert.False(PresentFor(hitSeed, new Dictionary<uint, int>
        {
            [Stat.MapMasterMissionNikoPctChance] = 100,
            [Stat.MapMasterMissionNikoChancePlusPct] = -150,
        }));

        Assert.False(PresentFor(hitSeed, new Dictionary<uint, int>
        {
            [Stat.MapDelveLeague] = 1,
            [Stat.AtlasKeystoneNoExtraContent] = 1,
        }));

        Assert.False(PresentFor(hitSeed, new Dictionary<uint, int>
        {
            [Stat.MapDelveLeague] = 1,
            [Stat.MapNukeEverything] = 1,
        }));

        Assert.True(PresentFor(midSeed, new Dictionary<uint, int>
        {
            [Stat.MapDelveLeague] = 1,
        }));
    }
}
