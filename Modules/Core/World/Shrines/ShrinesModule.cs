using GameServer.Modding;
using static Program;

namespace GameServer.Modules.Shrines;

/// <summary>
/// Map shrines: the level generator's shrine rooms place <c>Metadata/Shrines/Shrine</c> lines, and the manager spawns one
/// shrine object per line on walkable ground, rolls its type from the area seed, and runs its lifecycle (a click in
/// walk-up range claims it, its buff lasts the Shrines row's duration, and leaving the area cleans up).
/// <para>This module only wires the framework to the manager: the types, their buffs and their guards are data
/// (<see cref="ShrineCatalog"/>), and per-type gameplay is a behaviour or an effect in <see cref="ShrineRegistry"/>, so a
/// new shrine never changes this file.</para>
/// </summary>
public sealed class ShrinesModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        string id = module.Manifest.Id;
        ShrineRegistry.RegisterDefaults();
        ApparitionRewards.Load();
        EvolutionShrine.Load();
        EvolutionShrine.Policy = module.Settings<EvolutionShrine.Settings>();
        module.Lifetime.Own(GameEvents.AreaGenerated.Add(id, player => ShrineManager.SpawnArea(player)));
        module.Lifetime.Own(GameEvents.PlayerEntered.Add(id, player => ShrineManager.SpawnArea(player)));
        module.Lifetime.Own(GameEvents.ObjectUse.Add(id, (player, use, requestedAt) => ShrineManager.Use(player, use, requestedAt)));
        module.Lifetime.Own(GameEvents.CompanionTick.Add(id, async (player, _) => { await ShrineManager.Tick(player); await ShrineGearEffects.Tick(player); }));
        module.Lifetime.Own(GameEvents.Kill.Add(id, kill => { LesserShrines.OnKill(kill); ShrineManager.OnKill(kill); EvolutionShrine.OnKill(kill); }));
        module.Lifetime.Own(GameEvents.MapOpening.Add(id, ShrineMapAdmission.Check));
        module.Lifetime.Own(GameEvents.Drops.Add(id, ApparitionRewards.Drops));
        module.Lifetime.Own(GameEvents.BuffCancel.Add(id, ShrineManager.CancelBuff));
        module.Lifetime.Own(GameEvents.PlayerLeaving.Add(id, (player, _) => { ShrineGearEffects.LeaveArea(player); return ShrineManager.LeaveArea(player); }));
        // The claimed shrines' buff stats join the character's own sum, so the hit, crit, armour and resistance paths
        // reading it see them; the buff's expiry takes them out again.
        module.Lifetime.Own(GameEvents.PlayerStatSources.Add(id, (player, stats) => ShrineGearEffects.AddStats(player, ShrineManager.AddStats(player, stats))));
        module.Lifetime.Own(ShrineManager.Lifetime());
    }
}
