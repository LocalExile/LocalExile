using System.Text.Json;

/// <summary>
/// The quest engine (Agents/docs/protocol/12-quests.md §5). A character's quest progress is the set of its quest flags
/// (QuestFlags.dat Ids, saved by name in data/saves/). Each quest is a script: it turns game events into flags and says
/// what its flags gate (an exit). The Gateway asks every script.
/// </summary>
static class Quests
{
    public abstract class Script
    {
        /// <summary>Flags set when a monster of this MonsterVarieties Id dies in this area.</summary>
        public virtual IEnumerable<string> OnMonsterKilled(string areaId, string varietyId) => [];

        /// <summary>Flags set when the player comes within <see cref="SightRange"/> of a living monster of this
        /// MonsterVarieties Id in this area.</summary>
        public virtual IEnumerable<string> OnMonsterSeen(string areaId, string varietyId) => [];

        /// <summary>The MonsterVarieties Ids whose sighting this script cares about in this area (the player tick looks only for them).</summary>
        public virtual IEnumerable<string> Watched(string areaId) => [];

        /// <summary>The level a character reaches at least when it kills a monster of this variety in this area, or 0.</summary>
        public virtual int LevelAfterKill(string areaId, string varietyId) => 0;

        /// <summary>The flag a character needs to take the exit from one area to the other, or null.</summary>
        public virtual string? FlagToEnter(string fromArea, string toArea) => null;

        /// <summary>For a room's server object of this type in this area: the flag that takes it away, or null when the
        /// quest does not manage it (only managed room objects are created).</summary>
        public virtual string? RoomObjectRemovedBy(string areaId, string objectType) => null;
    }

    /// <summary>a1q1 "Enemy at the Gate": Hillock stands before Lioneye's Watch at the end of The Twilight Strand. Coming
    /// near him sets SeenHillock (the quest log's "Kill Hillock"); his death sets KilledHillock, which takes down the force
    /// wall in front of the gate and opens the way into the town (12-quests.md §6).</summary>
    public sealed class EnemyAtTheGate : Script
    {
        public const string KilledHillock = "a1q1-KilledHillock", SeenHillock = "a1q1-SeenHillock";
        const string Strand = "1_1_1", Town = "1_1_town";
        const string Hillock = "Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal";

        public override IEnumerable<string> OnMonsterKilled(string areaId, string varietyId) =>
            areaId == Strand && varietyId == Hillock ? [KilledHillock] : [];

        public override IEnumerable<string> OnMonsterSeen(string areaId, string varietyId) =>
            areaId == Strand && varietyId == Hillock ? [SeenHillock] : [];

        public override IEnumerable<string> Watched(string areaId) => areaId == Strand ? [Hillock] : [];

        // QuestStates "You have killed Hillock … and levelled up. Open the Passive Skill Screen …": his kill gives
        // level 2 (his own experience alone does not reach it).
        public override int LevelAfterKill(string areaId, string varietyId) => areaId == Strand && varietyId == Hillock ? 2 : 0;

        public override string? FlagToEnter(string fromArea, string toArea) => fromArea == Strand && toArea == Town ? KilledHillock : null;

        // The force wall across the way to the gate (town_entrance_exclude.arm), until Hillock is dead.
        const string ForceWall = "Metadata/Terrain/Act1/Area1/Objects/Tutorial_Blocker_3";

        public override string? RoomObjectRemovedBy(string areaId, string objectType) =>
            areaId == Strand && objectType == ForceWall ? KilledHillock : null;
    }

    /// <summary>a1q4 "Breaking Some Eggs": the Glyph Wall in The Mud Flats takes the three glyphs from the Rhoa Nests and
    /// sets PlacedGlyphs, which opens the Submerged Passage (12-quests.md §1–4; the objects are <see cref="GlyphQuest"/>'s).
    /// The client stops the player at its own invisible blockers until then; the Gateway also keeps the exit closed.</summary>
    public sealed class BreakingSomeEggs : Script
    {
        public override string? FlagToEnter(string fromArea, string toArea) =>
            fromArea == GlyphQuest.Area && toArea == GlyphQuest.Passage ? GlyphQuest.PlacedGlyphs : null;
    }

    static readonly Script[] All = [new EnemyAtTheGate(), new BreakingSomeEggs()];

    /// <summary>Flags every character has from the start (added on login, not a quest's), all for the hideout
    /// (21-hideouts.md §1): HideoutUnlocked; HASH32 0x2f1b9c25 (row 718, the schema's "HideoutJukeboxEnabled"), which the
    /// HUD's hideout panel checks (0x14216c840); CanEditHideout, tested before the Edit button shows (live trace
    /// 2026-10-20); MapDeviceActive, the map device's per-player "active" state (its .ot:
    /// per_player_quest_flag_state_active_1). ThisIsMyHideout (row 717): hideout NPCs' own options need it in NPCTalk
    /// QuestFlag1 (Niko's View Subterranean Chart and Visit Mine Encampment); without it the client lists their
    /// "not my hideout" rows (717 in QuestFlag2). Every hideout here is the player's own. EndGameAvailable is the
    /// epilogue flag the client requires before an Atlas map can be activated.</summary>
    public static readonly string[] EveryCharacter = ["HideoutUnlocked", "HideoutJukeboxEnabled", "CanEditHideout", "MapDeviceActive",
        CompletedAllTutorials, ThisIsMyHideout, EndGameAvailable];

    /// <summary>QuestFlags row 717, sent only while the player is in a hideout (Program.PlayerQuestFlags).</summary>
    public const string ThisIsMyHideout = "ThisIsMyHideout";

    /// <summary>QuestFlags row 367: the tutorials are done, as on an account that has played before. Quest states that
    /// teach (a1q1 after Hillock: "Open the Passive Skill Screen …", FlagsMissing 280/367) give way to the next step
    /// ("Enter through the gate for a reward").</summary>
    public const string CompletedAllTutorials = "CompletedAllTutorials";

    /// <summary>CharacterStartQuestState/QuestStates EpilogueAtlasDemo flag: without it the client refuses to activate an
    /// Atlas map ("You do not have the required Quest State to access this area"; live 2026-10-29).</summary>
    public const string EndGameAvailable = "EndGameAvailable";

    /// <summary>
    /// League quest flags: a set of their own, kept in the Player component's second flag set (+0x1d0). They are not a
    /// table: the client has the names built in, sorted, with their ids (0x142fca900, 0x776 entries; lookup
    /// 0x1423f1fb0). Ids start at 1: the client tests bit id − 1 (0x14155cdf0, and the map device's Targetable, live
    /// 2026-10-20). Every character has the hideout ones (21-hideouts.md §1).
    /// </summary>
    public static class League
    {
        public const int HaveGuild = 2, UnlockedHideouts = 3, HaveHideout = 200, AtlasUnlocked = 296, UnlockedMapDevice = 350;

        /// <summary>Niko met in a map: his Delve options (View Subterranean Chart, Visit Mine Encampment) need it. NPCTalk
        /// columns 15-18 are league quest flag ids (16 required, 18 refused: the first-meeting "Talk to Niko" has 68 there).</summary>
        public const int DelveEncounterComplete = 68;

        /// <summary>The World panel's special pins (SpecialMapPins.dat column 2 is the league quest flag id that shows each):
        /// HaveGuildHideout, MenagerieUnlocked, NikoHaveMinedSulphite (the Mine Encampment), SanctumIntroNewGivenTexts,
        /// VillageKingsmarchUnlocked, HeistVisitedHub, ChayulaLeagueAreaUnlocked, AncestralHallsOfDeadUnlocked,
        /// NecropolisAreaUnlocked, DeepwaterBoatUnlocked (0x140a9bd50).</summary>
        public static readonly int[] WorldPanelPins = [1033, 409, 9, 414, 608, 736, 636, 677, 419, 1834];

        public static readonly int[] EveryCharacter = [UnlockedHideouts, HaveHideout, AtlasUnlocked, UnlockedMapDevice, DelveEncounterComplete,
            .. WorldPanelPins];

        /// <summary>The bits the client keeps for these ids (id − 1).</summary>
        public static IEnumerable<int> Bits(IEnumerable<int> ids) => ids.Where(id => id > 0).Select(id => id - 1);
    }

    public static IReadOnlyList<string> OnMonsterKilled(string areaId, string varietyId) =>
        [.. All.SelectMany(s => s.OnMonsterKilled(areaId, varietyId))];

    /// <summary>How close (cells) the player must come to a monster for <see cref="OnMonsterSeen"/>: our own number, about
    /// half a screen.</summary>
    public const double SightRange = 60;

    public static int LevelAfterKill(string areaId, string varietyId) => All.Max(s => s.LevelAfterKill(areaId, varietyId));

    public static IReadOnlyList<string> OnMonsterSeen(string areaId, string varietyId) =>
        [.. All.SelectMany(s => s.OnMonsterSeen(areaId, varietyId))];

    public static IReadOnlySet<string> Watched(string areaId) => All.SelectMany(s => s.Watched(areaId)).ToHashSet();

    public static string? RoomObjectRemovedBy(string areaId, string objectType) =>
        All.Select(s => s.RoomObjectRemovedBy(areaId, objectType)).FirstOrDefault(f => f is not null);

    public static string? FlagToEnter(string fromArea, string toArea) =>
        All.Select(s => s.FlagToEnter(fromArea, toArea)).FirstOrDefault(f => f is not null);

    /// <summary>QuestFlags.dat rows by Id, from an extracted QuestFlags.json (the row is what the client keeps).</summary>
    public static Dictionary<string, int> ParseRows(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var rows = new Dictionary<string, int>();
        int i = 0;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("Id", out var id) && id.GetString() is { } name) rows.TryAdd(name, i);
            i++;
        }
        return rows;
    }

    static readonly Lazy<Dictionary<string, int>> TableRows = new(() =>
    {
        const string file = "data/tables/QuestFlags.json";
        try { return File.Exists(file) ? ParseRows(File.ReadAllText(file)) : []; }
        catch (Exception ex) when (ex is IOException or JsonException) { Console.WriteLine($"[gateway] {file}: {ex.Message}"); return []; }
    });

    /// <summary>The QuestFlags rows of these flag Ids, ascending; unknown Ids are skipped.</summary>
    public static IReadOnlyList<int> Rows(IEnumerable<string> ids, IReadOnlyDictionary<string, int>? rows = null) =>
        [.. ids.Select(id => (rows ?? TableRows.Value).TryGetValue(id, out int r) ? r : -1).Where(r => r >= 0).Order()];

    /// <summary>Whether the client has a quest flag: the character's own, or among the rows the client gets (the
    /// player's flag set also carries the World panel's access flags: an activated waypoint's area, 16-waypoints.md §4).
    /// The client opens its ways by these, so the server's checks use the same.</summary>
    public static bool ClientHas(string flag, IReadOnlySet<string> own, IReadOnlyCollection<int> clientRows, IReadOnlyDictionary<string, int>? rows = null) =>
        own.Contains(flag) || Rows([flag], rows) is [int row] && clientRows.Contains(row);

    /// <summary>Of the flags a change set or cleared, those the client now has differently: a flag the client had already
    /// (through an access flag) changes nothing it sees, so nothing is created or removed for it.</summary>
    public static List<string> EffectiveChanges(IEnumerable<string> changed, Func<string, bool> before, Func<string, bool> after) =>
        [.. changed.Where(f => before(f) != after(f))];

    public static IReadOnlySet<string> Names(IEnumerable<int> rows)
    {
        var wanted = rows.ToHashSet();
        return TableRows.Value.Where(kv => wanted.Contains(kv.Value)).Select(kv => kv.Key).ToHashSet();
    }
}
