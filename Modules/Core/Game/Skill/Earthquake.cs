using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    // Both player Earthquake rows use an 18-cell impact and a 28-cell aftershock before area modifiers.
    // The source gem, weapon and swing roll belong to the impact even if the player moves or unsockets the gem.
    sealed record EarthquakeAftershock(SupportedGem Gem, AttackHand Hand, SkillCatalog.Unarmed Weapon,
        AttackAccuracyProfile Accuracy, int CriticalChance, int CriticalMultiplier, int? CriticalVsBlind,
        GroundCritical CriticalVsGround, double HitMore, double AilmentMore);

    public sealed partial class ProjectileSession
    {
        internal bool EarthquakePending { get; private set; }

        internal void ScheduleEarthquake(long at, Action erupt)
        {
            EarthquakePending = true;
            Runtime.Schedule(at, () =>
            {
                EarthquakePending = false;
                if (Active && Player.PlayerLife != 0) erupt();
            });
        }
    }

    static void ScheduleEarthquakeAftershockLocked(Player player, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, SupportedGem admitted,
        (int X, int Y) centre, ushort sequence, AttackHand hand, SkillCatalog.Unarmed weapon, int? offCritical,
        SkillCriticalRoll critical, double now, double radiusMore, double damageMore, double ruthlessMore, bool cryExerted)
    {
        var session = ProjectilesFor(player);
        if (session.EarthquakePending) return;
        var stats = CharacterPlayerStats(player);
        int PlayerStat(uint key) => stats.GetValueOrDefault(key);
        int Own(uint key) => admitted.Stats.GetValueOrDefault(key);
        double seconds = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), PlayerStat, Own);
        int steps = (int)Math.Floor(seconds * 10 + 1e-9);
        int commonMore = Own(Stat.QuakeSlamFullyChargedExplosionDamagePlusPctFinal);
        double More(uint key) => Math.Max(0, 1 + (commonMore + Own(key) * (double)steps) / 100);
        double areaMore = Math.Max(0, 1 + Own(Stat.EarthquakeSkillAftershockAreaOfEffectPlusPctFinalPer100msDuration) * (double)steps / 100);
        var area = SkillAreaStats(player, admitted.Stats, gem.Effect, stats);
        int radius = SkillArea.Radius(28, key => area.GetValueOrDefault(key), spell: false, areaMore);
        int? swingCritical = hand == AttackHand.Off ? offCritical : null;
        var source = new EarthquakeAftershock(admitted, hand, weapon, SupportedAccuracy(player, skillId, hand),
            SupportedCriticalChance(player, skillId, hand, offCritical), SupportedCriticalMultiplier(player, skillId, weapon),
            SupportedCriticalChanceVsBlinded(player, skillId, release: false, swingCritical),
            SupportedCriticalChanceVsGround(player, skillId, release: false, swingCritical),
            More(Stat.EarthquakeSkillAftershockHitDamagePlusPctFinalPer100msDuration) * damageMore,
            More(Stat.EarthquakeSkillAftershockAilmentDamagePlusPctFinalPer100msDuration) * damageMore);

        long at = Math.Max(session.Runtime.Now, ProjectileRuntime.Time(now + seconds));
        bool Current() => session.Active && player.PlayerLife != 0;
        session.ScheduleEarthquake(at, () =>
        {
            var hit = AreaSkillHitStrike(player, skillId, gem, centre, centre, immediate: true, sequence,
                actionToken: null, cooldownUse: null, melee: true, damageMore: 1, excludeMonster: 0, critical,
                channelGeneration: null, targetId: 0, repeat: 0, radiusOverride: radius, once: null, repeatDelay: 0,
                radiusMore, sacrifice: null, swingAt: null, triggeredCurrent: Current, discharge: null, strikeIndex: 0,
                firstContactAt: null, ruthlessMore, priorTargets: null, scaledBlastChaos: 0,
                singleStrikeTarget: null, onHit: null, scheduledSession: session, cryExerted, prepaidRawExtra: 0,
                baseChaos: 0, hammer: null, aftershock: source);
            if (!hit.IsCompleted) throw new InvalidOperationException("Scheduled Earthquake aftershock must not yield.");
            hit.GetAwaiter().GetResult();
        });
    }
}
