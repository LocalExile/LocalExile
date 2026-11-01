using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;


/// <summary>How a monster type starts: as usual, or waiting to rise (MonsterEmerge).</summary>
enum EmergeKind
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
/// the others <see cref="EmergeKind.Lurking"/>. Unique monsters keep their own state: a boss's entrance is its fight's
/// script (BossFights), not a proximity rise.
/// They are created in state 1; when the player comes within <see cref="Range"/> cells (or their aggro range, if
/// shorter), or a lurking one is hit, they rise: the Emerge action (S→C 0x1ab, the .act's Emerge clip), then
/// Transitionable → 2 (0x2ba; on_transition runs EnableTargetable), then 0x1ac when the clip has played; until then they
/// are busy, so the AI neither moves them nor attacks with them. Data: object-types.json (<c>basic_actions</c>,
/// <c>client_transitionable</c>, <c>client_targetable</c>: scripts/re/ot_components.py), monster-takehit.json
/// (<c>emerge</c>: the clip's length, scripts/re/monster_takehit.py), ActionTypes.json.
/// </summary>
sealed class MonsterEmerge(string folder = "data")
{
    /// <summary>How near (grid cells) the player comes before a monster rises; its aggro range when that is shorter. The
    /// official server decides it in the monster's .ais file, which the client does not have: 24 cells keeps the emerge
    /// on screen.</summary>
    public const int Range = 24;

    /// <summary>How long a monster without a known Emerge clip takes to rise.</summary>
    public const double DefaultSeconds = 1.5;

    /// <summary>The Transitionable state a monster waiting to rise is created in (untransitioned).</summary>
    public const byte UnrisenState = 1;

    private readonly Lazy<Dictionary<string, EmergeKind>> _types = new(() => LoadTypes(folder));
    private readonly Lazy<Dictionary<string, double>> _clips = new(() => LoadClips(folder));
    private readonly Lazy<ushort?> _action = new(() => LoadAction(folder));

    /// <summary>The Emerge basic action's skill id (ActionTypes HASH16 &amp; 0x3fff: 0x0101), null without the table.</summary>
    public ushort? SkillId => _action.Value is ushort hash ? (ushort)(hash & 0x3fff) : null;

    /// <summary>How a monster of this variety and rarity starts (see <see cref="EmergeKind"/>).</summary>
    public EmergeKind Kind(MonsterCatalog.Variety v, MonsterRarity rarity = MonsterRarity.Normal) =>
        rarity != MonsterRarity.Unique && v.TransitionStates <= 1 && v.ComponentsOrBase.Contains("Transitionable") && SkillId is not null
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
        _clips.Value.TryGetValue(v.Id, out double s) && s > 0 ? s : DefaultSeconds;

    /// <summary>The rise's first two messages, in order: the Emerge action in place (0x1ab, not predicted) and the
    /// Transitionable step (0x2ba). <see cref="InstanceProtocol.ActionEnded"/> (0x1ac) follows when the clip has played.</summary>
    public static byte[][] RiseMessages(uint id, (int X, int Y) at, ushort skill, byte transitionableIndex, byte state) =>
    [
        InstanceProtocol.ActionStarted(id, at.X, at.Y, at.X, at.Y, skill, 0xffff, 0x0408),
        InstanceEntities.TransitionableUpdate(id, transitionableIndex, state),
    ];

    /// <summary>The buried monsters of a connection by object id: not in <see cref="ClientState.Monsters"/> until they
    /// rise. Guarded by the Monsters lock.</summary>
    public static SortedDictionary<uint, SpawnedMonster> Buried(ClientState conn) => Grounds.GetValue(conn, _ => new()).Monsters;

    /// <summary>Every monster that has not risen yet, buried or lurking (for the instance's saved state). Under the
    /// Monsters lock.</summary>
    public static List<SpawnedMonster> Unrisen(ClientState conn)
    {
        var g = Grounds.GetValue(conn, _ => new());
        return [.. g.Monsters.Values, .. g.Lurking.Select(id => conn.Monsters.GetValueOrDefault(id)).OfType<SpawnedMonster>()];
    }

    /// <summary>Registers a new monster (SpawnMonster): a buried one that has not risen goes to <see cref="Buried"/>,
    /// everything else to the connection's monsters; a lurking one that has not risen is busy until it rises. Returns
    /// whether it waits to rise (the caller then starts the watch). Under the Monsters lock.</summary>
    public static bool AddLocked(ClientState conn, uint id, SpawnedMonster m, EmergeKind kind, bool unrisen)
    {
        var g = Grounds.GetValue(conn, _ => new());
        bool waits = unrisen && kind != EmergeKind.None;
        if (waits && kind == EmergeKind.Buried) { g.Monsters[id] = m; return true; }
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
        var g = Grounds.GetValue(conn, _ => new());
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
        var g = Grounds.GetValue(conn, _ => new());
        var risen = new List<(uint, SpawnedMonster, (int, int), double)>();
        bool Near(SpawnedMonster m, out (int X, int Y) at)
        {
            at = m.Walk.Position(now);
            double d = Math.Sqrt((double)(at.X - player.X) * (at.X - player.X) + (double)(at.Y - player.Y) * (at.Y - player.Y));
            return d <= (m.AggroRange > 0 ? Math.Min(Range, m.AggroRange) : Range);
        }
        double SecondsOf(SpawnedMonster m) => m.Variety is { } v ? Seconds(v) : DefaultSeconds;
        foreach (var (id, m) in g.Monsters)
            if (Near(m, out var at)) risen.Add((id, m, at, SecondsOf(m)));
        foreach (var (id, m, _, _) in risen)
        {
            g.Monsters.Remove(id);
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
            // A little past the clip, so the action's end (0x1ac) goes out before the AI's first order.
            m.BusyUntil = now + seconds + 0.3;
        }
        watching = g.Watching = g.Monsters.Count + g.Lurking.Count > 0;
        return risen;
    }

    sealed class Ground
    {
        public readonly SortedDictionary<uint, SpawnedMonster> Monsters = [];
        public readonly HashSet<uint> Lurking = [];
        public bool Watching;
    }

    static readonly ConditionalWeakTable<ClientState, Ground> Grounds = new();

    /// <summary>The kind of one object-types.json entry (see <see cref="EmergeKind"/>); used by the loader and tests.</summary>
    public static EmergeKind KindOf(JsonElement t)
    {
        if (!t.TryGetProperty("basic_actions", out var actions) || actions.ValueKind != JsonValueKind.Array
            || !actions.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.String && a.GetString() == "Emerge")) return EmergeKind.None;
        int read = t.GetProperty("read").GetInt32();
        if (!t.GetProperty("components").EnumerateArray().Take(read).Any(c => c.GetString() == "Transitionable")) return EmergeKind.None;
        if (t.TryGetProperty("transitionable", out var tr) && tr.TryGetProperty("num_states", out var n) && n.GetInt32() > 1) return EmergeKind.None;
        string start = t.TryGetProperty("client_transitionable", out var ct) && ct.ValueKind == JsonValueKind.Object
            && ct.TryGetProperty("create_untransitioned", out var cu) && cu.ValueKind == JsonValueKind.String ? cu.GetString()! : "";
        // Only a type that waits in a paused clip rises: others (Hillock's sword stage) use the Transitionable otherwise.
        if (!start.Contains("PauseAnimation", StringComparison.Ordinal)) return EmergeKind.None;
        bool untargetable = t.TryGetProperty("client_targetable", out var tg) && tg.ValueKind == JsonValueKind.False
            || start.Contains("DisableTargetable", StringComparison.Ordinal);
        return untargetable ? EmergeKind.Buried : EmergeKind.Lurking;
    }

    static Dictionary<string, EmergeKind> LoadTypes(string folder)
    {
        string path = Path.Combine(folder, "object-types.json");
        if (!File.Exists(path)) return [];
        using var doc = GameTables.Open(path);
        var types = new Dictionary<string, EmergeKind>();
        foreach (var t in doc.RootElement.GetProperty("types").EnumerateArray())
            if (KindOf(t) is var kind and not EmergeKind.None)
                types[t.GetProperty("type").GetString()!] = kind;
        return types;
    }

    // monster-takehit.json: each variety's "emerge" clip length (ms) → seconds.
    static Dictionary<string, double> LoadClips(string folder)
    {
        string path = Path.Combine(folder, "monster-takehit.json");
        if (!File.Exists(path)) return [];
        using var doc = GameTables.Open(path);
        var clips = new Dictionary<string, double>();
        foreach (var p in doc.RootElement.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("emerge", out var e) && e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("length", out var l) && l.ValueKind == JsonValueKind.Number)
                clips[p.Name] = l.GetDouble() / 1000;
        return clips;
    }

    // ActionTypes.json: the Emerge row's HASH16.
    static ushort? LoadAction(string folder)
    {
        string path = Path.Combine(folder, "tables", "ActionTypes.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var r in doc.RootElement.EnumerateArray())
            if (r.TryGetProperty("Id", out var id) && id.GetString() == "Emerge" && r.TryGetProperty("HASH16", out var h) && h.ValueKind == JsonValueKind.Number)
                return (ushort)h.GetInt32();
        return null;
    }
}
