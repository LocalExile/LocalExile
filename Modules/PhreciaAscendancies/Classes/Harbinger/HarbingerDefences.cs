
namespace GameServer.Modules.PhreciaAscendancies.Classes.Harbinger;

/// <summary>Extra base ES from final pools; eligible non-chaos bypass after Ward and before ES.</summary>
static class HarbingerDefences
{
    public static int Taken(int damage, Func<string, int> get) => IncomingResourceDefences.Taken(damage, get);

    public static uint Maximum(uint ordinary, uint life, uint mana, int increased, int lifePercent, int manaPercent)
    {
        return IncomingResourceDefences.Maximum(ordinary, life, mana, increased, lifePercent, manaPercent);
    }

    public static int Damage(EnergyShield shield, int damage, int bypassPercent, double now)
    {
        return IncomingResourceDefences.Damage(shield, damage, bypassPercent, now);
    }

    public static int DamageOverTime(EnergyShield shield, int damage, int bypassPercent, double now,
        ref double bypassCarry)
    {
        return IncomingResourceDefences.DamageOverTime(shield, damage, bypassPercent, now, ref bypassCarry);
    }
}
