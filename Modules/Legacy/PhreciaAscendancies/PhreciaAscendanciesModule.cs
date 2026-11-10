using GameServer.Modding;

namespace GameServer.Modules.PhreciaAscendancies;

/// <summary>Optional admission and node-stat sources for the implemented Phrecia classes.</summary>
public sealed class PhreciaAscendanciesModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        AscendancyPolicies.Register(module.Lifetime, new PhreciaFamilyPolicy());
        var gameplay = new Classes.Harbinger.HarbingerGameplay();
        GameEvents.CompanionTick.Add(module.Manifest.Id, gameplay.Tick);
        GameEvents.SkillCastAt.Add(module.Manifest.Id, gameplay.Cast);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, gameplay.StatsFor);
        GameEvents.GrantedSkillExecutable.Add(module.Manifest.Id, gameplay.Executable);
        GameEvents.TransientClosing.Add(module.Manifest.Id, gameplay.Close);
        GameEvents.PauseChanged.Add(module.Manifest.Id, gameplay.Pause);
    }
}
