using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Data.Terrain.Generation;

namespace GameServer.Gateway.Tests;

public sealed class ShrineTests
{
    [Fact]
    public void Every_shrine_resolves_its_own_buff_duration_and_guards()
    {
        Assert.Empty(ShrineCatalog.Skipped);
        Assert.Equal(68, ShrineCatalog.All.Count);

        var acceleration = ShrineCatalog.Find("Acceleration")!;
        Assert.Equal(45, acceleration.Duration);
        Assert.Equal(241, acceleration.BuffRow);
        Assert.Equal("shrine_acceleration", acceleration.BuffId);
        // BuffDefinitions 241's GrantedStats are action_speed_-%, base_projectile_speed_+% and has_shrine_effect;
        // BuffTemplates 383 gives -50, 80 and 1. The client key is the Stats row + 1.
        Assert.Equal([(348u, -50), (178u, 80), (5819u, 1)], acceleration.Stats);

        // The Divine shrine lasts 20 s where the other standard rows last 45.
        Assert.Equal(20, ShrineCatalog.Find("GodMode")!.Duration);
        Assert.Equal(45, ShrineCatalog.Find("Brutal")!.Duration);

        // The monster buff is its own template and values: Acceleration's monsters take -60 action speed and 100
        // projectile speed where the player takes -50 and 80.
        Assert.NotNull(acceleration.Monster);
        Assert.Equal((ushort)384, acceleration.Monster!.TemplateRow);
        Assert.Equal("acceleration_monster", acceleration.Monster.Template);
        Assert.Equal([(348u, -60), (178u, 100), (5819u, 1)], acceleration.Monster.Stats);
        Assert.Empty(acceleration.GuardPacks);

        // Guard packs are data: only the boss and influence shrines have them (Crusader: four packs).
        Assert.Equal(4, ShrineCatalog.Find("Crusader")!.GuardPacks.Count);
        Assert.Equal("atlasexileinfluence-crusader-shrine1", ShrineCatalog.Find("Crusader")!.GuardPacks[0].Id);

        // A map rolls only the fifteen basic shrines: the apparition rows wait for their scarab, and the lesser and
        // greater rows are skills, not map shrine types.
        Assert.Equal(15, ShrineCatalog.Standard.Count);
        Assert.Equal(["Acceleration", "Lightning", "Cold", "Criticals", "Brutal", "Regen", "Massive", "Defence",
            "Resistance", "GodMode", "Multi", "Skeletons", "Smoke", "Resonating", "Gloom"],
            ShrineCatalog.Standard.Select(s => s.Id));
        Assert.Equal(8, ShrineCatalog.ApparitionIds.Length);
        Assert.DoesNotContain(ShrineCatalog.Standard, s => ShrineCatalog.ApparitionIds.Contains(s.Id));
        Assert.DoesNotContain(ShrineCatalog.Standard, s => s.Lesser || s.GuardPacks.Count > 0 || s.Duration == 0);
        Assert.Equal(ShrineCatalog.Skipped, []);
    }

    [Fact]
    public void Shrines_plan_from_walkable_room_lines_and_write_the_ten_client_components()
    {
        // data/object-types.json lists Positioned, BaseEvents, Stats, Animated, Buffs, Life, ObjectMagicProperties,
        // Shrine, MinimapIcon and InteractionAction as the shrine's deserialized components (read = 10).
        Assert.Equal(10, ShrineObject.Components.Length);
        Assert.Equal(["Positioned", "BaseEvents", "Stats", "Animated", "Buffs", "Life", "ObjectMagicProperties",
            "Shrine", "MinimapIcon", "InteractionAction"], ShrineObject.Components);

        // Two header bytes, 18 Positioned, 10 Stats, 1 Animated, 2 Buffs, 55 Life, 4 magic properties, 2 Shrine,
        // 1 MinimapIcon, 1 InteractionAction, 1 trailing task count.
        var blob = new ShrineObject(120, 45).Blob();
        Assert.Equal(98, blob.Length);
        Assert.Equal(0, blob[0]);
        Assert.Equal(0, blob[^1]);
        // The Shrine component starts with the Shrines row the client looks up for the icon.
        Assert.Equal(0, blob[92]);
        Assert.Equal(11, new ShrineObject(120, 45, Row: 11).Blob()[92]);

        // A shrine room line is a shrine only on walkable ground, and the same layout and seed roll the same types.
        bool Walkable(int x, int y) => x == 5 && y == 5;
        const string Statue = "Metadata/Shrines/generic_shrine.ao";
        PlacedTileObject Line(string type, int x, int y) => new(type, Statue, x, y, 0.5f, 0f);
        var lines = new[] { Line(ShrinePlacement.Type, 5, 5), Line(ShrinePlacement.Type, 5, 5), Line(ShrinePlacement.Type, 0, 0) };
        var plan = ShrinePlacement.Plan(lines, Walkable, seed: 7);
        Assert.Single(plan);
        Assert.Equal((5, 5), (plan[0].X, plan[0].Y));
        Assert.Equal(0.5f, plan[0].Rotation);
        // The statue is the room line's model: the shrine type has no animated_object of its own.
        Assert.Equal(Statue, plan[0].Model);
        Assert.Contains(plan[0].Shrine, ShrineCatalog.Standard);
        Assert.Equal(plan, ShrinePlacement.Plan(lines, Walkable, seed: 7));
        Assert.Empty(ShrinePlacement.Plan(lines, Walkable, seed: 7, limit: 0));
        // A room line with no model of its own still shows the shrine's default statue.
        var bare = ShrinePlacement.Plan([new(ShrinePlacement.Type, "", 5, 5, 0f, 0f)], Walkable, seed: 7);
        Assert.Equal(ShrinePlacement.DefaultModel, bare[0].Model);
    }

    [Fact]
    public void One_area_in_twenty_rolls_its_own_shrine_beyond_the_generators()
    {
        // The campaign's zones have no shrine rooms and a map may roll one beyond its rooms: 5% of areas carry a shrine
        // of their own, rolled from the area's hash and seed so an instance keeps it on every entry.
        Assert.Equal(5, ShrinePlacement.AreaChancePercent);
        const ushort coast = 0x7ad2;
        const int centre = 500;
        bool Ground(int x, int y) => Math.Max(Math.Abs(x - centre), Math.Abs(y - centre)) < 400;
        var placed = Enumerable.Range(0, 2000).Select(seed => ShrinePlacement.AreaShrine(coast, (uint)seed, Ground, (centre, centre))).ToList();
        int hits = placed.Count(p => p is not null);
        Assert.InRange(hits, 85, 115);   // 5% of 2000, within a couple of standard deviations
        Assert.Equal(placed, Enumerable.Range(0, 2000).Select(seed => ShrinePlacement.AreaShrine(coast, (uint)seed, Ground, (centre, centre))));
        double Distance(ShrinePlacement.Placed p) => Math.Sqrt(Math.Pow(p.X - centre, 2) + Math.Pow(p.Y - centre, 2));
        foreach (var shrine in placed.OfType<ShrinePlacement.Placed>())
        {
            Assert.Contains(shrine.Shrine, ShrineCatalog.Standard);
            Assert.Equal(ShrinePlacement.DefaultModel, shrine.Model);
            Assert.True(Ground(shrine.X, shrine.Y));
            Assert.InRange(Distance(shrine), ShrinePlacement.NearestCell, ShrinePlacement.FarthestCell + 1);
        }
        // A roll that misses places nothing, whatever the ground; a hit on ground with no room for the statue places nothing too.
        Assert.All(placed.Where((p, i) => ShrinePlacement.Roll(coast, (uint)i) >= 5), p => Assert.Null(p));
        uint hit = (uint)placed.FindIndex(p => p is not null);
        Assert.Null(ShrinePlacement.AreaShrine(coast, hit, (_, _) => false, (centre, centre)));
    }
}
