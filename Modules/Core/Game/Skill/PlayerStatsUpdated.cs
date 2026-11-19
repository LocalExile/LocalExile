public static partial class Game
{
    /// <summary>
    /// The player's stats changed (or a life, mana or moving condition flipped), after the area's Stats phase: the systems
    /// that take their configuration from the stats read them here, once, instead of inside the steps that compute them.
    /// </summary>
    static void PlayerStatsUpdated(Player conn, IReadOnlyDictionary<uint, int> stats)
    {
        int Get(uint key) => stats.GetValueOrDefault(key);
        PresentStats(conn);
        RecoveryRatesChanged(conn);
        // A periodic charge gain's stat may have come (ChargeSession.Observe): the charges ticker runs while it has work.
        WakeCharges(conn);
        WakeFootsteps(conn);
        // What the pools are built of (Adrenaline, Fortification, the aspects and aura reservations, conditional items):
        // a change rebuilds them, and the steps that read the pools run in the next phase.
        EnsureConditionalItemPools(conn);
        EnsureAntiquarianPools(conn);
        EnsureOwnAuraPools(conn);
        EnsureFortificationPools(conn);
        ConfigureFortificationRecovery(conn, conn.Pools.Life, Get);
        ConfigureLeech(conn, Get, CriticalOnLowLife(conn, stats));
        ConfigureKillOnslaught(conn, Get);
        ConfigureSoulEater(conn, stats);
        ObserveAntiquarian(conn, stats);
        ReconcileRampageSource(conn, stats);
        lock (conn.Monsters)
        {
            if (conn.RageRetired || conn.PlayerLife == 0) conn.Rage.Clear();
            if (conn.ResourceEffectsState is ResourceEffectsState state && ResourceEffectsCurrent(conn, state))
                state.Mana.Observe(RageStat(stats, ManaRecoveryRules.RagePer200));
            // A new immunity removes the ailments it rejects; the stats that read them follow.
            var ailments = conn.ElementalAilments.Snapshot(AreaTime(conn));
            conn.ElementalAilments.Reconcile(Get);
            if (conn.ElementalAilments.Snapshot(AreaTime(conn)) != ailments)
                foreach (var step in PlayerStatChangeReaders[(int)PlayerStatChange.ElementalAilments]) MarkPlayerStats(conn, step);
            if (conn.IncomingAilments.Reconcile(Get))
            {
                EnsureConditionalItemPools(conn);
                MarkPlayerStats(conn, PlayerStatStep.Sources);
            }
        }
    }
}
