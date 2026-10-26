using GameServer.Core.World;

/// <summary>
/// Pure damage construction from unmodified skill/weapon base and added damage. Never construct a Seed from
/// AttackHit or SpellDamage.Packet: those have already applied hit-only modifiers and discarded conversion history.
/// Application chances, avoidance, target mitigation, status lifetime and native packets belong to the caller.
/// Sources: GGG's 3.0 DoT rework, https://www.pathofexile.com/forum/view-thread/1930316/filter-account-type/staff;
/// PoB commit 16de4b82d57f1c0de6eb40f37143c32d4da36a02 src/Modules/CalcOffence.lua lines 69-148 (ancestry),
/// 1938-1987 (conversion priority), 4413-4421/4710-4720/4990-5002 (ailment rather than hit modifier scopes),
/// 4775-4845 and 5100-5186 (ailment destination type and additive critical DoT multiplier).
/// Pinned base coefficients/durations: src/Modules/Data.lua lines 262-267.
/// </summary>
static class AilmentDamage
{
    // Current 3.29.3b Metadata/Characters/Character.ot and Metadata/Monsters/Monster.ot respectively.
    public const double PlayerCriticalDotMultiplier = 50;
    public const double MonsterCriticalDotMultiplier = 30;

    [Flags]
    public enum Types
    {
        None = 0, Physical = 1, Lightning = 2, Cold = 4, Fire = 8, Chaos = 16,
        Elemental = Lightning | Cold | Fire, All = Physical | Elemental | Chaos
    }

    [Flags]
    public enum Tags { None = 0, Attack = 1, Spell = 2, Melee = 4, Projectile = 8, Minion = 16 }

    [Flags]
    public enum Scope { Hit = 1, Ailment = 2, Both = Hit | Ailment }

    public enum ModifierKind { Increased, More, DotMultiplier }
    public enum ConversionKind { Global, Skill, GainAsExtra }

    /// <summary>
    /// One modifier entry matches once, even when multiple ancestral types match. Types.None matches every type.
    /// Attack/melee/spell/projectile DAMAGE must use Scope.Hit; explicitly worded damage with attack skills or
    /// ailments caused by melee hits may use Scope.Ailment/Both with RequiredTags. A DotMultiplier matches the
    /// ailment's resulting damage type, not its source ancestry. Its value is additive with the critical DoT bonus.
    /// </summary>
    public readonly record struct Modifier(double Value, ModifierKind Kind = ModifierKind.Increased,
        Scope AppliesTo = Scope.Both, Types DamageTypes = Types.None, Tags RequiredTags = Tags.None,
        Ailment? OnlyAilment = null);

    public readonly record struct Conversion(DamageType From, DamageType To, int Percent,
        ConversionKind Kind = ConversionKind.Global);

    public readonly record struct Part(DamageType Type, Types Ancestry, DamageRange Base);

    /// <summary>Immutable pre-modifier damage with every conversion branch's type ancestry still present.</summary>
    public sealed class Seed
    {
        readonly Part[] parts;
        public Tags SourceTags { get; }
        public IReadOnlyList<Part> Parts => Array.AsReadOnly(parts);

        Seed(Tags sourceTags, IEnumerable<Part> parts)
        {
            SourceTags = sourceTags;
            this.parts = parts.Where(p => !p.Base.IsZero).ToArray();
        }

        public static Seed FromBase(DamagePacket unmodifiedBase, Tags sourceTags) =>
            new(sourceTags, unmodifiedBase.Components.Select(c => new Part(c.Type, Mask(c.Type), c.Range)));

        /// <summary>Added effectiveness applies only to this unmodified added packet, before increased/more damage.</summary>
        public Seed Add(DamagePacket unmodifiedAdded, double effectiveness = 1) => new(SourceTags,
            parts.Concat(unmodifiedAdded.Scale(effectiveness).Components.Select(c => new Part(c.Type, Mask(c.Type), c.Range))));

        /// <summary>
        /// Convert/gain simultaneously for each source type, in physical/lightning/cold/fire/chaos order.
        /// Skill conversion takes priority; global conversion fills its remainder; gain does not consume the source.
        /// The whole-percent normalization matches DamageConversion and pinned PoB buildConversionTable.
        /// </summary>
        public Seed Convert(params Conversion[] rules)
        {
            foreach (var rule in rules)
                if (!DamageTypes.CanConvert(rule.From, rule.To))
                    throw new ArgumentOutOfRangeException(nameof(rules), "Conversion must follow the damage-type order.");

            var current = parts.ToList();
            foreach (var from in DamageTypes.ConversionOrder)
            {
                var outgoing = rules.Where(r => r.From == from && r.Percent > 0).ToArray();
                if (outgoing.Length == 0) continue;
                double skill = outgoing.Where(r => r.Kind == ConversionKind.Skill).Sum(r => (double)r.Percent);
                double global = outgoing.Where(r => r.Kind == ConversionKind.Global).Sum(r => (double)r.Percent);
                double skillScale = skill > 100 ? 100 / skill : 1;
                double globalScale = skill > 100 ? 0 : global > 0 ? Math.Min(1, (100 - skill) / global) : 1;
                var branches = DamageTypes.ConversionOrder.Where(to => DamageTypes.CanConvert(from, to)).Select(to =>
                {
                    double converted = Math.Floor(outgoing.Where(r => r.To == to && r.Kind == ConversionKind.Skill)
                        .Sum(r => (double)r.Percent) * skillScale
                        + outgoing.Where(r => r.To == to && r.Kind == ConversionKind.Global)
                            .Sum(r => (double)r.Percent) * globalScale + .5);
                    double gain = outgoing.Where(r => r.To == to && r.Kind == ConversionKind.GainAsExtra)
                        .Sum(r => (double)r.Percent);
                    return (To: to, Converted: converted, Gain: gain);
                }).ToArray();
                double kept = Math.Max(0, 1 - Math.Min(100, branches.Sum(b => b.Converted)) / 100);
                var sources = current.Where(p => p.Type == from).ToArray();
                current.RemoveAll(p => p.Type == from);
                foreach (var part in sources)
                {
                    if (kept > 0) current.Add(part with { Base = part.Base.Scale(kept) });
                    foreach (var branch in branches)
                    {
                        double fraction = (branch.Converted + branch.Gain) / 100;
                        if (fraction > 0) current.Add(new(branch.To, part.Ancestry | Mask(branch.To), part.Base.Scale(fraction)));
                    }
                }
            }
            return new(SourceTags, current);
        }

        /// <summary>Unmitigated, noncritical hit ranges; useful for sharing the seed without feeding hit damage to DoT.</summary>
        public DamagePacket Hit(IEnumerable<Modifier> modifiers)
        {
            var mods = modifiers.ToArray();
            var result = DamagePacket.Empty;
            foreach (var part in parts)
                result = result.Add(part.Type, part.Base.Scale(Scale(mods, Scope.Hit, SourceTags, part.Ancestry, null)));
            return result;
        }
    }

    /// <summary>
    /// SourceDamage is the ailment's own modified damage range, before its base DPS coefficient and DoT multiplier.
    /// Roll uses a fresh source-damage roll and preserves fractional DPS (fixed 101 fire gives 90.9 ignite DPS).
    /// It does not reuse a hit roll, lucky hit policy, hit crit multiplier, triple damage, penetration or resistance.
    /// </summary>
    public readonly record struct Calculation(Ailment Ailment, DamageType Type, DamageRange SourceDamage,
        double DpsFactor, double DurationSeconds)
    {
        public DamageRange DamagePerSecond => SourceDamage.Scale(DpsFactor);
        public bool HasDamage => !SourceDamage.IsZero && DpsFactor > 0;
        public DamageOverTime Roll(Random random) => new(Type, SourceDamage.Roll(random) * DpsFactor, DurationSeconds, Ailment);
        public DamageOverTime Average => new(Type, SourceDamage.Average * DpsFactor, DurationSeconds, Ailment);
    }

    /// <summary>
    /// Build a damaging-ailment range separately from the hit. AdditionalEligibleTypes is for explicit rules such as
    /// Conflux/all damage can poison; it changes eligibility, never conversion. Bleeding still requires an attack.
    /// criticalDotMultiplier is the actor's COMPLETE critical-ailment stat (normally player 50 or monster/minion 30),
    /// not an additional bonus on top of that default. Explicit critical-multiplier-to-DoT rules need typed modifiers.
    /// </summary>
    public static Calculation Calculate(Seed seed, Ailment ailment, IEnumerable<Modifier> modifiers,
        bool critical = false, double criticalDotMultiplier = PlayerCriticalDotMultiplier,
        Types additionalEligibleTypes = Types.None)
    {
        var (type, eligible, coefficient, duration) = ailment switch
        {
            Ailment.Ignite => (DamageType.Fire, Types.Fire, .9, 4d),
            Ailment.Bleed => (DamageType.Physical, Types.Physical, .7, 5d),
            Ailment.Poison => (DamageType.Chaos, Types.Physical | Types.Chaos, .3, 2d),
            _ => throw new ArgumentOutOfRangeException(nameof(ailment), "Only ignite, bleed and poison are hit-based ailments.")
        };
        var mods = modifiers.ToArray();
        var range = DamageRange.Zero;
        bool canBleed = ailment != Ailment.Bleed || (seed.SourceTags & Tags.Attack) != 0;
        if (canBleed)
            foreach (var part in seed.Parts)
                if (((eligible | additionalEligibleTypes) & Mask(part.Type)) != 0)
                    range = range.Add(part.Base.Scale(Scale(mods, Scope.Ailment, seed.SourceTags,
                        part.Ancestry | Mask(type), ailment)));
        double dotMultiplier = (critical ? criticalDotMultiplier : 0) + mods
            .Where(m => m.Kind == ModifierKind.DotMultiplier && Matches(m, Scope.Ailment, seed.SourceTags, Mask(type), ailment))
            .Sum(m => m.Value);
        return new(ailment, type, range, coefficient * Math.Max(0, 1 + dotMultiplier / 100), duration);
    }

    static double Scale(IEnumerable<Modifier> modifiers, Scope scope, Tags tags, Types types, Ailment? ailment)
    {
        double increased = 0, more = 1;
        foreach (var modifier in modifiers)
        {
            if (!Matches(modifier, scope, tags, types, ailment)) continue;
            if (modifier.Kind == ModifierKind.Increased) increased += modifier.Value;
            else if (modifier.Kind == ModifierKind.More) more *= Math.Max(0, 1 + modifier.Value / 100);
        }
        return Math.Max(0, 1 + increased / 100) * more;
    }

    static bool Matches(Modifier modifier, Scope scope, Tags tags, Types types, Ailment? ailment) =>
        (modifier.AppliesTo & scope) != 0 && (modifier.RequiredTags & tags) == modifier.RequiredTags
        && (modifier.DamageTypes == Types.None || (modifier.DamageTypes & types) != 0)
        && (modifier.OnlyAilment is null || modifier.OnlyAilment == ailment);

    static Types Mask(DamageType type) => (Types)(1 << DamageTypes.OrderOf(type));
}
