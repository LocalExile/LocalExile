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

    internal void RetireResourceEffects()
    {
        lock (Monsters)
        {
            ResourceEffectsLease.Retire();
            if (ResourceEffectsState is { } state)
            {
                state.Clear();
                RetiredResourceBuffs.UnionWith(state.RetiredBuffs);
            }
            ResourceEffectsState = null;
        }
    }
}
