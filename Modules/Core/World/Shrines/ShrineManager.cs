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
        // The shrines the area's clients were shown (each one the area's object, created once).
        public readonly HashSet<uint> Shown = [], Spent = [];
        public bool Spawned;
    }

    sealed class AreaState(SharedArea area)
    {
        public SharedArea Area = area;
        public Dictionary<uint, ShrineInstance> Instances => Area.Instances;
        public readonly Dictionary<ushort, ActiveBuff> Buffs = [];
        public readonly List<int> PendingKillShrineDurations = [];
        public double NextAscendancyShrineAt;
        public double NextPeriodicShrineAt;
        public double NextBloodShrineAt;
        public int Token;
    }

    /// <summary>Base duration in seconds for periodic random shrine grants (ClientStrings ReminderTextRandomShrineDuration).</summary>
    public const int RandomShrineBaseDurationSeconds = 20;

    static readonly string[] BloodShrineIds =
        ["BloodShrineRat", "BloodShrineSwarm", "BloodShrineToad", "BloodShrineCrow", "BloodShrineBat"];

    public static IReadOnlyList<ShrineCatalog.ShrineDefinition> BloodShrines =>
        [.. ShrineCatalog.All.Where(s => BloodShrineIds.Contains(s.Id))];

    static readonly ConditionalWeakTable<InstanceState, SharedArea> Areas = new();
    static readonly ConditionalWeakTable<Player, AreaState> States = new();
    static AreaState Of(Player player) => States.GetValue(player,
        c => new(c.Instance is { } instance ? Areas.GetOrCreateValue(instance) : new()));
    static string ClaimKey(ShrineInstance shrine) => shrine.Definition.Lesser ? $"Shrines/used-lesser/{shrine.ObjectId}"
        : $"Shrines/used/{shrine.At.X}/{shrine.At.Y}";
    sealed record KeptLesser(string Type, int X, int Y, double Until);
    static Player[] Members(Player player) => States.Where(p => ReferenceEquals(p.Value.Area, Of(player).Area)
        && !p.Key.Closed && !p.Key.LeavingArea).Select(p => p.Key).ToArray();

    public static async Task SpawnArea(Player player)
    {
        var state = Of(player);
        await state.Area.Gate.WaitAsync();
        try
        {
            if (!state.Area.Spawned)
            {
                state.Area.Spawned = true;
                if (player.Layout.Walkable is { } walk && ServerLevels.For(player.Layout.Id, player.Seed) is { } level)
                    foreach (var placed in ShrinePlacement.Plan(level.RoomObjects, walk, player.Seed,
                        greedChancePct: MapModsOf(player)?.Get(Stat.MapShrinesChanceToBeGreedShrinePct) ?? 0))
                        await Create(player, NewInstance(placed.Shrine, placed.X, placed.Y, placed.Rotation, placed.Model));
                if (player.Instance is { } instance)
                    foreach (var (key, value) in instance.ContentState.Where(p => p.Key.StartsWith("Shrines/lesser/", StringComparison.Ordinal)))
                    {
                        var kept = JsonSerializer.Deserialize<KeptLesser>(value)!;
                        if (kept.Until <= AreaTime(player)) { instance.ContentState.TryRemove(key, out _); continue; }
                        if (ShrineCatalog.Find(kept.Type) is not { Lesser: true } type) continue;
                        var lesser = NewInstance(type, kept.X, kept.Y, 0, null);
                        lesser.ExpiresAt = kept.Until;
                        await Create(player, lesser, uint.Parse(key["Shrines/lesser/".Length..]));
                    }
            }
            foreach (var shrine in state.Instances.Values) await Present(player, shrine);
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
            Stats = registered?.PlayerStats ?? definition.Stats,
            Duration = registered?.Duration ?? definition.Duration,
        };
    }

    /// <summary>Creates one shrine object at its cell and registers it for clicks. The Shrines row goes in the object's
    /// Shrine component (the client draws that row's buff icon over it), so the icon and the buff the click grants are
    /// one value.</summary>
    static async Task Create(Player player, ShrineInstance shrine, uint restoredId = 0)
    {
        uint id = restoredId != 0 ? restoredId : !shrine.Definition.Lesser && player.Instance is { } instance
            ? uint.Parse(instance.ContentState.GetOrAdd($"Shrines/id/{shrine.At.X}/{shrine.At.Y}", _ => player.TakeObjectId().ToString()))
            : player.TakeObjectId();
        shrine.ObjectId = id;
        if (shrine.Definition.Lesser && player.Instance is { } owner)
            owner.ContentState[$"Shrines/lesser/{id}"] = JsonSerializer.Serialize(new KeptLesser(shrine.Definition.Id,
                shrine.At.X, shrine.At.Y, shrine.ExpiresAt));
        if (player.Instance?.HasState(ClaimKey(shrine)) == true) shrine.State = ShrineState.Used;
        Of(player).Instances[id] = shrine;
        await Present(player, shrine);
        if (shrine.State == ShrineState.Inactive)
            foreach (var effect in shrine.Before) await effect.OnSpawn(new(player, AreaTime(player)), shrine);
    }

    static async Task Present(Player player, ShrineInstance shrine)
    {
        if (!Of(player).Area.Shown.Add(shrine.ObjectId)) return;
        var obj = new ShrineObject(shrine.At.X, shrine.At.Y, shrine.Rotation, Model: shrine.Model,
            Row: shrine.State == ShrineState.Used ? NoShrineRow : shrine.Definition.Row, Lesser: shrine.Definition.Lesser);
        await SendWorldCreate(player, shrine.ObjectId,
            InstanceEntities.CreateObject(shrine.ObjectId, InstanceEntities.ObjectTypeHash(shrine.Definition.Lesser ? ShrinePlacement.LesserType : ShrinePlacement.Type), obj.Blob()),
            $"create shrine {shrine.Definition.Id} at {shrine.At}");
        await SendWorldRecord(player, shrine.ObjectId, InstanceProtocol.EnterWakeZone(shrine.ObjectId), null);
    }

    public static async Task SpawnLesser(Player player, (int X, int Y) at, double seconds, string type)
    {
        if (ShrineCatalog.Find(type) is not { Lesser: true } definition || player.Layout.Walkable is not { } walk) return;
        var state = Of(player);
        await state.Area.Gate.WaitAsync();
        try
        {
            if (player.Closed || player.LeavingArea || player.PlayerLife == 0 || seconds <= 0) return;
            var cell = Clear(walk, at.X, at.Y, 4);
            if (!walk(cell.X, cell.Y)) return;
            var shrine = NewInstance(definition, cell.X, cell.Y, 0, null);
            shrine.ExpiresAt = AreaTime(player) + seconds;
            await Create(player, shrine);
        }
        finally { state.Area.Gate.Release(); }
    }

    /// <summary>Dev (/shrines): the named types, or every basic type, on a ring around the player inside the click
    /// range, so each type's icon, buff and tooltip can be checked in one area instead of hunting a seed that rolls it.</summary>
    public static async Task<int> SpawnAroundPlayer(Player player, IReadOnlyList<string> ids)
    {
        var state = Of(player);
        await state.Area.Gate.WaitAsync();
        try
        {
        if (player.Layout.Walkable is not { } walk) return 0;
        var types = new List<ShrineCatalog.ShrineDefinition>();
        if (ids.Count == 0) types.AddRange(ShrineCatalog.Standard);
        else foreach (string id in ids) types.Add(FindDefinition(id));
        var (px, py) = player.Walk.Position(AreaTime(player));
        int made = 0;
        for (int i = 0; i < types.Count; i++)
        {
            double angle = Math.PI * 2 * i / types.Count;
            var (x, y) = Clear(walk, px + (int)Math.Round(Math.Cos(angle) * (ShrinePlacement.ObjectSize + 5)),
                py + (int)Math.Round(Math.Sin(angle) * (ShrinePlacement.ObjectSize + 5)), ShrinePlacement.ObjectSize + 6);
            var shrine = NewInstance(types[i], x, y, 0, types[i].Lesser ? null : ShrinePlacement.DefaultModel);
            if (types[i].Lesser) shrine.ExpiresAt = AreaTime(player) + 10;
            await Create(player, shrine);
            made++;
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} /shrines: {made} shrine(s) created around ({px}, {py})");
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
    public static async Task<bool> Use(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        var state = Of(player);
        ShrineInstance? shrine;
        await state.Area.Gate.WaitAsync();
        try { state.Instances.TryGetValue(use.TargetId, out shrine); }
        finally { state.Area.Gate.Release(); }
        if (shrine is null) return false;
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea) return true;
        int action = Interlocked.Increment(ref player.ActionCount);
        double now = AreaTime(player);
        int objectSize = shrine.Definition.Lesser ? 2 : ShrinePlacement.ObjectSize;
        var (here, stop, arrive) = player.Walk.WalkUpTo(shrine.At.X, shrine.At.Y, objectSize, now);
        if (!use.Started)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, shrine.At.X, shrine.At.Y, use.SkillId,
                    use.Sequence, use.Flags, targetObjectId: use.TargetId), null,
                still: () => PlayerActionCurrent(player, action));
        if (arrive > now) await AreaDelay(player, arrive - now);
        await state.Area.Gate.WaitAsync();
        try
        {
            if (PlayerActionCurrent(player, action) && player.PlayerLife > 0
                && PlayerWalk.InWalkUpRange(player.Walk.Position(AreaTime(player)), shrine.At, objectSize)
                && shrine.State == ShrineState.Inactive
                && shrine.ExpiresAt > AreaTime(player)
                && (player.Instance is null || player.Instance.SetState(ClaimKey(shrine))))
            {
                // Only arrival claims the shared object; another player may reach it while this player is walking.
                shrine.State = ShrineState.Used;
                var ctx = new ShrineContext(player, AreaTime(player));
                foreach (var effect in shrine.Before) await effect.OnActivation(ctx, shrine);
                uint party = PartyRoster.PartyOf(player);
                foreach (var member in Members(player))
                {
                    await Spent(member, shrine);
                    if (member.PlayerLife == 0) continue;
                    if (!ReferenceEquals(member, player) && (shrine.Definition.Monster is null || party == 0
                        || PartyRoster.PartyOf(member) != party || Dist(member.Walk.Position(ctx.Now), shrine.At) > NearbyRadius)) continue;
                    // Effects that follow a player must never share their timers, skeletons or art with a party member.
                    var personal = NewInstance(shrine.Definition, shrine.At.X, shrine.At.Y, shrine.Rotation, shrine.Model);
                    personal.ObjectId = shrine.ObjectId;
                    personal.State = ShrineState.Used;
                    personal.ActivatedAt = ctx.Now;
                    await Grant(member, personal, new(member, AreaTime(member)));
                    if (!shrine.Definition.Lesser && !shrine.Definition.PvPOnly
                        && (MapModsOf(member)?.Get(Stat.MapShrinesGrantARandomAdditionalEffect) ?? 0) > 0)
                    {
                        var pool = ShrineCatalog.Standard.Where(s => !string.Equals(s.Id, shrine.Definition.Id, StringComparison.Ordinal)).ToArray();
                        if (pool.Length > 0)
                        {
                            var bonusDef = pool[AreaRng(member).Next(pool.Length)];
                            var bonusShrine = NewInstance(bonusDef, shrine.At.X, shrine.At.Y, shrine.Rotation, shrine.Model);
                            bonusShrine.ObjectId = shrine.ObjectId;
                            bonusShrine.State = ShrineState.Used;
                            bonusShrine.ActivatedAt = ctx.Now;
                            await Grant(member, bonusShrine, new(member, AreaTime(member)));
                        }
                    }
                }
            }
        }
        finally { state.Area.Gate.Release(); }
        await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null,
            still: () => PlayerActionCurrent(player, action));
        return true;
    }

    /// <summary>The claimed shrine's object again, in the same place with the same model, but with the Shrine component's
    /// "no shrine" key (<see cref="NoShrineRow"/>): the icon it drew and its interaction are gone with the row. The object
    /// id is kept, so the client replaces the one it has (the opened-chest pattern).</summary>
    // A used shrine changes for every client at once (its members' loops call this for each of them).
    static async Task Spent(Player player, ShrineInstance shrine)
    {
        if (!Of(player).Area.Spent.Add(shrine.ObjectId)) return;
        await SendWorldRemoval(player, shrine.ObjectId, null);
        if (shrine.Definition.Lesser)
        {
            Of(player).Area.Shown.Remove(shrine.ObjectId);
            Of(player).Instances.Remove(shrine.ObjectId);
            player.Instance?.ContentState.TryRemove($"Shrines/lesser/{shrine.ObjectId}", out _);
            player.Instance?.ContentState.TryRemove(ClaimKey(shrine), out _);
            return;
        }
        await SendWorldCreate(player, shrine.ObjectId,
            InstanceEntities.CreateObject(shrine.ObjectId, InstanceEntities.ObjectTypeHash(ShrinePlacement.Type),
                new ShrineObject(shrine.At.X, shrine.At.Y, shrine.Rotation, Model: shrine.Model, Row: NoShrineRow).Blob()),
            $"0x02bb shrine 0x{shrine.ObjectId:x} {shrine.Definition.Id} used (no row, no icon, not interactable)");
        await SendWorldRecord(player, shrine.ObjectId, InstanceProtocol.EnterWakeZone(shrine.ObjectId), null);
    }

    /// <summary>Whether this shrine's buff grants a stat the client only reads from the skill panel, so the panel has to
    /// be rebuilt when the buff starts or ends (every other shrine leaves the panel alone: re-sending it mid-fight is a
    /// long message and the client desyncs on it).</summary>
    static bool TouchesSkillPanel(ShrineInstance shrine)
    {
        if (!(Stat.SkillRepeatCount is var repeat) || !(Stat.BaseSpellRepeatCount is var baseRepeat))
            return false;
        return shrine.Stats.Any(s => (s.Key == repeat || s.Key == baseRepeat) && s.Value > 0);
    }

    /// <summary>Grants the shrine's buff for its row's duration and starts its effect. A second shrine of the same row
    /// replaces the first: its buff is removed before the new one, and the older timer cannot remove the newer buff.</summary>
    static async Task Grant(Player player, ShrineInstance shrine, ShrineContext ctx, bool mapGranted = true)
    {
        var state = Of(player);
        var definition = shrine.Definition;
        var playerStats = CharacterPlayerStats(player);
        int Stat(uint key) => playerStats.GetValueOrDefault(key);
        int duration = Stat(global::Stat.ShrineEffectDurationPlusPct) + (MapModsOf(player)?.Get(global::Stat.MapPlayerShrineEffectDurationPlusPct) ?? 0);
        if (mapGranted && !definition.Lesser) duration += MapModsOf(player)?.Get(global::Stat.MapShrineGrantedPlayerBuffDurationPlusPct) ?? 0;
        shrine.Duration = Math.Clamp(shrine.Duration * Math.Max(0L, 100L + duration) / 100, 0, int.MaxValue);
        int effectPercent = ShrineGearEffects.EffectiveShrineEffectPct(player, Stat, mapGranted && !definition.Lesser);
        if (effectPercent != 0)
            shrine.Stats = shrine.Stats.Select(s => (s.Key, s.Key is global::Stat.HasShrineEffect or global::Stat.AlwaysCrit
                or global::Stat.BaseCannotBeDamaged or global::Stat.GlobalKnockback or global::Stat.ExtraGore or global::Stat.SkillRepeatCount
                ? s.Value : (int)Math.Clamp((long)s.Value * Math.Max(0L, 100L + effectPercent) / 100, int.MinValue, int.MaxValue))).ToArray();
        ActiveBuff? previous;
        previous = state.Buffs.GetValueOrDefault(definition.BuffRow);
        if (previous is not null) await EndBuff(player, definition.BuffRow, previous);
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return;
        ushort instance;
        int token;
        token = ++state.Token;
        state.Buffs.Remove(definition.BuffRow);
        instance = player.NextBuff++;
        shrine.BuffInstance = instance;
        shrine.BuffToken = token;
        state.Buffs[definition.BuffRow] = new ActiveBuff
        {
            Shrine = shrine, Instance = instance, Token = token,
            EndsAt = ctx.Now + shrine.Duration,
        };
        Console.WriteLine($"[gateway] shrine {definition.Id} grants " +
            string.Join(", ", shrine.Stats.Select(s => $"0x{s.Key:x}={s.Value}")));
        await Send(player, [], $"0x01c1 shrine {definition.Id} buff {definition.BuffRow} for {shrine.Duration} s",
            atGate: () =>
            {
                if (player.Closed || player.LeavingArea) return null;
                // A new shrine of the row is a new countdown.
                player.Area.Buffs.Set(player.Id, new(definition.BuffRow, player.Id, U68: Buff.Seconds(shrine.Duration)));
                var bytes = new List<byte>();
                // The buff's stats are in the character's sum now, so pools and the panel must follow.
                RecomputePoolsAndSpeed(player);
                bytes.AddRange(PlayerStatsOffFrames(player));
                return [.. bytes];
            });
        // The repeat count a shrine can grant (Echoing's skill_repeat_count) is part of the skill panel, not of the
        // character stats the client re-reads, so the panel is rebuilt for the shrines that grant one and no others:
        // a full rebuild re-sends every gem's skill and the skill bar, which the client cannot take mid-fight.
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return;
        if (TouchesSkillPanel(shrine)) await SendSkills(player);
        foreach (var effect in shrine.After) await effect.OnBuffApplied(ctx, shrine);
    }

    /// <summary>Right-click cancellation (C→S 0x004b): ends the active shrine buff published under wire instance
    /// <paramref name="buffInstance"/> when its BuffDefinitions row is marked Removable.</summary>
    public static async Task<bool> CancelBuff(Player player, ushort buffInstance)
    {
        if (buffInstance == 0 || player.Closed || player.LeavingArea || player.PlayerLife == 0) return false;
        if (player.Area.Buffs.Find(player.Id, buffInstance) is not { } active
            || active.Source != player.Id || !global::Game.IsRemovableBuff(active.Row))
            return false;
        if (!States.TryGetValue(player, out var state)) return false;
        await state.Area.Gate.WaitAsync();
        try
        {
            if (player.Closed || player.LeavingArea || player.PlayerLife == 0
                || !state.Buffs.TryGetValue(active.Row, out var buff))
                return false;
            await EndBuff(player, active.Row, buff);
            return true;
        }
        finally { state.Area.Gate.Release(); }
    }

    /// <summary>The buff's duration ending: the icon, its stats and its effect go together, and only if this granted
    /// buff is still the connection's buff of that row (a later shrine of the same row replaced it otherwise). Called
    /// from the tick at <see cref="ActiveBuff.EndsAt"/>, so it stops with the area instead of outliving it.</summary>
    static async Task EndBuff(Player player, ushort row, ActiveBuff buff)
    {
        bool removed = false;
        await Send(player, [], $"0x01c2 shrine buff {row} ends", atGate: () =>
        {
            if (player.Closed || player.LeavingArea) return null;
            var state = Of(player);
            if (!state.Buffs.TryGetValue(row, out var current) || current.Instance != buff.Instance || current.Token != buff.Token) return null;
            state.Buffs.Remove(row);
            removed = true;
            player.Area.Buffs.Clear(player.Id, row, player.Id);
            var bytes = new List<byte>();
            RecomputePoolsAndSpeed(player);
            bytes.AddRange(PlayerStatsOffFrames(player));
            return [.. bytes];
        });
        if (!removed || player.Closed || player.LeavingArea) return;
        if (TouchesSkillPanel(buff.Shrine)) await SendSkills(player);
        var ctx = new ShrineContext(player, AreaTime(player));
        foreach (var effect in buff.Shrine.After) await effect.OnExpired(ctx, buff.Shrine);
    }

    public static async Task GrantRandomShrine(Player player, IReadOnlyList<ShrineCatalog.ShrineDefinition> pool,
        int baseDurationSeconds, ShrineContext ctx)
    {
        if (pool.Count == 0 || player.Closed || player.LeavingArea || player.PlayerLife == 0) return;
        var state = Of(player);
        var available = pool.Where(s => !state.Buffs.ContainsKey(s.BuffRow)).ToArray();
        var choices = available.Length > 0 ? available : pool;
        var chosen = choices[AreaRng(player).Next(choices.Count)];
        var here = player.Walk.Position(ctx.Now);
        var personal = NewInstance(chosen, here.X, here.Y, 0, null);
        personal.State = ShrineState.Used;
        personal.ActivatedAt = ctx.Now;
        personal.Duration = Math.Max(1, baseDurationSeconds);
        await Grant(player, personal, ctx, mapGranted: false);
    }

    public static void OnKill(KillContext kill)
    {
        if (!kill.PlayerKill || kill.Player.PlayerLife == 0 || kill.Monster.Rarity is not (MonsterRarity.Rare or MonsterRarity.Unique))
            return;
        var player = kill.Player;
        var playerStats = CharacterPlayerStats(player);
        int centiseconds = playerStats.GetValueOrDefault(Stat.GainShrineBuffOnRareOrUniqueKillCentiseconds);
        if (centiseconds <= 0) return;
        var state = Of(player);
        lock (state.PendingKillShrineDurations)
            state.PendingKillShrineDurations.Add(Math.Max(1, centiseconds / 100));
    }

    // ---- Per-tick, stats and cleanup ------------------------------------------------------------------------------

    /// <summary>One player tick: the unclaimed shrines' neighbourhood steps (monsters coming near and leaving) and the
    /// active effects. Leaving the area drops everything first.</summary>
    public static async Task Tick(Player player)
    {
        if (player.Closed || player.LeavingArea || player.Paused) return;
        await LesserShrines.Tick(player);
        var state = Of(player);
        await state.Area.Gate.WaitAsync();
        try
        {
            if (player.Closed || player.LeavingArea) return;
            double now = AreaTime(player);
            var ctx = new ShrineContext(player, now);
            foreach (var expired in state.Instances.Values.Where(s => s.ExpiresAt <= now).ToArray())
            {
                expired.State = ShrineState.Used;
                foreach (var member in Members(player)) await Spent(member, expired);
            }
            // One driver for the physical area's hazards and auras, even when a party shares it.
            if (ReferenceEquals(Members(player).Where(c => c.Connection.PlayerSent).MinBy(c => c.Id), player))
                foreach (var shrine in state.Instances.Values.Where(s => s.State == ShrineState.Inactive))
                {
                    uint[] entered, left;
                    var inside = player.Area.MonstersNear(shrine.At, NearbyRadius).Where(p => p.Value is { Hostile: true, Dead: false, Life: > 0 }
                        && Dist(p.Value.Walk.Position(now), shrine.At) <= NearbyRadius).Select(p => p.Key).ToHashSet();
                    entered = inside.Except(shrine.Nearby).ToArray();
                    left = shrine.Nearby.Except(inside).ToArray();
                    shrine.Nearby.Clear(); shrine.Nearby.UnionWith(inside);
                    foreach (var effect in shrine.Before)
                    {
                        foreach (uint id in entered) await effect.OnNearbyMonsterEnter(ctx, shrine, id);
                        foreach (uint id in left) await effect.OnNearbyMonsterLeave(ctx, shrine, id);
                        await effect.Tick(ctx, shrine);
                    }
                }
            foreach (var (row, buff) in state.Buffs.ToArray())
                if (player.PlayerLife == 0 || buff.EndsAt <= now) await EndBuff(player, row, buff);
                else foreach (var effect in buff.Shrine.After) await effect.Tick(ctx, buff.Shrine);

            if (player.PlayerLife > 0 && !InTown(player))
            {
                int[] killDurations;
                lock (state.PendingKillShrineDurations)
                {
                    killDurations = [.. state.PendingKillShrineDurations];
                    state.PendingKillShrineDurations.Clear();
                }
                foreach (int seconds in killDurations)
                    await GrantRandomShrine(player, ShrineCatalog.Standard, seconds, ctx);

                var playerStats = CharacterPlayerStats(player);
                int ascendancyMs = playerStats.GetValueOrDefault(Stat.GainShrineBuffEveryXMsFromAscendancy);
                if (ascendancyMs <= 0) state.NextAscendancyShrineAt = 0;
                else if (now >= state.NextAscendancyShrineAt)
                {
                    state.NextAscendancyShrineAt = now + ascendancyMs / 1000.0;
                    await GrantRandomShrine(player, ShrineCatalog.Standard, RandomShrineBaseDurationSeconds, ctx);
                }

                int periodicMs = playerStats.GetValueOrDefault(Stat.GainShrineBuffEveryXMs);
                if (periodicMs <= 0) state.NextPeriodicShrineAt = 0;
                else if (now >= state.NextPeriodicShrineAt)
                {
                    state.NextPeriodicShrineAt = now + periodicMs / 1000.0;
                    await GrantRandomShrine(player, ShrineCatalog.Standard, RandomShrineBaseDurationSeconds, ctx);
                }

                int bloodMs = playerStats.GetValueOrDefault(Stat.GainBloodShrineBuffEveryXMsFromUnique);
                if (bloodMs <= 0) state.NextBloodShrineAt = 0;
                else if (now >= state.NextBloodShrineAt)
                {
                    state.NextBloodShrineAt = now + bloodMs / 1000.0;
                    await GrantRandomShrine(player, BloodShrines, RandomShrineBaseDurationSeconds, ctx);
                }
            }
        }
        finally { state.Area.Gate.Release(); }
    }

    public static async Task LeaveArea(Player player)
    {
        if (!States.TryGetValue(player, out var state)) return;
        await state.Area.Gate.WaitAsync();
        try
        {
            var ctx = new ShrineContext(player, AreaTime(player));
            ActiveBuff[] buffs;
            buffs = [.. state.Buffs.Values]; state.Buffs.Clear();
            foreach (var buff in buffs)
                foreach (var effect in buff.Shrine.After) await effect.OnMapUnload(ctx, buff.Shrine);
            States.Remove(player);
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
            foreach (var (player, _) in States.ToArray()) LeaveArea(player).GetAwaiter().GetResult();
            Areas.Clear();
        }
    }

    public static Player[] NearbyPlayers(Player player, (int X, int Y) at, double radius) => Members(player)
        .Where(c => c.Connection.PlayerSent && c.PlayerLife > 0 && Dist(c.Walk.Position(AreaTime(c)), at) <= radius).ToArray();

    /// <summary>A standing shrine spell uses the same defenses and death accounting as an enemy spell.</summary>
    public static async Task HitPlayers(Player source, (int X, int Y) at, double radius, DamageType type, DamagePacket packet,
        Func<Player, bool>? accept = null)
    {
        foreach (var player in NearbyPlayers(source, at, radius))
        {
            if (accept is not null && !accept(player)) continue;
            int amount = (int)Math.Clamp(packet.Roll(AreaRng(player)).MinTotal, 0, int.MaxValue);
            if (amount <= 0 || player.Immune || InGracePeriod(player) || !TrySyntheticEnemyHit(player, type, amount, block: true, spell: true, suppress: true, out var hit)) continue;
            if (hit.Damage > 0)
            {
                var stats = CharacterPlayerStats(player);
                ApplyPlayerAilmentsLocked(player, DamagePacket.Of(type, amount, amount), DamagePacket.Of(type, hit.Damage, hit.Damage),
                    false, hit.Blocked, AreaTime(player), stats);
            }
            Note("shrine spell hit");
            await SendPlayerEnergyShield(player);
            if (hit.Mana != hit.ManaBefore)
                await SendPlayerMana(player, "shrine spell mana");
            if (player.PlayerLife == 0) await PlayerDied(player, "death from shrine spell");
        }
    }

    /// <summary>The claimed shrines' buff stats in the character's sum (<see cref="GameEvents.PlayerStatSources"/>), and
    /// nothing for a shrine that is unclaimed, spent or gone.</summary>
    public static Dictionary<uint, int> AddStats(Player player, Dictionary<uint, int> stats)
    {
        if (!States.TryGetValue(player, out var state)) return stats;
        foreach (var buff in state.Buffs.Values.Where(b => player.PlayerLife > 0 && b.EndsAt > AreaTime(player)))
            foreach (var (key, value) in buff.Shrine.Stats)
                stats[key] = (int)Math.Clamp((long)stats.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
        return stats;
    }

    /// <summary>The area's monster level: an open map's own level for this area, else the WorldAreas level.</summary>
    public static byte AreaLevel(Player player)
    {
        int level = ServerLevels.AreaLevel(player.Layout.Id);
        if (MapRunOf(player) is { Area.Level: > 0 } openMap)
            level = openMap.Area.Level;
        return (byte)Math.Clamp(level, 1, 100);
    }

}
