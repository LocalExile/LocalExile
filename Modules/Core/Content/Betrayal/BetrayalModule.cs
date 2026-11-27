using GameServer.Modding;

namespace GameServer.Modules.Betrayal;

/// <summary>
/// Betrayal: Jun's mission in map areas. A new instance rolls it from the map's master-mission stats
/// (<see cref="BetrayalPlan.Rolled"/>); a Transportation (carts and their guards) or an Intervention (assassination portals
/// and its ruleset) led by a Syndicate member of that division, who kneels by a MakeChoice when brought down
/// (<see cref="BetrayalEncounter"/>). Not built yet: the choice window and the Syndicate board (the choice is Interrogate),
/// the Fortification and Research missions, the cart's drive, the assassins the portals send, the members who intervene,
/// the safehouses and Catarina, and the rewards.
/// </summary>
public sealed class BetrayalModule : IModule
{
    readonly BetrayalEncounter encounter = new();

    /// <summary>This module's game data, built once when it loads.</summary>
    public static void LoadData() => BetrayalTables.LoadGameData();

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        string id = module.Manifest.Id;
        module.Lifetime.Own(new Cleanup(encounter.Clear));
        module.Lifetime.Own(RegisterDeliriumPauseSource(encounter.IsActivePause));
        GameEvents.AreaPopulated.Add(id, encounter.Place);
        GameEvents.EnterMessages.Add(id, encounter.EnterMessages);
        GameEvents.PlayerEntered.Add(id, encounter.Entered);
        GameEvents.AreaTick.Add(id, GameEvents.Always((player, seconds) => encounter.Has(player) ? encounter.Advance(player, seconds) : Task.CompletedTask));
        GameEvents.ObjectUse.Add(id, encounter.Use);
        GameEvents.Kill.Add(id, encounter.Killed);
    }

    sealed class Cleanup(Action clean) : IDisposable
    {
        public void Dispose() => clean();
    }
}
