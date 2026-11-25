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

        var league = ServerLeagues.Resolve(null, "Phrecia", LeagueCatalog.Startup);
        Assert.Contains("Idols", league.Modules);
        Assert.Contains("Portals", league.Modules);
        var uniques = new UniqueCatalog(Program.Items);
        var definition = Assert.Single(uniques.All, candidate => candidate.Name == "Lone Adventurer"
            && candidate.Mods.Contains("UniqueMapRelicLoneAdventurer"));
        var idol = uniques.Create(definition, new Random(71))! with { Identified = true };
        Assert.Equal(1, ItemModStats.Sum([idol], Program.Items.Mods)[Stat.MapPortalLimit]);

        var control = await OpenAndClick(equipIdol: false, idol, league);
        var equipped = await OpenAndClick(equipIdol: true, idol, league);

        Assert.Equal((0, 6, 6, 5, true), control);
        Assert.Equal((1, 1, 1, 0, false), equipped);
    }

    async Task<(int Limit, int Opened, int Published, int Left, bool CanUseSecond)> OpenAndClick(
        bool equipIdol, Item idol, LeagueRuntime league)
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
            InstanceNumber = equipIdol ? 39002u : 39001u,
            InstanceOwner = owner,
            LeagueModule = league,
            PlayerLife = 1000,
            Connection = { PlayerSent = true, Session = new("idol replay", fixture.Stream, fixture.Ciphers) },
        };
        player.HideoutDoodads[60] = new("Map Device", 0, null, null, 100, 100, 0, 0);
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
            Assert.Equal(run.Left.Order(), published.Select(pair => pair.Value.Index));
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
                equipIdol, limit, opened = run.PortalsLeft, published = published.Length, left, canUseSecond,
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
