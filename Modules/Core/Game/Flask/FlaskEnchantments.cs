using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    sealed class FlaskAutomation
    {
        public readonly ConcurrentQueue<(FlaskEnchantments.Trigger Trigger, uint Item, byte Slot)> Events = new();
        public readonly ConcurrentDictionary<(FlaskEnchantments.Trigger Trigger, uint Item, byte Slot), byte> Pending = new();
        public readonly SemaphoreSlim UseGate = new(1, 1);
    }
    static readonly ConditionalWeakTable<ClientState, FlaskAutomation> FlaskAutomationStates = new();
    static FlaskAutomation Automation(ClientState conn) => FlaskAutomationStates.GetValue(conn, _ => new());

    internal static void QueueFlaskTrigger(ClientState conn, FlaskEnchantments.Trigger trigger, uint item = 0, byte slot = 0)
    {
        if (!conn.Closed && !conn.LeavingArea && conn.PlayerLife > 0)
        {
            var state = Automation(conn);
            if (state.Pending.TryAdd((trigger, item, slot), 0)) state.Events.Enqueue((trigger, item, slot));
        }
    }

    internal static async Task<bool> UseUtilityFlask(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, uint slot, InventoryItem flask, ItemCatalog.BaseType type, UtilityFlasks.Utility utility)
    {
        var state = Automation(conn);
        await state.UseGate.WaitAsync();
        try
        {
            // Re-read after waiting: a move or earlier automatic/manual use may have changed the flask.
            var current = ItemsOf(conn).Items(PlayerInventories.Flask).FirstOrDefault(i => i.Id == flask.Id && i.X == slot);
            if (current is null) return false;
            if (!await UseUtilityFlaskCore(remote, stream, ciphers, conn, slot, current, type, utility)) return false;
            QueueFlaskTrigger(conn, FlaskEnchantments.Trigger.Adjacent, flask.Id, (byte)slot);
            return true;
        }
        finally { state.UseGate.Release(); }
    }

    // Runs from the existing pause-aware flask ticker, outside combat/inventory locks. Each flask is used at most
    // once per event chain, preventing adjacent enchanted flasks from recursively emptying one another.
    internal static async Task DrainFlaskTriggers(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var state = Automation(conn);
        if (conn.Paused || conn.LeavingArea) return;
        if (conn.PlayerLife == 0 || InTown(conn) || InGracePeriod(conn))
        { while (state.Events.TryDequeue(out var discarded)) state.Pending.TryRemove(discarded, out _); return; }
        var used = new HashSet<uint>();
        while (state.Events.TryDequeue(out var signal))
        {
            state.Pending.TryRemove(signal, out _);
            foreach (var flask in ItemsOf(conn).Items(PlayerInventories.Flask).OrderBy(i => i.X))
            {
                if (used.Contains(flask.Id) || signal.Trigger is FlaskEnchantments.Trigger.End or FlaskEnchantments.Trigger.Full
                    && signal.Item != flask.Id) continue;
                if (ItemsFor(conn).ByHash(flask.Item.TypeHash) is not { } type || ItemsFor(conn).UtilityFlaskOf(type) is not { } utility) continue;
                bool active = conn.UtilityFlaskEffects.Of(flask.Id) is { } effect && effect.Ends > conn.Clock.Elapsed.TotalSeconds;
                var local = ItemModStats.Local(flask.Item, ItemsFor(conn).Mods);
                if (!FlaskEnchantments.Matches(local, signal.Trigger, flask.X, signal.Slot, active)) continue;
                // Current charges are authoritative; an event cannot conjure charges or bypass Mageblood/death checks.
                if (signal.Trigger == FlaskEnchantments.Trigger.Full
                    && flask.Item.Charges < FlaskCharges.Of(utility.Charges, local).MaxCharges) continue;
                used.Add(flask.Id);
                await UseUtilityFlask(remote, stream, ciphers, conn, flask.X, flask, type, utility);
            }
        }
    }

    internal static void QueueFlaskAilmentChanges(ClientState conn,
        (bool Ignite, int Chill, bool Freeze, int Shock) before, (bool Ignite, int Chill, bool Freeze, int Shock) after)
    {
        if (!before.Ignite && after.Ignite) QueueFlaskTrigger(conn, FlaskEnchantments.Trigger.Ignite);
        if (before.Chill == 0 && after.Chill > 0) QueueFlaskTrigger(conn, FlaskEnchantments.Trigger.Chill);
        if (!before.Freeze && after.Freeze) QueueFlaskTrigger(conn, FlaskEnchantments.Trigger.Freeze);
        if (before.Shock == 0 && after.Shock > 0) QueueFlaskTrigger(conn, FlaskEnchantments.Trigger.Shock);
    }
}
