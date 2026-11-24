public static partial class Game
{
    sealed record SkitterHex(ushort Skill, string Effect, SupportedGem Gem);
    sealed class SkitterAuraState
    {
        public HashSet<SpawnedMonster> Covered = [];
        public HashSet<SpawnedMonster> Current = [];
        public string? Curse;
    }
    static readonly RuleTable<GolemState.Golem, SkitterAuraState> SkitterAuras = new();

    static SkitterHex? SkitterHexFor(Player player, bool cold)
    {
        byte row = cold ? PlayerInventories.Ring1 : PlayerInventories.Ring2;
        var ring = ItemsOf(player).Items(row).FirstOrDefault();
        if (ring?.Item is not { } item || !item.Sockets.Any(s => s.Gem is not null)) return null;
        uint replaces = cold ? Stat.LocalLeftRingSocketedCurseReplacesSkitterbotsChillingAura
            : Stat.LocalRightRingSocketedCurseReplacesSkitterbotsShockingAura;
        if (ItemModStats.LocalValue(item, ItemsFor(player).Mods, replaces) == 0) return null;
        foreach (var source in GemSkillEffects(player))
        {
            string? effect = SkillsFor(player).EffectId(source.Effect);
            if (!CurseCatalog.Hex(effect)) continue;
            var gem = SupportedSkill(player, source.Id);
            if (gem.Host == ring.Id && gem.RequirementsMet && gem.SourceGem is not null)
                return new(source.Id, effect!, gem);
        }
        return null;
    }

    static bool SkitterAuraCovers(GolemState.Golem fighter, SpawnedMonster monster, MonsterElementalAilments.Kind ailment, double now) =>
        SkitterAuras.TryGetValue(fighter, out var state) && state.Curse is { } curse
            ? monster.Curses.All.ContainsKey((fighter.Id, curse))
            : monster.Ailments.Magnitude(ailment, now) > 0;

    static void SkitterAuraTickLocked(Player player, GolemState.Golem fighter, double now)
    {
        bool cold = fighter.Definition.Kind == "SkitterCold";
        var ailment = cold ? MonsterElementalAilments.Kind.Chill : MonsterElementalAilments.Kind.Shock;
        var hex = SkitterHexFor(player, cold);
        var state = SkitterAuras.GetOrCreateValue(fighter);
        if (state.Curse != hex?.Effect)
        {
            foreach (var monster in state.Covered) RemoveSkitterAura(fighter.Id, monster, state.Curse);
            state.Covered.Clear();
            state.Curse = hex?.Effect;
        }
        int radius = SkitterAuraRadius(fighter);
        var at = fighter.Monster.Walk.Position(now);
        int AilmentStat(uint stat) => SkitterAilmentStat(fighter, stat);
        foreach (var monster in player.Area.Monsters.Values)
        {
            if (monster is not { Hostile: true, Dead: false, Life: > 0 } || Dist(monster.Walk.Position(now), at) > radius) continue;
            if (hex is not null)
            {
                if (!ApplyCurseDataLocked(player, monster, hex.Skill, hex.Effect, hex.Gem.Stats, now,
                    aura: true, fighter.Player, casterId: fighter.Id))
                    monster.Curses.Remove(fighter.Id, hex.Effect);
            }
            else monster.Ailments.ApplyAura(ailment, fighter.Id, .5, now, AilmentStat, n => monster.Effects[n]);
            state.Current.Add(monster);
        }
        foreach (var monster in state.Covered)
            if (!state.Current.Contains(monster)) RemoveSkitterAura(fighter.Id, monster, state.Curse);
        (state.Covered, state.Current) = (state.Current, state.Covered);
        state.Current.Clear();
    }

    static void RemoveSkitterAura(uint bot, SpawnedMonster monster, string? curse)
    {
        if (curse is not null) monster.Curses.Remove(bot, curse);
        else monster.Ailments.RemoveAura(bot);
    }

    static void ClearSkitterAuraLocked(GolemState.Golem fighter)
    {
        if (!SkitterAuras.TryGetValue(fighter, out var state)) return;
        foreach (var monster in state.Covered) RemoveSkitterAura(fighter.Id, monster, state.Curse);
        SkitterAuras.Remove(fighter);
    }
}
