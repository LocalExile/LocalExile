using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string CallOfSteelEffect = "CallOfSteel";
    const string SteelShrapnelBallistaEffect = "ShrapnelBallistaAltX";
    const ushort SteelShardBuff = 1610, SteelWardBuff = 1635;
    const ushort SteelwormBuff = 1820;
    const int BaseSteelShardLimit = 12, SteelWardLimit = 6, SteelwormShardThreshold = 8;
    const double SteelwormBuffSeconds = 4;
    // The native skill gives separate collection and reflection areas. Their radii are not in the exported stats.
    const int SteelCollectionRadius = 100, SteelReflectionRadius = 20;
    static readonly RuleTable<Player, SteelState> SteelStates = new();

    sealed class SteelState
    {
        public int Shards, Refill;
        public SteelwormState? Steelworm;
        public readonly List<(long Expires, int Block)> Wards = [];
    }

    sealed class SteelwormState(Item source)
    {
        public Item Source { get; } = source;
        public int Spent;
        public long Expires;
    }

    static bool ScheduleCallOfSteel(Player player, ushort skillId, int recoveryGeneration, Func<bool> valid)
    {
        using var mutation = ProjectileMutation(player);
        if (!valid() || player.PlayerLife == 0) return false;
        var source = SupportedSkill(player, skillId);
        var stats = CharacterPlayerStats(player);
        int amount = source.Stats.GetValueOrDefault(Stat.CallOfSteelReloadAmount);
        int interval = source.Stats.GetValueOrDefault(Stat.CallOfSteelReloadTime);
        if (amount <= 0 || interval <= 0) return false;
        int useSpeed = stats.GetValueOrDefault(Stat.SteelStealCastSpeedPlusPct);
        double windup = Math.Max(TickSeconds, SkillsFor(player).CastTimeMs(source.Effect) / 1000d
            / Math.Max(.01, 1 + useSpeed / 100d) / PlayerActionFactor(player));
        var session = ProjectilesFor(player);
        long release = Math.Max(session.Runtime.Now, ProjectileRuntime.Time(AreaTime(player))) + ProjectileRuntime.Time(windup);
        session.Runtime.Schedule(release, () =>
        {
            if (!session.Active || !valid() || player.PlayerLife == 0 || player.ActionInterruptionGeneration != recoveryGeneration
                || !SameTriggerSource(source, SupportedSkill(player, skillId))) return;
            var state = SteelStates.GetOrCreateValue(player);
            var current = CharacterPlayerStats(player);
            int limit = (int)Math.Clamp((long)BaseSteelShardLimit + current.GetValueOrDefault(Stat.MaxSteelAmmo), 0, ushort.MaxValue);
            int removed = DetonateCalledImpales(session, source, current);
            SetSteelShards(player, state, (int)Math.Min(limit, (long)state.Shards + amount + removed));
            int refill = ++state.Refill;
            long spacing = ProjectileRuntime.Time(interval / 1000d);
            void Reload()
            {
                if (!session.Active || player.PlayerLife == 0 || state.Refill != refill || state.Shards >= limit) return;
                SetSteelShards(player, state, (int)Math.Min(limit, (long)state.Shards + amount));
                if (state.Shards < limit) session.Runtime.Schedule(session.Runtime.Now + spacing, Reload);
            }
            if (state.Shards < limit) session.Runtime.Schedule(session.Runtime.Now + spacing, Reload);
        });
        return true;
    }

    static int DetonateCalledImpales(ProjectileSession session, SupportedGem source, IReadOnlyDictionary<uint, int> stats)
    {
        var player = session.Player;
        long at = session.Runtime.Now;
        double now = at / (double)ProjectileRuntime.QuantaPerSecond;
        var center = player.Walk.Position(now);
        int increase = stats.GetValueOrDefault(Stat.SteelStealAreaOfEffectPlusPct);
        var area = SkillAreaStats(player, source.Stats, source.Effect, stats);
        int AreaStat(uint key) => area.GetValueOrDefault(key);
        int BlastStat(uint key) => key == SkillArea.Increase
            ? ActorScale.Saturate((long)AreaStat(key) + increase) : AreaStat(key);
        int collection = SkillArea.Radius(SteelCollectionRadius, AreaStat, spell: false);
        int blast = SkillArea.Radius(SteelReflectionRadius, BlastStat, spell: false);

        var enemies = player.Area.Monsters.Values.Where(m => m.Hostile && m.OwnerId is null && !m.WorldHidden)
            .Select(m => (Monster: m, Position: m.Walk.Position(now)))
            .Where(m => Dist(m.Position, center) <= collection + blast).ToArray();
        double more = Math.Max(0, 1 + stats.GetValueOrDefault(Stat.SteelStealReflectDamagePlusPct) / 100d);
        int removed = 0;
        // Consume all sources first, so a death or a stage change caused by one blast cannot delete the other sources.
        List<((int X, int Y) Center, double Damage)> blasts = [];
        foreach (var (monster, position) in enemies)
        {
            if (monster.Impale.Stacks == 0 || Dist(position, center) > collection) continue;
            var (count, reflected) = monster.Impale.Consume(now);
            removed += count;
            if (reflected > 0) blasts.Add((position, reflected * more));
        }
        foreach (var (origin, reflected) in blasts)
            foreach (var (enemy, position) in enemies)
            {
                if (enemy.Dead || enemy.Life == 0 || enemy.Invulnerable || Dist(position, origin) > blast) continue;
                int damage = MonsterImpaleDamage(enemy, reflected, now);
                if (damage <= 0 || ApplyMonsterHitLocked(player, enemy, damage, playerStats: stats, allyAt: origin) is not { } hit)
                    continue;
                uint id = enemy.ObjectId;
                session.World(id, InstanceProtocol.LifeUpdate(id, hit.Life), "Call of Steel reflected impale damage");
                if (hit.StageStarts) CommitProjectileBossStage(session, id, enemy, at);
                if (hit.Life == 0) CommitProjectileKill(session, id, enemy, at, AreaRng(player));
            }
        return removed;
    }

    static void SetSteelShards(Player player, SteelState state, int count)
    {
        state.Shards = count;
        if (count == 0) player.Area.Buffs.Clear(player.Id, SteelShardBuff, player.Id);
        else player.Area.Buffs.Set(player.Id, new(SteelShardBuff, player.Id, Stacks: (ushort)count));
    }

    // Commit once per launched attack, including each Steel Ballista volley. The charged attack has
    // already read its projectile stats; a newly earned bonus is available to subsequent attacks and spells.
    static int SpendProjectileSteelShards(Player player, ProjectileSession session,
        int skillCost, int additionalCost, int steelwormProjectiles)
    {
        long cost = Math.Max(0, skillCost) + (long)Math.Max(0, additionalCost);
        if (cost == 0 || !SteelStates.TryGetValue(player, out var state) || state.Shards <= 0) return 0;
        int spent = (int)Math.Min(cost, state.Shards);
        state.Refill++;
        SetSteelShards(player, state, state.Shards - spent);
        if (steelwormProjectiles > 0 && ActiveOffHand(player)?.Item is { } quiver)
        {
            var worm = state.Steelworm;
            if (worm is null || !ReferenceEquals(worm.Source, quiver))
            {
                ClearSteelwormLocked(player, state);
                state.Steelworm = worm = new(quiver);
            }
            worm.Spent += spent;
            if (worm.Spent >= SteelwormShardThreshold)
            {
                worm.Spent %= SteelwormShardThreshold;
                GrantSteelwormProjectiles(player, session, state, worm, steelwormProjectiles);
            }
        }
        return Math.Min(Math.Max(0, skillCost), spent);
    }

    static void GrantSteelwormProjectiles(Player player, ProjectileSession session, SteelState state,
        SteelwormState worm, int count)
    {
        long expires = session.Runtime.Now + ProjectileRuntime.Time(SteelwormBuffSeconds);
        worm.Expires = expires;
        var area = player.Area;
        var entity = new EntityId(player.Id);
        var stats = new Dictionary<uint, int> { [Stat.NumberOfAdditionalProjectiles] = count };
        area.SetStatBuff(entity, worm, stats);
        area.Buffs.Set(player.Id, BuffRows.Spec(SteelwormBuff, player.Id, stats, SteelwormBuffSeconds));
        void Expire()
        {
            if (!ReferenceEquals(state.Steelworm, worm) || worm.Expires != expires) return;
            worm.Expires = 0;
            area.RemoveStatBuff(entity, worm);
            area.Buffs.Clear(player.Id, SteelwormBuff, player.Id);
        }
        if (player.Frames.Running)
        {
            long frames = (long)Math.Ceiling(SteelwormBuffSeconds * 1000 / Program.FrameMs);
            InArea(player, owner => owner.Timers.After(owner.Frame, frames, _ =>
            {
                if (CurrentGameWork && !player.Closed && ReferenceEquals(player.Area, area)) Expire();
            }, owner: entity, source: CombatLifetime));
        }
        else session.Runtime.Schedule(expires, () => { if (session.Active) Expire(); });
    }

    static void ReconcileSteelwormSourceLocked(Player player)
    {
        if (SteelStates.TryGetValue(player, out var state) && state.Steelworm is { } worm
            && !ReferenceEquals(ActiveOffHand(player)?.Item, worm.Source))
            ClearSteelwormLocked(player, state);
    }

    static void ClearSteelwormLocked(Player player, SteelState state)
    {
        if (state.Steelworm is not { } worm) return;
        state.Steelworm = null;
        if (worm.Expires == 0) return;
        player.Area.RemoveStatBuff(new EntityId(player.Id), worm);
        player.Area.Buffs.Clear(player.Id, SteelwormBuff, player.Id);
    }

    internal static void ClearSteelworm(Player player)
    {
        if (!SteelStates.TryGetValue(player, out var state)) return;
        using var mutation = ProjectileMutation(player, advance: false);
        ClearSteelwormLocked(player, state);
    }

    internal static void ClearAllSteelworms()
    {
        foreach (var (player, _) in SteelStates) ClearSteelworm(player);
    }

    static ProjectileUse PrepareSteelRelease(ProjectileReleaseContext context, ProjectileUse use, string effect)
    {
        var shot = (ProjectileOffense)use.Payload!;
        int spent = context.SteelShardsSpent;
        if (effect == "ShatteringSteelAltX" && spent > 0 && spent == context.Own(Stat.SteelAmmoConsumedPerUse)
            && SteelStates.TryGetValue(context.Player, out var state))
            AddSteelWard(context, state);
        var profile = use.Profile;
        if (spent == 0 && context.Own(Stat.Fires1ProjectileIfNoSteelAmmo) != 0)
            profile = profile with { Count = 1, ParallelOrigins = null };
        if (spent == 0 && context.Own(Stat.AlreadySplitIfNoSteelShards) != 0
            && profile.SplitForm is { Metadata: { } shardMetadata } shard)
            profile = profile with
            {
                Id = shardMetadata, Speed = profile.Speed * shard.SpeedMultiplier,
                ContactCells = shard.ContactCells ?? profile.ContactCells,
                CannotSplit = true, Split = 0, EndsAtAim = false, EndArea = shard.EndArea, SplitForm = null,
            };
        if (effect is "LancingSteel" or "LancingSteelAltX"
            && context.Own(Stat.LancingSteelDamagePlusPctFinalAfterFirstHitOnTarget) is not 0 and var repeatPct)
            shot = shot with { LancingSteelHits = new(Math.Max(0, 1 + repeatPct / 100d)) };
        if (effect == "LancingSteel" && spent > 0)
        {
            long bonus = context.Own(Stat.NumberOfProjectilesToFirePlusPctFinalPerSteelAmmoConsumed) * (long)spent;
            profile = profile with { Count = (int)Math.Clamp(profile.Count * (100 + bonus) / 100, 1, 256) };
        }
        if (effect == "ShatteringSteelAltX")
        {
            double bonus = Math.Max(0, context.Own(Stat.ShatteringSteelHitDamagePlusPctFinalScaledByProjectileDistancePerAmmoConsumed))
                * spent / 100d;
            shot = shot with { ShatteringSteelAmmoHitBonus = bonus };
        }
        use = use with { Profile = profile, Payload = shot };
        return shot.LancingSteelHits is { } series ? AimLancingSteelCluster(context, use, series) : use;
    }

    static ProjectileUse AimLancingSteelCluster(ProjectileReleaseContext context, ProjectileUse use, ProjectileOffense.LancingSteelHitSeries series)
    {
        var session = context.Session;
        var ray = use.Aim - use.Origin;
        var forward = ray.Unit;
        // A small forward offset gives the cluster its own origin. Keep the cast origin if the offset meets terrain.
        var origin = use.Origin + forward * Math.Min(4, ray.Length / 2);
        if (session.TraceTerrain(use.Origin, origin) is not null) origin = use.Origin;
        double range = use.Profile.Range;
        double search = range * Math.Clamp(1 + context.Get(Stat.LancingSteelTargetingRangePlusPct) / 100d, 0, 2);
        return use with { Origin = origin, InitialAim = Select };

        ProjectileVector? Select(ProjectileVector from)
        {
            ProjectileVector? best = null;
            double nearest = double.PositiveInfinity;
            bool seen = true;
            long at = session.Runtime.Now;
            foreach (var target in session.ActorsNear(from, search, at))
            {
                if (target.Team == use.Team) continue;
                var aim = target.Position(at);
                var offset = aim - from;
                double distance = offset.Length;
                if (distance <= 0 || distance > search || (offset.Dot(forward) < 0 && !TargetAccuracy.InCloseRange(distance)))
                    continue;
                bool alreadyHit = series.Targets.Contains(target.Key);
                if (best is not null && (alreadyHit && !seen || alreadyHit == seen && distance >= nearest)) continue;
                if (session.TraceTerrain(from, aim) is not null) continue;
                (best, nearest, seen) = (aim, distance, alreadyHit);
            }
            return best;
        }
    }

    static void AddSteelWard(ProjectileReleaseContext context, SteelState state)
    {
        var player = context.Player;
        var session = context.Session;
        int block = context.Own(Stat.AdditionalBlockChanceAgainstProjectilesPctPerSteelCharge);
        double duration = SkillDurationModifiers.Seconds(context.Own(Stat.BaseSkillEffectDuration), context.PlayerStat, context.Own);
        if (block <= 0 || duration <= 0) return;
        long now = session.Runtime.Now;
        state.Wards.RemoveAll(ward => ward.Expires <= now);
        if (state.Wards.Count >= SteelWardLimit)
        {
            int oldest = 0;
            for (int i = 1; i < state.Wards.Count; i++)
                if (state.Wards[i].Expires < state.Wards[oldest].Expires) oldest = i;
            state.Wards.RemoveAt(oldest);
        }
        long expires = now + ProjectileRuntime.Time(duration);
        state.Wards.Add((expires, block));
        PresentSteelWard(player, state);
        void Expire()
        {
            if (state.Wards.RemoveAll(ward => ward.Expires <= expires) > 0) PresentSteelWard(player, state);
        }
        if (player.Frames.Running)
        {
            // A failed area transfer retires projectiles but keeps the connection and its area buffs.
            // The area's timer still clears this ward when its duration ends.
            var area = player.Area;
            long frames = Math.Max(1, (long)Math.Ceiling(duration * 1000 / Program.FrameMs));
            InArea(player, owner => owner.Timers.After(owner.Frame, frames, _ =>
            {
                if (CurrentGameWork && !player.Closed && ReferenceEquals(player.Area, area)) Expire();
            }, owner: new EntityId(player.Id), source: CombatLifetime));
        }
        else session.Runtime.Schedule(expires, () => { if (session.Active) Expire(); });
    }

    static void PresentSteelWard(Player player, SteelState state)
    {
        var area = player.Area;
        int stacks = state.Wards.Count;
        if (stacks == 0)
        {
            area.Buffs.Clear(player.Id, SteelWardBuff, player.Id);
            area.RemoveStatBuff(new EntityId(player.Id), state);
            return;
        }
        int total = ActorScale.Saturate(state.Wards.Sum(ward => (long)ward.Block));
        long nowTicks = player.ProjectileRuntime is { Active: true } runtime ? runtime.Runtime.Now : ProjectileRuntime.Time(AreaTime(player));
        long maxExpires = state.Wards.Max(ward => ward.Expires);
        double remaining = (maxExpires - nowTicks) / (double)ProjectileRuntime.QuantaPerSecond;
        var stats = new Dictionary<uint, int> { [Stat.AdditionalBlockChanceAgainstProjectilesPct] = total };
        area.SetStatBuff(new EntityId(player.Id), state, stats);
        area.Buffs.Set(player.Id, BuffRows.Spec(SteelWardBuff, player.Id, stats,
            remaining > 0 ? remaining : (double?)null) with { Stacks = (ushort)stacks });
    }

    static void ClearSteelBuffsOnDeath(Player player)
    {
        if (!SteelStates.TryGetValue(player, out var state)) return;
        using var mutation = ProjectileMutation(player, advance: false);
        state.Refill++;
        ClearSteelwormLocked(player, state);
        if (state.Wards.Count == 0) return;
        state.Wards.Clear();
        PresentSteelWard(player, state);
    }
}
