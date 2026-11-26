using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using Xunit.Abstractions;

namespace GameServer.Gateway.Tests;

public sealed class IdolPortalLimitTests(ITestOutputHelper output)
{
    const string Layout = "MapWorldsStrand";
    const byte IdolInventory = 62;

    [Fact]
    public async Task Lone_Adventurer_opens_only_one_consumable_Strand_portal()
    {
        using var host = new ModHost("Modules", message => output.WriteLine(message), modules: true)
            { Wanted = () => new HashSet<string>(["Game", "Idols", "Portals"]) };
        host.Start(watch: false);
        Assert.True(host.IsLoaded("Idols"), "Build the Idols module before running this Gateway test.");
        Assert.True(host.IsLoaded("Portals"));
        // The loaded module gives the two stats only Idols have their effect (Endless Tide's rolled less Beyond merging
        // radius; a chance of 20 more Rogue Exiles, drawn like the game's own chances), and a map with Idols names the
        // Atlas family a stat belongs to, which carries that family's league block.
        var idolLeague = ServerLeagues.Resolve(null, "Phrecia", LeagueCatalog.Startup);
        Func<uint, int> Stats(uint stat, int value) => id => id == stat ? value : 0;
        Assert.Equal(-40, GameEvents.BeyondMergeRadiusFinal.For(idolLeague)
            .Sum(run => run(null!, Stats(Stat.MapBeyondPortalSearchRadiusPlusPctFinal, -40))));
        Assert.Equal(27.0, MapBeyond.MergeRadius(0, -40, false), 6);
        int Exiles(int chance, Random rng) => GameEvents.AdditionalRogueExiles.For(idolLeague)
            .Sum(run => run(null!, Stats(Stat.MapSpawn20AdditionalExilesChancePct, chance), rng));
        Assert.Equal(20, Exiles(100, new Random(1)));
        var untouched = new Random(1);
        Assert.Equal(0, Exiles(0, untouched));
        Assert.Equal(new Random(1).Next(), untouched.Next());
        // A chance an Idol can roll (5 to 8): one draw of 100, as the game's own chances.
        for (int seed = 0; seed < 40; seed++)
        {
            var (drawn, expected) = (new Random(seed), new Random(seed));
            Assert.Equal(expected.Next(100) < 6 ? 20 : 0, Exiles(6, drawn));
            Assert.Equal(expected.Next(), drawn.Next());
        }
        var rules = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "idols")
            .GetType("GameServer.Modules.Idols.IdolRules", throwOnError: true)!;
        MapMods With(params (uint Stat, int Value)[] idols) => (MapMods)rules.GetMethod("With")!.Invoke(null,
            [MapMods.None, idols.ToDictionary(s => s.Stat, s => s.Value)])!;
        var harvest = Game.AtlasPassiveNodes.Nodes.Where(n => AtlasStats.Family(n) == "harvest").SelectMany(n => n.Stats)
            .Select(s => With((s.Key, 1))).First(m => m.AtlasFamilies.Count > 0);
        Assert.Equal(["harvest"], harvest.AtlasFamilies);
        Assert.Equal(1, Game.ComposeMapAreaStats(harvest, 3, new Dictionary<uint, int>(), new HashSet<string>(), 0)[Stat.MapHarvestSeedsAreTier1]);
        Assert.Empty(With((Stat.MapPackSizePlusPct, 5)).AtlasFamilies);
        // The loaded module gives its item class the jewels' affix caps.
        Assert.Equal((2, 2), new ItemRolls(Program.Items).RareMaxAffixes(ItemCatalog.Create(Program.Items.Find("Minor Idol")!)));

        var league = ServerLeagues.Resolve(null, "Phrecia", LeagueCatalog.Startup);
        Assert.Contains("Idols", league.Modules);
        Assert.Contains("Portals", league.Modules);
        var uniques = new UniqueCatalog(Program.Items);
        var definition = Assert.Single(uniques.All, candidate => candidate.Name == "Lone Adventurer"
            && candidate.Mods.Contains("UniqueMapRelicLoneAdventurer"));
        var idol = uniques.Create(definition, new Random(71))! with { Identified = true };
        Assert.Equal(1, ItemModStats.Sum([idol], Program.Items.Mods)[Stat.MapPortalLimit]);

        var control = await OpenAndClick(equipIdol: false, idol, league, oneAtATime: false);
        var equipped = await OpenAndClick(equipIdol: true, idol, league, oneAtATime: false);
        var singleDisplay = await OpenAndClick(equipIdol: false, idol, league, oneAtATime: true);
        var singleDisplayWithIdol = await OpenAndClick(equipIdol: true, idol, league, oneAtATime: true);

        Assert.Equal((0, 6, 6, 5, true), control);
        Assert.Equal((1, 1, 1, 0, false), equipped);
        Assert.Equal((0, 6, 1, 5, true), singleDisplay);
        Assert.Equal((1, 1, 1, 0, false), singleDisplayWithIdol);
    }

    async Task<(int Limit, int Opened, int Published, int Left, bool CanUseSecond)> OpenAndClick(
        bool equipIdol, Item idol, LeagueRuntime league, bool oneAtATime)
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        string owner = fixture.Player.Character.Name!;
        var hideout = HideoutCatalog.Default!;
        var layout = new AreaLayout(hideout.AreaHash16, hideout.AreaId, 256, 256, 100, 100,
            (x, y) => x >= 0 && x < 256 && y >= 0 && y < 256, []);
        var player = new Player(layout, 1, 1)
        {
            Character = { Name = owner },
            Instance = new(),
            InstanceNumber = 39001u + (equipIdol ? 1u : 0u) + (oneAtATime ? 2u : 0u),
            InstanceOwner = owner,
            LeagueModule = league,
            PlayerLife = 1000,
            Connection = { PlayerSent = true, Session = new("idol replay", fixture.Stream, fixture.Ciphers) },
        };
        const uint atzoatl = 3802575025;
        var template = Assert.IsType<HideoutCatalog.Doodad>(Game.HideoutFurniture(player)?.Device);
        var device = template with { X = 100, Y = 100 };
        if (oneAtATime)
        {
            var variant = HideoutCatalog.DoodadByHash(atzoatl);
            Assert.NotNull(variant);
            device = device with { Hash = atzoatl, Type = variant.Value.Type, Model = variant.Value.Models?.FirstOrDefault(), Variation = 0 };
        }
        Assert.True(HideoutCatalog.IsPlaced(device));
        Assert.Equal(oneAtATime, Game.HideoutNextPortalMs(device) is > 0);
        player.HideoutDoodads[60] = device;
        Assert.NotNull(Game.MapDeviceHere(player));
        CharacterSaves.SetAtlasLayout(owner, Layout);
        var items = Game.ItemsOf(player);
        if (equipIdol)
        {
            Assert.Null(GameEvents.FirstInventoryRefusal(player, IdolInventory, idol));
            items.Add(IdolInventory, 0, 0, idol);
            Assert.Single(items.Items(IdolInventory));
        }

        var map = Program.Items.Find("Metadata/Items/Maps/MapKeyTier12")!;
        items.Add(PlayerInventories.MapDevice, 0, 0, ItemCatalog.Create(map) with { Rarity = ItemRarity.Rare, Identified = true });
        var clock = new PortalClock();
        var previousUpdates = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        Program.InGame.TryAdd(player, 0);
        try
        {
            await PortalsFeature.ActivateMapDevice(player, (0, 0));
            var run = MapDevice.OpenOf(owner);
            Assert.NotNull(run);
            Assert.Equal(Layout, run.Area.Id);
            Assert.Equal(12, run.Area.Tier);
            Assert.Empty(items.Items(PlayerInventories.MapDevice));
            await clock.Published.Task.WaitAsync(TimeSpan.FromSeconds(10));
            KeyValuePair<uint, (int X, int Y, int Index)>[] published;
            lock (player.MapPortals) published = [.. player.MapPortals.OrderBy(pair => pair.Value.Index)];
            Assert.Equal(run.Left.Order().Take(oneAtATime ? 1 : run.PortalsLeft), published.Select(pair => pair.Value.Index));
            var first = published[0];
            player.Walk.Teleport(first.Value.X, first.Value.Y, Game.AreaTime(player));
            Assert.True(await GameEvents.AnyObjectUse(player,
                new InstanceTransfer.UseSkillOnObject(first.Key, SkillCatalog.FirstSkillId, 1, 0), Game.AreaTime(player)));
            Assert.Equal(run.Area.Hash16, player.PendingTransfer?.Area);
            int left = MapDevice.OpenOf(owner)!.PortalsLeft;
            lock (player.MapPortals) Assert.DoesNotContain(first.Key, player.MapPortals.Keys);

            // The real click spent the first portal. Emulate Auth admitting that transfer before testing another slot.
            MapDevice.Entered(run, player.InstanceNumber + 100);
            bool canUseSecond = MapDevice.TakePortal(owner, 1, run.Run) is not null;
            int limit = run.Mods.Get(Stat.MapPortalLimit);
            output.WriteLine("IDOL_PORTALS " + JsonSerializer.Serialize(new
            {
                equipIdol, oneAtATime, limit, opened = run.PortalsLeft, published = published.Length, left, canUseSecond,
                publishedSlots = published.Select(pair => pair.Value.Index), utc = DateTimeOffset.UtcNow,
            }));
            return (limit, run.PortalsLeft, published.Length, left, canUseSecond);
        }
        finally
        {
            player.PendingTransfer = null;
            clock.ReleaseTransfer.TrySetResult();
            Game.AreaUpdateContext.Value = previousUpdates;
            Program.InGame.TryRemove(player, out _);
            MapDevice.Close(owner);
        }
    }

    sealed class PortalClock : IPlayerUpdateSchedule
    {
        public readonly TaskCompletionSource Published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseTransfer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public double Now(Player player) => 10;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            if (milliseconds == 2000) return ReleaseTransfer.Task;
            if (milliseconds == Game.MapPortalRelistMs) Published.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
