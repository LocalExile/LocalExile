using GameServer.Core;

/// <summary>
/// What the client's action classes (ActiveSkills.ActionType → ActionTypes Id) do on their own that the tables do not
/// say, for any skill of the class, monster's or player's: the radius the class adds to the skill's base area, the
/// projectile it creates, the ground it leaves and the debuffs it puts on what it hits. A skill's own stats size and time
/// each of them; this table only says which the class has. A class that is not here adds nothing.
/// <para>Client evidence (3.29.3c, the class's factory from <c>FUN_141947300("Name", factory)</c>, its vtable's own
/// functions): each radius is the constant its area scaler call adds, <c>FUN_14193efd0</c>/<c>FUN_14193f1f0</c>
/// computing pct × (active_skill_base_area_of_effect_radius + base_radius_+ + constant) / 100:
/// ColdSnap FUN_14130bc40 +15, CorpseErruption FUN_141adbae0 +15, StormCall FUN_1414d2b80 +20, LightningWarp
/// FUN_14138f120 +16, RainOfArrows FUN_141439820/FUN_141bd7150 +24, BlastRain FUN_141b5faf0 24, TrapGroundFire
/// FUN_141ce4510 +16, Wither FUN_141412ee0 18, VaalColdSnap FUN_141cc33b0 60; CausticArrow FUN_1412e1450 reads
/// caustic_arrow_explode_on_hit_base_area_of_effect_radius instead. The projectile resolvers: TetheringArrow
/// FUN_141ab3dc0 (skill_art_variation), TotemSnipe FUN_141a85c90 (totem_art_variation 12), Flamethrower FUN_141b85f40
/// (skill_art_variation, the first stage). The farthest a spell class acts (its vtable +0x160, which the player's click
/// and a monster's cast both stop at): the spell base FUN_1419e9eb0 is the class's +0x4b8 constant (120 for nearly every
/// class: FUN_140168470; Skeletal Chains' 80) × (100 + spell_maximum_action_distance_+%) / 100; the projectile spells'
/// FUN_141ba6f90 128 × the same; Bone Lance's FUN_141249a80 a flat 90; Ethereal Knives' FUN_1419f57f0
/// 45 × (100 + projectile_speed_+%) / 100.</para>
/// <para>The grounds and debuffs are the BuffDefinitions rows whose GrantedStats are the skill's own stats
/// (<see cref="BuffStats"/>): Fire Trap's base_fire_damage_to_deal_per_minute is ground_fire_burn's
/// base_fire_damage_taken_per_minute, Bear Trap's no_movement_speed is beartrap_debuff's, Wither's
/// base_movement_velocity_+% and chaos_damage_taken_+% are hinder's and withered's, Ensnaring Arrow's
/// tethered_movement_speed_+%_final_per_rope is tethered_debuff's.</para>
/// </summary>
public static class ActionClasses
{
    /// <summary>Where a class's ground lies: where its area landed, where its projectile ended, or under the trap that
    /// went off (whose action draws it: the server sends no ground object of its own).</summary>
    public enum GroundAt { Landing, ProjectileEnd, Trap }

    /// <summary>A ground a class leaves: its GroundEffectTypes Id, the stat naming its GroundEffects variation, where it
    /// lies, and the stat its time comes from.</summary>
    public sealed record GroundSpec(string Type, uint? VariationStat, GroundAt At, uint DurationStat = Stat.BaseSkillEffectDuration);

    /// <summary>A debuff a class puts on what it hits: its BuffDefinitions Id and the stat its time comes from.</summary>
    public sealed record DebuffSpec(string Buff, uint DurationStat);

    /// <summary>One action class: see <see cref="ActionClasses"/>.</summary>
    public sealed record ActionClass(string Id)
    {
        /// <summary>Added to active_skill_base_area_of_effect_radius before the area modifiers scale it.</summary>
        public int? BaseRadius { get; init; }
        /// <summary>A stat the class reads as its base radius in place of active_skill_base_area_of_effect_radius.</summary>
        public uint? RadiusStat { get; init; }
        /// <summary>The projectile the class creates, from the skill's stats (by name); null when it names its own row
        /// through the art tables.</summary>
        public Func<Func<uint, int>, string>? Projectile { get; init; }
        /// <summary>The farthest the class acts (cells), from the skill's stats; null keeps the spell base's.</summary>
        public Func<Func<uint, int>, int>? ActionDistance { get; init; }
        public GroundSpec? Ground { get; init; }
        public IReadOnlyList<DebuffSpec> Debuffs { get; init; } = [];
        /// <summary>A class that acts on its user: the BuffDefinitions Id it puts on it, by the skill. Such a skill is a
        /// buff on its user (MonsterSkills.ShapeOf), whatever else its types say.</summary>
        public Func<SkillCatalog, int, string>? SelfBuff { get; init; }
    }

    // The classes on the projectile spell base (their +0x160 is FUN_141ba6f90).
    static readonly string[] ProjectileSpellClasses =
        ["BallLightning", "Fireball", "FrostBolt", "IceSpear", "LightningProjectileTrap", "Siphon", "Spark"];

    static readonly Dictionary<string, ActionClass> Table = new ActionClass[]
    {
        new("ColdSnap") { BaseRadius = 15 },
        // Molten Shell: the Guard skill's shield is molten_shell_shield; the older threshold form (a Rogue Exile's
        // molten_shell_monster, fire_shield_damage_threshold) is fire_shield. Both grant the skill's armour stats.
        new("FireShield") { SelfBuff = (skills, effect) => skills.HasType(effect, "Guard") ? "molten_shell_shield" : "fire_shield" },
        new("CorpseErruption") { BaseRadius = 15 },
        new("StormCall") { BaseRadius = 20 },
        new("LightningWarp") { BaseRadius = 16 },
        new("RainOfArrows") { BaseRadius = 24 },
        new("BlastRain") { BaseRadius = 24 },
        new("VaalColdSnap") { BaseRadius = 60 },
        new("TrapGroundFire") { BaseRadius = 16, Ground = new("Fire", null, GroundAt.Trap) },
        new("TrapBeartrap") { Debuffs = [new("beartrap_debuff", Stat.BaseSkillEffectDuration)] },
        new("Wither")
        {
            BaseRadius = 18,
            Debuffs = [new("hinder", Stat.BaseSkillEffectDuration), new("withered", Stat.BaseSecondarySkillEffectDuration)],
        },
        new("CausticArrow")
        {
            RadiusStat = Stat.CausticArrowExplodeOnHitBaseAreaOfEffectRadius,
            Ground = new("CausticCloud", Stat.GroundCausticArtVariation, GroundAt.ProjectileEnd),
        },
        new("Desecrate") { Ground = new("Desecration", null, GroundAt.Landing) },
        new("TetheringArrow")
        {
            Projectile = get => get(Stat.SkillArtVariation) switch
            {
                1 => "Metadata/Projectiles/HeistThugEnsnaringTotemArrow",
                2 or 4 => "Metadata/Projectiles/WebTetheringArrow",
                3 => "Metadata/Projectiles/TetheringArrowDemonic",
                5 => "Metadata/Projectiles/AncestralSpearProjectile",
                _ => "Metadata/Projectiles/TetheringArrow",
            },
            Debuffs = [new("tethered_debuff", Stat.BaseSkillEffectDuration)],
        },
        new("TotemSnipe")
        {
            Projectile = get => get(Stat.TotemArtVariation) == 12
                ? "Metadata/Projectiles/CrossbowSnipeDemonicArcticProjectile" : "Metadata/Projectiles/CrossbowSnipeProjectile",
        },
        new("Flamethrower")
        {
            ActionDistance = get => SpellDistance(128, get),
            Projectile = get => get(Stat.SkillArtVariation) switch
            {
                1 => "Metadata/Projectiles/FlamethrowerBlue1",
                2 => "Metadata/Projectiles/IncinerateHoly",
                3 => "Metadata/Projectiles/GhostProjectile",
                4 => "Metadata/Projectiles/IncinerateKitavaMinion",
                _ => "Metadata/Projectiles/Flamethrower1",
            },
        },
        new("BoneLance") { ActionDistance = _ => 90 },
        new("ShadowProjectiles") { ActionDistance = get => 45 * (100 + get(Stat.ProjectileSpeedPlusPct)) / 100 },
        new("SkeletalChains") { ActionDistance = get => SpellDistance(80, get) },
    }.Concat(ProjectileSpellClasses.Select(id => new ActionClass(id) { ActionDistance = get => SpellDistance(128, get) }))
        .ToDictionary(c => c.Id, StringComparer.Ordinal);

    static int SpellDistance(int basis, Func<uint, int> get) => basis * Math.Max(0, 100 + get(Stat.SpellMaximumActionDistancePlusPct)) / 100;

    /// <summary>The farthest a spell acts, in cells: its class's distance, else the spell base's 120, each scaled by
    /// the skill's stats (see <see cref="ActionClasses"/>).</summary>
    public static int SpellActionDistance(int effect, Func<uint, int> get) =>
        OfEffect(effect)?.ActionDistance?.Invoke(get) ?? SpellDistance(120, get);

    /// <summary>The class by its ActionTypes Id; null for one that adds nothing.</summary>
    public static ActionClass? Of(string? actionType) => actionType is not null ? Table.GetValueOrDefault(actionType) : null;

    /// <summary>The class of a GrantedEffects row (its ActiveSkills ActionType).</summary>
    public static ActionClass? OfEffect(int effect) => Of(SkillCatalog.ActionTypeId(effect));

    /// <summary>The stats a buff row's GrantedStats take from a skill's (by name): the stat itself; else its per-unit
    /// form (<c>X_per_rope</c>: tethered_movement_speed_+%_final); a <c>base_{type}_damage_taken_per_minute</c> the
    /// skill's <c>base_{type}_damage_to_deal_per_minute</c>; a flag (is_, on_, no_) 1. A stat the skill gives none of
    /// is left out. Empty for a row the tables do not have.</summary>
    public static Dictionary<uint, int> BuffStats(ushort row, Func<uint, int> skill, IEnumerable<string>? skillStats = null)
    {
        Dictionary<uint, int> result = new();
        if (BuffRows.Of(row) is not { } info) return result;
        foreach (uint id in info.GrantedStats)
        {
            // is_moving is the player's own state, which the client keeps: the buff reads it, it grants none.
            if (id == Stat.IsMoving) continue;
            string name = Stats.Name(id);
            int value = skill(id);
            if (value == 0 && skillStats is not null
                && skillStats.FirstOrDefault(s => s.StartsWith(name + "_per_", StringComparison.Ordinal)) is { } perUnit)
                value = skill(Stat.Of(perUnit));
            if (value == 0 && name.EndsWith("_damage_taken_per_minute", StringComparison.Ordinal))
                value = skill(Stat.Of(name.Replace("_damage_taken_per_minute", "_damage_to_deal_per_minute", StringComparison.Ordinal)));
            if (value == 0 && (name.StartsWith("is_", StringComparison.Ordinal) || name.StartsWith("on_", StringComparison.Ordinal)
                || name.StartsWith("no_", StringComparison.Ordinal)))
                value = 1;
            if (value != 0) result[id] = value;
        }
        return result;
    }
}
