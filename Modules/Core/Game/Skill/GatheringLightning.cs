using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

static partial class Game
{
    const ushort GatheringLightningBuffRow = 2687;
    static readonly RuleTable<Player, GatheringLightningState> GatheringLightningStates = new();

    sealed class GatheringLightningState(GameArea area, int stacks)
    {
        public readonly GameArea Area = area;
        public readonly int Stacks = stacks;
        public double Until;
        public GatheringLightningProjection? Projection;
    }

    sealed record GatheringLightningProjection(IReadOnlyList<GrantedSkill> BaseSkills, IReadOnlyList<GrantedSkill> Skills);

    static GatheringLightningState? CurrentGatheringLightning(Player player, double now) =>
        GatheringLightningStates.TryGetValue(player, out var state) && ReferenceEquals(player.Area, state.Area)
            && now < state.Until && player.Area.Buffs.Has(player.Id, GatheringLightningBuffRow, player.Id) ? state : null;

    static int GatheringLightningChains(Func<uint, int> get, int stacks)
    {
        int chains = Math.Max(0, get(Stat.NumberOfChains) + get(Stat.AdditionalBeamOnlyChains) + stacks);
        int more = get(Stat.SupportChainCountPlusPctFinal);
        return more != 0 ? Math.Max(0, (int)(chains * (1 + more / 100d))) : chains;
    }

    static bool IsGatheringLightning(Player player, GrantedSkill skill) =>
        SkillsFor(player).EffectsPerLevel((int)skill.EffectsPerLevelRow) is { } row && IsGatheringLightning(row.Effect);

    // The native beam reads its final chain count from the action's skill record (0x14193ef80).
    // Reuse the normal skill snapshot; a new stack changes only the Gathering Lightning records.
    static IReadOnlyList<GrantedSkill> GatheringLightningSkillRecords(Player player,
        IReadOnlyList<GrantedSkill> skills, double now)
    {
        if (CurrentGatheringLightning(player, now) is not { } state) return skills;
        if (state.Projection is { } projection && ReferenceEquals(skills, projection.BaseSkills)) return projection.Skills;
        GrantedSkill Project(GrantedSkill skill)
        {
            if (!IsGatheringLightning(player, skill) || skill.Stats is not { } stats) return skill;
            int Get(uint key) => stats.FirstOrDefault(stat => stat.Key == key).Value;
            int chains = GatheringLightningChains(Get, state.Stacks);
            return skill with { Stats = [.. stats.Where(stat => stat.Key != Stat.VirtualNumberOfChainsForBeams),
                (Stat.VirtualNumberOfChainsForBeams, chains)] };
        }
        var projected = skills.Select(Project).ToArray();
        state.Projection = new(skills, projected);
        return projected;
    }

    static void PublishGatheringLightningSkills(Player player, ProjectileSession session, double now)
    {
        if (player.PlayerSkillsCache is not { } skills) return;
        var changed = GatheringLightningSkillRecords(player, skills, now)
            .Where(skill => IsGatheringLightning(player, skill)).ToArray();
        if (changed.Length == 0) return;
        session.World(player.Id, InstanceEntities.PlayerSkillsChange(player.Id, changed, [],
            canDetonateMines: CanDetonateMines(player)), "Gathering Lightning beam chains");
        session.Enqueue(() => RestoreSkillBarAfterActorUpdate(player));
    }

    static void ReleaseGatheringLightning(Player player, ProjectileSession session, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) aim, uint targetId, SkillCriticalRoll criticalRoll, ushort sequence,
        int repeat, bool cryExerted, double? ruthlessMore, int ancestralTargets)
    {
        double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
        var area = player.Area;
        var source = SupportedSkill(player, skillId);
        int Own(uint id) => source.Stats.GetValueOrDefault(id);
        var state = CurrentGatheringLightning(player, now);
        int stacks = state?.Stacks ?? 0;
        double more = Math.Max(0, 1 + (double)Own(Stat.HitAndAilmentDamagePlusPctFinalPerGatheringLightning) * stacks / 100);
        double repeatMore = MeleeRepeatDamageMore(player, skillId, repeat);
        var contacts = new List<uint>();
        var melee = AreaSkillHitForUse(ruthlessMore, player, skillId, gem, from, aim, immediate: true,
            sequence: sequence, criticalRoll: criticalRoll, targetId: targetId, damageMore: repeatMore,
            scheduledSession: session, cryExerted: cryExerted, repeat: repeat, hitAndAilmentMore: more,
            onDirectHit: contacts.Add, ancestralTargets: ancestralTargets);
        if (!melee.IsCompleted) throw new InvalidOperationException("Scheduled Gathering Lightning contact must not yield.");
        melee.GetAwaiter().GetResult();
        if (contacts.Count == 0) return;

        var playerStats = CharacterPlayerStats(player);
        int Get(uint id) => ActorScale.Add(id, playerStats.GetValueOrDefault(id), Own(id));
        // StaticStrikeNew variation 1 starts its beam at the struck enemy. Its first and later
        // searches use 35 and 50 cells, respectively, both modified by chaining range.
        double range = Math.Max(0, 1 + Get(Stat.ChainingRangePlusPct) / 100d);
        int firstRange = Math.Max(1, (int)(35 * range));
        int chainRange = Math.Max(1, (int)(50 * range));
        int chains = GatheringLightningChains(Get, stacks);
        foreach (uint direct in contacts)
        {
            if (!area.Monsters.TryGetValue(direct, out var origin)) continue;
            var hitTargets = new HashSet<uint> { direct };
            var current = origin;
            for (int hop = 0; hop <= chains && current.Effects[Stat.CannotBeChainedFrom] == 0; hop++)
            {
                var at = current.Walk.Position(now);
                int reach = hop == 0 ? firstRange : chainRange;
                var next = area.MonstersNear(at, reach)
                    .Where(pair => !hitTargets.Contains(pair.Key) && pair.Value is
                        { Hostile: true, Dead: false, Life: > 0, OwnerId: null, Asleep: false, WorldHidden: false })
                    .Select(pair => (pair.Key, Monster: pair.Value, Position: pair.Value.Walk.Position(now)))
                    .Where(pair => Dist(at, pair.Position) <= reach
                        && (player.Layout.Sight is not { } sight || !ClientPathfinder.LineBelow(at, pair.Position, sight, 1)))
                    .OrderBy(pair => Dist(at, pair.Position)).ThenBy(pair => pair.Key).FirstOrDefault();
                if (next.Key == 0) break;
                hitTargets.Add(next.Key);
                var beam = AreaSkillHitForUse(1, player, skillId, gem, at, next.Position, immediate: true,
                    sequence: sequence, melee: false, criticalRoll: criticalRoll, damageMore: repeatMore,
                    radiusOverride: reach, singleStrikeTarget: next.Key, scheduledSession: session,
                    cryExerted: cryExerted, repeat: repeat, hitAndAilmentMore: more, allowMeleeSplash: false);
                if (!beam.IsCompleted) throw new InvalidOperationException("Scheduled Gathering Lightning beam must not yield.");
                beam.GetAwaiter().GetResult();
                current = next.Monster;
            }
        }

        int cap = Own(Stat.StaticStrikeMaximumGatheringLightningStacks);
        int gained = Own(Stat.StaticStrikeNumberGatheringLightningToGainOnHit);
        int duration = Own(Stat.BaseSkillEffectDuration);
        if (cap <= 0 || gained <= 0 || duration <= 0) return;
        int newStacks = (int)Math.Min(Math.Min(cap, ushort.MaxValue), stacks + (long)gained * contacts.Count);
        double seconds = SkillDurationModifiers.Seconds(duration, key => playerStats.GetValueOrDefault(key), Own);
        if (seconds <= 0) return;
        bool changed = newStacks != stacks;
        var refreshed = changed ? new GatheringLightningState(area, newStacks) : state!;
        double expires = now + seconds;
        refreshed.Until = expires;
        if (changed) GatheringLightningStates.AddOrUpdate(player, refreshed);
        area.Buffs.Set(player.Id, new(GatheringLightningBuffRow, player.Id,
            U68: Buff.Seconds(seconds), Stacks: (ushort)newStacks));
        if (changed) PublishGatheringLightningSkills(player, session, now);
        session.Runtime.Schedule(ProjectileRuntime.Time(expires), () =>
        {
            if (!session.Active || refreshed.Until != expires
                || !GatheringLightningStates.TryGetValue(player, out var latest) || !ReferenceEquals(latest, refreshed))
                return;
            GatheringLightningStates.Remove(player);
            area.Buffs.Clear(player.Id, GatheringLightningBuffRow, player.Id);
            PublishGatheringLightningSkills(player, session, expires);
        });
    }
}
