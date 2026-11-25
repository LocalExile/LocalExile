public static partial class Game
{
    sealed record SpectreChildPlan(string Family, string Variety, int Count, int Limit, double Lifetime);
    sealed record SpectreChildSummon(GolemState.Golem Parent, SpectreChildPlan Plan, (int X, int Y) Aim);
    static Dictionary<int, SpectreChildPlan> SpectreChildPlans = [];
    static readonly RuleTable<Player, Queue<SpectreChildSummon>> PendingSpectreChildren = new();
    static readonly RuleTable<GolemState.Golem, GolemState.Golem> SpectreChildParents = new();

    static void LoadSpectreChildrenData()
    {
        SpectreChildPlans = [];
        foreach (var (name, family) in new[]
        {
            ("AzmeriGoddessSummonSkeletonGhostflame", "SpectreSkeleton"),
            ("SSMAzmeriRobotArgusItsRainingMannequins", "SpectreConstruct"),
            ("AzmeriWanderingEnfeebleGeofriHexproof", "SpectreEnfeebleGround"),
            ("AzmeriWanderingVulnerabilityGeofriHexproof", "SpectreVulnerabilityGround"),
            ("AzmeriWanderingTemporalChainsGeofriHexproof", "SpectreTemporalChainsGround"),
            ("AzmeriWanderingEnfeebleGeofri", "SpectreEnfeebleGround"),
            ("AzmeriWanderingVulnerabilityGeofri", "SpectreVulnerabilityGround"),
            ("AzmeriWanderingTemporalChainsGeofri", "SpectreTemporalChainsGround"),
        })
        {
            int effect = Skills.EffectRow(name);
            if (effect < 0 || Skills.MonsterSummon(effect, 1) is not { } summon
                || Monsters.Find(summon.Variety.ToString()) is not { Supported: true } variety) continue;
            int repeats = Skills.EffectStats(effect, 1).FirstOrDefault(stat => stat.Item1 == Stat.SkillRepeatCount).Item2;
            int count = (int)Math.Clamp((long)summon.Count * (1L + Math.Max(0, repeats)), 1, 200);
            // A new brood replaces the oldest of this parent's children once the native cast batch is full.
            SpectreChildPlans.Add(effect, new(family, variety.Id, count, summon.Limit > 0 ? summon.Limit : count, summon.Lifetime));
        }
    }

    static bool CanUseSpectreSummon(Player player, GolemState.Golem parent, GolemState.Ability ability,
        double distance, double now)
    {
        if (!SpectreChildPlans.TryGetValue(ability.Effect, out var plan)
            || !GolemReady(parent, ability, now) || distance > ability.Attack.Range) return false;
        return player.Summons.Fighters.Values.Count(child => child.Monster.SummonerId == parent.Id
            && child.Definition.Kind == plan.Family && !child.Monster.Dead && child.Monster.Life > 0) < plan.Limit;
    }

    static void QueueSpectreChildrenLocked(Player player, GolemState.Golem parent, GolemState.Action action)
    {
        if (SpectreChildPlans.TryGetValue(action.Ability.Effect, out var plan)
            && GolemCurrent(player, parent) && !parent.Monster.Dead && parent.Monster.Life > 0)
            PendingSpectreChildren.GetOrCreateValue(player).Enqueue(new(parent, plan, action.Aim));
    }

    static async Task SpectreChildrenTick(Player player)
    {
        if (!PendingSpectreChildren.TryGetValue(player, out var queue)) return;
        SpectreChildSummon[] pending;
        using (ProjectileMutation(player, advance: false))
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Paused || queue.Count == 0) return;
            pending = [.. queue];
            queue.Clear();
        }
        foreach (var request in pending)
        {
            var (parent, plan, aim) = request;
            bool ParentCurrent() => GolemCurrent(player, parent) && !parent.Monster.Dead && parent.Monster.Life > 0;
            if (!ParentCurrent()) continue;
            bool curseGround = SpectreGroundHex(plan.Family) is not null;
            var definition = new SummonCatalog.Definition(parent.Definition.Id, plan.Family, plan.Variety, 0, 0,
                Follows: !curseGround, Targetable: !curseGround);
            for (int index = 0; index < Math.Min(plan.Count, plan.Limit); index++)
                if (await SummonFighterCore(player, parent.Source, definition, aim, restoredVariety: plan.Variety,
                    current: ParentCurrent, remaining: plan.Lifetime, triggered: true, spectreChild: request) == 0) break;
        }
    }

    static bool SpectreChildStillOwned(Player player, GolemState.Golem fighter) =>
        !SpectreChildParents.TryGetValue(fighter, out var parent)
        || GolemCurrent(player, parent) && !parent.Monster.Dead && parent.Monster.Life > 0;
}
