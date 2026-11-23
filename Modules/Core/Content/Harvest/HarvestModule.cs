using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Harvest;

/// <summary>Horticrafting (the station in the hideout, its recipe browser and its crafts), the Sacred Grove of a map
/// (<see cref="SacredGrove"/>) and Oshabi's fight (<see cref="OshabiEncounter"/>).</summary>
public sealed class HarvestModule : IModule
{
    const string StationType = "Metadata/MiscellaneousObjects/Harvest/CraftingSilo_Hideout";
    const string Window = "harvest";
    /// <summary>The client's HarvestCraftingItem inventory row.</summary>
    public const byte Slot = 43;

    sealed class State { public uint Station; }
    readonly ConditionalWeakTable<IPlayer, State> _states = new();
    readonly ConditionalWeakTable<IItemCrafting, HarvestCrafting> _recipes = new();

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
        GameEvents.AreaGenerated.Add(module.Manifest.Id, async player =>
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
            await Craft(module, PlayerOf(player), InstanceHarvest.ReadCraft(bytes), LeagueFor(player).HardMode);
            return true;
        });
        GameEvents.Packet(InstanceHarvest.MoveItemId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            return await MoveItem(player, InstanceHarvest.ReadMoveItem(bytes));
        });
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
    /// The item and all lifeforce costs commit together; a refusal spends nothing.</summary>
    async Task Craft(IModuleContext module, IPlayer player, ushort recipe, bool hardMode)
    {
        var world = WorldOf(player);
        if (world.OpenWindowOwner != Window || !_states.TryGetValue(player, out var state) || state.Station == 0) return;
        // world.Crafting is one instance per game mode, so the cached recipes keep the mode they were made for.
        var harvest = _recipes.GetValue(world.Crafting, items => new HarvestCrafting(items, hardMode: hardMode));
        string name = harvest.ByHash(recipe)?.Id ?? $"0x{recipe:x4}";
        var crafted = await world.CraftInSlot(Slot, item => harvest.Apply(recipe, item, global::Game.AreaRng(PlayerOf(player))), $"Harvest recipe {name}");
        if (crafted is null)
        {
            module.Log($"{player.Character} recipe {name} refused (item, recipe or lifeforce)");
            await player.Send(InstanceHarvest.CraftResult((ushort)world.ItemIdIn(Slot)), "0x0276 Harvest item event (refused)");
            await player.Tell("Harvest craft refused: incompatible item or insufficient lifeforce.");
            return;
        }
        // The slot item's 0x276 event clears the panel's pending state and refreshes the item for another craft.
        await player.Send(InstanceHarvest.CraftResult((ushort)crafted.Id), $"0x0276 Harvest item event {crafted.Id} ({name})");
    }
}
