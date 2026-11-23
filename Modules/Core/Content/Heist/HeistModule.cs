using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Heist;

/// <summary>The hideout's Heist Locker, and the Rogue Harbour's three native screens.</summary>
public sealed partial class HeistModule : IModule
{
    const string LockerType = "Metadata/MiscellaneousObjects/Heist/HeistStash_Hideout";

    // The client's own screen table (24-byte rows: name, its length, the kind) next to HeistStorageWindow 0x37.
    const byte MissionUi = 0x34, BlueprintUi = 0x35, NpcInventoryUi = 0x36;

    // The scripts of the Rogue Harbour's dialogue rows (client.db NPCTalk):
    //   Adiyah, "Prepare Heist":        FinishNPCChat(); SendOpenScreen( HeistMission )
    //   Adiyah, "Prepare Grand Heist":  SendOpenScreen( HeistBlueprint, 3, -1 )
    //   each rogue, "Show Inventory":   FinishNPCChat(); SendOpenScreen( HeistNPCInventory, n ), n = 0..8
    // The arguments are the message's first two u32 (a negative one as the client writes it).
    [GeneratedRegex(@"SendOpenScreen\(\s*(\w+)\s*(?:,\s*(-?\d+)\s*)?(?:,\s*(-?\d+)\s*)?\)")]
    private static partial Regex OpenScreen();

    sealed class State { public uint Locker; }
    readonly ConditionalWeakTable<IPlayer, State> _states = new();

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        GameEvents.HideoutReady.Add(module.Manifest.Id, async player =>
        {
            var modPlayer = PlayerOf(player);
            if (await WorldOf(modPlayer).SpawnHideoutStation(HideoutSlot.Small, LockerType, "heist-locker", "Heist Locker") is { } id)
                _states.GetOrCreateValue(modPlayer).Locker = id;
        });
        GameEvents.ObjectUse.Add(module.Manifest.Id, async (player, use, at) =>
        {
            if (!_states.TryGetValue(PlayerOf(player), out var state) || state.Locker == 0 || use.TargetId != state.Locker) return false;
            var world = WorldOf(player);
            if (await world.WalkUpToStation(state.Locker, at))
                await world.OpenWindow("heist", InstanceWaypoints.HeistLockerUi, "0x007b open native Heist Locker (HeistStashOpen)");
            return true;
        });
        GameEvents.NpcOption.Add(module.Manifest.Id, async talk =>
        {
            if (OpenScreen().Match(talk.Script) is not { Success: true } call) return false;
            string screen = call.Groups[1].Value;
            byte ui = screen switch
            {
                "HeistMission" => MissionUi,
                "HeistBlueprint" => BlueprintUi,
                "HeistNPCInventory" => NpcInventoryUi,
                _ => 0,
            };
            if (ui == 0) return false;
            var world = WorldOf(talk.Player);
            world.NpcEndTalk();
            await talk.ModPlayer.Send(InstanceWaypoints.OpenUi(ui, Argument(call.Groups[2]), Argument(call.Groups[3])),
                $"0x007b open {screen} ({talk.Npc}: \"{talk.Option}\", NPCTalk {talk.Row})");
            return true;
        });
    }

    static uint Argument(Group g) => g.Success && int.TryParse(g.Value, out int v) ? unchecked((uint)v) : 0;
}
