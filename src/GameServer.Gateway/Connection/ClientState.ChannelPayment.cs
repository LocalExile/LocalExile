using GameServer.Modding;

// A payment receipt is host data. It grants no permission without the current rule/action/skill checks.
sealed record ChannelPaymentReceipt(ushort Skill, int Effect, int Action, int Generation, IModuleLifetime Rules);

sealed partial class ClientState
{
    internal ChannelPaymentReceipt? PaidChannel;
    internal double NonChaosDotBypassCarry;
    internal double NonChaosDotTakenCarry;
    internal double ChaosDotBypassCarry;
}
