using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

/// <summary>
/// Grounds a monster's skill leaves where the player can stand in them (burning ground from a Fire Trap, Caustic Arrow's
/// caustic ground, Desecrate's, Toxic Rain's), for any monster and skill: a GroundEffectTypes kind (Fire, CausticCloud,
/// Desecration...) drawn as a <see cref="GroundEffectObject"/> with one of the kind's GroundEffects rows, and the kind's
/// buff (GroundEffectTypes BuffDefinition1: ground_fire_burn, caustic_cloud, ground_desecration) held on the player while
/// they stand in it, with the stats the skill gives the ground (its <c>base_{type}_damage_taken_per_minute</c>: the
/// skill's <c>base_{type}_damage_to_deal_per_minute</c>). The degeneration and the look are the player's debuffs'
/// (Game.PlayerDebuffs). Grounds of one kind do not add up: the player has the strongest of those they stand in.
/// A ground outlives its owner and goes when its time is up or the area changes.
/// </summary>
public static partial class Game
{
    /// <summary>A ground kind as drawn: the buff it keeps on whoever stands in it, its GroundEffects row and art, and the
    /// radius (world units) the art is drawn for (GroundEffects __col4, as FlaskGrounds reads it).</summary>
    public sealed record GroundKind(string Type, ushort BuffRow, ushort ArtRow, string Art, double ArtRadius)
    {
        /// <summary>The radius the art is drawn for, in cells.</summary>
        public double ArtCells => ArtRadius / WorldPerCell;
    }

    static Dictionary<string, (ushort Buff, List<(ushort Row, int Variation, int Version, string Art, double ArtRadius)> Art)> GroundTypes = default!;

    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadHostileGroundsData()
    {
        var kinds = new Dictionary<string, (ushort, List<(ushort, int, int, string, double)>)>(StringComparer.Ordinal);
        var ids = ClientTables.TableOrEmpty("GroundEffectTypes").Select(r => (Id: r.Text("Id") ?? "", Buff: r.IntOrNull("BuffDefinition1") ?? 0)).ToList();
        foreach (var (id, buff) in ids) kinds.TryAdd(id, ((ushort)Math.Max(0, buff), []));
        foreach (var r in ClientTables.TableOrEmpty("GroundEffects"))
            if (r.IntOrNull("GroundEffectTypesKey") is int k && k >= 0 && k < ids.Count
                && r.Texts("AOFile").FirstOrDefault() is { Length: > 0 } art)
                kinds[ids[k].Id].Item2.Add(((ushort)r.Index, r.Int("__col1"), r.Int("__col2"), art, r.RealOrNull("__col4") ?? 0));
        GroundTypes = kinds;
    }

    /// <summary>A kind's look for a skin (its <c>ground_*_art_variation</c>; 0 the plain one): the kind's GroundEffects
    /// row of that variation in the current art (version 2) if it has one, else the older; the plain skin's when the
    /// variation has none. Null for a kind the tables do not have.</summary>
    public static GroundKind? GroundKindOf(string type, int variation = 0)
    {
        if (!GroundTypes.TryGetValue(type, out var kind) || kind.Buff == 0) return null;
        var art = kind.Art.Where(a => a.Variation == variation).OrderByDescending(a => a.Version).FirstOrDefault();
        if (art.Art is null) art = kind.Art.Where(a => a.Variation == 0).OrderByDescending(a => a.Version).FirstOrDefault();
        return art.Art is null ? null : new(type, kind.Buff, art.Row, art.Art, art.ArtRadius);
    }

    /// <summary>Lays a ground of the kind at a cell for a time (seconds), reaching <paramref name="radius"/> cells,
    /// keeping its buff with <paramref name="stats"/> on the player standing in it. A kind without art (Art "") sends no
    /// object: the skill's own action draws that ground.</summary>
    public static async Task PlaceHostileGround(ClientState conn, uint owner, GroundKind kind,
        IReadOnlyDictionary<string, int> stats, (int X, int Y) at, double radius, double seconds)
    {
        if (radius <= 0 || seconds <= 0) return;
        uint placed = 0;
        await SendWorldBatch(conn, [], admission =>
        {
            lock (conn.Monsters)
            {
                if (conn.Closed || conn.LeavingArea) return null;
                double now = AreaTime(conn);
                uint ground = conn.TakeObjectId();
                bool drawn = kind.Art.Length > 0;
                conn.MonsterPlaced.Grounds[ground] = new(ground, owner, kind.BuffRow, stats, at, radius, now + seconds, conn.Layout, drawn);
                placed = ground;
                if (!drawn) return null;
                float scale = kind.ArtRadius > 0 ? (float)(radius * WorldPerCell / kind.ArtRadius) : 1;
                var bytes = new WorldRecordBuilder(conn, admission);
                bytes.Create(ground, InstanceEntities.CreateObject(ground, InstanceEntities.ObjectTypeHash(GroundEffectObject.TypePath),
                    new GroundEffectObject(at.X, at.Y, kind.ArtRow, kind.Art, (float)seconds) { Scale = scale, Team = 1 }.Blob()));
                bytes.Presentation(ground, InstanceProtocol.EnterWakeZone(ground));
                return bytes.Build();
            }
        });
        if (placed != 0)
            Console.WriteLine($"[gateway] {conn.Remote} monster 0x{owner:x} leaves {kind.Type} ground 0x{placed:x} (GroundEffects row {kind.ArtRow}) at "
                + $"({at.X}, {at.Y}), radius {radius:0.#}, {seconds:0.#} s: " + string.Join(", ", stats.Select(s => $"{s.Key} {s.Value}")));
    }

    /// <summary>World units per cell (the client's 10.869565 = 250 / 23).</summary>
    const double WorldPerCell = 250.0 / 23;

    // Per connection: the holder of each buff row the grounds keep on the player (one per row, the strongest ground's).
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ClientState, Dictionary<ushort, object>> GroundHolders = new();

    /// <summary>Each AI step: the grounds whose time is up (or of another area) go, and the player standing in grounds
    /// holds each kind's buff with the strongest one's stats.</summary>
    static async Task HostileGroundsTick(ClientState conn)
    {
        var placed = conn.MonsterPlaced;
        var holders = GroundHolders.GetValue(conn, _ => []);
        if (placed.Grounds.Count == 0 && holders.Count == 0) return;
        Dictionary<ushort, MonsterPlacedObjects.Ground?> standing = [];
        await SendWorldBatch(conn, [], admission =>
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.Paused) return null;
                double now = AreaTime(conn);
                var bytes = new WorldRecordBuilder(conn, admission);
                foreach (var ground in placed.Grounds.Values.ToArray())
                {
                    bool here = ReferenceEquals(ground.Area, conn.Layout);
                    if (here && now < ground.Until) continue;
                    placed.Grounds.Remove(ground.Id);
                    if (here && ground.Drawn) bytes.Remove(ground.Id);
                }
                var player = conn.Walk.Position(now);
                bool exposed = conn.PlayerLife > 0 && !InGracePeriod(conn);
                foreach (ushort row in holders.Keys) standing[row] = null;
                foreach (var ground in placed.Grounds.Values)
                {
                    if (!exposed || Dist(player, ground.At) > ground.Radius) continue;
                    if (standing.GetValueOrDefault(ground.BuffRow) is { } best && Strength(best) >= Strength(ground)) continue;
                    standing[ground.BuffRow] = ground;
                }
                return bytes.Count > 0 ? bytes.Build() : null;
            }
        });
        foreach (var (row, ground) in standing)
        {
            var holder = holders.TryGetValue(row, out var h) ? h : holders[row] = new object();
            if (ground is null) { await ReleasePlayerDebuff(conn, holder); holders.Remove(row); }
            else await HoldPlayerDebuff(conn, holder, row, ground.Owner, ground.Stats);
        }
    }

    // A ground's strength: its damage a minute of every type (the grounds of one kind name one type).
    static long Strength(MonsterPlacedObjects.Ground ground) =>
        ground.Stats.Where(s => s.Key.EndsWith("_damage_taken_per_minute", StringComparison.Ordinal)).Sum(s => (long)s.Value);
}
