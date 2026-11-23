using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>
/// Shared helpers for campaign Act 1–5 boss encounter controllers: timed ground effects (using <c>TakeObjectId()</c>),
/// <c>NoRewards</c> add spawning, invulnerability / Transitionable state updates, arena gate locking with safe cleanup,
/// and pause/multiplayer-aware player lookup. Every state container belongs to the caller's <see cref="RuleTable{TKey, TValue}"/>
/// so hot-reload clears everything cleanly with zero static player references.
/// </summary>
internal static class CampaignBossHelpers
{
    public const string InvulnerableStatKey = "campaign_boss_invulnerable";
    public static readonly IReadOnlyDictionary<uint, int> CannotBeDamagedStats =
        new Dictionary<uint, int> { [Stat.BaseCannotBeDamaged] = 1 };

    public sealed record TimedEffect(uint Id, (int X, int Y) At, double Ends, string Model, float Scale = 1f);

    public sealed record ArenaGate(uint Id, string Type, string? Model, int X, int Y, float Rotation, float? Z,
        int TransitionableIndex, int Size, bool HasBlockage)
    {
        public bool Locked;
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

    public static Player? NearestLivingTarget(Player runner, (int X, int Y) origin, double now, double maxRange = 140, uint? awakeId = null) =>
        ActivePlayers(runner)
            .Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)
                && (awakeId is null || (MonsterShown(p, awakeId.Value) && !MonsterAsleep(p, awakeId.Value))))
            .Select(p => (Player: p, Dist: Distance(origin, p.Walk.Position(now))))
            .Where(x => x.Dist <= maxRange)
            .OrderBy(x => x.Dist)
            .Select(x => x.Player)
            .FirstOrDefault();

    public static (int X, int Y) WalkableNear(Player runner, (int X, int Y) at) =>
        runner.Layout.Walkable is { } w && !w(at.X, at.Y) ? runner.Layout.Near(at) : at;

    public static (int X, int Y) RingSpot(Player runner, (int X, int Y) center, double angle, double radius)
    {
        int x = (int)Math.Round(center.X + Math.Cos(angle) * radius);
        int y = (int)Math.Round(center.Y + Math.Sin(angle) * radius);
        return WalkableNear(runner, (x, y));
    }

    public static List<GlyphQuest.Placement> PlacementsOf(string area, uint seed, string objectType) =>
        new QuestObjectTypes.AreaPlacements(area, seed, []).Of(objectType);

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
            await SendWorldRemoval(runner, id, null);
        }
    }

    public static void SetInvulnerable(Player runner, uint id, SpawnedMonster m, bool invulnerable, ushort? buffRow = null)
    {
        if (invulnerable)
        {
            m.SetTemporaryStats(InvulnerableStatKey, CannotBeDamagedStats);
            if (buffRow is ushort b) runner.Area.Buffs.Set(id, new(b, id));
        }
        else
        {
            m.RemoveTemporaryStats(InvulnerableStatKey);
            if (buffRow is ushort b) runner.Area.Buffs.Clear(id, b, id);
        }
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

    public static MonsterAttack SpellAttack(Player runner, SpawnedMonster source, DamagePacket damage, double radius = 16) =>
        new(0x4000, 1.0, radius, damage) { Spell = true, Scripted = true };

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

    public static async Task<ArenaGate?> CreateGate(Player runner, string objectType, GlyphQuest.Placement at, bool startLocked, string? fallbackModel = null)
    {
        var info = QuestObjectTypes.Find(objectType);
        IReadOnlyList<string> components = info?.Components ?? ["Positioned", "BaseEvents", "Animated", "Transitionable", "TriggerableBlockage"];
        if (!QuestObject.CanSerialize(components)) return null;
        int trIdx = QuestObject.TransitionableIndex(components);
        if (trIdx < 0) return null;
        bool hasBlockage = components.Contains("TriggerableBlockage");
        int size = info?.Size ?? 4;
        string? model = info?.OwnModel == true ? null : at.Model ?? info?.Model ?? fallbackModel;
        uint id = runner.TakeObjectId();
        var gate = new ArenaGate(id, objectType, model, at.X, at.Y, at.Rotation, at.Z, trIdx, size, hasBlockage)
        {
            Locked = startLocked,
        };
        byte state = (byte)(startLocked ? 1 : 2);
        var blob = LabyrinthTrials.Blob(components, model ?? "", at.X, at.Y, at.Rotation, 1f, state, at.Z,
            blockage: hasBlockage, blocked: startLocked);
        await SendWorldCreate(runner, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(objectType), blob),
            null, wake: true, awaitBirth: false);
        ApplyGateBlocking(runner, gate, startLocked);
        return gate;
    }

    public static async Task SetGateLocked(Player runner, ArenaGate gate, bool locked)
    {
        if (gate.Locked == locked) return;
        gate.Locked = locked;
        byte state = (byte)(locked ? 1 : 2);
        await SendWorldRecord(runner, gate.Id, InstanceEntities.TransitionableUpdate(gate.Id, (byte)gate.TransitionableIndex, state), null);
        if (gate.HasBlockage)
            await SendWorldRecord(runner, gate.Id, InstanceEntities.BlockageUpdate(gate.Id, locked), null);
        foreach (var p in ActivePlayers(runner))
            ApplyGateBlocking(p, gate, locked);
    }

    public static void ApplyGateBlocking(Player player, ArenaGate gate, bool locked)
    {
        if (!locked)
        {
            player.RemoveBlocker(gate.Id);
            if (player.DoorBarriers.Remove(gate.Id)) player.UpdateBarrier();
            return;
        }
        player.SetBlocker(gate.Id, gate.X, gate.Y, gate.Size);
    }

    public static void SyncGatesForPlayer(Player player, IEnumerable<ArenaGate> gates)
    {
        foreach (var gate in gates)
            ApplyGateBlocking(player, gate, gate.Locked);
    }

    public static void ReleaseGatesForPlayer(Player player, IEnumerable<ArenaGate> gates)
    {
        foreach (var gate in gates)
            ApplyGateBlocking(player, gate, locked: false);
    }
}
