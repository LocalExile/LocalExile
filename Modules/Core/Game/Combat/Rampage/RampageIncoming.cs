using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    // Reuse the ordinary planner's cadence, action generations and repath budget for owned weapon targets.
    static bool PlanRampageWeaponTarget(Player player, MonsterAiCadence cadence, MonsterAiPlan plan,
        uint id, SpawnedMonster monster, double now, (int X, int Y) from, double playerDistance)
    {
        if (!CurrentRampageWork || !monster.Hostile) return false;
        // With no Rampage actor there is no weapon to find; iterate StandingRampageWorld.Actors directly (O(weapons)
        // instead of scanning all Area.Monsters per monster in PlanMonsterAi).
        if (player.StandingRampageWorld is not { Actors.Count: > 0 } rampage) return false;
        uint bestId = 0;
        SpawnedMonster? bestWeapon = null;
        double bestDist = double.PositiveInfinity;
        foreach (var (actorId, actor) in rampage.Actors)
        {
            if (actor.Weapon is null) continue;
            if (!player.Area.Monsters.TryGetValue(actorId, out var w) || w.OwnerId != player.Id || w.Dead || w.Life == 0) continue;
            double wd = Dist(from, w.Walk.Position(now));
            if (wd < bestDist) { bestDist = wd; bestId = actorId; bestWeapon = w; }
        }
        if (bestWeapon is null) return false;
        var owned = (Key: bestId, Value: bestWeapon);
        var target = owned.Value.Walk.Position(now);
        double distance = Dist(from, target);
        if (distance >= playerDistance) return false;
        // Only a hitting attack goes into a weapon: a kit spell that summons or only places something (an essence's
        // Locus/Corsair summon, cast instantly) has no damage and a near-zero action time, so swinging it every AI step
        // read as a monster attacking the air eight times a second while standing still (live 2026-11-23).
        if (monster.Attack is { Damage.IsZero: false } attack && distance <= attack.Range)
        {
            // Stand, then act (PlanMonsterAi): an in-place action waits for the walk's end.
            if (StandsToAct(attack) && monster.Walk.Moving(now)) return true;
            monster.BusyUntil = now + attack.Seconds;
            monster.LastTarget = null;
            monster.Walk.Order(from.X, from.Y, now);
            monster.SwingGeneration = ++monster.ActionGeneration;
            cadence.BeginSwing(monster, monster.ActionGeneration);
            plan.WeaponSwings.Add((id, monster, monster.ActionGeneration, owned.Key, owned.Value));
        }
        else if (cadence.TryChase(monster, target, now, retryAfterArrival: now >= monster.Walk.ArrivesAt))
        {
            monster.LastTarget = target;
            monster.SwingGeneration = null;
            plan.Moves.Add((id, monster, NextPlannedAction(monster), PlaceMove(plan, monster, target, now), target, "chases Rampage weapon"));
        }
        return true;
    }

    static async Task RunPlannedRampageWeaponSwing(Player player, MonsterAiCadence cadence, uint id, SpawnedMonster attacker, int action,
        uint targetId, SpawnedMonster weapon)
    {
        try { await MonsterSwingAtRampageWeapon(player, id, attacker, action, targetId, weapon); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
        finally { cadence.CompleteSwing(attacker, action); }
    }

    static async Task MonsterSwingAtRampageWeapon(Player player, uint id, SpawnedMonster attacker, int action, uint targetId, SpawnedMonster weapon)
    {
        if (attacker.Attack is not { } attack) return;
        bool TargetCurrent() => CurrentRampageWork && player.OwnedCombatState is RampageWorld world
            && world.Actors.ContainsKey(targetId) && !weapon.Dead && weapon.Life > 0 && weapon.OwnerId == player.Id
            && ReferenceEquals(player.Area.Monsters.GetValueOrDefault(targetId), weapon);
        double startedAt = 0;
        if (!await SendMonsterAction(player, id, attacker, action, () =>
        {
            if (!TargetCurrent()) return null;
            startedAt = AreaTime(player);
            var from = attacker.Walk.Position(startedAt); var to = weapon.Walk.Position(startedAt);
            attacker.BusyUntil = startedAt + attack.Seconds;
            return attack.InPlace ? InstanceProtocol.ActionStarted(id, from.X, from.Y, to.X, to.Y,
                    attack.SkillId, player.NextActionSequence(), player.MonsterOrderFlags, targetObjectId: targetId)
                : InstanceProtocol.ActionStartedNow(id, from.X, from.Y, to.X, to.Y,
                    attack.SkillId, player.NextActionSequence(), player.MonsterOrderFlags, targetId);
        }, "Monster attacks Rampage weapon")) return;
        if (!await WaitMonsterActionTime(player, id, attacker, action, startedAt + attack.Seconds / 2)) return;
        await SendMonsterAction(player, id, attacker, action, () =>
        {
            double now = Math.Max(ProjectileWorldTime(player), AreaTime(player));
            if (!TargetCurrent() || Dist(attacker.Walk.Position(now), weapon.Walk.Position(now)) > attack.Range + 2)
                return null;
            int accuracy = attacker.Mods.Effects.Accuracy(Monsters.BaseAccuracy(attacker.Level) ?? 0);
            var sources = player.FlaskGrounds.Blinded(id) || player.BlindAura.Blinded(id) || PhysicalAegisBlinds(player, id)
                || player.FlaskGrounds.Blinded(targetId) || player.BlindAura.Blinded(targetId) || PhysicalAegisBlinds(player, targetId)
                ? StatSourceValues(player) : null;
            accuracy = MonsterBlindRating(player, id, attacker, accuracy, now, sources);
            if (!RampageAttackHits(player, targetId, weapon, accuracy, now, AreaRng(player), sources)) return null;
            var packet = MonsterModHit(attacker, attack.Damage.Roll(AreaRng(player)), weapon.Life,
                weapon.MaxLife, AreaRng(player), out bool critical, now: now).Scale(SappedDamageFactor(attacker, now));
            var (damage, _) = MonsterDamage(weapon, packet, critical, true, default);
            weapon.Life = (uint)Math.Max(0, (long)weapon.Life - damage);
            if (weapon.Life == 0) weapon.Dead = true;
            return [];
        }, "Monster hit on Rampage weapon", recordId: targetId);
    }
}
