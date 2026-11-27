using System.Collections.Immutable;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core;

/// <summary>
/// NPCs (Agents/docs/protocol/17-npcs.md): NPCs.dat rows from the extracted tables with their object type's deserialized
/// components (data/object-types.json), and where each town's NPCs stand: the NPC lines of the town's room (.arm) through
/// the client's room placement (TownNpcs, 17-npcs.md §2).
/// </summary>
public sealed class NpcCatalog
{
    /// <summary>An NPCs.dat row. <paramref name="ShopRow"/>: the NPCShop row of its ShopSet (NPCShopSets.Shop), −1 for none.</summary>
    public sealed record Npc(int Row, string Id, string Name, IReadOnlyList<string>? Components, StateMachineData? StateMachine,
        byte TransitionState, int ShopRow = -1)
    {
        /// <summary>The model to send instead of the type's: a town room's line (<see cref="Placement.Model"/>), else the
        /// model a town room gives this NPC (TownNpcs.ModelOf), so /npc shows Irasha, whose type Metadata/NPC/NPC has none.</summary>
        public string? Model { get; init; }
        /// <summary>A fixed world z (a town room's line, <see cref="Placement.Height"/>).</summary>
        public float? Height { get; init; }
        public int HardModeShopRow { get; init; } = -1;
        /// <summary>The type's Transitionable num_states (object-types.json; 1 when it has none): the last state.</summary>
        public byte TransitionStates { get; init; } = 1;
        public bool Supported => Components is { } c && NpcObject.CanSerialize(c, StateMachine is not null);
        public override string ToString() => $"{Name} ({Id}, row {Row})";
    }

    /// <summary>The keys held on a click that pick one of an NPC's options at once, as NPCTalk's <c>__col22</c> numbers
    /// them (4 there is none): Ctrl + click on Nessa sells, Alt + click buys, both identify (17-npcs.md §3).</summary>
    public enum Shortcut { None = 0, Ctrl = 1, Alt = 2, CtrlAlt = 3 }

    /// <summary>The shortcut a click with these keys held asks for.</summary>
    public static Shortcut ShortcutFor(bool ctrl, bool alt) =>
        ctrl ? alt ? Shortcut.CtrlAlt : Shortcut.Ctrl : alt ? Shortcut.Alt : Shortcut.None;

    /// <summary>An NPCTalk row: the NPC's dialogue option <paramref name="Index"/> (column 1, what C→S 0x82 carries), its
    /// NPCTalkCategory Id (Gossip, Quests, BuyItems, SellItems, IdentifyItems, RespecPassivePointsForGold …), script and
    /// NPCTextAudio rows.</summary>
    public sealed record Talk(int Row, int Npc, int Index, string Option, string Category, string Script, IReadOnlyList<int> TextAudio)
    {
        /// <summary>A SelectHideout row's script opens the guild's list: <c>SendOpenScreen( HideoutSelectionWindow, 1 )</c>
        /// (the character's list has no argument or 0).</summary>
        public bool OpensGuildHideouts => System.Text.RegularExpressions.Regex.IsMatch(Script, @"HideoutSelectionWindow\s*,\s*1\s*\)");
        public bool Introduction { get; init; }
        /// <summary>The keys that pick this row on the click itself (NPCTalk <c>__col22</c>).</summary>
        public Shortcut Shortcut { get; init; }
        public bool Automatic { get; init; }
        public bool AutomaticContextSupported { get; init; }
        /// <summary>Like <see cref="AutomaticContextSupported"/>, but permits league quest flag columns (<c>__col15</c>,
        /// <c>__col16</c>, <c>__col17</c> != 1, <c>__col18</c>, <c>__col19</c>) for NPCs that opt into league-flag filtering.</summary>
        public bool AutomaticLeagueContextSupported { get; init; }
        public int Priority { get; init; }
        public IReadOnlyList<string> RequiredFlags { get; init; } = [];
        public IReadOnlyList<string> ExcludedFlags { get; init; } = [];
        /// <summary>NPCTalk QuestFlag3: the flags that mark the row as heard (they are the row's own QuestFlag4/5 or its
        /// quest's next step: Tarkleigh's first "Dweller of the Deep" has a1q7-ReceivedQuest, the second a1q7-Congratulated).
        /// Of two rows with the same text, the one not heard yet is the quest's next.</summary>
        public IReadOnlyList<string> HeardFlags { get; init; } = [];
        /// <summary>NPCTalk column 15: league quest flags the row marks as heard. Later rows need them in column 16
        /// (Kirac's "The Elderslayers" needs 998 from "Brother"; "Elder Reward" needs 994 from "The Elder").</summary>
        public IReadOnlyList<int> HeardLeagueFlags { get; init; } = [];
        public string? StartFlag { get; init; }
        public string? FinishFlag { get; init; }
        public int? RewardOffer { get; init; }
        /// <summary>The row's second script (NPCTalk Script2).</summary>
        public string Script2 { get; init; } = "";
        /// <summary>NPCTalk <c>__col17</c>: league quest flag set when chosen (1 is the manual-only sentinel, 1911 is none).</summary>
        public int? StartLeagueFlag { get; init; }
        /// <summary>NPCTalk <c>__col19</c>: league quest flag set when finished (1 is the sentinel, 1911 is none).</summary>
        public int? FinishLeagueFlag { get; init; }
        /// <summary>NPCTalk <c>__col15</c>: the LeagueQuestFlags rows the row sets when chosen. Data only: 1168 rows of
        /// many leagues use these three columns (1911 is the "none" sentinel of <c>__col17/__col19</c>), and the server
        /// applies them only for NPCs that opt in via <see cref="GameEvents.NpcUsesLeagueFlags"/>. The client already
        /// filters what it shows by them.</summary>
        public IReadOnlyList<int> LeagueFlagsSet { get; init; } = [];
        /// <summary>NPCTalk <c>__col16</c>: the LeagueQuestFlags rows the row requires (see <see cref="LeagueFlagsSet"/>).</summary>
        public IReadOnlyList<int> LeagueFlagsRequired { get; init; } = [];
        /// <summary>NPCTalk <c>__col18</c>: the LeagueQuestFlags rows that hide the row (see <see cref="LeagueFlagsSet"/>).</summary>
        public IReadOnlyList<int> LeagueFlagsRefused { get; init; } = [];
        public bool Available(IReadOnlySet<string> flags) => RequiredFlags.All(flags.Contains) && !ExcludedFlags.Any(flags.Contains);
        public bool Available(IReadOnlySet<string> flags, IReadOnlySet<int> leagueFlags) =>
            Available(flags) && LeagueFlagsRequired.All(leagueFlags.Contains) && !LeagueFlagsRefused.Any(leagueFlags.Contains);

        // A call names one flag or several: Eramir's "Bandit Amulets" (NPCTalk 95) has
        // SetQuestFlag("a2q7-QuestRewardTaken", "a2b-DeliveredAmulets").
        static readonly System.Text.RegularExpressions.Regex FlagCall =
            new(@"\b(SetQuestFlag|ClearQuestFlag|SetSessionQuestFlag)\(\s*((?:""?[\w-]+""?\s*,\s*)*""?[\w-]+""?)\s*\)");

        IEnumerable<(string Call, string Flag)> FlagCalls() =>
            FlagCall.Matches(Script + "\n" + Script2).SelectMany(m => m.Groups[2].Value.Split(',')
                .Select(f => (m.Groups[1].Value, f.Trim().Trim('"'))));

        /// <summary>The flags the row's scripts set when it is chosen (<c>triggerer.SetQuestFlag("a1q6-ReceivedQuest")</c>;
        /// SetSessionQuestFlag too: its flag is a session flag, never saved).</summary>
        public IReadOnlyList<string> ScriptSets => [.. FlagCalls().Where(c => c.Call != "ClearQuestFlag").Select(c => c.Flag).Distinct()];

        /// <summary>The flags the row's scripts clear (<c>triggerer.ClearQuestFlag("a1q6-DeliveredAllFlame")</c>).</summary>
        public IReadOnlyList<string> ScriptClears => [.. FlagCalls().Where(c => c.Call == "ClearQuestFlag").Select(c => c.Flag).Distinct()];

        /// <summary>The monster varieties the row's script transitions (<c>IfVarietyContains( "&lt;variety&gt;", { Transition(); } )</c>):
        /// Fairgraves' "Thank You" turns the NPC into SkeletonBossFairgraves (Quests.TurnsIntoRule).</summary>
        public IReadOnlyList<string> TransitionsVarieties => [.. System.Text.RegularExpressions.Regex
            .Matches(Script + "\n" + Script2, @"IfVarietyContains\(\s*""([^""]+)""").Select(m => m.Groups[1].Value).Distinct()];

        string Scripts => Script + "\n" + Script2;

        /// <summary><c>triggerer.FinishNPCChat()</c>: the dialogue ends with this row (Sin's Pantheon rows, Bannon's
        /// "Rescue", Lilly's and Weylam's sailing rows): no option list comes back.</summary>
        public bool FinishesChat => Scripts.Contains("FinishNPCChat(", StringComparison.Ordinal);

        /// <summary><c>triggerer.DoNPCChat(n)</c>: when this row's talk ends, the NPC's option <c>n</c> plays next (a talk in
        /// several pages: Sin's "Kitava" 882 → 883 → 884, rows no option list shows).</summary>
        public int? NextChat => System.Text.RegularExpressions.Regex.Match(Scripts, @"DoNPCChat\(\s*(\d+)\s*\)") is { Success: true } m
            ? int.Parse(m.Groups[1].Value) : null;

        /// <summary><c>triggerer.AreaTransitionTo(&lt;area&gt;, &lt;index&gt;, &lt;delay&gt;)</c>: the row moves the player to that
        /// WorldAreas Id after the delay in seconds (Lilly's "Sail to Wraeclast" → 2_6_town, Weylam's sailing rows). The
        /// index (0 or 1) is probably the arrival point; not decoded.</summary>
        public (string Area, int Index, double Delay)? AreaTransition =>
            System.Text.RegularExpressions.Regex.Match(Scripts, @"AreaTransitionTo\(\s*""?([\w]+)""?\s*,\s*(\d+)\s*,\s*([\d.]+)\s*\)") is { Success: true } m
                ? (m.Groups[1].Value, int.Parse(m.Groups[2].Value), double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture))
                : null;

        /// <summary><c>triggerer.SendOpenScreen(&lt;screen&gt;[, &lt;arg&gt;])</c>: the screen name and optional argument
        /// (<c>PantheonScreen</c>, <c>AtlasPassiveTreeScreen, 1</c>, <c>CharacterPassiveTreeScreen, 1</c>, <c>HideoutSelectionWindow, 0/1</c>).</summary>
        public (string Screen, uint Arg)? OpenScreenRequest =>
            System.Text.RegularExpressions.Regex.Match(Scripts, @"SendOpenScreen\(\s*(\w+)\s*(?:,\s*(\d+)\s*)?\)") is { Success: true } m
                ? (m.Groups[1].Value, m.Groups[2].Success ? uint.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 0u)
                : null;

        /// <summary>The screen name from <see cref="OpenScreenRequest"/>, or null.</summary>
        public string? OpensScreen => OpenScreenRequest?.Screen;

        /// <summary><c>triggerer.DropQuestItem("&lt;itemId&gt;")</c>: quest items dropped by the dialogue script.</summary>
        public IReadOnlyList<string> DropQuestItems => [.. System.Text.RegularExpressions.Regex
            .Matches(Scripts, @"DropQuestItem\(\s*""([^""]+)""\s*\)")
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];

        /// <summary><c>triggerer.SetMavenRegionFlag()</c>: Envoy's Atlas witness region flag trigger.</summary>
        public bool SetsMavenRegionFlag => Scripts.Contains("SetMavenRegionFlag(", StringComparison.Ordinal);

        /// <summary><c>SendMavenDialog(&lt;id&gt;)</c>: MavenDialog.dat row ids voiced by the script.</summary>
        public IReadOnlyList<string> MavenDialogs => [.. System.Text.RegularExpressions.Regex
            .Matches(Scripts, @"SendMavenDialog\(\s*[""']?(\w+)[""']?\s*\)")
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];

        /// <summary><c>SendTextAudio(&lt;audioId&gt;, '&lt;npcId&gt;'[, &lt;range&gt;])</c>: direct NPCTextAudio lines played by the script.</summary>
        public IReadOnlyList<(string AudioId, string NpcId, int Range)> ScriptTextAudios => [.. System.Text.RegularExpressions.Regex
            .Matches(Scripts, @"SendTextAudio\(\s*[""']?(\w+)[""']?\s*,\s*[""']([^""']+)[""']\s*(?:,\s*(\d+))?\s*\)")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value,
                m.Groups[3].Success ? int.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) : 0))];

        /// <summary><c>PlayAnimation(&lt;name&gt;)</c>: scripted animation clips requested on the speaking NPC.</summary>
        public IReadOnlyList<string> PlayAnimations => [.. System.Text.RegularExpressions.Regex
            .Matches(Scripts, @"PlayAnimation\(\s*[""']?(\w+)[""']?\s*\)")
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];

        /// <summary><c>DispatchRulesetEvent(&lt;event&gt;)</c>: both quoted (<c>"open_maven_invitation Atlas1"</c>,
        /// <c>"open_memory_line"</c>) and unquoted (<c>BanditCallVote</c>, <c>wishes</c>) event names.</summary>
        public IReadOnlyList<string> RulesetEvents => [.. System.Text.RegularExpressions.Regex
            .Matches(Scripts, @"DispatchRulesetEvent\(\s*(?:""([^""]+)""|'([^']+)'|(\w+))\s*\)")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value)
            .Where(s => s.Length > 0).Distinct(StringComparer.Ordinal)];

        /// <summary><c>StartTimer()</c> in Script2 (Act 3 Fairgraves NPCTalk 502): starts the NPC's scripted countdown timer.</summary>
        public bool StartsTimer => Script2.Contains("StartTimer(", StringComparison.Ordinal);

        /// <summary><c>IncrementCounter()</c> in Script2 (Act 3 Fairgraves NPCTalk 503): advances the NPC's scripted state counter.</summary>
        public bool IncrementsCounter => Script2.Contains("IncrementCounter(", StringComparison.Ordinal);

        /// <summary>A top-level <c>DirtyTransition();</c>: the NPC itself steps to its next Transitionable state (Dialla at
        /// the Rapture Device, NPCTalk 534: state 1 → 2, withered).</summary>
        public bool TransitionsSelf => System.Text.RegularExpressions.Regex.Replace(Scripts, @"\{[^{}]*\}", "")
            .Contains("DirtyTransition()", StringComparison.Ordinal);

        /// <summary>The object types the row's script transitions: <c>ForEachOfType( "&lt;type&gt;", { DirtyTransition(); } )</c>
        /// (Dialla's 521 opens the Ascent, 535 the wound into the Belly) and <c>ForEachInRange( r, { IfType( "&lt;type&gt;",
        /// { Transition(); } ); } )</c> (Piety's 621 opens the way to The Harvest).</summary>
        public IReadOnlyList<string> TransitionsTypes => [.. System.Text.RegularExpressions.Regex
            .Matches(Scripts, @"(?:ForEachOfType|IfType)\(\s*""([^""]+)""\s*,\s*\{\s*(?:DirtyTransition|Transition)\(\)")
            .Select(m => m.Groups[1].Value).Distinct()];
    }

    /// <summary>An NPC standing in a town: its NPCs.dat Id, cell and facing (radians); optionally the model and fixed
    /// height its town room gives it and a Transitionable state other than the type's.</summary>
    public sealed record Placement(string Id, int X, int Y, float Rotation, string? Model = null, float? Height = null,
        byte? TransitionState = null, string? ShownBy = null, string? GoneWith = null)
    {
        /// <summary>A town room's own line (TownNpcs): it may stand on a cell the walk map blocks (a stall, a pier).</summary>
        public bool FromRoom { get; init; }
        /// <summary>League quest flag id required for this placement to appear (e.g. EagonHideout requires 1492).</summary>
        public int? RequiredLeagueFlag { get; init; }
    }

    // The campaign towns are TownNpcs (their rooms' NPC lines); other towns here. Facing in radians (Positioned +0x29c).
    static readonly Dictionary<ushort, Placement[]> Towns = new()
    {
        // The Azurite Mine Encampment (Delve_Hub, HASH16 0x8b9b): Niko by the waypoint (333, 402) and the arrival
        // (330, 420); our own cell (2026-10-21). DelveMinerHub lists View Subterranean Chart, Flares and Dynamite
        // (row 1754, completing delve2), Fractured Walls, Sell Items (row 1788) and Identify Items (row 1800).
        [StashCatalog.DelveHub] = [new("Metadata/NPC/League/Delve/DelveMinerHub", 342, 410, 4.0f)],
        // The Menagerie (Menagerie_Hub): Einhar placed live with /npc_place Einhar 495 485 290 (2026-10-30).
        // EinharMenagerie has no .ot of its own (Metadata/NPC/NPC), so pass the room line's model (entrance.arm) so he
        // renders with Einhar's model while keeping the Menagerie's own NPCTalk rows ("The Blood Altar", row 1600).
        [StashCatalog.MenagerieHub] = [new("Metadata/NPC/League/Bestiary/EinharMenagerie", 495, 485, 290 * MathF.PI / 180,
            Model: "Metadata/NPC/League/Bestiary/Einhar.ao")],
        // The Forbidden Sanctum (SanctumFoyer_Fellshrine): Divinia by the waypoint (126, 149), to be placed live with /npc_place.
        // Kingsmarch (KalguuranSettlersLeague), the Rogue Harbour (HeistHub) and the Monastery of the Keepers
        // (ChayulaLeague) are not here: their hub rooms name their NPCs, so they are placed from their rooms' lines
        // (TownNpcs), as a campaign town's are.
        [StashCatalog.SanctumHub] = [new("Metadata/NPC/League/Sanctum/SanctumNPCTown", 136, 158, 4.0f)],
    };

    sealed record RegisteredNpcOverlay(long Id, string Owner, Npc Npc, int SyntheticRow);
    sealed record RegisteredTalkOverlay(long Id, string Owner, Talk Talk);

    sealed record CatalogSnapshot(
        List<Npc> Rows,
        Dictionary<int, Npc> ByRow,
        Dictionary<string, Npc> ById,
        Dictionary<(int Npc, int Index), Talk> Talk,
        long LocalRevision,
        long SharedRevision);

    static ImmutableArray<RegisteredNpcOverlay> _sharedNpcOverlays = [];
    static ImmutableArray<RegisteredTalkOverlay> _sharedTalkOverlays = [];
    static long _sharedNextId;
    static long _sharedRevision;
    static int _nextSyntheticNpcRow = 50_000;

    ImmutableArray<RegisteredNpcOverlay> _localNpcOverlays = [];
    ImmutableArray<RegisteredTalkOverlay> _localTalkOverlays = [];
    long _localNextId;
    long _localRevision;
    readonly Lock _snapshotLock = new();
    readonly List<Npc> _stockRows;
    readonly Dictionary<(int Npc, int Index), Talk> _stockTalk = [];
    readonly CatalogSnapshot _stockSnapshot;
    CatalogSnapshot _snapshot;

    /// <summary>Advances whenever shared runtime <see cref="NpcCatalog"/> overlays change.</summary>
    public static long OverlayRevision => Volatile.Read(ref _sharedRevision);

    /// <summary>Advances whenever this catalog's instance or shared runtime overlays change.</summary>
    public long Revision => Volatile.Read(ref _localRevision) + Volatile.Read(ref _sharedRevision);

    /// <summary>Registers a runtime <see cref="Npc"/> overlay visible to every <see cref="NpcCatalog"/> instance until
    /// disposed or <paramref name="owner"/> unloads via <see cref="GameEventOwners"/>.</summary>
    public static IDisposable RegisterOverlay(string owner, Npc npc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ValidateNpc(npc);
        long id = Interlocked.Increment(ref _sharedNextId);
        int syntheticRow = npc.Row < 0 ? Interlocked.Increment(ref _nextSyntheticNpcRow) : npc.Row;
        ImmutableInterlocked.Update(ref _sharedNpcOverlays, list => list.Add(new(id, owner, npc, syntheticRow)));
        Interlocked.Increment(ref _sharedRevision);
        return GameEventOwners.Own(owner, new Undo(() =>
        {
            if (ImmutableInterlocked.Update(ref _sharedNpcOverlays, list => list.RemoveAll(e => e.Id == id)))
                Interlocked.Increment(ref _sharedRevision);
        }));
    }

    /// <summary>Registers a runtime <see cref="Npc"/> overlay on this <see cref="NpcCatalog"/> instance.</summary>
    public IDisposable Register(string owner, Npc npc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ValidateNpc(npc);
        long id = Interlocked.Increment(ref _localNextId);
        int syntheticRow = npc.Row < 0 ? Interlocked.Increment(ref _nextSyntheticNpcRow) : npc.Row;
        ImmutableInterlocked.Update(ref _localNpcOverlays, list => list.Add(new(id, owner, npc, syntheticRow)));
        Interlocked.Increment(ref _localRevision);
        return GameEventOwners.Own(owner, new Undo(() =>
        {
            if (ImmutableInterlocked.Update(ref _localNpcOverlays, list => list.RemoveAll(e => e.Id == id)))
                Interlocked.Increment(ref _localRevision);
        }));
    }

    /// <summary>Registers a runtime <see cref="Talk"/> dialogue row overlay visible to every <see cref="NpcCatalog"/>
    /// instance until disposed or <paramref name="owner"/> unloads via <see cref="GameEventOwners"/>.</summary>
    public static IDisposable RegisterTalkOverlay(string owner, Talk talk)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(talk);
        long id = Interlocked.Increment(ref _sharedNextId);
        ImmutableInterlocked.Update(ref _sharedTalkOverlays, list => list.Add(new(id, owner, talk)));
        Interlocked.Increment(ref _sharedRevision);
        return GameEventOwners.Own(owner, new Undo(() =>
        {
            if (ImmutableInterlocked.Update(ref _sharedTalkOverlays, list => list.RemoveAll(e => e.Id == id)))
                Interlocked.Increment(ref _sharedRevision);
        }));
    }

    /// <summary>Registers a runtime <see cref="Talk"/> dialogue row overlay on this <see cref="NpcCatalog"/> instance.</summary>
    public IDisposable RegisterTalk(string owner, Talk talk)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(talk);
        long id = Interlocked.Increment(ref _localNextId);
        ImmutableInterlocked.Update(ref _localTalkOverlays, list => list.Add(new(id, owner, talk)));
        Interlocked.Increment(ref _localRevision);
        return GameEventOwners.Own(owner, new Undo(() =>
        {
            if (ImmutableInterlocked.Update(ref _localTalkOverlays, list => list.RemoveAll(e => e.Id == id)))
                Interlocked.Increment(ref _localRevision);
        }));
    }

    static void ValidateNpc(Npc npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        if (string.IsNullOrWhiteSpace(npc.Id))
            throw new ArgumentException("NpcCatalog.Npc.Id must be non-empty.", nameof(npc));
    }

    public IReadOnlyList<Npc> Rows => CurrentSnapshot().Rows;
    public string? Error { get; }

    NpcCatalog(List<Npc> rows, string? error, IEnumerable<Talk>? talk = null)
    {
        _stockRows = rows;
        Error = error;
        foreach (var t in talk ?? []) _stockTalk.TryAdd((t.Npc, t.Index), t);
        _stockSnapshot = BuildSnapshot(_stockRows, _stockTalk, [], [], [], [], 0, 0);
        _snapshot = _stockSnapshot;
    }

    CatalogSnapshot CurrentSnapshot()
    {
        long localRev = Volatile.Read(ref _localRevision);
        long sharedRev = Volatile.Read(ref _sharedRevision);
        var current = Volatile.Read(ref _snapshot);
        if (current.LocalRevision == localRev && current.SharedRevision == sharedRev)
            return current;
        lock (_snapshotLock)
        {
            localRev = Volatile.Read(ref _localRevision);
            sharedRev = Volatile.Read(ref _sharedRevision);
            current = _snapshot;
            if (current.LocalRevision == localRev && current.SharedRevision == sharedRev)
                return current;
            var sharedNpcs = _sharedNpcOverlays;
            var localNpcs = _localNpcOverlays;
            var sharedTalk = _sharedTalkOverlays;
            var localTalk = _localTalkOverlays;
            if (sharedNpcs.IsEmpty && localNpcs.IsEmpty && sharedTalk.IsEmpty && localTalk.IsEmpty)
            {
                var stock = _stockSnapshot with { LocalRevision = localRev, SharedRevision = sharedRev };
                Volatile.Write(ref _snapshot, stock);
                return stock;
            }
            var rebuilt = BuildSnapshot(_stockRows, _stockTalk, sharedNpcs, localNpcs, sharedTalk, localTalk, localRev, sharedRev);
            Volatile.Write(ref _snapshot, rebuilt);
            return rebuilt;
        }
    }

    static CatalogSnapshot BuildSnapshot(
        List<Npc> stockRows,
        Dictionary<(int Npc, int Index), Talk> stockTalk,
        ImmutableArray<RegisteredNpcOverlay> sharedNpcs,
        ImmutableArray<RegisteredNpcOverlay> localNpcs,
        ImmutableArray<RegisteredTalkOverlay> sharedTalk,
        ImmutableArray<RegisteredTalkOverlay> localTalk,
        long localRev,
        long sharedRev)
    {
        var rows = new List<Npc>(stockRows.Count + sharedNpcs.Length + localNpcs.Length);
        rows.AddRange(stockRows);
        var byRow = new Dictionary<int, Npc>(rows.Count + sharedNpcs.Length + localNpcs.Length);
        var byId = new Dictionary<string, Npc>(rows.Count + sharedNpcs.Length + localNpcs.Length, StringComparer.OrdinalIgnoreCase);
        var rowPos = new Dictionary<int, int>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            byRow[r.Row] = r;
            byId.TryAdd(r.Id, r);
            rowPos[r.Row] = i;
        }
        void ApplyNpcs(ImmutableArray<RegisteredNpcOverlay> entries)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                var n = entry.Npc;
                if (n.Row < 0)
                {
                    if (byId.TryGetValue(n.Id, out var existingById))
                        n = n with { Row = existingById.Row };
                    else
                    {
                        int assigned = entry.SyntheticRow;
                        while (byRow.ContainsKey(assigned)) assigned++;
                        n = n with { Row = assigned };
                    }
                }
                if (byRow.TryGetValue(n.Row, out var replacedRow)
                    && byId.TryGetValue(replacedRow.Id, out var curId) && ReferenceEquals(curId, replacedRow))
                    byId.Remove(replacedRow.Id);
                if (byId.TryGetValue(n.Id, out var replacedId) && replacedId.Row != n.Row)
                {
                    byRow.Remove(replacedId.Row);
                    if (rowPos.Remove(replacedId.Row, out int oldPos))
                    {
                        int lastIdx = rows.Count - 1;
                        if (oldPos < lastIdx)
                        {
                            var moved = rows[lastIdx];
                            rows[oldPos] = moved;
                            rowPos[moved.Row] = oldPos;
                        }
                        rows.RemoveAt(lastIdx);
                    }
                }
                if (rowPos.TryGetValue(n.Row, out int pos))
                    rows[pos] = n;
                else
                {
                    rowPos[n.Row] = rows.Count;
                    rows.Add(n);
                }
                byRow[n.Row] = n;
                byId[n.Id] = n;
            }
        }
        ApplyNpcs(sharedNpcs);
        ApplyNpcs(localNpcs);
        var talk = new Dictionary<(int Npc, int Index), Talk>(stockTalk);
        void ApplyTalk(ImmutableArray<RegisteredTalkOverlay> entries)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                var t = entries[i].Talk;
                talk[(t.Npc, t.Index)] = t;
            }
        }
        ApplyTalk(sharedTalk);
        ApplyTalk(localTalk);
        return new(rows, byRow, byId, talk, localRev, sharedRev);
    }

    sealed class Undo(Action undo) : IDisposable
    {
        Action? _undo = undo;
        public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
    }

    public static NpcCatalog Load(string folder = "data")
    {
        try { return Read(folder); }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new([], $"NPCs: {ex.Message}");
        }
    }

    /// <summary>The NPCTalk row an NPC's option index (C→S 0x82) stands for.</summary>
    public Talk? TalkOf(Npc npc, int index) => CurrentSnapshot().Talk.GetValueOrDefault((TalkSource(npc).Row, index));

    /// <summary>Every NPCTalk row of the NPC (its talk source's), by option index.</summary>
    public IEnumerable<Talk> TalksOf(Npc npc)
    {
        var snap = CurrentSnapshot();
        int source = TalkSource(npc, snap).Row;
        return snap.Talk.Values.Where(t => t.Npc == source).OrderBy(t => t.Index);
    }

    public Talk? NextConversationOf(Npc npc, IReadOnlySet<string> flags, IReadOnlySet<int> visited) => TalksOf(npc)
        .Where(t => t.Automatic && t.AutomaticContextSupported && !visited.Contains(t.Row)
            && (t.Introduction || t.Category is "Quests" or "QuestReward")
            && t.StartFlag is { } start && !flags.Contains(start) && t.Available(flags))
        .OrderByDescending(t => t.Priority).FirstOrDefault();

    public Talk? NextConversationOf(Npc npc, IReadOnlySet<string> flags, IReadOnlySet<int> visited, IReadOnlySet<int> leagueFlags) => TalksOf(npc)
        .Where(t => t.Automatic && (t.AutomaticContextSupported || t.AutomaticLeagueContextSupported) && !visited.Contains(t.Row)
            && (t.Introduction || t.Category is "Quests" or "QuestReward")
            && (t.StartFlag is { } start && !flags.Contains(start)
                || t.StartLeagueFlag is int startLeague && !leagueFlags.Contains(startLeague))
            && t.Available(flags, leagueFlags))
        .OrderByDescending(t => t.Priority).FirstOrDefault();

    /// <summary>The option a click with <paramref name="keys"/> held picks: the NPC's first row with that shortcut whose
    /// quest flags the character meets (rows that share one differ in them: Alva's "View Temple" in the player's own
    /// hideout, else "Sell Items"). The rows' other conditions (17-npcs.md §3) are not read, as for a picked option.</summary>
    public Talk? ShortcutOf(Npc npc, Shortcut keys, IReadOnlySet<string> flags) =>
        keys == Shortcut.None ? null : TalksOf(npc).FirstOrDefault(t => t.Shortcut == keys && t.Available(flags));

    public bool HasAvailableReward(Npc npc, int offer, IReadOnlySet<string> flags)
    {
        var snap = CurrentSnapshot();
        int source = TalkSource(npc, snap).Row;
        return snap.Talk.Values.Any(t => t.Npc == source && t.RewardOffer == offer
            && t.Category != "BanditRespec" && t.Available(flags));
    }

    public bool HasAvailableReward(Npc npc, int offer, IReadOnlySet<string> flags, IReadOnlySet<int> leagueFlags)
    {
        var snap = CurrentSnapshot();
        int source = TalkSource(npc, snap).Row;
        return snap.Talk.Values.Any(t => t.Npc == source && t.RewardOffer == offer
            && t.Category != "BanditRespec" && t.Available(flags, leagueFlags));
    }

    /// <summary>The quest reward offers any NPC currently shows (the quests complete), for the vendors' gem stock.</summary>
    public IReadOnlySet<int> AvailableRewards(IReadOnlySet<string> flags) =>
        CurrentSnapshot().Talk.Values.Where(t => t.RewardOffer is { } offer && t.Category != "BanditRespec" && t.Available(flags))
            .Select(t => t.RewardOffer!.Value).ToHashSet();

    /// <summary>The NPC whose NPCTalk rows (and shop) this one uses: itself, or for a copy without rows of its own (the
    /// hideout Kirac, NPCs.dat row 412) the first row of the same name that has them (Kirac, 410).</summary>
    public Npc TalkSource(Npc npc) => TalkSource(npc, CurrentSnapshot());

    static Npc TalkSource(Npc npc, CatalogSnapshot snap) => snap.Talk.Keys.Any(k => k.Npc == npc.Row) ? npc
        : snap.Rows.FirstOrDefault(r => r.Name == npc.Name && snap.Talk.Keys.Any(k => k.Npc == r.Row)) ?? npc;

    /// <summary>The town's NPCs, or none.</summary>
    // Hideouts: Nessa, at the cell the user picked in the Lush Hideout (2026-10-20); the real game has no town NPCs in
    // hideouts. Niko and Kirac (their hideout NPCs.dat rows) stand to her right and left on screen (2026-10-21): screen
    // right is +x / -y on the grid.
    static readonly Placement[] Hideout =
    [
        new("Metadata/NPC/Act1/Nessa", 256, 351, 4.0f),
        new("Metadata/NPC/League/Delve/DelveMinerHideout", 263, 344, 4.0f),
        new("Metadata/NPC/Missions/Hideout/Helena", 270, 337, 4.0f),
        new("Metadata/NPC/Epilogue/KiracHideout", 249, 358, 4.0f),
        new("Metadata/NPC/Epilogue/LillyHideout", 235, 372, 4.0f),
        new("Metadata/NPC/League/Bestiary/EinharHideout", 242, 365, 4.0f),
        // Last, and two steps past Helena on screen: on every hideout's floor this moves nothing placed before or after him.
        new("Metadata/NPC/League/Kalguur/VillageFaustusHideout", 284, 323, 4.0f),
        // Eagon Caeserius (Secrets of the Atlas): appears once unlocked via EagonWild (LQF 1492 EagonUnlocked).
        new("Metadata/NPC/Epilogue/EagonHideout", 291, 316, 4.0f) { RequiredLeagueFlag = 1492 },
    ];

    public static IReadOnlyList<Placement> HideoutNpcs => Hideout;

    /// <summary>The NPCs of an area instance of <paramref name="seed"/>: a campaign town's (the notes say which room's are
    /// left out for another seed), another town's, a hideout's, or none.</summary>
    public static IReadOnlyList<Placement> In(ushort areaHash, uint seed, List<string>? notes = null) =>
        TownNpcs.Of(areaHash) is { } town ? town.Placements(seed, notes)
        : Towns.GetValueOrDefault(areaHash) ?? (HideoutCatalog.ByArea(areaHash) is not null ? Hideout : []);

    /// <summary>The NPC a placement stands for, with the placement's model, height and state.</summary>
    public Npc? Place(Placement p) => Find(p.Id) is { } npc
        ? npc with { Model = p.Model ?? npc.Model, Height = p.Height, TransitionState = p.TransitionState ?? npc.TransitionState }
        : null;

    /// <summary>An NPC by row, Id or name (exact name first, then the first Id or name containing the text).</summary>
    public Npc? Find(string query)
    {
        var snap = CurrentSnapshot();
        if (int.TryParse(query, out int row)) return snap.ByRow.GetValueOrDefault(row);
        if (snap.ById.TryGetValue(query, out var byId)) return byId;
        return snap.Rows.FirstOrDefault(r => r.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? snap.Rows.FirstOrDefault(r => r.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    static NpcCatalog Read(string folder)
    {
        if (!ClientTables.Has("NPCs")) return new([], $"the NPCs client table is missing from {ClientTables.DefaultPath}");
        var components = new Dictionary<string, IReadOnlyList<string>>();
        var stateMachines = new Dictionary<string, StateMachineData>();
        var transitionStates = new Dictionary<string, byte>();
        var sharedStates = ObjectTypes.SharedStates();
        foreach (var (type, t) in ObjectTypes.All())
        {
            if (!type.StartsWith("Metadata/NPC/", StringComparison.OrdinalIgnoreCase)) continue;
            components[type] = ObjectTypes.Components(t);
            if (ObjectTypes.StateMachine(t, sharedStates) is { } machine) stateMachines[type] = machine;
            if (t.IntOrNull("transitionable_num_states") is int numStates)
                transitionStates[type] = (byte)Math.Clamp(numStates, 1, 127);
        }
        var shopOfSet = new List<int>();
        var hardModeShopOfSet = new List<int>();
        if (ClientTables.TableOrNull("NPCShopSets") is { } shopSets)
        {
            shopOfSet = [.. shopSets.Select(s => s.IntOrNull("Shop") ?? -1)];
            hardModeShopOfSet = [.. shopSets.Select(s => s.IntOrNull("ShopRuthless") ?? -1)];
        }
        var rows = new List<Npc>();
        foreach (var r in ClientTables.Table("NPCs"))
        {
            string id = r.Text("Id") ?? "";
            // The client files every NPCs.dat row under its Id on the row's Metadata .ot (the register's 0xac-byte
            // table, base at row +0x10; 04-player-entity.md §1): an Id without an .ot of its own (Act 6's Tarkleigh on
            // Act 1's) has its Metadata's components.
            string meta = r.TextOrNull("Metadata") ?? "";
            string type = components.ContainsKey(id) || !components.ContainsKey(meta) ? id : meta;
            // Without the object_types table, assume the base NPC list.
            IReadOnlyList<string>? list = components.Count == 0 ? NpcObject.BaseComponents : components.GetValueOrDefault(type);
            int set = r.IntOrNull("ShopSet") ?? -1;
            rows.Add(new Npc(rows.Count, id, r.Text("Name") ?? "", list,
                stateMachines.GetValueOrDefault(type), transitionStates.GetValueOrDefault(type, (byte)1),
                set >= 0 && set < shopOfSet.Count ? shopOfSet[set] : -1)
            {
                Model = TownNpcs.ModelOf(id),
                TransitionStates = transitionStates.GetValueOrDefault(type, (byte)1),
                HardModeShopRow = set >= 0 && set < hardModeShopOfSet.Count ? hardModeShopOfSet[set] : -1,
            });
        }
        return new(rows, null, ReadTalk(folder));
    }

    static List<Talk> ReadTalk(string folder)
    {
        if (!ClientTables.Has("NPCTalk") || !ClientTables.Has("NPCTalkCategory")) return [];
        List<string> names = [.. ClientTables.Table("NPCTalkCategory").Select(c => c.Text("Id") ?? "")];
        List<string> flags = [];
        if (ClientTables.Has("QuestFlags"))
            flags = [.. ClientTables.Table("QuestFlags").Select(f => f.Text("Id") ?? "")];
        string? Flag(int row) => row >= 0 && row < flags.Count && flags[row] != "NEVER_SET" ? flags[row] : null;
        var talk = new List<Talk>();
        foreach (var r in ClientTables.Table("NPCTalk"))
        {
            int Int(string name) => !r.IsNull(name) ? r.Int(name) : -1;
            int category = Int("Category");
            int OptionalInt(string name) => (r.IntOrNull(name) ?? -1);
            string[] FlagList(string name) => [.. r.Ints(name).Where(i => i >= 0 && i < flags.Count).Select(i => flags[i])];
            int col17 = OptionalInt("__col17"), col19 = OptionalInt("__col19");
            bool nonLeagueAutoContext = !r.IsNull("__col28") && r.Ints("__col28").Length == 0
                && OptionalInt("__col27") == 60 && OptionalInt("__col29") == 60
                && OptionalInt("__col31") == 0 && OptionalInt("__col32") < 0
                && r.Has("BaseItemTypes") && r.IsNull("BaseItemTypes")
                && r.BoolOrNull("__col14") == false
                && r.BoolOrNull("__col20") == false;
            talk.Add(new Talk(talk.Count, Int("NPCKey"), Int("__col1"), r.Text("DialogueOption") ?? "",
                category >= 0 && category < names.Count ? names[category] : "", r.Text("Script") ?? "",
                [.. r.Ints("NPCTextAudioKeys")])
            {
                Introduction = r.BoolOrNull("__col13") == true,
                Shortcut = OptionalInt("__col22") is >= 1 and <= 3 and var keys ? (Shortcut)keys : Shortcut.None,
                Automatic = r.BoolOrNull("__col33") == true,
                Priority = OptionalInt("__col34"),
                AutomaticContextSupported = nonLeagueAutoContext
                    && new[] { "__col15", "__col16", "__col18" }.All(k => !r.IsNull(k) && r.Ints(k).Length == 0)
                    && col17 == 0x777 && col19 == 0x777,
                AutomaticLeagueContextSupported = nonLeagueAutoContext
                    && (col17 == 0x777 || col17 is > 1 and <= Quests.League.Count)
                    && (col19 == 0x777 || col19 is > 1 and <= Quests.League.Count),
                RequiredFlags = FlagList("QuestFlag1"), ExcludedFlags = FlagList("QuestFlag2"), HeardFlags = FlagList("QuestFlag3"),
                HeardLeagueFlags = [.. r.Ints("__col15").Where(i => i is > 0 and < 0x777)],
                StartFlag = Flag(OptionalInt("QuestFlag4")), FinishFlag = Flag(OptionalInt("QuestFlag5")),
                StartLeagueFlag = col17 is > 1 and <= Quests.League.Count ? col17 : null,
                FinishLeagueFlag = col19 is > 1 and <= Quests.League.Count ? col19 : null,
                RewardOffer = OptionalInt("QuestRewardOffersKey") is int offer and >= 0 ? offer : null,
                Script2 = r.TextOrNull("Script2") ?? "",
                LeagueFlagsSet = r.IsNull("__col15") ? [] : [.. r.Ints("__col15")],
                LeagueFlagsRequired = r.IsNull("__col16") ? [] : [.. r.Ints("__col16")],
                LeagueFlagsRefused = r.IsNull("__col18") ? [] : [.. r.Ints("__col18")],
            });
        }
        return talk;
    }
}
