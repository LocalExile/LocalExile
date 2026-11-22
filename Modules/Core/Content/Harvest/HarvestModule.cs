using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Harvest;

/// <summary>Horticrafting (the station in the hideout, its recipe browser and its crafts), the Sacred Grove of a map
/// (<see cref="SacredGrove"/>) and Oshabi's fight (<see cref="OshabiEncounter"/>).</summary>
public sealed class HarvestModule : IModule, IDisposable
{
    const string StationType = "Metadata/MiscellaneousObjects/Harvest/CraftingSilo_Hideout";
    const string Window = "harvest";
    /// <summary>The client's HarvestCraftingItem inventory row.</summary>
    public const byte Slot = 43;

    sealed class State { public uint Station; }
    readonly ConditionalWeakTable<IPlayer, State> _states = new();
    readonly ConditionalWeakTable<ItemCatalog, HarvestCrafting> _recipes = new();

    public void Dispose()
    {
        _states.Clear();
        _recipes.Clear();
    }

    sealed record SlotOutcome(uint RemovedId, InventoryItem? Changed, IReadOnlyList<ItemMoves.Price> Payments);

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        GameEvents.HideoutReady.Add(module.Manifest.Id, async conn =>
        {
            var player = PlayerOf(conn);
            if (await WorldOf(player).SpawnHideoutStation(HideoutSlot.Large, StationType, "harvest", "Horticrafting Station") is { } id)
                _states.GetOrCreateValue(player).Station = id;
        });
        GameEvents.FeatureFilled.Add(module.Manifest.Id, (_, row) => SacredGrove.FeatureRows.Contains(row));
        GameEvents.MapOpening.Add(module.Manifest.Id, SacredGrove.Opening);
        GameEvents.AreaGenerated.Add(module.Manifest.Id, async conn =>
        {
            await SacredGrove.Enter(conn);
            await OshabiEncounter.Enter(conn);
        });
        GameEvents.Tick.Add(module.Manifest.Id, async (conn, _) =>
        {
            await SacredGrove.Tick(conn);
            await OshabiEncounter.Tick(conn);
        });
        GameEvents.Drops.Add(module.Manifest.Id, drop =>
        {
            HarvestLifeforce.Drops(drop);
            OshabiEncounter.Drops(drop);
        });
        module.Lifetime.Own(OshabiEncounter.CleanupOnUnload());
        GameEvents.Experience.Add(module.Manifest.Id, HarvestLifeforce.Experience);
        // The grove monsters' more life: HarvestLifeScalingPerLevel and the Doubling scarab's.
        module.Lifetime.Own(MonsterModEffects.HandOverMore([SacredGrove.LevelMoreLife, SacredGrove.SextantMoreLife], []));
        GameEvents.ObjectUse.Add(module.Manifest.Id, async (conn, use, at) =>
        {
            if (!_states.TryGetValue(PlayerOf(conn), out var state) || state.Station == 0 || use.TargetId != state.Station)
                return await SacredGrove.Use(conn, use, at) || await OshabiEncounter.Use(conn, use, at);
            var world = WorldOf(conn);
            if (await world.WalkUpToStation(state.Station, at))
                await world.OpenWindow(Window, InstanceWaypoints.HorticraftingUi, "0x007b open Horticrafting Station recipe browser");
            return true;
        });
        GameEvents.Packet(InstanceHarvest.CraftId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            await Craft(module, conn, PlayerOf(conn), InstanceHarvest.ReadCraft(bytes), LeagueFor(conn).HardMode);
            return true;
        });
    }

    /// <summary>C→S 0x274: execute the HarvestCraftOptions recipe whose HASH16 the native panel sent, on the item in the slot.
    /// The item (replacement or consumption, e.g. DivinationCardGamble rolling 0) and all lifeforce costs commit together;
    /// a refusal spends nothing.</summary>
    async Task Craft(IModuleContext module, ClientState conn, IPlayer player, ushort recipe, bool hardMode)
    {
        var world = WorldOf(player);
        if (world.OpenWindowOwner != Window || !_states.TryGetValue(player, out var state) || state.Station == 0) return;
        // ItemsFor(conn) is one catalog instance per game mode, so the cached recipes keep the mode they were made for.
        var catalog = ItemsFor(conn);
        var rolls = RollsFor(conn);
        var harvest = _recipes.GetValue(catalog, c => new HarvestCrafting(c, rolls, hardMode: hardMode));
        string name = harvest.ByHash(recipe)?.Id ?? $"0x{recipe:x4}";

        var items = ItemsOf(conn);
        var stash = PersonalStashOf(conn);
        var result = ExecuteSlotCraft(items, stash, Slot, item => harvest.ApplyHarvest(recipe, item, Random.Shared));
        if (result is null)
        {
            module.Log($"{player.Character} recipe {name} refused (item, recipe or lifeforce)");
            await player.Send(InstanceHarvest.CraftResult((ushort)world.ItemIdIn(Slot)), "0x0276 Harvest item event (refused)");
            await player.Tell("Harvest craft refused: incompatible item or insufficient lifeforce.");
            return;
        }

        uint inventory = PlayerInventories.InventoryId(Slot);
        if (result.Changed is { } crafted)
        {
            // Clear and refill the slot in separate changes: the native panel watching the slot sees the item replaced.
            await Send(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers,
                InstanceItems.InventoryChange(inventory, items.NextRevision(Slot), [crafted.Id], []),
                $"Harvest recipe {name}: remove crafted item {crafted.Id} from the slot");
            await Send(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers,
                InstanceItems.InventoryChange(inventory, items.NextRevision(Slot), [], [crafted]),
                $"Harvest recipe {name}: crafted item {crafted.Id}");
        }
        else
        {
            await Send(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers,
                InstanceItems.InventoryChange(inventory, items.NextRevision(Slot), [result.RemovedId], []),
                $"Harvest recipe {name}: consumed item {result.RemovedId} from the slot");
        }

        foreach (var payment in result.Payments)
        {
            if (payment.Inventory is { } paid)
                await Send(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers,
                    InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                        items.NextRevision(PlayerInventories.MainInventory), [.. paid.Removed, .. paid.Changed.Select(c => c.Id)], paid.Changed),
                    $"Harvest recipe {name}: paid from inventory");
            await SendStashPayment(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, stash, payment.Stash);
        }

        // The slot item's 0x276 event (3.29.3c 0x1404915b0) clears the panel's pending state and refreshes the UI
        // (itemId == 0 clears pending state cleanly when the craft destroyed the item).
        ushort eventItem = result.Changed is { } kept ? (ushort)kept.Id : (ushort)0;
        await player.Send(InstanceHarvest.CraftResult(eventItem), $"0x0276 Harvest item event {eventItem} ({name})");
    }

    static SlotOutcome? ExecuteSlotCraft(PlayerInventories items, AccountStash stash, byte row,
        Func<Item, HarvestCrafting.HarvestOutcome?> craftIt) => Saves.Atomically<SlotOutcome?>(() =>
    {
        var targets = items.Items(row);
        if (targets.Count != 1 || craftIt(targets[0].Item) is not { } outcome) return null;
        foreach (var (currency, amount) in outcome.Costs)
            if (amount < 0 || (long)items.Count(PlayerInventories.MainInventory, currency) + stash.CountOf(currency) < amount)
                return null;
        var payments = new List<ItemMoves.Price>(outcome.Costs.Count);
        foreach (var (currency, amount) in outcome.Costs)
            if (amount > 0) payments.Add(Pay(items, stash, currency, amount)
                ?? throw new InvalidOperationException("Harvest payment changed after validation"));
        if (outcome.Item is { } nextItem)
        {
            var changed = items.Change(row, targets[0].Id, _ => nextItem)
                ?? throw new InvalidOperationException("Harvest item disappeared after payment");
            return new(targets[0].Id, changed, payments);
        }
        if (items.Remove(row, targets[0].Id) is null)
            throw new InvalidOperationException("Harvest item disappeared after payment");
        return new(targets[0].Id, null, payments);
    });

    static ItemMoves.Price? Pay(PlayerInventories items, AccountStash stash, uint currency, int amount)
    {
        int inInventory = items.Count(PlayerInventories.MainInventory, currency), inStash = stash.CountOf(currency);
        if ((long)inInventory + inStash < amount) return null;
        int fromInventory = Math.Min(amount, inInventory);
        var paid = fromInventory > 0 ? items.Pay(PlayerInventories.MainInventory, currency, fromInventory) : null;
        var fromStash = amount > fromInventory ? stash.Pay(currency, amount - fromInventory) : null;
        return new ItemMoves.Price(paid, fromStash);
    }
}
