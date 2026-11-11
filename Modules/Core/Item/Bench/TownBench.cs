/// <summary>
/// Where a town's crafting bench (TownCraftingBench) stands: the town rooms list one next to their stash, as a server
/// object line (<c>x y 0 angle … "model.ao" "Metadata/Terrain/Missions/Hideouts/Objects/TownCraftingBench"</c>), which the
/// client does not spawn itself, so the server creates it. Cells are room cells turned into area cells the way
/// <see cref="TownNpcs.Room.Cell"/> turns them (a room placed with code 0 at (0, 0) keeps them as they are).
/// Hideouts use their decoration's bench instead (HideoutScene.Bench).
/// </summary>
static class TownBench
{
    public const string Type = "Metadata/Terrain/Missions/Hideouts/Objects/TownCraftingBench";

    /// <summary>A town's bench: its area cell and facing (radians), its object type and the model its room line names.</summary>
    public sealed record Placement(int X, int Y, float Rotation, string Type, string? Model);

    static readonly Dictionary<ushort, Placement> Towns = new()
    {
        // Oriath Docks (2_10_town, HASH16 0x95b0): dock_town.arm (503 × 503 cells, code 0, origin (0, 0)) lists the bench
        // at (507, 300), angle 0, model TownCraftingBench01_01.ao — to the screen-left of the stash at (532, 296).
        [StashCatalog.OriathDocks] = new(507, 300, 0, Type, "Metadata/Terrain/Doodads/TownCraftingBench/TownCraftingBench01_01.ao"),
    };

    /// <summary>The bench of the town whose area is <paramref name="areaHash"/>, or null.</summary>
    public static Placement? In(ushort areaHash) => Towns.GetValueOrDefault(areaHash);
}
