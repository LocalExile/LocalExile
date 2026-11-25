using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Breach;

/// <summary>
/// The numbers of the Breach encounter. Every value says where it comes from: [data] a client table or a capture,
/// ESTIMATED a guess.
/// </summary>
public static class BreachTables
{
    /// <summary>The Initiator's encounter_state values.</summary>
    public const int Inactive = 0, Expanding = 1, Enraged = 2, ClosingEnraged = 3, Closing = 4;

    /// <summary>The Initiator: Metadata/MiscellaneousObjects/Brequel/BrequelInitiator.</summary>
    public const string InitiatorType = "Metadata/MiscellaneousObjects/Brequel/BrequelInitiator";

    /// <summary>The clip the Expanding state plays first (1.0 s) and the end clip (2.666 s). A state never follows state
    /// 1 by less than the activate clip.</summary>
    public const double ClipActivate = 1.000, ClipEnd = 2.666;

    /// <summary>How close a player comes to touch it (cells): 15 [data] (BrequelStabiliser interaction_distance_override).</summary>
    public const double TouchRadius = 15;

    /// <summary>How many Breaches of an area may be open at once: 2 (3.29.0 patch note).</summary>
    public const int MaxOpen = 2;

    /// <summary>How long a Breach stays open after the touch without the enrage: 60 s. ESTIMATED.</summary>
    public const double OpenSeconds = 60;

    /// <summary>How long state 2 (enraged) lasts before state 3 (closing_enraged): 1.0 s. ESTIMATED (the capture leaves
    /// 0.9-1.8 s for it once state 3's end clip is taken off, and its two touched Breaches disagree).</summary>
    public const double EnragedSeconds = 1.0;

    /// <summary>The environment override's blend time (seconds): 2.0, a fade in. ESTIMATED (Delirium's pattern).</summary>
    public const double EnvSeconds = 2.0;

    /// <summary>How long after state 3 / 4 the Initiator and what is left of its wave go: the end clip.</summary>
    public const double RemoveAfter = ClipEnd;

    /// <summary>How far a wave pack's centre stands from the Breach's cell (cells): up to 110 [data] (the breach-open
    /// capture's 28 type-resolved creates, 26 within ~110 cells of their pack, median ~30). The captures carry no row of
    /// the Breach object itself, so the origin is the Breach's cell by assumption.</summary>
    public const double WaveMaxDistance = 110;

    /// <summary>How many members one tick spawns at most: 16. The fastest measured second is 26.5/s, about 7 a tick at
    /// 4 Hz, so 16 leaves room for a slow tick to catch up without dumping the wave at once.</summary>
    public const int MaxWavePerTick = 16;

    /// <summary>The wave's shape: members per second of the open window, the mean of the two touched official Breaches
    /// (281 and 362 members over ~38 s and ~41 s) [data].</summary>
    public static readonly double[] WaveRates = [18, 23, 15, 8, 16, 14, 8.5, 11.5, 13.5, 5, 6.5, 6, 1, 4.5, 7, 8,
        10.5, 26.5, 18.5, 23, 12.5, 3.5, 11.5, 2, 6, 6, 1, 11, 9, 2.5, 0, 6, 1, 2.5, 1.5, 0.5, 0, 0, 0, 0.5, 0.5];

    /// <summary>How many seconds the wave runs: the last measured member is at s40.</summary>
    public static int WaveSeconds => WaveRates.Length;

    /// <summary>How many members the wave has spawned by <paramref name="age"/> seconds after the touch: the curve above,
    /// integrated (a fraction of the current second counts in part).</summary>
    public static double ExpectedWaveMembers(double age)
    {
        if (age <= 0) return 0;
        int whole = Math.Min((int)age, WaveRates.Length);
        double total = 0;
        for (int i = 0; i < whole; i++) total += WaveRates[i];
        if (whole < WaveRates.Length) total += WaveRates[whole] * (age - whole);
        return total;
    }

    /// <summary>The members' measured rarity mix (both captures: normal 524, magic 108, rare 11 of 643) [data], drawn
    /// per pack (a magic pack is magic throughout, a rare pack has one rare): the per-pack draw is ESTIMATED.</summary>
    public const double NormalShare = 524.0 / 643, MagicShare = 108.0 / 643;

    /// <summary>How far apart the open Breach's ramp lines go out (seconds): 0.2 [data] (172 state-1 rows over 33.8 s
    /// and 173 over 37.0 s on the two touched official Breaches). At 0.2 s the radius ramp (39 -&gt; 150
    /// cells) finishes 34.2 s after the touch, so with breach_enrage the Breach enrages there and closes at ~35.2 s.</summary>
    public const double RulesetGap = 0.2;

    /// <summary>The environment's row: BreachArtVariations row 0's Encounter (Environments 1187 LeagueBrequelEncounter)
    /// [data]; null without the table. Read from the receiving client's own tables, never taken from a capture: the
    /// captures' 0xdf sends 1182, the same Id on a client whose Environments has 5 rows fewer from row 1055.</summary>
    public static ushort? EnvRow { get; private set; }

    /// <summary>The Rulesets row of the Unstable Breach: ExpandingBrequelEncounterRules, 164 [data]; null without it.</summary>
    public static byte? RulesetRow { get; private set; }

    /// <summary>The Initiator's components and StateMachine (its one shared state, encounter_state) from object_types,
    /// with the StateMachine's component index; null when the data cannot write it.</summary>
    public static (IReadOnlyList<string> Components, StateMachineData States, byte Index)? Initiator { get; private set; }

    /// <summary>The wave's packs: the MonsterPacks rows of the Unstable Breach [data]. The rows named "breach-*" hold the
    /// pre-3.29 Breach monsters, and "brequelfort-*" stand in the Hive's areas (WorldAreasKeys 1924, 1925).</summary>
    public static IReadOnlyList<PackCatalog.Pack> Packs { get; private set; } = [];

    static readonly string[] PackIds = ["brequel-neutral", "brequel-chaos", "brequel-physical", "brequel-cold", "brequel-fire", "brequel-lightning"];

    /// <summary>The life a Breach monster has more at this level (%, BreachLifeScalingPerLevel: L68 = 107, L84 = 208)
    /// [data].</summary>
    public static int LifeScale(int level) => LifeScales.GetValueOrDefault(Math.Clamp(level, 1, 100), 0);

    static Dictionary<int, int> LifeScales = [];

    /// <summary>Builds this module's game data when it loads.</summary>
    public static void LoadGameData()
    {
        EnvRow = ClientTables.TableOrEmpty("BreachArtVariations").Where(r => r.Index == 0).Select(r => r.IntOrNull("Encounter")).FirstOrDefault()
            is int env and >= 0 and <= ushort.MaxValue ? (ushort)env : null;
        RulesetRow = ClientTables.TableOrEmpty("Rulesets").Where(r => r.TextOrNull("Id") == "ExpandingBrequelEncounterRules")
            .Select(r => (int?)r.Index).FirstOrDefault() is int row and >= 0 and <= byte.MaxValue ? (byte)row : null;
        var scales = new Dictionary<int, int>();
        foreach (var r in ClientTables.TableOrEmpty("BreachLifeScalingPerLevel"))
            if (r.IntOrNull("Level") is int level && r.IntOrNull("Scale") is int scale) scales[level] = scale;
        LifeScales = scales;
        Packs = [.. PackIds.Select(id => global::Game.Packs.Find(id)).OfType<PackCatalog.Pack>().Where(p => p.Entries.Count > 0)];
        Initiator = null;
        if (QuestObjectTypes.Find(InitiatorType) is { } info && QuestObject.CanSerialize(info.Components, true)
            && ObjectTypes.All().Where(t => t.Type == InitiatorType).Select(t => (ClientRow?)t.Row).FirstOrDefault() is { } type
            && ObjectTypes.StateMachine(type, ObjectTypes.SharedStates()) is { } states && states.IndexOf("encounter_state") >= 0)
            Initiator = (info.Components, states, (byte)info.Components.ToList().IndexOf("StateMachine"));
    }
}
