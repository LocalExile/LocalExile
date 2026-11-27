using GameServer.Modding;

namespace GameServer.Modules.Settlers;

/// <summary>
/// Settlers of Kalguur / Kingsmarch module entry point (3.29.3c).
/// Wave 2 subsystems (Kingsmarch hubtown, simulation/economy, UI protocol, recombinator, map ores, bosses)
/// extend this partial class or register their hooks via <see cref="Load"/>.
/// </summary>
public sealed partial class SettlersModule : IModule
{
    public static SettlersSettings Settings { get; private set; } = SettlersSettings.Default;

    IModuleContext _module = null!;

    public static void LoadData()
    {
        LoadSubsystemsData();
    }

    static partial void LoadSubsystemsData();
    partial void RegisterSubsystems(IModuleContext module);

    public void Load(IModuleContext module)
    {
        _module = module;
        Settings = module.Settings<SettlersSettings>();
        LoadData();
        RegisterSubsystems(module);
    }
}
