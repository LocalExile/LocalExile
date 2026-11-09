/// <summary>
/// What the Goddess of Justice does in Izaro's arenas, so far: her Guardians (<c>IzaroSummonSkeletonGhostflame</c>, 20 s
/// cooldown, <c>number_of_monsters_to_summon</c> 2, <c>projectile_minimum_range</c> 25, Skeletons/IzaroSummonSkeletonGhostflame:
/// IzaroSkeleton.ot's <c>time_until_death</c> 30). She is not on him yet, so the circles are the server's: they open at
/// random places in the arena, six at a time (<c>skill_repeat_count</c>), each with its mortar's impact (LbBossMortarSummon:
/// <c>skelesummon_impact.ao</c>, 6.625 s). From when he is hurt to 90 % of his life (the wiki). Her mortars, her Teleport and
/// the beam ending on her are not done.
/// </summary>
static class IzaroGoddess
{
    public const string Guardian = "Metadata/Monsters/Skeletons/IzaroSummonSkeletonGhostflame",
        Impact = "Metadata/Effects/Spells/monsters_effects/labyrinth/skeleton_king/summon/skelesummon_impact.ao";

    public const int Circles = 6, GuardiansInACircle = 2, HurtPercent = 90, MinimumRange = 25, ArenaReach = 70;
    public const double EverySeconds = 20, GuardianSeconds = 30, ImpactSeconds = 6.625;
}
