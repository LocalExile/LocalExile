using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    const string WindburstEffect = "TriggeredSupportWindburst";
    const string WindburstArt = "Metadata/Projectiles/TriggeredSupportTornado";
    const uint WindburstDistanceStat = Stat.SupportTriggerTornadosOnAttackHitAfterMovingXMetres;

    static readonly ProjectileSkillDefinition WindburstProjectile = new(WindburstArt,
        new((get, speed, _) => GatewayProjectileDefaults.Resolve(WindburstArt, get, speed, attack: true)));
    static readonly IReadOnlySet<ProjectileEndReason> WindburstExpiry =
        new HashSet<ProjectileEndReason> { ProjectileEndReason.Lifetime };

    sealed class WindburstProgress
    {
        public PlayerWalk? Walk;
        public double LastTrigger;
    }
    static readonly Store<Player, WindburstProgress> WindburstDistances = new();

    static void TriggerWindburstLocked(Player player, ushort skill, int effect, (int X, int Y) target, double at,
        int metres, IReadOnlyDictionary<uint, int> playerStats)
    {
        if (player is { Closed: true } or { LeavingArea: true } or { Paused: true } or { PlayerLife: 0 }) return;
        var walk = player.Walk;
        var progress = WindburstDistances.GetOrCreateValue(player);
        if (!ReferenceEquals(progress.Walk, walk))
        {
            progress.Walk = walk;
            progress.LastTrigger = 0;
        }
        double distance = walk.TravelledCells(at);
        double since = distance - progress.LastTrigger;
        if (since < metres * 10d) return;

        var parent = SupportedSkill(player, skill);
        if (effect >= 0 && parent.Effect != effect
            || parent.Stats.GetValueOrDefault(WindburstDistanceStat) != metres
            || !SkillsFor(player).IsAttack(parent.Effect) || !parent.RequirementsMet
            || PlacedKind(parent) is not (false, false) || parent.Stats.GetValueOrDefault(Stat.IsTotem) != 0
            || TriggerOnlyReason(parent) is not null
            || parent.Supports.FirstOrDefault(s => SupportHas(s, WindburstDistanceStat)) is not { } support
            || SupportCompanionSourceLocked(player, parent, support, WindburstEffect, "HungryLoopSupportedByTornados") is not { } source
            || GemSkillInstanceDisabled(player, source.Effect, source.Id, triggered: true) is not null) return;

        var session = ProjectilesFor(player);
        if (!session.Active) return;
        var gem = SupportedSkill(player, source.Id);
        int Own(uint stat) => gem.Stats.GetValueOrDefault(stat);
        double life = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), playerStats.GetValueOrDefault,
            Own, DurationKinds(player, source.Effect));
        int radius = SkillRadius(player, gem.Stats, spell: false, source.Effect);
        if (life <= 0 || radius <= 0) return;
        double areaMore = Math.Max(0, 1 + Own(Stat.ActiveSkillAreaDamagePlusPctFinal) / 100d);
        var origin = walk.Position(at);
        ushort sequence = NextLockstepSequence(player);
        var skillUse = (source.Id, source.Effect, Name: "Windburst", gem.Level, gem.Quality, source.Gem);
        var use = CaptureGemProjectile(player, session, source.Id, skillUse, target,
            CaptureSkillCriticalRoll(player, source.Id), sequence, from: origin, capturedSkill: gem);
        var shot = (ProjectileOffense)use.Payload!;
        double speed = use.Profile.Speed;
        use = use with
        {
            Payload = shot with
            {
                ClientPredicted = false, MirageSource = null, CriticalTrigger = null, KineticFluxDriver = null
            },
            Profile = use.Profile with
            {
                Returns = false, CannotReturn = true, NativeVolley = new() { Nova = true },
                EndArea = new("Windburst expiry", radius, Multiplier: areaMore),
                EndAreaReasons = WindburstExpiry,
                Legs = index => new SparkLegs(sequence, index, use.Owner.Id, life, 0, speed)
            }
        };
        progress.LastTrigger = distance;
        session.ScheduleContact(ProjectileRuntime.Time(at), () => player.PlayerLife > 0, () =>
        {
            session.ReleaseNow(use);
            if (ServerTrace.Projectiles)
                Console.WriteLine($"[windburst] release at={session.Runtime.Now} travelled={since:F2} origin={origin} target={target}");
        });
    }
}
