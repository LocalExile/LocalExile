using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class MapOverlayAndEncountersTests
{
    [Fact]
    public void Map_display_stats_compose_with_atlas_scaling_and_encode_in_second_list()
    {
        uint qtyKey = Program.Stats.Key("map_item_drop_quantity_+%")!.Value;
        uint explicitPackKey = Program.Stats.Key("map_pack_size_+%")!.Value;
        uint atlasEffectKey = Program.Stats.Key("map_mod_effect_+%_for_non_unique_maps")!.Value;
        uint rawAtlasOnlyKey = Program.Stats.Key("map_strongbox_close_after_opening_chance_%")!.Value;

        var atlas = new Dictionary<uint, int>
        {
            [atlasEffectKey] = 50,
            [rawAtlasOnlyKey] = 25,
        };
        var mods = new MapMods(new Dictionary<uint, int>())
        {
            ExplicitMods =
            [
                new Dictionary<uint, int>
                {
                    [qtyKey] = 40,
                    [explicitPackKey] = 20,
                }
            ],
            Implicit = new Dictionary<uint, int> { [qtyKey] = 10 },
            Fragments = new Dictionary<uint, int> { [qtyKey] = 5 },
            MapRarity = ItemRarity.Rare,
            FragmentCount = 1,
        };

        var display = Game.ComposeMapDisplayStats(mods, atlas);
        // Explicit 40 scaled by +50% Atlas effect = 60, plus 10 implicit + 5 fragment = 75.
        Assert.Equal(75, display[qtyKey]);
        Assert.Equal(30, display[explicitPackKey]);
        Assert.False(display.ContainsKey(rawAtlasOnlyKey));
        Assert.False(display.ContainsKey(atlasEffectKey));

        byte[] plain = new AreaInstance(1, 0x235c, 7, 2).Encode();
        byte[] withDisplay = new AreaInstance(1, 0x235c, 7, 2, DisplayStats: display).Encode();
        Assert.NotEqual(plain.Length, withDisplay.Length);
        // Header flag bit 0x40 enables the three area-stat lists at bytes [18..20].
        Assert.Equal(0x40, withDisplay[18] & 0x40);
    }

    [Fact]
    public void PortalRotation_computes_radial_atan2_facing_around_device()
    {
        // North of device (dx=0, dy=-10): atan2(0, 10) = 0
        Assert.Equal(0f, MapDevice.PortalRotation(100, 100, 100, 90, 0), 4);
        // East of device (dx=10, dy=0): atan2(10, 0) = +pi/2
        Assert.Equal(MathF.PI / 2f, MapDevice.PortalRotation(100, 100, 110, 100, 1), 4);
        // South of device (dx=0, dy=10): atan2(0, -10) = +pi
        Assert.Equal(MathF.PI, MapDevice.PortalRotation(100, 100, 100, 110, 2), 4);
        // West of device (dx=-10, dy=0): atan2(-10, 0) = -pi/2
        Assert.Equal(-MathF.PI / 2f, MapDevice.PortalRotation(100, 100, 90, 100, 3), 4);
    }
}
