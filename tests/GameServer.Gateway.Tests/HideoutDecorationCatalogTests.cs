using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>
/// The three 3.29.3c MTX hideouts' default decorations (Agents/docs/protocol/21-hideouts.md §2): the catalog resolves
/// every doodad and its type, and the functional furniture/NPCs the scene already creates are filtered out of the
/// decoration list.
/// <para>
/// One test because AGENTS.md allows two per PR and these share a single load of the real hideout data.
/// </para>
/// </summary>
public sealed class HideoutDecorationCatalogTests
{
    static readonly Lazy<IReadOnlyList<HideoutCatalog.Hideout>> Loaded = new(() =>
        HideoutCatalog.Load(Path.Combine("data", "hideouts.json"), Path.Combine("data", "tables")));

    [Fact]
    public void The_3_29_3c_mtx_hideouts_export_their_full_decoration()
    {
        var byArea = Loaded.Value.ToDictionary(h => h.AreaId);
        var maze = byArea["HideoutMaze_"];
        var divine = byArea["HideoutDivine"];
        var exilecon = byArea["HideoutExilecon2026"];

        // 39: the maze's walls, spires, furniture-side scenery and its controller (Functions reads no bytes).
        Assert.Equal((68, 39), (maze.Doodads.Count, maze.Decorations.Count()));
        Assert.Equal((163, 134), (divine.Doodads.Count, divine.Decorations.Count()));
        Assert.Equal((332, 303), (exilecon.Doodads.Count, exilecon.Decorations.Count()));
        Assert.True(maze.FullDecoration && divine.FullDecoration && exilecon.FullDecoration);
        Assert.False(byArea["HideoutForest"].FullDecoration);

        // The scene already places these; creating them again would duplicate the object.
        Assert.DoesNotContain(divine.Decorations, d => d.Type!.StartsWith("Metadata/NPC/", StringComparison.Ordinal));
        Assert.DoesNotContain(divine.Decorations, d => d.Name == "Stash");
        Assert.DoesNotContain(divine.Decorations, d => d.Name == "Crafting Bench");
        Assert.Contains(divine.Decorations, d => d.Name == "Divine Pedestal");
        Assert.Contains(maze.Decorations, d => d.Name == "[DNT] Maze Hideout Controller");
        Assert.Contains(exilecon.Decorations, d => d.Name == "Exotic Jungle Cluster");

        // Apex of Divinity's built-in device is its own type, not the standard mapping device.
        var device = Assert.Single(divine.Doodads, HideoutCatalog.IsPlaced);
        Assert.EndsWith("Divive_Plinth_Map_Device", device.Type);

        // Every remaining decoration's deserialized components can be written by HideoutObject.
        foreach (var d in maze.Decorations.Concat(divine.Decorations).Concat(exilecon.Decorations))
        {
            Assert.NotNull(d.Type);
            Assert.NotNull(d.Model);
            var (components, states) = HideoutCatalog.TypeOf(d.Type!)
                ?? throw new Xunit.Sdk.XunitException($"{d.Name}: {d.Type} is not in data/object-types.json");
            Assert.True(HideoutObject.CanSerialize(components), $"{d.Name}: {d.Type} ({string.Join(", ", components)})");
            Assert.NotEmpty(new HideoutObject(d.X, d.Y, d.Rotation, components,
                new HideoutDoodad(d.Hash, (byte)d.Variation),
                [.. states.Select(s => s == "active" ? 1 : s == "max_open_portals" ? 6 : 0)], d.Model).Blob());
        }
    }
}
