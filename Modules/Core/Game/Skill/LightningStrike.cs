using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Protocol.Instance;

static partial class Game
{
    internal static bool IsLightningStrike(int effect) => Skills.EffectId(effect) == "LightningStrike";

    // One paid use has a single melee contact and a volley. Both start at the strike's contact keyframe;
    // the client already draws the bolts. A melee victim is excluded from this volley's contacts.
    internal static Task ScheduleLightningStrike(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) aim, uint targetId, int recoveryGeneration,
        CooldownUseTicket? ticket, SkillCriticalRoll criticalRoll, ushort sequence, double? swingAt, bool cryExerted = false)
    {
        var session = ProjectilesFor(remote, stream, ciphers, conn);
        session.Advance();
        double acceptedAt = swingAt ?? ProjectileWorldTime(conn);
        double duration = SupportedAttackSeconds(conn, skillId);
        double contact = GemAttackContactDelay(conn, gem.Effect, duration, out _, out _);
        int repeats = MeleeAttackRepeats(conn, skillId);
        bool Valid() => session.Active && conn.ActionInterruptionGeneration == recoveryGeneration
            && (ticket?.Current(conn) ?? PlayerStateAllowsAction(conn))
            && GemSkillInstanceDisabled(conn, gem.Effect, skillId) is null;
        for (int repeat = 0; repeat <= repeats; repeat++)
        {
            int index = repeat;
            session.Schedule(ProjectileRuntime.Time(acceptedAt + contact + repeat * duration / (repeats + 1)), Valid, () =>
                ReleaseLightningStrike(remote, stream, ciphers, conn, session, skillId, gem, from, aim, targetId,
                    criticalRoll, sequence, index, cryExerted));
        }
        return Task.CompletedTask;
    }

    internal static ProjectileUse ReleaseLightningStrike(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ProjectileSession session, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) aim, uint targetId, SkillCriticalRoll criticalRoll, ushort sequence, int repeat = 0,
        bool cryExerted = false)
    {
        double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
        uint primary = LightningStrikeTarget(conn, from, aim, targetId, now);
        bool hit = false;
        ProjectileActorKey? excluded = null;
        double more = MeleeRepeatDamageMore(conn, skillId, repeat);
        if (primary != 0 && conn.Monsters.TryGetValue(primary, out var monster))
        {
            excluded = session.ActorKey(primary, monster);
            var at = monster.Walk.Position(now);
            // Immediate means the scheduler already waited for contact. Selection is one strike, not the
            // generic area approximation, and its success is known before presentation can await network IO.
            var melee = AreaSkillHit(remote, stream, ciphers, conn, skillId, gem, from, at,
                immediate: true, sequence: sequence, criticalRoll: criticalRoll, targetId: primary,
                radiusOverride: WeaponFor(conn).Range + monster.StopRange, damageMore: more,
                singleStrikeTarget: primary, onHit: _ => hit = true, scheduledSession: session, cryExerted: cryExerted);
            if (!melee.IsCompleted) throw new InvalidOperationException("Scheduled Lightning Strike contact must not yield.");
            melee.GetAwaiter().GetResult();
        }
        var use = ClientDrawnVolley(conn, session, skillId, gem,
            CaptureGemProjectile(conn, session, skillId, gem, aim, criticalRoll, sequence, cryExerted: cryExerted), sequence, 0);
        var shot = (ProjectileOffense)use.Payload!;
        return use with
        {
            Payload = shot with
            {
                ExcludedTarget = excluded,
                // The native skill makes the bolts unavoidable when its melee contact hit.
                AccuracyProfile = hit && shot.AccuracyProfile is { } accuracy ? accuracy with { CannotBeEvaded = true } : shot.AccuracyProfile,
                Hit = shot.Hit.Scale(more),
            }
        };
    }

    internal static uint LightningStrikeTarget(ClientState conn, (int X, int Y) from, (int X, int Y) aim,
        uint targetId, double now)
    {
        var weapon = WeaponFor(conn);
        return conn.Monsters.Where(e => e.Value.Hostile && !e.Value.Dead && e.Value.Life > 0 && e.Value.OwnerId is null
                && (targetId == 0 || e.Key == targetId)
                && SwingInReach(conn, e.Value, from, e.Value.Walk.Position(now), weapon.Range + 2)
                && (targetId != 0 || Dist(e.Value.Walk.Position(now), aim) <= weapon.Range + e.Value.StopRange))
            .OrderBy(e => Dist(e.Value.Walk.Position(now), aim)).ThenBy(e => e.Key)
            .Select(e => e.Key).FirstOrDefault();
    }

    // Scheduled contacts commit rewards and queue charge gains before yielding to the outbox. A held socket must
    // not postpone gameplay, and the ordinary area-hit path must not recursively advance this runtime.
    static void CommitLightningStrikeMeleeHit(ProjectileSession session, uint id, SpawnedMonster monster,
        MonsterHit hit, int damage, bool critical, StunHit stun, ushort sequence)
    {
        var conn = session.Conn;
        double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
        if (hit.Life == 0) session.Packet(InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, 1)]));
        if (HitReaction.TryFlinch(monster, stun, now, Random.Shared))
        {
            double until = monster.FlinchUntil;
            session.Packet(() => TakeHitPacket(conn, id, monster, until, now));
        }
        var player = conn.Walk.Position(now);
        var target = monster.Walk.Position(now);
        session.Packet(InstanceProtocol.ActionHit(PlayerId, sequence, id, flags: (ushort)(0x4201 | (critical ? 0x20 : 0)),
            kind: 3, angle: HitAngle(target.X - player.X, target.Y - player.Y)));
        session.Packet(InstanceProtocol.LifeUpdate(id, hit.Life),
            $"Lightning Strike melee t={now:0.000000} target=0x{id:x} damage={damage} life={hit.Life}/{monster.MaxLife}");
        if (critical)
        {
            CommitProjectileCritFlasks(session, now, Random.Shared);
            session.Enqueue(() => MaybeCreateProfaneGround(session.remote, session.stream, session.ciphers, conn, target));
        }
        if (hit.Life == 0) CommitProjectileKill(session, id, monster, session.Runtime.Now, Random.Shared);
        if (monster.ThresholdReached) session.Enqueue(() => QuestThreshold(session.remote, session.stream, session.ciphers, conn, id, monster));
    }
}
