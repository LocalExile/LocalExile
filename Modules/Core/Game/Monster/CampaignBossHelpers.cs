using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>
/// Shared helpers for campaign Act 1–5 boss encounter controllers: timed ground effects (using <c>TakeObjectId()</c>),
/// <c>NoRewards</c> add spawning, invulnerability / Transitionable state updates, arena gate locking with per-player
/// diffed blocker/hull sync and safe cleanup, and zero-allocation target lookup. Every state container lives in a
/// <see cref="RuleTable{TKey, TValue}"/> so hot-reload clears everything cleanly with zero static player references.
/// </summary>
internal static class CampaignBossHelpers
{
    public const string InvulnerableStatKey = "campaign_boss_invulnerable";
    public static readonly IReadOnlyDictionary<uint, int> CannotBeDamagedStats =
        new Dictionary<uint, int> { [Stat.BaseCannotBeDamaged] = 1 };

    sealed class DeathStamp { public int Deaths; public int Epoch = -1; }

    static readonly RuleTable<Player, Dictionary<uint, bool>> SyncedGateStates = new();
    static readonly RuleTable<InstanceState, QuestObjectTypes.AreaPlacements> CachedAreaPlacements = new();
    static readonly RuleTable<Player, DeathStamp> HandledPlayerDeaths = new();

    public const double BossHealOnDeathRadius = 160;

    public sealed record TimedEffect(uint Id, (int X, int Y) At, double Ends, string Model, float Scale = 1f);

    public sealed record ArenaGate(uint Id, string Type, string? Model, int X, int Y, float Rotation, float? Z,
        int TransitionableIndex, int Size, bool HasBlockage, byte LockedState, byte UnlockedState)
    {
        public bool Locked;
    }

    public sealed record ArenaMarker(uint Id, string Key, string Type, int X, int Y, float Rotation, float? Z);

    /// <summary>Registers serializable arena marker placements (e.g. <c>TeleportMarkerSerialized1..4</c>,
    /// <c>AbberathJumpOut</c>) into <see cref="InstanceWorld.SceneObjects"/> under <paramref name="keyPrefix"/> so
    /// native entity-targeted client actions (<c>ActionType 724 ArakaaliHide</c>, <c>ActionType 704 AbberathHide</c>)
    /// receive real <c>Positioned</c> target entities. Idempotent across re-entry and hot-reload.</summary>
    public static List<ArenaMarker> RegisterSceneMarkers(InstanceState instance, string keyPrefix,
        IEnumerable<GlyphQuest.Placement> placements)
    {
        List<ArenaMarker> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var p in placements)
        {
            string key = $"{keyPrefix}{p.Type}:{p.X},{p.Y}";
            if (!seen.Add(key)) continue;
            if (instance.World.SceneObjects.TryGetValue(key, out var existing))
            {
                result.Add(new(existing.Id, key, existing.Type, existing.Object.GridX, existing.Object.GridY,
                    existing.Object.Rotation, existing.Object.Height));
                continue;
            }
            if (QuestObjectTypes.Find(p.Type) is not { } info || !QuestObject.CanSerialize(info.Components))
                continue;
            string? model = info.OwnModel ? null : (string.IsNullOrEmpty(p.Model) ? info.Model : p.Model);
            uint id = instance.World.TakeObjectId();
            var obj = new QuestObject(p.X, p.Y, info.Components, p.Rotation, p.Z, model);
            instance.World.SceneObjects[key] = (id, p.Type, obj);
            result.Add(new(id, key, p.Type, p.X, p.Y, p.Rotation, p.Z));
        }
        return result;
    }

    /// <summary>Sends <c>CreateObject</c> (with wake + birth await) for any marker not yet present in the area's
    /// <c>Scenes</c> set, ensuring every observer has the marker entity before an action targets its object id.</summary>
    public static async Task<bool> BindSceneMarkers(Player player, IEnumerable<ArenaMarker> markers, string? log = null)
    {
        bool allBound = true;
        foreach (var marker in markers)
        {
            if (Scenes(player).Contains(marker.Id)) continue;
            if (!player.Instance.World.SceneObjects.TryGetValue(marker.Key, out var entry))
            {
                allBound = false;
                continue;
            }
            if (await SendWorldCreate(player, entry.Id,
                () => InstanceEntities.CreateObject(entry.Id, InstanceEntities.ObjectTypeHash(entry.Type), entry.Object.Blob()),
                log, wake: true))
                Scenes(player).Add(entry.Id);
            else
                allBound = false;
        }
        return allBound;
    }

    public static bool Alive(SpawnedMonster? m) => m is { Dead: false, Life: > 0 };

    public static double Distance((int X, int Y) a, (int X, int Y) b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static Player[] ActivePlayers(Player runner) =>
        [.. runner.Frames.Players().Where(p => !p.Closed && !p.LeavingArea && p.Connection.PlayerSent
            && ReferenceEquals(p.Instance, runner.Instance))];

    public static Player? NearestLivingTarget(Player runner, (int X, int Y) origin, double now, double maxRange = 140, uint? awakeId = null)
    {
        Player? best = null;
        double bestDist = maxRange;
        foreach (var p in runner.Frames.Players())
        {
            if (p.Closed || p.LeavingArea || !p.Connection.PlayerSent || !ReferenceEquals(p.Instance, runner.Instance))
                continue;
            if (p.PlayerLife == 0 || p.Paused || InGracePeriod(p))
                continue;
            if (awakeId is uint mid && (!MonsterShown(p, mid) || MonsterAsleep(p, mid)))
                continue;
            double d = Distance(origin, p.Walk.Position(now));
            if (d <= bestDist)
            {
                bestDist = d;
                best = p;
            }
        }
        return best;
    }

    public static (int X, int Y) WalkableNear(Player runner, (int X, int Y) at) =>
        runner.Layout.Walkable is { } w && !w(at.X, at.Y) ? runner.Layout.Near(at) : at;

    public static (int X, int Y) RingSpot(Player runner, (int X, int Y) center, double angle, double radius)
    {
        int x = (int)Math.Round(center.X + Math.Cos(angle) * radius);
        int y = (int)Math.Round(center.Y + Math.Sin(angle) * radius);
        return WalkableNear(runner, (x, y));
    }

    public static QuestObjectTypes.AreaPlacements LoadAreaPlacements(string area, uint seed) =>
        new(area, seed, []);

    /// <summary>Cached per-instance <see cref="QuestObjectTypes.AreaPlacements"/> (loaded once per instance, never per tick).</summary>
    public static QuestObjectTypes.AreaPlacements AreaPlacements(Player runner) =>
        CachedAreaPlacements.GetValue(runner.Instance, _ => new(runner.Layout.Id, runner.Seed, []));

    public static string StartedFlag(string phaseKey) => Quests.InstanceFlag($"{phaseKey}-started");
    public static string PhaseFlag(string phaseKey, int index) => Quests.InstanceFlag($"{phaseKey}-phase-{index}");
    public static bool IsStarted(InstanceState instance, string phaseKey) => instance.HasState(StartedFlag(phaseKey));
    public static void MarkStarted(InstanceState instance, string phaseKey) => instance.SetState(StartedFlag(phaseKey));
    public static void MarkPhaseCompleted(InstanceState instance, string phaseKey, int completedIndex) =>
        instance.SetState(PhaseFlag(phaseKey, completedIndex));

    /// <summary>Synchronizes a boss's <see cref="LifePhaseGate"/> and <see cref="SpawnedMonster.EntranceGrace"/>
    /// against persisted <see cref="InstanceState"/> flags (and restored <see cref="SpawnedMonster.Life"/>) so
    /// <c>SavedMonster</c> portal re-entry never replays completed phases or traps the boss in entrance grace.</summary>
    public static int SyncSavedPhases(InstanceState instance, SpawnedMonster boss, string phaseKey, int[] thresholds)
    {
        boss.LifePhases ??= new(thresholds);
        int completed = 0;
        for (int i = 0; i < thresholds.Length; i++)
            if (instance.HasState(PhaseFlag(phaseKey, i)))
                completed = i + 1;
        if (completed == 0 && boss.MaxLife > 0 && boss.Life > 0 && boss.Life < boss.MaxLife)
        {
            double pct = 100.0 * boss.Life / boss.MaxLife;
            for (int i = 0; i < thresholds.Length; i++)
                if (pct < thresholds[i] - 1.0)
                    completed = i + 1;
        }
        if (completed > boss.LifePhases.Completed)
            AdvancePhases(boss.LifePhases, completed);
        if (instance.HasState(StartedFlag(phaseKey)) || boss.LifePhases.Completed > 0
            || (boss.MaxLife > 0 && boss.Life > 0 && boss.Life < boss.MaxLife))
        {
            boss.EntranceGrace = false;
            boss.Aggro = true;
        }
        return boss.LifePhases.Completed;
    }

    /// <summary>Idempotent hook called on player death (and checked when <c>PlayerLife == 0</c>) so nearby living
    /// bosses carrying <c>BossHealOnPlayerDeath</c> (+10%) or <c>BossHealOnPlayerDeathMinor</c> (+5%) recover life.</summary>
    public static async Task OnPlayerDiedInBossArea(Player player)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife != 0) return;
        var stamp = HandledPlayerDeaths.GetValue(player, _ => new());
        if (stamp.Deaths == player.PlayerDeaths && stamp.Epoch == player.DeathNotificationEpoch) return;
        stamp.Deaths = player.PlayerDeaths;
        stamp.Epoch = player.DeathNotificationEpoch;
        await ApplyBossHealOnPlayerDeath(player);
    }

    /// <summary>Heals nearby alive hostile bosses carrying <see cref="BossMods.HealOnPlayerDeath"/> (+10% max life) or
    /// <see cref="BossMods.HealOnPlayerDeathMinor"/> (+5% max life) and broadcasts their updated life.</summary>
    public static async Task ApplyBossHealOnPlayerDeath(Player player)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea) return;
        double now = AreaTime(player);
        var playerAt = player.Walk.Position(now);
        List<(uint Id, uint Life)> healed = [];
        foreach (var (id, m) in player.Area.Monsters.ToArray())
        {
            if (!m.Hostile || m.Dead || m.Life == 0 || m.MaxLife == 0 || m.Life >= m.MaxLife || m.WorldHidden) continue;
            double fraction = BossMods.PlayerDeathHealFraction(m.Variety);
            if (fraction <= 0) continue;
            if (Distance(m.Walk.Position(now), playerAt) > BossHealOnDeathRadius) continue;
            uint gain = (uint)Math.Max(1, Math.Round(m.MaxLife * fraction));
            uint nextLife = (uint)Math.Min((ulong)m.MaxLife, (ulong)m.Life + gain);
            if (nextLife <= m.Life) continue;
            m.Life = nextLife;
            healed.Add((id, nextLife));
        }
        foreach (var (id, life) in healed)
            await SendWorldRecord(player, id, InstanceProtocol.LifeUpdate(id, life),
                $"[BossHealOnPlayerDeath] boss 0x{id:x} heals to {life}");
    }

    public static async Task<uint> SpawnTimedEffect(Player runner, List<TimedEffect> list, (int X, int Y) at,
        string aoPath, double ends, float scale = 1f, float rotation = 0f, bool fixedRotation = true)
    {
        uint id = runner.TakeObjectId();
        var obj = new EffectObject(at.X, at.Y, aoPath)
        {
            Scale = scale,
            Rotation = rotation,
            Flags0 = fixedRotation ? EffectObject.FixedRotation : (byte)0,
        };
        var blob = obj.Blob();
        list.Add(new(id, at, ends, aoPath, scale));
        await SendWorldCreate(runner, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), blob),
            null, wake: true, awaitBirth: false);
        return id;
    }

    public static async Task ExpireEffects(Player runner, List<TimedEffect> list, double now)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].Ends > now) continue;
            uint id = list[i].Id;
            list.RemoveAt(i);
            await SendWorldRemoval(runner, id, null);
        }
    }

    public static async Task ClearAllEffects(Player runner, List<TimedEffect> list)
    {
        var copy = list.ToArray();
        list.Clear();
        foreach (var fx in copy)
            await SendWorldRemoval(runner, fx.Id, null);
    }

    public static async Task<uint> SpawnAdd(Player runner, string varietyId, byte level, (int X, int Y) at,
        uint summonerId, HashSet<uint>? track = null, bool friendly = false, bool entranceGrace = false,
        MonsterRarity rarity = MonsterRarity.Normal, Func<bool>? ownedCurrent = null)
    {
        if (Monsters.Find(varietyId) is not { Supported: true } v || !v.Id.Equals(varietyId, StringComparison.OrdinalIgnoreCase))
            return 0;
        var spot = WalkableNear(runner, at);
        uint id = await SpawnMonster(runner, v, level, rarity, spot.X, spot.Y,
            friendly: friendly,
            instanceOwned: true,
            owned: (mid, m) =>
            {
                m.NoRewards = true;
                m.SummonerId = summonerId;
                m.Aggro = !friendly;
                m.EntranceGrace = entranceGrace;
                track?.Add(mid);
            },
            ownedCurrent: ownedCurrent);
        return id;
    }

    public static async Task RetireMonsters(Player runner, IEnumerable<uint> ids)
    {
        List<uint>? removed = null;
        using (ProjectileMutation(runner))
        {
            foreach (uint id in ids.ToArray())
            {
                if (!runner.Area.Monsters.TryGetValue(id, out var m)) continue;
                m.NoRewards = true;
                m.Dead = true;
                m.Life = 0;
                m.Aggro = false;
                m.ActionGeneration++;
                ClearMonsterAuraCarrier(runner.Area, m);
                runner.Area.Monsters.Remove(id);
                runner.Instance.World.MonsterOrigins.TryRemove(id, out _);
                (removed ??= []).Add(id);
            }
        }
        if (removed is null) return;
        foreach (uint id in removed)
            await SendWorldRemoval(runner, id, null);
    }

    public static void SetInvulnerable(Player runner, uint id, SpawnedMonster m, bool invulnerable, ushort? buffRow = null)
    {
        if (invulnerable)
        {
            m.SetTemporaryStats(InvulnerableStatKey, CannotBeDamagedStats);
            m.RebuildStats();
            if (buffRow is ushort b) runner.Area.Buffs.Set(id, new(b, id));
        }
        else
        {
            m.RemoveTemporaryStats(InvulnerableStatKey);
            m.RebuildStats();
            if (buffRow is ushort b) runner.Area.Buffs.Clear(id, b, id);
        }
        m.PresentationChanged?.Invoke();
    }

    public static void ClearInvulnerableOnUnload(GameArea area, uint id, SpawnedMonster m, ushort? buffRow = null)
    {
        m.RemoveTemporaryStats(InvulnerableStatKey);
        m.RebuildStats();
        if (buffRow is ushort b) area.Buffs.Clear(id, b, id);
        m.PresentationChanged?.Invoke();
    }

    public static async Task SetMonsterTransition(Player runner, uint id, SpawnedMonster m, byte state, string? log = null)
    {
        if (m.Variety is null) return;
        int idx = m.Variety.ComponentsOrBase.ToList().IndexOf("Transitionable");
        if (idx < 0) return;
        if (runner.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
            runner.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { TransitionState = state } };
        await SendWorldRecord(runner, id, InstanceEntities.TransitionableUpdate(id, (byte)idx, state), log);
    }

    public static MonsterAttack PhysicalAttack(SpawnedMonster source, double scale, double radius = 14)
    {
        var (min, max) = Monsters.BaseDamage(source.Variety!, source.Level) ?? (8, 12);
        return new(0x4000, 1.0, radius, DamagePacket.Of(DamageType.Physical, min * scale, max * scale))
        { Spell = false, Scripted = true };
    }

    public static MonsterAttack ElementalSpellAttack(SpawnedMonster source, DamageType element, double scale, double radius = 16)
    {
        var (min, max) = Monsters.BaseDamage(source.Variety!, source.Level) ?? (8, 12);
        return new(0x4000, 1.0, radius, DamagePacket.Of(element, min * scale, max * scale))
        { Spell = true, Scripted = true };
    }

    public static void AdvancePhases(LifePhaseGate phases, int completedCount)
    {
        while (phases.Completed < completedCount)
        {
            if (!phases.Begin(0, 1)) break;
            phases.Finish();
        }
    }

    public static async Task<ArenaGate?> CreateGate(Player runner, string objectType, GlyphQuest.Placement at,
        bool startLocked, byte lockedState, byte unlockedState, string? fallbackModel = null)
    {
        if (QuestObjectTypes.Find(objectType) is not { } info || !QuestObject.CanSerialize(info.Components))
            return null;
        int trIdx = QuestObject.TransitionableIndex(info.Components);
        if (trIdx < 0) return null;
        bool hasBlockage = info.Components.Contains("TriggerableBlockage");
        int size = Math.Max(2, info.Size);
        string? model = info.OwnModel ? null : (string.IsNullOrEmpty(at.Model) ? info.Model ?? fallbackModel : at.Model);
        uint id = runner.TakeObjectId();
        var gate = new ArenaGate(id, objectType, model, at.X, at.Y, at.Rotation, at.Z, trIdx, size, hasBlockage, lockedState, unlockedState)
        {
            Locked = startLocked,
        };
        byte state = startLocked ? lockedState : unlockedState;
        // Always pass blockage: true so Positioned uses dynamic + blocksNoWalker and never stamps a permanent static Hull.
        var blob = LabyrinthTrials.Blob(info.Components, model ?? "", at.X, at.Y, at.Rotation, 1f, state, at.Z,
            blockage: true, blocked: startLocked);
        await SendWorldCreate(runner, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(objectType), blob),
            null, wake: true, awaitBirth: false);
        foreach (var p in ActivePlayers(runner))
            SyncGateForPlayer(p, gate);
        return gate;
    }

    public static async Task SetGateLocked(Player runner, ArenaGate gate, bool locked)
    {
        if (gate.Locked == locked) return;
        gate.Locked = locked;
        byte state = locked ? gate.LockedState : gate.UnlockedState;
        await SendWorldRecord(runner, gate.Id, InstanceEntities.TransitionableUpdate(gate.Id, (byte)gate.TransitionableIndex, state), null);
        if (gate.HasBlockage)
            await SendWorldRecord(runner, gate.Id, InstanceEntities.BlockageUpdate(gate.Id, locked), null);
        foreach (var p in ActivePlayers(runner))
            SyncGateForPlayer(p, gate);
    }

    static HashSet<(int X, int Y)> GateBarrierCells(Player player, ArenaGate gate)
    {
        if (!string.IsNullOrEmpty(gate.Model))
        {
            var hull = ServerLevels.ModelWalkCells(gate.Model, gate.X, gate.Y, gate.Rotation);
            if (hull.Count > 0) return hull;
        }
        if (player.Layout.Walkable is { } walk)
        {
            var (halfX, halfY) = LabyrinthTrials.DoorHull(gate.Model ?? "");
            var cells = LabyrinthTrials.HullCells(gate.X, gate.Y, gate.Rotation, halfX, halfY)
                .Where(c => walk(c.X, c.Y)).ToHashSet();
            if (cells.Count > 0) return cells;
        }
        return [];
    }

    public static void SyncGateForPlayer(Player player, ArenaGate gate)
    {
        var map = SyncedGateStates.GetValue(player, _ => []);
        if (map.TryGetValue(gate.Id, out bool applied) && applied == gate.Locked)
            return;
        map[gate.Id] = gate.Locked;
        ApplyGateBlocking(player, gate, gate.Locked);
    }

    public static void SyncGatesForPlayer(Player player, IEnumerable<ArenaGate> gates)
    {
        foreach (var gate in gates)
            SyncGateForPlayer(player, gate);
    }

    public static void ReleaseGatesForPlayer(Player player, IEnumerable<ArenaGate> gates)
    {
        SyncedGateStates.TryGetValue(player, out var map);
        foreach (var gate in gates)
        {
            map?.Remove(gate.Id);
            ApplyGateBlocking(player, gate, locked: false);
        }
    }

    public static void ReleaseAllGatesOnUnload()
    {
        foreach (var pair in SyncedGateStates)
        {
            var player = pair.Key;
            bool dirty = false;
            foreach (var gateId in pair.Value.Keys.ToArray())
            {
                player.RemoveBlocker(gateId);
                dirty |= player.DoorBarriers.Remove(gateId);
            }
            pair.Value.Clear();
            if (dirty) player.UpdateBarrier();
        }
    }

    static void ApplyGateBlocking(Player player, ArenaGate gate, bool locked)
    {
        if (!locked)
        {
            player.RemoveBlocker(gate.Id);
            if (player.DoorBarriers.Remove(gate.Id)) player.UpdateBarrier();
            return;
        }
        var barrier = GateBarrierCells(player, gate);
        if (barrier.Count > 0)
        {
            // CreateGate marks gates with blocksNoWalker = true, so client collision uses the TriggerableBlockage hull
            // cells alone rather than a Positioned square blocker.
            player.RemoveBlocker(gate.Id);
            player.DoorBarriers[gate.Id] = barrier;
            player.UpdateBarrier();
        }
        else
        {
            player.SetBlocker(gate.Id, gate.X, gate.Y, gate.Size);
        }
    }
}
