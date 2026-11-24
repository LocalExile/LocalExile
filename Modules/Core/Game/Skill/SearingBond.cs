using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const ushort SearingBondBeamRow = 7; // MiscBeams row for Searing Bond
    const int SearingBondPlacementGroup = 1;
    const double SearingBondContactSeconds = .1;

    sealed class SearingBondSource(IReadOnlyDictionary<uint, int> stats)
    {
        public readonly IReadOnlyDictionary<uint, int> Stats = stats;
        public readonly Dictionary<uint, uint> Beams = [];
    }

    static readonly RuleTable<TotemState.Totem, SearingBondSource> SearingBondSources = new();
    static readonly RuleTable<GameArea, HashSet<uint>> SearingBondTargets = new();
    static readonly RuleTable<SpawnedMonster, MonsterIgnites> SearingBondBurns = new();

    static readonly uint[] SearingBondDamageMore =
    [
        Stat.SupportBurningDamagePlusPctFinal,
        Stat.SupportAdditionalTotemDamagePlusPctFinal,
        Stat.SupportGemElementalDamagePlusPctFinal,
        Stat.SupportControlledDestructionSpellDamagePlusPctFinal,
        Stat.SupportEfficacySpellDamagePlusPctFinal,
        Stat.SupportEfficacyDamageOverTimePlusPctFinal,
        Stat.SupportRapidDecayDamageOverTimePlusPctFinal,
    ];

    static bool IsSearingBond(Player player, int effect) =>
        SkillsFor(player).EffectId(effect) is "SearingBond" or "SearingBondAltX";

    static bool HasSearingBondDamage(GameArea area) =>
        SearingBondTargets.TryGetValue(area, out var targets) && targets.Count != 0;

    internal static Task TickSearingBondDamage(Player player) => SearingBondTargets.TryGetValue(player.Area, out var targets)
        ? TickStrongestDots(player, targets, SearingBondBurns.GetOrCreateValue,
            (monster, now) => MonsterDotTaken(monster, DamageType.Fire, now), "dies from Searing Bond")
        : Task.CompletedTask;

    static double SearingBondDps(SearingBondSource source, IReadOnlyDictionary<uint, int> caster, bool area = false)
    {
        var stats = MergeSkillStats(caster, source.Stats);
        int Get(uint key) => stats.GetValueOrDefault(key);
        double increased = (double)Get(Stat.DamagePlusPct) + Get(Stat.FireDamagePlusPct) + Get(Stat.ElementalDamagePlusPct)
            + Get(Stat.DamageOverTimePlusPct) + Get(Stat.FireDamageOverTimePlusPct) + Get(Stat.BurnDamagePlusPct)
            + Get(Stat.TotemDamagePlusPct) + Get(Stat.SearingBondDamagePlusPct);
        if (Get(Stat.SpellDamageModifiersApplyToSkillDot) != 0) increased += Get(Stat.SpellDamagePlusPct);
        if (area) increased += Get(Stat.AreaDamagePlusPct);
        double more = DamageStats.ElementalMore(Get(Stat.CombinedElementalDamagePlusPctFinal));
        if (area) more *= Math.Max(0, 1 + Get(Stat.SupportAreaConcentrateAreaDamagePlusPctFinal) / 100d);
        foreach (uint modifier in SearingBondDamageMore) more *= Math.Max(0, 1 + Get(modifier) / 100d);
        double dot = Math.Max(0, 1 + ((double)Get(Stat.DotMultiplierPlus) + Get(Stat.FireDotMultiplierPlus)) / 100d);
        double dps = source.Stats.GetValueOrDefault(Stat.BaseFireDamageToDealPerMinute) / 60d
            * Math.Max(0, 1 + increased / 100d) * more * dot;
        return new SkillDamageContext(SkillHitKind.Spell, stats).OverTime(new(DamageType.Fire, dps, 1)).DamagePerSecond;
    }

    static void SearingBondUseLocked(Player player, TotemState.Totem totem, SearingBondSource source,
        double now, WorldRecordBuilder bytes)
    {
        var caster = TotemCasterStats(CharacterPlayerStats(player));
        double range = source.Stats.GetValueOrDefault(Stat.BaseTotemRange)
            * Math.Max(0, 1 + (source.Stats.GetValueOrDefault(Stat.TotemRangePlusPct)
                + (double)caster.GetValueOrDefault(Stat.TotemRangePlusPct)) / 100d);
        double dps = SearingBondDps(source, caster);
        HashSet<uint> linked = [];
        var time = ProjectileWorldTime(player);
        void Connect(uint target, (int X, int Y) end, bool show)
        {
            double length = Dist(totem.At, end);
            if (length > range) return;
            if (show && linked.Add(target) && !source.Beams.ContainsKey(target))
            {
                uint id = player.TakeObjectId();
                var beam = new BeamEffectObject(totem.At.X, totem.At.Y, totem.Id,
                    end.X * BeamEffectObject.WorldPerCell, end.Y * BeamEffectObject.WorldPerCell, target, SearingBondBeamRow);
                bytes.Create(id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(BeamEffectObject.TypePath), beam.Blob()));
                bytes.Presentation(id, InstanceProtocol.EnterWakeZone(id));
                source.Beams.Add(target, id);
            }
            if (dps <= 0) return;
            var middle = ((totem.At.X + end.X) / 2d, (totem.At.Y + end.Y) / 2d);
            foreach (var (id, monster) in player.Area.MonstersNear(middle, length / 2 + BondBeamRadius))
            {
                if (!TotemTargetable(monster) || !InsideBondBeam(monster.Walk.Position(now), totem.At, end, BondBeamRadius)) continue;
                ApplySearingBondDamage(player, id, monster, time, dps, SearingBondContactSeconds, totem.Id);
            }
        }

        Connect(player.Id, player.Walk.Position(now), show: true);
        foreach (var other in player.Totems.Living(now))
            if (!ReferenceEquals(other, totem))
                Connect(other.Id, other.At, show: totem.Id < other.Id || !SearingBondSources.TryGetValue(other, out _));
        foreach (uint target in source.Beams.Keys.Where(id => !linked.Contains(id)).ToArray())
        {
            bytes.Remove(source.Beams[target]);
            source.Beams.Remove(target);
        }
    }

    static void DetonateSearingBondsLocked(Player player, IReadOnlyDictionary<uint, int> ownerStats, int placementLimit,
        int? ordinaryLimit, WorldRecordBuilder bytes)
    {
        double now = AreaTime(player);
        var living = player.Totems.Living(now).ToArray();
        if (living.Length < TotemState.SharedLimit(placementLimit, ordinaryLimit, 0, living)) return;
        var detonating = new List<(TotemState.Totem Totem, SearingBondSource Source)>();
        foreach (var totem in living)
            if (SearingBondSources.TryGetValue(totem, out var source)
                && source.Stats.GetValueOrDefault(Stat.SearingBondTotemsDetonateAtMaxTotemCount) != 0)
                detonating.Add((totem, source));
        if (detonating.Count == 0) return;

        var caster = TotemCasterStats(ownerStats);
        // A placement can finish between projectile ticks. Its burn starts no earlier than the placement.
        double at = Math.Max(now, ProjectileWorldTime(player));
        foreach (var (totem, source) in detonating)
        {
            int Own(uint key) => source.Stats.GetValueOrDefault(key);
            double more = Math.Max(0, 1 + Own(Stat.SearingBondTotemsDetonationDamageOverTimePlusPctFinalPerActiveTotem)
                * (double)detonating.Count / 100d);
            double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), caster.GetValueOrDefault, Own);
            double dps = SearingBondDps(source, caster, area: true) * more;
            if (dps > 0 && duration > 0)
            {
                int radius = SkillRadius(player, source.Stats, spell: true, totem.EffectRow);
                foreach (var (id, monster) in player.Area.MonstersNear(totem.At, radius))
                    if (TotemTargetable(monster) && Dist(monster.Walk.Position(now), totem.At) <= radius)
                        ApplySearingBondDamage(player, id, monster, at, dps, duration, totem.Id);
            }
            totem.DiedAt = now;
            player.RemoveBlocker(totem.Id);
            StopSearingBond(totem, bytes);
        }
        player.Totems.Changed?.Invoke();
        PresentNearby(player);
        TotemsDiedLocked(player, now, ownerStats);
    }

    static void ApplySearingBondDamage(Player player, uint id, SpawnedMonster monster, double now,
        double dps, double duration, uint totem)
    {
        if (monster.Effects[Stat.BaseCannotBeDamaged] != 0) return;
        ulong source = ((ulong)player.Id << 32) | totem;
        SearingBondBurns.GetOrCreateValue(monster).Renew(now, dps, duration, source);
        SearingBondTargets.GetOrCreateValue(player.Area).Add(id);
    }

    static void StopSearingBond(TotemState.Totem totem, WorldRecordBuilder bytes)
    {
        if (!SearingBondSources.TryGetValue(totem, out var source)) return;
        foreach (uint id in source.Beams.Values) bytes.Remove(id);
        SearingBondSources.Remove(totem);
    }
}
