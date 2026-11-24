public static partial class Game
{
    // Doedre's Skin grants five extra Hex slots and applies a 20% less modifier to these auras.
    const int EffigyAdditionalHexes = 5;
    const double EffigyCurseEffect = .8;

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
        foreach (var row in GemSkillEffects(player))
        {
            string? effect = SkillsFor(player).EffectId(row.Effect);
            if (effect is null || !CurseCatalog.Hex(effect)) continue;
            var hex = SupportedSkill(player, row.Id);
            if (!hex.RequirementsMet || hex.Host != grant.Host || hex.SourceGem is null) continue;
            int radius = CurseRadius(player, row.Id, true, hex, effigy.Player);
            foreach (var (_, monster) in player.Area.MonstersNear(at, radius))
                if (Dist(at, monster.Walk.Position(now)) <= radius
                    && ApplyCurseDataLocked(player, monster, row.Id, effect, hex.Stats, now, true, effigy.Player,
                        effectMultiplier: EffigyCurseEffect, casterId: effigy.Id, additionalCapacity: EffigyAdditionalHexes))
                    state.Current.Add((monster, effect));
        }
        foreach (var (monster, effect) in state.Covered)
            if (!state.Current.Contains((monster, effect))) monster.Curses.Remove(effigy.Id, effect);
        (state.Covered, state.Current) = (state.Current, state.Covered);
        state.Current.Clear();
    }

    static void ClearEffigyHexesLocked(GolemState.Golem effigy)
    {
        if (!EffigyAuras.TryGetValue(effigy, out var state)) return;
        foreach (var (monster, effect) in state.Covered) monster.Curses.Remove(effigy.Id, effect);
        EffigyAuras.Remove(effigy);
    }
}
