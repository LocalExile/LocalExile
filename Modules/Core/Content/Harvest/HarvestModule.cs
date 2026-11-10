using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Harvest;

/// <summary>Horticrafting: the station in the hideout, its recipe browser and its crafts.</summary>
public sealed class HarvestModule : IModule
{
    const string StationType = "Metadata/MiscellaneousObjects/Harvest/CraftingSilo_Hideout";
    const string Window = "harvest";
    /// <summary>The client's HarvestCraftingItem inventory row.</summary>
    public const byte Slot = 43;

    sealed class State { public uint Station; }
    readonly ConditionalWeakTable<IPlayer, State> _states = new();
    readonly ConditionalWeakTable<IItemCrafting, HarvestCrafting> _recipes = new();

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        GameEvents.HideoutReady.Add(module.Manifest.Id, async conn =>
        {
            var player = PlayerOf(conn);
            if (await module.World(player).SpawnHideoutStation(HideoutSlot.Large, StationType, "harvest", "Horticrafting Station") is { } id)
                _states.GetOrCreateValue(player).Station = id;
        });
        GameEvents.ObjectUse.Add(module.Manifest.Id, async (conn, use, at) =>
        {
            if (!_states.TryGetValue(PlayerOf(conn), out var state) || state.Station == 0 || use.TargetId != state.Station) return false;
            var world = module.World(PlayerOf(conn));
            if (await world.WalkUpToStation(state.Station, at))
                await world.OpenWindow(Window, InstanceWaypoints.HorticraftingUi, "0x007b open Horticrafting Station recipe browser");
            return true;
        });
        GameEvents.Packet(InstanceHarvest.CraftId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            await Craft(module, PlayerOf(conn), InstanceHarvest.ReadCraft(bytes));
            return true;
        });
    }

    /// <summary>C→S 0x274: execute the HarvestCraftOptions recipe whose HASH16 the native panel sent, on the item in the slot.
    /// The item and all lifeforce costs commit together; a refusal spends nothing.</summary>
    async Task Craft(IModuleContext module, IPlayer player, ushort recipe)
    {
        var world = module.World(player);
        if (world.OpenWindowOwner != Window || !_states.TryGetValue(player, out var state) || state.Station == 0) return;
        var harvest = _recipes.GetValue(world.Crafting, items => new HarvestCrafting(items));
        string name = harvest.ByHash(recipe)?.Id ?? $"0x{recipe:x4}";
        var crafted = await world.CraftInSlot(Slot, item => harvest.Apply(recipe, item, Random.Shared), $"Harvest recipe {name}");
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
