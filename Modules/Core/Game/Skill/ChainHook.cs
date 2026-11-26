using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const int ChainHookBaseRange = 60;
    const int ChainHookMaximumCloseRange = 15;
    static readonly double ChainHookSearchCosine = Math.Cos(27.5 * Math.PI / 180);
    static readonly double ChainHookImpactCosine = Math.Cos(70 * Math.PI / 180);

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
        int size = LivingCollision.PlayerSize;
        foreach (var cell in ClientPathfinder.Line(from, destination).Skip(1))
            if (!player.Layout.WalkableFor(cell.X, cell.Y, size) || player.Barrier.Contains(cell)) return from;
        return player.Blockers.Any(b => Math.Max(Math.Abs(destination.X - b.X), Math.Abs(destination.Y - b.Y)) <= b.Size + size)
            ? from : destination;
    }

    static Task ScheduleBaseChainHook(Player player, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) aim,
        uint targetId, int recovery, int action, ushort sequence, double? swingAt, CooldownUseTicket? ticket,
        SkillCriticalRoll critical, bool cryExerted, int? rallyingDamage)
    {
        var session = ProjectilesFor(player);
        session.Advance();
        var source = SupportedSkill(player, skillId);
        double delay = GemAttackContactDelay(player, gem.Effect, SupportedAttackSeconds(player, skillId), out _, out _);
        long due = ProjectileRuntime.Time((swingAt ?? AreaTime(player)) + delay);
        session.Runtime.Schedule(Math.Max(session.Runtime.Now, due), () =>
        {
            bool Current() => session.Active && PlayerStateAllowsAction(player) && player.ActionCount == action
                && player.ActionInterruptionGeneration == recovery && (ticket?.Current(player) ?? true)
                && SameTriggerSource(source, SupportedSkill(player, skillId))
                && GemSkillInstanceDisabled(player, gem.Effect, skillId) is null;
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
            if (anchorId != 0)
            {
                if (origin != anchor) direction = GroundSlamWave.Direction(origin, anchor);
                if (Dist(origin, anchor) > closeRange)
                {
                    var landing = ChainHookLanding(player, origin, anchor, direction);
                    if (landing == origin || Dist(landing, anchor) > closeRange) return;
                    player.Walk.Teleport(landing.X, landing.Y, now);
                    session.World(player.Id, InstanceProtocol.Teleport(player.Id, landing.X, landing.Y), "Chain Hook pull");
                    origin = landing;
                }
            }

            // ChainStrike hits a 140-degree outer sector and a separate 15-cell inner sweep. A victim in
            // both shapes still receives one contact from this use.
            int inner = SkillArea.Radius(15, stat => stat == Stat.ActiveSkillBaseRadiusPlus ? 0 : StatValue(stat), false);
            var hit = AreaSkillHitForUse(null, player, skillId, gem, origin, origin, immediate: true,
                sequence: sequence, criticalRoll: critical, radiusOverride: Math.Max(inner, radius),
                triggeredCurrent: Current, scheduledSession: session, cryExerted: cryExerted,
                rallyingDamage: rallyingDamage, nearbyOnly: true, targetFilter: (id, at) =>
                {
                    if (!ChainHookTarget(player.Area.Monsters.GetValueOrDefault(id)) || !ChainHookSight(player, origin, at))
                        return false;
                    double distance = Dist(origin, at);
                    return distance <= inner || distance <= radius
                        && ChainHookInSector(origin, at, direction, distance, ChainHookImpactCosine);
                });
            if (!hit.IsCompleted) throw new InvalidOperationException("Scheduled Chain Hook contact must not yield.");
            hit.GetAwaiter().GetResult();
        });
        return Task.CompletedTask;
    }
}
