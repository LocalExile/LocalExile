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
        if (!CurrentRampageWork) return false;
        // With no Rampage actor there is no weapon to find, and the search below passes over every monster of the
        // instance for each monster that plans (half a second in ten with 1000 of them). Asked only of a world that
        // stands: the search's own RampageWorld would make one, and then only when the player owns a living monster.
        if (monster.Hostile && player.StandingRampageWorld is { Actors.Count: 0 }) return false;
        var owned = monster.Hostile ? player.Area.Monsters.Where(p => p.Value.OwnerId == player.Id && !p.Value.Dead
            && p.Value.Life > 0 && player.RampageWorld.Actors.TryGetValue(p.Key, out var actor) && actor.Weapon is not null)
            .OrderBy(p => Dist(from, p.Value.Walk.Position(now))).FirstOrDefault() : default;
        if (owned.Value is null) return false;
        var target = owned.Value.Walk.Position(now);
        double distance = Dist(from, target);
        if (distance >= playerDistance) return false;
        if (monster.Attack is { } attack && distance <= attack.Range)
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
                weapon.MaxLife, AreaRng(player), out bool critical, now: now);
            var (damage, _) = MonsterDamage(weapon, packet, critical, true, default);
            weapon.Life = (uint)Math.Max(0, (long)weapon.Life - damage);
            if (weapon.Life == 0) weapon.Dead = true;
            return [];
        }, "Monster hit on Rampage weapon");
    }
}
