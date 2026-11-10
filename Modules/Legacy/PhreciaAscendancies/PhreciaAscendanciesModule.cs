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
        module.Lifetime.Own(GameEvents.CompanionTick.Add(module.Lifetime.Id, gameplay.Tick));
        module.Lifetime.Own(GameEvents.SkillCastAt.Add(module.Lifetime.Id, gameplay.Cast));
        module.Lifetime.Own(GameEvents.PlayerStatSources.Add(module.Lifetime.Id, gameplay.StatsFor));
        module.Lifetime.Own(GameEvents.GrantedSkillExecutable.Add(module.Lifetime.Id, gameplay.Executable));
        module.Lifetime.Own(GameEvents.TransientClosing.Add(module.Lifetime.Id, gameplay.Close));
        module.Lifetime.Own(GameEvents.PauseChanged.Add(module.Lifetime.Id, gameplay.Pause));
    }
}
