public static partial class Game
{
    // Percent recovery uses the maximum pool, including its reserved part, and rounds down.
    public static (int Life, int Mana, uint EnergyShield) BlockRecovery(ClientState conn, IReadOnlyDictionary<uint, int> stats)
    {
        long Pct(string name, long maximum) => Stats.Key(name) is uint key ? maximum * Math.Max(0, stats.GetValueOrDefault(key)) / 100 : 0;
        return ((int)Pct("recover_%_of_maximum_life_on_block", conn.PlayerMaxLife), (int)Pct("mana_%_gained_on_block", conn.Pools.Mana),
            (uint)Pct("energy_shield_%_gained_on_block", conn.EnergyShield.Maximum));
    }

    // On-block recovery precedes damage, including the damage retained by Glancing Blows. Commit all three pools
    // under Monsters before routing the hit through ES/Mana/Life; recovery at a full pool cannot offset that hit.
    static void RecoverOnBlockLocked(ClientState conn, IReadOnlyDictionary<uint, int> stats)
    {
        if (conn.PlayerLife == 0 || conn.Closed || conn.LeavingArea) return;
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        var recovery = BlockRecovery(conn, stats);
        var leech = LeechOf(conn);
        if (Get("cannot_recover_life") == 0)
            conn.PlayerLife = PetrifiedBlood.RecoverLife(conn, conn.PlayerLife,
                Math.Max(0L, (long)conn.Pools.LifeGainedOnBlock + recovery.Life), ref leech.LifeInstantCarry, minimum: 1);
        conn.PlayerMana = RecoveryPool.Advance(conn.PlayerMana, (uint)UnreservedMana(conn),
            Math.Max(0L, (long)conn.Pools.ManaGainedOnBlock + recovery.Mana), ref leech.ManaInstantCarry);
        if (Get("cannot_recover_energy_shield") == 0 && Get("no_energy_shield_recovery") == 0)
            conn.EnergyShield.Recover(recovery.EnergyShield);
        // Reaching full ends ordinary leech even if this same hit immediately lowers the pool again.
        TrimLeechLocked(conn);
    }
}
