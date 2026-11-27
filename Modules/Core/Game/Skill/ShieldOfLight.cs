using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    const ushort ShieldOfLightAction = 0x8a7a;
    const int ShieldOfLightSteps = 10;
    const double ShieldOfLightStepSeconds = .025;
    const double ShieldOfLightSin = .8660254037844386, ShieldOfLightCos = .5;
    const int NativeShieldWaveBucketSize = 23;

    static void ScheduleShieldOfLightLocked(Player player,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, SupportedGem source,
        SkillCriticalRoll critical, (int X, int Y) aim, ushort sequence, double now,
        int radius, int allyRadius, double damageMore, double ruthless)
    {
        if (radius <= 0 || ShieldWeapon(player, gem.Id) is not { } shield) return;
        var origin = player.Walk.Position(now);
        var facing = origin == aim
            ? (Math.Sin(player.Layout.SpawnRotation), -Math.Cos(player.Layout.SpawnRotation))
            : GroundSlamWave.Direction(origin, aim);
        var attack = CaptureAreaAttack(player, gem.Id, source, AttackHand.Main, shield, null, damageMore, damageMore, "light wave");
        var alliedAttack = attack with { Phase = "ally light wave" };
        var session = ProjectilesFor(player);
        int interruption = player.ActionInterruptionGeneration;
        bool Current() => PlayerStateAllowsAction(player) && interruption == player.ActionInterruptionGeneration;
        int allyLimit = source.Stats.GetValueOrDefault(Stat.MaximumNumberOfAdditionalShieldsOfLight);
        bool seekAllies = allyRadius > 0 && allyLimit > 0;
        StartWave(origin, facing, attack, primary: true);

        // RetaliationLightningWave advances its 120° cone over .25 s (0x141bdbda0/0x141bdbff0).
        // Each ally gets an independent wave, aimed at the first enemy contacted by the caster (0x141bdc1f0/0x141bdded0).
        void StartWave((int X, int Y) centre, (double X, double Y) direction, RetainedAreaAttack hit, bool primary)
        {
            long start = session.Runtime.Now;
            var seen = new HashSet<uint>();
            Schedule(0);
            void Schedule(int step) => session.ScheduleContact(start + ProjectileRuntime.Time(step * ShieldOfLightStepSeconds), Current, () =>
            {
                var targets = RetaliationConeTargetsLocked(player, centre, direction, ShieldOfLightSin, ShieldOfLightCos,
                    radius, step, ShieldOfLightSteps, seen);
                if (primary && seekAllies && targets is not null)
                {
                    seekAllies = false;
                    double at = ProjectileWorldTime(player);
                    var anchor = targets.Select(id => (Id: id, At: player.Area.Monsters[id].Walk.Position(at)))
                        .MinBy(target => ShieldOfLightScanOrder(target.Id, target.At)).At;
                    foreach (var ally in ShieldOfLightAllies(player, anchor, at, allyRadius).Take(allyLimit))
                        StartWave(ally, GroundSlamWave.Direction(ally, anchor), alliedAttack, primary: false);
                }
                HitRetaliationWaveLocked(player, gem, centre, sequence, radius, critical, ruthless, session, Current, hit, seen, targets);
                if (step < ShieldOfLightSteps) Schedule(step + 1);
            });
        }
    }

    static (int Y, int X, uint Id) ShieldOfLightScanOrder(uint id, (int X, int Y) at) =>
        (at.Y / NativeShieldWaveBucketSize, at.X / NativeShieldWaveBucketSize, id);

    static IEnumerable<(int X, int Y)> ShieldOfLightAllies(Player player, (int X, int Y) anchor, double now, int radius)
    {
        // The native 23-cell buckets are visited row by row, with the oldest object first in each bucket.
        var peers = AreaPlayers(player).Where(ally => ally.Area == player.Area && !ally.Closed && !ally.LeavingArea).ToArray();
        var eligible = new List<(uint Id, (int X, int Y) At)>();
        foreach (var ally in peers)
        {
            if (ReferenceEquals(ally, player) || ally.PlayerLife == 0) continue;
            var at = ally.Walk.Position(now);
            if (Dist(anchor, at) <= radius + LivingCollision.PlayerSize && MeleeArcSight(player, at, anchor))
                eligible.Add((ally.Id, at));
        }
        foreach (var (id, minion) in player.Area.MonstersNear(anchor, radius))
        {
            if (minion.Hostile || minion.Dead || minion.Life == 0 || minion.WorldHidden) continue;
            var at = minion.Walk.Position(now);
            if (Dist(anchor, at) > radius + LivingCollision.Size(minion) || !MeleeArcSight(player, at, anchor)) continue;
            var origin = player.Instance?.World.MonsterOrigins.GetValueOrDefault(id);
            foreach (var peer in peers)
            {
                if (origin is not null) break;
                origin = peer.MinionOrigins.GetValueOrDefault(id);
            }
            if (origin?.Object.Stats is { } stats
                && stats.Any(s => s.Key == Stat.IsPlayerMinion && s.Value != 0)
                && stats.Any(s => s.Key == Stat.IsSemanticMinion && s.Value != 0))
                eligible.Add((id, at));
        }
        return eligible.OrderBy(ally => ShieldOfLightScanOrder(ally.Id, ally.At)).Select(ally => ally.At);
    }
}
