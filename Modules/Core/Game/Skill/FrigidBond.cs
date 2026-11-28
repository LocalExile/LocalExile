using GameServer.Core.World;

public static partial class Game
{
    // The client exposes the beam's effect pack, but no width stat. Use a narrow 4-cell corridor around the actual link.
    const int BondBeamRadius = 4;
    static readonly RuleTable<GameArea, HashSet<uint>> FrigidBondTargets = new();
    static readonly RuleTable<SpawnedMonster, MonsterIgnites> FrigidBondDamage = new();

    static bool HasFrigidBondDamage(GameArea area) =>
        FrigidBondTargets.TryGetValue(area, out var targets) && targets.Count > 0;

    internal static Task TickFrigidBondDamage(Player player) =>
        FrigidBondTargets.TryGetValue(player.Area, out var targets)
            ? TickStrongestDots(player, targets, FrigidBondDamage.GetOrCreateValue,
                (monster, now) => MonsterDotTaken(monster, DamageType.Cold, now), "dies from Frigid Bond")
            : Task.CompletedTask;

    static void ApplyFrigidBondLocked(Player player, LinkSkills.Link link, (int X, int Y) from, (int X, int Y) to,
        IReadOnlyDictionary<uint, int> playerStats, double now)
    {
        var skill = link.SkillStats;
        if (skill.GetValueOrDefault(Stat.LinkSkillsDealColdDotToEnemiesInBeamAoe) == 0) return;
        var merged = MergeSkillStats(playerStats, skill);
        int Get(uint key) => merged.GetValueOrDefault(key);
        double seconds = SkillDurationModifiers.Seconds(skill.GetValueOrDefault(Stat.SupportDamagingLinksBaseDurationMs)
            + skill.GetValueOrDefault(Stat.SupportDamagingLinksDurationMs), playerStats.GetValueOrDefault, skill.GetValueOrDefault);
        if (seconds <= 0) return;

        double dps = ColdSpellDamageOverTime(skill, Get);
        int radius = SkillArea.Radius(BondBeamRadius, Get, spell: true);
        double time = ProjectileWorldTime(player);
        ulong source = ((ulong)player.Id << 32) | link.Target;
        var center = ((from.X + to.X) / 2d, (from.Y + to.Y) / 2d);
        foreach (var (id, monster) in player.Area.MonstersNear(center, Dist(from, to) / 2 + radius))
        {
            if (!monster.Hostile || monster.Dead || monster.Life == 0 || monster.Invulnerable
                || monster.Effects[Stat.BaseCannotBeDamaged] != 0
                || !InsideBondBeam(monster.Walk.Position(now), from, to, radius)) continue;
            if (dps > 0)
            {
                var damage = FrigidBondDamage.GetOrCreateValue(monster);
                damage.SetExpiryRate(time, monster.RecipientDebuffRate);
                damage.Renew(time, dps, seconds, source);
                FrigidBondTargets.GetOrCreateValue(player.Area).Add(id);
            }
            monster.Ailments.ApplyAura(MonsterElementalAilments.Kind.Chill, source, seconds, time, Get, key => monster.Effects[key]);
        }
    }

    static double ColdSpellDamageOverTime(IReadOnlyDictionary<uint, int> skill, Func<uint, int> get)
    {
        double basis = skill.GetValueOrDefault(Stat.BaseColdDamageToDealPerMinute) / 60d;
        if (basis <= 0 || DamageStats.Prohibited(DamageType.Cold, get)) return 0;
        double increased = (double)get(Stat.DamagePlusPct) + get(Stat.ColdDamagePlusPct) + get(Stat.ElementalDamagePlusPct)
            + get(Stat.DamageOverTimePlusPct) + get(Stat.ColdDamageOverTimePlusPct) + get(Stat.AreaDamagePlusPct)
            + (get(Stat.SpellDamageModifiersApplyToSkillDot) != 0 ? get(Stat.SpellDamagePlusPct) : 0);
        double more = WintertideDotSupportMore.Aggregate(1.0, (factor, name) =>
            factor * Math.Max(0, 1 + BrandStat(skill, name) / 100d));
        double dotMultiplier = Math.Max(0, 1 + ((double)get(Stat.DotMultiplierPlus) + get(Stat.ColdDotMultiplierPlus)) / 100d);
        return basis * Math.Max(0, 1 + increased / 100d) * more * dotMultiplier
            * DamageStats.ElementalMore(get(Stat.CombinedElementalDamagePlusPctFinal));
    }

    static bool InsideBondBeam((int X, int Y) at, (int X, int Y) from, (int X, int Y) to, int radius)
    {
        double x = to.X - from.X, y = to.Y - from.Y;
        double squareLength = x * x + y * y;
        double along = squareLength > 0 ? Math.Clamp(((at.X - from.X) * x + (at.Y - from.Y) * y) / squareLength, 0, 1) : 0;
        double offsetX = at.X - (from.X + along * x), offsetY = at.Y - (from.Y + along * y);
        return offsetX * offsetX + offsetY * offsetY <= radius * radius;
    }
}