using System.Collections.Concurrent;

// Reload-persistent data only: no Game delegate, task, module type or lifetime is retained here.
public readonly record struct FlaskAutomationEvent(FlaskEnchantments.Trigger Trigger, uint Item, byte Slot, long Activation = 0);
public sealed class FlaskAutomationChain
{
    public readonly HashSet<uint> Accepted = [];
    public readonly HashSet<FlaskAutomationEvent> Pending = [];
    public bool Finished;
}
public readonly record struct FlaskAutomationStep(FlaskAutomationEvent Signal, FlaskAutomationChain Chain, bool Root)
{
    public FlaskEnchantments.Trigger Trigger => Signal.Trigger;
    public uint Item => Signal.Item;
    public byte Slot => Signal.Slot;
    public long Activation => Signal.Activation;
}
public enum FlaskOutputPool { None, Life, Mana }
public sealed record FlaskOutput(byte[] Packet, string? Log = null, FlaskOutputPool Pool = FlaskOutputPool.None,
    UtilityFlasks.Active? UtilityStart = null)
{
    public static implicit operator FlaskOutput(byte[] packet) => new(packet);
}
public sealed class FlaskAutomationState
{
    readonly Lock gate = new();
    public readonly ConcurrentQueue<FlaskAutomationStep> Events = new();
    readonly HashSet<FlaskAutomationEvent> roots = [];
    public readonly SemaphoreSlim UseGate = new(1, 1);
    // Presentation receipts and revisions are accessed under Monsters, including at the send gate.
    public readonly List<FlaskOutput> Packets = [];
    public long StatRevision, PresentedStatRevision;
    public void Enqueue(FlaskAutomationEvent signal, FlaskAutomationChain? chain = null)
    {
        lock (gate)
        {
            bool root = chain is null;
            if (root && !roots.Add(signal)) return;
            chain ??= new();
            if (chain.Finished || !chain.Pending.Add(signal)) return;
            Events.Enqueue(new(signal, chain, root));
        }
    }
    // Game calls these while holding Monsters. Its accepted callback also holds FlaskBarGate:
    // the head/progress check, charge deduction and accepted mark form one synchronous transaction.
    public bool CanAccept(FlaskAutomationStep step, uint item)
    {
        lock (gate) return Events.TryPeek(out var head) && head == step && !step.Chain.Finished
            && !step.Chain.Accepted.Contains(item);
    }
    public void Accepted(FlaskAutomationStep step, uint item)
    {
        lock (gate) step.Chain.Accepted.Add(item);
    }
    public void Complete(FlaskAutomationStep step)
    {
        lock (gate)
        {
            if (!Events.TryPeek(out var head) || head != step || !Events.TryDequeue(out _)) return;
            if (step.Root) roots.Remove(step.Signal);
            step.Chain.Pending.Remove(step.Signal);
            if (step.Chain.Pending.Count == 0)
            { step.Chain.Finished = true; step.Chain.Accepted.Clear(); }
        }
    }
    public void Discard()
    {
        lock (gate) while (Events.TryPeek(out var head)) Complete(head);
    }
}
public sealed partial class ClientState
{
    public FlaskAutomationState FlaskAutomation { get; } = new();
}
