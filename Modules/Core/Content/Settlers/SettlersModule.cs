using GameServer.Modding;
using GameServer.Modules.Settlers.Bosses;
using GameServer.Modules.Settlers.MapOres;
using GameServer.Modules.Settlers.Operations;
using GameServer.Modules.Settlers.Town;

namespace GameServer.Modules.Settlers;

/// <summary>
/// Settlers of Kalguur / Kingsmarch module entry point (3.29.3c).
/// Wires Town (<see cref="TownSubsystem"/>), Operations (<see cref="OperationsSubsystem"/>), Map Ores
/// (<see cref="SettlersMapOres"/>), and Boss Arenas (<see cref="SettlersBosses"/>) over one shared authoritative
/// <see cref="TownOperationsBridge"/> backed by <see cref="VillageTownService"/> and <see cref="VillageStore"/>.
/// </summary>
public sealed partial class SettlersModule : IModule
{
    public static SettlersSettings Settings { get; private set; } = SettlersSettings.Default;

    readonly TownOperationsBridge _bridge = new();
    IModuleContext _module = null!;

    sealed class Cleanup(Action clean) : IDisposable { public void Dispose() => clean(); }

    public static void LoadData()
    {
        TownSubsystem.LoadGameData();
        OperationsSubsystem.LoadGameData();
        MapOreTables.LoadGameData();
        SettlersBosses.LoadGameData();
    }

    public void Load(IModuleContext module)
    {
        _module = module;
        Settings = module.Settings<SettlersSettings>();
        LoadData();

        // Install the single authoritative Town <-> Operations <-> MapOres <-> Bosses bridge before registering handlers.
        OperationsSubsystem.SetHost(_bridge);
        module.Lifetime.Own(new Cleanup(() => _bridge.Clear()));

        // 1. Kingsmarch Hub World, Karui Shores Intro/Quests, and Core Idle Town Engine.
        TownSubsystem.Register(module);
        Func<VillageTownState, double, long, bool> onSimTick = _bridge.OnSimulateTickLocked;
        VillageTownService.OnSimulateTick += onSimTick;

        // 2. Shipping, Atlas Map Runners, Rewards Stash, Disenchanting, and Dual-Mode Recombinator.
        OperationsSubsystem.Register(module);

        // 3. Map-Side Ore Deposits (all 5 + Dust Hourglass), Lost Shipments, Starfall Crater Entrance, Scarabs, Atlas, Astrolabe.
        SettlersMapOres.Register(module, _bridge, () => Settings.OreFoundMultiplier);

        // 4. Settlers Boss Arenas (The Black Knight, Admiral Valerius, Sasan the Bandit Lord).
        SettlersBosses.Register(module);
        SettlersBossCallbacks.GetOreFoundMultiplier = () => Settings.OreFoundMultiplier;
        SettlersBossCallbacks.GetBossRewardMultiplier = () => Settings.ShippingRewardMultiplier;
        Func<BossVerisiumClaimedEvent, Task> onBossVerisium = evt =>
        {
            _bridge.GrantResource(evt.Player, VillageResourceKind.PendingVerisiumOre, evt.OreAmount, "boss:black-knight");
            return Task.CompletedTask;
        };
        Func<RescueShipmentEvent, Task> onRescueShipment = evt =>
            OperationsSubsystem.OnPirateBossDefeatedAsync(evt.Player);
        Func<RescueMappersEvent, Task> onRescueMappers = evt =>
            OperationsSubsystem.OnBanditBossDefeatedAsync(evt.Player);
        SettlersBossCallbacks.OnBossVerisiumClaimed += onBossVerisium;
        SettlersBossCallbacks.OnRescueShipment += onRescueShipment;
        SettlersBossCallbacks.OnRescueMappers += onRescueMappers;

        module.Lifetime.Own(new Cleanup(() =>
        {
            VillageTownService.OnSimulateTick -= onSimTick;
            SettlersBossCallbacks.OnBossVerisiumClaimed -= onBossVerisium;
            SettlersBossCallbacks.OnRescueShipment -= onRescueShipment;
            SettlersBossCallbacks.OnRescueMappers -= onRescueMappers;
            SettlersBossCallbacks.GetOreFoundMultiplier = static () => 1.0;
            SettlersBossCallbacks.GetBossRewardMultiplier = static () => 1.0;
        }));
    }
}
