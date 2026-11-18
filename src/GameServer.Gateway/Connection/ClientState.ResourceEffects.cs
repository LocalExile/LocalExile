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
    public readonly PhysicalAegisState PhysicalAegis = new();
    public HashSet<ushort> RetiredPhysicalAegisBuffs { get; } = [];
    public readonly UnhingedState Unhinged = new();
    public HashSet<ushort> RetiredUnhingedBuffs { get; } = [];

    public void RetireResourceEffects()
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
