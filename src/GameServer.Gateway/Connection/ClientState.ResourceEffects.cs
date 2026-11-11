/// <summary>Optional transfer checkpoints on opaque rule-owned state, under the connection lock.</summary>
interface ITransferCombatState : ITransientCombatState
{
    void Freeze(double at);
    void Resume(double at);
}

sealed partial class ClientState
{
    internal ITransferCombatState? ResourceEffectsState;
    internal ConnectionWorker ResourceEffectsLease { get; } = new("resource-effects");
    internal HashSet<(uint Actor, ushort Buff)> RetiredResourceBuffs { get; } = [];
    internal readonly PhysicalAegisState PhysicalAegis = new();
    internal HashSet<ushort> RetiredPhysicalAegisBuffs { get; } = [];
    internal readonly UnhingedState Unhinged = new();
    internal HashSet<ushort> RetiredUnhingedBuffs { get; } = [];

    internal void RetireResourceEffects()
    {
        lock (Monsters)
        {
            ResourceEffectsLease.Retire();
            foreach (ushort buff in PhysicalAegis.Retire()) RetiredPhysicalAegisBuffs.Add(buff);
            if (Unhinged.Retire() is ushort unhingedBuff and not 0) RetiredUnhingedBuffs.Add(unhingedBuff);
            if (ResourceEffectsState is { } state)
            {
                state.Clear();
                RetiredResourceBuffs.UnionWith(state.RetiredBuffs);
            }
            ResourceEffectsState = null;
        }
    }
}
