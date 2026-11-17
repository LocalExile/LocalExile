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
        // Every kill asks: only a monster some Voidstone names needs the character's save looked at.
        if (!_stones.Any(s => s.Defeat.Contains(varietyId) || s.AllOf?.Contains(varietyId) == true)) return null;
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
        if (ClientTables.Has("MavenFights"))
        {
            foreach (var row in ClientTables.Table("MavenFights"))
            {
                if (row.IntOrNull("BaseItemType") is not int item) continue;
                int[] areas = [.. row.Ints("WitnessAreas")];
                int count = (row.IntOrNull("WitnessesRequired") ?? areas.Length);
                invites[item] = new(row.Text("Id") ?? "", item, count,
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
        if (ClientTables.Has("AtlasVoidstoneSlots"))
        {
            var doc = ClientTables.Table("AtlasVoidstoneSlots");
            int i = 0;
            foreach (var row in doc)
            {
                int slot = (row.IntOrNull("SlotNumber") ?? i);
                i++;
                string itemId = items.GetValueOrDefault((row.IntOrNull("Voidstone") ?? -1), "");
                string flag = flags.GetValueOrDefault((row.IntOrNull("QuestFlag") ?? -1), "");
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
        if (!ClientTables.Has("MonsterVarieties")) return map;
        var doc = ClientTables.Table("MonsterVarieties");
        int row = 0;
        foreach (var v in doc)
            map[row++] = v.Text("Id") ?? "";
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
        if (!ClientTables.Has("WorldAreas")) return list;
        var doc = ClientTables.Table("WorldAreas");
        int i = 0;
        foreach (var a in doc)
        {
            string[] bosses = [.. a.Ints("Bosses_MonsterVarietiesKeys")
                .Select(b => varieties.GetValueOrDefault(b, ""))
                .Where(id => id.Length > 0)];
            list.Add((i++, a.Text("Id") ?? "", bosses));
        }
        return list;
    }

    static List<string> AtlasMapBosses(string folder, IReadOnlyDictionary<int, string> varieties)
    {
        var bosses = new List<string>();
        if (!ClientTables.Has("AtlasNode") || !ClientTables.Has("WorldAreas")) return bosses;
        var n = ClientTables.Table("AtlasNode");
        var a = ClientTables.Table("WorldAreas");
        int areaCount = a.Count;
        foreach (var row in n)
        {
            if (!row.Bool("IsNormalMap") || row.Bool("NotOnAtlas")
                || row.IntOrNull("Area1") is not int area
                || area is int i && (i < 0 || i >= areaCount)) continue;
            var keys = a[area].Ints("Bosses_MonsterVarietiesKeys");
            if (keys.Length == 0) continue;
            string id = varieties.GetValueOrDefault(keys[^1], "");
            if (id.Length > 0) bosses.Add(id);
        }
        return bosses;
    }

    static Dictionary<int, string> FlagIds(string folder)
    {
        var map = new Dictionary<int, string>();
        if (!ClientTables.Has("QuestFlags")) return map;
        var doc = ClientTables.Table("QuestFlags");
        int row = 0;
        foreach (var f in doc)
            map[row++] = f.Text("Id") ?? "";
        return map;
    }

    static Dictionary<int, string> ItemIds(string folder)
    {
        var map = new Dictionary<int, string>();
        if (!ClientTables.Has("BaseItemTypes")) return map;
        var doc = ClientTables.Table("BaseItemTypes");
        int row = 0;
        foreach (var t in doc)
            map[row++] = t.Text("Id") ?? "";
        return map;
    }

    static HashSet<int> ParseSlots(string? text) =>
        [.. (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out int n) ? n : -1)
            .Where(n => n >= 0)];

    static HashSet<string> ParseParts(string? text) =>
        [.. (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)];
}
