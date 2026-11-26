using System.Net.Sockets;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    public static (Player Owner, TotemState.Totem Source, double Until)? DecoyTarget(SpawnedMonster monster, double now)
    {
        if (monster.DecoyTaunt is not { } taunt) return null;
        if (now < taunt.Until && !taunt.Owner.Closed && !taunt.Owner.LeavingArea && !taunt.Owner.RageRetired
            && taunt.Owner.PendingTransfer is null && taunt.Owner.PlayerLife > 0 && taunt.Source.Alive(AreaTime(taunt.Owner)))
            return taunt;
        monster.DecoyTaunt = null;
        return null;
    }

    static bool DecoyCanTaunt(SpawnedMonster monster, TotemState.Totem totem, double radius, double now) =>
        TotemTargetable(monster) && !monster.Scripted && !monster.EntranceGrace && monster.Effects[Stat.CannotBeTaunted] == 0
        && Dist(monster.Walk.Position(now), totem.At) <= radius;

    static void DecoyTotemUseLocked(Player player, TotemState.Totem totem, double now, WorldRecordBuilder bytes)
    {
        if (player.Paused || player.Connection.LockstepClient && !player.Connection.WorldOutput.PublishedObject(totem.Id).IsReady) return;
        if (totem.TauntAt is double contact)
        {
            if (now < contact) return;
            totem.TauntAt = null;
            ApplyDecoyTauntLocked(player, totem, now);
        }
        if (!player.Area.Monsters.Values.Any(m => DecoyCanTaunt(m, totem, totem.DecoyRadius, now))
            || TotemCastSeconds(player, totem.SkillId) is not double seconds) return;
        totem.TauntAt = now + seconds;
        bytes.World(totem.Id, InstanceProtocol.ActionStarted(totem.Id, totem.At.X, totem.At.Y, totem.At.X, totem.At.Y,
            totem.GrantedId, ++totem.Sequence, 0x0408));
    }

    public static void ApplyDecoyTauntLocked(Player player, TotemState.Totem totem, double now) =>
        TauntFromTotemLocked(player, totem, totem.DecoyRadius, FlaskGrounds.TauntSeconds, now);

    static void TauntFromTotemLocked(Player player, TotemState.Totem totem, double radius, double seconds, double now)
    {
        if (!totem.Alive(now) || radius <= 0 || seconds <= 0) return;
        foreach (var (id, monster) in player.Area.Monsters)
        {
            // Its orders go to the owner's client, which must have been shown the monster (MonsterTargetsLocked).
            if (MonsterAsleep(player, id)
                || !DecoyCanTaunt(monster, totem, radius, now)
                || monster.TauntedUntil > now
                || DecoyTarget(monster, now) is { } previous && !ReferenceEquals(previous.Source, totem)) continue;
            monster.DecoyTaunt = (player, totem, now + seconds / monster.RecipientDebuffRate);
            monster.Aggro = true;
        }
    }

    static bool DecoyShotBlocked(Player player, MonsterAttack attack, (int X, int Y) from, (int X, int Y) to) =>
        attack.Projectile is { Lobbed: false } && player.Layout.Sight is { } sight
        && ClientPathfinder.LineBelow(from, to, sight, 1);

    // Swings at the totem when it reaches it; else asks for a cell around it (PlanDecoyChasesLocked).
    static void PlanDecoyTarget(Player player, MonsterAiCadence cadence, MonsterAiPlan plan, uint id,
        SpawnedMonster monster, TotemState.Totem totem, double now, (int X, int Y) from,
        List<(MonsterSurround.Asker Asker, SpawnedMonster M, TotemState.Totem Totem)> chasers)
    {
        if (monster.Attack is not { Summon: null } attack) return;
        double distance = Dist(from, totem.At);
        double reach = attack.Range + totem.BodySize;
        bool blocked = DecoyShotBlocked(player, attack, from, totem.At);
        if (distance <= reach && !blocked)
        {
            monster.LastTarget = null;
            monster.SwingGeneration = ++monster.ActionGeneration;
            monster.BusyUntil = now + attack.Seconds;
            BossKits.Consume(monster, attack, now);
            cadence.BeginSwing(monster, monster.ActionGeneration);
            plan.DecoySwings.Add((id, monster, monster.ActionGeneration, totem));
        }
        else if (monster.Variety is not ({ MovementSpeed: 0 } or { Immobile: true }) && !monster.Walk.MovementBlocked(now))
            // A shooter whose line is blocked comes to the contact ring, as around the player.
            chasers.Add((new(id, from.X, from.Y, LivingCollision.Size(monster), monster.LastTarget, reach,
                !blocked && attack.Ranged), monster, totem));
    }

    // Each totem's chasers take distinct cells around it (MonsterSurround, as around the player), so a pack does not
    // queue behind its front monster for one cell.
    static void PlanDecoyChasesLocked(Player player, MonsterAiCadence cadence, MonsterAiPlan plan, double now,
        List<(MonsterSurround.Asker Asker, SpawnedMonster M, TotemState.Totem Totem)> chasers,
        IEnumerable<(uint Id, int X, int Y, int Size)> bodies, (int X, int Y) playerAt, Func<int, int, int, bool> walkable)
    {
        // The frame's bodies leave out this player, whom the player's surround has at its centre: here it is a body too.
        var standing = bodies.Select(b => (b.Id, b.X, b.Y, b.Size));
        if (player.PlayerLife > 0 && !player.IsPhasing) standing = standing.Append((player.Id, playerAt.X, playerAt.Y, LivingCollision.PlayerSize));
        foreach (var group in chasers.GroupBy(c => c.Totem))
        {
            var totem = group.Key;
            var asking = group.Select(c => c.Asker.Id).ToHashSet();
            var occupied = standing.Where(b => !asking.Contains(b.Id) && b.Id != totem.Id
                && Math.Max(Math.Abs(b.X - totem.At.X), Math.Abs(b.Y - totem.At.Y)) < 64).Select(b => (b.X, b.Y, b.Size))
                .Concat(totem.DecoyUnreachable.Select(c => (c.X, c.Y, 0)));
            var slots = MonsterSurround.Assign(group.Select(c => c.Asker), totem.At, occupied, walkable, totem.BodySize);
            foreach (var (asker, monster, _) in group)
            {
                // A full surround: aim at the totem and queue behind the others.
                var to = slots.TryGetValue(asker.Id, out var slot) ? slot : totem.At;
                if ((asker.X, asker.Y) == to) { monster.LastTarget = to; continue; }
                if (!LockstepReorderDue(player, plan, monster, cadence, now)
                    || !cadence.TryChase(monster, to, now, retryAfterArrival: now >= monster.Walk.ArrivesAt)) continue;
                // Standing where the last order to this cell found it: the search found no way in (live: a cell between
                // the player, the totem and two swinging monsters). The next pass gives it another.
                if (totem.DecoyOrders.TryGetValue(asker.Id, out var last) && last == (to, (asker.X, asker.Y))
                    && !monster.Walk.Moving(now))
                {
                    totem.DecoyUnreachable.Add(to);
                    continue;
                }
                totem.DecoyOrders[asker.Id] = (to, (asker.X, asker.Y));
                monster.LastTarget = to;
                monster.SwingGeneration = null;
                plan.Moves.Add((asker.Id, monster, NextPlannedAction(monster), PlaceMove(plan, monster, to, now), to,
                    "chases Decoy Totem"));
            }
        }
    }

    static async Task RunDecoySwing(Player player,
        MonsterAiCadence cadence, uint id, SpawnedMonster attacker, int action, TotemState.Totem totem)
    {
        try
        {
            if (attacker.Attack is not { } attack) return;
            double startedAt = 0, contact = 0;
            ushort sequence = 0;
            if (!await SendMonsterAction(player, id, attacker, action, () =>
            {
                double now = AreaTime(player);
                // The ordinary swing's order: its start on a lockstep frame, the chilled attack time, the busy time and
                // a lockstep client's walk stop.
                if (!totem.Alive(now) || DecoyTarget(attacker, now)?.Source != totem
                    || MonsterSwingOrderLocked(player, attacker, attack, action, true, attack.Seconds, now) is not { } order) return null;
                sequence = player.Connection.LockstepClient ? order.Sequence : attacker.NextSequence();
                // The clip, and so the release, the action's sequence picks (the Sand Spitter's attack1 or attack2).
                attack = MonsterAttackForSequence(player, attacker, attack, sequence);
                (startedAt, contact) = (order.StartedAt, MonsterLaunchDelay(attack, attack.Release?.Fraction ?? .5, order.Seconds));
                var from = order.From;
                // An in-place attack leaves a walk going under it, as the client does; a melee one stands to swing and
                // needs the 0x1ac to begin (MonsterSwing).
                if (attack.InPlace)
                    return InstanceProtocol.ActionStarted(id, from.X, from.Y, totem.At.X, totem.At.Y, attack.SkillId,
                        sequence, player.MonsterOrderFlags, targetObjectId: totem.Id);
                attacker.Walk.Order(from.X, from.Y, order.StartedAt);
                return InstanceProtocol.ActionStartedNow(id, from.X, from.Y, totem.At.X, totem.At.Y, attack.SkillId,
                    sequence, 0, totem.Id);
            }, ServerTrace.Ai ? $"Monster 0x{id:x} attacks Decoy Totem 0x{totem.Id:x}" : null)) return;
            if (player.Connection.LockstepClient && attack.Projectile is { Variation: > 0, Lobbed: false } native)
            {
                // As at the player (MonsterSwing): a native shot leaves at the end of the frame that crosses the contact.
                LaunchMonsterProjectile(player, id, attacker, attack, native, action, totem.At,
                    LockstepShotAt(startedAt, contact), sequence);
                return;
            }
            if (!await WaitMonsterActionTime(player, id, attacker, action, startedAt + contact)) return;
            if (attack.Projectile is { } shot)
            {
                LaunchMonsterProjectile(player, id, attacker, attack, shot, action, totem.At, sequence: sequence);
                return;
            }
            await SendMonsterAction(player, id, attacker, action, () =>
            {
                double now = AreaTime(player);
                var from = attacker.Walk.Position(now);
                if (Dist(from, totem.At) > attack.Range + totem.BodySize + 2
                    || !DecoyTakeHitLocked(player, totem, attacker, attack, now, 1)) return null;
                return InstanceProtocol.ActionHit(id, sequence, totem.Id, flags: MonsterHitFlags(blocked: false, critical: false), kind: 0x03,
                    code: HitCode(attack.Damage), angle: HitAngle(totem.At.X - from.X, totem.At.Y - from.Y));
            });
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
        finally { cadence.CompleteSwing(attacker, action); }
    }

    public static bool DecoyTakeHitLocked(Player player, TotemState.Totem totem, SpawnedMonster attacker,
        MonsterAttack attack, double now, double multiplier)
    {
        double clock = AreaTime(player);
        if (!totem.Alive(clock)) return false;
        var random = player.IncomingHitRandom;
        var rolled = MonsterModHit(attacker, attack.Damage.Roll(random), totem.Life, totem.MaxLife, random,
            out _, now: now, cannotCrit: VaalOversoul.CannotCrit(attacker, attack)).Scale(multiplier * SappedDamageFactor(attacker, now));
        // Totems/Totem.ot (the variety's base type) has -80% inherent damage taken. Its elemental and chaos resistances
        // are from MonsterTypes.
        var variety = Monsters.Find(totem.VarietyId);
        var resisted = variety is null ? rolled : rolled.Select((type, range) =>
            range.Scale(1 - DamageModel.Capped(Monsters.BaseResistance(variety, totem.Level, type) ?? 0) / 100.0));
        long damage = Math.Max(0, (long)Math.Round(resisted.AverageTotal * .2));
        uint before = totem.Life;
        totem.Life = (uint)Math.Max(0, before - damage);
        RecoupTotemHitLocked(player, (uint)Math.Min(uint.MaxValue, damage), clock);
        if (totem.Life == 0)
        {
            totem.DiedAt = clock;
            TotemsDiedLocked(player, clock, CharacterPlayerStats(player));
        }
        return true;
    }
}
