using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    // Both gem effects use ActionTypes 1323. The client's contact branches on the projected variation.
    const ushort HolyHammersAction = 0xb10d;
    const double HolyHammerFallSeconds = .25;

    static double HolyHammersContactDelay(Player player, double attackSeconds, out string? clip, out double fraction)
    {
        // All seven HolySlam timelines are views of the class's ground slam clips.
        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject;
        var weapon = AnimationWeaponProfileFor(player);
        var animation = Animations.SkillAttackContact(ao, weapon, HolyHammersSourceClip(weapon));
        clip = animation?.Name;
        fraction = animation?.Contact ?? Animations.AttackContact(ao, weapon);
        // In a lockstep frame the stats are applied before the actors are stepped. Publish a consumed charge only
        // after the contact's frame, so the native action reads the removable count that created its hammers.
        return AttackTiming.ContactDelay(attackSeconds, fraction, Program.FrameMs / 1000d, early: false);
    }

    static string HolyHammersSourceClip(WeaponProfile weapon) =>
        weapon.Staff ? "groundslam_staff" : weapon.TwoHandMelee ? "groundslam_maul"
        : weapon.DualWield ? "groundslam_mace_mace" : weapon.Shield ? "groundslam_mace_shield" : "groundslam_mace";

    static void ScheduleHolyHammersLocked(Player player,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, SupportedGem admitted,
        (int X, int Y) from, (int X, int Y) aim, ushort sequence, int repeat, AttackHand hand,
        SkillCatalog.Unarmed weapon, int? offCritical, SkillCriticalRoll critical, int radius,
        double damageMore, double ruthless, bool exerted, double now)
    {
        var session = ProjectilesFor(player);
        int Get(uint key) => admitted.Stats.GetValueOrDefault(key);
        bool spirals = SkillsFor(player).BehaviourVariation(gem.Effect) == 1;
        var charges = ChargesOf(player);
        int spent = !charges.Converted(ChargeType.Power) && now < charges.State.ExpiresAt(ChargeType.Power)
            ? charges.Spend(ChargeType.Power, spirals ? int.MaxValue : 1, now) : 0;
        double chargeBonus = spirals ? Get(Stat.HolyHammersDamagePlusPctFinalPerPowerChargeConsumed) * (double)spent
            : spent > 0 ? Get(Stat.HolyHammersDamagePlusPctFinalIfConsumingPowerCharge) : 0;
        double more = damageMore * Math.Max(0, 1 + chargeBonus / 100);
        var source = CaptureAreaAttack(player, gem.Id, admitted, hand, weapon, offCritical, more, more, "hammer");
        double firstFactor = spirals ? 1 : Math.Max(0, 1 + Get(Stat.HolyHammersDamagePlusPctFinalForInitialHammerInCascade) / 100d);
        var first = source with { HitMore = more * firstFactor, AilmentMore = more * firstFactor };
        int cascades = Math.Max(0, Get(Stat.HolyHammersMaximumNumberOfHammerslamCascades));
        if (radius <= 0) return;

        float yaw = from == aim ? player.Layout.SpawnRotation : MathF.Atan2(aim.X - from.X, from.Y - aim.Y);
        if (yaw < 0) yaw += MathF.Tau;
        var forward = (X: MathF.Sin(yaw), Y: -MathF.Cos(yaw));
        int reach = Math.Max(0, radius - LivingCollision.PlayerSize - 2);
        var intended = (from.X + (int)MathF.Floor(forward.X * reach + .5f),
            from.Y + (int)MathF.Floor(forward.Y * reach + .5f));
        var centre = from;
        var layout = player.Layout;
        bool Ground(int x, int y) => x >= 0 && y >= 0 && x < layout.GridW && y < layout.GridH
            && layout.WalkableFor(x, y, 0);
        int Trace(int x, int y) => player.Barrier.Contains((x, y)) ? 0
            : layout.Sight?.Invoke(x, y) ?? (Ground(x, y) ? 1 : 0);
        foreach (var cell in ClientPathfinder.Line(from, intended))
        {
            if (Trace(cell.X, cell.Y) == 0) break;
            centre = cell;
        }
        bool Active() => session.Active && player.PlayerLife > 0;
        long start = session.Runtime.Now;
        long fall = ProjectileRuntime.Time(HolyHammerFallSeconds);

        bool Hit((int X, int Y) at, RetainedAreaAttack attack)
        {
            if (!Active()) return false;
            double time = ProjectileWorldTime(player);
            var candidates = player.Area.MonstersNear(at, radius)
                .Where(pair => pair.Value is { Dead: false, Life: > 0, Hostile: true, OwnerId: null, WorldHidden: false }
                    && Dist(at, pair.Value.Walk.Position(time)) <= radius + LivingCollision.Size(pair.Value))
                .Select(pair => pair.Key).ToArray();
            if (candidates.Length == 0) return false;
            bool landed = false;
            var hit = AreaSkillHitStrike(player, gem.Id, gem, at, at, immediate: true, sequence,
                actionToken: null, cooldownUse: null, melee: true, damageMore: 1, excludeMonster: 0, critical,
                channelGeneration: null, targetId: 0, repeat, radiusOverride: radius, once: null, repeatDelay: 0,
                radiusMore: 1, sacrifice: null, swingAt: null, triggeredCurrent: Active, discharge: null, strikeIndex: 0,
                firstContactAt: null, ruthless, priorTargets: null, scaledBlastChaos: 0, singleStrikeTarget: null,
                onHit: _ => landed = true, scheduledSession: session, cryExerted: exerted, prepaidRawExtra: 0,
                baseChaos: 0, hammer: null, retainedAttack: attack, allowMeleeSplash: false, contactTargets: candidates);
            if (!hit.IsCompleted) throw new InvalidOperationException("Scheduled Holy Hammers contact must not yield.");
            hit.GetAwaiter().GetResult();
            return landed;
        }

        if (spirals)
        {
            session.Runtime.Schedule(start, () => Hit(centre, source));
            int count = ActorScale.Saturate(cascades + (long)spent * Get(Stat.HolyHammersNumAdditionalHammerslamsPerConsumedPowerCharge));
            long interval = ProjectileRuntime.Time(.1);
            long at = start + fall;
            foreach (var cell in HolyHammerSpiral(from, centre, forward, radius, count))
            {
                at += interval;
                if (Ground(cell.X, cell.Y)) session.Runtime.Schedule(at, () => Hit(cell, source));
            }
            return;
        }

        var step = ((int)MathF.Floor(forward.X * (radius * 1.4f)), (int)MathF.Floor(forward.Y * (radius * 1.4f)));
        void Cascade((int X, int Y) at, int remaining, RetainedAreaAttack attack)
        {
            if (!Hit(at, attack) || remaining <= 0) return;
            var next = (at.X + step.Item1, at.Y + step.Item2);
            if (!ClientPathfinder.LineBelow(at, next, Trace, 1))
                session.Runtime.Schedule(session.Runtime.Now + fall, () => Cascade(next, remaining - 1, source));
        }

        int sides = ActorScale.Saturate((long)Get(Stat.HolyHammersNumberOfAdditionalHammerslamsToCreate)
            + (spent > 0 ? Get(Stat.HolyHammersNumAdditionalHammerslamsIfConsumingPowerCharge) : 0));
        var side = (X: -(int)MathF.Floor(forward.Y * (radius * 1.6f)), Y: (int)MathF.Floor(forward.X * (radius * 1.6f)));
        for (int i = 0; i < sides; i++)
        {
            int offset = (i / 2 + 1) * (i % 2 == 0 ? 1 : -1);
            var at = (centre.X + side.X * offset, centre.Y + side.Y * offset);
            if (!ClientPathfinder.LineBelow(centre, at, Trace, 1))
                session.Runtime.Schedule(start + fall, () => Cascade(at, cascades, first));
        }
        session.Runtime.Schedule(start, () => Cascade(centre, cascades, first));
    }

    internal static IEnumerable<(int X, int Y)> HolyHammerSpiral((int X, int Y) origin, (int X, int Y) centre,
        (float X, float Y) forward, int radius, int count)
    {
        // The native spiral starts at the centred impact. Each new polar point is one impact diameter from the last.
        var across = (X: forward.Y, Y: -forward.X);
        int x = centre.X - origin.X, y = centre.Y - origin.Y;
        float previousAngle = MathF.Atan2(x * forward.X + y * forward.Y, x * across.X + y * across.Y);
        float initialRadius = MathF.Sqrt(x * (float)x + y * (float)y);
        float previousRadius = initialRadius;
        float slope = radius / MathF.PI;
        float spacing = radius * 2f;
        for (int i = 0; i < count; i++)
        {
            float low = previousAngle + .01f;
            float high = previousAngle + .1f;
            float step = spacing / Math.Max(1, previousRadius);
            float DistanceSquared(float angle)
            {
                float r = initialRadius + slope * angle;
                return r * r + previousRadius * previousRadius - 2 * r * previousRadius * MathF.Cos(angle - previousAngle);
            }

            while (DistanceSquared(high) < spacing * spacing) high += step;
            for (int bit = 0; bit < 10; bit++)
            {
                float mid = (low + high) * .5f;
                if (DistanceSquared(mid) < spacing * spacing) low = mid;
                else high = mid;
            }
            previousAngle = high;
            previousRadius = initialRadius + slope * high;
            float side = previousRadius * MathF.Cos(high), along = previousRadius * MathF.Sin(high);
            yield return ((int)MathF.Floor(origin.X + .5f + side * across.X + along * forward.X),
                (int)MathF.Floor(origin.Y + .5f + side * across.Y + along * forward.Y));
        }
    }
}
