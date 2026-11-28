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
        if (damage <= 0 || (player.Minions.All.Count == 0 && player.Companion is null)) return 0;
        bool Eligible(GolemState.Golem fighter) => GolemCurrent(player, fighter) && !fighter.Monster.Dead
            && fighter.Monster.Life > 0 && !GolemIgnoresDots(fighter)
            && fighter.Monster.Effects[Stat.BaseCannotBeDamaged] == 0;

        int diverted = 0;
        if (player.Minions.All.Count > 0)
        {
            const uint companionShare = Stat.DamageRemovedFromMinionsBeforeLifeOrEsPctIfOnlyOneMinion;
            var companion = OnlyLivingFighter(player, now);
            int companionPct = 0;
            if (companion is not null && companion.Own.ContainsKey(companionShare) && Eligible(companion))
            {
                var gem = SupportedSkill(player, companion.Source);
                if (gem.RequirementsMet) companionPct = Math.Clamp(GolemStat(gem.Stats, companionShare), 0, 100);
            }
            int spectrePct = Math.Clamp(GolemStat(stats, Stat.DamageRemovedFromSpectresBeforeLifeOrEsPct), 0, 100);
            if (spectrePct > 0)
            {
                var spectres = player.Summons.Fighters.Values.Where(g => g.Definition.Kind == "Spectre"
                    && g.Monster.SummonerId is null && Eligible(g)).OrderBy(g => g.Id).ToArray();
                int share = (int)((long)damage * spectrePct / 100);
                for (int i = 0; i < spectres.Length; i++)
                    diverted += TakeMinionLifeLocked(player, spectres[i], share / spectres.Length
                        + (i < share % spectres.Length ? 1 : 0), now);
            }

            int voidSpawnPct = GolemStat(stats, Stat.DamageRemovedFromVoidSpawnsBeforeLifeOrEsPerVoidSpawnsPct);
            if (voidSpawnPct > 0)
            {
                var spawns = player.Summons.Fighters.Values.Where(g => g.Definition.Kind == VoidSpawnFamily && Eligible(g))
                    .OrderBy(g => g.Id).ToArray();
                if (spawns.Length > 0)
                {
                    int share = (int)((long)(damage - diverted) * Math.Min(100L, (long)spawns.Length * voidSpawnPct) / 100);
                    for (int i = 0; i < spawns.Length; i++)
                        diverted += TakeMinionLifeLocked(player, spawns[i], share / spawns.Length + (i < share % spawns.Length ? 1 : 0), now);
                }
            }

            if (companion is not null && companionPct > 0)
                diverted += TakeMinionLifeLocked(player, companion, (int)((long)(damage - diverted) * companionPct / 100), now);
        }

        // Loyal Bodyguard: 20% (HM 15%) of hit damage taken from nearest permanent mercenary's Life if higher than player's.
        diverted += CompanionTakeOwnerHitLocked(player, damage - diverted, now, stats);
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
