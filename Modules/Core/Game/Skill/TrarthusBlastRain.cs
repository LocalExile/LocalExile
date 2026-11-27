using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string TrarthusBlastRain = "BlastRainAltX";
    const ushort TrarthusBurnBuff = 2613;
    const double TrarthusWaveFirstQuery = .1;
    const double TrarthusWaveDuration = .25;
    static readonly double TrarthusWaveEdge = Math.Cos(100 * Math.PI / 180);
    static readonly RuleTable<GameArea, HashSet<uint>> TrarthusBurnTargets = new();
    static readonly RuleTable<SpawnedMonster, MonsterIgnites> TrarthusBurns = new();

    static readonly uint[] BowRainBurnMore =
    [
        Stat.SupportBurningDamagePlusPctFinal, Stat.SupportGemElementalDamagePlusPctFinal,
        Stat.SupportEfficacyDamageOverTimePlusPctFinal, Stat.SupportRapidDecayDamageOverTimePlusPctFinal,
        Stat.SupportReducedDurationDamagePlusPctFinal, Stat.CrueltyDamageOverTimePlusPctFinal,
        Stat.ActiveSkillDamagePlusPctFinal, Stat.ActiveSkillFireDamagePlusPctFinal,
        Stat.ActiveSkillElementalDamagePlusPctFinal, Stat.ActiveSkillProjectileDamagePlusPctFinal,
        Stat.SupportChanceToIgniteFireDamagePlusPctFinal,
        Stat.SupportAttackSkillsElementalDamagePlusPctFinal,
        Stat.SupportSlowerProjectilesDamagePlusPctFinal, Stat.SupportPierceProjectileDamagePlusPctFinal,
        Stat.SupportMultipleProjectileDamagePlusPctFinal, Stat.SupportLesserMultipleProjectileDamagePlusPctFinal,
        Stat.SupportSplitProjectileDamagePlusPctFinal, Stat.SupportForkProjectileDamagePlusPctFinal,
        Stat.SupportParallelProjectilesDamagePlusPctFinal, Stat.SupportGreaterVolleyProjectileDamagePlusPctFinal,
        Stat.SupportRainProjectileDamagePlusPctFinal,
    ];

    static DamageOverTime BowRainBurnDamage(ProjectileOffense shot, IReadOnlyDictionary<uint, int> own,
        IReadOnlyDictionary<uint, int> caster, double seconds, bool trap, bool mine, bool recentMine, double damageFactor)
    {
        var merged = MergeSkillStats(caster, own);
        int Get(uint key) => merged.GetValueOrDefault(key);
        // The debuff uses projectile damage modifiers. Its application area only determines which targets receive it.
        double increased = (double)Get(Stat.DamagePlusPct) + Get(Stat.FireDamagePlusPct) + Get(Stat.ElementalDamagePlusPct)
            + Get(Stat.DamageOverTimePlusPct) + Get(Stat.FireDamageOverTimePlusPct) + Get(Stat.BurnDamagePlusPct)
            + Get(Stat.ProjectileDamagePlusPct) + (shot.FromTotem ? Get(Stat.TotemDamagePlusPct) : 0)
            + (trap || mine ? (double)Get(mine ? Stat.MineDamagePlusPct : Stat.TrapDamagePlusPct)
                + Get(Stat.TrapOrMineDamagePlusPct) + (recentMine ? Get(Stat.MineAreaDamagePlusPctIfDetonatedMineRecently) : 0) : 0);
        double more = DamageStats.ElementalMore(Get(Stat.CombinedElementalDamagePlusPctFinal)) * ChargeDamageMore(caster);
        foreach (uint stat in BowRainBurnMore) more *= Math.Max(0, 1 + Get(stat) / 100d);
        if (trap || mine)
        {
            more *= Math.Max(0, 1 + Get(Stat.SkitterbotsTrapMineDamagePlusPctFinal) / 100d);
            foreach (uint stat in PlacedChaosGroundMoreStats) more *= Math.Max(0, 1 + Get(stat) / 100d);
        }
        if (shot.FromTotem) more *= Math.Max(0, 1 + Get(Stat.SupportTotemDamagePlusPctFinal) / 100d);
        double dotMultiplier = Math.Max(0, 1 + ((double)Get(Stat.DotMultiplierPlus) + Get(Stat.FireDotMultiplierPlus)
            + Get(Stat.DotMultiplierPlusWithBowSkills)) / 100);
        double dps = own.GetValueOrDefault(Stat.BaseFireDamageToDealPerMinute) / 60d * Math.Max(0, 1 + increased / 100)
            * more * dotMultiplier * (shot.Mirage ? MirageDamageFactor(own) : 1) * damageFactor;
        if (DamageStats.Prohibited(DamageType.Fire, Get)) dps = 0;
        return new SkillDamageContext(SkillHitKind.Attack, caster).OverTime(new(DamageType.Fire, dps, seconds));
    }

    static bool HasTrarthusBurn(GameArea area) =>
        TrarthusBurnTargets.TryGetValue(area, out var targets) && targets.Count > 0;

    static bool TrarthusBurning(SpawnedMonster monster, double now) =>
        TrarthusBurns.TryGetValue(monster, out var burns) && burns.IgnitedAt(now);

    static Task TickTrarthusBurn(Player player) => TrarthusBurnTargets.TryGetValue(player.Area, out var targets)
        ? TickStrongestDots(player, targets, TrarthusBurns.GetOrCreateValue,
            (monster, now) => MonsterDotTaken(monster, DamageType.Fire, now), "burnt by Blast Rain of Trarthus")
        : Task.CompletedTask;

    static void ApplyTrarthusBurn(Player player, uint id, SpawnedMonster monster, DamageOverTime burn, double now)
    {
        var burns = TrarthusBurns.GetOrCreateValue(monster);
        burns.SetExpiryRate(now, monster.RecipientDebuffRate);
        if (!burns.Add(now, burn.DamagePerSecond, burn.RemainingSeconds)) return;
        TrarthusBurnTargets.GetOrCreateValue(player.Area).Add(id);
        ShowDotBuffLocked(player, id, burns, TrarthusBurnBuff, now);
        if (ServerTrace.Projectiles)
            Console.WriteLine($"[trarthus-blast] burn {id} t={now:F6} dps={burn.DamagePerSecond:F2} duration={burn.RemainingSeconds:F3}");
    }

    static void ReleaseTrarthusBlastRain(ProjectileSession session, ProjectileUse use,
        IReadOnlyDictionary<uint, int> own, IReadOnlyDictionary<uint, int> playerStats, ushort skill, int effect,
        double damageFactor, bool trap, bool mine, double? actionStart)
    {
        var player = session.Player;
        var shot = (ProjectileOffense)use.Payload!;
        var caster = shot.ActualCaster ?? playerStats;
        double seconds = SkillDurationModifiers.Seconds(own.GetValueOrDefault(Stat.BaseSkillEffectDuration),
            caster.GetValueOrDefault, own.GetValueOrDefault);
        var burn = BowRainBurnDamage(shot, own, caster, seconds, trap, mine,
            mine && player.RecentEvents.Happened(StatConditions.DetonatedMine, AreaTime(player)), damageFactor);
        var at = use.Aim;
        var direction = (at - use.Origin).Unit;
        float facing = (float)Math.Atan2(direction.X, -direction.Y);
        int radius = SkillRadius(player, own, spell: false, effect);
        int waveRadius = SkillSecondaryRadius(player, own, spell: false, effect);
        int speed = RainOfArrowsFallSpeed(player, skill, playerStats, own, shot.FromTotem);
        // Native 3.29.3c chooses a single aimed impact with a .33 s fall animation after the release delay.
        double preFall = .325 * Math.Min(2.4, 100d / (100 + speed));
        long now = session.Runtime.Now;
        long fallAt = Math.Max(now, ProjectileRuntime.Time(actionStart ?? now / (double)ProjectileRuntime.QuantaPerSecond)
            + ProjectileRuntime.Time(preFall));
        long landAt = fallAt + ProjectileRuntime.Time(.33);
        bool burning = burn.DamagePerSecond > 0 && burn.RemainingSeconds > 0;
        if (burning) use = use with { Payload = shot with { TrarthusBurn = burn } };

        session.Runtime.Schedule(fallAt, () =>
        {
            if (session.Active && player.PlayerLife != 0)
                session.RainArt(at, "Metadata/Effects/Spells/blast_rain/arrow_drop.ao", 1, rotation: facing, clip: 0);
        });
        session.Runtime.Schedule(landAt, () =>
        {
            if (!session.Active || player.PlayerLife == 0) return;
            session.Runtime.Impact(use, at, new(TrarthusBlastRain, radius));
            session.RainArt(at, "Metadata/Effects/Spells/blast_rain/blastrainexplosion_aoe.ao", 1, radius);
            if (waveRadius <= 0) return;
            session.RainArt(at, "Metadata/Effects/Spells/blast_rain/wave_effect_new.ao", 1, waveRadius, facing);
            if (burning) ScheduleWave();
        });

        void ScheduleWave()
        {
            var seen = new HashSet<uint>();
            long waveTime = ProjectileRuntime.Time(TrarthusWaveDuration);
            long firstQuery = ProjectileRuntime.Time(TrarthusWaveFirstQuery);
            long tick = ProjectileRuntime.Time(TickSeconds);
            var centre = ((int)at.X, (int)at.Y);
            for (long elapsed = firstQuery; ; elapsed += tick)
            {
                long step = Math.Min(elapsed, waveTime);
                int reach = (int)(waveRadius * step / waveTime);
                session.Runtime.Schedule(landAt + step, () => TouchWave(reach));
                if (step == waveTime) break;
            }

            void TouchWave(int reach)
            {
                if (!session.Active || player.PlayerLife == 0) return;
                double time = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
                foreach (var (id, monster) in player.Area.MonstersNear((at.X, at.Y), reach))
                {
                    if (seen.Contains(id) || !monster.Hostile || monster.Dead || monster.Life == 0
                        || monster.Invulnerable || monster.WorldHidden || monster.Effects[Stat.BaseCannotBeDamaged] != 0) continue;
                    var point = monster.Walk.Position(time);
                    var offset = ProjectileSession.Vector(point) - at;
                    double distance = offset.Length;
                    if (distance > reach || offset.Dot(direction) < distance * TrarthusWaveEdge
                        || (player.Layout.Sight is { } sight && ClientPathfinder.LineBelow(centre, point, sight, 1))
                        || (player.SightBarrier.Count > 0 && ClientPathfinder.Line(centre, point).Any(player.SightBarrier.Contains)))
                        continue;
                    seen.Add(id);
                    ApplyTrarthusBurn(player, id, monster, burn, time);
                }
            }
        }
    }
}
