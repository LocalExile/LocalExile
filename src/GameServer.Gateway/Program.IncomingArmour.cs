using GameServer.Core.World;

partial class Program
{
    static bool ChaosArmourApplies(ClientState conn) =>
        Stats.Key("base_armour_applies_to_chaos_damage") is uint key && CharacterPlayerStats(conn).GetValueOrDefault(key) != 0;

    static string PlayerResistanceDescription(ClientState conn, DamageType type, int resistance, int maximum) =>
        $"{DamageModel.Capped(resistance, maximum)}% resistance" + (type == DamageType.Chaos && ChaosArmourApplies(conn) ? ", armour" : "");

    // Resistance precedes armour. Spell suppression, block, Ward and ES follow this stage.
    // Incorruptible applies armour to chaos hits, without granting physical damage reduction to chaos.
    static int ApplyPlayerArmour(ClientState conn, DamageType type, int afterResistance)
    {
        if (type == DamageType.Physical)
            return DamageModel.MitigatePhysical(afterResistance, conn.Pools.Armour, AdditionalPhysicalReduction(conn));
        if (type != DamageType.Chaos || !ChaosArmourApplies(conn)) return afterResistance;
        return DamageModel.MitigatePhysical(afterResistance, conn.Pools.Armour);
    }
}
