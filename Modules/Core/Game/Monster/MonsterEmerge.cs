using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core;


/// <summary>How a monster type starts: as usual, or waiting to rise (MonsterEmerge).</summary>
public enum EmergeKind
{
    /// <summary>An ordinary monster (or one whose entrance the server does not play).</summary>
    None,
    /// <summary>Hidden and untargetable until it rises (Gravel Eater, Dripping Dead): kept out of the connection's
    /// monsters, so nothing on the server reaches it either.</summary>
    Buried,
    /// <summary>Frozen in its emerge pose but targetable on the client (Tide Strider, Blood Elemental): it stays among
    /// the connection's monsters and can be hit, and rises when the player comes near or hits it.</summary>
    Lurking,
}

/// <summary>
/// Monsters that rise from the ground or the water (Gravel Eaters, Dripping Dead, the Twilight Strand's Drowned, Tide
/// Striders …; Agents/docs/protocol/10-chat-and-commands.md §5 "Emerge monsters"). Their type has the basic action
/// <c>Emerge</c> (ActionTypes 16641 = 0x4101, skill id 0x0101) and a Transitionable without <c>num_states</c> (2 states)
/// whose client script <c>create_untransitioned</c> plays the emerge clip and pauses it. A type whose client Targetable
/// says <c>targetable = false</c> (or whose create_untransitioned runs DisableTargetable) is <see cref="EmergeKind.Buried"/>,
/// the others <see cref="EmergeKind.Lurking"/>. Unique monsters keep their own state, except campaign Yugul: his paused
/// entrance must rise before generic AI can fight, or his native Targetable stays disabled until an instance rebind.
/// They are created in state 1; when the player comes within <see cref="Range"/> cells (or their aggro range, if
/// shorter), or a lurking one is hit, they rise: the Emerge action (S→C 0x1ab, the .act's Emerge clip),
/// Transitionable → 2 (0x2bc; on_transition runs EnableTargetable) and 0x1ac, which lets the clip play; until it has
/// played they are busy, so the AI neither moves them nor attacks with them. Data: client.db's object_types
/// (<c>basic_actions</c>, <c>client_transitionable_*</c>, <c>client_targetable</c>: scripts/re/ot_components.py),
/// monster_takehit (<c>emerge_*</c>: the clip's length, scripts/re/monster_takehit.py) and ActionTypes.
/// </summary>
public sealed class MonsterEmerge(string folder = "data")
{
    /// <summary>How near (grid cells) the player comes before a monster rises; its aggro range when that is shorter. The
    /// official server decides it in the monster's .ais file, which the client does not have: 24 cells keeps the emerge
    /// on screen.</summary>
    public const int Range = 24;

    // Eight 3.29.3b opening-capture events support this bounded calibration.
    public const int StrandDrownedRange = 60;

    static int WakeRange(string areaId, MonsterCatalog.Variety? variety) =>
        areaId == TutorialObjects.Strand && variety?.Id.StartsWith(
            "Metadata/Monsters/ZombieDismember/ZombieDrownedStrandWake", StringComparison.Ordinal) == true
            ? StrandDrownedRange : Range;

    /// <summary>How long a monster without a known Emerge clip takes to rise.</summary>
    public const double DefaultSeconds = 1.5;

    /// <summary>The Transitionable state a monster waiting to rise is created in (untransitioned).</summary>
    public const byte UnrisenState = 1;

    private readonly Lazy<Dictionary<string, EmergeKind>> _types = new(LoadTypes);
    private readonly Lazy<Dictionary<string, (double Seconds, int Index)>> _clips = new(LoadClips);
    private readonly Lazy<ushort?> _action = new(() => LoadAction(folder));

    /// <summary>The Emerge basic action's skill id (ActionTypes HASH16 &amp; 0x3fff: 0x0101), null without the table.</summary>
    public ushort? SkillId => _action.Value is ushort hash ? (ushort)(hash & 0x3fff) : null;

    /// <summary>How a monster of this variety and rarity starts (see <see cref="EmergeKind"/>).</summary>
    public EmergeKind Kind(MonsterCatalog.Variety v, MonsterRarity rarity = MonsterRarity.Normal) =>
        (rarity != MonsterRarity.Unique || v.Id == "Metadata/Monsters/Frog/FrogGod/SilverFrog")
            && v.TransitionStates <= 1 && v.ComponentsOrBase.Contains("Transitionable") && SkillId is not null
            && _types.Value.TryGetValue(v.BaseType, out var kind) ? kind : EmergeKind.None;

    /// <summary>Whether area packs may place this variety: not an "Emerge" variety that would wait for good (a
    /// Transitionable whose start the server cannot play). Emerge varieties without a Transitionable are ordinary.</summary>
    public bool CanPlace(MonsterCatalog.Variety v) =>
        Kind(v) != EmergeKind.None || !(v.Id.Contains("Emerge", StringComparison.OrdinalIgnoreCase) && v.ComponentsOrBase.Contains("Transitionable"));

    /// <summary>The Transitionable state of a new monster: 1 while it waits to rise, 2 for an emerge monster that has
    /// risen (kept by the instance), else the type's own (MonsterCatalog: num_states, 1 when the .ot gives none).</summary>
    public static byte CreateState(MonsterCatalog.Variety v, EmergeKind kind, bool unrisen) =>
        kind == EmergeKind.None ? v.TransitionStates : unrisen ? UnrisenState : RisenState(v);

    /// <summary>The fully transitioned state: num_states, 2 when the .ot gives none.</summary>
    public static byte RisenState(MonsterCatalog.Variety v) => (byte)Math.Max(2, (int)v.TransitionStates);

    /// <summary>How long the variety's Emerge clip plays, in seconds (<see cref="DefaultSeconds"/> when unknown).</summary>
    public double Seconds(MonsterCatalog.Variety v) =>
        _clips.Value.TryGetValue(v.Id, out var clip) && clip.Seconds > 0 ? clip.Seconds : DefaultSeconds;

    /// <summary>The variety's Emerge clip's index in its rig (0x1b2 plays it), or null when unknown.</summary>
    public uint? ClipIndex(MonsterCatalog.Variety v) =>
        _clips.Value.TryGetValue(v.Id, out var clip) && clip.Index >= 0 ? (uint)clip.Index : null;

    /// <summary>The rise, in GGG's order and tick: the Transitionable step (0x2bc), then the Emerge action in place aimed
    /// at the monster itself, with the 0x1ac that starts its clip.</summary>
    public static byte[][] RiseMessages(uint id, (int X, int Y) at, ushort skill, ushort sequence, byte transitionableIndex, byte state) =>
    [
        InstanceEntities.TransitionableUpdate(id, transitionableIndex, state),
        InstanceProtocol.ActionStartedNow(id, at.X, at.Y, at.X, at.Y, skill, sequence, 0x0800, id),
    ];

    /// <summary>The buried monsters of a connection by object id: not in <see cref="ClientState.Monsters"/> until they
    /// rise. Guarded by the Monsters lock.</summary>
    public static SortedDictionary<uint, SpawnedMonster> Buried(ClientState conn) => conn.DormantMonsters;

    /// <summary>Every monster that has not risen yet, buried or lurking (for the instance's saved state). Under the
    /// Monsters lock.</summary>
    public static List<SpawnedMonster> Unrisen(ClientState conn)
    {
        var g = conn.EmergeGround;
        return [.. conn.DormantMonsters.Values, .. g.Lurking.Select(id => conn.Monsters.GetValueOrDefault(id)).OfType<SpawnedMonster>()];
    }

    /// <summary>Registers a new monster (SpawnMonster): a buried one that has not risen goes to <see cref="Buried"/>,
    /// everything else to the connection's monsters; a lurking one that has not risen is busy until it rises. Returns
    /// whether it waits to rise (the caller then starts the watch). Under the Monsters lock.</summary>
    public static bool AddLocked(ClientState conn, uint id, SpawnedMonster m, EmergeKind kind, bool unrisen)
    {
        var g = conn.EmergeGround;
        bool waits = unrisen && kind != EmergeKind.None;
        if (waits && kind == EmergeKind.Buried) { conn.DormantMonsters[id] = m; return true; }
        conn.Monsters[id] = m;
        if (waits)
        {
            m.BusyUntil = double.PositiveInfinity;
            g.Lurking.Add(id);
        }
        return waits;
    }

    /// <summary>Marks the connection's watch as running; false when it already was. Under the Monsters lock.</summary>
    public static bool StartWatch(ClientState conn)
    {
        var g = conn.EmergeGround;
        if (g.Watching) return false;
        return g.Watching = true;
    }

    /// <summary>The monsters that rise now: within reach of the player (<see cref="Range"/>, or the aggro range if
    /// shorter), or a lurking one that has been hit. A buried one leaves <see cref="Buried"/> for the connection's
    /// monsters; each is aggroed and busy for its clip. Returns them with where they stand; the caller sends the rise.
    /// When none is left waiting the watch ends (<paramref name="watching"/> false). Under the Monsters lock.</summary>
    public List<(uint Id, SpawnedMonster Monster, (int X, int Y) At, double Seconds)> RiseLocked(ClientState conn,
        (int X, int Y) player, double now, out bool watching)
    {
        var g = conn.EmergeGround;
        var risen = new List<(uint, SpawnedMonster, (int, int), double)>();
        bool Near(SpawnedMonster m, out (int X, int Y) at)
        {
            at = m.Walk.Position(now);
            double d = Math.Sqrt((double)(at.X - player.X) * (at.X - player.X) + (double)(at.Y - player.Y) * (at.Y - player.Y));
            int range = WakeRange(conn.Layout.Id, m.Variety);
            return d <= (m.AggroRange > 0 ? Math.Min(range, m.AggroRange) : range);
        }
        double SecondsOf(SpawnedMonster m) => m.Variety is { } v ? Seconds(v) : DefaultSeconds;
        foreach (var (id, m) in conn.DormantMonsters)
            if (Near(m, out var at)) risen.Add((id, m, at, SecondsOf(m)));
        foreach (var (id, m, _, _) in risen)
        {
            conn.DormantMonsters.Remove(id);
            conn.Monsters[id] = m;
        }
        foreach (uint id in g.Lurking.ToList())
        {
            if (!conn.Monsters.TryGetValue(id, out var m) || m.Dead) { g.Lurking.Remove(id); continue; }
            if (Near(m, out var at) || m.Life < m.MaxLife)
            {
                g.Lurking.Remove(id);
                risen.Add((id, m, at, SecondsOf(m)));
            }
        }
        foreach (var (_, m, _, seconds) in risen)
        {
            m.Aggro = true;
            m.LastTarget = null;
            // The clip-end callback completes Emerge in both modes (141e933b0 → 141e934b0 → 141e8f670).
            // No extra idle tail follows the clip; the next AI decision may run once it ends.
            m.BusyUntil = now + seconds;
        }
        watching = g.Watching = conn.DormantMonsters.Count + g.Lurking.Count > 0;
        return risen;
    }

    /// <summary>Whether any monster still waits to rise, and none watches for it (its watcher's connection left).
    /// Under the Monsters lock.</summary>
    public static bool Unwatched(ClientState conn) =>
        conn.EmergeGround is { Watching: false } g && conn.DormantMonsters.Count + g.Lurking.Count > 0;

    /// <summary>The watch ended with its connection. Under the Monsters lock.</summary>
    public static void StopWatch(ClientState conn) => conn.EmergeGround.Watching = false;

    /// <summary>Whether this monster still lies in wait (buried ones are not in the instance's monsters until they rise).
    /// Under the Monsters lock.</summary>
    public static bool Waiting(ClientState conn, uint id) => conn.EmergeGround.Lurking.Contains(id);

    /// <summary>The kind of one object_types row (see <see cref="EmergeKind"/>); used by the loader and tests.</summary>
    public static EmergeKind KindOf(ClientRow t)
    {
        if (!t.Texts("basic_actions").Contains("Emerge")) return EmergeKind.None;
        if (!ObjectTypes.Components(t).Contains("Transitionable")) return EmergeKind.None;
        if (t.IntOrNull("transitionable_num_states") > 1) return EmergeKind.None;
        string start = t.TextOrNull("client_transitionable_create_untransitioned") ?? "";
        // Only a type that waits in a paused clip rises: others (Hillock's sword stage) use the Transitionable otherwise.
        if (!start.Contains("PauseAnimation", StringComparison.Ordinal)) return EmergeKind.None;
        bool untargetable = t.BoolOrNull("client_targetable") == false || start.Contains("DisableTargetable", StringComparison.Ordinal);
        return untargetable ? EmergeKind.Buried : EmergeKind.Lurking;
    }

    static Dictionary<string, EmergeKind> LoadTypes()
    {
        var types = new Dictionary<string, EmergeKind>();
        foreach (var (type, t) in ObjectTypes.All())
            if (KindOf(t) is var kind and not EmergeKind.None)
                types[type] = kind;
        return types;
    }

    // monster_takehit: each variety's emerge clip length (ms) → seconds.
    static Dictionary<string, (double Seconds, int Index)> LoadClips()
    {
        var clips = new Dictionary<string, (double, int)>();
        foreach (var r in ClientTables.TableOrEmpty("monster_takehit"))
            if (r.RealOrNull("emerge_length") is double length)
                clips[r.Text("variety")!] = (length / 1000, r.IntOrNull("emerge_index") ?? -1);
        return clips;
    }

    // ActionTypes: the Emerge row's HASH16.
    static ushort? LoadAction(string folder)
    {
        if (!ClientTables.Has("ActionTypes")) return null;
        foreach (var r in ClientTables.Table("ActionTypes"))
            if (r.TextOrNull("Id") == "Emerge" && r.IntOrNull("HASH16") is int h)
                return (ushort)h;
        return null;
    }
}
