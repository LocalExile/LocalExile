using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string CallOfSteelEffect = "CallOfSteel";
    const ushort SteelShardBuff = 1610, SteelWardBuff = 1635;
    const int BaseSteelShardLimit = 12, SteelWardLimit = 6;
    // The native skill gives separate collection and reflection areas. Their radii are not in the exported stats.
    const int SteelCollectionRadius = 100, SteelReflectionRadius = 20;
    static readonly RuleTable<Player, SteelState> SteelStates = new();

    sealed class SteelState
    {
        public int Shards, Refill;
        public readonly List<(long Expires, int Block)> Wards = [];
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

    static ProjectileUse PrepareSteelRelease(ProjectileReleaseContext context, ProjectileUse use, string effect)
    {

        var player = context.Player;
        int cost = context.Own(Stat.SteelAmmoConsumedPerUse), spent = 0;
        var shot = (ProjectileOffense)use.Payload!;
        if (cost > 0 && !shot.FromTrapOrMine && !shot.Mirage && shot.ActualCaster is null
            && SteelStates.TryGetValue(player, out var state) && state.Shards > 0)
        {
            spent = Math.Min(cost, state.Shards);
            state.Refill++;
            SetSteelShards(player, state, state.Shards - spent);
            if (effect == "ShatteringSteelAltX" && spent == cost) AddSteelWard(context, state);
        }
        var profile = use.Profile;
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
            if (spent == 0 && context.Own(Stat.Fires1ProjectileIfNoSteelAmmo) != 0)
                profile = profile with { Count = 1, ParallelOrigins = null };
            double bonus = Math.Max(0, context.Own(Stat.ShatteringSteelHitDamagePlusPctFinalScaledByProjectileDistancePerAmmoConsumed))
                * spent / 100d;
            shot = shot with { ShatteringSteelAmmoHitBonus = bonus };
        }
        return use with { Profile = profile, Payload = shot };
    }

    static void AddSteelWard(ProjectileReleaseContext context, SteelState state)
    {
        var player = context.Player;
        var session = context.Session;
        int block = context.Own(Stat.AdditionalBlockChanceAgainstProjectilesPctPerSteelCharge);
        double duration = SkillDurationModifiers.Seconds(context.Own(Stat.BaseSkillEffectDuration), context.PlayerStat, context.Own);
        if (block <= 0 || duration <= 0 || state.Wards.Count >= SteelWardLimit) return;
        long expires = session.Runtime.Now + ProjectileRuntime.Time(duration);
        state.Wards.Add((expires, block));
        PresentSteelWard(player, state);
        session.Runtime.Schedule(expires, () =>
        {
            if (session.Active && state.Wards.RemoveAll(ward => ward.Expires <= session.Runtime.Now) > 0)
                PresentSteelWard(player, state);
        });
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
        area.SetStatBuff(new EntityId(player.Id), state, new Dictionary<uint, int> { [Stat.AdditionalBlockChanceAgainstProjectilesPct] = total });
        area.Buffs.Set(player.Id, new(SteelWardBuff, player.Id, Values: [(uint)total], Stacks: (ushort)stacks));
    }

    static void ClearSteelWardOnDeath(Player player)
    {
        if (!SteelStates.TryGetValue(player, out var state)) return;
        using var mutation = ProjectileMutation(player, advance: false);
        state.Refill++;
        if (state.Wards.Count == 0) return;
        state.Wards.Clear();
        PresentSteelWard(player, state);
    }
}
