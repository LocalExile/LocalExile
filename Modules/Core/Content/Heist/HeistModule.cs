using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Heist;

/// <summary>
/// Heist module: hideout Heist Locker, Rogue Harbour native screens (<c>0x34</c>–<c>0x37</c>),
/// blueprint reveal/crew/confirm &amp; contract signing handlers (<c>0x279</c>, <c>0x27d</c>–<c>0x27f</c>), S→C <c>0x27c</c>
/// rogue state, and alert/lockdown mission controller (<c>0x164</c>, <c>0x163</c> type 8).
/// </summary>
public sealed partial class HeistModule : IModule
{
    const string LockerType = "Metadata/MiscellaneousObjects/Heist/HeistStash_Hideout";

    // The scripts of the Rogue Harbour's dialogue rows (client.db NPCTalk):
    //   Adiyah, "Prepare Heist":        FinishNPCChat(); SendOpenScreen( HeistMission )
    //   Adiyah, "Prepare Grand Heist":  SendOpenScreen( HeistBlueprint, 3, -1 )
    //   each rogue, "Show Inventory":   FinishNPCChat(); SendOpenScreen( HeistNPCInventory, n ), n = 0..8
    // The arguments are the message's first two u32 (a negative one as the client writes it).
    [GeneratedRegex(@"SendOpenScreen\(\s*(\w+)\s*(?:,\s*(-?\d+)\s*)?(?:,\s*(-?\d+)\s*)?\)")]
    private static partial Regex OpenScreen();

    sealed class State { public uint Locker; }
    sealed class Cleanup(Action clean) : IDisposable { public void Dispose() => clean(); }

    // Key by GameArea (plus HideoutEditor fallback) so re-entering, guest visits, and module reloads resolve the locker.
    readonly ConditionalWeakTable<GameArea, State> _states = new();
    readonly HeistMissionController _missions = new();

    uint? LockerOf(Player player)
    {
        if (_states.TryGetValue(player.Area, out var state) && state.Locker != 0)
            return state.Locker;
        foreach (var (id, entry) in player.HideoutEditor.All())
        {
            if (entry.Key == "heist-locker")
            {
                _states.GetOrCreateValue(player.Area).Locker = id;
                return id;
            }
        }
        return null;
    }

    public static void LoadData() => HeistTables.LoadGameData();

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        string id = module.Manifest.Id;
        module.Lifetime.Own(new Cleanup(() =>
        {
            _states.Clear();
            _missions.Clear();
        }));

        GameEvents.HideoutReady.Add(id, async player =>
        {
            if (await WorldOf(PlayerOf(player)).SpawnHideoutStation(HideoutSlot.Small, LockerType, "heist-locker", "Heist Locker") is { } lockerId)
                _states.GetOrCreateValue(player.Area).Locker = lockerId;
        });

        GameEvents.ObjectUse.Add(id, async (player, use, at) =>
        {
            if (LockerOf(player) is not uint locker || use.TargetId != locker) return false;
            var world = WorldOf(player);
            if (await world.WalkUpToStation(locker, at))
                await world.OpenWindow("heist", InstanceWaypoints.HeistLockerUi, "0x007b open native Heist Locker (HeistStashOpen)");
            return true;
        });

        GameEvents.PlayerEntered.Add(id, async player =>
        {
            if (HeistTables.IsRogueHarbour(player.Layout.Id))
            {
                await HeistProgress.EnsureRogueUnlocks(player);
                await HeistProgress.SendState(player, reason: "entered Rogue Harbour");
            }
            else if (HeistTables.IsMissionArea(player.Layout.Id, player.Layout.Hash))
            {
                await _missions.OnPlayerEntered(player);
            }
        });

        GameEvents.AreaTick.Add(id, start =>
            HeistTables.IsMissionArea(start.Layout.Id, start.Layout.Hash) ? _missions.OnTick : null);

        GameEvents.InventoryRefusal.Add(id, HeistPlanning.Refusal);

        GameEvents.MessagesHandled.Add(id, async (player, ids) =>
        {
            if (!HeistTables.IsRogueHarbour(player.Layout.Id)) return;
            if (ids.Contains(InstanceItems.PlaceItemId) || ids.Contains(InstanceItems.PickUpItemId)
                || ids.Contains(InstanceItems.MissionSlotMoveId) || ids.Contains(InstanceItems.LockerMoveId))
                await HeistProgress.SendState(player, reason: "inventory updated in Rogue Harbour");
        });

        GameEvents.NpcOption.Add(id, async talk =>
        {
            if (talk.Category == "EndlessHeistContracts")
            {
                WorldOf(talk.Player).NpcEndTalk();
                await talk.ModPlayer.Tell("Adiyah's contract shop requires HeistContract item support and the unported HeistPlugin terrain generator.");
                return true;
            }

            if (talk.Category == "OpenHeistNPCBlueprint")
            {
                SpawnedNpc? npc;
                lock (talk.Player.Npcs) npc = talk.Player.Npcs.GetValueOrDefault(talk.Player.TalkingTo);
                byte revealerRow = npc is not null && HeistTables.RevealerByNpcRow.TryGetValue(npc.Npc.Row, out byte rev) ? rev : (byte)0;
                WorldOf(talk.Player).NpcEndTalk();
                await HeistProgress.SendState(talk.Player, reason: "open reveal blueprint screen");
                await talk.ModPlayer.Send(
                    InstanceWaypoints.OpenUi(HeistPlanning.BlueprintUi, revealerRow, unchecked((uint)-1)),
                    $"0x007b open HeistBlueprint reveal ({talk.Npc}: revealer {revealerRow}, NPCTalk {talk.Row})");
                return true;
            }

            if (OpenScreen().Match(talk.Script) is not { Success: true } call) return false;
            string screen = call.Groups[1].Value;
            byte ui = screen switch
            {
                "HeistMission" => HeistPlanning.MissionUi,
                "HeistBlueprint" => HeistPlanning.BlueprintUi,
                "HeistNPCInventory" => HeistPlanning.NpcInventoryUi,
                _ => 0,
            };
            if (ui == 0) return false;
            WorldOf(talk.Player).NpcEndTalk();
            await HeistProgress.SendState(talk.Player, reason: $"open {screen}");
            await talk.ModPlayer.Send(InstanceWaypoints.OpenUi(ui, Argument(call.Groups[2]), Argument(call.Groups[3])),
                $"0x007b open {screen} ({talk.Npc}: \"{talk.Option}\", NPCTalk {talk.Row})");
            return true;
        });

        GameEvents.Packet(InstanceHeist.SignContractId).Add(id, (player, bytes) =>
            HeistPlanning.HandleSignContract(module, player, bytes));
        GameEvents.Packet(InstanceHeist.RevealBlueprintId).Add(id, (player, bytes) =>
            HeistPlanning.HandleRevealBlueprint(module, player, bytes));
        GameEvents.Packet(InstanceHeist.ConfirmBlueprintId).Add(id, (player, _) =>
            HeistPlanning.HandleConfirmBlueprint(module, player));
        GameEvents.Packet(InstanceHeist.SetBlueprintCrewId).Add(id, (player, bytes) =>
            HeistPlanning.HandleSetBlueprintCrew(module, player, bytes));
    }

    static uint Argument(Group g) => g.Success && int.TryParse(g.Value, out int v) ? unchecked((uint)v) : 0;
}
