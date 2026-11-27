using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const double SweepClipSeconds = 1.166;
    const double SweepWindupSeconds = .990;
    const double SweepCentreOffset = 50 / WorldUnits.PerCell;
    const double SweepStep = 100 / WorldUnits.PerCell;
    const double SweepTickSeconds = (double)ProjectileRuntime.TickQuanta / ProjectileRuntime.QuantaPerSecond;
    const int FrozenSweepRadius = 25;
    static readonly uint FrozenStatueType = InstanceEntities.ObjectTypeHash(FrozenLegionStatueObject.TypePath);

    static bool IsFrozenLegionEffect(string? effect) => effect is "FrozenLegion" or "FrozenLegionAltX";

    static bool ScheduleFrozenLegion(Player player,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) aim, ushort sequence, int castMs, int recovery,
        double? startedAt, CooldownUseTicket? ticket, Func<bool> castCurrent)
    {
        using var mutation = ProjectileMutation(player);
        if (ticket is not { State: { } state, FrozenLegionUses: > 0 } || !castCurrent()) return false;
        var session = ProjectilesFor(player);
        var parent = SupportedSkill(player, gem.Id);
        double release = (startedAt ?? AreaTime(player)) + castMs / 1000d
            * Animations.SpellAreaRelease(SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject);
        bool CastCurrent() => castCurrent() && player.ActionInterruptionGeneration == recovery
            && SameTriggerSource(parent, SupportedSkill(player, gem.Id));
        session.ScheduleContact(ProjectileRuntime.Time(release), CastCurrent, Place);
        return true;

        void Place()
        {
            var catalog = SkillsFor(player);
            string sweepId = catalog.EffectId(gem.Effect) == "FrozenLegionAltX" ? "FrozenSweepAltX" : "FrozenSweep";
            var sweep = GemSkillEffects(player).FirstOrDefault(g => g.Gem == gem.Gem && catalog.EffectId(g.Effect) == sweepId
                && ReferenceEquals(SupportedSkill(player, g.Id).SourceGem, parent.SourceGem));
            if (sweep.Id == 0) return;

            var source = SupportedSkill(player, sweep.Id);
            var stats = CharacterPlayerStats(player);
            var area = SkillAreaStats(player, source.Stats, sweep.Effect, stats);
            int radius = SkillArea.Radius(FrozenSweepRadius, key => area.GetValueOrDefault(key), spell: false);
            // The statue's wire skill needs its native row and the resolved radius, not the owner's actor stats.
            var actorSkill = catalog.FindByRow(sweep.Id, sweep.Effect, sweep.Level,
                radius == FrozenSweepRadius ? null : [(Stat.ActiveSkillBaseRadiusPlus, radius - FrozenSweepRadius)]);
            if (actorSkill is null) return;

            double now = ProjectileWorldTime(player);
            state.Refresh(now, state.Rate);
            int count = Math.Min(ticket.FrozenLegionUses, Math.Max(0, state.Uses - state.SpentUses)
                + (ticket.CooldownReset ? 0 : 1));
            if (count <= 0) return;
            var origin = player.Walk.Position(now);
            var initial = GroundSlamWave.Direction(from, aim);
            double first = from == aim ? player.Layout.SpawnRotation - Math.PI / 2 : Math.Atan2(initial.Y, initial.X);
            int distance = Math.Max(1, parent.Stats.GetValueOrDefault(Stat.RingOfIcePlacementDistance));
            var points = new List<(int X, int Y)>(count);
            int extraChance = parent.Stats.GetValueOrDefault(Stat.FrozenLegionPctChanceToSummonAdditionalStatue)
                + stats.GetValueOrDefault(Stat.FrozenLegionPctChanceToSummonAdditionalStatue);
            bool additional = extraChance > 0 && RandomRolls.Chance(extraChance, 100, AreaRng(player));
            int slots = count + (additional ? 1 : 0);
            for (int index = 0; index < slots; index++)
            {
                double angle = first + index * Math.Tau / slots;
                var at = (origin.X + (int)Math.Round(distance * Math.Cos(angle)),
                    origin.Y + (int)Math.Round(distance * Math.Sin(angle)));
                if (player.Layout.WalkableFor(at.Item1, at.Item2, 0) && !player.Barrier.Contains(at)
                    && MeleeArcSight(player, origin, at) && !points.Contains(at))
                    points.Add(at);
            }

            int spent = Math.Min(points.Count, count);
            if (!ticket.CooldownReset && !state.CompleteReservedUse(now, ticket.UseVersion, spent)) return;
            player.SkillReadyAt[CooldownKey(player, gem.Effect, gem.Id)] = now + state.RemainingSeconds;
            session.Enqueue(() => SendNativeCooldownRefresh(player));
            if (points.Count == 0) return;

            var equipment = Looks(player, ItemsOf(player)).Where(item => item.Slot is 0 or 1).ToArray();
            var hand = AttackHandFor(player, sweep.Id, sequence);
            var weapon = HandWeapon(player, hand);
            int? offCritical = hand == AttackHand.Off ? HandWeaponCritical(player, hand) : null;
            long sweepIncrease = (long)source.Stats.GetValueOrDefault(Stat.FrozenSweepDamagePlusPct)
                + stats.GetValueOrDefault(Stat.FrozenSweepDamagePlusPct);
            if (sweepIncrease != 0)
                source = source with { Stats = new Dictionary<uint, int>(source.Stats)
                    { [Stat.DamagePlusPct] = ActorScale.Saturate(sweepIncrease + source.Stats.GetValueOrDefault(Stat.DamagePlusPct)) } };
            double sweepMore = Math.Max(0, 1 + (source.Stats.GetValueOrDefault(Stat.FrozenSweepDamagePlusPctFinal)
                + stats.GetValueOrDefault(Stat.FrozenSweepDamagePlusPctFinal)) / 100d);
            var attack = CaptureAreaAttack(player, sweep.Id, source, hand, weapon, offCritical, sweepMore, sweepMore, "statue");
            float speed = Math.Max(.01f, (float)(SweepClipSeconds / Math.Max(SweepTickSeconds, SupportedAttackSeconds(player, sweep.Id, hand))));
            var sweepContact = (sweep.Id, sweep.Effect, "Frozen Sweep", sweep.Level, sweep.Quality, sweep.Gem);
            foreach (var point in points)
                PlaceFrozenStatue(session, point, origin, sweepContact, actorSkill,
                    equipment, attack, sequence, radius, speed);
        }
    }

    static void PlaceFrozenStatue(ProjectileSession session, (int X, int Y) at, (int X, int Y) origin,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) sweep, GrantedSkill actorSkill,
        IReadOnlyList<VisibleItem> equipment, RetainedAreaAttack attack, ushort sequence, int radius, float speed)
    {
        var player = session.Player;
        uint id = player.TakeObjectId();
        attack = attack with { ProxyActor = (id, at) };
        var inward = GroundSlamWave.Direction(at, origin);
        float rotation = (float)Math.Atan2(inward.X, -inward.Y);
        var step = Along(SweepStep);
        var centre = Along(SweepCentreOffset);
        int epoch = player.ProjectileEpoch;
        bool Current() => session.Active && player.ProjectileActive(epoch) && player.PlayerLife > 0;
        bool created = false;
        player.TrackProjectileObject(id);
        session.Create(id, () =>
        {
            if (!Current()) return null;
            created = true;
            return InstanceEntities.CreateObject(id, FrozenStatueType,
                FrozenLegionStatueObject.Blob(at.X, at.Y, rotation, actorSkill, equipment, [(Stat.HideMiniLifeBar, 1)]));
        }, wake: true, log: $"Frozen Legion statue at ({at.X},{at.Y})");
        // Give the client one birth and wake cycle to register the statue's own granted sweep.
        long windup = session.Runtime.Now + ProjectileRuntime.Time(.15);
        session.ScheduleContact(windup, Current, () => session.World(id,
            InstanceEntities.ActorCurrentAction(id, FrozenLegionStatueObject.ActorComponentIndex,
                FrozenLegionStatueObject.Action(sweep.Id, sequence, step.X, step.Y, sweeping: false)),
            "Frozen Legion statue windup"));
        long release = windup + ProjectileRuntime.Time(SweepWindupSeconds);
        session.ScheduleContact(release, Current, Release);
        session.Runtime.Schedule(release + ProjectileRuntime.Time(SweepClipSeconds / speed), () =>
        {
            if (player.UntrackProjectileObject(id) && created)
                session.WorldRecords(id, () => [InstanceProtocol.RemoveObject(id)], log: "Frozen Legion statue retirement", remove: true);
        });

        (int X, int Y) Along(double length)
        {
            var end = (at.X + (int)Math.Round(inward.X * length), at.Y + (int)Math.Round(inward.Y * length));
            var walkable = at;
            foreach (var cell in ClientPathfinder.Line(at, end))
            {
                if (!player.Layout.WalkableFor(cell.X, cell.Y, 0) || player.Barrier.Contains(cell)) break;
                walkable = cell;
            }
            return walkable;
        }

        void Release()
        {
            session.World(id, InstanceEntities.ActorCurrentAction(id, FrozenLegionStatueObject.ActorComponentIndex,
                FrozenLegionStatueObject.Action(sweep.Id, sequence, step.X, step.Y, sweeping: true, speed)),
                "Frozen Legion statue sweep");
            if (radius <= 0) return;
            double now = ProjectileWorldTime(player);
            long window = ProjectileRuntime.Time(FrozenLegionStatueObject.SweepContactWindow / speed);
            var critical = CaptureSkillCriticalRoll(player, sweep.Id);
            var hits = new List<(uint Id, long Offset)>();
            foreach (var (targetId, monster) in player.Area.MonstersNear(centre, radius))
            {
                if (monster is not { Hostile: true, Dead: false, Life: > 0, OwnerId: null, WorldHidden: false }) continue;
                var target = monster.Walk.Position(now);
                if (Dist(centre, target) > radius + Math.Max(0, LivingCollision.Size(monster))
                    || !MeleeArcSight(player, centre, target)) continue;
                // The Sweep starts at facing + 202.5 degrees (0x141c14370). Each candidate contacts once as it turns.
                double angle = Math.Atan2(target.X - centre.X, centre.Y - target.Y) - rotation - 9 * Math.PI / 8;
                angle -= Math.Floor(angle / Math.Tau) * Math.Tau;
                long offset = (long)(angle * window / Math.Tau) / ProjectileRuntime.TickQuanta;
                hits.Add((targetId, offset * ProjectileRuntime.TickQuanta));
            }
            foreach (var group in hits.OrderBy(hit => hit.Offset).ThenBy(hit => hit.Id).Take(100).GroupBy(hit => hit.Offset))
            {
                uint[] targets = group.Select(hit => hit.Id).ToArray();
                session.ScheduleContact(release + group.Key, Current, () =>
                {
                    var hit = AreaSkillHitStrike(player, sweep.Id, sweep, centre, centre, immediate: true, sequence,
                        actionToken: null, cooldownUse: null, melee: true, damageMore: 1, excludeMonster: 0, critical,
                        channelGeneration: null, targetId: 0, repeat: 0, radiusOverride: radius, once: null,
                        repeatDelay: 0, radiusMore: 1, sacrifice: null, swingAt: null, triggeredCurrent: Current,
                        discharge: null, strikeIndex: 0, firstContactAt: null, ruthlessMore: 1, priorTargets: null,
                        scaledBlastChaos: 0, singleStrikeTarget: null, onHit: null, scheduledSession: session,
                        cryExerted: false, prepaidRawExtra: 0, baseChaos: 0, hammer: null,
                        retainedAttack: attack, allowMeleeSplash: false, contactTargets: targets);
                    if (!hit.IsCompletedSuccessfully) session.Enqueue(() => hit);
                });
            }
        }
    }
}
