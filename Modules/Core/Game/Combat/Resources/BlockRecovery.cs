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
    static void RecoverOnBlockLocked(Player player, IReadOnlyDictionary<uint, int> stats)
    {
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea) return;
        int Get(uint key) => stats.GetValueOrDefault(key);
        var recovery = BlockRecovery(player, stats);
        var leech = LeechOf(player);
        long life = !LifeRecovery.NonLeechBlocked(Get) ? (long)player.Pools.LifeGainedOnBlock + recovery.Life : 0;
        long mana = (long)player.Pools.ManaGainedOnBlock + recovery.Mana;
        uint shield = Get(Stat.CannotRecoverEnergyShield) == 0 && Get(Stat.NoEnergyShieldRecovery) == 0 ? recovery.EnergyShield : 0;
        if (life > 0) player.PlayerLife = PetrifiedBlood.RecoverLife(player, player.PlayerLife, life, ref leech.LifeInstantCarry, minimum: 1);
        if (mana > 0) player.PlayerMana = RecoveryPool.Advance(player.PlayerMana, (uint)UnreservedMana(player), mana, ref leech.ManaInstantCarry);
        if (shield > 0) player.EnergyShield.Recover(shield);
        TrimLeechLocked(player);
    }
}
