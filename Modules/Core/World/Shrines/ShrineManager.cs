using GameServer.Modules.Social;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using static Game;
using static Program;

namespace GameServer.Modules.Shrines;

/// <summary>Physical shrines belong to the retained instance. Each player owns their own timed buffs and effects.</summary>
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

    sealed class SharedArea
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly Dictionary<uint, ShrineInstance> Instances = [];
        public bool Spawned;
    }

    sealed class AreaState(SharedArea area)
    {
        public SharedArea Area = area;
        public Dictionary<uint, ShrineInstance> Instances => Area.Instances;
        public readonly Dictionary<ushort, ActiveBuff> Buffs = [];
        public readonly HashSet<uint> Presented = [];
        public int Token;
    }

    static readonly ConditionalWeakTable<InstanceState, SharedArea> Areas = new();
    static readonly ConditionalWeakTable<ClientState, AreaState> States = new();
    static AreaState Of(ClientState conn) => States.GetValue(conn,
        c => new(c.Instance is { } instance ? Areas.GetOrCreateValue(instance) : new()));
    static string ClaimKey(ShrineInstance shrine) => shrine.Definition.Lesser ? $"Shrines/used-lesser/{shrine.ObjectId}"
        : $"Shrines/used/{shrine.At.X}/{shrine.At.Y}";
    sealed record KeptLesser(string Type, int X, int Y, double Until);
    static ClientState[] Members(ClientState conn) => States.Where(p => ReferenceEquals(p.Value.Area, Of(conn).Area)
        && !p.Key.Closed && !p.Key.LeavingArea).Select(p => p.Key).ToArray();

    public static async Task SpawnArea(ClientState conn)
    {
        var state = Of(conn);
        await state.Area.Gate.WaitAsync();
        try
        {
            if (!state.Area.Spawned)
            {
                state.Area.Spawned = true;
                if (conn.Layout.Walkable is { } walk && ServerLevels.For(conn.Layout.Id, conn.Seed) is { } level)
                    foreach (var placed in ShrinePlacement.Plan(level.RoomObjects, walk, conn.Seed))
                        await Create(conn, NewInstance(placed.Shrine, placed.X, placed.Y, placed.Rotation, placed.Model));
                if (conn.Instance is { } instance)
                    foreach (var (key, value) in instance.ContentState.Where(p => p.Key.StartsWith("Shrines/lesser/", StringComparison.Ordinal)))
                    {
                        var kept = JsonSerializer.Deserialize<KeptLesser>(value)!;
                        if (kept.Until <= conn.Clock.Elapsed.TotalSeconds) { instance.ContentState.TryRemove(key, out _); continue; }
                        if (ShrineCatalog.Find(kept.Type) is not { Lesser: true } type) continue;
                        var lesser = NewInstance(type, kept.X, kept.Y, 0, null);
                        lesser.ExpiresAt = kept.Until;
                        await Create(conn, lesser, uint.Parse(key["Shrines/lesser/".Length..]));
                    }
            }
            foreach (var shrine in state.Instances.Values) await Present(conn, shrine);
        }
        finally { state.Area.Gate.Release(); }
    }

    static ShrineInstance NewInstance(ShrineCatalog.ShrineDefinition definition, int x, int y, float rotation, string? model)
    {
        var registered = ShrineRegistry.Find(definition.Id);
        return new()
        {
            Definition = definition,
            At = (x, y),
            Rotation = rotation,
            Model = registered?.Model ?? model,
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
    static async Task Create(ClientState conn, ShrineInstance shrine, uint restoredId = 0)
    {
        uint id = restoredId != 0 ? restoredId : !shrine.Definition.Lesser && conn.Instance is { } instance
            ? uint.Parse(instance.ContentState.GetOrAdd($"Shrines/id/{shrine.At.X}/{shrine.At.Y}", _ => conn.TakeObjectId().ToString()))
            : conn.TakeObjectId();
        shrine.ObjectId = id;
        if (shrine.Definition.Lesser && conn.Instance is { } owner)
            owner.ContentState[$"Shrines/lesser/{id}"] = JsonSerializer.Serialize(new KeptLesser(shrine.Definition.Id,
                shrine.At.X, shrine.At.Y, shrine.ExpiresAt));
        if (conn.Instance?.HasState(ClaimKey(shrine)) == true) shrine.State = ShrineState.Used;
        Of(conn).Instances[id] = shrine;
        foreach (var member in Members(conn)) await Present(member, shrine);
        if (shrine.State == ShrineState.Inactive)
            foreach (var effect in shrine.Before) await effect.OnSpawn(new(conn, conn.Clock.Elapsed.TotalSeconds), shrine);
    }

    static async Task Present(ClientState conn, ShrineInstance shrine)
    {
        if (!Of(conn).Presented.Add(shrine.ObjectId)) return;
        var obj = new ShrineObject(shrine.At.X, shrine.At.Y, shrine.Rotation, Model: shrine.Model,
            Row: shrine.State == ShrineState.Used ? NoShrineRow : shrine.Definition.Row, Lesser: shrine.Definition.Lesser);
        await SendWorldCreate(conn, shrine.ObjectId,
            InstanceEntities.CreateObject(shrine.ObjectId, InstanceEntities.ObjectTypeHash(shrine.Definition.Lesser ? ShrinePlacement.LesserType : ShrinePlacement.Type), obj.Blob()),
            $"create shrine {shrine.Definition.Id} at {shrine.At}");
        await SendWorldRecord(conn, shrine.ObjectId, InstanceProtocol.EnterWakeZone(shrine.ObjectId), null);
    }

    public static async Task SpawnLesser(ClientState conn, (int X, int Y) at, double seconds, string type)
    {
        if (ShrineCatalog.Find(type) is not { Lesser: true } definition || conn.Layout.Walkable is not { } walk) return;
        var state = Of(conn);
        await state.Area.Gate.WaitAsync();
        try
        {
            if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || seconds <= 0) return;
            var cell = Clear(walk, at.X, at.Y, 4);
            if (!walk(cell.X, cell.Y)) return;
            var shrine = NewInstance(definition, cell.X, cell.Y, 0, null);
            shrine.ExpiresAt = conn.Clock.Elapsed.TotalSeconds + seconds;
            await Create(conn, shrine);
        }
        finally { state.Area.Gate.Release(); }
    }

    /// <summary>Dev (/shrines): the named types, or every basic type, on a ring around the player inside the click
    /// range, so each type's icon, buff and tooltip can be checked in one area instead of hunting a seed that rolls it.</summary>
    public static async Task<int> SpawnAroundPlayer(ClientState conn, IReadOnlyList<string> ids)
    {
        var state = Of(conn);
        await state.Area.Gate.WaitAsync();
        try
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
            var shrine = NewInstance(types[i], x, y, 0, types[i].Lesser ? null : ShrinePlacement.DefaultModel);
            if (types[i].Lesser) shrine.ExpiresAt = conn.Clock.Elapsed.TotalSeconds + 10;
            await Create(conn, shrine);
            made++;
        }
        Console.WriteLine($"[gateway] {conn.Wire.Remote} /shrines: {made} shrine(s) created around ({px}, {py})");
        return made;
        }
        finally { state.Area.Gate.Release(); }
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
        ShrineInstance? shrine;
        await state.Area.Gate.WaitAsync();
        try { state.Instances.TryGetValue(use.TargetId, out shrine); }
        finally { state.Area.Gate.Release(); }
        if (shrine is null) return false;
        if (conn.PlayerLife == 0 || conn.Closed || conn.LeavingArea) return true;
        int action = Interlocked.Increment(ref conn.ActionCount);
        double now = conn.Clock.Elapsed.TotalSeconds;
        int objectSize = shrine.Definition.Lesser ? 2 : ShrinePlacement.ObjectSize;
        var (here, stop, arrive) = conn.Walk.WalkUpTo(shrine.At.X, shrine.At.Y, objectSize, now);
        if (!use.Started)
            await SendWorldRecord(conn, PlayerId,
                InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, shrine.At.X, shrine.At.Y, use.SkillId,
                    use.Sequence, use.Flags, targetObjectId: use.TargetId), null,
                still: () => PlayerActionCurrent(conn, action));
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        await state.Area.Gate.WaitAsync();
        try
        {
            if (PlayerActionCurrent(conn, action) && conn.PlayerLife > 0
                && PlayerWalk.InWalkUpRange(conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds), shrine.At, objectSize)
                && shrine.State == ShrineState.Inactive
                && shrine.ExpiresAt > conn.Clock.Elapsed.TotalSeconds
                && (conn.Instance is null || conn.Instance.SetState(ClaimKey(shrine))))
            {
                // Only arrival claims the shared object; another player may reach it while this player is walking.
                shrine.State = ShrineState.Used;
                var ctx = new ShrineContext(conn, conn.Clock.Elapsed.TotalSeconds);
                foreach (var effect in shrine.Before) await effect.OnActivation(ctx, shrine);
                uint party = PartyRoster.PartyOf(conn);
                foreach (var member in Members(conn))
                {
                    await Spent(member, shrine);
                    if (member.PlayerLife == 0) continue;
                    if (!ReferenceEquals(member, conn) && (shrine.Definition.Monster is null || party == 0
                        || PartyRoster.PartyOf(member) != party || Dist(member.Walk.Position(ctx.Now), shrine.At) > NearbyRadius)) continue;
                    // Effects that follow a player must never share their timers, skeletons or art with a party member.
                    var personal = NewInstance(shrine.Definition, shrine.At.X, shrine.At.Y, shrine.Rotation, shrine.Model);
                    personal.ObjectId = shrine.ObjectId;
                    personal.State = ShrineState.Used;
                    personal.ActivatedAt = ctx.Now;
                    await Grant(member, personal, new(member, member.Clock.Elapsed.TotalSeconds));
                }
            }
        }
        finally { state.Area.Gate.Release(); }
        await SendWorldRecord(conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null,
            still: () => PlayerActionCurrent(conn, action));
        return true;
    }

    /// <summary>The claimed shrine's object again, in the same place with the same model, but with the Shrine component's
    /// "no shrine" key (<see cref="NoShrineRow"/>): the icon it drew and its interaction are gone with the row. The object
    /// id is kept, so the client replaces the one it has (the opened-chest pattern).</summary>
    static async Task Spent(ClientState conn, ShrineInstance shrine)
    {
        await SendWorldRemoval(conn, shrine.ObjectId, null);
        if (shrine.Definition.Lesser)
        {
            Of(conn).Presented.Remove(shrine.ObjectId);
            Of(conn).Instances.Remove(shrine.ObjectId);
            conn.Instance?.ContentState.TryRemove($"Shrines/lesser/{shrine.ObjectId}", out _);
            conn.Instance?.ContentState.TryRemove(ClaimKey(shrine), out _);
            return;
        }
        await SendWorldCreate(conn, shrine.ObjectId,
            InstanceEntities.CreateObject(shrine.ObjectId, InstanceEntities.ObjectTypeHash(ShrinePlacement.Type),
                new ShrineObject(shrine.At.X, shrine.At.Y, shrine.Rotation, Model: shrine.Model, Row: NoShrineRow).Blob()),
            $"0x02bb shrine 0x{shrine.ObjectId:x} {shrine.Definition.Id} used (no row, no icon, not interactable)");
        await SendWorldRecord(conn, shrine.ObjectId, InstanceProtocol.EnterWakeZone(shrine.ObjectId), null);
    }

    /// <summary>Whether this shrine's buff grants a stat the client only reads from the skill panel, so the panel has to
    /// be rebuilt when the buff starts or ends (every other shrine leaves the panel alone: re-sending it mid-fight is a
    /// long message and the client desyncs on it).</summary>
    static bool TouchesSkillPanel(ShrineInstance shrine)
    {
        if (Stats.Key("skill_repeat_count") is not uint repeat || Stats.Key("base_spell_repeat_count") is not uint baseRepeat)
            return false;
        return shrine.Stats.Any(s => (s.Key == repeat || s.Key == baseRepeat) && s.Value > 0);
    }

    /// <summary>Grants the shrine's buff for its row's duration and starts its effect. A second shrine of the same row
    /// replaces the first: its buff is removed before the new one, and the older timer cannot remove the newer buff.</summary>
    static async Task Grant(ClientState conn, ShrineInstance shrine, ShrineContext ctx)
    {
        var state = Of(conn);
        var definition = shrine.Definition;
        var playerStats = CharacterPlayerStats(conn);
        int Stat(string name) => Stats.Key(name) is uint key ? playerStats.GetValueOrDefault(key) : 0;
        int duration = Stat("shrine_effect_duration_+%") + (MapModsOf(conn)?.Get("map_player_shrine_effect_duration_+%") ?? 0);
        shrine.Duration = Math.Clamp(shrine.Duration * Math.Max(0L, 100L + duration) / 100, 0, int.MaxValue);
        int effectPercent = Stat("shrine_buff_effect_on_self_+%") + (MapModsOf(conn)?.Get("map_player_shrine_buff_effect_on_self_+%") ?? 0);
        if (effectPercent != 0 && (definition.Monster is not null || definition.Lesser))
            shrine.Stats = shrine.Stats.Select(s => (s.Key, Stats.Name(s.Key) is "has_shrine_effect" or "always_crit" or "base_cannot_be_damaged" or "global_knockback" or "extra_gore"
                ? s.Value : (int)Math.Clamp((long)s.Value * Math.Max(0L, 100L + effectPercent) / 100, int.MinValue, int.MaxValue))).ToArray();
        ActiveBuff? previous;
        lock (conn.Monsters) previous = state.Buffs.GetValueOrDefault(definition.BuffRow);
        if (previous is not null) await EndBuff(conn, definition.BuffRow, previous);
        if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return;
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
            await Send(conn, replaced, $"0x01c2 shrine buff {definition.BuffRow} replaced by a new shrine");
        await Send(conn, [], $"0x01c1 shrine {definition.Id} buff {definition.BuffRow} for {shrine.Duration} s",
            atGate: () =>
            {
                lock (conn.Monsters)
                {
                    if (conn.Closed || conn.LeavingArea) return null;
                    var bytes = new List<byte>(InstanceEntities.AddBuff(PlayerId, instance, definition.BuffRow, PlayerId,
                        u68: BitConverter.SingleToUInt32Bits((float)shrine.Duration)));
                    // The buff's stats are in the character's sum now, so pools and the panel must follow.
                    RecomputePoolsAndSpeed(conn);
                    bytes.AddRange(PlayerStatsOffFrames(conn));
                    return [.. bytes];
                }
            });
        // The repeat count a shrine can grant (Echoing's skill_repeat_count) is part of the skill panel, not of the
        // character stats the client re-reads, so the panel is rebuilt for the shrines that grant one and no others:
        // a full rebuild re-sends every gem's skill and the skill bar, which the client cannot take mid-fight.
        if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return;
        if (TouchesSkillPanel(shrine)) await SendSkills(conn);
        foreach (var effect in shrine.After) await effect.OnBuffApplied(ctx, shrine);
    }

    /// <summary>The buff's duration ending: the icon, its stats and its effect go together, and only if this granted
    /// buff is still the connection's buff of that row (a later shrine of the same row replaced it otherwise). Called
    /// from the tick at <see cref="ActiveBuff.EndsAt"/>, so it stops with the area instead of outliving it.</summary>
    static async Task EndBuff(ClientState conn, ushort row, ActiveBuff buff)
    {
        bool removed = false;
        await Send(conn, [], $"0x01c2 shrine buff {row} ends", atGate: () =>
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
                bytes.AddRange(PlayerStatsOffFrames(conn));
                return [.. bytes];
            }
        });
        if (!removed || conn.Closed || conn.LeavingArea) return;
        if (TouchesSkillPanel(buff.Shrine)) await SendSkills(conn);
        var ctx = new ShrineContext(conn, conn.Clock.Elapsed.TotalSeconds);
        foreach (var effect in buff.Shrine.After) await effect.OnExpired(ctx, buff.Shrine);
    }

    // ---- Per-tick, stats and cleanup ------------------------------------------------------------------------------

    /// <summary>One player tick: the unclaimed shrines' neighbourhood steps (monsters coming near and leaving) and the
    /// active effects. Leaving the area drops everything first.</summary>
    public static async Task Tick(ClientState conn)
    {
        if (conn.Closed || conn.LeavingArea || conn.Paused) return;
        await LesserShrines.Tick(conn);
        var state = Of(conn);
        await state.Area.Gate.WaitAsync();
        try
        {
            if (conn.Closed || conn.LeavingArea) return;
            double now = conn.Clock.Elapsed.TotalSeconds;
            var ctx = new ShrineContext(conn, now);
            foreach (var expired in state.Instances.Values.Where(s => s.ExpiresAt <= now).ToArray())
            {
                expired.State = ShrineState.Used;
                foreach (var member in Members(conn)) await Spent(member, expired);
            }
            // One driver for the physical area's hazards and auras, even when a party shares it.
            if (ReferenceEquals(Members(conn).Where(c => c.PlayerSent).MinBy(c => c.PeerId), conn))
                foreach (var shrine in state.Instances.Values.Where(s => s.State == ShrineState.Inactive))
                {
                    uint[] entered, left;
                    lock (conn.Monsters)
                    {
                        var inside = conn.Monsters.Where(p => p.Value is { Hostile: true, Dead: false, Life: > 0 }
                            && Dist(p.Value.Walk.Position(now), shrine.At) <= NearbyRadius).Select(p => p.Key).ToHashSet();
                        entered = inside.Except(shrine.Nearby).ToArray();
                        left = shrine.Nearby.Except(inside).ToArray();
                        shrine.Nearby.Clear(); shrine.Nearby.UnionWith(inside);
                    }
                    foreach (var effect in shrine.Before)
                    {
                        foreach (uint id in entered) await effect.OnNearbyMonsterEnter(ctx, shrine, id);
                        foreach (uint id in left) await effect.OnNearbyMonsterLeave(ctx, shrine, id);
                        await effect.Tick(ctx, shrine);
                    }
                }
            foreach (var (row, buff) in state.Buffs.ToArray())
                if (conn.PlayerLife == 0 || buff.EndsAt <= now) await EndBuff(conn, row, buff);
                else foreach (var effect in buff.Shrine.After) await effect.Tick(ctx, buff.Shrine);
        }
        finally { state.Area.Gate.Release(); }
    }

    public static async Task LeaveArea(ClientState conn)
    {
        if (!States.TryGetValue(conn, out var state)) return;
        await state.Area.Gate.WaitAsync();
        try
        {
            var ctx = new ShrineContext(conn, conn.Clock.Elapsed.TotalSeconds);
            ActiveBuff[] buffs;
            lock (conn.Monsters) { buffs = [.. state.Buffs.Values]; state.Buffs.Clear(); }
            foreach (var buff in buffs)
                foreach (var effect in buff.Shrine.After) await effect.OnMapUnload(ctx, buff.Shrine);
            States.Remove(conn);
            if (!States.Any(p => ReferenceEquals(p.Value.Area, state.Area)))
            {
                foreach (var shrine in state.Instances.Values)
                    foreach (var effect in shrine.Before) await effect.OnMapUnload(ctx, shrine);
                state.Instances.Clear();
                state.Area.Spawned = false;
            }
        }
        finally { state.Area.Gate.Release(); }
    }

    // Module unload also releases effects on monsters that survive the assembly reload. Claim flags live in InstanceState.
    public static IDisposable Lifetime() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var (conn, _) in States.ToArray()) LeaveArea(conn).GetAwaiter().GetResult();
            Areas.Clear();
        }
    }

    public static ClientState[] NearbyPlayers(ClientState conn, (int X, int Y) at, double radius) => Members(conn)
        .Where(c => c.PlayerSent && c.PlayerLife > 0 && Dist(c.Walk.Position(c.Clock.Elapsed.TotalSeconds), at) <= radius).ToArray();

    /// <summary>A standing shrine spell uses the same defenses and death accounting as an enemy spell.</summary>
    public static async Task HitPlayers(ClientState source, (int X, int Y) at, double radius, DamageType type, DamagePacket packet,
        Func<ClientState, bool>? accept = null)
    {
        foreach (var conn in NearbyPlayers(source, at, radius))
        {
            if (accept is not null && !accept(conn)) continue;
            int amount = (int)Math.Clamp(packet.Roll(Random.Shared).MinTotal, 0, int.MaxValue);
            if (amount <= 0 || conn.Immune || InGracePeriod(conn) || !TrySyntheticEnemyHit(conn, type, amount, block: true, spell: true, suppress: true, out var hit)) continue;
            if (hit.Damage > 0)
            {
                var stats = CharacterPlayerStats(conn);
                lock (conn.Monsters)
                    ApplyPlayerAilmentsLocked(conn, DamagePacket.Of(type, amount, amount), DamagePacket.Of(type, hit.Damage, hit.Damage),
                        false, hit.Blocked, conn.Clock.Elapsed.TotalSeconds, stats);
            }
            await SendWorldRecord(conn, PlayerId,
                InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerLife, conn.PlayerReservedLife), "shrine spell hit");
            await SendPlayerEnergyShield(conn);
            if (hit.Mana != hit.ManaBefore)
                await SendPlayerMana(conn, "shrine spell mana");
            if (conn.PlayerLife == 0) await PlayerDied(conn, "death from shrine spell");
        }
    }

    /// <summary>The claimed shrines' buff stats in the character's sum (<see cref="GameEvents.PlayerStatSources"/>), and
    /// nothing for a shrine that is unclaimed, spent or gone.</summary>
    public static Dictionary<uint, int> AddStats(ClientState conn, Dictionary<uint, int> stats)
    {
        if (!States.TryGetValue(conn, out var state)) return stats;
        lock (conn.Monsters)
            foreach (var buff in state.Buffs.Values.Where(b => conn.PlayerLife > 0 && b.EndsAt > conn.Clock.Elapsed.TotalSeconds))
                foreach (var (key, value) in buff.Shrine.Stats)
                    stats[key] = (int)Math.Clamp((long)stats.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
        return stats;
    }

    /// <summary>The area's monster level: an open map's own level for this area, else the WorldAreas level.</summary>
    public static byte AreaLevel(ClientState conn)
    {
        int level = ServerLevels.AreaLevel(conn.Layout.Id);
        if (MapRunOf(conn) is { Area.Level: > 0 } openMap)
            level = openMap.Area.Level;
        return (byte)Math.Clamp(level, 1, 100);
    }

}
