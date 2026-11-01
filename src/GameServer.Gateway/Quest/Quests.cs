using System.Collections;
using System.Text.Json;

/// <summary>
/// The quest engine (Agents/docs/protocol/12-quests.md §5, Agents/docs/game/campaign-quests.md). A character's quest progress
/// is the set of its quest flags (QuestFlags.dat Ids, saved by name). Each quest is a declarative table of <see cref="Rule"/>s
/// that turn game events into flags and say what the flags gate; a module gives the tables, one <see cref="Act"/> per act
/// (Modules/Core/World/QuestObjects/Acts/Act&lt;n&gt;.cs), and <see cref="Register"/>s them while it is loaded. The Gateway and
/// the Game module ask the tables (the static queries below) and do the rest generically from the client's data
/// (Modules/Core/Game/World/Quest/):
/// <list type="bullet">
/// <item>quest items: their .it Quest component (<see cref="QuestItemFlags"/>: grant, extra, remove and use flags) decides
/// the flags they hold, when they go and when they may drop again (<see cref="QuestItems"/>);</item>
/// <item>NPC dialogue: NPCTalk's QuestFlag4 (on choosing) and QuestFlag5 (on finishing) and the script's
/// SetQuestFlag/ClearQuestFlag/SetSessionQuestFlag (<see cref="NpcCatalog.Talk"/>); a row that sets a carried quest item's
/// remove_flag hands that item in (Nessa's Medicine Chest, Fairgraves' Allflame);</item>
/// <item>rewards: QuestRewardOffers through NPCTalk (QuestRewardCatalog), and QuestStaticRewards (passive and refund points,
/// stats) derived from the flags (<see cref="QuestStaticRewards"/>);</item>
/// <item>the quest log: the client shows QuestStates from the flags by itself (<see cref="QuestStates"/> mirrors it for
/// the tests).</item>
/// </list>
/// <para>Adding a quest (the long form, with the act checklist: campaign-quests.md "Adding a quest"): read the quest's rows
/// (<c>Quest</c>, <c>QuestStates</c>, the <c>NPCTalk</c> rows naming its flags, <c>QuestRewardOffers</c>,
/// <c>QuestStaticRewards</c>, its items' <c>.it</c> files, <c>WorldAreas</c> Bosses/EndQuestFlags, <c>SessionQuestFlags</c>,
/// the rooms' objects); add a <see cref="Quest"/> to the act's table with one rule per event the server must see
/// (<see cref="On"/>: Killed, Seen, Near, NearExit, Entered, Used/UsedWith, Cleared …) and per thing the flags change
/// (Exit, Object, Chest, Drops, Npc, Exists, Appears, Threshold, TurnsInto, SecondForm, Spawns, Blocker, Wall, LevelUp …);
/// dialogue flags need no rule. Only what the data cannot express needs code (a1q4's Glyph Wall: <see cref="GlyphQuest"/>,
/// with its oracle step in <see cref="QuestSimulator.ObjectHandlers"/>). Then the act's event sequence goes through the
/// oracle (<see cref="QuestSimulator"/>): the flags after the act must contain CharacterStartQuestState's preset for it.</para>
/// </summary>
static partial class Quests
{
    /// <summary>A rule of a quest's table. Area and object/variety Ids are WorldAreas, MonsterVarieties, NPCs, Chests or
    /// .ot type Ids, as the data names them.</summary>
    public abstract record Rule;

    /// <summary>Flags set when a monster of the variety dies in the area.</summary>
    public sealed record KillRule(string Area, string Variety, string[] Flags) : Rule;
    /// <summary>Flags set when a monster of one of the varieties dies in the area and no living monster of any of them is
    /// left in the instance: a fight of several bosses (The Gemling Legion's seven, Solaris and Lunaris).</summary>
    public sealed record KilledAllRule(string Area, string[] Varieties, string[] Flags) : Rule;
    /// <summary>A boss that forms once no living monster of any of the varieties is left in the instance (The Rotting
    /// Core's Depraved Trinity after Doedre's, Maligaro's and Shavronne's souls), at its own boss point
    /// (<see cref="AreaPopulation.LaterFormPoints"/>), else where the last of them died. The area does not place it.</summary>
    public sealed record FormsRule(string Area, string[] Varieties, string Next) : Rule;
    /// <summary>Flags set when the player comes within <see cref="SightRange"/> of a living monster of the variety.</summary>
    public sealed record SeenRule(string Area, string Variety, string[] Flags) : Rule;
    /// <summary>Flags set when the player comes within <see cref="SightRange"/> of a room object of this type (an NPC that
    /// only speaks, a blockage): its place comes from its room (AreaPopulation.LoadObjects).</summary>
    public sealed record NearRule(string Area, string ObjectType, string[] Flags) : Rule
    {
        /// <summary>Flags the character must have for the rule to fire (null: none). The carried quest items whose
        /// remove_flag is among <see cref="Flags"/> are taken in the same save, as for <see cref="UsedRule"/>: a quest item
        /// "used" at an object that cannot be clicked (a9q5's Bottled Storm at The Vastiri Desert's sand barrier).</summary>
        public string[]? Needs { get; init; }
    }
    /// <summary>Flags set when the player comes within <see cref="SightRange"/> of the area's exit to another area, shown
    /// or not.</summary>
    public sealed record NearExitRule(string Area, string To, string[] Flags) : Rule;
    /// <summary>Flags set when the player enters the area. (WorldAreas StartQuestFlags, the Visited- flags, come with the
    /// World panel's visits already, 16-waypoints.md §4.)</summary>
    public sealed record EnteredRule(string Area, string[] Flags) : Rule;
    /// <summary>Flags set when the player uses (C→S 0x24) a quest object of this type the engine created, when the
    /// character has every flag of <see cref="Needs"/> (null: always). Carried quest items whose remove_flag is among the
    /// flags are taken in the same save (the Thaumetic Emblem in its seal, the spike and the gem in the tree).</summary>
    public sealed record UsedRule(string Area, string ObjectType, string[] Flags, string[]? Needs = null) : Rule;
    /// <summary>Flags set when the last hostile monster of the area's instance dies (a1q8: "Clear the Fetid Pool"), for a
    /// character that has <see cref="After"/> (null: any).</summary>
    public sealed record ClearedRule(string Area, string[] Flags, string? After = null) : Rule;
    /// <summary>The exit from one area to the other needs the flag: the Gateway refuses it and creates it only once the
    /// client has the flag.</summary>
    public sealed record ExitRule(string From, string To, string Flag) : Rule;
    /// <summary>The monster never dies: when a hit takes its life to <see cref="LifePct"/> % of its maximum or below (a
    /// lethal hit and /kill included) the flags are set and it leaves the fight (Kitava on the Cathedral Rooftop:
    /// a5q6-CompletedBoss; the real fight ends when he destroys the Sign of Purity at about half life).</summary>
    public sealed record ThresholdRule(string Area, string Variety, int LifePct, string[] Flags) : Rule;
    /// <summary>The level a character reaches at least when it kills the monster (a1q1: Hillock → 2).</summary>
    public sealed record LevelRule(string Area, string Variety, int Level) : Rule;
    /// <summary>A quest chest the engine creates where its room or tile places it (Chests.dat Id); opening it (once per
    /// instance) drops the item (BaseItemTypes Id) while the character needs it (<see cref="QuestItems.Drops"/>). A chest
    /// with a Transitionable (the Strange Device) is untargetable in state 1 until <see cref="OpensWith"/>, then 2, and its
    /// last state once opened.</summary>
    public sealed record ChestRule(string Area, string ChestType, string Item, string? OpensWith = null) : Rule
    {
        /// <summary>A chest no room or tile places (a7q7's seven Fireflies in The Dread Thicket: the real server places them):
        /// the engine spreads the area's scattered chests over its walk map, the same cells for the same seed.</summary>
        public bool Scattered { get; init; }
    }
    /// <summary>The monster appears at the room or tile object when the player first comes within <see cref="SightRange"/> of
    /// it in the instance, unless <see cref="Until"/> is set (a10q1: Plaguewing from KitavaDemonSpawnerPlaguewing as the
    /// player reaches Bannon on The Cathedral Rooftop). The area's population leaves it out.</summary>
    public sealed record NearSpawnRule(string Area, string ObjectType, string Variety, string? Until) : Rule;
    /// <summary>The monster drops the quest item where it dies, while the character needs it.</summary>
    public sealed record DropRule(string Area, string Variety, string Item) : Rule;
    /// <summary>A room NPC (NPCs.dat Id, its .ot type) the engine creates where its room places it, until
    /// <see cref="GoneWith"/> is set (null: always), once <see cref="ShownBy"/> is set (null: from the start). In a town the
    /// rule picks the stage of a town NPC its room lists (DiallaTown until a4q1-RaptureActivated, DiallaTownWithered after).
    /// With <see cref="TransitionedBy"/> set the NPC stands in its last Transitionable state (WeylamReef2 on his throne once
    /// the Brine King is dead) or once <see cref="FreedBy"/> died in the instance (Clarissa kneels until her guards' captain dies),
    /// else in state 1; with <see cref="AtObject"/> it stands where that object is (no room line places it). Created,
    /// removed and transitioned as the flags change (the Game module's QuestNpcs.cs).</summary>
    public sealed record NpcRule(string Area, string NpcType, string? GoneWith, string? TransitionedBy = null, string? ShownBy = null) : Rule
    {
        /// <summary>A monster variety whose death in the instance frees the NPC: Transitionable 1 → 2 (Clarissa kneels until
        /// her guards' captain dies).</summary>
        public string? FreedBy { get; init; }
        /// <summary>Another room or tile object whose place the NPC takes, for an NPC no room line places (Lady Dialla on
        /// the tower top appears at Dominus' DeathPortalLocator).</summary>
        public string? AtObject { get; init; }
    }
    /// <summary>An object type that exists only from <see cref="From"/> and/or until <see cref="Until"/> is set: its .ot
    /// BaseEvents <c>serialisation_start_flag</c> / <c>serialisation_stop_flag</c> (Lilly on the Cathedral Rooftop after
    /// a5q6-CompletedBoss; the Control Blocks' two exits, before and after Avarius). Area transitions (by their tile type),
    /// NPCs and quest objects follow it in every area. An exit with <see cref="To"/> leads there: of two exits on one tile
    /// record that swap by flag, each leads to its own version of the area (the Chamber of Innocence's InnocenceToCourts to
    /// The Templar Courts, InnocenceToDestroyedCourts to The Torched Courts), whatever the layout's resolution says, and the
    /// twin the layout left out is created (AreaTransitionCatalog.QuestExitDestinations).</summary>
    public sealed record ExistsRule(string ObjectType, string? From, string? Until) : Rule
    {
        public string? To { get; init; }
    }
    /// <summary>When the monster dies the NPC appears where it fell (the god Sins, SinInnocence): the real server's boss
    /// scripts spawn them, no room places them. Until <see cref="GoneWith"/> (null: it stays for the instance); with
    /// GoneWith set it also stands beside the boss on a later visit while the boss's kill flags are set and GoneWith is not
    /// (SinInnocence gives a5q6 only there).</summary>
    public sealed record AppearsRule(string Area, string Variety, string NpcType, string? GoneWith) : Rule;
    /// <summary>A dialogue row of the NPC with this option text hands in every carried copy of the item, setting its
    /// remove_flag in the same save, for rows whose own flags do not (Hargan's "Victario's Holdfasts" takes each Platinum
    /// Bust carried: a3q11-DeliveredBust1–3 have no NPCTalk setter).</summary>
    public sealed record HandInRule(string Npc, string Option, string Item) : Rule
    {
        /// <summary>Only this NPCTalk row of that text (null: any): Fairgraves' rows all read "A Swig of Hope", and only
        /// row 496 (both items carried) takes both at once.</summary>
        public int? Row { get; init; }
    }
    /// <summary>Flags set when the dialogue row (NPCTalk row) is chosen that no column or script of the row sets: the real
    /// server's quest scripts (a9q2: Irasha's "Feather of Sekhema" 1313 takes the feather for her, IrashaTakeFeather, which
    /// her "Feather Reward" needs; "Oyun" 1301/1312 starts the quest, a9q2-QuestReceived). By row, because rows with one
    /// text differ (Irasha's three "Feather of Sekhema").</summary>
    public sealed record TalkRowRule(int Row, string[] Flags) : Rule;
    /// <summary>A quest portal no tile or room places as an exit: an area transition to <see cref="To"/> where the room or tile
    /// object <see cref="AtObject"/> stands (Maligaro's portals, MaligaroPortalTransition, which the Reverie Device spawns
    /// beside itself; Sin's EpiloguePortal back to the docks). Gate it with an <see cref="ExitRule"/>: hidden until the flag,
    /// as its Transitionable is untargetable until then.</summary>
    public sealed record PortalRule(string Area, string AtObject, string To, string Type) : Rule;
    /// <summary>A room or tile quest object the engine creates where the area places it (<see cref="QuestObjectTypes"/>): a
    /// seal, a lever, a door, an altar. Its Transitionable shows <see cref="TransitionedBy"/>: state 1 without the flag, its
    /// last state with it (the client plays the transition when the flag comes), and its TriggerableBlockage blocks until
    /// then. Using it is a <see cref="UsedRule"/>.</summary>
    public sealed record ObjectRule(string Area, string ObjectType, string? TransitionedBy) : Rule;
    /// <summary>Using the quest object (its <see cref="UsedRule"/> taking effect) brings the monster where the object stands
    /// (the Apex on the Dark Altar: the Vaal Oversoul). The area's population leaves it out.</summary>
    public sealed record SpawnRule(string Area, string ObjectType, string Variety) : Rule;
    /// <summary>The NPC turns into the monster when a dialogue row whose script names the variety
    /// (<c>IfVarietyContains( "&lt;variety&gt;", { Transition(); } )</c>) is chosen: the NPC goes, the monster appears in its
    /// place. The area's population leaves that boss out.</summary>
    public sealed record TurnsIntoRule(string Area, string NpcType, string Variety) : Rule;
    /// <summary>When the monster dies its second form appears where it fell (Merveil, the Siren → Merveil, the Twisted:
    /// <c>set_monster_has_second_form</c>). The area's population leaves the second form out.</summary>
    public sealed record SecondFormRule(string Area, string Variety, string Next) : Rule;
    /// <summary>Room objects the client spawns itself and stops at (.ot <c>blocking</c>; <see cref="Size"/> = object_size − 1)
    /// until <see cref="UnblockedBy"/> (their <c>unblock_quest_flag</c>): the server's walk estimate stops there too.</summary>
    public sealed record BlockerRule(string Area, string ObjectType, int Size, string UnblockedBy) : Rule;
    /// <summary>A room object the server creates (a blocking wall, BlockerObject) until the flag removes it (a1q1's force
    /// wall), with the stretch of walk map it closes (QuestBarriers).</summary>
    public sealed record WallRule(string Area, string ObjectType, string RemovedBy) : Rule;

    /// <summary>One quest's table: its Quest.dat Id and rules (collection initializer).</summary>
    public sealed class Quest(string id) : IEnumerable<Rule>
    {
        public string Id { get; } = id;
        public List<Rule> Rules { get; } = [];
        public void Add(Rule rule) => Rules.Add(rule);
        public IEnumerator<Rule> GetEnumerator() => Rules.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public override string ToString() => Id;
    }

    /// <summary>The rules by what they react to: <c>On.Killed(area, variety, flags…)</c> reads like the data.</summary>
    public static class On
    {
        public static Rule Killed(string area, string variety, params string[] flags) => new KillRule(area, variety, flags);
        public static Rule Seen(string area, string variety, params string[] flags) => new SeenRule(area, variety, flags);
        public static Rule Near(string area, string objectType, params string[] flags) => new NearRule(area, objectType, flags);
        public static Rule NearWith(string area, string objectType, string[] needs, params string[] flags) =>
            new NearRule(area, objectType, flags) { Needs = needs };
        public static Rule KilledAll(string area, string[] varieties, params string[] flags) => new KilledAllRule(area, varieties, flags);
        public static Rule Forms(string area, string[] varieties, string next) => new FormsRule(area, varieties, next);
        public static Rule NearExit(string area, string to, params string[] flags) => new NearExitRule(area, to, flags);
        public static Rule Entered(string area, params string[] flags) => new EnteredRule(area, flags);
        public static Rule Used(string area, string objectType, params string[] flags) => new UsedRule(area, objectType, flags);
        public static Rule UsedWith(string area, string objectType, string[] needs, params string[] flags) => new UsedRule(area, objectType, flags, needs);
        public static Rule Object(string area, string objectType, string? transitionedBy = null) => new ObjectRule(area, objectType, transitionedBy);
        public static Rule Spawns(string area, string objectType, string variety) => new SpawnRule(area, objectType, variety);
        public static Rule Cleared(string area, string? after, params string[] flags) => new ClearedRule(area, flags, after);
        public static Rule Exit(string from, string to, string flag) => new ExitRule(from, to, flag);
        public static Rule LevelUp(string area, string variety, int level) => new LevelRule(area, variety, level);
        public static Rule Threshold(string area, string variety, int lifePct, params string[] flags) => new ThresholdRule(area, variety, lifePct, flags);
        public static Rule Chest(string area, string chestType, string item, string? opensWith = null) => new ChestRule(area, chestType, item, opensWith);
        public static Rule ScatteredChest(string area, string chestType, string item) => new ChestRule(area, chestType, item) { Scattered = true };
        public static Rule SpawnsNear(string area, string objectType, string variety, string? until = null) => new NearSpawnRule(area, objectType, variety, until);
        public static Rule Drops(string area, string variety, string item) => new DropRule(area, variety, item);
        public static Rule Npc(string area, string npcType, string? goneWith, string? transitionedBy = null, string? shownBy = null,
            string? freedBy = null, string? atObject = null) =>
            new NpcRule(area, npcType, goneWith, transitionedBy, shownBy) { FreedBy = freedBy, AtObject = atObject };
        public static Rule Exists(string objectType, string? from = null, string? until = null, string? to = null) =>
            new ExistsRule(objectType, from, until) { To = to };
        public static Rule Appears(string area, string variety, string npcType, string? goneWith = null) => new AppearsRule(area, variety, npcType, goneWith);
        public static Rule HandIn(string npc, string option, string item, int? row = null) => new HandInRule(npc, option, item) { Row = row };
        public static Rule TalkRow(int row, params string[] flags) => new TalkRowRule(row, flags);
        public static Rule Portal(string area, string atObject, string to, string type) => new PortalRule(area, atObject, to, type);
        public static Rule TurnsInto(string area, string npcType, string variety) => new TurnsIntoRule(area, npcType, variety);
        public static Rule SecondForm(string area, string variety, string next) => new SecondFormRule(area, variety, next);
        public static Rule Blocker(string area, string objectType, int size, string unblockedBy) => new BlockerRule(area, objectType, size, unblockedBy);
        public static Rule Wall(string area, string objectType, string removedBy) => new WallRule(area, objectType, removedBy);
    }

    /// <summary>A quest state of the area instance, not of the character: no QuestFlags row, never saved, the same for
    /// everyone in the instance and gone with it (the Library's hidden door, which the game opens per instance with a loose
    /// candle). Rules name it like a flag (<c>instance:&lt;name&gt;</c>); the Gateway keeps it on the instance.</summary>
    public const string InstancePrefix = "instance:";
    public static bool IsInstanceFlag(string flag) => flag.StartsWith(InstancePrefix, StringComparison.Ordinal);
    public static string InstanceFlag(string name) => InstancePrefix + name;

    /// <summary>One act's quests, as a module gives them: a class deriving this, one per act and file
    /// (Modules/Core/World/QuestObjects/Acts/Act&lt;n&gt;.cs). The module finds them all in its assembly and registers each
    /// (<see cref="Register"/>), so a new act is a new file and no list changes. <see cref="Number"/> orders the acts (the
    /// Epilogue is 11).</summary>
    public abstract class Act
    {
        public abstract int Number { get; }
        /// <summary>The act's quests in the game's order (Quest.dat rows).</summary>
        public abstract IReadOnlyList<Quest> Table { get; }
        public override string ToString() => $"Act {Number} ({Table.Count} quests)";
    }

    // The registered acts, by who gave them (a module's id); the joined quests and rules are rebuilt on each change and
    // read without a lock.
    static readonly Lock RegistryGate = new();
    static readonly Dictionary<string, IReadOnlyList<Act>> Registered = new(StringComparer.Ordinal);
    static volatile RuleSet _everything = RuleSet.Empty;
    static volatile string[] _sourceList = [];
    static System.Collections.Concurrent.ConcurrentDictionary<string, RuleSet> _bySources = new(StringComparer.Ordinal);

    /// <summary>Gives the engine these acts' tables, replacing what <paramref name="source"/> gave before (a module's reload);
    /// disposing the result takes them away again (the module unloads). Every connection asks the same tables.</summary>
    public static IDisposable Register(string source, IEnumerable<Act> acts)
    {
        // Each act's table is read once here: the rule objects are the same in every set built from it.
        var list = acts.OrderBy(a => a.Number).Select(a => (Act)new Snapshot(a.Number, a.Table)).ToList();
        lock (RegistryGate)
        {
            Registered[source] = list;
            Rebuild();
        }
        return new Registration(() =>
        {
            lock (RegistryGate)
                if (Registered.TryGetValue(source, out var current) && ReferenceEquals(current, list))
                {
                    Registered.Remove(source);
                    Rebuild();
                }
        });
    }

    static void Rebuild()
    {
        _sourceList = [.. Registered.Where(r => r.Value.Count > 0).Select(r => r.Key).Order(StringComparer.Ordinal)];
        _bySources = new(StringComparer.Ordinal);
        _everything = Build(_sourceList);
    }

    // The tables of these sources, act by act in the game's order, each rule knowing its source.
    static RuleSet Build(IReadOnlyCollection<string> sources)
    {
        var acts = Registered.Where(r => sources.Contains(r.Key)).SelectMany(r => r.Value.Select(a => (Source: r.Key, Act: a)))
            .OrderBy(a => a.Act.Number).ToList();
        var sourceOf = new Dictionary<Rule, string>(ReferenceEqualityComparer.Instance);
        var quests = new List<Quest>();
        foreach (var (source, act) in acts)
            foreach (var quest in act.Table)
            {
                quests.Add(quest);
                foreach (var rule in quest.Rules) sourceOf.TryAdd(rule, source);
            }
        return new RuleSet(quests, sourceOf);
    }

    sealed class Snapshot(int number, IReadOnlyList<Quest> table) : Act
    {
        public override int Number => number;
        public override IReadOnlyList<Quest> Table => table;
    }

    sealed class Registration(Action remove) : IDisposable
    {
        int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) remove(); }
    }

    /// <summary>Every quest the engine runs, act by act in the game's order.</summary>
    public static IReadOnlyList<Quest> All => Everything.Quests;

    /// <summary>How close (cells) the player must come to a monster or object for the sight rules: our own number, about
    /// half a screen.</summary>
    public const double SightRange = 60;

    /// <summary>
    /// A set of quest tables with its rules indexed by kind and area (built once per registry change, read without a
    /// lock): every registered table (<see cref="Everything"/>), or those of the modules a league lists (<see cref="For"/>).
    /// The queries are its methods; the static ones of <see cref="Quests"/> ask <see cref="Everything"/> (the oracle, the
    /// layouts, the tools). The game asks a player's set (Game.QuestsOf), so a league that does not list a module runs none
    /// of its tables.
    /// </summary>
    public sealed class RuleSet
    {
        public static readonly RuleSet Empty = new([], new Dictionary<Rule, string>(ReferenceEqualityComparer.Instance));

        // Typed arrays (a KillRule[] for KillRule …), so a query neither scans other kinds nor allocates to read them.
        readonly Dictionary<Type, Array> byKind = [];
        readonly Dictionary<(Type, string), Array> byArea = [];
        readonly IReadOnlyDictionary<Rule, string> sourceOf;
        readonly HashSet<string> sources = new(StringComparer.Ordinal);
        readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlySet<string>> watched = new(StringComparer.Ordinal);

        internal RuleSet(IReadOnlyList<Quest> quests, IReadOnlyDictionary<Rule, string> sourceOf)
        {
            Quests = quests;
            this.sourceOf = sourceOf;
            var rules = quests.SelectMany(q => q.Rules).ToList();
            foreach (var g in rules.GroupBy(r => r.GetType())) byKind[g.Key] = Typed(g.Key, [.. g]);
            // A rule's area: its Area, or the area an exit leaves (ExitRule.From).
            foreach (var g in rules.GroupBy(r => (r.GetType(), AreaOf(r))).Where(g => g.Key.Item2 is not null))
                byArea[(g.Key.Item1, g.Key.Item2!)] = Typed(g.Key.Item1, [.. g]);
            foreach (var r in rules) if (sourceOf.GetValueOrDefault(r) is { } source) sources.Add(source);
        }

        static Array Typed(Type type, List<Rule> rules)
        {
            var array = Array.CreateInstance(type, rules.Count);
            for (int i = 0; i < rules.Count; i++) array.SetValue(rules[i], i);
            return array;
        }

        static string? AreaOf(Rule rule) => (rule.GetType().GetProperty("Area") ?? rule.GetType().GetProperty("From"))?.GetValue(rule) as string;

        /// <summary>The quests of the set, act by act.</summary>
        public IReadOnlyList<Quest> Quests { get; }

        /// <summary>Whether the set has no tables (a league that lists no module that registered any).</summary>
        public bool IsEmpty => Quests.Count == 0;

        /// <summary>Whether the tables a source (a module's id) registered are in the set.</summary>
        public bool Includes(string source) => sources.Contains(source);

        /// <summary>The source (a module's id) that registered the rule, or null.</summary>
        public string? SourceOf(Rule rule) => sourceOf.GetValueOrDefault(rule);

        T[] Of<T>() where T : Rule => byKind.TryGetValue(typeof(T), out var r) ? (T[])r : [];

        T[] In<T>(string areaId) where T : Rule => byArea.TryGetValue((typeof(T), areaId), out var r) ? (T[])r : [];

        public IReadOnlyList<string> OnMonsterKilled(string areaId, string varietyId) =>
            [.. In<KillRule>(areaId).Where(r => r.Variety == varietyId).SelectMany(r => r.Flags).Distinct()];

        /// <summary>The area's quest portals (<see cref="PortalRule"/>).</summary>
        public IReadOnlyList<PortalRule> Portals(string areaId) => In<PortalRule>(areaId);

        /// <summary>The flags a dialogue row sets beyond its own columns (<see cref="TalkRowRule"/>).</summary>
        public IReadOnlyList<string> OnTalkRow(int row) => [.. Of<TalkRowRule>().Where(r => r.Row == row).SelectMany(r => r.Flags).Distinct()];

        /// <summary>The multi-kill rules a death of this variety may complete (<see cref="KilledAllRule"/>).</summary>
        public IReadOnlyList<KilledAllRule> KilledAllOf(string areaId, string varietyId) =>
            [.. In<KilledAllRule>(areaId).Where(r => r.Varieties.Contains(varietyId))];

        /// <summary>The bosses a death of this variety may form (<see cref="FormsRule"/>).</summary>
        public IReadOnlyList<FormsRule> FormsAfter(string areaId, string varietyId) =>
            [.. In<FormsRule>(areaId).Where(r => r.Varieties.Contains(varietyId))];

        public IReadOnlyList<string> OnMonsterSeen(string areaId, string varietyId) =>
            [.. In<SeenRule>(areaId).Where(r => r.Variety == varietyId).SelectMany(r => r.Flags).Distinct()];

        /// <summary>The MonsterVarieties Ids whose sighting a rule cares about in this area (the player tick looks only for them).</summary>
        public IReadOnlySet<string> Watched(string areaId) => watched.GetOrAdd(areaId, a => In<SeenRule>(a).Select(r => r.Variety).ToHashSet());

        public IReadOnlyList<NearRule> NearObjects(string areaId) => In<NearRule>(areaId);
        public IReadOnlyList<NearExitRule> NearExits(string areaId) => In<NearExitRule>(areaId);

        public IReadOnlyList<string> OnEntered(string areaId) =>
            [.. In<EnteredRule>(areaId).SelectMany(r => r.Flags).Distinct()];

        /// <summary>The flags using the object sets: its rules without needs, and those whose needs the character has
        /// (<paramref name="has"/>; null: none of them).</summary>
        public IReadOnlyList<string> OnUsed(string areaId, string objectType, Func<string, bool>? has = null) =>
            [.. In<UsedRule>(areaId).Where(r => r.ObjectType == objectType && (r.Needs is null || has is not null && r.Needs.All(has)))
                .SelectMany(r => r.Flags).Distinct()];

        /// <summary>The flags using the object needs that the character lacks (for the log and the oracle).</summary>
        public IReadOnlyList<string> UseMissing(string areaId, string objectType, Func<string, bool> has) =>
            [.. In<UsedRule>(areaId).Where(r => r.ObjectType == objectType && r.Needs is not null)
                .SelectMany(r => r.Needs!).Where(f => !has(f)).Distinct()];

        public IReadOnlyList<NearSpawnRule> NearSpawns(string areaId) => In<NearSpawnRule>(areaId);

        public IReadOnlyList<ObjectRule> Objects(string areaId) => In<ObjectRule>(areaId);

        /// <summary>The quest items a dialogue row of the NPC (NPCs Id) with this option hands in (<see cref="HandInRule"/>).</summary>
        public IReadOnlyList<string> HandsIn(string npcId, string option, int row = -1) =>
            [.. Of<HandInRule>().Where(r => r.Npc == npcId && r.Option == option && (r.Row is null || r.Row == row)).Select(r => r.Item)];

        /// <summary>The monsters using the object brings (<see cref="SpawnRule"/>).</summary>
        public IReadOnlyList<string> SpawnsOnUse(string areaId, string objectType) =>
            [.. In<SpawnRule>(areaId).Where(r => r.ObjectType == objectType).Select(r => r.Variety)];

        /// <summary>The flags set when the area's last hostile monster dies, for a character with these flags.</summary>
        public IReadOnlyList<string> OnCleared(string areaId, Func<string, bool> has) =>
            [.. In<ClearedRule>(areaId).Where(r => (r.After is null || has(r.After))).SelectMany(r => r.Flags).Distinct()];

        /// <summary>Whether a rule wants to know when the area is cleared (the kill path counts the living monsters only then).</summary>
        public bool WatchesClear(string areaId) => In<ClearedRule>(areaId).Length > 0;

        /// <summary>Whether a monster of the area has a quest threshold (the player tick then looks for one that reached it).</summary>
        public bool HasThresholds(string areaId) => In<ThresholdRule>(areaId).Length > 0;

        /// <summary>The monster's quest threshold in the area (<see cref="ThresholdRule"/>), or null.</summary>
        public ThresholdRule? ThresholdOf(string areaId, string varietyId) =>
            In<ThresholdRule>(areaId).FirstOrDefault(r => r.Variety == varietyId);

        public int LevelAfterKill(string areaId, string varietyId) =>
            In<LevelRule>(areaId).Where(r => r.Variety == varietyId).Select(r => r.Level).DefaultIfEmpty(0).Max();

        /// <summary>The flag a character needs to take the exit from one area to the other, or null.</summary>
        public string? FlagToEnter(string fromArea, string toArea) => ExitOf(fromArea, toArea)?.Flag;

        /// <summary>The rule that gates the exit from one area to the other, or null.</summary>
        public ExitRule? ExitOf(string fromArea, string toArea) =>
            In<ExitRule>(fromArea).FirstOrDefault(r => r.To == toArea);

        /// <summary>For a room's server object of this type in this area: the flag that takes it away, or null when no rule
        /// manages it as a wall (only managed room objects are created).</summary>
        public string? RoomObjectRemovedBy(string areaId, string objectType) =>
            In<WallRule>(areaId).FirstOrDefault(r => r.ObjectType == objectType)?.RemovedBy;

        public IReadOnlyList<ChestRule> Chests(string areaId) => In<ChestRule>(areaId);
        public IReadOnlyList<string> DropsOnKill(string areaId, string varietyId) =>
            [.. In<DropRule>(areaId).Where(r => r.Variety == varietyId).Select(r => r.Item)];
        public IReadOnlyList<NpcRule> Npcs(string areaId) => In<NpcRule>(areaId);

        /// <summary>Whether an object of the type exists for a character with these flags (<see cref="ExistsRule"/>; true without
        /// a rule or a type).</summary>
        public bool Exists(string? objectType, Func<string, bool> has) =>
            objectType is null || Of<ExistsRule>().Where(r => r.ObjectType == objectType)
                .All(r => (r.From is null || has(r.From)) && (r.Until is null || !has(r.Until)));

        /// <summary>Where an exit of this tile type leads (<see cref="ExistsRule.To"/>), or null.</summary>
        public string? ExitTo(string? objectType) =>
            objectType is null ? null : Of<ExistsRule>().FirstOrDefault(r => r.ObjectType == objectType && r.To is not null)?.To;

        /// <summary>The rules that give an exit type a destination of its own (<see cref="ExistsRule.To"/>).</summary>
        public IReadOnlyList<ExistsRule> ExitRetargets() => [.. Of<ExistsRule>().Where(r => r.To is not null)];

        /// <summary>The exit types with a destination of their own (<see cref="ExistsRule.To"/>).</summary>
        public IEnumerable<(string Type, string To)> ExitDestinations() =>
            Of<ExistsRule>().Where(r => r.To is not null).Select(r => (r.ObjectType, r.To!));

        /// <summary>Whether a rule decides when the NPC type is in the area (<see cref="NpcRule"/>, <see cref="ExistsRule"/>,
        /// <see cref="AppearsRule"/>).</summary>
        public bool ManagesNpc(string areaId, string npcType) =>
            In<NpcRule>(areaId).Any(r => r.NpcType == npcType) || Of<ExistsRule>().Any(r => r.ObjectType == npcType)
            || In<AppearsRule>(areaId).Any(r => r.NpcType == npcType);

        /// <summary>The NPC types a room places that a rule of the area manages (its NpcRules, and the NPCs with a serialisation
        /// flag: ExistsRule), outside towns.</summary>
        public IEnumerable<string> PlacedNpcTypes(string areaId) =>
            In<NpcRule>(areaId).Select(r => r.NpcType)
                .Concat(Of<ExistsRule>().Select(r => r.ObjectType).Where(t => t.StartsWith("Metadata/NPC/", StringComparison.Ordinal))).Distinct();

        /// <summary>Whether a room or town NPC of the type stands in the area for a character with these flags.</summary>
        public bool NpcPresent(string areaId, string npcType, Func<string, bool> has) =>
            Exists(npcType, has) && In<NpcRule>(areaId).Where(r => r.NpcType == npcType)
                .All(r => (r.ShownBy is null || has(r.ShownBy)) && (r.GoneWith is null || !has(r.GoneWith)));

        /// <summary>Whether the NPC stands in its last Transitionable state (<see cref="NpcRule.TransitionedBy"/>).</summary>
        public bool NpcTransitioned(string areaId, string npcType, Func<string, bool> has) =>
            In<NpcRule>(areaId).Any(r => r.NpcType == npcType && r.TransitionedBy is { } f && has(f));

        /// <summary>Whether a death of the variety frees an NPC of the area (<see cref="NpcRule.FreedBy"/>).</summary>
        public bool Frees(string areaId, string varietyId) => In<NpcRule>(areaId).Any(r => r.FreedBy == varietyId);

        /// <summary>Whether the NPC stands freed: a death of its <see cref="NpcRule.FreedBy"/> variety in this instance.</summary>
        public bool NpcFreed(string areaId, string npcType, IReadOnlySet<string> killed) =>
            In<NpcRule>(areaId).Any(r => r.NpcType == npcType && r.FreedBy is { } v && killed.Contains(v));

        /// <summary>The NPCs that appear where a monster of the variety dies in the area.</summary>
        public IReadOnlyList<AppearsRule> AppearOnKill(string areaId, string varietyId) =>
            [.. In<AppearsRule>(areaId).Where(r => r.Variety == varietyId)];

        public IReadOnlyList<AppearsRule> Appearing(string areaId) => In<AppearsRule>(areaId);
        public IReadOnlyList<TurnsIntoRule> TurnsInto(string areaId) => In<TurnsIntoRule>(areaId);
        public string? SecondForm(string areaId, string varietyId) =>
            In<SecondFormRule>(areaId).FirstOrDefault(r => r.Variety == varietyId)?.Next;
        public IReadOnlyList<BlockerRule> Blockers(string areaId) => In<BlockerRule>(areaId);

        /// <summary>Flags only an event sets (a kill, a cleared area): a dialogue row does not set them, even when its QuestFlag4
        /// names one (Fairgraves' second "Thank You", NPCTalk 171, has a1q6-KilledFairgraves there: he must still be fought).</summary>
        public bool SetByEvent(string flag) =>
            Of<KillRule>().Any(r => r.Flags.Contains(flag)) || Of<ClearedRule>().Any(r => r.Flags.Contains(flag))
            || Of<KilledAllRule>().Any(r => r.Flags.Contains(flag))
            || Of<ThresholdRule>().Any(r => r.Flags.Contains(flag));

        /// <summary>The flag a dialogue row sets when chosen: its QuestFlag4, unless an event owns that flag (<see cref="SetByEvent"/>).</summary>
        public string? TalkStartFlag(NpcCatalog.Talk talk) => talk.StartFlag is { } start && !SetByEvent(start) ? start : null;

        /// <summary>Whether the area's population leaves this boss out: a quest brings it (an NPC that turns into it, a second
        /// form).</summary>
        public bool QuestSpawned(string areaId, string varietyId) =>
            In<TurnsIntoRule>(areaId).Any(r => r.Variety == varietyId)
            || In<SecondFormRule>(areaId).Any(r => r.Next == varietyId)
            || In<FormsRule>(areaId).Any(r => r.Next == varietyId)
            || In<SpawnRule>(areaId).Any(r => r.Variety == varietyId)
            || In<NearSpawnRule>(areaId).Any(r => r.Variety == varietyId);
    }

    /// <summary>Every registered table.</summary>
    public static RuleSet Everything => _everything;

    /// <summary>The tables that run for a league with these modules: those of the modules it lists that registered some
    /// (each source on its own: a league runs a module's tables only when it lists that module).</summary>
    public static RuleSet For(IReadOnlySet<string> modules)
    {
        var everything = _everything;
        var sources = _sourceList.Where(modules.Contains).ToArray();
        if (sources.Length == 0) return RuleSet.Empty;
        if (sources.Length == _sourceList.Length) return everything;
        string key = string.Join(',', sources);
        return _bySources.GetOrAdd(key, _ => Build(sources));
    }

    // The registry's static queries: every registered table (Everything).
    public static IReadOnlyList<string> OnMonsterKilled(string areaId, string varietyId) => Everything.OnMonsterKilled(areaId, varietyId);
    public static IReadOnlyList<PortalRule> Portals(string areaId) => Everything.Portals(areaId);
    public static IReadOnlyList<string> OnTalkRow(int row) => Everything.OnTalkRow(row);
    public static IReadOnlyList<KilledAllRule> KilledAllOf(string areaId, string varietyId) => Everything.KilledAllOf(areaId, varietyId);
    public static IReadOnlyList<FormsRule> FormsAfter(string areaId, string varietyId) => Everything.FormsAfter(areaId, varietyId);
    public static IReadOnlyList<string> OnMonsterSeen(string areaId, string varietyId) => Everything.OnMonsterSeen(areaId, varietyId);
    public static IReadOnlySet<string> Watched(string areaId) => Everything.Watched(areaId);
    public static IReadOnlyList<NearRule> NearObjects(string areaId) => Everything.NearObjects(areaId);
    public static IReadOnlyList<NearExitRule> NearExits(string areaId) => Everything.NearExits(areaId);
    public static IReadOnlyList<string> OnEntered(string areaId) => Everything.OnEntered(areaId);
    public static IReadOnlyList<string> OnUsed(string areaId, string objectType, Func<string, bool>? has = null) => Everything.OnUsed(areaId, objectType, has);
    public static IReadOnlyList<string> UseMissing(string areaId, string objectType, Func<string, bool> has) => Everything.UseMissing(areaId, objectType, has);
    public static IReadOnlyList<NearSpawnRule> NearSpawns(string areaId) => Everything.NearSpawns(areaId);
    public static IReadOnlyList<ObjectRule> Objects(string areaId) => Everything.Objects(areaId);
    public static IReadOnlyList<string> HandsIn(string npcId, string option, int row = -1) => Everything.HandsIn(npcId, option, row);
    public static IReadOnlyList<string> SpawnsOnUse(string areaId, string objectType) => Everything.SpawnsOnUse(areaId, objectType);
    public static IReadOnlyList<string> OnCleared(string areaId, Func<string, bool> has) => Everything.OnCleared(areaId, has);
    public static bool WatchesClear(string areaId) => Everything.WatchesClear(areaId);
    public static ThresholdRule? ThresholdOf(string areaId, string varietyId) => Everything.ThresholdOf(areaId, varietyId);
    public static int LevelAfterKill(string areaId, string varietyId) => Everything.LevelAfterKill(areaId, varietyId);
    public static string? FlagToEnter(string fromArea, string toArea) => Everything.FlagToEnter(fromArea, toArea);
    public static ExitRule? ExitOf(string fromArea, string toArea) => Everything.ExitOf(fromArea, toArea);
    public static string? RoomObjectRemovedBy(string areaId, string objectType) => Everything.RoomObjectRemovedBy(areaId, objectType);
    public static IReadOnlyList<ChestRule> Chests(string areaId) => Everything.Chests(areaId);
    public static IReadOnlyList<string> DropsOnKill(string areaId, string varietyId) => Everything.DropsOnKill(areaId, varietyId);
    public static IReadOnlyList<NpcRule> Npcs(string areaId) => Everything.Npcs(areaId);
    public static bool Exists(string? objectType, Func<string, bool> has) => Everything.Exists(objectType, has);
    public static string? ExitTo(string? objectType) => Everything.ExitTo(objectType);
    public static IEnumerable<(string Type, string To)> ExitDestinations() => Everything.ExitDestinations();
    public static bool ManagesNpc(string areaId, string npcType) => Everything.ManagesNpc(areaId, npcType);
    public static IEnumerable<string> PlacedNpcTypes(string areaId) => Everything.PlacedNpcTypes(areaId);
    public static bool NpcPresent(string areaId, string npcType, Func<string, bool> has) => Everything.NpcPresent(areaId, npcType, has);
    public static bool NpcTransitioned(string areaId, string npcType, Func<string, bool> has) => Everything.NpcTransitioned(areaId, npcType, has);
    public static bool Frees(string areaId, string varietyId) => Everything.Frees(areaId, varietyId);
    public static bool NpcFreed(string areaId, string npcType, IReadOnlySet<string> killed) => Everything.NpcFreed(areaId, npcType, killed);
    public static IReadOnlyList<AppearsRule> AppearOnKill(string areaId, string varietyId) => Everything.AppearOnKill(areaId, varietyId);
    public static IReadOnlyList<AppearsRule> Appearing(string areaId) => Everything.Appearing(areaId);
    public static IReadOnlyList<TurnsIntoRule> TurnsInto(string areaId) => Everything.TurnsInto(areaId);
    public static string? SecondForm(string areaId, string varietyId) => Everything.SecondForm(areaId, varietyId);
    public static IReadOnlyList<BlockerRule> Blockers(string areaId) => Everything.Blockers(areaId);
    public static bool SetByEvent(string flag) => Everything.SetByEvent(flag);
    public static string? TalkStartFlag(NpcCatalog.Talk talk) => Everything.TalkStartFlag(talk);
    public static bool QuestSpawned(string areaId, string varietyId) => Everything.QuestSpawned(areaId, varietyId);

    /// <summary>Flags every character has from the start (added on login, not a quest's), all for the hideout
    /// (21-hideouts.md §1): HideoutUnlocked; HASH32 0x2f1b9c25 (row 718, the schema's "HideoutJukeboxEnabled"), which the
    /// HUD's hideout panel checks (0x14216c840); CanEditHideout, tested before the Edit button shows (live trace
    /// 2026-10-20); MapDeviceActive, the map device's per-player "active" state (its .ot:
    /// per_player_quest_flag_state_active_1). ThisIsMyHideout (row 717): hideout NPCs' own options need it in NPCTalk
    /// QuestFlag1 (Niko's View Subterranean Chart and Visit Mine Encampment); without it the client lists their
    /// "not my hideout" rows (717 in QuestFlag2). Every hideout here is the player's own. EndGameAvailable is the
    /// epilogue flag the client requires before an Atlas map can be activated: until earned it is one of
    /// <see cref="EndgameOnly"/>.</summary>
    public static readonly string[] EveryCharacter = ["HideoutUnlocked", "HideoutJukeboxEnabled", "CanEditHideout", "MapDeviceActive",
        CompletedAllTutorials, ThisIsMyHideout, EndGameAvailable];

    /// <summary>
    /// Of <see cref="EveryCharacter"/>, the flags given for the endgame only, until the character earns them (Kitava's
    /// end sets EndGameAvailable; ClientState.UnearnedQuestFlags). The client needs EndGameAvailable to activate an Atlas
    /// map at the map device (live 2026-10-29) and to enter a map area (every map's WorldAreas EndQuestFlags), and every
    /// character here may run maps from its hideout. But the same flag is Kitava's Act 10 penalty (QuestStaticRewards 39,
    /// which the client applies from the flags it has), the Epilogue's gate (Karui Shores' and Oriath's EndQuestFlags) and
    /// the condition of Act 10's last rows (Lilly's "Set Sail from Oriath" 1433, Lani's "Kitava" and "Kitava Reward"
    /// 1415/1416) and exits (Sin's portal home). So an unearned one is not among the character's flags
    /// (ClientState.QuestFlags): the client gets it only in a hideout or a map area (Game.PlayerQuestFlags, as
    /// ThisIsMyHideout only in a hideout), and every server gate reads the flags the client has, so the campaign's gates
    /// stay shut until Kitava. In a hideout or a map before Kitava the client's character panel shows the second −30%;
    /// the server's resistances (Game.ResistancePenalty) count it only once earned.
    /// </summary>
    public static readonly string[] EndgameOnly = [EndGameAvailable];

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
