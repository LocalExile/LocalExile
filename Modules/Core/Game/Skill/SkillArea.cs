

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
    public static int BaseRadius(string? effect, Func<uint, int> get) =>
        get(Stat.ActiveSkillBaseAreaOfEffectRadius) is int radius and > 0 ? radius : KnownBaseRadius(effect) ?? 12;

    static int? KnownBaseRadius(string? effect) =>
        effect is "LightningWarp" or "VaalLightningWarpInstant" ? 16
            : effect == "VaalGroundSlam" ? 40 : IsCyclone(effect) ? 16 : effect == "RollingMagma" ? 14
            : effect is "Earthquake" or "Earthshatter" ? 18
            : effect == "Cleave" ? 20
            : effect == "TectonicSlam" ? 15
            : GatewayProjectileSkillProfiles.IsShatteringSteel(effect) ? 28
            : effect is "FireTrap" or "VaalFireTrap" ? 16 : effect == "StormblastMine" ? 20 : effect is "DarkPact" or "DarkPactRoyale" ? 26
            : effect is "StormCall" or "StormCallAltX" or "Frostblink" or "FrostblinkHardMode" or "FrostblinkAltX" || Game.IsPyroclastMine(effect) ? 20
            : effect is "GalvanicArrow" or "GalvanicArrowAltX" or "GalvanicArrowAltY" ? 28
            : effect is "StormBrand" or "StormBrandAltX" or "StormBrandAltY"
                or "PenanceBrand" or "PenanceBrandAltX" or "PenanceBrandAltY"
                or "WintertideBrand" or "WintertideBrandAltX" or "WintertideBrandAltY" ? 22
            : effect == "Brandsurge" ? 26
            : effect == "EarthquakeAltX" ? 18
            : effect == "TectonicSlamAltX" ? 15
            : GatewayProjectileSkillProfiles.IsMoltenStrike(effect) ? 9 : 12;

    static readonly uint[] MoreArea =
    [
        Stat.ActiveSkillAreaOfEffectPlusPctFinal, Stat.SkillAreaOfEffectPlusPctFinal,
        Stat.SupportConcentratedEffectSkillAreaOfEffectPlusPctFinal, Stat.SkillAreaOfEffectPlusPctFinalFromSkillSpecificStat,
        Stat.BloodSandStanceMeleeSkillsAreaOfEffectPlusPctFinal, Stat.MapPlayersSkillAreaOfEffectPlusPctFinal,
        Stat.GladiatorAreaOfEffectPlusPctFinalWhileWieldingMace,
    ];

    const uint MorePerRemovableCharge = Stat.AreaOfEffectPlusPctFinalPerRemovablePowerFrenzyOrEnduranceCharge;

    /// <summary>The base form of increased area: the player's goes into the panel total <c>skill_area_of_effect_+%</c>
    /// (PanelStats), a skill's own (gem quality) stays in it.</summary>
    public const uint BaseIncreased = Stat.BaseSkillAreaOfEffectPlusPct;

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
    public static int Increased(Func<uint, int> get, bool spell) =>
        get(Stat.BaseSkillAreaOfEffectPlusPct) + get(Stat.SkillAreaOfEffectPlusPct) + (spell ? get(Stat.SpellAreaOfEffectPlusPct) : 0);

    /// <summary>The client's precomputed radius percentage (0x1591), not the increased area percentage.</summary>
    public static int RadiusPercent(Func<uint, int> get, bool spell) =>
        (int)Math.Clamp((long)Math.Floor(100 * Math.Sqrt(Modifier(get, spell)))
            * Math.Max(0L, 100L + get(Stat.ActiveSkillAreaOfEffectRadiusPlusPctFinal)) / 100 - 100, -100, int.MaxValue);

    /// <summary>Supports' more or less area, each a "_final" multiplier on the supported skill: Spell Cascade, Arcanist
    /// Brand, Corrupting Cry and Blessed Call (only a warcry carries the last two).</summary>
    static readonly uint[] SupportMoreArea =
    [
        Stat.SupportSpellCascadeAreaOfEffectPlusPctFinal, Stat.SupportBrandAreaOfEffectPlusPctFinal,
        Stat.SupportCorruptingCryAreaOfEffectPlusPctFinal, Stat.ConsecratedCryWarcryAreaOfEffectPlusPctFinal,
        Stat.SupportSpellBoostAreaOfEffectPlusPctFinal, Stat.SupportPulveriseAreaOfEffectPlusPctFinal,
    ];

    /// <summary>Greater Spell Echo: increased area for each repeat of the spell (the first cast is repeat 0).</summary>
    public const uint IncreasedPerRepeat = Stat.SupportGreaterSpellEchoAreaOfEffectPlusPctPerRepeat;

    /// <summary>A "more area" percentage as the radius multiplier it gives: the square root of (1 + more), as the area
    /// modifier is (Fist of War's 59% more area is ×1.26 radius, not ×1.59).</summary>
    public static double RadiusMultiplier(int moreAreaPercent) => Math.Sqrt(Math.Max(0, 1 + moreAreaPercent / 100.0));

    /// <summary>The area modifier: (1 + increased) × each more multiplier, at least 0.</summary>
    public static double Modifier(Func<uint, int> get, bool spell)
    {
        int increased = Increased(get, spell);
        double mod = Math.Max(0, 1 + increased / 100.0);
        foreach (uint more in MoreArea) mod *= Math.Max(0, 1 + get(more) / 100.0);
        foreach (uint more in SupportMoreArea) mod *= Math.Max(0, 1 + get(more) / 100.0);
        if (get(Stat.IsTotem) != 0)
            mod *= Math.Max(0, 1 + get(Stat.StygianSpireTotemAreaOfEffectPlusPctFinalPerMaximumTotem) / 100.0);
        // Discharge: more area per removable charge it will spend (the charges above the minimum).
        if (get(MorePerRemovableCharge) is int perCharge and not 0)
            mod *= Math.Max(0, 1 + perCharge * Charges.Types.Sum(t => Math.Max(0, get(Stat.Of($"current_removable_{Charges.Name(t)}_charges")))) / 100.0);
        return mod;
    }

    /// <summary>The ActiveSkillTypes a skill's type-scoped increased area follows (<see cref="TypedIncrease"/>).</summary>
    public readonly record struct Types(bool Melee, bool Attack, bool Warcry, bool Vaal, bool Totem, bool Trap, bool Mine);

    /// <summary>The increased area scoped to a skill's types (passives and items: "increased Area of Effect for Melee
    /// Skills", "... for Attacks" and so on), which the panel total skill_area_of_effect_+% does not hold. A Herald has
    /// no such stat in this build.</summary>
    public static int TypedIncrease(Func<uint, int> get, Types types) =>
        (types.Melee ? get(Stat.MeleeSkillsAreaOfEffectPlusPct) : 0) + (types.Attack ? get(Stat.AttackAreaOfEffectPlusPct) : 0)
        + (types.Warcry ? get(Stat.WarcrySkillAreaOfEffectPlusPct) : 0) + (types.Vaal ? get(Stat.VaalSkillsAreaOfEffectPlusPct) : 0)
        + (types.Totem ? get(Stat.TotemSkillAreaOfEffectPlusPct) : 0) + (types.Trap ? get(Stat.TrapSkillAreaOfEffectPlusPct) : 0)
        + (types.Mine ? get(Stat.MineAreaOfEffectPlusPct) : 0) + (types.Trap || types.Mine ? get(Stat.TrapAndMineAreaOfEffectPlusPct) : 0);

    /// <summary>The panel total of increased area, where <see cref="TypedIncrease"/> is added for a skill.</summary>
    public const uint Increase = Stat.SkillAreaOfEffectPlusPct;

    /// <summary>The radius in cells for <paramref name="baseRadius"/> (the skill's base when it has none of its own).</summary>
    public static int Radius(int baseRadius, Func<uint, int> get, bool spell) => Radius(baseRadius, get, spell, 1);

    internal static int Radius(int baseRadius, Func<uint, int> get, bool spell, double areaMore)
    {
        int radius = baseRadius + get(Stat.ActiveSkillBaseRadiusPlus);
        double scaled = radius * Math.Floor(100 * Math.Sqrt(Modifier(get, spell) * areaMore)) / 100;
        scaled *= Math.Max(0, 1 + get(Stat.ActiveSkillAreaOfEffectRadiusPlusPctFinal) / 100.0);
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

    // Volcanic Fissure and older callers use the original bounded sweep.
    public static double Length(int areaRadius) => Math.Max(0, BaseLength + areaRadius - GenericAreaRadius);

    /// <summary>The reach in cells of the wave a skill of <paramref name="areaRadius"/> cells sends: at 0% AoE it grows
    /// by <paramref name="gemBasePlus"/> (e.g. gem level's active_skill_base_radius_+) without runaway multiplication, and
    /// scales proportionally with increased/more area (including Fist of War).</summary>
    public static double Length(int areaRadius, int gemBasePlus)
    {
        double baseScaleRadius = Math.Max(1, GenericAreaRadius + Math.Max(0, gemBasePlus));
        double baseLength = Math.Max(0, BaseLength + Math.Max(0, gemBasePlus));
        return Math.Max(0, baseLength * Math.Max(0, areaRadius) / baseScaleRadius);
    }

    /// <summary>The half angle in degrees this use sends: the estimate above, grown by the skill's own
    /// <c>ground_slam_angle_+%</c> (Ground Slam's alternate-quality form holds a flat 100), capped at a right angle so an
    /// increased angle never reaches behind the player.</summary>
    public static double HalfAngle(Func<uint, int> get) =>
        Math.Clamp(HalfAngleDegrees * (1 + get(Stat.GroundSlamAnglePlusPct) / 100.0), 0, 90);

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
