using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    static FlaskAutomationState Automation(ClientState conn) => conn.FlaskAutomation;

    public static void QueueFlaskTrigger(ClientState conn, FlaskEnchantments.Trigger trigger, uint item = 0, byte slot = 0)
    {
        lock (conn.Monsters)
        {
            if (CurrentGameWork && !conn.Closed && !conn.LeavingArea && conn.PlayerLife > 0)
                Automation(conn).Enqueue(new(trigger, item, slot));
        }
    }

    public static Task<bool> UseUtilityFlask(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, uint slot, InventoryItem flask, ItemCatalog.BaseType type, UtilityFlasks.Utility utility)
        => UseUtilityFlaskForSignal(remote, stream, ciphers, conn, slot, flask, type, utility, null);

    static async Task<bool> UseUtilityFlaskForSignal(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, uint slot, InventoryItem flask, ItemCatalog.BaseType type, UtilityFlasks.Utility utility,
        FlaskAutomationStep? signal)
    {
        var state = Automation(conn);
        var owner = CombatLifetime;
        await state.UseGate.WaitAsync();
        try
        {
            // Re-read after waiting: a move or earlier automatic/manual use may have changed the flask.
            var current = ItemsOf(conn).Items(PlayerInventories.Flask).FirstOrDefault(i => i.Id == flask.Id && i.X == slot);
            if (!CurrentGameWork || !ReferenceEquals(owner, CombatLifetime) || current is null) return false;
            return await UseUtilityFlaskCore(remote, stream, ciphers, conn, slot, current, type, utility, signal);
        }
        finally { state.UseGate.Release(); }
    }

    // Runs from the existing pause-aware flask ticker, outside combat/inventory locks. Each flask is used at most
    // once per event chain, preventing adjacent enchanted flasks from recursively emptying one another.
    public static async Task DrainFlaskTriggers(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var state = Automation(conn);
        if (!CurrentGameWork || conn.Paused || conn.LeavingArea) return;
        lock (conn.Monsters)
        {
            if (!CurrentGameWork) return;
            if (conn.PlayerLife == 0 || InTown(conn) || InGracePeriod(conn))
            { state.Discard(); return; }
        }
        while (CurrentGameWork && state.Events.TryPeek(out var signal))
        {
            foreach (var flask in ItemsOf(conn).Items(PlayerInventories.Flask).OrderBy(i => i.X))
            {
                lock (conn.Monsters)
                    if (!CurrentGameWork || !state.CanAccept(signal, flask.Id)) continue;
                if (signal.Trigger is FlaskEnchantments.Trigger.End or FlaskEnchantments.Trigger.Full
                    && signal.Item != flask.Id) continue;
                if (!EndSignalCurrent(conn, flask, signal)) continue;
                if (ItemsFor(conn).ByHash(flask.Item.TypeHash) is not { } type || ItemsFor(conn).UtilityFlaskOf(type) is not { } utility) continue;
                bool active = conn.UtilityFlaskEffects.Of(flask.Id) is { } effect && effect.Ends > AreaTime(conn);
                var local = ItemModStats.Local(flask.Item, ItemsFor(conn).Mods);
                if (!FlaskEnchantments.Matches(local, signal.Trigger, flask.X, signal.Slot, active)) continue;
                // Current charges are authoritative; an event cannot conjure charges or bypass Mageblood/death checks.
                if (signal.Trigger == FlaskEnchantments.Trigger.Full
                    && flask.Item.Charges < FlaskCharges.Of(utility.Charges, local).MaxCharges) continue;
                await UseUtilityFlaskForSignal(remote, stream, ciphers, conn, flask.X, flask, type, utility, signal);
                if (!CurrentGameWork) return; // Leave an unfinished edge for the replacement drainer.
            }
            lock (conn.Monsters)
            {
                if (!CurrentGameWork) return;
                state.Complete(signal);
            }
        }
    }

    static bool EndSignalCurrent(ClientState conn, InventoryItem flask, FlaskAutomationStep? signal) =>
        signal is not { Signal: { Trigger: FlaskEnchantments.Trigger.End } end }
        || flask.Id == end.Item && flask.X == end.Slot && conn.UtilityFlaskEffects.IsLatest(end.Item, end.Activation)
            && conn.UtilityFlaskEffects.Of(end.Item) is null
            && FlaskEnchantments.Matches(ItemModStats.Local(flask.Item, ItemsFor(conn).Mods), end.Trigger,
                flask.X, end.Slot, active: false);

    public static void QueueFlaskAilmentChanges(ClientState conn,
        (bool Ignite, int Chill, bool Freeze, int Shock) before, (bool Ignite, int Chill, bool Freeze, int Shock) after)
    {
        if (!before.Ignite && after.Ignite) QueueFlaskTrigger(conn, FlaskEnchantments.Trigger.Ignite);
        if (before.Chill == 0 && after.Chill > 0) QueueFlaskTrigger(conn, FlaskEnchantments.Trigger.Chill);
        if (!before.Freeze && after.Freeze) QueueFlaskTrigger(conn, FlaskEnchantments.Trigger.Freeze);
        if (before.Shock == 0 && after.Shock > 0) QueueFlaskTrigger(conn, FlaskEnchantments.Trigger.Shock);
    }
}
