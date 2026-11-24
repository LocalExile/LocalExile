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

    public static void LoadData()
    {
        HarvestTables.LoadGameData();
        SacredGrove.LoadGameData();
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        GameEvents.HideoutReady.Add(module.Manifest.Id, async player =>
        {
            var modPlayer = PlayerOf(player);
            if (await WorldOf(modPlayer).SpawnHideoutStation(HideoutSlot.Large, StationType, "harvest", "Horticrafting Station") is { } id)
                _states.GetOrCreateValue(modPlayer).Station = id;
        });
        GameEvents.FeatureFilled.Add(module.Manifest.Id, (_, row) => SacredGrove.FeatureRows.Contains(row));
        GameEvents.MapOpening.Add(module.Manifest.Id, SacredGrove.Opening);
        GameEvents.PlayerEntered.Add(module.Manifest.Id, async player =>
        {
            await SacredGrove.Enter(player);
            await OshabiEncounter.Enter(player);
        });
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always(async (player, _) =>
        {
            await SacredGrove.Tick(player);
            await OshabiEncounter.Tick(player);
        }));
        GameEvents.Drops.Add(module.Manifest.Id, drop =>
        {
            HarvestLifeforce.Drops(drop);
            OshabiEncounter.Drops(drop);
        });
        module.Lifetime.Own(OshabiEncounter.CleanupOnUnload());
        GameEvents.Experience.Add(module.Manifest.Id, HarvestLifeforce.Experience);
        // The grove monsters' more life: HarvestLifeScalingPerLevel and the Doubling scarab's.
        module.Lifetime.Own(MonsterModEffects.HandOverMore([SacredGrove.LevelMoreLife, SacredGrove.SextantMoreLife], []));
        GameEvents.ObjectUse.Add(module.Manifest.Id, async (player, use, at) =>
        {
            if (!_states.TryGetValue(PlayerOf(player), out var state) || state.Station == 0 || use.TargetId != state.Station)
                return await SacredGrove.Use(player, use, at) || await OshabiEncounter.Use(player, use, at);
            var world = WorldOf(player);
            if (await world.WalkUpToStation(state.Station, at))
                await world.OpenWindow(Window, InstanceWaypoints.HorticraftingUi, "0x007b open Horticrafting Station recipe browser");
            return true;
        });
        GameEvents.Packet(InstanceHarvest.CraftId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await Craft(module, player, InstanceHarvest.ReadCraft(bytes), LeagueFor(player).HardMode);
            return true;
        });
        GameEvents.Packet(InstanceHarvest.QuickMoveItemId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            return await QuickMoveItem(player, InstanceHarvest.ReadQuickMoveItem(bytes));
        });
        GameEvents.Packet(InstanceHarvest.MoveItemId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            return await MoveItem(player, InstanceHarvest.ReadMoveItem(bytes));
        });
        GameEvents.Packet(InstanceHarvest.ActivateCropId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            if (bytes.Length < InstanceHarvest.ActivateCropLength) return false;
            return await SacredGrove.ActivateFromLabel(player, InstanceHarvest.ReadActivateCrop(bytes));
        });
    }

    /// <summary>C→S 0xf8: Ctrl + click an item between main/expanded inventory and HarvestCraftingItem (row 43).</summary>
    async Task<bool> QuickMoveItem(Player player, InstanceItems.LeagueWindowMove request)
    {
        if (WorldOf(player).OpenWindowOwner != Window) return false;
        var items = ItemsOf(player);
        if (ItemMoves.MoveToSlot(items, Slot, request.ItemId, request.Into, ShapeOf) is { } moved)
        {
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(moved.From),
                items.NextRevision(moved.From), [request.ItemId], []),
                $"0x00f8 Ctrl + click: item {request.ItemId} leaves row {moved.From}");
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(moved.To),
                items.NextRevision(moved.To), [], [moved.Placed]),
                $"0x00f8 Ctrl + click: item {request.ItemId} into row {moved.To}");
            return true;
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00f8 item {request.ItemId} {(request.Into ? "into" : "out of")} Horticrafting slot refused (missing, slot taken or no room)");
        return true;
    }

    /// <summary>C→S 0xf9: Ctrl + click an item between inventory/stash and HarvestCraftingItem (row 43).</summary>
    async Task<bool> MoveItem(Player player, InstanceHarvest.MoveItem request)
    {
        if (WorldOf(player).OpenWindowOwner != Window) return false;
        var items = ItemsOf(player);
        if (request.IntoStation)
        {
            if (ItemMoves.MoveToSlot(items, Slot, request.ItemId, intoSlot: true, ShapeOf) is { } moved)
            {
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(moved.From),
                    items.NextRevision(moved.From), [request.ItemId], []), $"0x00f9 Ctrl + click: item {request.ItemId} leaves row {moved.From}");
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(moved.To),
                    items.NextRevision(moved.To), [], [moved.Placed]), $"0x00f9 Ctrl + click: item {request.ItemId} into Horticrafting slot");
                return true;
            }
            if (player.StashOpen)
            {
                var stash = PersonalStashOf(player);
                if (request.Tab < stash.Count && ItemMoves.StashToSlot(stash, items, request.Tab, Slot, request.ItemId, ShapeOf) is { } fromStash)
                {
                    await SendTabChange(player, stash, request.Tab, [fromStash.Id], [],
                        $"0x00f9 stash tab {request.Tab}: remove item {fromStash.Id} into Horticrafting slot");
                    await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(Slot),
                        items.NextRevision(Slot), [], [fromStash]), $"0x00f9 Ctrl + click: item {fromStash.Id} into Horticrafting slot");
                    return true;
                }
            }
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00f9 item {request.ItemId} into Horticrafting slot refused (missing or slot taken)");
            return true;
        }
        if (ItemMoves.MoveToSlot(items, Slot, request.ItemId, intoSlot: false, ShapeOf) is { } takenOut)
        {
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(takenOut.From),
                items.NextRevision(takenOut.From), [request.ItemId], []), $"0x00f9 Ctrl + click: item {request.ItemId} leaves Horticrafting slot");
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(takenOut.To),
                items.NextRevision(takenOut.To), [], [takenOut.Placed]), $"0x00f9 Ctrl + click: item {request.ItemId} into row {takenOut.To}");
            return true;
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00f9 item {request.ItemId} out of Horticrafting slot refused (missing or no room)");
        return true;
    }

    /// <summary>C→S 0x274: execute the HarvestCraftOptions recipe whose HASH16 the native panel sent, on the item in the slot.
    /// The item (replacement or consumption, e.g. DivinationCardGamble rolling 0) and all lifeforce costs commit together;
    /// a refusal spends nothing.</summary>
    async Task Craft(IModuleContext module, Player player, ushort recipe, bool hardMode)
    {
        var modPlayer = PlayerOf(player);
        var world = WorldOf(modPlayer);
        if (world.OpenWindowOwner != Window || !_states.TryGetValue(modPlayer, out var state) || state.Station == 0) return;
        // ItemsFor(player) is one catalog instance per game mode, so the cached recipes keep the mode they were made for.
        var catalog = ItemsFor(player);
        var rolls = RollsFor(player);
        var harvest = _recipes.GetValue(catalog, c => new HarvestCrafting(c, rolls, hardMode: hardMode));
        string name = harvest.ByHash(recipe)?.Id ?? $"0x{recipe:x4}";

        var items = ItemsOf(player);
        var stash = PersonalStashOf(player);
        var result = ExecuteSlotCraft(items, stash, Slot, item => harvest.ApplyHarvest(recipe, item, global::Game.AreaRng(player)));
        if (result is null)
        {
            module.Log($"{modPlayer.Character} recipe {name} refused (item, recipe or lifeforce)");
            await modPlayer.Send(InstanceHarvest.CraftResult((ushort)world.ItemIdIn(Slot)), "0x0276 Harvest item event (refused)");
            string hint = harvest.RefusalHint(recipe, items.Items(Slot) is [var slotItem] ? slotItem.Item : null)
                ?? "Harvest craft refused: incompatible item or insufficient lifeforce.";
            await modPlayer.Tell(hint);
            return;
        }

        uint inventory = PlayerInventories.InventoryId(Slot);
        if (result.Changed is { } crafted)
        {
            // Clear and refill the slot in separate changes: the native panel watching the slot sees the item replaced.
            await Send(player,
                InstanceItems.InventoryChange(inventory, items.NextRevision(Slot), [crafted.Id], []),
                $"Harvest recipe {name}: remove crafted item {crafted.Id} from the slot");
            await Send(player,
                InstanceItems.InventoryChange(inventory, items.NextRevision(Slot), [], [crafted]),
                $"Harvest recipe {name}: crafted item {crafted.Id}");
        }
        else
        {
            await Send(player,
                InstanceItems.InventoryChange(inventory, items.NextRevision(Slot), [result.RemovedId], []),
                $"Harvest recipe {name}: consumed item {result.RemovedId} from the slot");
        }

        foreach (var payment in result.Payments)
        {
            if (payment.Inventory is { } paid)
                await Send(player,
                    InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                        items.NextRevision(PlayerInventories.MainInventory), [.. paid.Removed, .. paid.Changed.Select(c => c.Id)], paid.Changed),
                    $"Harvest recipe {name}: paid from inventory");
            await SendStashPayment(player, stash, payment.Stash);
        }

        // The slot item's 0x276 event (3.29.3c 0x1404915b0) clears the panel's pending state and refreshes the UI
        // (itemId == 0 clears pending state cleanly when the craft destroyed the item).
        ushort eventItem = result.Changed is { } kept ? (ushort)kept.Id : (ushort)0;
        await modPlayer.Send(InstanceHarvest.CraftResult(eventItem), $"0x0276 Harvest item event {eventItem} ({name})");
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
