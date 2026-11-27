using GameServer.Core.Projectiles;

public static partial class Game
{
    const ushort GlacialShieldSwipeAction = 0xc727;
    const int GlacialShieldSwipeSteps = 14;
    const double GlacialShieldSwipeStepSeconds = .05;
    const double SwipeSin = .766044443118978, SwipeCos = .642787609686539;

    static void ScheduleGlacialShieldSwipeLocked(Player player,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, SupportedGem source,
        SkillCriticalRoll critical, (int X, int Y) aim, ushort sequence, double now, int radius, double damageMore, double ruthless)
    {
        if (radius <= 0 || ShieldWeapon(player, gem.Id) is not { } shield) return;
        var origin = player.Walk.Position(now);
        var facing = origin == aim
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
            var targets = RetaliationConeTargetsLocked(player, origin, facing, SwipeSin, SwipeCos,
                radius, step, GlacialShieldSwipeSteps, seen);
            HitRetaliationWaveLocked(player, gem, origin, sequence, radius, critical, ruthless, session, Current, attack, seen, targets);
            if (step < GlacialShieldSwipeSteps) Schedule(step + 1);
        });
    }
}
