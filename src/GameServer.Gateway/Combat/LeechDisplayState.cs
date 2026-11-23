/// <summary>The invisible BuffDefinitions 38/39/40 leech recovery buffs presented on the player. They carry the full
/// recovery-per-minute and is-*-leeching values so the client can draw the leech overlay on the resource orb; the
/// server's actual pool update may still be capped (for example at Low Life by Petrified Blood).</summary>
public sealed class LeechDisplayState
{
    public ushort LifeBuff, ManaBuff, EnergyShieldBuff;
    public int LifePerMinute, ManaPerMinute, EnergyShieldPerMinute;
    public double LifeExpiry, ManaExpiry, EnergyShieldExpiry;

    public void Clear()
    {
        LifeBuff = ManaBuff = EnergyShieldBuff = 0;
        LifePerMinute = ManaPerMinute = EnergyShieldPerMinute = 0;
        LifeExpiry = ManaExpiry = EnergyShieldExpiry = 0;
    }
}
