/// <summary>
/// The Goddess of Justice (<c>Metadata/Monsters/Labyrinth/GoddessOfJustice</c>): a monster that cannot be damaged or
/// targeted and does not move, riding on Izaro's <c>jnt_back_spirit</c> bone in every trial (the original, live: always
/// with him, turning as he turns). She lobs her Spirit Mortars at the player (<c>LabyrinthSpiritMortar</c>:
/// 3.5 s cooldown: five mortars at once to where the player stood, the client's own action), and from
/// when Izaro is hurt to 90 % of his life (the wiki) her Guardians' mortars (<c>LbBossMortarSummon</c>) open circles at
/// random places of the arena, six every 20 s (<c>skill_repeat_count</c>), two Guardians in each
/// (<c>IzaroSummonSkeletonGhostflame</c>: <c>number_of_monsters_to_summon</c> 2, <c>projectile_minimum_range</c> 25;
/// IzaroSkeleton.ot's <c>time_until_death</c> 30). She casts the gargoyles' awakening beams from her hands, nothing else meanwhile (live). When Izaro dies she turns slowly and
/// flies up (her <c>death</c> clip, live); when he leaves she goes down with him. Her Teleport (<c>IzaroWarp</c>) is the
/// Cruel and Merciless final trial's (the wiki).
/// </summary>
public static class IzaroGoddess
{
    public const string Variety = "Metadata/Monsters/Labyrinth/GoddessOfJustice",
        Bone = "jnt_back_spirit",
        Guardian = "Metadata/Monsters/Skeletons/IzaroSummonSkeletonGhostflame",
        Impact = "Metadata/Effects/Spells/monsters_effects/labyrinth/skeleton_king/summon/skelesummon_impact.ao",
        SpiritMortar = "Metadata/Projectiles/LbBossSpiritMortar",
        GuardianMortar = "Metadata/Projectiles/LbBossMortarSummon";

    /// <summary>How she rides on his bone: kind 0 with the mask 3 (as the art on Izaro, IzaroSkills), so she turns with
    /// him. The client takes an attached object's facing from the bone alone (0x142909a50), never its own, so her
    /// GoddessOfJusticeFaceLastTarget turned nothing (live).</summary>
    public const byte AttachKind = 0, AttachFlags = 3;

    /// <summary>Her GrantedEffects rows: the Spirit Mortar and the awakening of a gargoyle (its beam from her hands:
    /// AwakenGargoyle, the client's action of that name, base_skill_effect_duration 10 s, 14 s cooldown).</summary>
    public const int MortarEffect = 5111, AwakenEffect = 5888;

    public const int Circles = 6, GuardiansInACircle = 2, HurtPercent = 90, MinimumRange = 25, ArenaReach = 70;
    public const double EverySeconds = 20, GuardianSeconds = 30, ImpactSeconds = 6.625;

    /// <summary>Art/Models/MONSTERS/Izaro/IzaroStatue/rig.amd: <c>death</c> (clip 3, 17.333 s), and <c>volly</c> (1.9 s), whose
    /// contacts release the mortars (0.566 to 1.4 s, 318.6 units up).</summary>
    public const uint DeathClip = 3;
    public const double DeathSeconds = 17.333, VolleySeconds = 1.9;
    public static readonly double[] VolleyContacts = [0.566, 0.733, 0.9, 1.066, 1.233, 1.4];
    public const float VolleyHeight = -318.593f;

    /// <summary>Her actor's Animation.dat types (GoddessOfJustice.act): SpellProjectileFire plays <c>volly</c>,
    /// SpellAreaOfEffect <c>channel</c>.</summary>
    public const byte VolleyAnimation = 24, SummonAnimation = 22;

    /// <summary>The Spirit Mortar: its cooldown (GrantedEffectsPerLevel), its speed (Projectiles.dat), its fire damage as
    /// FloatStatsValues of the monster damage at her level (StatInterpolations 3: 0.6 to 1 below the row at level 66, 0.8 to
    /// 1.2 from it). Its landing's radius is not in the data (ours), nor how far she throws (ours).</summary>
    public const double MortarCooldown = 3.5, MortarRadius = 8, MortarReach = 90;

    /// <summary>The Spirit Mortars of a volley (the original, live: five, one after another; the data names no count).</summary>
    public const int MortarCount = 5;
    public const int MortarSpeed = 750;

}
