using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const double FallingCrystalDelay = .95;
    const double TriggeredFissureLength = 80;
    const double TriggeredFissureSpeed = 150;

    static void TriggerSlamSupportLocked(Player player, ushort drivingSkill, SupportedGem parent,
        AttackHand hand, SkillCatalog.Unarmed weapon, int? offCritical, (int X, int Y) at, double now,
        uint chanceStat, string effectId)
    {
        if (parent.Stats.GetValueOrDefault(chanceStat) <= 0
            || parent.Supports.FirstOrDefault(s => SupportHas(s, chanceStat)) is not { } support
            || !SameTriggerSource(parent, SupportedSkill(player, drivingSkill))) return;
        var source = GemSkillEffects(player).FirstOrDefault(g => SkillsFor(player).EffectId(g.Effect) == effectId
            && CurrentPlayerSkillSourceLocked(player, g.Id) is { Active: true } child
            && child.Identity.Host == parent.Host && child.Identity.Socket == support.Gem.Socket);
        if (source.Name is null || GemSkillInstanceDisabled(player, source.Effect, source.Id, triggered: true) is not null
            || CooldownForSkill(player, source.Id) is not { } cooldown) return;
        var session = ProjectilesFor(player);
        bool Active() => session.Active && player.PlayerLife > 0 && !player.Closed && !player.LeavingArea;
        if (!Active()) return;
        int key = CooldownKey(player, source.Effect, source.Id);
        var state = CooldownStateLocked(player, key, cooldown, now);
        if (!state.Ready || !RandomRolls.Chance(parent.Stats.GetValueOrDefault(chanceStat), 100, AreaRng(player))
            || !state.TryUse(now, cooldown)) return;

        var childGem = SupportedSkill(player, source.Id);
        bool radial = effectId == "TriggeredSupportFissure";
        var hitSource = CaptureAreaAttack(player, source.Id, childGem, hand, weapon, offCritical, 1, 1,
            radial ? "support fissure" : "falling crystal");
        var critical = CaptureSkillCriticalRoll(player, source.Id);
        player.CooldownSources[source.Effect] = source.Id;
        player.SkillReadyAt[key] = now + state.RemainingSeconds;
        ushort sequence = NextLockstepSequence(player);
        if (!player.Connection.LockstepClient) sequence &= 0x7fff;
        session.World(player.Id, InstanceProtocol.InstantAction(player.Id, source.Id, at.X, at.Y,
            sequence: sequence, flags: player.Connection.LockstepClient ? (ushort)0x101 : (ushort)0, triggered: true),
            $"0x01ce {hitSource.Phase} at {at}");
        state.ConfirmNativeUse(state.UseVersion, source.Id, synchronizeProgress: true);

        var use = (source.Id, source.Effect, Name: radial ? "Fissure" : "Falling Crystal", childGem.Level, childGem.Quality, source.Gem);
        long start = Math.Max(session.Runtime.Now, ProjectileRuntime.Time(now));
        void Hit((int X, int Y) centre, int radius, List<uint> targets)
        {
            if (targets.Count == 0) return;
            var hit = AreaSkillHitStrike(player, source.Id, use, centre, centre, immediate: true, sequence,
                actionToken: null, cooldownUse: null, melee: true, damageMore: 1, excludeMonster: 0, critical,
                channelGeneration: null, targetId: 0, repeat: 0, radiusOverride: radius, once: null, repeatDelay: 0,
                radiusMore: 1, sacrifice: null, swingAt: null, triggeredCurrent: Active, discharge: null, strikeIndex: 0,
                firstContactAt: null, ruthlessMore: 1, priorTargets: null, scaledBlastChaos: 0, singleStrikeTarget: null,
                onHit: null, scheduledSession: session, cryExerted: false, prepaidRawExtra: 0, baseChaos: 0, hammer: null,
                retainedAttack: hitSource, allowMeleeSplash: false, contactTargets: targets);
            if (!hit.IsCompleted) throw new InvalidOperationException("A scheduled slam support hit must not yield.");
            hit.GetAwaiter().GetResult();
        }

        bool Target(SpawnedMonster monster) =>
            monster is { Hostile: true, Dead: false, Life: > 0, OwnerId: null, WorldHidden: false };
        bool ClearSight((int X, int Y) from, (int X, int Y) to) =>
            player.Layout.Sight is not { } sight || !ClientPathfinder.LineBelow(from, to, sight, 1);

        if (!radial)
        {
            int radius = SkillRadius(player, childGem.Stats, spell: false, effect: source.Effect);
            session.Runtime.Schedule(start + ProjectileRuntime.Time(FallingCrystalDelay), () =>
            {
                if (!Active()) return;
                double landing = ProjectileWorldTime(player);
                List<uint> targets = [];
                foreach (var (id, monster) in player.Area.MonstersNear(at, radius))
                {
                    if (!Target(monster)) continue;
                    var position = monster.Walk.Position(landing);
                    if (Dist(at, position) <= radius + Math.Max(0, LivingCollision.Size(monster)) && ClearSight(at, position))
                        targets.Add(id);
                }
                Hit(at, radius, targets);
            });
            return;
        }

        int body = SkillRadius(player, 10, childGem.Stats, spell: false, effect: source.Effect);
        int count = Math.Max(1, childGem.Stats.GetValueOrDefault(Stat.VolcanicFissureNumberOfSimultaneousCrackCount));
        // The triggered parent is detached as action 1, and starts the radial cracks from that seed.
        var random = new AnimationVariantSelector.NativeRandom([sequence, 1, player.Id, 0, 0, (uint)count, 0, 0]);
        float initial = random.Range(0, 6.2831855f);
        var origin = ProjectileSession.Vector(at);
        var paths = new ProjectileVector[count];
        double furthest = 0;
        for (int i = 0; i < paths.Length; i++)
        {
            double angle = initial + Math.Tau * i / paths.Length;
            var aim = (at.X + (int)Math.Round(Math.Sin(angle) * TriggeredFissureLength),
                at.Y - (int)Math.Round(Math.Cos(angle) * TriggeredFissureLength));
            var end = at;
            foreach (var cell in ClientPathfinder.Line(at, aim).Skip(1))
            {
                if (!player.Layout.WalkableFor(cell.X, cell.Y, 0) || player.Barrier.Contains(cell)) break;
                end = cell;
            }
            paths[i] = ProjectileSession.Vector(end) - origin;
            furthest = Math.Max(furthest, paths[i].Length);
        }
        var contacted = new HashSet<uint>();
        double covered = 0;
        double speed = TriggeredFissureSpeed * Math.Max(.01, 1 + childGem.Stats.GetValueOrDefault(Stat.VolcanicFissureSpeedPlusPct) / 100d);
        void Advance()
        {
            if (!Active()) return;
            double elapsed = (session.Runtime.Now - start) / (double)ProjectileRuntime.QuantaPerSecond;
            double advance = Math.Min(furthest, speed * elapsed);
            double time = ProjectileWorldTime(player);
            List<uint> targets = [];
            foreach (var path in paths)
            {
                double length = path.Length;
                if (covered >= length) continue;
                var direction = path.Unit;
                var from = origin + direction * covered;
                double step = Math.Min(advance, length) - covered;
                var mid = from + direction * (step / 2);
                var tile = ((int)Math.Round(mid.X), (int)Math.Round(mid.Y));
                foreach (var (id, monster) in player.Area.MonstersNear((mid.X, mid.Y), body + step / 2))
                {
                    if (!Target(monster) || contacted.Contains(id)) continue;
                    var position = monster.Walk.Position(time);
                    var relative = ProjectileSession.Vector(position) - from;
                    double along = Math.Clamp(relative.Dot(direction), 0, step);
                    if ((relative - direction * along).Length <= body + Math.Max(0, LivingCollision.Size(monster))
                        && ClearSight(tile, position) && contacted.Add(id))
                        targets.Add(id);
                }
            }
            Hit(at, body, targets);
            covered = advance;
            if (covered < furthest)
                session.Runtime.Schedule(session.Runtime.Now + ProjectileRuntime.TickQuanta, Advance);
        }
        if (furthest > 0) session.Runtime.Schedule(start + ProjectileRuntime.TickQuanta, Advance);
    }
}
