using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    static bool IsPurifyingFlame(string? effect) => effect is "PurifyingFlame" or "PurifyingFlameAltX" or "PurifyingFlameRoyale";

    // 3.29.3c SanctifyRectangle, SanctifyImpact and SanctifyWave use 11, 18 and 50 cells respectively.
    // Use the ground type's base radius and the gem's separate consecration radius modifier.
    const int PurifyingFlamePathWidth = 11;
    const int PurifyingFlameImpactRadius = 18;
    const int PurifyingFlameWaveRadius = 50;
    const int PurifyingFlameGroundRadius = 10;
    const double PurifyingFlameWaveDelay = .25;

    delegate void PurifyingFlameHit(int radius, Func<uint, (int X, int Y), bool> covers, double more, Action<uint>? onHit);

    static void SchedulePurifyingFlame(Player player, ProjectileSession session, int effect,
        IReadOnlyDictionary<uint, int> own, IReadOnlyDictionary<uint, int> caster, (int X, int Y) from, (int X, int Y) aim,
        Func<bool> current, PurifyingFlameHit hit, int repeat = 0)
    {
        var areaStats = SkillAreaStats(player, own, effect, caster);
        double now = ProjectileWorldTime(player);
        int targetingIncrease = own.GetValueOrDefault(Stat.SanctifyAreaOfEffectPlusPctWhenTargetingConsecratedGround)
            + caster.GetValueOrDefault(Stat.SanctifyAreaOfEffectPlusPctWhenTargetingConsecratedGround);
        int increasedArea = repeat * own.GetValueOrDefault(SkillArea.IncreasedPerRepeat);
        if (targetingIncrease != 0 && ActiveConsecratedGrounds(player, now).Any(g => g.Covers(aim)))
            increasedArea += targetingIncrease;
        if (increasedArea != 0) areaStats[SkillArea.Increase] = Get(SkillArea.Increase) + increasedArea;

        int baseRadiusBonus = areaStats.GetValueOrDefault(Stat.ActiveSkillBaseRadiusPlus);
        int impact = SkillArea.Radius(PurifyingFlameImpactRadius, Get, spell: true);
        int wave = SkillArea.Radius(PurifyingFlameWaveRadius, Get, spell: true);
        int width = SkillArea.Radius(PurifyingFlamePathWidth - baseRadiusBonus, Get, spell: true);
        int ground = SkillArea.Radius(PurifyingFlameGroundRadius - baseRadiusBonus
            + own.GetValueOrDefault(Stat.ActiveSkillGroundConsecrationRadiusPlus), Get, spell: true);
        var direction = GroundSlamWave.Direction(from, aim);
        double length = Dist(from, aim);
        var struck = new HashSet<uint>();
        int reach = Math.Max(impact, (int)Math.Ceiling(Math.Sqrt(length * length + (double)width * width)));

        double duration = SkillDurationModifiers.Seconds(own.GetValueOrDefault(Stat.BaseSkillEffectDuration),
            caster.GetValueOrDefault, own.GetValueOrDefault, DurationKinds(player, effect));
        if (ground > 0 && duration > 0)
        {
            var kind = FlaskGrounds.Kind.ConsecratedGround;
            int taken = OwnConsecratedGroundCreatedLocked(player, caster)
                + own.GetValueOrDefault(Stat.SanctifyConsecratedGroundEnemyDamageTakenPlusPct)
                + caster.GetValueOrDefault(Stat.SanctifyConsecratedGroundEnemyDamageTakenPlusPct);
            player.FlaskGrounds.Add(new(kind, aim.X, aim.Y, ground, now + duration,
                QueueGroundArtLocked(player, kind, aim, ground), taken));
            PresentNearby(player);
            session.Enqueue(() => PublishFlaskChanges(player));
        }
        hit(reach, (_, at) => CoversImpact(at), 1, id => struck.Add(id));

        double waveMore = Math.Max(0, 1 + own.GetValueOrDefault(Stat.SanctifyWaveDamagePlusPctFinal) / 100d);
        if (wave <= 0 || waveMore <= 0) return;
        session.ScheduleContact(session.Runtime.Now + ProjectileRuntime.Time(PurifyingFlameWaveDelay), current, () =>
        {
            var active = ActiveConsecratedGrounds(player, ProjectileWorldTime(player));
            if (active.Length > 0)
                hit(wave, (id, at) => !struck.Contains(id) && active.Any(g => g.Covers(at)), waveMore, null);
        });

        int Get(uint key) => areaStats.GetValueOrDefault(key);

        bool CoversImpact((int X, int Y) at)
        {
            if (Dist(aim, at) <= impact) return true;
            if (length == 0 || width == 0) return false;
            double x = at.X - from.X, y = at.Y - from.Y;
            double along = x * direction.X + y * direction.Y;
            return along >= 0 && along <= length && Math.Abs(y * direction.X - x * direction.Y) <= width;
        }
    }

    static FlaskGrounds.Ground[] ActiveConsecratedGrounds(Player player, double now) =>
        [.. AreaPlayers(player).Where(p => !p.Closed && !p.LeavingArea).SelectMany(p => p.FlaskGrounds.Grounds)
            .Where(g => g.Kind == FlaskGrounds.Kind.ConsecratedGround && now < g.Ends && (g.SourceAlive?.Invoke(now) ?? true))];
}
