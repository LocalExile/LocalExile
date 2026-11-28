using GameServer.Core.Protocol.Instance;
using GameServer.Storage;
using Xunit;

namespace GameServer.Gateway.Tests;

[Collection("Gateway")]
public sealed class VillageStoreTests
{
    [Fact]
    public void VillageStore_roundtrips_league_account_snapshot_and_settings_and_town_npcs_validate()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"localexile-village-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var db = new SaveDatabase(Path.Combine(tempDir, "characters.db"));
            var snapshot = new VillageStore.TownSnapshot(
                Town: new("alice", "Standard", 125_000, 1_700_000_000, 1_699_999_000, 500, 2_500, 777, 3, 2, 1, 0x3, "{\"ok\":true}"),
                Resources: [new(0, 420, 0.25), new(20, 9_500, 0.0)],
                FarmPlots: [new(0, 10, 0.5), new(1, 11, 0.75)],
                Workers: [new(1, "Eirik", 2, 180, 4, 0, 0, "{\"mining\":5}")],
                Recruits: [new(0, "Astrid", 3, 1_200, 140, "{\"farming\":4}")],
                Ships: [new(0, 1, 2, 1_700_000_000, 1_700_003_600, 15, 25_000, "{\"1\":500}", "[1]", 0, 0, false)],
                PortQuotas: [new(2, 0, 1, 1_000, 250, 20)],
                MapDevices: [new(0, 1, 0, 1_700_000_000, 600, 45, 6, false, 0, "[1]", "{}")],
                RewardItems: [new(0, 0, 0, 0, 0, "{\"type\":\"Currency\"}")],
                DisenchantQueue: [new(0, 99, 1_500, 500, 120.0, 40.0)]);

            VillageStore.Save(db, snapshot);
            Assert.Null(VillageStore.Load(db, "alice", "Hardcore"));
            var loaded = VillageStore.Load(db, "alice", "Standard");
            Assert.NotNull(loaded);
            Assert.Equal(125_000, loaded.Town.TreasuryGold);
            Assert.Equal(2, loaded.Resources.Count);
            Assert.Equal(2, loaded.FarmPlots.Count);
            Assert.Single(loaded.Workers);
            Assert.Single(loaded.Recruits);
            Assert.Single(loaded.Ships);
            Assert.Single(loaded.PortQuotas);
            Assert.Single(loaded.MapDevices);
            Assert.Single(loaded.RewardItems);
            Assert.Single(loaded.DisenchantQueue);

            long updatedDust = VillageStore.AddResource(db, "alice", "Standard", 20, 500);
            Assert.Equal(10_000, updatedDust);

            // Waypoint settings.json must parse cleanly with retail 3.29 defaults.
            string settingsPath = Path.Combine("Modules", "Core", "Content", "Settlers", "settings.json");
            var schema = ModSettings.Parse(File.ReadAllText(settingsPath));
            Assert.Equal(16, schema.Settings.Count);
            Assert.Equal(1.0, schema.Settings["goldBurnRateMultiplier"].Default!.GetValue<double>());
            Assert.True(schema.Settings["offlineCatchupEnabled"].Default!.GetValue<bool>());

            // Karui Shores must include Johan (VillageMayorTown) and Faustus (VillageFaustusTown with Faustus.ao).
            var karui = TownNpcs.Of(0xb4e7);
            Assert.NotNull(karui);
            Assert.Contains(karui.Npcs, n => n.Id == "Metadata/NPC/League/Kalguur/VillageMayorTown" && n.X == 686 && n.Y == 759);
            Assert.Contains(karui.Npcs, n => n.Id == "Metadata/NPC/League/Kalguur/VillageFaustusTown" && n.X == 686 && n.Y == 746 && n.Model is not null);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
