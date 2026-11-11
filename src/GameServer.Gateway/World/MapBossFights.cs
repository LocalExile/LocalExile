using System.Text.Json;
using GameServer.Core;

/// <summary>
/// Device-recipe fights the area does not play by itself (21-hideouts.md §5). Maven invitations spawn
/// <c>MavenFights.WitnessAreas</c> bosses in MavenHub (or that many Atlas map bosses for The Atlas). A recipe arena
/// with a held-back last WorldAreas boss (Maven's Nucleus, Sirus after the throne, Atziri after her vessels) forms him
/// when the others are dead; Sirus also brings his storm objects. <c>AtlasVoidstoneSlots</c> drop the matching
/// Voidstone the first time those bosses are beaten and set the GrantAtlasUpgrade quest flags.
/// </summary>
public sealed class MapBossFights
{
    public const string StormVariety = "Metadata/Monsters/InvisibleFire/InvisibleFireOrionDeathZoneSupporter";
    public const int StormCount = 4;
    public const string WalkingSirus = "Metadata/Monsters/AtlasExiles/AtlasExile5";

    const string StonesKey = "voidstones", PartsKey = "voidstone_parts";

    public sealed record Invitation(string Id, int ItemRow, int Count, IReadOnlyList<string> Bosses);

    public sealed record Phase(string AreaId, IReadOnlyList<string> First, string Last);

    public sealed record Stone(int Slot, string ItemId, string Flag, IReadOnlyList<string> Defeat,
        IReadOnlyList<string>? AllOf = null);

    // Recipe arenas whose last WorldAreas boss waits for the rest (not Uber Elder: both stand together).
    static readonly HashSet<string> PhasedAreas = new(StringComparer.Ordinal)
    {
        "MavenBoss", "AtlasExilesBoss5", "MapAtziri1", "MapAtziri2",
    };

    static readonly Lazy<MapBossFights> Loaded = new(() => Load("data"));

    readonly Dictionary<int, Invitation> _invites;
    readonly Dictionary<string, Phase> _phases;
    readonly IReadOnlyList<Stone> _stones;
    readonly IReadOnlyList<string> _atlasBosses;

    MapBossFights(Dictionary<int, Invitation> invites, Dictionary<string, Phase> phases, IReadOnlyList<Stone> stones,
        IReadOnlyList<string> atlasBosses)
    {
        _invites = invites;
        _phases = phases;
        _stones = stones;
        _atlasBosses = atlasBosses;
    }

    public static MapBossFights Current => Loaded.Value;

    public Invitation? InvitationOf(int itemRow) => _invites.GetValueOrDefault(itemRow);

    /// <summary>The bosses a Maven invitation stands in MavenHub: WitnessAreas' last WorldAreas boss, or that many
    /// ordinary Atlas map bosses when the row has none (The Atlas).</summary>
    public IReadOnlyList<string> Witnesses(int itemRow)
    {
        if (InvitationOf(itemRow) is not { } fight) return [];
        if (fight.Bosses.Count > 0) return fight.Bosses;
        return [.. _atlasBosses.Distinct(StringComparer.Ordinal).Take(Math.Max(1, fight.Count))];
    }

    public Phase? PhaseOf(string areaId) => _phases.GetValueOrDefault(areaId);

    /// <summary>Whether this class already drives the body: a member of a first wave, an arena's held-back last boss,
    /// the Atlas bosses an invitation can stand in MavenHub, or a body a Voidstone records the defeat of. A module that
    /// would otherwise give every bar'd monster its own rotation asks this, so a kit cannot take over a fight this class
    /// forms, holds back or tracks.
    /// </summary>
    public bool Drives(string varietyId) => _phases.Values.Any(phase =>
            phase.Last == varietyId || phase.First.Contains(varietyId))
        || _atlasBosses.Contains(varietyId)
        || _stones.Any(stone => stone.Defeat.Contains(varietyId)
            || (stone.AllOf is { Count: > 0 } need && need.Contains(varietyId)));

    /// <summary>Marks the held-back last boss absent so Plan does not place him with the first wave.</summary>
    public PackCatalog.AreaPacks HoldFinal(PackCatalog.AreaPacks area, Func<int, string?> varietyId)
    {
        if (PhaseOf(area.AreaId) is not { } phase) return area;
        var absent = new HashSet<int>(area.Absent);
        for (int i = 0; i < area.Bosses.Count; i++)
            if (varietyId(area.Bosses[i]) == phase.Last) absent.Add(i);
        return absent.Count == area.Absent.Count ? area : area with { Absent = absent };
    }

    /// <summary>The last boss, once this death was one of the first wave and none of them is left alive.</summary>
    public string? FormsAfter(string areaId, string varietyId, Func<string, bool> firstWaveAlive)
    {
        if (PhaseOf(areaId) is not { } phase || !phase.First.Contains(varietyId)) return null;
        return phase.First.Any(firstWaveAlive) ? null : phase.Last;
    }

    public static bool StormsWith(string formed) => formed == WalkingSirus;

    /// <summary>Which Voidstone this kill unlocks (and an AllOf part to remember), given the slots and parts already saved.</summary>
    public (Stone? Ready, string? Part) Resolve(string varietyId, IReadOnlySet<int> haveSlots, IReadOnlySet<string> haveParts)
    {
        foreach (var stone in _stones)
        {
            if (haveSlots.Contains(stone.Slot)) continue;
            if (stone.Defeat.Contains(varietyId)) return (stone, null);
            if (stone.AllOf is { Count: > 0 } need && need.Contains(varietyId))
            {
                var next = haveParts.Append(varietyId).ToHashSet(StringComparer.Ordinal);
                return need.All(next.Contains) ? (stone, varietyId) : (null, varietyId);
            }
        }
        return default;
    }

    public static IReadOnlySet<int> Socketed(string? character) =>
        ParseSlots(character is null ? null : CharacterSaves.ModuleData(character, StonesKey));

    public Stone? StoneByItem(string itemId) =>
        _stones.FirstOrDefault(s => s.ItemId.Equals(itemId, StringComparison.Ordinal));

    /// <summary>Sockets or unsockets a Voidstone slot from the Atlas click (C→S 0xf6). False when the slot already
    /// matches <paramref name="on"/>.</summary>
    public bool SetSocketed(string character, int slot, bool on)
    {
        var have = Socketed(character).ToHashSet();
        if (!(on ? have.Add(slot) : have.Remove(slot))) return false;
        CharacterSaves.ChangeModuleData(character, StonesKey, _ => string.Join(',', have.Order()));
        return true;
    }

    /// <summary>Remembers a Voidstone part or sockets the slot. Returns the stone when it is newly unlocked.</summary>
    public Stone? Unlock(string character, string varietyId)
    {
        var have = Socketed(character);
        var parts = ParseParts(CharacterSaves.ModuleData(character, PartsKey));
        var (ready, part) = Resolve(varietyId, have, parts);
        if (part is not null && !parts.Contains(part))
            CharacterSaves.ChangeModuleData(character, PartsKey, _ => string.Join(',', parts.Append(part).Order(StringComparer.Ordinal)));
        if (ready is null || have.Contains(ready.Slot)) return null;
        CharacterSaves.ChangeModuleData(character, StonesKey, _ =>
            string.Join(',', have.Append(ready.Slot).Order()));
        return ready;
    }

    public static MapBossFights Load(string folder)
    {
        var varieties = VarietyIds(folder);
        var areaBosses = AreaBosses(folder, varieties);
        var invites = new Dictionary<int, Invitation>();
        string fights = Path.Combine(folder, "tables", "MavenFights.json");
        if (ClientTables.Exists(fights))
        {
            using var doc = GameTables.Open(fights);
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (row.GetProperty("BaseItemType") is not { ValueKind: JsonValueKind.Number } item) continue;
                int[] areas = row.TryGetProperty("WitnessAreas", out var list) && list.ValueKind == JsonValueKind.Array
                    ? [.. list.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number).Select(v => v.GetInt32())]
                    : [];
                int count = row.TryGetProperty("WitnessesRequired", out var n) && n.ValueKind == JsonValueKind.Number
                    ? n.GetInt32() : areas.Length;
                invites[item.GetInt32()] = new(row.GetProperty("Id").GetString() ?? "", item.GetInt32(), count,
                    [.. areas.Select(a => areaBosses.GetValueOrDefault(a, "")).Where(id => id.Length > 0)]);
            }
        }

        var phases = new Dictionary<string, Phase>(StringComparer.Ordinal);
        foreach (var (areaId, bosses) in AllAreaBossLists(folder, varieties))
        {
            if (!PhasedAreas.Contains(areaId) || bosses.Count < 2) continue;
            string last = bosses[^1];
            var first = bosses.Where(id => id != last).Distinct(StringComparer.Ordinal).ToList();
            if (first.Count == 0 || last.Length == 0) continue;
            phases[areaId] = new(areaId, first, last);
        }

        var flags = FlagIds(folder);
        var items = ItemIds(folder);
        var stones = new List<Stone>();
        string slots = Path.Combine(folder, "tables", "AtlasVoidstoneSlots.json");
        if (ClientTables.Exists(slots))
        {
            using var doc = GameTables.Open(slots);
            int i = 0;
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                int slot = row.TryGetProperty("SlotNumber", out var sn) && sn.ValueKind == JsonValueKind.Number
                    ? sn.GetInt32() : i;
                i++;
                string itemId = items.GetValueOrDefault(row.TryGetProperty("Voidstone", out var vs) && vs.ValueKind == JsonValueKind.Number
                    ? vs.GetInt32() : -1, "");
                string flag = flags.GetValueOrDefault(row.TryGetProperty("QuestFlag", out var qf) && qf.ValueKind == JsonValueKind.Number
                    ? qf.GetInt32() : -1, "");
                if (itemId.Length == 0) continue;
                stones.Add(StoneOf(slot, itemId, flag));
            }
        }

        return new MapBossFights(invites, phases, stones, AtlasMapBosses(folder, varieties));
    }

    static Stone StoneOf(int slot, string itemId, string flag) => slot switch
    {
        0 => new(slot, itemId, flag, ["Metadata/Monsters/MavenBoss/MavenBrainBoss"]),
        1 => new(slot, itemId, flag, ["Metadata/Monsters/AtlasBosses/TheShaperBossUberElder"],
            ["Metadata/Monsters/AtlasBosses/TheShaperBoss", "Metadata/Monsters/AtlasBosses/TheElder/TheElder"]),
        2 => new(slot, itemId, flag, [],
            ["Metadata/Monsters/AtlasInvaders/ConsumeMonsters/ConsumeBoss",
                "Metadata/Monsters/AtlasInvaders/CleansingMonsters/CleansingBoss"]),
        3 => new(slot, itemId, flag,
            ["Metadata/Monsters/AtlasMemory/BenevolenceBoss", "Metadata/Monsters/AtlasMemory/BenevolenceBossUBER"]),
        _ => new(slot, itemId, flag, []),
    };

    static Dictionary<int, string> VarietyIds(string folder)
    {
        var map = new Dictionary<int, string>();
        string path = Path.Combine(folder, "tables", "MonsterVarieties.json");
        if (!ClientTables.Exists(path)) return map;
        using var doc = GameTables.Open(path);
        int row = 0;
        foreach (var v in doc.RootElement.EnumerateArray())
            map[row++] = v.GetProperty("Id").GetString() ?? "";
        return map;
    }

    static Dictionary<int, string> AreaBosses(string folder, IReadOnlyDictionary<int, string> varieties)
    {
        var map = new Dictionary<int, string>();
        foreach (var (i, _, bosses) in AreaRows(folder, varieties))
            if (bosses.Count > 0) map[i] = bosses[^1];
        return map;
    }

    static List<(string Id, IReadOnlyList<string> Bosses)> AllAreaBossLists(string folder,
        IReadOnlyDictionary<int, string> varieties) =>
        [.. AreaRows(folder, varieties).Select(a => (a.Id, a.Bosses))];

    static List<(int Index, string Id, IReadOnlyList<string> Bosses)> AreaRows(string folder,
        IReadOnlyDictionary<int, string> varieties)
    {
        var list = new List<(int, string, IReadOnlyList<string>)>();
        string path = Path.Combine(folder, "tables", "WorldAreas.json");
        if (!ClientTables.Exists(path)) return list;
        using var doc = GameTables.Open(path);
        int i = 0;
        foreach (var a in doc.RootElement.EnumerateArray())
        {
            string[] bosses = [.. a.GetProperty("Bosses_MonsterVarietiesKeys").EnumerateArray()
                .Where(b => b.ValueKind == JsonValueKind.Number)
                .Select(b => varieties.GetValueOrDefault(b.GetInt32(), ""))
                .Where(id => id.Length > 0)];
            list.Add((i++, a.GetProperty("Id").GetString() ?? "", bosses));
        }
        return list;
    }

    static List<string> AtlasMapBosses(string folder, IReadOnlyDictionary<int, string> varieties)
    {
        var bosses = new List<string>();
        string nodes = Path.Combine(folder, "tables", "AtlasNode.json");
        string areas = Path.Combine(folder, "tables", "WorldAreas.json");
        if (!ClientTables.Exists(nodes) || !ClientTables.Exists(areas)) return bosses;
        using var n = GameTables.Open(nodes);
        using var a = GameTables.Open(areas);
        int areaCount = a.RootElement.GetArrayLength();
        foreach (var row in n.RootElement.EnumerateArray())
        {
            if (!row.GetProperty("IsNormalMap").GetBoolean() || row.GetProperty("NotOnAtlas").GetBoolean()
                || row.GetProperty("Area1") is not { ValueKind: JsonValueKind.Number } area
                || area.GetInt32() is int i && (i < 0 || i >= areaCount)) continue;
            var keys = a.RootElement[area.GetInt32()].GetProperty("Bosses_MonsterVarietiesKeys");
            if (keys.GetArrayLength() == 0) continue;
            string id = varieties.GetValueOrDefault(keys[keys.GetArrayLength() - 1].GetInt32(), "");
            if (id.Length > 0) bosses.Add(id);
        }
        return bosses;
    }

    static Dictionary<int, string> FlagIds(string folder)
    {
        var map = new Dictionary<int, string>();
        string path = Path.Combine(folder, "tables", "QuestFlags.json");
        if (!ClientTables.Exists(path)) return map;
        using var doc = GameTables.Open(path);
        int row = 0;
        foreach (var f in doc.RootElement.EnumerateArray())
            map[row++] = f.GetProperty("Id").GetString() ?? "";
        return map;
    }

    static Dictionary<int, string> ItemIds(string folder)
    {
        var map = new Dictionary<int, string>();
        string path = Path.Combine(folder, "tables", "BaseItemTypes.json");
        if (!ClientTables.Exists(path)) return map;
        using var doc = GameTables.Open(path);
        int row = 0;
        foreach (var t in doc.RootElement.EnumerateArray())
            map[row++] = t.GetProperty("Id").GetString() ?? "";
        return map;
    }

    static HashSet<int> ParseSlots(string? text) =>
        [.. (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out int n) ? n : -1)
            .Where(n => n >= 0)];

    static HashSet<string> ParseParts(string? text) =>
        [.. (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)];
}
