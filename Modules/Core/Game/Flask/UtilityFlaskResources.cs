using GameServer.Core.World;

public static partial class Game
{
    static bool ApplyUtilityResourceEffectsLocked(Player player, IReadOnlyDictionary<uint, int> local,
        int spent, double now, double effectPower, Func<uint, int> character)
    {
        double lifeRecovery = 0;
        if (local.GetValueOrDefault(Stat.LocalUniqueFlaskInstantlyRecoversPctMaximumLife) is > 0 and var heal)
            lifeRecovery = Math.Floor(player.PlayerMaxLife * (double)heal * effectPower / 100);
        if (local.GetValueOrDefault(Stat.LocalFlaskLoseAllEnduranceChargesAndRecoverPctLifeForEachOnUse) is > 0 and var healPerCharge)
        {
            int consumed = SpendCharacterChargesOnFlaskUse(player, ChargeType.Endurance, int.MaxValue, now);
            if (consumed > 0 && player.PlayerLife > 0 && !LifeRecovery.NonLeechBlocked(character))
                lifeRecovery += Math.Floor(player.PlayerMaxLife * (double)consumed * healPerCharge / 100);
        }
        if (lifeRecovery > 0)
        {
            uint life = (uint)Math.Min(LifeCap(player), player.PlayerLife + lifeRecovery);
            if (life > player.PlayerLife)
            {
                player.PlayerLife = life;
                Automation(player).Packets.Add(new([], $"player life {life} (flask)", FlaskOutputPool.Life));
            }
        }
        if (local.GetValueOrDefault(Stat.LocalFlaskGainChargesConsumedAsVaalSoulsOnUse) == 0
            || player.VaalSoulsRetired || player.VaalPreventionUntil > VaalResourceTime(player)
            || VaalImmortalCallActive(player, now)
            || CharacterPlayerStats(player).GetValueOrDefault(Stat.HasSoulGainPrevention) != 0) return false;
        RefreshVaalSouls(player);
        int gained = 0;
        while (gained < spent && player.VaalSouls.Gain()) gained++;
        return gained > 0;
    }

    static void AccrueUtilityFlaskDamage(Player player, double now)
    {
        var effects = player.UtilityFlaskEffects;
        var progress = effects.SelfDamage;
        double from = progress.Through;
        if (now <= from) return;
        progress.Through = now;
        if (double.IsNaN(from) || !effects.HasSelfDamage) return;
        var active = effects.SelfDamageEffects();
        // Equal flask effects overlap at their strongest rate. Every start and expiry splits the elapsed interval.
        while (from < now)
        {
            int rate = 0;
            double next = now;
            foreach (var effect in active)
            {
                if (effect.Ends <= from) continue;
                if (effect.Started > from) next = Math.Min(next, effect.Started);
                else
                {
                    next = Math.Min(next, effect.Ends);
                    rate = Math.Max(rate, effect.SelfChaosDamageLifePctPerMinute);
                }
            }
            progress.Pending += player.PlayerMaxLife * (double)rate / 6000 * (next - from);
            from = next;
        }
    }

    static void ClearUtilityFlaskDamage(Player player, double now)
    {
        var progress = player.UtilityFlaskEffects.SelfDamage;
        progress.Through = now;
        progress.Pending = progress.TakenCarry = progress.BypassCarry = 0;
    }

    static void AdvanceUtilityFlaskChaosDamageLocked(Player player, double now, IReadOnlyDictionary<uint, int> stats)
    {
        AccrueUtilityFlaskDamage(player, now);
        var progress = player.UtilityFlaskEffects.SelfDamage;
        double raw = progress.Pending;
        progress.Pending = 0;
        const DamageType type = DamageType.Chaos;
        if (raw <= 0 || player.Closed || player.LeavingArea || player.Immune || InGracePeriod(player) || InTown(player)
            || HardcoreProgressBlocked(player) || PlayerImmunities(player, stats).Contains(type)) return;
        int Get(uint stat) => stats.GetValueOrDefault(stat);
        double resisted = DamageModel.Mitigate(raw, Resistance(player, type), MaxResistance(player, type));
        int damage = IncomingResourceDefences.TakenOverTime(resisted, Get, ref progress.TakenCarry,
            IncomingResourceDefences.LessTakenPct(player.Pools, Get), typedIncreasedPct: Get(Stat.ChaosDamageTakenPlusPct),
            finalTakenPct: Get(IncomingResourceDefences.UnhingedSaneDamageFinal));
        int life = DamagePlayerChaosOverTime(player, damage, now, stats);
        player.PlayerLife = VaalImmortalCallLife(player, (uint)Math.Max(0, (long)player.PlayerLife - life), now);
        if (player.PlayerLife > 0) return;
        ClearUtilityFlaskDamage(player, now);
        player.InvalidateIncomingHits();
        RecordHardcoreDeath(player);
    }
}
