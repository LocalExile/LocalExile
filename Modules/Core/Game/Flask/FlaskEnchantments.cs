using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    static FlaskAutomationState Automation(Player player) => player.FlaskAutomation;

    public static void QueueFlaskTrigger(Player player, FlaskEnchantments.Trigger trigger, uint item = 0, byte slot = 0)
    {
        if (CurrentGameWork && !player.Closed && !player.LeavingArea && player.PlayerLife > 0)
        {
            Automation(player).Enqueue(new(trigger, item, slot));
            PresentStats(player);
        }
    }

    public static Task<bool> UseUtilityFlask(Player player, uint slot, InventoryItem flask, ItemCatalog.BaseType type, UtilityFlasks.Utility utility)
        => UseUtilityFlaskForSignal(player, slot, flask, type, utility, null);

    static async Task<bool> UseUtilityFlaskForSignal(Player player, uint slot, InventoryItem flask, ItemCatalog.BaseType type, UtilityFlasks.Utility utility,
        FlaskAutomationStep? signal)
    {
        var state = Automation(player);
        var owner = CombatLifetime;
        await state.UseGate.WaitAsync();
        try
        {
            // Re-read after waiting: a move or earlier automatic/manual use may have changed the flask.
            var current = ItemsOf(player).Items(PlayerInventories.Flask).FirstOrDefault(i => i.Id == flask.Id && i.X == slot);
            if (!CurrentGameWork || !ReferenceEquals(owner, CombatLifetime) || current is null) return false;
            return await UseUtilityFlaskCore(player, slot, current, type, utility, signal);
        }
        finally { state.UseGate.Release(); }
    }

    // "Used when Charges reach full" (local_flask_use_on_full_charges) is a condition on the charges a flask HOLDS, not
    // only on a charge change: the official captures of a Pathfinder with five such flasks enter the area with all five
    // at their maximum and use all five together shortly after the player's first input - before the first hit on a
    // monster and before the first kill, so with no charge change at all. A charge change can never carry the entry
    // case: a flask that is already full never rises to full again. Checked on each drain of the flask triggers, so a
    // use the town, the grace period, death or an area change held back happens on the first drain that admits it.
    public static void QueueFullFlasks(Player player)
    {
        IReadOnlyDictionary<uint, int>? character = null;
        foreach (var flask in ItemsOf(player).Items(PlayerInventories.Flask).OrderBy(i => i.X))
        {
            if (ItemsFor(player).ByHash(flask.Item.TypeHash) is not { } type
                || ItemsFor(player).UtilityFlaskOf(type) is not { } utility) continue;
            var local = FlaskLocalStats(player, flask.Item);
            if (!FlaskEnchantments.Matches(local, FlaskEnchantments.Trigger.Full, flask.X, flask.X, active: false)) continue;
            var charges = FlaskCharges.Of(utility.Charges, local, key => (character ??= CharacterPlayerStats(player)).GetValueOrDefault(key));
            // A use that took no charges, or one Mageblood refuses, would leave the flask at its maximum, and a
            // standing condition would then use it on every tick.
            if (charges.ChargesPerUse <= 0 || charges.ChargesPerUse > flask.Item.Charges || flask.Item.Charges < charges.MaxCharges
                || MagebloodBlocksFlaskUse(player, flask.Item)) continue;
            if (Automation(player).Enqueue(new(FlaskEnchantments.Trigger.Full, flask.Id, flask.X)))
                Console.WriteLine($"[gateway] {player.Connection.Remote} flask {flask.X} ({type.Name}) holds {flask.Item.Charges}/{charges.MaxCharges} charges "
                    + $"with local_flask_use_on_full_charges: automatic use queued (takes {charges.ChargesPerUse})");
        }
    }

    // Runs as a stat presentation after a trigger was queued (QueueFlaskTrigger), outside combat/inventory locks. Each flask is used at most
    // once per event chain, preventing adjacent enchanted flasks from recursively emptying one another.
    public static async Task DrainFlaskTriggers(Player player)
    {
        var state = Automation(player);
        if (!CurrentGameWork || player.Paused || player.LeavingArea) return;
        if (!CurrentGameWork) return;
        if (player.PlayerLife == 0 || InTown(player) || InGracePeriod(player))
        { state.Discard(); return; }
        QueueFullFlasks(player);
        while (CurrentGameWork && state.Events.TryPeek(out var signal))
        {
            foreach (var flask in ItemsOf(player).Items(PlayerInventories.Flask).OrderBy(i => i.X))
            {
                if (!CurrentGameWork || !state.CanAccept(signal, flask.Id)) continue;
                if (signal.Trigger is FlaskEnchantments.Trigger.End or FlaskEnchantments.Trigger.Full
                    && signal.Item != flask.Id) continue;
                if (!EndSignalCurrent(player, flask, signal)) continue;
                if (ItemsFor(player).ByHash(flask.Item.TypeHash) is not { } type || ItemsFor(player).UtilityFlaskOf(type) is not { } utility) continue;
                bool active = player.UtilityFlaskEffects.Of(flask.Id) is { } effect && effect.Ends > AreaTime(player);
                var local = ItemModStats.Local(flask.Item, ItemsFor(player).Mods);
                if (!FlaskEnchantments.Matches(local, signal.Trigger, flask.X, signal.Slot, active)) continue;
                // Current charges are authoritative; an event cannot conjure charges or bypass Mageblood/death checks.
                if (signal.Trigger == FlaskEnchantments.Trigger.Full
                    && flask.Item.Charges < FlaskCharges.Of(utility.Charges, local).MaxCharges) continue;
                await UseUtilityFlaskForSignal(player, flask.X, flask, type, utility, signal);
                if (!CurrentGameWork) return; // Leave an unfinished edge for the replacement drainer.
            }
            if (!CurrentGameWork) return;
            state.Complete(signal);
        }
    }

    static bool EndSignalCurrent(Player player, InventoryItem flask, FlaskAutomationStep? signal) =>
        signal is not { Signal: { Trigger: FlaskEnchantments.Trigger.End } end }
        || flask.Id == end.Item && flask.X == end.Slot && player.UtilityFlaskEffects.IsLatest(end.Item, end.Activation)
            && player.UtilityFlaskEffects.Of(end.Item) is null
            && FlaskEnchantments.Matches(ItemModStats.Local(flask.Item, ItemsFor(player).Mods), end.Trigger,
                flask.X, end.Slot, active: false);

    public static void QueueFlaskAilmentChanges(Player player,
        (bool Ignite, int Chill, bool Freeze, int Shock) before, (bool Ignite, int Chill, bool Freeze, int Shock) after)
    {
        if (!before.Ignite && after.Ignite) QueueFlaskTrigger(player, FlaskEnchantments.Trigger.Ignite);
        if (before.Chill == 0 && after.Chill > 0) QueueFlaskTrigger(player, FlaskEnchantments.Trigger.Chill);
        if (!before.Freeze && after.Freeze) QueueFlaskTrigger(player, FlaskEnchantments.Trigger.Freeze);
        if (before.Shock == 0 && after.Shock > 0) QueueFlaskTrigger(player, FlaskEnchantments.Trigger.Shock);
    }
}
