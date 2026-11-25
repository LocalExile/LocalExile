using GameServer.Core.World;

public static partial class Game
{
    // Percent recovery uses the maximum pool, including its reserved part, and rounds down.
    public static (int Life, int Mana, uint EnergyShield) BlockRecovery(Player player, IReadOnlyDictionary<uint, int> stats)
    {
        long Pct(uint key, long maximum) => maximum * Math.Max(0, stats.GetValueOrDefault(key)) / 100;
        return ((int)Pct(Stat.RecoverPctOfMaximumLifeOnBlock, player.PlayerMaxLife), (int)Pct(Stat.ManaPctGainedOnBlock, player.Pools.Mana),
            (uint)Pct(Stat.EnergyShieldPctGainedOnBlock, player.EnergyShield.Maximum));
    }

    // "When you Block" effects occur before the blocked hit's damage, including the damage retained by Glancing Blows:
    // GGG's servers (3.30.0b) send the gained Life and then the hit's damage as two Life updates, and a gain at a full
    // pool is lost. Commit all three pools in the area's frame before routing the hit through ES/Mana/Life. Reaching full here
    // ends leech even though this same hit lowers the pool again: GGG's servers remove the leech buff between those two
    // updates (5 of 5 blocks that filled Life while leeching), and leech runs on after blocks that do not fill it.
    static void RecoverOnBlockLocked(Player player, IReadOnlyDictionary<uint, int> stats, double now, bool attack)
    {
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea) return;
        int Get(uint key) => stats.GetValueOrDefault(key);
        var recovery = BlockRecovery(player, stats);
        var leech = LeechOf(player);
        long life = 0;
        if (!LifeRecovery.NonLeechBlocked(Get))
        {
            life = (long)player.Pools.LifeGainedOnBlock + recovery.Life;
            if (attack && Get(Stat.RecoverPctLifeWhenYouBlockAttackDamageWhileWieldingAStaff) is int staffRecovery and > 0
                && WeaponProfileFor(player, activeSet: true).Staff)
                life += (long)player.PlayerMaxLife * staffRecovery / 100;
        }
        long mana = (long)player.Pools.ManaGainedOnBlock + recovery.Mana;
        uint shield = 0;
        if (Get(Stat.CannotRecoverEnergyShield) == 0 && Get(Stat.NoEnergyShieldRecovery) == 0)
        {
            long shieldTotal = recovery.EnergyShield;
            if (!attack && Get(Stat.RecoverPctEnergyShieldWhenYouBlockSpellDamageWhileWieldingAStaff) is int staffEs and > 0
                && WeaponProfileFor(player, activeSet: true).Staff)
                shieldTotal += (long)player.EnergyShield.Maximum * staffEs / 100;
            shield = (uint)Math.Min(uint.MaxValue, shieldTotal);
        }
        if (life > 0) player.PlayerLife = PetrifiedBlood.RecoverLife(player, player.PlayerLife, life, ref leech.LifeInstantCarry, minimum: 1);
        if (mana > 0) player.PlayerMana = RecoveryPool.Advance(player.PlayerMana, (uint)UnreservedMana(player), mana, ref leech.ManaInstantCarry);
        if (shield > 0) player.EnergyShield.Recover(shield);
        if (RandomRolls.Chance(Get(Stat.EnergyShieldRechargesOnBlockPct), 100, player.IncomingHitRandom))
            player.EnergyShield.StartRecharge(now, Get);
        TrimLeechLocked(player);
    }
}
