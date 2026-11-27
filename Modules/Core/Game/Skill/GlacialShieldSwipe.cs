using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    const ushort GlacialShieldSwipeAction = 0xc727;
    const int GlacialShieldSwipeSteps = 14;
    const int GlacialShieldSwipeBand = 10;
    const double GlacialShieldSwipeStepSeconds = .05;
    const double SwipeSin = .766044443118978, SwipeCos = .642787609686539;

    static void ScheduleGlacialShieldSwipeLocked(Player player,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, SupportedGem source,
        SkillCriticalRoll critical, (int X, int Y) aim, ushort sequence, double now, int radius, double damageMore, double ruthless)
    {
        if (radius <= 0 || ShieldWeapon(player, gem.Id) is not { } shield) return;
        var origin = player.Walk.Position(now);
        var (facingX, facingY) = origin == aim
            ? (Math.Sin(player.Layout.SpawnRotation), -Math.Cos(player.Layout.SpawnRotation))
            : GroundSlamWave.Direction(origin, aim);
        var attack = CaptureAreaAttack(player, gem.Id, source, AttackHand.Main, shield, null, damageMore, damageMore, "ice-wave");
        var session = ProjectilesFor(player);
        var seen = new HashSet<uint>();
        int interruption = player.ActionInterruptionGeneration;
        bool Current() => PlayerStateAllowsAction(player) && interruption == player.ActionInterruptionGeneration;
        long start = session.Runtime.Now;
        Schedule(0);

        // RetaliationIceWave advances a 10-cell band over .7 s (0x141bdb400/0x141bdb590).
        // Its five adjacent 20° sectors share one set of targets (0x141bdb160/0x141be07d0).
        void Schedule(int step) => session.ScheduleContact(start + ProjectileRuntime.Time(step * GlacialShieldSwipeStepSeconds), Current, () =>
        {
            int inner = (int)((long)Math.Max(0, radius - GlacialShieldSwipeBand) * step / GlacialShieldSwipeSteps);
            int outer = Math.Min(radius, inner + GlacialShieldSwipeBand);
            var time = ProjectileWorldTime(player);
            List<uint>? targets = null;
            foreach (var (id, monster) in player.Area.MonstersNear(origin, outer))
            {
                if (!monster.Hostile || monster.Dead || monster.Life == 0 || monster.OwnerId is not null
                    || monster.WorldHidden || seen.Contains(id)) continue;
                var at = monster.Walk.Position(time);
                double dx = at.X - origin.X, dy = at.Y - origin.Y;
                double distanceSq = dx * dx + dy * dy;
                int body = LivingCollision.Size(monster);
                double near = Math.Max(0, inner - body), far = (double)outer + body;
                if (distanceSq < near * near || far < 0 || distanceSq > far * far) continue;
                double forward = dx * facingX + dy * facingY;
                double across = dy * facingX - dx * facingY;
                if (forward * SwipeSin < Math.Abs(across) * SwipeCos || !MeleeArcSight(player, origin, at)) continue;
                (targets ??= []).Add(id);
            }
            if (targets is not null)
            {
                var hit = AreaSkillHitStrike(player, gem.Id, gem, origin, origin, immediate: true, sequence,
                    actionToken: null, cooldownUse: null, melee: true, damageMore: 1, excludeMonster: 0,
                    criticalRoll: critical, channelGeneration: null, targetId: 0, repeat: 0, radiusOverride: radius,
                    once: seen, repeatDelay: 0, radiusMore: 1, sacrifice: null, swingAt: null, triggeredCurrent: Current,
                    discharge: null, strikeIndex: 0, firstContactAt: null, ruthlessMore: ruthless, priorTargets: null,
                    scaledBlastChaos: 0, singleStrikeTarget: null, onHit: null, scheduledSession: session, cryExerted: false,
                    prepaidRawExtra: 0, baseChaos: 0, hammer: null, retainedAttack: attack, allowMeleeSplash: false,
                    contactTargets: targets);
                if (!hit.IsCompleted) throw new InvalidOperationException("Scheduled Glacial Shield Swipe contact must not yield.");
                hit.GetAwaiter().GetResult();
            }
            if (step < GlacialShieldSwipeSteps) Schedule(step + 1);
        });
    }
}
