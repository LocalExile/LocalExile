using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core;

/// <summary>
/// Hideouts (Agents/docs/protocol/21-hideouts.md): every Hideouts.dat row with its area and default decoration, from
/// data/hideouts.json (scripts/re/atlas hideouts). A placed doodad's hash is an MtxTypeGameSpecific HASH32; the
/// HideoutDoodads row of that MtxTypeGameSpecific row gives its object type (InheritsFrom) and models
/// (Variation_AOFiles, picked by the placement's variation). Every character has the <see cref="Default"/> hideout.
/// </summary>
public static class HideoutCatalog
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadGameData()
    {
        DoodadTypeIndex = DoodadTypes();
        Devices = LoadDevices();
        CollisionRadii = LoadCollisionRadii();
        HideoutMusicRows = LoadHideoutMusicRows();
        All = LoadAll();
        ObjectTypes = LoadObjectTypes();
    }

    /// <summary>The first campaign hideout (Hideouts row 0, found in The Dread Thicket): every character's.</summary>
    public const string DefaultArea = "HideoutForest";
    public const string CraftingBenchType = "Metadata/Terrain/Missions/Hideouts/Objects/HideoutCraftingBench";
    public const string ItemDisplayCaseType = "Metadata/MiscellaneousObjects/Hideout/ItemDisplayCase";

    /// <summary>The standard bench, to the screen-left of the guild stash in the isometric grid.</summary>
    public static Doodad? BenchBesideGuild(Hideout hideout, int guildX, int guildY, Func<int, int, bool>? walkable)
    {
        if (hideout.First(CraftingBenchType) is not { } bench) return null;
        foreach (var (dx, dy) in new[] { (-12, 12), (-16, 16), (-12, 8), (-8, 12) })
        {
            int x = guildX + dx, y = guildY + dy;
            bool clear = x >= 2 && y >= 2;
            for (int ox = -2; clear && ox <= 2; ox++)
                for (int oy = -2; clear && oy <= 2; oy++) clear = walkable?.Invoke(x + ox, y + oy) ?? true;
            if (clear) return bench with { X = x, Y = y };
        }
        return null;
    }

    /// <summary>A doodad of a hideout's decoration, in area grid cells; Rotation in radians (the file's r / 65536 turns).</summary>
    public sealed record Doodad(string Name, uint Hash, string? Type, string? Model, int X, int Y, float Rotation, int Variation)
    {
        public double CollisionRadius { get; init; }
    }

    /// <summary>One hideout: Hash16 is its Hideouts.dat key (the Player component's), AreaHash16 its WorldAreas row's.</summary>
    public sealed record Hideout(int Row, ushort Hash16, string AreaId, ushort AreaHash16, string Name, IReadOnlyList<Doodad> Doodads)
    {
        public Doodad? First(string type) => Doodads.FirstOrDefault(d => d.Type == type);
    }

    static IReadOnlyList<Hideout> All = default!;

    static IReadOnlyList<Hideout> LoadAll()
    {
        var list = Load();
        Console.WriteLine(list.Count == 0
            ? "[gateway] hideouts: no hideout_layouts table in client.db, hideouts are off"
            : $"[gateway] hideouts: {list.Count}, default {DefaultOf(list)?.Name ?? "none"}");
        return list;
    }

    public static IReadOnlyList<Hideout> Hideouts => All;

    // Local server: every exported personal template is available, including UnlockMtx templates.
    public static byte[] OwnedRows => SelectableRows(Hideouts);

    public static byte[] SelectableRows(IEnumerable<Hideout> hideouts) =>
        [.. hideouts.Select(h => h.Row).Where(row => row is >= 0 and <= 255).Distinct().Order().Select(row => (byte)row)];

    /// <summary>The hideout whose area this is, or null.</summary>
    public static Hideout? ByArea(ushort areaHash16) => Hideouts.FirstOrDefault(h => h.AreaHash16 == areaHash16);

    public static Hideout? Default => DefaultOf(Hideouts);

    public static Hideout? Selected(string? area) => Hideouts.FirstOrDefault(h => h.AreaId == area) ?? Default;

    static Hideout? DefaultOf(IReadOnlyList<Hideout> list) => list.FirstOrDefault(h => h.AreaId == DefaultArea) ?? list.FirstOrDefault();

    /// <summary>The hideout's waypoint as the waypoint object (its variation's model), or null.</summary>
    public static WaypointObject? Waypoint(Hideout h) =>
        h.First(WaypointObject.MetadataPath) is { } d
            ? new WaypointObject(d.X, d.Y, d.Model, d.Rotation, Hideout: new HideoutDoodad(d.Hash, (byte)d.Variation)) : null;

    /// <summary>The doodad of a hideout's template the server creates besides the waypoint and stash: the map device
    /// (21-hideouts.md §2), or the variant the owner placed instead.</summary>
    public static bool IsPlaced(Doodad d) => d.Type is { } type && IsMapDevice(type);

    /// <summary>A map device type: the templates' own (…MappingDevice) and the Decorations panel's variants, which replace it.</summary>
    public static bool IsMapDevice(string type) => type.EndsWith("MappingDevice", StringComparison.Ordinal)
        || type.Contains("/MapDeviceVariants/", StringComparison.Ordinal) || type.StartsWith("Metadata/MapDevices/", StringComparison.Ordinal);

    // Our own cells for some doodads, replacing the decoration's: (hideout area, doodad name) → cell. The Lush Hideout's
    // map device stands far out at (449, 460) in its decoration; the user picked (257, 283), near the waypoint (2026-10-20).
    static readonly Dictionary<(string Area, string Name), (int X, int Y)> Moved = new()
    {
        [(DefaultArea, "Map Device")] = (257, 283),
    };

    /// <summary>Looks up a decoration by MtxTypeGameSpecific HASH32 (from HideoutDoodads).</summary>
    public static (string? Type, string[]? Models)? DoodadByHash(uint hash) =>
        DoodadTypeIndex.TryGetValue(hash, out var entry) ? entry : null;

    /// <summary>A map device's separate button (the object a click on opens the device, e.g. the Eldritch Reaper's
    /// brazier), from MapDevices.dat: the row whose HideoutDoodads row is the device's names the button's MiscObjects row
    /// and its offset from the device in grid cells (unrotated). A doodad's rows are its variations in order (the template
    /// device's Hideout_Small, without a button, is CartographyDevice2.ao). Null for a variation without one.</summary>
    public static (string Type, int Dx, int Dy)? MapDeviceButton(uint hash, int variation) =>
        Devices.TryGetValue((hash, variation), out var d) && d.Button is { } type ? (type, d.Dx, d.Dy) : null;

    sealed record DeviceRow(string? Button, int Dx, int Dy, int Radius);

    static Dictionary<(uint Hash, int Variation), DeviceRow> Devices = default!;

    static Dictionary<(uint, int), DeviceRow> LoadDevices()
    {
        var result = new Dictionary<(uint, int), DeviceRow>();
        if (!ClientTables.Has("MapDevices") || !ClientTables.Has("MiscObjects") || !ClientTables.Has("HideoutDoodads")
            || !ClientTables.Has("MtxTypeGameSpecific")) return result;
        var hashOfRow = ClientTables.Table("MtxTypeGameSpecific").Select(r => r.UInt("HASH32")).ToList();
        var doodadHash = ClientTables.Table("HideoutDoodads")
            .Select(r => r.IntOrNull("MtxTypeGameSpecific") is (>= 0 and int g) && g < hashOfRow.Count ? hashOfRow[g] : (uint?)null).ToList();
        var misc = ClientTables.Table("MiscObjects").Select(r => r.TextOrNull("EffectVirtualPath")).ToList();
        var variations = new Dictionary<int, int>();
        foreach (var r in ClientTables.Table("MapDevices"))
        {
            if (r.IntOrNull("HideoutDoodan") is not (>= 0 and int doodad) || doodad >= doodadHash.Count) continue;
            int variation = variations[doodad] = variations.GetValueOrDefault(doodad, -1) + 1;
            if (doodadHash[doodad] is not uint hash) continue;
            var offset = r.Ints("__col5");
            string? button = r.IntOrNull("MiscObject") is (>= 0 and int m) && m < misc.Count && misc[m] is { Length: > 0 } type
                && offset.Length >= 2 ? type : null;
            result.TryAdd((hash, variation), new(button, button is null ? 0 : offset[0], button is null ? 0 : offset[1], r.IntOrNull("__col2") ?? 0));
        }
        return result;
    }

    /// <summary>Collision hull radius for a hideout decoration model, or 0 when none is extracted.</summary>
    public static double CollisionRadius(string? model) =>
        model is not null && CollisionRadii.TryGetValue(model, out double r) ? r : 0;

    /// <summary>Returns the track name for a Music.dat row valid in hideouts (or 0xffff default), else null.</summary>
    public static string? HideoutMusicName(ushort row) =>
        row == InstanceHideoutEditing.DefaultMusicRow
            ? HideoutMusicRows.GetValueOrDefault((ushort)0) ?? "Default Hideout Music"
            : HideoutMusicRows.TryGetValue(row, out var name) ? name : null;

    static Dictionary<uint, (string? Type, string[]? Models)> DoodadTypeIndex = default!;
    static Dictionary<string, double> CollisionRadii = default!;
    static Dictionary<ushort, string> HideoutMusicRows = default!;

    static Dictionary<string, double> LoadCollisionRadii()
    {
        var radii = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var r in ClientTables.TableOrEmpty("hideout_collision_radii")) radii.TryAdd(r.Text("model")!, r.Real("radius"));
        return radii;
    }

    static Dictionary<ushort, string> LoadHideoutMusicRows()
    {
        var rows = new Dictionary<ushort, string>();
        if (!ClientTables.Has("Music")) return rows;
        foreach (var r in ClientTables.Table("Music"))
            if (r.Index is >= 0 and <= ushort.MaxValue && r.Bool("IsAvailableInHideout"))
                rows[(ushort)r.Index] = r.Text("Name") is { Length: > 0 } name ? name : (r.Text("Id") ?? $"Track {r.Index}");
        return rows;
    }

    /// <summary>An object type's deserialized components and shared-state count (the object_types table), or null.</summary>
    public static (IReadOnlyList<string> Components, IReadOnlyList<string> SharedStates)? TypeOf(string type) =>
        ObjectTypes.TryGetValue(type, out var t) ? t : null;

    static Dictionary<string, (IReadOnlyList<string>, IReadOnlyList<string>)> ObjectTypes = default!;

    static Dictionary<string, (IReadOnlyList<string>, IReadOnlyList<string>)> LoadObjectTypes()
    {
        var result = new Dictionary<string, (IReadOnlyList<string>, IReadOnlyList<string>)>(StringComparer.Ordinal);
        var doodadTypes = DoodadTypeIndex.Values.Select(v => v.Type).OfType<string>()
            .Concat(Devices.Values.Select(d => d.Button).OfType<string>()).ToHashSet(StringComparer.Ordinal);
        var sharedStates = ClientTables.TableOrEmpty("object_type_shared_states").By("type");
        foreach (var (type, t) in global::ObjectTypes.All())
        {
            if (!type.Contains("Hideout", StringComparison.OrdinalIgnoreCase) && !doodadTypes.Contains(type)) continue;
            result[type] = (global::ObjectTypes.Components(t), [.. sharedStates[t.Index].Select(s => s.Text("name")!)]);
        }
        return result;
    }

    // client.db's hideout_layouts, hideout_doodads and hideout_collision_radii (data/hideouts.json from scripts/re/atlas).
    public static List<Hideout> Load()
    {
        if (ClientTables.TableOrNull("hideout_layouts") is not { } layouts) return [];
        var doodadTypes = DoodadTypeIndex ?? DoodadTypes();
        var doodadsOf = ClientTables.TableOrEmpty("hideout_doodads").By("hideout");
        var radii = CollisionRadii ?? LoadCollisionRadii();
        var result = new List<Hideout>();
        foreach (var h in layouts)
        {
            var doodads = new List<Doodad>();
            foreach (var d in doodadsOf[h.Index])
            {
                uint hash = d.UInt("hash");
                int fv = d.Int("fv");
                var (type, models) = doodadTypes.GetValueOrDefault(hash);
                string? model = models is { Length: > 0 } ? models[Math.Clamp(fv, 0, models.Length - 1)] : null;
                doodads.Add(new Doodad(d.Text("name") ?? "", hash, type, model, d.Int("x"), d.Int("y"), d.Int("r") / 65536f * 2 * MathF.PI, fv)
                {
                    CollisionRadius = model is not null ? radii.GetValueOrDefault(model) : 0
                });
            }
            string area = h.Text("area") ?? "";
            for (int i = 0; i < doodads.Count; i++)
                if (Moved.TryGetValue((area, doodads[i].Name), out var cell)) doodads[i] = doodads[i] with { X = cell.X, Y = cell.Y };
            result.Add(new Hideout(h.Int("row"), (ushort)h.Int("hash16"), area, (ushort)h.Int("areaHash16"), h.Text("name") ?? "", doodads));
        }
        return result;
    }

    // MtxTypeGameSpecific HASH32 → (HideoutDoodads InheritsFrom, Variation_AOFiles).
    static Dictionary<uint, (string? Type, string[]? Models)> DoodadTypes()
    {
        var result = new Dictionary<uint, (string?, string[]?)>();
        if (!ClientTables.Has("MtxTypeGameSpecific") || !ClientTables.Has("HideoutDoodads")) return result;
        var hashOfRow = ClientTables.Table("MtxTypeGameSpecific").Select(r => r.UInt("HASH32")).ToList();
        foreach (var r in ClientTables.Table("HideoutDoodads"))
        {
            if (r.IntOrNull("MtxTypeGameSpecific") is not (>= 0 and int row) || row >= hashOfRow.Count) continue;
            result.TryAdd(hashOfRow[row], (r.Text("InheritsFrom"), r.Texts("Variation_AOFiles")));
        }
        return result;
    }
}
