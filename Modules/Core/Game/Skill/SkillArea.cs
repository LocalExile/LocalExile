

/// <summary>
/// A skill's area of effect radius (Agents/docs/protocol/11-skills.md §12): the skill's
/// active_skill_base_area_of_effect_radius plus active_skill_base_radius_+, scaled by the square root of its area
/// modifier, as Path of Building's calcRadius does: floor(base × floor(100 × √(increased × more)) / 100). Increased:
/// base_skill_area_of_effect_+% (passives, gem quality), skill_area_of_effect_+% and, for spells, spell_area_of_effect_+%.
/// More: each of the "_final" area stats; active_skill_area_of_effect_radius_+%_final scales the radius itself. The
/// skill's stats include the player's. Discharge's more area per removable charge counts; qualified item attribute
/// coefficients and supported conditional increases are resolved before this view is built.
/// </summary>
public static class SkillArea
{
    public static bool IsCyclone(string? effect) => effect is "Cyclone" or "CycloneAltX";

    /// <summary>Explicit data wins. Cyclone's base radius is not in its exported stat set (PoB: 16 cells). The player's
    /// Ground Slam ("GroundSlam") has none either: the +40% final radius belongs to the old "ExileGroundSlam" row, which
    /// no gem grants (see GroundSlamWave).</summary>
    public static int BaseRadius(string? effect, Func<string, int> get) =>
        get("active_skill_base_area_of_effect_radius") is int radius and > 0 ? radius
            : effect == "LightningWarp" ? 16 : effect == "VaalGroundSlam" ? 40 : IsCyclone(effect) ? 16 : effect == "RollingMagma" ? 14
            : effect == "FireTrap" ? 15 : effect == "StormblastMine" ? 20 : effect is "DarkPact" or "DarkPactRoyale" ? 26
            : effect is "StormCall" or "Frostblink" or "FrostblinkHardMode" ? 20
            : effect is "StormBrand" or "StormBrandAltX" or "StormBrandAltY"
                or "PenanceBrand" or "PenanceBrandAltX" or "PenanceBrandAltY"
                or "WintertideBrand" or "WintertideBrandAltX" or "WintertideBrandAltY" ? 22
            : effect == "Brandsurge" ? 26
            : GatewayProjectileSkillProfiles.IsMoltenStrike(effect) ? 9 : 12;

    static readonly string[] MoreArea =
    [
        "active_skill_area_of_effect_+%_final", "skill_area_of_effect_+%_final",
        "support_concentrated_effect_skill_area_of_effect_+%_final", "skill_area_of_effect_+%_final_from_skill_specific_stat",
        "blood_sand_stance_melee_skills_area_of_effect_+%_final",
    ];

    const string MorePerRemovableCharge = "area_of_effect_+%_final_per_removable_power_frenzy_or_endurance_charge";

    /// <summary>The base form of increased area: the player's goes into the panel total <c>skill_area_of_effect_+%</c>
    /// (PanelStats), a skill's own (gem quality) stays in it.</summary>
    public const string BaseIncreased = "base_skill_area_of_effect_+%";

    /// <summary>The player's final stats without the base form of increased area (<paramref name="baseKey"/>), which
    /// the panel total already counts: a skill's own base can then be laid over them and <see cref="Increased"/>
    /// counts each source once.</summary>
    public static Dictionary<uint, int> WithoutPlayerBase(Dictionary<uint, int> playerFinal, uint? baseKey)
    {
        if (baseKey is uint key) playerFinal.Remove(key);
        return playerFinal;
    }

    /// <summary>The combined increased-area input before more multipliers: a skill's own base (when the stats hold
    /// one) plus the player's total, with the spell-only increase for spells.</summary>
    public static int Increased(Func<string, int> get, bool spell) =>
        get("base_skill_area_of_effect_+%") + get("skill_area_of_effect_+%") + (spell ? get("spell_area_of_effect_+%") : 0);

    /// <summary>The client's precomputed radius percentage (0x1591), not the increased area percentage.</summary>
    public static int RadiusPercent(Func<string, int> get, bool spell) =>
        (int)Math.Clamp((long)Math.Floor(100 * Math.Sqrt(Modifier(get, spell)))
            * Math.Max(0L, 100L + get("active_skill_area_of_effect_radius_+%_final")) / 100 - 100, -100, int.MaxValue);

    /// <summary>Supports' more or less area, each a "_final" multiplier on the supported skill: Spell Cascade, Arcanist
    /// Brand, Corrupting Cry and Blessed Call (only a warcry carries the last two).</summary>
    static readonly string[] SupportMoreArea =
    [
        "support_spell_cascade_area_of_effect_+%_final", "support_brand_area_of_effect_+%_final",
        "support_corrupting_cry_area_of_effect_+%_final", "consecrated_cry_warcry_area_of_effect_+%_final",
    ];

    /// <summary>Greater Spell Echo: increased area for each repeat of the spell (the first cast is repeat 0).</summary>
    public const string IncreasedPerRepeat = "support_greater_spell_echo_area_of_effect_+%_per_repeat";

    /// <summary>A "more area" percentage as the radius multiplier it gives: the square root of (1 + more), as the area
    /// modifier is (Fist of War's 59% more area is ×1.26 radius, not ×1.59).</summary>
    public static double RadiusMultiplier(int moreAreaPercent) => Math.Sqrt(Math.Max(0, 1 + moreAreaPercent / 100.0));

    /// <summary>The area modifier: (1 + increased) × each more multiplier, at least 0.</summary>
    public static double Modifier(Func<string, int> get, bool spell)
    {
        int increased = Increased(get, spell);
        double mod = Math.Max(0, 1 + increased / 100.0);
        foreach (string more in MoreArea) mod *= Math.Max(0, 1 + get(more) / 100.0);
        foreach (string more in SupportMoreArea) mod *= Math.Max(0, 1 + get(more) / 100.0);
        // Discharge: more area per removable charge it will spend (the charges above the minimum).
        if (get(MorePerRemovableCharge) is int perCharge and not 0)
            mod *= Math.Max(0, 1 + perCharge * Charges.Types.Sum(t => Math.Max(0, get($"current_removable_{Charges.Name(t)}_charges"))) / 100.0);
        return mod;
    }

    /// <summary>The ActiveSkillTypes a skill's type-scoped increased area follows (<see cref="TypedIncrease"/>).</summary>
    public readonly record struct Types(bool Melee, bool Attack, bool Warcry, bool Vaal, bool Totem, bool Trap, bool Mine);

    /// <summary>The increased area scoped to a skill's types (passives and items: "increased Area of Effect for Melee
    /// Skills", "... for Attacks" and so on), which the panel total skill_area_of_effect_+% does not hold. A Herald has
    /// no such stat in this build.</summary>
    public static int TypedIncrease(Func<string, int> get, Types types) =>
        (types.Melee ? get("melee_skills_area_of_effect_+%") : 0) + (types.Attack ? get("attack_area_of_effect_+%") : 0)
        + (types.Warcry ? get("warcry_skill_area_of_effect_+%") : 0) + (types.Vaal ? get("vaal_skills_area_of_effect_+%") : 0)
        + (types.Totem ? get("totem_skill_area_of_effect_+%") : 0) + (types.Trap ? get("trap_skill_area_of_effect_+%") : 0)
        + (types.Mine ? get("mine_area_of_effect_+%") : 0) + (types.Trap || types.Mine ? get("trap_and_mine_area_of_effect_+%") : 0);

    /// <summary>The panel total of increased area, where <see cref="TypedIncrease"/> is added for a skill.</summary>
    public const string Increase = "skill_area_of_effect_+%";

    /// <summary>The radius in cells for <paramref name="baseRadius"/> (the skill's base when it has none of its own).</summary>
    public static int Radius(int baseRadius, Func<string, int> get, bool spell) => Radius(baseRadius, get, spell, 1);

    public static int Radius(int baseRadius, Func<string, int> get, bool spell, double moreArea)
    {
        int radius = baseRadius + get("active_skill_base_radius_+");
        double scaled = radius * Math.Floor(100 * Math.Sqrt(Modifier(get, spell) * moreArea)) / 100;
        scaled *= Math.Max(0, 1 + get("active_skill_area_of_effect_radius_+%_final") / 100.0);
        return Math.Max(0, (int)Math.Floor(scaled));
    }
}

/// <summary>
/// Ground Slam's wave: the slam sends a sector forward from the player and its front advances to the skill's reach,
/// instead of dropping one circle on the aimed point. The exported tables carry no shape for it. The player's gem
/// (SkillGems row 13 → GemEffects "GroundSlam" → GrantedEffects "GroundSlam" → ActiveSkills "ground_slam" → ActionTypes
/// "DistanceScaledGroundSlam") has a per-level <c>active_skill_base_radius_+</c> (0 at gem level 1, 18 cells at 40) and
/// its alternate-quality form a flat <c>ground_slam_angle_+%</c> 100, but no base radius, no cone angle, no length and
/// no speed. <c>ground_slam_cone_angle_override</c>, <c>ground_slam_radius_+%</c> and
/// <c>ground_slam_retarget_after_contact_point</c> are defined in Stats.dat and no player stat set holds them (the
/// skill reads 0); <c>ground_slam_angle_+%</c> and <c>groundslam_damage_to_close_targets_+%_final</c> are the only two
/// that a player's stat set does hold. The old "ExileGroundSlam" row, whose +40% final radius and 4 melee repeats the
/// task notes once attributed to the player's gem, is granted by no GemEffects row at all.
/// The length therefore rides on the skill's area radius (SkillArea) and the three numbers below are the wave's only
/// tunables: they are ESTIMATES to calibrate against the live client (stand at a known distance in front, and time the
/// hit at a known distance).
/// </summary>
public static class GroundSlamWave
{
    /// <summary>ActionTypes "DistanceScaledGroundSlam" HASH16 (row 3): the action of the player's Ground Slam and its
    /// alternate-quality and Royale forms (GrantedEffects GroundSlam, GroundSlamAltX, GroundSlamAltY, GroundSlamRoyale
    /// and PlaytestSlam). Vaal Ground Slam's own skill is "VaalDistanceScaledGroundSlam" (row 4), so the Vaal blast keeps
    /// its radial hit.</summary>
    public const ushort Action = 0x2390;

    /// <summary>The wave's reach in cells at the generic area radius (12 cells). The exported tables carry no length;
    /// the community describes Ground Slam's wave as about 60 units and the client draws it well past melee reach, so
    /// this is an ESTIMATE calibrated against the live client (stand at a known distance in front of a monster).</summary>
    public const double BaseLength = 60;

    /// <summary>The area radius the base length above is quoted at (<see cref="BaseRadius"/>'s generic value): the
    /// gem's own per-level active_skill_base_radius_+ is added to the length, so a higher-level gem reaches further.</summary>
    public const int GenericAreaRadius = 12;

    /// <summary>The wave's half angle in degrees before <c>ground_slam_angle_+%</c>: half of the cone
    /// <c>ground_slam_cone_angle_override</c> names (60 degrees across, not on each side), which no player stat set
    /// carries, so it is a constant here.</summary>
    public const double HalfAngleDegrees = 30;

    /// <summary>How fast the wave's front travels, in cells a second (10 cells = 1 m). ESTIMATE: no table carries a
    /// speed. The live client plays the whole slam in one 0.633 s clip, so the front has to cross the wave's reach
    /// well inside that: at 60 cells a second a monster 60 cells away took its hit a whole second after the
    /// animation was over (measured live 2026-11-07), which reads as damage arriving late. 400 lands the farthest
    /// reach in about 0.15 s and keeps the nearest-first stagger.</summary>
    public const double CellsPerSecond = 400;

    /// <summary>The reach in cells of the wave a skill of <paramref name="areaRadius"/> cells sends: the base length,
    /// moved by however far the skill's own radius sits from the generic one (so the gem's per-level radius, a
    /// support's more area and the player's area stats all lengthen it without the level×base runaway a plain
    /// multiple of the radius gives).</summary>
    public static double Length(int areaRadius) => Math.Max(0, BaseLength + (areaRadius - GenericAreaRadius));

    /// <summary>The half angle in degrees this use sends: the estimate above, grown by the skill's own
    /// <c>ground_slam_angle_+%</c> (Ground Slam's alternate-quality form holds a flat 100), capped at a right angle so an
    /// increased angle never reaches behind the player.</summary>
    public static double HalfAngle(Func<string, int> get) =>
        Math.Clamp(HalfAngleDegrees * (1 + get("ground_slam_angle_+%") / 100.0), 0, 90);

    /// <summary>Seconds after the slam's contact the front reaches a monster <paramref name="distance"/> cells away.</summary>
    public static double AdvanceSeconds(double distance) => Math.Max(0, distance) / CellsPerSecond;

    /// <summary>Scale the gem's close-target hit bonus over the wave's reach, using the same falloff as Vaal Ground Slam.
    /// The distance is the contact's distance from the original slam, so moving after release does not change it.</summary>
    public static double CloseHitMultiplier(double distance, double length, int maximumPercent) =>
        1 + maximumPercent / 100.0 * Math.Clamp(1 - distance / Math.Max(1, length), 0, 1);

    /// <summary>The wave's unit direction: from where the player stands toward what it aimed at, or (0, 0) when the two
    /// are the same cell, which is the one case a wave has no direction of its own.</summary>
    public static (double X, double Y) Direction((int X, int Y) from, (int X, int Y) to)
    {
        double x = to.X - from.X, y = to.Y - from.Y;
        double length = Math.Sqrt(x * x + y * y);
        return length < 1 ? (0, 0) : (x / length, y / length);
    }

    /// <summary>Whether a monster at <paramref name="at"/> is inside the wave: within <paramref name="length"/> cells of
    /// <paramref name="from"/> and within <paramref name="halfAngleDegrees"/> of its direction. A zero direction (the
    /// player was aimed at their own feet) and a monster on top of the player are inside every direction; what is behind
    /// the player never is.</summary>
    public static bool Covers((double X, double Y) direction, (int X, int Y) from, (int X, int Y) at,
        double halfAngleDegrees, double length)
    {
        double x = at.X - from.X, y = at.Y - from.Y;
        double distance = Math.Sqrt(x * x + y * y);
        if (distance > length) return false;
        if (direction.X == 0 && direction.Y == 0 || distance <= 1) return true;
        double cos = Math.Clamp((x * direction.X + y * direction.Y) / distance, -1, 1);
        return Math.Acos(cos) * 180 / Math.PI <= halfAngleDegrees;
    }
}
