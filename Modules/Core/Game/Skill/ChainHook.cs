using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const int ChainHookBaseRange = 60;
    const int ChainHookMaximumCloseRange = 15;
    static readonly double ChainHookSearchCosine = Math.Cos(27.5 * Math.PI / 180);
    static readonly double ChainHookImpactCosine = Math.Cos(70 * Math.PI / 180);

    public sealed partial class ProjectileSession
    {
        internal (int X, int Y)? ChainHookRest;
    }

    static bool IsBaseChainHook(string? effect) => effect is "ChainHook" or "ChainHookRoyale";

    // ChainStrike and ChainGrab clamp the aim to 60 cells before searching in a 55-degree sector.
    static int ChainHookRange(int increase) => (int)Math.Clamp(ChainHookBaseRange * (100L + increase) / 100, 0, 512);

    static bool ChainHookInSector((int X, int Y) origin, (int X, int Y) at,
        (double X, double Y) direction, double distance, double cosine) =>
        (at.X - origin.X) * direction.X + (at.Y - origin.Y) * direction.Y >= distance * cosine;

    static bool ChainHookTarget(SpawnedMonster? m) =>
        m is { Hostile: true, Dead: false, Life: > 0, Asleep: false, OwnerId: null, WorldHidden: false };

    static bool ChainHookSight(Player player, (int X, int Y) from, (int X, int Y) to)
    {
        if (player.Layout.Sight is { } sight && ClientPathfinder.LineBelow(from, to, sight, 1)) return false;
        return player.Barrier.Count == 0 || ClientPathfinder.Line(from, to).All(cell => !player.Barrier.Contains(cell));
    }

    // The native pull (0x141d04fa0) projects eight cells back from the anchor along its original ray.
    static (int X, int Y) ChainHookLanding(Player player, (int X, int Y) from,
        (int X, int Y) anchor, (double X, double Y) direction)
    {
        const int standOff = 8;
        if (Dist(from, anchor) <= standOff) return from;
        var destination = (X: anchor.X - (int)Math.Floor(direction.X * standOff + .5),
            Y: anchor.Y - (int)Math.Floor(direction.Y * standOff + .5));
        // Chain Strike places the player on the movement layer's nonzero cells (0x141d04fa0),
        // with a clear projectile ray. A cell on a wall's edge need not have full walking clearance.
        int size = LivingCollision.PlayerSize;
        foreach (var cell in ClientPathfinder.Line(destination, from))
        {
            if (cell == from) break;
            if (!player.Layout.WalkableFor(cell.X, cell.Y, 0) || !ChainHookSight(player, from, cell)) continue;
            if (player.Blockers.All(b => Math.Max(Math.Abs(cell.X - b.X), Math.Abs(cell.Y - b.Y)) > b.Size + size))
                return cell;
        }
        return from;
    }

    // ChainStrikeThrow reaches its release at the attack's duration. The beam's own contact then
    // invokes the pull and hit together (0x141d04270, 0x141d04a10 and chain_attach.ao).
    static double ChainHookFlight(double distance, int range, double rawAttackSeconds)
    {
        double fraction = Math.Min(1, distance / range);
        double beamRate = (2 - fraction * fraction) / (.5 + .5 * rawAttackSeconds);
        return (distance <= 30 ? .2075 : .415) / beamRate;
    }

    static Task ScheduleBaseChainHook(Player player, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) aim,
        uint targetId, int recovery, int action, ushort sequence, double? swingAt, CooldownUseTicket? ticket,
        SkillCriticalRoll critical, bool cryExerted, int? rallyingDamage)
    {
        var session = ProjectilesFor(player);
        session.Advance();
        var source = SupportedSkill(player, skillId);
        double began = swingAt ?? AreaTime(player);
        var hand = AttackHandFor(player, skillId, sequence);
        double seconds = SupportedAttackSeconds(player, skillId, hand);
        bool Current() => session.Active && PlayerStateAllowsAction(player) && player.ActionCount == action
            && player.ActionInterruptionGeneration == recovery && (ticket?.Current(player) ?? true)
            && SameTriggerSource(source, SupportedSkill(player, skillId))
            && GemSkillInstanceDisabled(player, gem.Effect, skillId) is null;

        session.Runtime.Schedule(Math.Max(session.Runtime.Now, ProjectileRuntime.Time(began)), () =>
        {
            if (!Current()) return;

            using var statScope = new PlayerStatScope(player, fresh: true);
            double now = ProjectileWorldTime(player);
            var origin = player.Walk.Position(now);
            var stats = SkillAreaStats(player, source.Stats, gem.Effect);
            int StatValue(uint stat) => stats.GetValueOrDefault(stat);
            int range = ChainHookRange(StatValue(Stat.ChainHookRangePlusPct));
            int rage = Math.Max(0, StatValue(Stat.CurrentRage));
            int rageDivisor = StatValue(Stat.ChainStrikeConeRadiusPlusPerXRage);
            int rageRadius = (rageDivisor > 0 ? rage / rageDivisor : 0)
                + (rage / 12) * StatValue(Stat.ChainStrikeConeRadiusPlusPer12Rage);
            int radius = SkillArea.Radius(24 + rageRadius, StatValue, false);
            int closeRange = Math.Min(ChainHookMaximumCloseRange, radius);

            var direction = origin == aim ? (0d, -1d) : GroundSlamWave.Direction(origin, aim);
            uint anchorId = 0;
            (int X, int Y) anchor = default;
            SpawnedMonster? target = player.Area.Monsters.GetValueOrDefault(targetId);
            if (ChainHookTarget(target))
            {
                var at = target!.Walk.Position(now);
                if (Dist(origin, at) <= range && ChainHookSight(player, origin, at))
                    (anchorId, anchor) = (targetId, at);
            }
            if (anchorId == 0)
            {
                double best = double.PositiveInfinity;
                foreach (var (id, m) in player.Area.MonstersNear(origin, range))
                {
                    if (!ChainHookTarget(m)) continue;
                    var at = m.Walk.Position(now);
                    double distance = Dist(origin, at);
                    if (distance >= best || distance > range
                        || !ChainHookInSector(origin, at, direction, distance, ChainHookSearchCosine)
                        || !ChainHookSight(player, origin, at)) continue;
                    (anchorId, anchor, best) = (id, at, distance);
                }
            }

            if (anchorId == 0 && Dist(origin, aim) > 12) return;
            if (anchorId != 0 && origin != anchor) direction = GroundSlamWave.Direction(origin, anchor);
            double distanceToAnchor = anchorId == 0 ? 0 : Dist(origin, anchor);
            bool pull = distanceToAnchor > closeRange;
            double delay;
            if (pull)
            {
                // The beam uses the hand's duration before the character's action-speed factor.
                double rawAttack = seconds * PlayerActionFactor(player);
                delay = seconds + ChainHookFlight(distanceToAnchor, range, rawAttack);
            }
            else
            {
                string ao = SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject;
                delay = Animations.SingleMeleeContactSeconds(ao, AnimationWeaponProfileFor(player), sequence, seconds);
            }
            // Keep early attack admission from cutting off the paired swing or the travelling beam before contact.
            player.NextAttackAt = Math.Max(player.NextAttackAt, began + delay + seconds * AttackTiming.EarlyAllowance);

            var hooked = pull ? player.Area.Monsters.GetValueOrDefault(anchorId) : null;
            long due = ProjectileRuntime.Time(began + delay);
            session.Runtime.Schedule(Math.Max(session.Runtime.Now, due), () =>
            {
                if (!Current()) return;
                using var hitScope = new PlayerStatScope(player, fresh: true);
                double at = ProjectileWorldTime(player);
                var centre = player.Walk.Position(at);
                if (pull)
                {
                    if (!ChainHookTarget(hooked) || !ReferenceEquals(hooked, player.Area.Monsters.GetValueOrDefault(anchorId))) return;
                    var point = hooked!.Walk.Position(at);
                    if (!ChainHookSight(player, centre, point) || Dist(centre, point) > range) return;
                    var landing = ChainHookLanding(player, centre, point, direction);
                    if (landing == centre || Dist(landing, point) > closeRange) return;
                    player.Walk.Teleport(landing.X, landing.Y, at);
                    session.ChainHookRest = player.Layout.Walkable?.Invoke(landing.X, landing.Y) == false ? landing : null;
                    session.World(player.Id, InstanceProtocol.Teleport(player.Id, landing.X, landing.Y), "Chain Hook pull");
                    centre = landing;
                }

                // A victim in both the outer sector and the inner sweep receives one contact.
                int inner = SkillArea.Radius(15, stat => stat == Stat.ActiveSkillBaseRadiusPlus ? 0 : StatValue(stat), false);
                var hit = AreaSkillHitForUse(null, player, skillId, gem, centre, centre, immediate: true,
                    sequence: sequence, criticalRoll: critical, radiusOverride: Math.Max(inner, radius),
                    triggeredCurrent: Current, scheduledSession: session, cryExerted: cryExerted,
                    rallyingDamage: rallyingDamage, nearbyOnly: true, targetFilter: (id, targetAt) =>
                    {
                        if (!ChainHookTarget(player.Area.Monsters.GetValueOrDefault(id)) || !ChainHookSight(player, centre, targetAt))
                            return false;
                        double distance = Dist(centre, targetAt);
                        return distance <= inner || distance <= radius
                            && ChainHookInSector(centre, targetAt, direction, distance, ChainHookImpactCosine);
                    });
                if (!hit.IsCompleted) throw new InvalidOperationException("Scheduled Chain Hook contact must not yield.");
                hit.GetAwaiter().GetResult();
            });
        });
        return Task.CompletedTask;
    }
}
