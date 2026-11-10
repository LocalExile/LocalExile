/// <summary>Optional transfer checkpoints on opaque rule-owned state, under the connection lock.</summary>
public interface ITransferCombatState : ITransientCombatState
{
    void Freeze(double at);
    void Resume(double at);
}

public sealed partial class ClientState
{
    public ITransferCombatState? ResourceEffectsState;
    public ConnectionWorker ResourceEffectsLease { get; } = new("resource-effects");
    public HashSet<(uint Actor, ushort Buff)> RetiredResourceBuffs { get; } = [];

    public void RetireResourceEffects()
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
