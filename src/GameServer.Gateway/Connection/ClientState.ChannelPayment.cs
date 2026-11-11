using GameServer.Modding;

// A payment receipt is host data. It grants no permission without the current rule/action/skill checks.
public sealed record ChannelPaymentReceipt(ushort Skill, int Effect, int Action, int Generation, IModuleLifetime Rules);

public sealed partial class ClientState
{
    public ChannelPaymentReceipt? PaidChannel;
    public double NonChaosDotBypassCarry;
    public double NonChaosDotTakenCarry;
}
