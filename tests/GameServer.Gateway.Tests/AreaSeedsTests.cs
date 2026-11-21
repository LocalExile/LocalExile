using GameServer.Core;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>A new instance's seed (AreaSeeds): a map nobody captured must not be pinned to seed 1, which is the seed
/// captures are dumped at and one the client can fail to build (MapWorldsColosseum exits on it).</summary>
public sealed class AreaSeedsTests
{
    static AreaSeeds Seeds(ushort map) => new()
    {
        Hideouts = new HashSet<ushort> { 0x1001 },
        Random = new HashSet<ushort> { 0x1002 },
        InPlaceOfOne = new Dictionary<ushort, uint> { [0x1003] = 460946050 },
        Maps = new HashSet<ushort> { map },
        Next = () => 1234567,
    };

    [Fact]
    public void A_map_with_no_capture_gets_a_random_seed_and_every_other_area_keeps_its_own()
    {
        const ushort map = 0x2725, other = 0x3001;
        var seeds = Seeds(map);

        // A map area with neither a capture nor a verified seed: random, not 1 (the seed the client may not build).
        Assert.Equal(1234567u, seeds.For(map));
        // A hideout has a fixed layout; an area the Gateway generates is random; a verified area keeps the seed it was
        // verified at.
        Assert.Equal(1u, seeds.For(0x1001));
        Assert.Equal(1234567u, seeds.For(0x1002));
        Assert.Equal(460946050u, seeds.For(0x1003));
        // An area that is no map and has nothing verified keeps seed 1, its captures' seed.
        Assert.Equal(1u, seeds.For(other));
        // The dev seed overrides everything but a hideout.
        Assert.Equal(99u, new AreaSeeds
        {
            Hideouts = new HashSet<ushort> { 0x1001 }, Maps = new HashSet<ushort> { map }, Fixed = 99, Next = () => 1234567,
        }.For(map));
        Assert.Equal(1u, new AreaSeeds { Hideouts = new HashSet<ushort> { map }, Fixed = 99 }.For(map));
    }

    [Fact]
    public void Map_seed_selection_preserves_only_usable_captures_and_the_gateway_reads_the_same_grid()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"map-seed-captures-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var world = ClientTables.Table("WorldAreas");
            var maps = ClientTables.Table("Maps");
            var legendary = ClientTables.Table("UniqueMaps");
            var infused = legendary.First(r => r.Text("Name") == "Infused Beachhead");
            ushort infusedHash = (ushort)world[infused.Int("WorldAreasKey")].Int("HASH16");
            Assert.Contains(infusedHash, WorldAreaMaps.Load(world, maps, folder, legendary));
            var uncaptured = WorldAreaMaps.Load(world, maps, folder);
            Assert.NotEmpty(uncaptured);
            var area = world.First(row => uncaptured.Contains((ushort)row.Int("HASH16")));
            string id = area.Text("Id")!;
            ushort hash = (ushort)area.Int("HASH16");
            string bin = Path.Combine(folder, $"{id}_seed1.bin");
            string meta = Path.ChangeExtension(bin, ".json");
            File.WriteAllText(meta, "{\"w\":2,\"h\":2}");
            File.WriteAllBytes(bin, [2, 3, 4, 5]);
            Assert.DoesNotContain(hash, WorldAreaMaps.Load(world, maps, folder));
            Assert.Equal(new byte[] { 2, 3, 4, 5 }, WalkMapCapture.Load(id, 1, out int width, out int height, folder));
            Assert.Equal((2, 2), (width, height));
            Assert.Equal(1u, new AreaSeeds { Maps = WorldAreaMaps.Load(world, maps, folder), Next = () => 99 }.For(hash));
            Assert.Equal(99u, new AreaSeeds
            {
                Maps = WorldAreaMaps.Load(world, maps, folder), Random = new HashSet<ushort> { hash }, Next = () => 99,
            }.For(hash));
            // Incomplete captures cannot pin an otherwise unknown layout.
            File.WriteAllBytes(bin, [2]);
            Assert.Contains(hash, WorldAreaMaps.Load(world, maps, folder));
            Assert.Null(WalkMapCapture.Load(id, 1, out _, out _, folder));
            File.WriteAllText(meta, "{\"w\":-1,\"h\":-1}");
            Assert.Contains(hash, WorldAreaMaps.Load(world, maps, folder));
            File.Delete(meta);
            Assert.Contains(hash, WorldAreaMaps.Load(world, maps, folder));
        }
        finally
        {
            foreach (string file in Directory.GetFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }

}
