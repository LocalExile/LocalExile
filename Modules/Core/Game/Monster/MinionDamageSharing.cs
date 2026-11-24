public static partial class Game
{
    static GolemState.Golem? OnlyLivingFighter(Player player, double now)
    {
        PlayerMinions.Minion? only = null;
        foreach (var minion in player.Minions.All)
        {
            if (minion.Until <= now || minion.Monster.Dead || minion.Monster.Life == 0
                || !ReferenceEquals(player.Area.Monsters.GetValueOrDefault(minion.Id), minion.Monster)) continue;
            if (only is not null) return null;
            only = minion;
        }
        return only is null ? null : OwnedFighter(player, only.Id);
    }

    static double CompanionshipDamageTaken(Player player, GolemState.Golem fighter, double now)
    {
        uint key = Stat.SupportCompanionshipMinionDamageTakenPlusPctFinalIfAtMostOneMinion;
        if (!fighter.Own.ContainsKey(key) || OnlyLivingFighter(player, now) != fighter) return 1;
        var gem = SupportedSkill(player, fighter.Source);
        return gem.RequirementsMet ? Math.Max(0, 1 + GolemStat(gem.Stats, key) / 100d) : 1;
    }

    static int MinionsTakeOwnerHitLocked(Player player, int damage, double now, IReadOnlyDictionary<uint, int> stats)
    {
        if (damage <= 0 || player.Minions.All.Count == 0) return 0;
        bool Eligible(GolemState.Golem fighter) => GolemCurrent(player, fighter) && !fighter.Monster.Dead
            && fighter.Monster.Life > 0 && !GolemIgnoresDots(fighter)
            && fighter.Monster.Effects[Stat.BaseCannotBeDamaged] == 0;

        const uint companionShare = Stat.DamageRemovedFromMinionsBeforeLifeOrEsPctIfOnlyOneMinion;
        var companion = OnlyLivingFighter(player, now);
        int companionPct = 0;
        if (companion is not null && companion.Own.ContainsKey(companionShare) && Eligible(companion))
        {
            var gem = SupportedSkill(player, companion.Source);
            if (gem.RequirementsMet) companionPct = Math.Clamp(GolemStat(gem.Stats, companionShare), 0, 100);
        }
        int diverted = 0;
        int spectrePct = Math.Clamp(GolemStat(stats, Stat.DamageRemovedFromSpectresBeforeLifeOrEsPct), 0, 100);
        if (spectrePct > 0)
        {
            var spectres = player.Golems.All.Values.Where(g => g.Definition.Kind == "Spectre"
                && g.Monster.SummonerId is null && Eligible(g)).OrderBy(g => g.Id).ToArray();
            int share = (int)((long)damage * spectrePct / 100);
            for (int i = 0; i < spectres.Length; i++)
                diverted += TakeMinionLifeLocked(player, spectres[i], share / spectres.Length
                    + (i < share % spectres.Length ? 1 : 0), now);
        }

        if (companion is not null && companionPct > 0)
            diverted += TakeMinionLifeLocked(player, companion, (int)((long)(damage - diverted) * companionPct / 100), now);
        return diverted;
    }

    static int TakeMinionLifeLocked(Player player, GolemState.Golem fighter, int amount, double now)
    {
        int loss = (int)Math.Min(amount, (long)fighter.Monster.Life);
        if (loss <= 0) return 0;
        fighter.Monster.Life -= (uint)loss;
        player.Minions.OutputQueue.Add(new([], null, fighter.Id, fighter.Monster, PlayerMinions.OutputKind.Life));
        if (fighter.Monster.Life == 0) GolemDieLocked(player, fighter, now, emitLife: false);
        else MinionInstabilityLocked(player, fighter, now);
        return loss;
    }
}
