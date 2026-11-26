using GameServer.Modding;

namespace GameServer.Modules.Breach;

/// <summary>
/// Breach as a LocalExile module: the Unstable Breach (the Brequel) in maps, an encounter of the area instance
/// (<see cref="BreachEncounter"/>). Listing this module in a league's modules.json turns it on. The Hive and the
/// Breach's boss and rewards are not built.
/// </summary>
public sealed class BreachModule : IModule
{
    readonly BreachEncounter _encounter = new();

    sealed class Cleanup(Action clean) : IDisposable { public void Dispose() => clean(); }

    /// <summary>This module's game data, built when it loads (before the Gateway listens). Tests that use the module call
    /// it once.</summary>
    public static void LoadData() => BreachTables.LoadGameData();

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        string id = module.Manifest.Id;
        module.Lifetime.Own(new Cleanup(_encounter.Clear));
        module.Lifetime.Own(RegisterDeliriumPauseSource(p => p.Instance is { } inst && _encounter.Of(inst)?.Breaches.Any(b => b.Open) == true));
        // Placed with the instance's population (the Anarchy pattern), once per instance: a kept instance keeps its own.
        GameEvents.AreaPopulated.Add(id, async area =>
        {
            if (await _encounter.Populate(area) is { } why)
                module.Log($"Breach: none in {area.Player.Layout.Id} ({why})");
        });
        GameEvents.EnterMessages.Add(id, _encounter.EnterMessages);
        GameEvents.PlayerEntered.Add(id, _encounter.Enter);
        GameEvents.AreaTick.Add(id, start => MapDevices.IsMapArea(start.Layout.Hash) ? (player, _) => _encounter.Step(player) : null);
    }
}
