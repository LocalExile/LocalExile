public static partial class Game
{
    /// <summary>
    /// The player's stats changed (or a life, mana or moving condition flipped), after the area's Stats phase: the systems
    /// that take their configuration from the stats read them here, once, instead of inside the steps that compute them.
    /// </summary>
    static void PlayerStatsUpdated(Player player, IReadOnlyDictionary<uint, int> stats)
    {
        int Get(uint key) => stats.GetValueOrDefault(key);
        PresentStats(player);
        RecoveryRatesChanged(player);
        double now = AreaTime(player);
        FortificationOf(player).SetExpiryRate(now, RecipientEffectTime.Rate(1, Get));
        player.UtilityFlaskEffects.SetExpiryRate(now, RecipientEffectTime.Rate(16, Get));
        player.EnergyShield.SetRechargeProtection(Get(Stat.KeystoneWickedWard) != 0 ? RecentlySeconds : 0);
        // A periodic charge gain's stat may have come (ChargeSession.Observe): the charges ticker runs while it has work.
        WakeCharges(player);
        WakeFootsteps(player);
        // What the pools are built of (Adrenaline, Fortification, the aspects and aura reservations, conditional items):
        // a change rebuilds them, and the steps that read the pools run in the next phase.
        EnsureConditionalItemPools(player);
        EnsureAntiquarianPools(player);
        EnsureOwnAuraPools(player);
        EnsureFortificationPools(player);
        ConfigureFortificationRecovery(player, player.Pools.Life, Get);
        ConfigureLeech(player, Get, CriticalOnLowLife(player, stats));
        // A stat an effect rule drives is the rule's: the older Onslaught code reads it as 0.
        ConfigureKillOnslaught(player, EffectRules.Unhandled(Get));
        EffectRules.Configure(player, Get, RuleSources(player));
        ConfigureSoulEater(player, stats);
        ObserveAntiquarian(player, stats);
        ReconcileRampageSource(player, stats);
        if (player.RageRetired || player.PlayerLife == 0) player.Rage.Clear();
        if (player.ResourceEffectsState is ResourceEffectsState state && ResourceEffectsCurrent(player, state))
            state.Mana.Observe(RageStat(stats, ManaRecoveryRules.RagePer200));
        // A new immunity removes the ailments it rejects; the stats that read them follow.
        var ailments = player.ElementalAilments.Snapshot(AreaTime(player));
        player.ElementalAilments.Reconcile(Get);
        if (player.ElementalAilments.Snapshot(AreaTime(player)) != ailments)
            foreach (var step in PlayerStatChangeReaders[(int)PlayerStatChange.ElementalAilments]) MarkPlayerStats(player, step);
        if (player.IncomingAilments.Reconcile(Get))
        {
            EnsureConditionalItemPools(player);
            MarkPlayerStats(player, PlayerStatStep.Sources);
        }
    }
}
