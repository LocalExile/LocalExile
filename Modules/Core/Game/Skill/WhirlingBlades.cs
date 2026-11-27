using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    static bool IsWhirlingBlades(string? effect) =>
        effect?.StartsWith("WhirlingBlades", StringComparison.Ordinal) == true;

    static bool WhirlingBladesTarget(SpawnedMonster? monster) =>
        monster is { Hostile: true, Dead: false, Life: > 0, Asleep: false, OwnerId: null, WorldHidden: false };

    static void ScheduleWhirlingBlades(Player player,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) start, (int X, int Y) landing, ushort sequence, int action, int interruption,
        CooldownUseTicket? ticket, double? swingAt, SkillCriticalRoll critical, bool exerted, int? rallyingDamage)
    {
        if (start == landing) return;

        // BladeFlurry (0x815b) gathers contacts on the resolved slide, with five cells of clearance plus target size,
        // then sorts by squared distance from the start (3.29.3c 0x141b5cc70 / 0x141b6aa60 / 0x141b5d7d0).
        const int reach = 5, maximumTargets = 100;
        const double contactClearance = 10;
        using var gate = ProjectileMutation(player);
        var session = ProjectilesFor(player);
        var source = SupportedSkill(player, gem.Id);
        double now = ProjectileWorldTime(player);
        // All seven class rigs use leave_ground at 33 ms and hit_ground at 433 ms in their 1100 ms flurry clips.
        // Native movement scales that window by attack time and travelled distance / 55 (0x141b5d9a0).
        double length = Dist(start, landing);
        double scale = SupportedAttackSeconds(player, gem.Id, sequence) * length / 55;
        double takeoff = (swingAt ?? now) + scale * (33d / 1100);
        double travel = scale * (400d / 1100);
        bool Current() => PlayerStateAllowsAction(player) && player.ActionCount == action
            && player.ActionInterruptionGeneration == interruption && (ticket?.Current(player) ?? true)
            && SameTriggerSource(source, SupportedSkill(player, gem.Id))
            && GemSkillInstanceDisabled(player, gem.Effect, gem.Id) is null;
        session.ScheduleVenomGyreRelease(start, landing, takeoff, travel, Current);
        var cells = ClientPathfinder.Line(start, landing).ToArray();
        var sightBarrier = player.SightBarrier;
        var midpoint = ((start.X + landing.X) / 2, (start.Y + landing.Y) / 2);
        var targets = new List<(uint Id, SpawnedMonster Monster, double Distance)>();
        foreach (var (id, monster) in player.Area.MonstersNear(midpoint, length / 2 + reach + 1))
        {
            if (!WhirlingBladesTarget(monster)) continue;
            var at = monster.Walk.Position(now);
            int size = LivingCollision.Size(monster);
            if (size < 0) continue;
            int margin = reach + size;
            if (!cells.Any(cell => Math.Max(Math.Abs(at.X - cell.X), Math.Abs(at.Y - cell.Y)) < margin)
                || !MeleeArcSight(player, start, at)
                || sightBarrier.Count != 0 && ClientPathfinder.Line(start, at).Any(sightBarrier.Contains)) continue;
            targets.Add((id, monster, Dist(start, at)));
        }
        if (targets.Count == 0) return;

        foreach (var (id, monster, distance) in targets.OrderBy(target => target.Distance).Take(maximumTargets))
        {
            // The update releases a target when travelled^2 + 10 exceeds its stored squared distance.
            // On the final animation event any remaining contact is attempted at the landing.
            double fraction = Math.Min(1, Math.Sqrt(Math.Max(0, distance * distance - 10)) / length);
            var from = (X: (int)Math.Floor(start.X + (landing.X - start.X) * fraction + .5),
                Y: (int)Math.Floor(start.Y + (landing.Y - start.Y) * fraction + .5));
            session.ScheduleContact(ProjectileRuntime.Time(takeoff + travel * fraction), Current, () =>
            {
                if (!WhirlingBladesTarget(monster) || !ReferenceEquals(player.Area.Monsters.GetValueOrDefault(id), monster))
                    return;
                var at = monster.Walk.Position(ProjectileWorldTime(player));
                if (Dist(from, at) > contactClearance + LivingCollision.Size(monster)) return;
                var hit = AreaSkillHitForUse(null, player, gem.Id, gem, from, from, immediate: true, sequence: sequence,
                    actionToken: action, cooldownUse: ticket, criticalRoll: critical, scheduledSession: session,
                    triggeredCurrent: Current, cryExerted: exerted, rallyingDamage: rallyingDamage, contactTargets: [id]);
                if (!hit.IsCompleted) throw new InvalidOperationException("Scheduled Whirling Blades contact must not yield.");
                hit.GetAwaiter().GetResult();
            });
        }
    }
}
