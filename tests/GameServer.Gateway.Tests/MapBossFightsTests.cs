namespace GameServer.Gateway.Tests;

/// <summary>Maven invitations, recipe-arena phases and AtlasVoidstoneSlots (21-hideouts.md §5).</summary>
public sealed class MapBossFightsTests
{
    static MapBossFights Fights => MapBossFights.Current;

    [Fact]
    public void InvitationWitnessesAreMavenFightsAreasOrAtlasMapBosses()
    {
        var formed = Device.Check([Of(4125)], out string why);
        Assert.True(formed is not null, why);
        Assert.Equal("MavenHub", formed!.Area.Id);
        Assert.Equal(4125, formed.ItemRow);
        var bosses = Fights.Witnesses(4125);
        Assert.Equal(4, bosses.Count);
        Assert.Contains("Metadata/Monsters/AtlasBosses/HydraBoss", bosses);
        Assert.Contains("Metadata/Monsters/AtlasBosses/MinotaurBoss", bosses);
        Assert.Contains("Metadata/Monsters/AtlasBosses/PhoenixBoss", bosses);
        Assert.Contains("Metadata/Monsters/AtlasBosses/ChimeraBoss", bosses);

        var atlas = Fights.InvitationOf(4131);
        Assert.NotNull(atlas);
        Assert.Equal(3, atlas!.Count);
        Assert.Empty(atlas.Bosses);
        Assert.Equal(3, Fights.Witnesses(4131).Count);
    }

    [Fact]
    public void RecipePhasesHoldTheLastBossAndVoidstonesUnlockFromThoseKills()
    {
        Assert.Equal("Metadata/Monsters/MavenBoss/MavenBrainBoss", Fights.PhaseOf("MavenBoss")!.Last);
        Assert.Equal("Metadata/Monsters/AtlasExiles/AtlasExile5", Fights.PhaseOf("AtlasExilesBoss5")!.Last);
        Assert.Equal("Metadata/Monsters/Atziri/Atziri", Fights.PhaseOf("MapAtziri1")!.Last);
        Assert.True(MapBossFights.StormsWith(MapBossFights.WalkingSirus));

        var maven = new PackCatalog.AreaPacks("MavenBoss", 84, [], new Dictionary<string, PackCatalog.Pack>(), [0, 1]);
        var held = Fights.HoldFinal(maven, row => row == 1
            ? "Metadata/Monsters/MavenBoss/MavenBrainBoss" : "Metadata/Monsters/MavenBoss/TheMaven");
        Assert.Contains(1, held.Absent);
        Assert.Equal("Metadata/Monsters/MavenBoss/MavenBrainBoss",
            Fights.FormsAfter("MavenBoss", "Metadata/Monsters/MavenBoss/TheMaven", _ => false));
        Assert.Null(Fights.FormsAfter("MavenBoss", "Metadata/Monsters/MavenBoss/TheMaven", _ => true));

        var empty = new HashSet<int>();
        var none = new HashSet<string>(StringComparer.Ordinal);
        Assert.Equal("Metadata/Items/AtlasUpgrades/AtlasUpgradePrimordial1",
            Fights.Resolve("Metadata/Monsters/MavenBoss/MavenBrainBoss", empty, none).Ready!.ItemId);
        Assert.Equal("Metadata/Items/AtlasUpgrades/AtlasUpgradePrimordial2",
            Fights.Resolve("Metadata/Monsters/AtlasBosses/TheShaperBossUberElder", empty, none).Ready!.ItemId);
        Assert.Equal("Metadata/Monsters/AtlasInvaders/ConsumeMonsters/ConsumeBoss",
            Fights.Resolve("Metadata/Monsters/AtlasInvaders/ConsumeMonsters/ConsumeBoss", empty, none).Part);
        Assert.Null(Fights.Resolve("Metadata/Monsters/AtlasInvaders/ConsumeMonsters/ConsumeBoss", empty, none).Ready);
        Assert.Equal("Metadata/Items/AtlasUpgrades/AtlasUpgradePrimordial3",
            Fights.Resolve("Metadata/Monsters/AtlasInvaders/CleansingMonsters/CleansingBoss", empty,
                new HashSet<string>(StringComparer.Ordinal) { "Metadata/Monsters/AtlasInvaders/ConsumeMonsters/ConsumeBoss" })
                .Ready!.ItemId);
        Assert.Equal("Metadata/Items/AtlasUpgrades/AtlasUpgradePrimordial4",
            Fights.Resolve("Metadata/Monsters/AtlasMemory/BenevolenceBoss", empty, none).Ready!.ItemId);
        Assert.Equal(0, Fights.StoneByItem("Metadata/Items/AtlasUpgrades/AtlasUpgradePrimordial1")!.Slot);
        Assert.Equal(3, Fights.StoneByItem("Metadata/Items/AtlasUpgrades/AtlasUpgradePrimordial4")!.Slot);
        Assert.Null(Fights.StoneByItem("Metadata/Items/Maps/MapWorldsBeach"));
    }

    static readonly Lazy<(MapDevice Device, IReadOnlyDictionary<int, uint> Hashes)> Loaded = new(() =>
    {
        var hashes = new Dictionary<int, uint>();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine("data", "tables", "BaseItemTypes.json")));
        int row = 0;
        foreach (var item in doc.RootElement.EnumerateArray())
            hashes[row++] = item.GetProperty("HASH32").GetUInt32();
        return (MapDevice.Load("data", r => hashes.TryGetValue(r, out uint hash) ? hash : null), hashes);
    });

    static MapDevice.Slot Of(int row) =>
        new(Loaded.Value.Hashes[row], MapDevice.MiscClass, "invitation", row, [], 0);

    static MapDevice Device => Loaded.Value.Device;
}
