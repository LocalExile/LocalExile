using GameServer.Core;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Which Animation.dat row a skill's action asks its actor's .act for, and the clip a weapon-stanced monster (a Rogue
/// Exile, whose .act has stances as the character classes' do) plays for it, with its contact.
/// <para>The row is the action class's, read from the client's code by <c>scripts/re/action_animations.py</c>
/// (ActiveSkills.ActionType names the class): the start of each passes a row to the projectile (0x141ba58f0), attack
/// (0x141b58830, 0x141c9af80), melee (0x141c9b060) or bow and thrown (0x141b720e0: GrantedEffects.Animation, else the
/// row it passes) routine, the spell base (0x1419e9840) asks its virtual +0x4e0, the war cries' start (0x141abf4b0)
/// +0x4d8. A channel or a walk (Cyclone, Blade Flurry, Charged Dash, Flicker Strike, Shield Charge) plays another way and
/// has none here.</para>
/// <para>The stance is the hands' ItemStances Ids, main hand first (OneHandSword + Shield = OneHandSwordShield, Bow +
/// Quiver = BowQuiver; none for an empty hand), as the .act names its stance blocks; a stance plays its own pool for
/// the row when it has one (monster_stance_clips "&lt;stance&gt;/&lt;key&gt;"), else the root's.</para>
/// </summary>
public static class SkillAnimations
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadGameData()
    {
        StanceNames = [.. ClientTables.TableOrEmpty("ItemStances").Select(r => r.Text("Id") ?? "")];
        Stanced = LoadStanced();
    }

    /// <summary>The row each action class plays whatever its stats.</summary>
    static readonly Dictionary<string, string> Fixed = new(StringComparer.Ordinal)
    {
        // Projectile spells (0x141ba58f0).
        ["BallLightning"] = "SpellProjectileLightning", ["BoneLance"] = "SpellProjectile", ["Fireball"] = "SpellProjectileFire",
        ["Flamethrower"] = "CastFast", ["FrostBolt"] = "SpellProjectileCold", ["IceSpear"] = "SpellProjectileCold",
        ["LightningProjectileTrap"] = "SpellProjectile", ["ShadowProjectiles"] = "ShadowProjectiles",
        ["Siphon"] = "SpellProjectileChaos", ["Spark"] = "SpellProjectileLightning",
        // The spell base's virtual +0x4e0.
        ["Arc"] = "SpellProjectileLightning", ["BloodRage"] = "SpellBuffFast", ["ChargedBlast"] = "CastFast",
        ["ColdSnap"] = "SpellAreaOfEffectCold", ["Contagion"] = "SpellAreaOfEffectChaos", ["CorpseErruption"] = "SpellAreaOfEffect",
        ["CorpseWarp"] = "SpellAreaOfEffect", ["Desecrate"] = "SpellAreaOfEffectChaos", ["DrainEssence"] = "SpellAreaOfEffect",
        ["Enrage"] = "SpellAreaOfEffect", ["FireShield"] = "SpellBuff", ["FireStorm"] = "SpellAreaOfEffectFire",
        ["FlameDash"] = "SpellProjectileFire", ["FrostBoltNova"] = "SpellAreaOfEffectCold", ["FrostWall"] = "SpellAreaOfEffectCold",
        ["ImmortalCall"] = "SpellBuff", ["LightningWarp"] = "SpellAreaOfEffectLightning", ["NewArcticArmour"] = "SpellBuffCold",
        ["OfferingOffensive"] = "SpellAreaOfEffect", ["QuickDodge"] = "SpellAreaOfEffect", ["RaiseSkeleton"] = "SpellAreaOfEffect",
        ["RaiseZombie"] = "SpellAreaOfEffect", ["RighteousFire"] = "SpellBuffFire", ["SearingBond"] = "SpellAreaOfEffect",
        ["SkeletalChains"] = "SpellProjectileChaos", ["Slither"] = "SpellBuffFast", ["SummonElemental"] = "SpellProjectileFire",
        ["SummonIceElemental"] = "SpellAreaOfEffect", ["TrapBeartrap"] = "SpellAreaOfEffect", ["TrapGroundFire"] = "SpellAreaOfEffectFire",
        ["VaalAuraDodge"] = "SpellBuff", ["VaalAuraSpeed"] = "SpellBuff", ["VaalColdSnap"] = "SpellAreaOfEffect",
        ["VaalStormCall"] = "SpellAreaOfEffectLightning", ["VaalSummonSkeletons"] = "SpellAreaOfEffect",
        ["VolatileDead"] = "SpellAreaOfEffect", ["Wither"] = "CastFast",
        ["CastAuraArmour"] = "SpellAreaOfEffect", ["CastAuraColdDamage"] = "SpellAreaOfEffect",
        ["CastAuraColdResistance"] = "SpellAreaOfEffect", ["CastAuraEnergyShield"] = "SpellAreaOfEffect",
        ["CastAuraEvasion"] = "SpellAreaOfEffect", ["CastAuraFireDamage"] = "SpellAreaOfEffect",
        ["CastAuraFireResistance"] = "SpellAreaOfEffect", ["CastAuraLifeRegen"] = "SpellAreaOfEffect",
        ["CastAuraLightningDamage"] = "SpellAreaOfEffect", ["CastAuraLightningResistance"] = "SpellAreaOfEffect",
        ["CastAuraPhysicalDamage"] = "SpellAreaOfEffect", ["CastAuraSpeed"] = "SpellAreaOfEffect",
        ["ColdWeakness"] = "SpellAreaOfEffect", ["ElementalWeakness"] = "SpellAreaOfEffect", ["Enfeeble"] = "SpellAreaOfEffect",
        ["FireWeakness"] = "SpellAreaOfEffect", ["LightningWeakness"] = "SpellAreaOfEffect",
        ["ProjectileWeakness"] = "SpellAreaOfEffect", ["TemporalChains"] = "SpellAreaOfEffect", ["Vulnerability"] = "SpellAreaOfEffect",
        // Attacks (0x141b58830; Dual Strike through 0x141c9af80).
        ["BlastRain"] = "BlastRain", ["BloodSpears"] = "GroundSlam", ["Cleave"] = "Cleave", ["CloneShot"] = "CloneShot",
        ["DualStrike"] = "DualStrike", ["GeometryAttack"] = "GroundSlam", ["GroundSlam"] = "GroundSlam", ["IceCrash_"] = "GroundSlam",
        ["LeapSlam"] = "LeapSlam", ["QuakeSlam"] = "GroundSlam", ["RainOfArrows"] = "RainOfArrows", ["RainOfSpores"] = "RainOfSpores",
        ["Reave"] = "Melee", ["ShockwaveSlam"] = "GroundSlam", ["UtulaRemoteSlam"] = "GroundSlam", ["VaalGroundSlam"] = "GroundSlam",
        // The melee family (0x141c9b060).
        ["DoubleStrike"] = "DoubleStrike", ["ElementalHit"] = "Melee", ["Frenzy"] = "Frenzy", ["GlacialHammer"] = "GlacialHammer",
        ["HeavyStrike"] = "HeavyStrike", ["LightningStrike"] = "Melee", ["Melee"] = "Melee", ["Puncture"] = "Puncture",
        ["StaticStrike"] = "Melee", ["VaalGlacialHammer"] = "HeavyStrike",
        // War cries (+0x4d8).
        ["EnduringCry"] = "Warcry", ["AbyssalCry"] = "SpellBuffFast",
    };

    /// <summary>Bow and thrown attacks (0x141b720e0): the skill's GrantedEffects.Animation, else this row.</summary>
    static readonly Dictionary<string, string> FromEffect = new(StringComparer.Ordinal)
    {
        ["Barrage"] = "Melee", ["BurningArrow"] = "Melee", ["CausticArrow"] = "Melee", ["ClusterBurst"] = "Melee",
        ["IceShot"] = "Melee", ["LightningArrow"] = "Melee", ["TetheringArrow"] = "Melee", ["ThrownWeaponProjectile"] = "Melee",
        ["TornadoShot"] = "Melee", ["TotemSnipe"] = "Snipe",
    };

    /// <summary>The Animation.dat Id the skill's action asks the actor for; null for an action not read (a channel, a walk,
    /// a class no Rogue Exile uses).</summary>
    public static string? Key(SkillCatalog skills, int effect, bool dualWielding)
    {
        if (ActionType(effect) is not { } action) return null;
        if (Fixed.TryGetValue(action, out string? key)) return key;
        if (FromEffect.TryGetValue(action, out key)) return EffectAnimation(effect) ?? key;
        int Value(uint k) => skills.EffectStats(effect, 1).FirstOrDefault(s => s.Item1 == k).Item2;
        return action switch
        {
            // 0x141bedb60: the hands' mode (0x1419e7570) is 3 when both hold weapons: 0x141c9af80 with DualStrike.
            "ViperStrike" => dualWielding ? "DualStrike" : "Melee",
            // 0x1414d2c20: skill_behaviour_variation 1 plays SpellAreaOfEffect.
            "StormCall" => Value(Stat.SkillBehaviourVariation) == 1 ? "SpellAreaOfEffect" : "SpellAreaOfEffectLightning",
            // 0x141a6a0b0: GroundSlam with skill_behaviour_variation 2 or skill_art_variation 1, else Melee (the fifth
            // attack's projectiles change no row).
            "MoltenStrike" => Value(Stat.SkillBehaviourVariation) == 2 || Value(Stat.SkillArtVariation) == 1 ? "GroundSlam" : "Melee",
            _ => null,
        };
    }

    /// <summary>The .act stance the hands' items select (VisibleItem.Stance is ItemClasses.ItemStance); "" for empty hands.</summary>
    public static string StanceName(IReadOnlyList<VisibleItem> held)
    {
        var names = StanceNames;
        string Name(byte slot) => held.FirstOrDefault(h => h.Slot == slot) is { Stance: var s and not 0xff } && s < names.Count ? names[s] : "";
        return Name(0) + Name(1);
    }

    /// <summary>The clip a stanced monster plays for the action, chosen as the client chooses one (the native selector seeded
    /// with the action's sequence); null when its .act has no stances or no pool for the row.</summary>
    public static SkillCatalog.MonsterClipContact? StanceClip(string varietyId, string stance, string key, ushort sequence)
    {
        if (!Stanced.TryGetValue(varietyId, out var pools)) return null;
        if (!(stance.Length > 0 && pools.TryGetValue($"{stance}/{key}", out var pool)) && !pools.TryGetValue(key, out pool)) return null;
        Span<uint> weights = pool.Length <= 32 ? stackalloc uint[pool.Length] : new uint[pool.Length];
        weights.Fill(1);
        return pool[AnimationVariantSelector.Select(sequence, weights)];
    }

    /// <summary>Whether the variety's .act has stances (monster_stances lists it).</summary>
    public static bool IsStanced(string varietyId) => Stanced.ContainsKey(varietyId);

    static List<string> StanceNames = default!;

    static string? ActionType(int effect) => SkillCatalog.ActionTypeId(effect);

    static string? EffectAnimation(int effect) =>
        ClientTables.TableOrNull("GrantedEffects")?.Row(effect)?.IntOrNull("Animation") is int row
            ? ClientTables.TableOrNull("Animation")?.Row(row)?.Text("Id") : null;

    /// <summary>Per variety whose first .act has stances: its pools by "&lt;stance&gt;/&lt;key&gt;" and root key.</summary>
    static Dictionary<string, Dictionary<string, SkillCatalog.MonsterClipContact[]>> Stanced = default!;

    static Dictionary<string, Dictionary<string, SkillCatalog.MonsterClipContact[]>> LoadStanced()
    {
        var result = new Dictionary<string, Dictionary<string, SkillCatalog.MonsterClipContact[]>>(StringComparer.Ordinal);
        if (ClientTables.TableOrNull("monster_stances") is not { } acts || ClientTables.TableOrNull("MonsterVarieties") is not { } varieties)
            return result;
        var clips = ClientTables.Table("monster_stance_clips").By("stance_act");
        var byAct = new Dictionary<string, Dictionary<string, SkillCatalog.MonsterClipContact[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var act in acts)
            byAct[act.Text("act")!] = clips[act.Index]
                .Where(c => c.TextOrNull("animation") is not null && !c.IsNull("index") && c.IntOrNull("length") is > 0)
                .GroupBy(c => c.Text("pool")!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Int("ord")).Select(c => new SkillCatalog.MonsterClipContact(
                    c.Text("animation")!, c.UInt("index"), c.Int("length"), c.IntOrNull("contact"), null)
                    { ContactsMs = c.IsNull("contacts") ? [] : c.Ints("contacts") }).ToArray(), StringComparer.Ordinal);
        foreach (var v in varieties)
            if (v.Texts("ACTFiles").FirstOrDefault() is { } act && byAct.TryGetValue(act, out var pools) && v.Text("Id") is { } id)
                result.TryAdd(id, pools);
        return result;
    }
}
