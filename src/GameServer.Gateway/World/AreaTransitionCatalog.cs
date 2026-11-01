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

    /// <summary>Every area /goto finds (<see cref="HashOf"/>): its id and name.</summary>
    public static IEnumerable<(string Id, string? Name)> Areas => Atlas.Value?.Values.Select(a => (a.Id, a.Name)) ?? [];

    /// <summary>The area transitions to create for this (area, seed), with one line per decision for the log.</summary>
    public static (uint Id, AreaTransitionObject Transition)[] For(ushort hash, string id, uint seed, List<string> notes)
    {
        var area = Atlas.Value?.GetValueOrDefault(hash);
        if (area is null)
        {
            notes.Add(Atlas.Value is null ? $"no {AtlasFile} (run scripts/dev/extract-data.ps1)" : $"area 0x{hash:x4} is not in the atlas");
            return [];
        }
        // A layout the Gateway generates itself knows each object's room and graph node: that decides (§8).
        if (UseLayout && ServerLevels.For(id, seed) is { } level)
            return [.. FromLayout(area, level, notes).Select((t, i) => (FirstId + (uint)i, t with { InTown = area.IsTown }))];
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
        return [.. placed.Select((t, i) => (FirstId + (uint)i, t with { InTown = area.IsTown }))];
    }

    /// <summary>The exits of a generated layout (<see cref="ServerLevels.Level.Exits"/>, <c>AreaExits</c>): "entranceN" leads
    /// to the area's Connections[N − 1], "teleportN" within the area to its "telepadN" node; an unused object and a
    /// disabled entrance (an arrival point only, e.g. Lioneye's Watch's Twilight Strand marker) are not created. A
    /// disabled teleport (an arena's way in that the real game unlocks, e.g. the Upper Prison's into Brutus's room) is
    /// created open: the encounters that would unlock it are not modelled.</summary>
    static List<AreaTransitionObject> FromLayout(AtlasArea area, ServerLevels.Level level, List<string> notes) =>
        FromExits(area.Id, area.Hash16, area.Connections ?? [], level.Exits, notes, level.Entrances);

    /// <summary><see cref="FromLayout"/> for an area's id, HASH16, WorldAreas connections and placed exits.
    /// <paramref name="entrances"/> (the layout's entrance points): an enabled "entranceN" that got no object, to an area
    /// no other exit leads to, is an exit without a model at its point. In the game that is a portal or an NPC's boat
    /// that appears there (The Harvest's way to Highgate after Malachai, Weylam's boat at The Beacon and the Brine King's
    /// Reef), or a town's exit no tile marks (Karui Shores' camp.arm "entrance1" to Oriath, as the atlas's point-only
    /// exits); a quest gate on the destination still applies (Quests.FlagToEnter).</summary>
    internal static List<AreaTransitionObject> FromExits(string areaId, ushort areaHash, IReadOnlyList<AtlasConnection> connections,
        IReadOnlyList<PlacedExit> exits, List<string> notes, IReadOnlyList<EntrancePoint>? entrances = null)
    {
        var placed = new List<AreaTransitionObject>();
        foreach (var e in exits)
        {
            var o = e.Object;
            string what = $"{o.Model.Split('/')[^1]} at ({o.X}, {o.Y})";
            float? height = float.IsNaN(o.Z) ? null : o.Z;
            // Delve's mine entrance and exit lead to areas no graph node names (Delve_Hub's hub.arm names no exit).
            if (TryDelveEntrance(areaId, o.Model, out ushort delveDestination))
            {
                placed.Add(new AreaTransitionObject(o.X, o.Y, delveDestination, o.Model, Rotation: o.Rotation, Height: height));
                notes.Add($"Delve entrance at ({o.X}, {o.Y}) → {IdOf(delveDestination)}");
                continue;
            }
            switch (e.Kind)
            {
                case ExitKind.Entrance when !e.Enabled:
                    notes.Add($"{what}: entrance{e.Number}disabled ({e.Why}), an arrival point only, not created");
                    break;
                case ExitKind.Entrance:
                    if (connections.FirstOrDefault(c => c.Index == e.Number) is not { } to)
                    {
                        notes.Add($"{what}: entrance{e.Number} ({e.Why}), but the area has {connections.Count} connection(s), not created");
                        break;
                    }
                    placed.Add(new AreaTransitionObject(o.X, o.Y, to.Hash16, o.Model, Rotation: o.Rotation, Height: height,
                        Type: Resolve(o.Type, what, notes)));
                    notes.Add($"{what} → {to.Id} (entrance{e.Number}, {e.Why})");
                    break;
                case ExitKind.Teleport when e.Target is { } target:
                    // A Teleport (type 1) labelled with its ClientStrings key; "%areaname%" (or no key) shows the area's name.
                    placed.Add(new AreaTransitionObject(o.X, o.Y, areaHash, o.Model, Rotation: o.Rotation, Height: height,
                        Type: Resolve(o.Type, what, notes)) { TeleportTo = target, Kind = 1, Label = LabelHash(e.Label) });
                    notes.Add($"{what}: teleport{e.Number}{(e.Enabled ? "" : "disabled")}{(e.Label is null ? "" : "-" + e.Label)} to ({target.X}, {target.Y}) within the area ({e.Why})");
                    break;
                case ExitKind.Teleport:
                    notes.Add($"{what}: teleport{e.Number} ({e.Why}), not created");
                    break;
                default:
                    notes.Add($"{what}: {e.Why}, not created");
                    break;
            }
        }
        foreach (var p in entrances ?? [])
        {
            if (!p.Enabled || connections.FirstOrDefault(c => c.Index == p.Number) is not { } to) continue;
            if (exits.Any(e => e.Kind == ExitKind.Entrance && e.Enabled && e.Number == p.Number)) continue;
            if (placed.Any(t => t.DestinationHash16 == to.Hash16 && t.TeleportTo is null)) continue;
            placed.Add(new AreaTransitionObject(p.X, p.Y, to.Hash16));
            notes.Add($"entrance{p.Number} → {to.Id} at its point ({p.X}, {p.Y}): no object of its own, an exit without a model");
        }
        return placed;
    }

    /// <summary>The ClientStrings HASH32 of a gate's label key (data/tables/ClientStrings.json), null for "%areaname%",
    /// an unknown key or without the table.</summary>
    static uint? LabelHash(string? key) => key is null or "%areaname%" ? null : ClientStrings.Value?.GetValueOrDefault(key);

    static readonly Lazy<Dictionary<string, uint>?> ClientStrings = new(() =>
    {
        const string file = "data/tables/ClientStrings.json";
        if (!File.Exists(file)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            var map = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var r in doc.RootElement.EnumerateArray())
                if (r.GetProperty("Id").GetString() is { } id && r.TryGetProperty("HASH32", out var h) && h.TryGetUInt32(out uint hash))
                    map.TryAdd(id, hash);
            return map;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    });

    /// <summary>Where a player arriving from <paramref name="from"/> belongs in a generated layout: the named point of the
    /// "entranceN" (enabled or not) whose connection is that area, e.g. The Submerged Passage's Depths return
    /// (entrance4disabled) for a player coming up from The Flooded Depths.</summary>
    public static (int X, int Y)? EntrancePoint(ushort hash, IReadOnlyList<EntrancePoint> entrances, ushort from)
    {
        if (Atlas.Value?.GetValueOrDefault(hash)?.Connections is not { } connections) return null;
        var numbers = connections.Where(c => c.Hash16 == from).Select(c => c.Index).ToHashSet();
        return entrances.Where(p => numbers.Contains(p.Number)).OrderBy(p => p.Enabled ? 1 : 0).Select(p => ((int, int)?)(p.X, p.Y)).FirstOrDefault();
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
            using var doc = GameTables.Open(file);
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

    /// <summary>Whether a scripts/walkmaps capture is read for an area the Gateway does not generate (on). A generated
    /// layout never reads one (<see cref="For"/>); scripts/re/exits turns captures off so that its comparison with the
    /// model matching (<see cref="UseLayout"/> off) runs on the generated objects too.</summary>
    internal static bool UseCaptures { get; set; } = true;

    /// <summary>Whether a generated layout's exits come from its graph nodes (§8, on), or from §7's model matching over
    /// its objects (off: scripts/re/exits compares the two).</summary>
    internal static bool UseLayout { get; set; } = true;

    /// <summary>True once scripts/walkmaps saved this layout's tile objects.</summary>
    public static bool Captured(string id, uint seed) => UseCaptures && File.Exists(CaptureFile(id, seed));

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
            // On a thread of its own: the reader's 56 MB of pooled buffers go with it (GameTables.Isolated).
            var file = GameTables.Isolated(() => JsonSerializer.Deserialize<AtlasFileData>(stream, Json));
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
    sealed record AtlasArea(string Id, string? Name, ushort Hash16, List<AtlasTopology>? Topologies, List<AtlasConnection>? Connections,
        bool IsTown = false);
    internal sealed record AtlasConnection(int Index, string Id, ushort Hash16);
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
