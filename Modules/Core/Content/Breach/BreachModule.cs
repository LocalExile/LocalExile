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
    readonly BreachEncounter _encounter = new();

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
        module.Lifetime.Own(new Cleanup(() =>
        {
            _encounter.Clear();
            BreachHub.Clear();
        }));
        module.Lifetime.Own(RegisterDeliriumPauseSource(_encounter.IsActivePause));

        // Map Unstable Breach encounters.
        GameEvents.AreaPopulated.Add(id, async area =>
        {
            if (await _encounter.Populate(area) is { } why)
                module.Log($"Breach: none in {area.Player.Layout.Id} ({why})");
        });
        GameEvents.EnterMessages.Add(id, player =>
        [
            .. _encounter.EnterMessages(player),
            BreachHiveblood.EnterMessage(player),
            BreachGenesisTree.EnterMessage(player),
        ]);
        GameEvents.PlayerEntered.Add(id, async player =>
        {
            await _encounter.Enter(player);
            await BreachHub.OnPlayerEnteredAsync(module, player);
        });
        GameEvents.AreaTick.Add(id, start =>
        {
            bool isMap = MapDevices.IsMapArea(start.Layout.Hash);
            return async (player, _) =>
            {
                if (isMap) await _encounter.Step(player);
                await BreachHub.TickAsync(player);
            };
        });

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
        GameEvents.Packet(BreachLocker.QuickMovePacketId).Add(id, async (player, bytes) =>
        {
            if (bytes.Length < InstanceItems.LeagueWindowMoveLength) return false;
            return await BreachLocker.QuickMoveAsync(module, player, InstanceItems.ReadLeagueWindowMove(bytes));
        });

        // Genesis Tree C→S packets: allocate (0x02d0), refund (0x02d1), fertilise Wombgift (0x02d3).
        GameEvents.Packet(BreachGenesisTree.AllocateNodePacketId).Add(id, (player, bytes) =>
            BreachGenesisTree.HandleAllocatePacketAsync(module, player, bytes));
        GameEvents.Packet(BreachGenesisTree.RefundNodePacketId).Add(id, (player, bytes) =>
            BreachGenesisTree.HandleRefundPacketAsync(module, player, bytes));
        GameEvents.Packet(BreachGenesisTree.FertilisePacketId).Add(id, (player, bytes) =>
            BreachGenesisTree.HandleFertilisePacketAsync(module, player, bytes));
    }
}
