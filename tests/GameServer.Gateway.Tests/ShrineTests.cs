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
    public void Guards_come_from_the_shrines_own_pack_rows_or_the_areas_and_keep_clear_of_entrances()
    {
        // A basic shrine names no pack: it is guarded by one of the area's packs, one pack, at that pack's own count,
        // normal rarity (no Atlas passive reaches a campaign area).
        var basic = ShrineGuards.Resolve(ShrineCatalog.Find("Acceleration")!, mapArea: false, _ => null).Rule;
        Assert.True(basic.Required);
        Assert.True(basic.AreaPool);
        Assert.Empty(basic.Packs);
        Assert.Null(basic.Fixed);
        Assert.Equal(MonsterRarity.Normal, basic.Rarity);

        // A shrine the data gives packs takes its count from them: Brine King's five rows roll 2–9, the Conquerors' 2–7.
        var brine = ShrineGuards.Resolve(ShrineCatalog.Find("BrineKing")!, mapArea: true, _ => null).Rule;
        Assert.False(brine.AreaPool);
        Assert.Equal(5, brine.Packs.Count);
        Assert.Equal((4, 9), (brine.Min, brine.Max));
        Assert.Equal((3, 7), (ShrineGuards.Resolve(ShrineCatalog.Find("Crusader")!, true, _ => null).Rule.Min,
            ShrineGuards.Resolve(ShrineCatalog.Find("Crusader")!, true, _ => null).Rule.Max));

        // A shrine too close to the entrance or a transition is not guarded (the 1.0.0d note): within 25 cells.
        Assert.True(ShrineGuards.TooNearEntrance((30, 30), (10, 30), [(200, 200)], 25));
        Assert.True(ShrineGuards.TooNearEntrance((30, 30), (200, 200), [(10, 30)], 25));
        Assert.False(ShrineGuards.TooNearEntrance((100, 30), (10, 30), [(200, 200)], 25));
    }

    [Fact]
    public void Each_shrine_gets_its_own_guard_pack_around_its_own_cell()
    {
        // Two shrines in one area, each guarded by one pack of the area's own monsters, members within 9 cells of the
        // shrine and never stacked on each other: the packs stay their own.
        var pack = new PackCatalog.Pack(0, "Test-Pack", 3, 4, 0, 0, [], [(1, 1)], new Dictionary<int, int>());
        var area = new PackCatalog.AreaPacks("Test", 10, [(pack, 100)], new Dictionary<string, PackCatalog.Pack>(), []);
        var rule = ShrineGuards.Resolve(ShrineCatalog.Find("Acceleration")!, false, _ => null).Rule;
        bool Walkable(int x, int y) => x > 0 && y > 0 && x < 99 && y < 99;
        var notes = new List<string>();
        var spawns = AreaPopulation.Plan(area, 100, 100, Walkable, (50, 50), [], [], seed: 11, _ => true, notes,
            shrineGuards: [("Acceleration", rule, [], 20, 20), ("Brutal", rule, [], 80, 80)]);
        var guards = spawns.Where(s => s.Pack == "Test-Pack").ToList();
        foreach (var cell in new[] { (20, 20), (80, 80) })
        {
            var mine = spawns.Where(s => Math.Abs(s.X - cell.Item1) <= 9 && Math.Abs(s.Y - cell.Item2) <= 9).ToList();
            Assert.InRange(mine.Count, 3, 4);
            Assert.All(mine, s => Assert.Equal("Test-Pack", s.Pack));
            Assert.All(mine, s => Assert.Equal(MonsterRarity.Normal, s.Rarity));
        }
        // No member stands twice in one cell, and each shrine's pack is its own: the scattered packs keep their 28-cell
        // spacing from both shrine centres, so these members are the guards.
        Assert.Equal(guards.Count, guards.Select(s => (s.X, s.Y)).Distinct().Count());
        Assert.Equal(2, notes.Count(n => n.StartsWith("shrine ")));
    }
}
