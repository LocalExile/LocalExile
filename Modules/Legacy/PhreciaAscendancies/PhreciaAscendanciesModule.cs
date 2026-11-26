using GameServer.Modding;

namespace GameServer.Modules.PhreciaAscendancies;

/// <summary>Optional admission and node-stat sources for the implemented Phrecia classes.</summary>
public sealed class PhreciaAscendanciesModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        // What the family's nodes need is in place before its classes can be chosen.
        GameEvents.AttackHit.Add(module.Manifest.Id, PhreciaStats.MaximumAttackDamage);
        GameEvents.DerivedStats.Add(module.Manifest.Id, PhreciaStats.DurationPerTenStrength);
        GameEvents.PoolSources.Add(module.Manifest.Id, PhreciaStats.ManaPerDexterity);
        GameEvents.CastSpeedMore.Add(module.Manifest.Id, PhreciaStats.CastSpeedWithFishingRod);
        // The Paladin's Light of Divinity (Augury of Penitence, 2551: nearby enemies take increased elemental damage) and
        // the Surfcaster's Ghost of the Deep (Torment Touched, 2550: a permanent buff on the player).
        PassiveBuffAuras.Apply(module.Lifetime, "CustomAscendancyNearbyEnemiesIncreasedElementalDamageTaken");
        PassiveBuffAuras.Apply(module.Lifetime, "CustomAscendancyIllegalFisherman");
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
