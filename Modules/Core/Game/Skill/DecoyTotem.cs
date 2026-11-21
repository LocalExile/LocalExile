using System.Net.Sockets;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    public static (ClientState Owner, TotemState.Totem Source, double Until)? DecoyTarget(SpawnedMonster monster, double now)
    {
        if (monster.DecoyTaunt is not { } taunt) return null;
        if (now < taunt.Until && !taunt.Owner.Closed && !taunt.Owner.LeavingArea && !taunt.Owner.RageRetired
            && taunt.Owner.PendingTransfer is null && taunt.Owner.PlayerLife > 0 && taunt.Source.Alive(AreaTime(taunt.Owner)))
            return taunt;
        monster.DecoyTaunt = null;
        return null;
    }

    static bool DecoyCanTaunt(SpawnedMonster monster, TotemState.Totem totem, double now) =>
        TotemTargetable(monster) && !monster.Scripted && !monster.EntranceGrace && monster.Effects["cannot_be_taunted"] == 0
        && Dist(monster.Walk.Position(now), totem.At) <= totem.DecoyRadius;

    static void DecoyTotemUseLocked(ClientState conn, TotemState.Totem totem, double now, WorldRecordBuilder bytes)
    {
        if (conn.Paused || conn.LockstepClient && !conn.WorldOutput.PublishedObject(totem.Id).IsReady) return;
        if (totem.TauntAt is double contact)
        {
            if (now < contact) return;
            totem.TauntAt = null;
            ApplyDecoyTauntLocked(conn, totem, now);
        }
        if (!conn.Monsters.Values.Any(m => DecoyCanTaunt(m, totem, now))
            || TotemCastSeconds(conn, totem.SkillId) is not double seconds) return;
        totem.TauntAt = now + seconds;
        bytes.World(totem.Id, InstanceProtocol.ActionStarted(totem.Id, totem.At.X, totem.At.Y, totem.At.X, totem.At.Y,
            totem.GrantedId, ++totem.Sequence, 0x0408));
    }

    public static void ApplyDecoyTauntLocked(ClientState conn, TotemState.Totem totem, double now)
    {
        if (!totem.Alive(now) || totem.DecoyRadius <= 0) return;
        foreach (var (id, monster) in conn.Monsters)
        {
            // Its orders go to the owner's client, which must have been shown the monster (MonsterTargetsLocked).
            if (conn.AsleepMonsters.Contains(id)
                || conn.Instance is not null && !ReferenceEquals(monster.Target, conn) && !conn.MonsterViews.ContainsKey(id)
                || !DecoyCanTaunt(monster, totem, now)
                || monster.TauntedUntil > now
                || DecoyTarget(monster, now) is { } previous && !ReferenceEquals(previous.Source, totem)) continue;
            monster.DecoyTaunt = (conn, totem, now + FlaskGrounds.TauntSeconds / monster.RecipientDebuffRate);
            monster.Aggro = true;
        }
    }

    static bool DecoyShotBlocked(ClientState conn, MonsterAttack attack, (int X, int Y) from, (int X, int Y) to) =>
        attack.Projectile is { Lobbed: false } && conn.Layout.Sight is { } sight
        && ClientPathfinder.LineBelow(from, to, sight, 1);

    static void PlanDecoyTarget(ClientState conn, MonsterAiCadence cadence, MonsterAiPlan plan, uint id,
        SpawnedMonster monster, TotemState.Totem totem, double now, (int X, int Y) from)
    {
        if (monster.Attack is not { Summon: null } attack) return;
        double distance = Dist(from, totem.At);
        double reach = attack.Range + totem.BodySize;
        if (distance <= reach && !DecoyShotBlocked(conn, attack, from, totem.At))
        {
            monster.LastTarget = null;
            monster.SwingGeneration = ++monster.ActionGeneration;
            monster.BusyUntil = now + attack.Seconds;
            BossKits.Consume(monster, attack, now);
            cadence.BeginSwing(monster, monster.ActionGeneration);
            plan.DecoySwings.Add((id, monster, monster.ActionGeneration, totem));
        }
        else if (monster.Variety is not ({ MovementSpeed: 0 } or { Immobile: true }) && !monster.Walk.MovementBlocked(now))
        {
            var aim = totem.At;
            if (distance > reach && attack.Projectile is null)
            {
                double fraction = Math.Max(1, reach - 1) / distance;
                aim = ((int)Math.Round(totem.At.X + (from.X - totem.At.X) * fraction),
                       (int)Math.Round(totem.At.Y + (from.Y - totem.At.Y) * fraction));
            }
            if (!LockstepReorderDue(conn, plan, monster, cadence, now)
                || !cadence.TryChase(monster, aim, now, retryAfterArrival: now >= monster.Walk.ArrivesAt)) return;
            monster.LastTarget = aim;
            monster.SwingGeneration = null;
            plan.Moves.Add((id, monster, NextPlannedAction(monster), PlaceMove(plan, monster, aim, now), aim, "chases Decoy Totem"));
        }
    }

    static async Task RunDecoySwing(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        MonsterAiCadence cadence, uint id, SpawnedMonster attacker, int action, TotemState.Totem totem)
    {
        try
        {
            if (attacker.Attack is not { } attack) return;
            double contactAt = 0;
            ushort sequence = 0;
            if (!await SendMonsterAction(remote, stream, ciphers, conn, id, attacker, action, () =>
            {
                double now = AreaTime(conn);
                // The ordinary swing's order: its start on a lockstep frame, the chilled attack time, the busy time and
                // a lockstep client's walk stop.
                if (!totem.Alive(now) || DecoyTarget(attacker, now)?.Source != totem
                    || MonsterSwingOrderLocked(conn, attacker, attack, action, true, attack.Seconds, now) is not { } order) return null;
                sequence = conn.LockstepClient ? order.Sequence : attacker.NextSequence();
                contactAt = order.StartedAt + order.Seconds * (attack.Release?.Fraction ?? .5);
                var from = order.From;
                // An in-place attack leaves a walk going under it, as the client does; a melee one stands to swing and
                // needs the 0x1ac to begin (MonsterSwing).
                if (attack.InPlace)
                    return InstanceProtocol.ActionStarted(id, from.X, from.Y, totem.At.X, totem.At.Y, attack.SkillId,
                        sequence, conn.MonsterOrderFlags, targetObjectId: totem.Id);
                attacker.Walk.Order(from.X, from.Y, order.StartedAt);
                return InstanceProtocol.ActionStartedNow(id, from.X, from.Y, totem.At.X, totem.At.Y, attack.SkillId,
                    sequence, 0, totem.Id);
            }, ServerTrace.Ai ? $"Monster 0x{id:x} attacks Decoy Totem 0x{totem.Id:x}" : null)) return;
            if (!await WaitMonsterActionTime(conn, id, attacker, action, contactAt)) return;
            if (attack.Projectile is { } shot)
            {
                LaunchMonsterProjectile(remote, stream, ciphers, conn, id, attacker, attack, shot, action, totem.At, sequence: sequence);
                return;
            }
            await SendMonsterAction(remote, stream, ciphers, conn, id, attacker, action, () =>
            {
                double now = AreaTime(conn);
                var from = attacker.Walk.Position(now);
                if (Dist(from, totem.At) > attack.Range + totem.BodySize + 2
                    || !DecoyTakeHitLocked(conn, totem, attacker, attack, now, 1)) return null;
                return InstanceProtocol.ActionHit(id, sequence, totem.Id, flags: MonsterHitFlags(blocked: false, critical: false), kind: 0x03,
                    code: HitCode(attack.Damage), angle: HitAngle(totem.At.X - from.X, totem.At.Y - from.Y));
            });
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
        finally { lock (conn.Monsters) cadence.CompleteSwing(attacker, action); }
    }

    public static bool DecoyTakeHitLocked(ClientState conn, TotemState.Totem totem, SpawnedMonster attacker,
        MonsterAttack attack, double now, double multiplier)
    {
        double clock = AreaTime(conn);
        if (!totem.Alive(clock)) return false;
        var random = conn.IncomingHitRandom;
        var rolled = MonsterModHit(attacker, attack.Damage.Roll(random), totem.Life, totem.MaxLife, random,
            out _, now: now, cannotCrit: VaalOversoul.CannotCrit(attacker, attack)).Scale(multiplier * SappedDamageFactor(attacker, now));
        // Totems/Totem.ot (the variety's base type) has -80% inherent damage taken. Its elemental and chaos resistances
        // are from MonsterTypes.
        var variety = Monsters.Find(totem.VarietyId);
        var resisted = variety is null ? rolled : rolled.Select((type, range) =>
            range.Scale(1 - DamageModel.Capped(Monsters.BaseResistance(variety, totem.Level, type) ?? 0) / 100.0));
        long damage = Math.Max(0, (long)Math.Round(resisted.AverageTotal * .2));
        totem.Life = (uint)Math.Max(0, (long)totem.Life - damage);
        if (totem.Life == 0)
        {
            totem.DiedAt = clock;
            TotemsDiedLocked(conn, clock, CharacterPlayerStats(conn));
        }
        return true;
    }
}
