using GameServer.Modding;

namespace GameServer.Modules.PhreciaAscendancies;

/// <summary>Optional admission and node-stat sources for the implemented Phrecia classes.</summary>
public sealed class PhreciaAscendanciesModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module) =>
        AscendancyPolicies.Register(module.Lifetime, new PhreciaFamilyPolicy());
}
