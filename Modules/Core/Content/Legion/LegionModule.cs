using GameServer.Modding;

namespace GameServer.Modules.Legion;

/// <summary>
/// Legion: the Timeless Monolith in a map the client tables give a Legion chance, the two armies in stasis around it, the
/// time stop and the reward chests the broken-out hoards leave. An encounter of the area instance (<see cref="LegionEncounter"/>):
/// a new instance rolls CoreLeagues row 15 when its monsters are placed and, with a roll, plans one monolith far from the
/// arrival. Listing this module in a league's modules.json turns it on. Agents/docs/game/league-encounters.md.
/// </summary>
public sealed class LegionModule : IModule
{
    readonly LegionEncounter _encounter = new();

    sealed class Cleanup(Action clean) : IDisposable { public void Dispose() => clean(); }

    /// <summary>This module's game data, built when it loads (before the Gateway listens). Tests that use the module call it once.</summary>
    public static void LoadData()
    {
        LegionTables.LoadGameData();
        LegionPackets.LoadGameData();
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        string id = module.Manifest.Id;
        module.Lifetime.Own(new Cleanup(_encounter.Clear));
        GameEvents.AreaPopulated.Add(id, async area =>
        {
            if (await _encounter.Place(area) is { } why)
                module.Log($"Legion: none in {area.Player.Layout.Id} ({why})");
        });
        GameEvents.EnterMessages.Add(id, _encounter.EnterMessages);
        GameEvents.PlayerEntered.Add(id, _encounter.Enter);
        GameEvents.AreaTick.Add(id, start => MapDevices.IsMapArea(start.Layout.Hash) ? (player, _) => _encounter.Step(player) : null);
        GameEvents.ObjectUse.Add(id, async (player, use, requestedAt) =>
        {
            if (_encounter.Of(player.Instance) is not { Monolith: not 0 } state || use.TargetId != state.Monolith) return false;
            await _encounter.Click(player, use, requestedAt);
            return true;
        });
    }
}
