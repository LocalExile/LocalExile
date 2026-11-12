using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using static Game;
using static Program;

namespace GameServer.Modules.Shrines;

/// <summary>
/// The shrine lifecycle of one connection: spawn, claim, buff, expiry and area cleanup, with the per-shrine and per-buff
/// state kept here and the gameplay of each type delegated to the effects its <see cref="ShrineDefinition"/>
/// registers (<see cref="ShrineRegistry"/>).
/// <para>State: a generated map's shrines by server object id (<see cref="ShrineInstance"/>), and the player buffs this
/// connection holds, by BuffDefinitions row. The buff's stats join the character's own sum through
/// <see cref="GameEvents.PlayerStatSources"/> for as long as the buff is in that set, so a claimed shrine's buff follows
/// the player wherever they walk and disappears with the set.</para>
/// <para>The state is per connection, as the modules' state is; the objects are the area's, so leaving the area drops
/// every instance, buff and effect (the client's area and its objects are gone with it).</para>
/// </summary>
public static class ShrineManager
{
    /// <summary>How close (cells) a monster must be for the unclaimed shrine's enter/leave steps.</summary>
    const int NearbyRadius = 20;

    /// <summary>The Shrine component's "no shrine" key (0x141e3e0c0: 0xff): the client draws no icon over the object and
    /// it is no longer a shrine to interact with, while the object, its model and its collision stay.</summary>
    const int NoShrineRow = 0xff;

    /// <summary>A claimed shrine's buff: the instance the client shows, the token that owns it, and its effect.</summary>
    sealed class ActiveBuff
    {
        public required ShrineInstance Shrine;
        public ushort Instance;
        public int Token;
        /// <summary>When the buff ends, on the connection clock: the tick ends it, so no timer outlives the area.</summary>
        public double EndsAt;
    }

    sealed class AreaState
    {
        public readonly Dictionary<uint, ShrineInstance> Instances = [];
        public readonly Dictionary<ushort, ActiveBuff> Buffs = [];
        public int Token;
    }

    static readonly ConditionalWeakTable<ClientState, AreaState> States = new();
    static AreaState Of(ClientState conn) => States.GetOrCreateValue(conn);

    // ---- Spawn ----------------------------------------------------------------------------------------------------

    /// <summary>The area's shrines: the level generator's shrine room lines, on walkable ground, each with its type from
    /// the seed and cell. A town, a hideout or an area without a generated level has none.</summary>
    public static async Task SpawnArea(ClientState conn)
    {
        var state = Of(conn);
        ClearArea(state);
        if (!MapDevices.Value.IsMapArea(conn.Layout.Hash)) return;
        if (conn.Layout.Walkable is not { } walk) return;
        if (ServerLevels.For(conn.Layout.Id, conn.Seed) is not { } level) return;
        foreach (var placed in ShrinePlacement.Plan(level.RoomObjects, walk, conn.Seed))
            await Create(conn, NewInstance(placed.Shrine, placed.X, placed.Y, placed.Rotation, placed.Model));
        if (state.Instances.Count > 0)
            Console.WriteLine($"[gateway] {conn.Wire.Remote} area {conn.Layout.Id}: {state.Instances.Count} shrine(s) created");
    }

    static ShrineInstance NewInstance(ShrineCatalog.ShrineDefinition definition, int x, int y, float rotation, string? model)
    {
        var registered = ShrineRegistry.Find(definition.Id);
        return new()
        {
            Definition = definition,
            At = (x, y),
            Rotation = rotation,
            Model = model,
            Before = ShrineRegistry.Before(definition),
            After = ShrineRegistry.After(definition),
            Stats = registered?.PlayerStats is { } stats ? Resolve(stats, definition.Id) : definition.Stats,
            Duration = registered?.Duration ?? definition.Duration,
        };
    }

    /// <summary>A registered definition's stats by name, as the client's stat keys. A name the client does not know is
    /// left out and named in the log rather than silently dropped.</summary>
    static IReadOnlyList<(uint Key, int Value)> Resolve(IReadOnlyList<(string Stat, int Value)> stats, string shrineId)
    {
        var list = new List<(uint Key, int Value)>(stats.Count);
        foreach (var (stat, value) in stats)
            if (Stats.Key(stat) is uint key) list.Add((key, value));
            else Console.WriteLine($"[gateway] shrine {shrineId}: the client has no stat \"{stat}\"; it is not granted");
        return list;
    }

    /// <summary>Creates one shrine object at its cell and registers it for clicks. The Shrines row goes in the object's
    /// Shrine component (the client draws that row's buff icon over it), so the icon and the buff the click grants are
    /// one value.</summary>
    static async Task Create(ClientState conn, ShrineInstance shrine)
    {
        var (remote, stream, ciphers) = conn.Wire;
        uint id = conn.NextObjectId++;
        shrine.ObjectId = id;
        Of(conn).Instances[id] = shrine;
        var obj = new ShrineObject(shrine.At.X, shrine.At.Y, shrine.Rotation, Model: shrine.Model, Row: shrine.Definition.Row);
        await Send(remote, stream, ciphers,
            InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(ShrinePlacement.Type), obj.Blob()),
            $"0x02bb create shrine 0x{id:x} {shrine.Definition.Id} at {shrine.At} model {shrine.Model}");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
        var ctx = new ShrineContext(conn, conn.Clock.Elapsed.TotalSeconds);
        foreach (var effect in shrine.Before) await effect.OnSpawn(ctx, shrine);
    }

    /// <summary>Dev (/shrines): the named types, or every basic type, on a ring around the player inside the click
    /// range, so each type's icon, buff and tooltip can be checked in one area instead of hunting a seed that rolls it.</summary>
    public static async Task<int> SpawnAroundPlayer(ClientState conn, IReadOnlyList<string> ids)
    {
        if (conn.Layout.Walkable is not { } walk) return 0;
        var types = new List<ShrineCatalog.ShrineDefinition>();
        if (ids.Count == 0) types.AddRange(ShrineCatalog.Standard);
        else foreach (string id in ids) types.Add(FindDefinition(id));
        var (px, py) = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        int made = 0;
        for (int i = 0; i < types.Count; i++)
        {
            double angle = Math.PI * 2 * i / types.Count;
            var (x, y) = Clear(walk, px + (int)Math.Round(Math.Cos(angle) * (ShrinePlacement.ObjectSize + 5)),
                py + (int)Math.Round(Math.Sin(angle) * (ShrinePlacement.ObjectSize + 5)), ShrinePlacement.ObjectSize + 6);
            await Create(conn, NewInstance(types[i], x, y, 0, ShrinePlacement.DefaultModel));
            made++;
        }
        Console.WriteLine($"[gateway] {conn.Wire.Remote} /shrines: {made} shrine(s) created around ({px}, {py})");
        return made;
    }

    /// <summary>The shrine type an argument names: by Shrines row id, case-insensitive. An unknown id is a refusal,
    /// not a silently missing shrine.</summary>
    static ShrineCatalog.ShrineDefinition FindDefinition(string id) => ShrineCatalog.Find(id) ?? ShrineCatalog.All.FirstOrDefault(s =>
            string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown shrine \"{id}\" (rows: {string.Join(", ", ShrineCatalog.Standard.Select(s => s.Id))}).");

    /// <summary>The nearest walkable cell to (<paramref name="x"/>, <paramref name="y"/>) within
    /// <paramref name="reach"/> rings, or the cell itself when none is (a dev placement still has to draw).</summary>
    static (int X, int Y) Clear(Func<int, int, bool> walk, int x, int y, int reach)
    {
        if (walk(x, y)) return (x, y);
        for (int r = 1; r <= reach; r++)
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r && walk(x + dx, y + dy)) return (x + dx, y + dy);
        return (x, y);
    }

    // ---- Activation -----------------------------------------------------------------------------------------------

    /// <summary>Whether the click was a shrine's. A claim needs an unclaimed shrine of this area; the click is the same
    /// walk-up action a chest or a quest object starts (C→S 0x24): the player walks to the shrine's walk-up range, and only
    /// on arrival does the buff follow. The client holds its action until the server's 0x01ab end, so an unanswered click
    /// leaves it waiting on an action that never finishes.</summary>
    public static async Task<bool> Use(ClientState conn, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        var state = Of(conn);
        if (!state.Instances.TryGetValue(use.TargetId, out var shrine)) return false;
        var (remote, stream, ciphers) = conn.Wire;
        if (shrine.State == ShrineState.Used)
        {
            // The spent statue stays in the area: a click on it grants nothing, and the action the client started ends here
            // so it is not left waiting on a use that will never finish.
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null);
            return true;
        }
        if (shrine.State != ShrineState.Inactive) return true;
        // Activating from the click: a second click cannot grant the same shrine twice while the walk is in flight.
        shrine.State = ShrineState.Activating;
        int action = Interlocked.Increment(ref conn.ActionCount);
        double now = conn.Clock.Elapsed.TotalSeconds;
        var (here, stop, arrive) = conn.Walk.WalkUpTo(shrine.At.X, shrine.At.Y, ShrinePlacement.ObjectSize, now);
        if (!use.Started)   // a lockstep client got its start from the frame pass
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId,
                InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, shrine.At.X, shrine.At.Y, use.SkillId,
                    use.Sequence, use.Flags, targetObjectId: use.TargetId),
                $"0x01ab player walks to shrine 0x{shrine.ObjectId:x} {shrine.Definition.Id} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s",
                still: () => PlayerActionCurrent(conn, action));
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (conn.ActionCount == action && !conn.Closed && !conn.LeavingArea
            && PlayerWalk.InWalkUpRange(stop, shrine.At, ShrinePlacement.ObjectSize))
        {
            Console.WriteLine($"[gateway] {remote} shrine 0x{shrine.ObjectId:x} {shrine.Definition.Id} used at ({stop.X}, {stop.Y})");
            var ctx = new ShrineContext(conn, conn.Clock.Elapsed.TotalSeconds);
            shrine.ActivatedAt = ctx.Now;
            foreach (var effect in shrine.Before) await effect.OnActivation(ctx, shrine);
            await Grant(conn, shrine, ctx);
            shrine.State = ShrineState.Used;
            // The spent statue stays where it stands, with no Shrines row on it any more: the client stops drawing the
            // shrine icon over it and it is no longer a shrine to interact with (the model itself is unchanged, because
            // ShrineVisualArtVariations maps only the boss/influence rows, so there is no "used" art to switch to).
            await Spent(conn, shrine);
        }
        else shrine.State = ShrineState.Inactive;   // the walk was interrupted: the shrine is still there to claim
        await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null);
        return true;
    }

    /// <summary>The claimed shrine's object again, in the same place with the same model, but with the Shrine component's
    /// "no shrine" key (<see cref="NoShrineRow"/>): the icon it drew and its interaction are gone with the row. The object
    /// id is kept, so the client replaces the one it has (the opened-chest pattern).</summary>
    static async Task Spent(ClientState conn, ShrineInstance shrine)
    {
        var (remote, stream, ciphers) = conn.Wire;
        await SendWorldRemoval(remote, stream, ciphers, conn, shrine.ObjectId, null);
        await SendWorldCreate(remote, stream, ciphers, conn, shrine.ObjectId,
            InstanceEntities.CreateObject(shrine.ObjectId, InstanceEntities.ObjectTypeHash(ShrinePlacement.Type),
                new ShrineObject(shrine.At.X, shrine.At.Y, shrine.Rotation, Model: shrine.Model, Row: NoShrineRow).Blob()),
            $"0x02bb shrine 0x{shrine.ObjectId:x} {shrine.Definition.Id} used (no row, no icon, not interactable)");
        await SendWorldRecord(remote, stream, ciphers, conn, shrine.ObjectId, InstanceProtocol.EnterWakeZone(shrine.ObjectId), null);
    }

    /// <summary>Grants the shrine's buff for its row's duration and starts its effect. A second shrine of the same row
    /// replaces the first: its buff is removed before the new one, and the older timer cannot remove the newer buff.</summary>
    static async Task Grant(ClientState conn, ShrineInstance shrine, ShrineContext ctx)
    {
        var state = Of(conn);
        var (remote, stream, ciphers) = conn.Wire;
        var definition = shrine.Definition;
        ushort instance;
        int token;
        byte[]? replaced = null;
        lock (conn.Monsters)
        {
            token = ++state.Token;
            if (state.Buffs.Remove(definition.BuffRow, out var old)) replaced = InstanceEntities.RemoveBuff(PlayerId, old.Instance);
            instance = conn.NextBuff++;
            shrine.BuffInstance = instance;
            shrine.BuffToken = token;
            state.Buffs[definition.BuffRow] = new ActiveBuff
            {
                Shrine = shrine, Instance = instance, Token = token,
                EndsAt = ctx.Now + shrine.Duration,
            };
            Console.WriteLine($"[gateway] shrine {definition.Id} grants " +
                string.Join(", ", shrine.Stats.Select(s => $"0x{s.Key:x}={s.Value}")));
        }
        if (replaced is not null)
            await Send(remote, stream, ciphers, replaced, $"0x01c2 shrine buff {definition.BuffRow} replaced by a new shrine");
        await Send(remote, stream, ciphers, [], $"0x01c1 shrine {definition.Id} buff {definition.BuffRow} for {definition.Duration} s",
            atGate: () =>
            {
                lock (conn.Monsters)
                {
                    if (conn.Closed || conn.LeavingArea) return null;
                    var bytes = new List<byte>(InstanceEntities.AddBuff(PlayerId, instance, definition.BuffRow, PlayerId,
                        u68: BitConverter.SingleToUInt32Bits(definition.Duration)));
                    // The buff's stats are in the character's sum now, so pools and the panel must follow.
                    RecomputePoolsAndSpeed(conn);
                    bytes.AddRange(PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
                    return [.. bytes];
                }
            });
        foreach (var effect in shrine.After) await effect.OnBuffApplied(ctx, shrine);
    }

    /// <summary>The buff's duration ending: the icon, its stats and its effect go together, and only if this granted
    /// buff is still the connection's buff of that row (a later shrine of the same row replaced it otherwise). Called
    /// from the tick at <see cref="ActiveBuff.EndsAt"/>, so it stops with the area instead of outliving it.</summary>
    static async Task EndBuff(ClientState conn, ushort row, ActiveBuff buff)
    {
        bool removed = false;
        await Send(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, [], $"0x01c2 shrine buff {row} ends", atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (conn.Closed || conn.LeavingArea) return null;
                var state = Of(conn);
                if (!state.Buffs.TryGetValue(row, out var current) || current.Instance != buff.Instance || current.Token != buff.Token) return null;
                state.Buffs.Remove(row);
                removed = true;
                var bytes = new List<byte>(InstanceEntities.RemoveBuff(PlayerId, buff.Instance));
                RecomputePoolsAndSpeed(conn);
                bytes.AddRange(PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
                return [.. bytes];
            }
        });
        if (!removed || conn.Closed || conn.LeavingArea) return;
        var ctx = new ShrineContext(conn, conn.Clock.Elapsed.TotalSeconds);
        foreach (var effect in buff.Shrine.After) await effect.OnExpired(ctx, buff.Shrine);
    }

    // ---- Per-tick, stats and cleanup ------------------------------------------------------------------------------

    /// <summary>One player tick: the unclaimed shrines' neighbourhood steps (monsters coming near and leaving) and the
    /// active effects. Leaving the area drops everything first.</summary>
    public static async Task Tick(ClientState conn)
    {
        if (conn.Closed) return;
        var state = Of(conn);
        double now = conn.Clock.Elapsed.TotalSeconds;
        var enters = new List<(ShrineInstance Shrine, uint Monster)>();
        var leaves = new List<(ShrineInstance Shrine, uint Monster)>();
        var effects = new List<(ShrineInstance Shrine, IShrineEffect Effect)>();
        var ended = new List<(ushort Row, ActiveBuff Buff)>();
        lock (conn.Monsters)
        {
            if (conn.LeavingArea)
            {
                ClearArea(state);
                return;
            }
            foreach (var shrine in state.Instances.Values.Where(s => s.State == ShrineState.Inactive))
            {
                var inside = new HashSet<uint>();
                foreach (var (id, monster) in conn.Monsters)
                    if (monster is { Hostile: true, Dead: false, Life: > 0 }
                        && Dist(monster.Walk.Position(now), shrine.At) <= NearbyRadius)
                        inside.Add(id);
                foreach (uint id in inside) if (shrine.Nearby.Add(id)) enters.Add((shrine, id));
                foreach (uint id in shrine.Nearby.Where(id => !inside.Contains(id)).ToArray())
                {
                    shrine.Nearby.Remove(id);
                    leaves.Add((shrine, id));
                }
            }
            foreach (var (row, buff) in state.Buffs)
            {
                if (buff.EndsAt <= now) { ended.Add((row, buff)); continue; }
                foreach (var effect in buff.Shrine.After) effects.Add((buff.Shrine, effect));
            }
        }
        var ctx = new ShrineContext(conn, now);
        foreach (var (shrine, monster) in enters)
            foreach (var effect in shrine.Before) await effect.OnNearbyMonsterEnter(ctx, shrine, monster);
        foreach (var (shrine, monster) in leaves)
            foreach (var effect in shrine.Before) await effect.OnNearbyMonsterLeave(ctx, shrine, monster);
        foreach (var (shrine, effect) in effects) await effect.Tick(ctx, shrine);
        foreach (var (row, buff) in ended) await EndBuff(conn, row, buff);
    }

    /// <summary>The area or the game is being left: the shrines' objects and the buffs on them go with the area. A
    /// claimed shrine's buff does not follow the player into the next area.</summary>
    public static async Task LeaveArea(ClientState conn)
    {
        var state = Of(conn);
        ShrineInstance[] shrines;
        lock (conn.Monsters)
        {
            shrines = [.. state.Instances.Values];
            ClearArea(state);
        }
        var ctx = new ShrineContext(conn, conn.Clock.Elapsed.TotalSeconds);
        foreach (var shrine in shrines)
        {
            foreach (var effect in shrine.Before) await effect.OnMapUnload(ctx, shrine);
            foreach (var effect in shrine.After) await effect.OnMapUnload(ctx, shrine);
        }
    }

    /// <summary>The claimed shrines' buff stats in the character's sum (<see cref="GameEvents.PlayerStatSources"/>), and
    /// nothing for a shrine that is unclaimed, spent or gone.</summary>
    public static Dictionary<uint, int> AddStats(ClientState conn, Dictionary<uint, int> stats)
    {
        var state = Of(conn);
        lock (conn.Monsters)
            foreach (var buff in state.Buffs.Values)
                foreach (var (key, value) in buff.Shrine.Stats)
                    stats[key] = (int)Math.Clamp((long)stats.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
        return stats;
    }

    /// <summary>The area's monster level: an open map's own level for this area, else the WorldAreas level.</summary>
    public static byte AreaLevel(ClientState conn)
    {
        int level = ServerLevels.AreaLevel(conn.Layout.Id);
        if (MapDevice.OpenOf(CharacterName(conn)) is { } openMap && openMap.Area.Hash16 == conn.Layout.Hash && openMap.Area.Level > 0)
            level = openMap.Area.Level;
        return (byte)Math.Clamp(level, 1, 100);
    }

    static void ClearArea(AreaState state)
    {
        state.Instances.Clear();
        state.Buffs.Clear();
    }
}
