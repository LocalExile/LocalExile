using GameServer.Core.Projectiles;

public static partial class Game
{
    const ushort EviscerateAction = 0x5f14;
    const int EviscerateTravelMs = 333, EviscerateStepMs = 10;

    readonly record struct EviscerateWave(ProjectileVector Tip, ProjectileVector Axis, double Length)
    {
        internal static EviscerateWave At(ProjectileVector direction, double extent)
        {
            var tip = new ProjectileVector(Math.Floor(direction.X * extent + .5), Math.Floor(direction.Y * extent + .5));
            return new(tip, tip.Unit, tip.Length);
        }

        internal bool Covers(ProjectileVector target, double halfWidth, double body)
        {
            if (Length == 0) return false;
            double absX = Math.Abs(Axis.X), absY = Math.Abs(Axis.Y);
            double extraX = halfWidth * absY + body, extraY = halfWidth * absX + body;
            if (target.X < Math.Min(0, Tip.X) - extraX || target.X > Math.Max(0, Tip.X) + extraX
                || target.Y < Math.Min(0, Tip.Y) - extraY || target.Y > Math.Max(0, Tip.Y) + extraY) return false;
            double projectedBody = body * (absX + absY);
            double along = target.Dot(Axis);
            double across = target.Y * Axis.X - target.X * Axis.Y;
            return along >= -projectedBody && along <= Length + projectedBody
                && Math.Abs(across) <= halfWidth + projectedBody;
        }
    }

    static void ScheduleEviscerateLocked(Player player,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, SupportedGem source,
        SkillCatalog.Unarmed weapon, SkillCriticalRoll critical, (int X, int Y) aim, ushort sequence,
        double now, int range, int width, double damageMore, double ruthless)
    {
        if (range <= 0 || width <= 0) return;
        var origin = player.Walk.Position(now);
        var point = ProjectileSession.Vector(origin);
        var direction = aim == origin
            ? new ProjectileVector(Math.Sin(player.Layout.SpawnRotation), -Math.Cos(player.Layout.SpawnRotation))
            : (ProjectileSession.Vector(aim) - point).Unit;
        var right = direction.Rotate(Math.PI / 6);
        var left = direction.Rotate(-Math.PI / 6);
        var rightHit = CaptureAreaAttack(player, gem.Id, source, AttackHand.Main, weapon, null, damageMore, damageMore, "right wave");
        var leftHit = rightHit with { Phase = "left wave" };
        var rightSeen = new HashSet<uint>();
        var leftSeen = new HashSet<uint>();
        var session = ProjectilesFor(player);
        int interruption = player.ActionInterruptionGeneration;
        bool Current() => PlayerStateAllowsAction(player) && interruption == player.ActionInterruptionGeneration;
        long start = session.Runtime.Now;
        double halfWidth = width / 2d;
        Schedule(EviscerateStepMs);

        // RetaliationShieldEvisceration expands two rectangular strips over .333 s (0x141bdc8b0/0x141bdd820).
        // Each strip keeps its own hit set, so a target in the overlap takes one hit from each (0x141be3fa0).
        void Schedule(int age) => session.ScheduleContact(start + ProjectileRuntime.Time(age / 1000d), Current, () =>
        {
            double extent = range * (age / (double)EviscerateTravelMs);
            var rightWave = EviscerateWave.At(right, extent);
            var leftWave = EviscerateWave.At(left, extent);
            double time = ProjectileWorldTime(player);
            List<uint>? rightTargets = null, leftTargets = null;
            double reach = Math.Sqrt(extent * extent + halfWidth * halfWidth) + 1;
            foreach (var (id, monster) in player.Area.MonstersNear(origin, reach))
            {
                if (!monster.Hostile || monster.Dead || monster.Life == 0 || monster.OwnerId is not null
                    || monster.WorldHidden || rightSeen.Contains(id) && leftSeen.Contains(id)) continue;
                var at = monster.Walk.Position(time);
                var offset = ProjectileSession.Vector(at) - point;
                int body = Math.Max(0, LivingCollision.Size(monster));
                bool reachesRight = !rightSeen.Contains(id) && rightWave.Covers(offset, halfWidth, body);
                bool reachesLeft = !leftSeen.Contains(id) && leftWave.Covers(offset, halfWidth, body);
                if (!(reachesRight || reachesLeft) || !MeleeArcSight(player, origin, at)) continue;
                if (reachesRight) (rightTargets ??= []).Add(id);
                if (reachesLeft) (leftTargets ??= []).Add(id);
            }
            HitRetaliationWaveLocked(player, gem, origin, sequence, range, critical, ruthless, session, Current, rightHit, rightSeen, rightTargets);
            HitRetaliationWaveLocked(player, gem, origin, sequence, range, critical, ruthless, session, Current, leftHit, leftSeen, leftTargets);
            if (age < EviscerateTravelMs) Schedule(Math.Min(age + EviscerateStepMs, EviscerateTravelMs));
        });
    }
}
