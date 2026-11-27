using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Breach;

/// <summary>
/// Breach ("Keepers of the Flame" / Brequel) as a LocalExile module: map Unstable Breaches (<see cref="BreachEncounter"/>),
/// Monastery of the Keepers hub &amp; quests (<see cref="BreachHub"/>), Hiveblood resource (<see cref="BreachHiveblood"/>),
/// Wombgift Locker (<see cref="BreachLocker"/>), and the Genesis Tree (<see cref="BreachGenesisTree"/>).
/// Listing this module in a league's <c>modules.json</c> enables it.
/// </summary>
public sealed class BreachModule : IModule
{
    readonly BreachWorldHost _world = new();

    sealed class Cleanup(Action clean) : IDisposable { public void Dispose() => clean(); }

    /// <summary>This module's game data, built when it loads (before the Gateway listens). Tests that use the module call
    /// it once.</summary>
    public static void LoadData()
    {
        BreachTables.LoadGameData();
        BreachHiveblood.LoadGameData();
        BreachLocker.LoadGameData();
        BreachHub.LoadGameData();
        BreachGenesisTree.LoadGameData();
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        string id = module.Manifest.Id;

        // Wire framework item fields & cross-subsystem delegates cleanly for hot-reload.
        BreachGenesisTree.AttachFruitComponent = static (item, level) => item with { BrequelFruitLevel = level };
        BreachGenesisTree.ReadFruitLevel = static item => item.BrequelFruitLevel is uint l ? (int)l : null;
        BreachGenesisTree.MarkFoulbornItem = static item => item with { Foulborn = true };
        BreachTables.AwardHiveblood = static (player, amount) => BreachHiveblood.AwardAsync(player, amount);
        BreachTables.CreateWombgift = BreachGenesisTree.CreateWombgift;
        BreachTables.TryRollBodyPart = BreachHub.TryRollBodyPart;

        module.Lifetime.Own(new Cleanup(() =>
        {
            BreachHub.Clear();
            BreachGenesisTree.AttachFruitComponent = null;
            BreachGenesisTree.ReadFruitLevel = null;
            BreachGenesisTree.MarkFoulbornItem = null;
            BreachTables.AwardHiveblood = null;
            BreachTables.CreateWombgift = null;
            BreachTables.TryRollBodyPart = null;
        }));

        // Map Unstable Breaches, Established Hives, Hive Fortresses, Wild/Pinnacle Bosses, Atlas & Scarabs.
        _world.Register(module);

        // Hiveblood & Genesis Tree state on area entry, Monastery hub/quests, and Hideout Wombgift Locker.
        GameEvents.EnterMessages.Add(id, player =>
        [
            BreachHiveblood.EnterMessage(player),
            BreachGenesisTree.EnterMessage(player),
        ]);
        GameEvents.PlayerEntered.Add(id, player => BreachHub.OnPlayerEnteredAsync(module, player));
        GameEvents.AreaTick.Add(id, _ => (player, _) => BreachHub.TickAsync(player));

        // Monastery of the Keepers, Hideout Wombgift Locker, Ailith / Dreamer dialogue & quests.
        GameEvents.AreaQuestFlags.Add(id, BreachHub.AreaQuestFlags);
        GameEvents.HideoutReady.Add(id, player => BreachHub.OnHideoutReadyAsync(module, player));
        GameEvents.ObjectUse.Add(id, (player, use, at) => BreachHub.OnObjectUseAsync(module, player, use, at));
        GameEvents.NpcUsesLeagueFlags.Add(id, BreachHub.UsesLeagueFlags);
        GameEvents.NpcOptionPicked.Add(id, (player, talk) => BreachHub.OnNpcOptionPickedAsync(module, player, talk));
        GameEvents.MessagesHandled.Add(id, async (player, ids) =>
        {
            if (ids.Contains(InstanceItems.PlaceItemId) || ids.Contains(InstanceItems.PickUpItemId))
                await BreachHub.SyncBodyPartQuestFlagsAsync(player);
        });

        // Wombgift Locker (BrequelStorage1, row 101) validation & C→S 0x010d Ctrl+click quick-move.
        GameEvents.InventoryRefusal.Add(id, BreachLocker.Refusal);
        GameEvents.Packet(InstanceBrequel.LockerMoveId).Add(id, async (player, bytes) =>
        {
            if (bytes.Length < InstanceBrequel.LockerMoveLength) return false;
            return await BreachLocker.QuickMoveAsync(module, player, InstanceBrequel.ReadLockerMove(bytes));
        });

        // Genesis Tree C→S packets: allocate (0x02d0), refund (0x02d1), fertilise Wombgift (0x02d3).
        GameEvents.Packet(InstanceBrequel.AllocateNodeId).Add(id, (player, bytes) =>
            BreachGenesisTree.HandleAllocatePacketAsync(module, player, bytes));
        GameEvents.Packet(InstanceBrequel.RefundNodeId).Add(id, (player, bytes) =>
            BreachGenesisTree.HandleRefundPacketAsync(module, player, bytes));
        GameEvents.Packet(InstanceBrequel.FertiliseWombgiftId).Add(id, (player, bytes) =>
            BreachGenesisTree.HandleFertilisePacketAsync(module, player, bytes));
    }
}
