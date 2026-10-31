using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Terrain.Generation;

/// <summary>
/// Every area's area transitions: where they are (the client's generated layout, captured per (area, seed) by
/// scripts/walkmaps, or the atlas's static placement for fixed layouts) and where each leads (the atlas,
/// data/area-transitions.json from scripts/re/atlas). Agents/docs/protocol/08-area-transitions.md §5, §6.
/// </summary>
static class AreaTransitionCatalog
{
    const string AtlasFile = "data/area-transitions.json";
    const string Folder = "data/walkability";
    /// <summary>Server object ids of an area's area transitions: 0x2000, 0x2001, …</summary>
    public const uint FirstId = 0x2000;

    static readonly Lazy<Dictionary<ushort, AtlasArea>?> Atlas = new(Load);

    /// <summary>The HASH16 of a WorldAreas id or name (case-insensitive, from the atlas), or null.</summary>
    public static ushort? HashOf(string idOrName) =>
        Atlas.Value?.Values.FirstOrDefault(a => a.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase)
            || (a.Name?.Equals(idOrName, StringComparison.OrdinalIgnoreCase) ?? false))?.Hash16;

    /// <summary>The WorldAreas id of an area hash (from the atlas), or null without the atlas.</summary>
    public static string? IdOf(ushort hash) => Atlas.Value?.GetValueOrDefault(hash)?.Id;

    /// <summary>The area transitions to create for this (area, seed), with one line per decision for the log.</summary>
    public static (uint Id, AreaTransitionObject Transition)[] For(ushort hash, string id, uint seed, List<string> notes)
    {
        var area = Atlas.Value?.GetValueOrDefault(hash);
        if (area is null)
        {
            notes.Add(Atlas.Value is null ? $"no {AtlasFile} (run scripts/dev/extract-data.ps1)" : $"area 0x{hash:x4} is not in the atlas");
            return [];
        }
        var topologies = area.Topologies ?? [];
        var captured = LoadCapture(id, seed);
        var placed = new List<AreaTransitionObject>();
        if (captured is not null)
        {
            var local = new List<CapturedObject>();
            // The graph variant the client generated: the one whose transition models cover the capture best.
            var best = topologies.MaxBy(t => captured.Count(o => t.Transitions?.Any(x => x.Model == o.Model) == true));
            foreach (var o in captured)
            {
                if (TryDelveEntrance(id, o.Model, out ushort delveDestination))
                {
                    placed.Add(new AreaTransitionObject(o.X, o.Y, delveDestination, o.Model,
                        Rotation: o.Rotation, Height: o.Z));
                    notes.Add($"Delve entrance at ({o.X}, {o.Y}) → {IdOf(delveDestination)}");
                    continue;
                }
                // Delve's entrance and unused elevator are not a boss-arena gate pair.
                if (id is "Delve_Hub" or "Delve_Main") continue;
                var candidates = (best?.Transitions ?? []).Where(x => x.Model == o.Model).ToList();
                var usable = candidates.Where(x => x.Enabled != false && x.DestinationHash16 is not null).ToList();
                var destinations = usable.Select(x => x.DestinationHash16!.Value).Distinct().ToList();
                string what = $"{o.Model?.Split('/')[^1]} at ({o.X}, {o.Y})";
                if (destinations.Count != 1)
                {
                    if (candidates.Count == 0) { local.Add(o); continue; }
                    notes.Add(candidates.Count == 0 ? $"{what}: not in topology {best?.Id}"
                        : destinations.Count == 0 ? $"{what}: {string.Join("/", candidates.Select(x => x.Enabled == false ? "disabled" : x.Status).Distinct())}, not created"
                        : $"{what}: ambiguous ({string.Join(", ", usable.Select(x => x.Destination).Distinct())}), not created");
                    continue;
                }
                var entry = usable[0];
                var type = Resolve(o.ObjectType, what, notes);
                placed.Add(new AreaTransitionObject(o.X, o.Y, destinations[0], o.Model, Rotation: o.Rotation, Height: o.Z,
                    Type: type));
                notes.Add($"{what} → {entry.Destination} (captured, topology {best?.Id})");
            }
            // Gates no topology names lead within the area: a map's boss room has a pair (MapBeach2 boss_1.arm: points
            // teleport1-%areaname%, telepad1, teleport2-Arena, telepad2), into the arena and back. Two are paired, each
            // to the other; any other number is left out.
            if (local.Count == 2)
                for (int i = 0; i < 2; i++)
                {
                    var (o, other) = (local[i], local[1 - i]);
                    placed.Add(new AreaTransitionObject(o.X, o.Y, hash, o.Model, Rotation: o.Rotation, Height: o.Z,
                        Type: Resolve(o.ObjectType, $"{o.Model?.Split('/')[^1]} at ({o.X}, {o.Y})", notes)) { TeleportTo = (other.X, other.Y) });
                    notes.Add($"{o.Model?.Split('/')[^1]} at ({o.X}, {o.Y}): not in topology {best?.Id}, a gate within the area to ({other.X}, {other.Y})");
                }
            else
                foreach (var o in local) notes.Add($"{o.Model?.Split('/')[^1]} at ({o.X}, {o.Y}): not in topology {best?.Id}");
        }
        else
        {
            // Fixed layouts: the atlas computed the placement from the room and tile files.
            var fixedTopology = topologies.FirstOrDefault(t => t.Transitions?.Any(x => x.Grid is not null) == true);
            foreach (var x in fixedTopology?.Transitions ?? [])
            {
                if (x.Grid is not { } g) continue;
                string what = $"{x.Model?.Split('/')[^1]} at ({g.X}, {g.Y})";
                if (x.Enabled == false || x.DestinationHash16 is null)
                {
                    notes.Add($"{what}: {(x.Enabled == false ? "disabled" : x.Status)}, not created");
                    continue;
                }
                var type = Resolve(x.ObjectType, what, notes);
                placed.Add(new AreaTransitionObject(g.X, g.Y, x.DestinationHash16.Value, x.Model, Rotation: g.Rotation, Height: g.Z,
                    Type: type));
                notes.Add($"{what} → {x.Destination} (fixed layout, topology {fixedTopology!.Id})");
            }
            if (fixedTopology is null)
                notes.Add($"no capture {Folder}/{id}_seed{seed}.transitions.json yet (run live.ps1 -Walkmaps)");
        }
        return [.. placed.Select((t, i) => (FirstId + (uint)i, t))];
    }

    internal static bool TryDelveEntrance(string area, string? model, out ushort destination)
    {
        destination = 0;
        destination = area switch
        {
            "Delve_Hub" when model?.EndsWith("DelveEntrance01_area_transition.ao", StringComparison.OrdinalIgnoreCase) == true
                => InstanceDelve.MineArea,
            "Delve_Main" when model?.EndsWith("delvemine_areatransition_v01_01_area_transition.ao", StringComparison.OrdinalIgnoreCase) == true
                => StashCatalog.DelveHub,
            _ => (ushort)0,
        };
        return destination != 0;
    }

    /// <summary>The object type to create a tile object with. Types that extend
    /// AreaTransition with more components are created as a plain AreaTransition (same model, place and destination):
    /// live 2026-10-18, The Mud Flats' Metadata/QuestObjects/WaterCave/WaterCaveAreaTransition (adds Transitionable)
    /// is read with only AreaTransition's components, so a Transitionable byte in the blob became the task count
    /// (Buffer Underflow), and without it the component kept state 0 and its post-create event crashed the client
    /// (0x141ded7b9, as for monsters sent state 0). How the real server sets that state is open (08 §5).</summary>
    static string Resolve(string? type, string what, List<string> notes)
    {
        if (type is null or AreaTransitionObject.MetadataPath) return AreaTransitionObject.MetadataPath;
        if (ObjectTypes.Value?.GetValueOrDefault(type) is not { } info)
        {
            notes.Add($"{what}: {type} is not in data/object-types.json, created as a plain AreaTransition");
            return AreaTransitionObject.MetadataPath;
        }
        var extra = info.Where(c => c != "BaseEvents").Except(AreaTransitionObject.ComponentOrder).ToList();
        if (extra.Count > 0 || !AreaTransitionObject.CanSerialize(info))
        {
            notes.Add($"{what}: {type} adds {string.Join(", ", extra)}, created as a plain AreaTransition");
            return AreaTransitionObject.MetadataPath;
        }
        return type;
    }

    // data/object-types.json (scripts/re/ot_components.py --all --json): each type's components, as the script merges
    // them (it lists Transitionable as read for WaterCaveAreaTransition; live, the client does not read it).
    static readonly Lazy<Dictionary<string, List<string>>?> ObjectTypes = new(() =>
    {
        const string file = "data/object-types.json";
        if (!File.Exists(file)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            var types = new Dictionary<string, List<string>>();
            foreach (var t in doc.RootElement.GetProperty("types").EnumerateArray())
                types[t.GetProperty("type").GetString()!] =
                    [.. t.GetProperty("components").EnumerateArray().Take(t.GetProperty("read").GetInt32()).Select(c => c.GetString()!)];
            return types;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    });

    /// <summary>True once scripts/walkmaps saved this layout's tile objects.</summary>
    public static bool Captured(string id, uint seed) => File.Exists(CaptureFile(id, seed));

    static string CaptureFile(string id, uint seed) => $"{Folder}/{id}_seed{seed}.transitions.json";

    // Quest exits extend AreaTransition under their own name (The Twilight Strand's SouthBeachTownEntrance). A generated
    // layout without any falls back to the atlas's fixed placement; a capture without any has none.
    static List<CapturedObject>? LoadCapture(string id, uint seed) =>
        LoadObjects(id, seed)?.Where(o => o.ObjectType is { } t && IsTransition(t)).ToList() is { } exits
            && (exits.Count > 0 || Captured(id, seed)) ? exits : null;

    // The tile objects of a capture or, without one, of the layout the Gateway generated itself, read the same way.
    static List<CapturedObject>? LoadObjects(string id, uint seed)
    {
        try
        {
            if (!Captured(id, seed))
                return ServerLevels.For(id, seed)?.Objects.Select(o => new CapturedObject(o.Type, o.Model, o.X, o.Y, o.Rotation, o.Z)).ToList();
            return JsonSerializer.Deserialize<CaptureFileData>(File.ReadAllText(CaptureFile(id, seed)), Json)?.Objects;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    static bool IsTransition(string type) =>
        (type.Contains("AreaTransition") || type.EndsWith("TownEntrance")) && !AreaObjects.IsEffect(type);

    /// <summary>Server object ids of an area's transition effects: 0x2f80, 0x2f81, …</summary>
    public const uint FirstEffectId = 0x2f80;

    /// <summary>The AreaTransitionEffects a tile places beside an entrance (Act 5's crater glow and ground shatter in The
    /// Slave Pens), from the capture or the generated layout. Their .ot are <c>server_only</c>: the client draws them
    /// only when the server creates them.</summary>
    public static (uint Id, TransitionEffect Effect)[] Effects(string id, uint seed) =>
        [.. (LoadObjects(id, seed) ?? []).Where(o => o.ObjectType is { } t && AreaObjects.IsEffect(t) && o.Model is not null)
            .Select((o, i) => (FirstEffectId + (uint)i, new TransitionEffect(o.ObjectType!, o.Model!, o.X, o.Y, o.Rotation, o.Z)))];

    static Dictionary<ushort, AtlasArea>? Load()
    {
        if (!File.Exists(AtlasFile)) return null;
        try
        {
            using var stream = File.OpenRead(AtlasFile);
            var file = JsonSerializer.Deserialize<AtlasFileData>(stream, Json);
            var areas = new Dictionary<ushort, AtlasArea>();
            foreach (var a in file?.Areas ?? []) areas.TryAdd(a.Hash16, a);
            Console.WriteLine($"[gateway] atlas: {areas.Count} areas from {AtlasFile}");
            return areas;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Console.WriteLine($"[gateway] atlas: {AtlasFile} unreadable: {ex.Message}");
            return null;
        }
    }

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    // Only the fields the Gateway uses; the serializer skips the rest of the atlas.
    sealed record AtlasFileData(List<AtlasArea>? Areas);
    sealed record AtlasArea(string Id, string? Name, ushort Hash16, List<AtlasTopology>? Topologies);
    sealed record AtlasTopology(string Id, string? Kind, List<AtlasTransition>? Transitions);
    sealed record AtlasTransition(string? Model, string? ObjectType, bool? Enabled, string? Status, string? Destination,
        ushort? DestinationHash16, AtlasGrid? Grid);
    sealed record AtlasGrid(int X, int Y, float Rotation, float? Z);
    sealed record CaptureFileData(List<CapturedObject>? Objects);
    sealed record CapturedObject(string? ObjectType, string? Model, int X, int Y, float Rotation, float? Z);
}

/// <summary>An AreaTransitionEffects object (GlowyFX, SlavePensIntroGround): Positioned, Animated, BaseEvents,
/// Transitionable (<c>num_states 3</c>: 1 hidden or paused at the start, 2 plays, 3 at the animation's end).</summary>
sealed record TransitionEffect(string Type, string Model, int X, int Y, float Rotation, float? Z)
{
    /// <summary>Created at its end state: the crater as it stands after the arrival's animation. Whether the real server
    /// plays state 2 on the first arrival is not captured.</summary>
    public const byte State = 3;

    /// <summary>The 0x2b9 blob: Positioned (the record's height), Animated (the type's model), Transitionable (u8 state),
    /// tasks; BaseEvents writes nothing (as the a1q4 waterfall's).</summary>
    public byte[] Blob()
    {
        var w = new BlobWriter().U8(0).U8(0);
        w.I32(X).I32(Y).F32(Rotation).U16(0).U8((byte)(Z is null ? 0 : 0x04)).U8(2);
        if (Z is float h) w.I32((int)MathF.Round(h / 7.8125f)).F32(h);
        w.U8(0).U8(1);
        w.U8(1).WStr32(Model);
        return w.U8(State).U8(0).ToArray();
    }

    public override string ToString() => $"{Type.Split('/')[^1]} at ({X}, {Y})";
}
