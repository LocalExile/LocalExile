using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string LivingLightningFamily = "LivingLightning";
    const string LivingLightningSupport = "SupportLivingLightning";
    const int LivingLightningZapEffect = 9346;

    sealed record LivingLightningCooldown(int Epoch, SingleUseCooldownState State);
    static readonly RuleTable<Player, LivingLightningCooldown> LivingLightningCooldowns = new();
    sealed class LivingLightningAttacks { public int Released; }
    static readonly RuleTable<GolemState.Golem, LivingLightningAttacks> LivingLightningAttackCounts = new();

    static SummonCatalog.Definition? LivingLightningDefinition(Player player, SupportedGem gem)
    {
        string? effect = SkillsFor(player).EffectId(gem.Effect);
        return effect is not null && gem.Supports.Any(support => support.Effect.Id == LivingLightningSupport)
            ? new(effect, LivingLightningFamily, "Metadata/Monsters/LivingLightning/LivingLightningSummoned",
                Stat.BaseNumberOfLivingLightningAllowed, 0, Follows: false, Targetable: false) : null;
    }

    static IReadOnlyList<GemSupports.Applied> ResolveLivingLightningSupports(Player player,
        IReadOnlyList<GemSupports.Applied> supports, List<GemSupports.Candidate> candidates,
        IReadOnlyList<(byte Row, InventoryItem Item)> equipment, IReadOnlySet<uint>? inactive)
    {
        if (supports.FirstOrDefault(s => s.Effect.Id == LivingLightningSupport) is not { } support) return [];
        int effect = SkillsFor(player).EffectRow("TriggeredSupportLivingLightning");
        return ResolveEquippedSupports(player, effect, support.Gem.Level,
            candidates.Where(candidate => candidate.Effect != support.Gem.Effect).ToList(),
            equipment, inactive, sourceIsGem: false);
    }

    internal static SupportedGem LivingLightningGem(Player player, SupportedGem source)
    {
        var support = source.Supports.First(s => s.Effect.Id == LivingLightningSupport);
        var catalog = SkillsFor(player);
        int effect = catalog.EffectRow("TriggeredSupportLivingLightning");
        var stats = catalog.EffectStats(effect, support.Gem.Level).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        ApplySupportStats(player, stats, source.LivingLightningSupports);
        stats[Stat.BaseDisplayMinionActorLevel] = catalog.PlayerLevelReq(effect, support.Gem.Level);
        stats[Stat.LivingLightningNumberOfAttacks] = GolemStat(stats, Stat.LivingLightningNumberOfAttacks)
            + support.Gem.Quality / 10;
        return source with { Stats = stats };
    }

    static void QueueLivingLightningLocked(Player player, uint id, SpawnedMonster target,
        ushort source, IReadOnlyDictionary<uint, int>? stats, DamagePacket damage, double now)
    {
        if (source == 0 || stats?.GetValueOrDefault(Stat.SummonLivingLightningOnLightningHit) is not > 0
            || damage[DamageType.Lightning].Min <= 0 || !target.Hostile || target.Invulnerable
            || player.PlayerLife == 0 || !ReferenceEquals(player.Area.Monsters.GetValueOrDefault(id), target)) return;
        var gem = SupportedSkill(player, source);
        if (!gem.RequirementsMet || PlacedKind(gem) != (false, false) || GolemStat(gem.Stats, Stat.IsTotem) != 0
            || LivingLightningDefinition(player, gem) is null) return;
        var support = gem.Supports.First(s => s.Effect.Id == LivingLightningSupport);
        var catalog = SkillsFor(player);
        int effect = catalog.EffectRow("TriggeredSupportLivingLightning");
        var own = LivingLightningGem(player, gem).Stats;
        if (SingleUseCooldown.ForSkill(catalog, Stats, effect, support.Gem.Level,
                PlayerStatValues(player), own) is not { } plan) return;
        if (!LivingLightningCooldowns.TryGetValue(player, out var cooldownState) || cooldownState.Epoch != player.ProjectileEpoch)
        {
            cooldownState = new(player.ProjectileEpoch, new(now, plan.RecoveryRate));
            LivingLightningCooldowns.AddOrUpdate(player, cooldownState);
        }
        if (!cooldownState.State.TryUse(now, plan)) return;
        player.Summons.HitSummons.Add(new(id, target, source, gem.Effect, gem.SourceGem, false,
            Kind: LivingLightningFamily, Supports: SummonSupportStamp(gem, livingLightning: true)) { WornGrant = gem.WornGrant });
    }

    static void ReleaseLivingLightningZapLocked(Player player, GolemState.Golem minion, GolemState.Action action, double now)
    {
        var ability = action.Ability;
        if (action.Victim is not { Hostile: true, Dead: false } current
            || !ReferenceEquals(player.Area.Monsters.GetValueOrDefault(action.Target), current)) return;
        int chains = GolemStat(minion.Own, Stat.NumberOfChains);
        uint id = action.Target;
        var visited = new HashSet<uint>();
        for (int jump = 0; jump <= chains; jump++)
        {
            visited.Add(id);
            GolemHitLocked(player, minion, ability, id, current, now);
            if (jump == chains) break;
            var last = current.Walk.Position(now);
            var next = player.Area.MonstersNear(last, ability.Attack.Range)
                .Where(m => !visited.Contains(m.Key) && m.Value is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false }
                    && Dist(m.Value.Walk.Position(now), last) <= ability.Attack.Range)
                .OrderBy(m => Dist(m.Value.Walk.Position(now), last)).ThenBy(m => m.Key).FirstOrDefault();
            if (next.Value is null) break;
            (id, current) = (next.Key, next.Value);
        }
        if (++LivingLightningAttackCounts.GetOrCreateValue(minion).Released >=
            GolemStat(minion.Own, Stat.LivingLightningNumberOfAttacks))
            RemoveSummonLocked(player, minion, now);
    }
}