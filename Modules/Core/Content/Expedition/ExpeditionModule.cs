using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Expedition;

/// <summary>
/// Expedition: an area that rolls the encounter (WorldAreaLeagueChances, CoreLeagues row 21) is played with the
/// ExpeditionPlugin terrain plugin, so the client's generation and the Gateway's place the encounter room; its detonator,
/// the faction's markers and remnants, the explosives the map owner places, the chain of blasts that digs the markers up,
/// the dug monsters and the faction's chests follow the official captures (<see cref="ExpeditionEncounter"/>). The encounter
/// belongs to the area instance.
/// </summary>
public sealed class ExpeditionModule : IModule
{
    /// <summary>This module's game data, built when it loads.</summary>
    public static void LoadData() => ExpeditionTables.LoadGameData();

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        var encounter = new ExpeditionEncounter();
        module.Lifetime.Own(encounter);
        string id = module.Manifest.Id;
        // The roll, once per instance and before its seed: the plugin state is a generation input of both sides.
        InstanceEvents.TerrainPluginStates.Add(id, request => ExpeditionTables.AreaId(request.Area) is { } area
            ? ExpeditionEncounter.PluginStates(area, Random.Shared, request.Map?.Mods) : null);
        // ExpeditionRules, after 0x21 and before the area's objects: the 0x2c9 messages and the client's placements need it.
        GameEvents.EnterMessages.Add(id, encounter.RulesetMessages);
        // The client places explosives only with IsMapOwner: the owner of the instance has it in this area.
        GameEvents.AreaQuestFlags.Add(id, encounter.QuestFlags);
        GameEvents.AreaGenerated.Add(id, player => encounter.OnAreaGenerated(module.Log, player));
        GameEvents.PlayerEntered.Add(id, player => encounter.OnPlayerEntered(module.Log, player));
        GameEvents.AreaTick.Add(id, encounter.TickFor);
        GameEvents.Kill.Add(id, encounter.OnKill);
        GameEvents.ObjectUse.Add(id, encounter.OnUse);
        GameEvents.Packet(InstanceRulesets.ActionId).Add(id, encounter.RulesetAction);
    }
}
