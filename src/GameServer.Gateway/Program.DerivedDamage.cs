partial class Program
{
    // The actual hostile RF tick and its tests share this calculation. The owner's generic minion-damage transfer
    // affects burning too; spell-to-attack, defence-to-attack and final Rage damage stay scoped to attacks.
    internal static double RighteousFireDamagePerSecond(ClientState conn, RighteousFireState rf)
    {
        double perSecond = (conn.PlayerMaxLife * (double)rf.DamagePctPerMinute
            + conn.Pools.EnergyShield * (double)rf.EnergyShieldDamagePctPerMinute) / 6000.0;
        var playerStats = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? playerStats.GetValueOrDefault(key) : 0;
        if (!SkillRestrictions.From(Get).DealsDamage(spell: true)) return 0;
        perSecond *= Math.Max(0, 1 + Get("delirium_aura_damage_over_time_+%_final") / 100.0);
        return perSecond * Math.Max(0, 1 + (DerivedDamageIncreases.From(Get).GenericPercent + Get("damage_+%")
            + Get("fire_damage_+%") + Get("elemental_damage_+%") + Get("burn_damage_+%")
            + Get("damage_over_time_+%")) / 100.0) * GamblerFor(playerStats).DamageFactor;
    }
}
