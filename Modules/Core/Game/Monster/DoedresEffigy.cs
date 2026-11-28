public static partial class Game
{
    const int EffigyAdditionalHexes = 5;

    sealed class EffigyHexes
    {
        public HashSet<(SpawnedMonster Monster, string Effect)> Covered = [];
        public HashSet<(SpawnedMonster Monster, string Effect)> Current = [];
    }

    static readonly RuleTable<GolemState.Golem, EffigyHexes> EffigyAuras = new();

    static void EffigyHexesTickLocked(Player player, GolemState.Golem effigy, double now)
    {
        if (InTown(player) || SupportedSkill(player, effigy.Source) is not { RequirementsMet: true, WornGrant: { } grant })
        {
            ClearEffigyHexesLocked(effigy);
            return;
        }
        var state = EffigyAuras.GetOrCreateValue(effigy);
        var at = effigy.Monster.Walk.Position(now);
        int penalty = GolemStat(effigy.Player, Stat.CursePillarCurseEffectPlusPctFinal);
        if (penalty == 0) penalty = GolemStat(effigy.Own, Stat.CursePillarCurseEffectPlusPctFinal);
        double multiplier = Math.Max(0, 1 + penalty / 100d);
        foreach (var row in GemSkillEffects(player))
        {
            if (CurseCatalog.HexFamily(SkillsFor(player).EffectId(row.Effect)) is not { } effect) continue;
            var hex = SupportedSkill(player, row.Id);
            if (!hex.RequirementsMet || hex.Host != grant.Host || hex.SourceGem is null) continue;
            int radius = CurseRadius(player, row.Id, true, hex, effigy.Player);
            foreach (var (_, monster) in player.Area.MonstersNear(at, radius))
                if (Dist(at, monster.Walk.Position(now)) <= radius
                    && ApplyCurseDataLocked(player, monster, row.Id, effect, hex.Stats, now, true, effigy.Player,
                        effectMultiplier: multiplier, casterId: effigy.Id, additionalCapacity: EffigyAdditionalHexes))
                    state.Current.Add((monster, effect));
        }
        foreach (var (monster, effect) in state.Covered)
            if (!state.Current.Contains((monster, effect))) RemoveEmittedHex(monster, effigy.Id, effect);
        (state.Covered, state.Current) = (state.Current, state.Covered);
        state.Current.Clear();
    }

    static string? SpectreGroundHex(string family) => family switch
    {
        "SpectreEnfeebleGround" => "Enfeeble",
        "SpectreVulnerabilityGround" => "Vulnerability",
        "SpectreTemporalChainsGround" => "TemporalChains",
        _ => null,
    };

    static void SpectreCurseGroundTickLocked(Player player, GolemState.Golem ground, string effect, double now)
    {
        if (InTown(player) || ground.Abilities.FirstOrDefault() is not { } curse)
        {
            ClearEffigyHexesLocked(ground);
            return;
        }
        var state = EffigyAuras.GetOrCreateValue(ground);
        var at = ground.Monster.Walk.Position(now);
        int radius = SkillArea.Radius(22, key => GolemStat(curse.Stats, key), spell: true);
        bool hexproof = ground.Monster.Variety?.Id.EndsWith("Hexproof", StringComparison.Ordinal) == true;
        var stats = hexproof ? new Dictionary<uint, int>(curse.Stats) { [Stat.IgnoreHexproof] = 1 } : curse.Stats;
        foreach (var (_, monster) in player.Area.MonstersNear(at, radius))
            if (Dist(at, monster.Walk.Position(now)) <= radius
                && ApplyCurseDataLocked(player, monster, curse.Skill, effect, stats, now, true, ground.Player,
                    casterId: ground.Id, additionalCapacity: 3))
                state.Current.Add((monster, effect));
        foreach (var (monster, old) in state.Covered)
            if (!state.Current.Contains((monster, old))) RemoveEmittedHex(monster, ground.Id, old);
        (state.Covered, state.Current) = (state.Current, state.Covered);
        state.Current.Clear();

        var target = player.Area.MonstersNear(at, 100)
            .Where(pair => pair.Value is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false }
                && Dist(at, pair.Value.Walk.Position(now)) <= 100)
            .OrderBy(pair => Dist(at, pair.Value.Walk.Position(now))).ThenBy(pair => pair.Key).FirstOrDefault();
        if (target.Value is { } enemy && now >= ground.NextMove)
        {
            var goal = enemy.Walk.Position(now);
            if (Dist(at, goal) > 3) GolemMoveLocked(player, ground, goal, now);
        }
    }

    static void ClearEffigyHexesLocked(GolemState.Golem effigy)
    {
        if (!EffigyAuras.TryGetValue(effigy, out var state)) return;
        foreach (var (monster, effect) in state.Covered) RemoveEmittedHex(monster, effigy.Id, effect);
        EffigyAuras.Remove(effigy);
    }

    static void RemoveEmittedHex(SpawnedMonster monster, uint source, string effect)
    {
        if (!monster.Curses.All.ContainsKey((source, effect))) return;
        monster.Curses.Remove(source, effect);
        monster.PresentationChanged?.Invoke();
    }
}
