public static partial class Game
{
    sealed class KeystoneRecoveryState
    {
        public double LifeInterruptedAt = double.NegativeInfinity;
        public double LifeRechargeBeganAt = double.NegativeInfinity;
        public double ShieldLossCarry;
    }

    static readonly Store<Player, KeystoneRecoveryState> KeystoneRecovery = new();

    static void ApplyRecoveryKeystoneStats(Dictionary<uint, int> stats)
    {
        if (stats.GetValueOrDefault(Stat.KeystoneEternalYouth) != 0)
            stats[Stat.CannotRechargeEnergyShield] = 1;
        if (stats.GetValueOrDefault(Stat.KeystoneSoulTether) != 0)
            stats[Stat.NoEnergyShieldRechargeOrRegeneration] = 1;
        if (stats.GetValueOrDefault(Stat.KeystoneFocusedRage) != 0)
            stats[Stat.NoManaRegeneration] = 1;
    }

    static double LifeRegenerationKeystoneFactor(Func<uint, int> get) =>
        get(Stat.KeystoneEternalYouth) != 0 && !PanelStats.ZealotsOath(get) ? .5 : 1;

    static void InitializePlayerEnergyShield(Player player)
    {
        bool ambition = PlayerStat(player, Stat.KeystoneSoulTether) != 0;
        player.EnergyShield.SetMaximum(player.Pools.EnergyShield, refill: !ambition);
        if (ambition) player.EnergyShield.Empty();
        KeystoneRecovery.Remove(player);
    }

    public static void RefillPlayerEnergyShield(Player player) =>
        player.EnergyShield.SetMaximum(player.Pools.EnergyShield, refill: PlayerStat(player, Stat.KeystoneSoulTether) == 0);

    internal static void RecordRechargeLifeDamage(Player player, long lifeDamage, double at, IReadOnlyDictionary<uint, int> stats)
    {
        if (lifeDamage <= 0 || stats.GetValueOrDefault(Stat.KeystoneEternalYouth) == 0) return;
        var state = KeystoneRecovery.GetOrCreateValue(player);
        if (stats.GetValueOrDefault(Stat.KeystoneWickedWard) != 0
            && at >= state.LifeRechargeBeganAt && at < state.LifeRechargeBeganAt + 4) return;
        state.LifeInterruptedAt = Math.Max(at, state.LifeInterruptedAt);
        state.LifeRechargeBeganAt = double.NegativeInfinity;
    }

    /// <summary>Advance the keystones on the same time interval as the resource writer.</summary>
    public static double AdvanceKeystoneRecovery(Player player, double from, double now, bool needsLife, Func<uint, int> get)
    {
        bool youth = get(Stat.KeystoneEternalYouth) != 0, ambition = get(Stat.KeystoneSoulTether) != 0;
        if (!youth && !ambition)
        {
            KeystoneRecovery.Remove(player);
            return 0;
        }

        var state = KeystoneRecovery.GetOrCreateValue(player);
        if (ambition && player.EnergyShield.Current > 0 && now > from)
            player.EnergyShield.Lose(player.EnergyShield.Maximum * .05 * (now - from), ref state.ShieldLossCarry);
        else
            state.ShieldLossCarry = 0;

        if (!youth) state.LifeInterruptedAt = double.NegativeInfinity;
        if (!youth || !needsLife || LifeRecovery.NonLeechBlocked(get) || get(Stat.CannotRecoverLife) != 0)
        {
            state.LifeRechargeBeganAt = double.NegativeInfinity;
            return 0;
        }

        double rechargeAt = EnergyShield.NotDelayed(get) ? from : state.LifeInterruptedAt + EnergyShield.Delay(get);
        double start = Math.Max(from, rechargeAt);
        if (now <= start) return 0;
        double rate = EnergyShield.BaseRechargePerMinute(player.PlayerMaxLife, get)
            * Math.Max(0, 1 + get(Stat.LifeRecoveryRatePlusPct) / 100d)
            * PanelStats.MapRecoveryMultiplier(get);
        if (rate > 0 && !double.IsFinite(state.LifeRechargeBeganAt)) state.LifeRechargeBeganAt = start;
        return rate * (now - start) / 60;
    }
}
