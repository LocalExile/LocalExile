using System.Collections.Concurrent;

// Reload-persistent data only: no Game delegate, task, module type or lifetime is retained here.
readonly record struct FlaskAutomationEvent(FlaskEnchantments.Trigger Trigger, uint Item, byte Slot, long Activation = 0);
sealed class FlaskAutomationChain
{
    internal readonly HashSet<uint> Accepted = [];
    internal readonly HashSet<FlaskAutomationEvent> Pending = [];
    internal bool Finished;
}
readonly record struct FlaskAutomationStep(FlaskAutomationEvent Signal, FlaskAutomationChain Chain, bool Root)
{
    internal FlaskEnchantments.Trigger Trigger => Signal.Trigger;
    internal uint Item => Signal.Item;
    internal byte Slot => Signal.Slot;
    internal long Activation => Signal.Activation;
}
enum FlaskOutputPool { None, Life, Mana }
sealed record FlaskOutput(byte[] Packet, string? Log = null, FlaskOutputPool Pool = FlaskOutputPool.None,
    UtilityFlasks.Active? UtilityStart = null)
{
    public static implicit operator FlaskOutput(byte[] packet) => new(packet);
}
sealed class FlaskAutomationState
{
    readonly Lock gate = new();
    internal readonly ConcurrentQueue<FlaskAutomationStep> Events = new();
    readonly HashSet<FlaskAutomationEvent> roots = [];
    internal readonly SemaphoreSlim UseGate = new(1, 1);
    // Presentation receipts and revisions are accessed under Monsters, including at the send gate.
    internal readonly List<FlaskOutput> Packets = [];
    internal long StatRevision, PresentedStatRevision;
    internal void Enqueue(FlaskAutomationEvent signal, FlaskAutomationChain? chain = null)
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
    internal bool CanAccept(FlaskAutomationStep step, uint item)
    {
        lock (gate) return Events.TryPeek(out var head) && head == step && !step.Chain.Finished
            && !step.Chain.Accepted.Contains(item);
    }
    internal void Accepted(FlaskAutomationStep step, uint item)
    {
        lock (gate) step.Chain.Accepted.Add(item);
    }
    internal void Complete(FlaskAutomationStep step)
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
    internal void Discard()
    {
        lock (gate) while (Events.TryPeek(out var head)) Complete(head);
    }
}
sealed partial class ClientState
{
    internal FlaskAutomationState FlaskAutomation { get; } = new();
}
