using System.Numerics;
using GameServer.Core.Projectiles;
using GameServer.Core.World;
using Ailment = MonsterElementalAilments.Kind;

static partial class Game
{
    const ushort ElementalHitMemoryBuff = 1143;
    internal const string SpectrumHitEffect = "ElementalHitAltX";
    const int ElementalHitCloneShot = 306;

    internal static ProjectileAreaRecipe ElementalHitBurst(double radius) => new("elemental-hit-burst", radius)
    {
        CentreOnContactTarget = true,
        ExcludeContactTarget = true,
        ReplacesDirectContact = false,
        RequiresEnemyHit = true,
    };

    static SkillCatalog.MonsterProjectileInfo? ElementalHitCloneArrow(SkillCatalog skills, DamageType element)
    {
        string metadata = element switch
        {
            DamageType.Cold => "Metadata/Projectiles/ElementalHitArrowCold",
            DamageType.Lightning => "Metadata/Projectiles/ElementalHitArrowLightning",
            _ => "Metadata/Projectiles/ElementalHitArrowFire",
        };
        return skills.Projectile(metadata) is { } projectile
            ? new(projectile.Id, projectile.Speed, false, skills.ProjectileContactCells(projectile.Id) ?? 1) : null;
    }

    sealed class ElementalHitMemory
    {
        public int Last = 3;
    }

    static readonly RuleTable<Player, ElementalHitMemory> ElementalHitElements = new();
    static readonly uint[] ElementalHitBans =
        [Stat.ElementalHitCannotRollFireDamage, Stat.ElementalHitCannotRollColdDamage, Stat.ElementalHitCannotRollLightningDamage];
    static readonly uint[] ElementalHitOnly =
        [Stat.DealNoNonFireDamage, Stat.DealNoNonColdDamage, Stat.DealNoNonLightningDamage];

    static int SelectElementalHitElement(Player player, ushort skillId, SupportedGem gem, ushort sequence, int repeat)
    {
        var state = ElementalHitElements.GetOrCreateValue(player);
        if (!player.Area.Buffs.Has(player.Id, ElementalHitMemoryBuff, player.Id)) state.Last = 3;

        var stats = CharacterPlayerStats(player);
        Span<int> pool = stackalloc int[3];
        int count = 0, fallback = 3;
        for (int i = 0; i < ElementalHitBans.Length; i++)
        {
            uint ban = ElementalHitBans[i];
            if (stats.GetValueOrDefault(ban) != 0 || gem.Stats.GetValueOrDefault(ban) != 0) continue;
            if (i == state.Last) fallback = i;
            else pool[count++] = i;
        }
        int repeatsLeft = Math.Max(0, MeleeAttackRepeats(player, skillId) - repeat);
        // 3.29.3c FUN_141acd990: action sequence, child, owner, repeat counter, auxiliary index and three zeros.
        var roll = new AnimationVariantSelector.NativeRandom([sequence, 0, player.Id, (uint)repeatsLeft, 0, 0, 0, 0]);
        int selected = count == 0 ? fallback : pool[(int)roll.Uniform((uint)count)];
        state.Last = selected;
        // SourceSkill reaches the buff's action-sequence field (+0x42). The client dereferences list element 0.
        player.Area.Buffs.Set(player.Id, new(ElementalHitMemoryBuff, player.Id, sequence, List: [(uint)selected]));
        return selected;
    }

    [Flags]
    enum ElementalHitAilments
    {
        None = 0,
        Ignite = 1, Scorch = 2, Chill = 4, Freeze = 8, Brittle = 16, Shock = 32, Sap = 64,
        Fire = Ignite | Scorch, Cold = Chill | Freeze | Brittle, Lightning = Shock | Sap,
    }

    static ElementalHitAilments ElementalHitAilmentsOn(SpawnedMonster monster, double now)
    {
        var result = ElementalHitAilments.None;
        if (monster.Ignites.IgnitedAt(now)) result |= ElementalHitAilments.Ignite;
        if (monster.Ailments.Magnitude(Ailment.Chill, now) > 0) result |= ElementalHitAilments.Chill;
        if (monster.Ailments.Magnitude(Ailment.Freeze, now) > 0) result |= ElementalHitAilments.Freeze;
        if (monster.Ailments.Magnitude(Ailment.Shock, now) > 0) result |= ElementalHitAilments.Shock;
        if (monster.RuleAilments.Magnitude((int)AlternateKind.Scorch, now) > 0) result |= ElementalHitAilments.Scorch;
        if (monster.RuleAilments.Magnitude((int)AlternateKind.Brittle, now) > 0) result |= ElementalHitAilments.Brittle;
        if (monster.RuleAilments.Magnitude((int)AlternateKind.Sap, now) > 0) result |= ElementalHitAilments.Sap;
        return result;
    }

    static int ElementalHitRadius(int radius, int element, ElementalHitAilments ailments)
    {
        var associated = element switch
        {
            0 => ElementalHitAilments.Fire,
            1 => ElementalHitAilments.Cold,
            _ => ElementalHitAilments.Lightning,
        };
        return (ailments & associated) != 0 ? (int)(radius * 1.8) : radius;
    }

    static double ElementalHitDamageMore(int perAilment, ElementalHitAilments ailments) =>
        Math.Max(0, 1 + perAilment * BitOperations.PopCount((uint)ailments) / 100.0);

    static void AddElementalHitNearby(Player player, (int X, int Y) centre, double now, int radius,
        (int Radius, double More)? splash, List<(uint Id, SpawnedMonster M)> targets, HashSet<uint> struck,
        Dictionary<uint, double> splashMore, ISet<uint>? once)
    {
        int reach = Math.Max(radius, splash?.Radius ?? 0);
        foreach (var (id, monster) in player.Area.MonstersNear(centre, reach))
        {
            if (!monster.Hostile || monster.Dead || monster.Life == 0 || monster.OwnerId is not null || monster.WorldHidden) continue;
            double distance = Dist(centre, monster.Walk.Position(now));
            if (distance > reach) continue;
            if (distance <= radius) splashMore.Remove(id);
            if (struck.Contains(id) || once?.Contains(id) == true) continue;
            struck.Add(id);
            targets.Add((id, monster));
            if (distance > radius && splash is { } supported) splashMore[id] = supported.More;
            once?.Add(id);
        }
    }

    // The normal and Spectrum gems' intrinsic damage uses separate stats from global added attack damage.
    static void ApplyElementalHitDamageStats(Dictionary<uint, int> stats)
    {
        stats[Stat.DealNoNonElementalDamage] = 1;
        Add(Stat.AttackMinimumAddedFireDamage, Stat.AttackMinimumAddedFireDamageForElementalHit);
        Add(Stat.AttackMaximumAddedFireDamage, Stat.AttackMaximumAddedFireDamageForElementalHit);
        Add(Stat.AttackMinimumAddedColdDamage, Stat.AttackMinimumAddedColdDamageForElementalHit);
        Add(Stat.AttackMaximumAddedColdDamage, Stat.AttackMaximumAddedColdDamageForElementalHit);
        Add(Stat.AttackMinimumAddedLightningDamage, Stat.AttackMinimumAddedLightningDamageForElementalHit);
        Add(Stat.AttackMaximumAddedLightningDamage, Stat.AttackMaximumAddedLightningDamageForElementalHit);

        void Add(uint target, uint source)
        {
            int value = stats.GetValueOrDefault(source);
            if (value != 0)
                stats[target] = ActorScale.Add(target, stats.GetValueOrDefault(target), value);
        }
    }
}
