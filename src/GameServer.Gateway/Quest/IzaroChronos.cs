/// <summary>
/// Izaro's Chrono Statues (LabyrinthSecrets <c>ChronoStatues1</c>, room boss_chronostatues.arm): three idols
/// (LabyrinthSecretEffects <c>ChronoStatues1_1..3</c>: Flame, Frost and Storm Idol) on the small elevators, rising with
/// Izaro (IzaroChronosStatuePhase1.ot: Transitionable 1 lowered, 2 rising with <c>lift_raise</c>, 3 raised, 4 active,
/// 5 lowering with <c>lift_lower</c>, 6 lowered). An idol cannot die (<c>set_cannot_die</c>). Raised, it channels
/// <c>ChronosStatuesCast</c> (ActiveSkills <c>chronos_statues_cast</c>): the client's action (0x14151d120 start, 0x14151d480
/// tick) plays its <c>Activate</c> pose at the idol's life percent and its glow by that percent (0x14151d630: up to 25 none,
/// then first, second, third from 76). Full, it casts its beacons (<c>beacon_izaro</c>, <c>set_user_life_to_1_on_skill_use</c>,
/// 3 s <c>base_skill_effect_duration</c>; its act's SpellAreaOfEffect is <c>Deactivate_cast</c> with the gold nova) and
/// starts again from 1 life. The "…2" ones (IzaroChronosStatuePhase2.ot: cannot be damaged, untargetable) stand in a later
/// trial's alcove and give Izaro <c>izaro_buff_loot_bonus</c>.
/// The original (live, the user, 2026-11-11): all three rise with Izaro; an idol fills in about 10 s (the .ot's
/// <c>life_regeneration_rate_per_minute_%</c> 360 would take about 16 s), then casts 6 circles (the row has 10), sits down
/// at once and fills again; hitting it takes its life down; when he leaves they go down.
/// </summary>
public static class IzaroChronos
{
    public const string Mechanic = "ChronoStatues";

    /// <summary>IzaroChronosStatuePhase1.ot's Transitionable.</summary>
    public const byte Rising = 2, Raised = 3, Active = 4, Lowering = 5, Lowered = 6;

    /// <summary>The returning ones' (IzaroChronosStatuePhase2.ot, three states): standing.</summary>
    public const byte ReturnedStanding = 1;

    /// <summary>ExtinguishPillar_chronos/rig.amd: lift_raise (10.7 s) and lift_lower (3.333 s), which the .ot's server
    /// part plays, so the server sends them.</summary>
    public const uint LiftRaiseClip = 7, LiftLowerClip = 6, LiftIdleDownClip = 5;

    /// <summary>How long after its channel ends an idol's lowering is sent: sent with the channel's end, the client left
    /// it standing (live 2026-11-11).</summary>
    public const double LowerAfterSeconds = 0.3;

    /// <summary>How long an idol takes from 1 life to full, how many circles it casts and how long it sits after
    /// (the original, live).</summary>
    public const double FillSeconds = 10, SitSeconds = 0.5;
    public const int Circles = 6;

    /// <summary>The returning ones fill at IzaroChronosStatuePhase2.ot's 460 % a minute.</summary>
    public const double ReturnedFillSeconds = 60.0 / 4.6;

    /// <summary>beacon_izaro: a circle bursts base_skill_effect_duration after the cast, within beacon_placement_radius of
    /// the player. Its radius is not in the data (the action's); ours.</summary>
    public const double FuseSeconds = 3, ExplodeSeconds = 2.041;
    public const int PlacementRadius = 100, CircleRadius = 12, CircleSpacing = 18;

    /// <summary>The circle on the ground and its burst, by element (0 fire, 1 cold, 2 lightning):
    /// Metadata/Effects/Spells/monsters_effects/elemental_beacon/&lt;element&gt;/aoe.ao (aoe_loop) and explode.ao.</summary>
    public static string CircleArt(int element) => $"Metadata/Effects/Spells/monsters_effects/elemental_beacon/{Folder(element)}/aoe.ao";

    /// <summary>The beacon's orb in the circle's middle (beacon.ot's rig.ao: orb_&lt;element&gt;_end 0, _loop 1, _start 2
    /// at 0.458 s, which plays first).</summary>
    public static string OrbArt(int element) => $"Metadata/Effects/Spells/monsters_effects/elemental_beacon/{Folder(element)}/rig.ao";
    public const uint OrbEndClip = 0, OrbLoopClip = 1;
    public const double OrbStartSeconds = 0.458;

    /// <summary>beacon_izaro's FloatStatsValues: its damage is that much of the monster damage at the idol's level
    /// (StatInterpolations 3).</summary>
    public const double DamageLow = 0.8, DamageHigh = 1.2;
    public static string BurstArt(int element) => $"Metadata/Effects/Spells/monsters_effects/elemental_beacon/{Folder(element)}/explode.ao";
    static string Folder(int element) => element switch { 0 => "fire", 1 => "ice", _ => "lightning" };

    /// <summary>An idol counts as left on him at the trial's end while it glows (over a quarter of its life: ours, the
    /// finish lines count 0 to 3).</summary>
    public const int LeftAbovePercent = 25;

    /// <summary>His line after the trial's end line: how many idols are left.</summary>
    public static string FinishLine(int left) => $"IzaroFinishChronosStatues{Math.Clamp(left, 0, 3)}";

    public static bool IsIdol(string varietyId) => varietyId.Contains("IzaroChronosStatue", StringComparison.Ordinal);
}
