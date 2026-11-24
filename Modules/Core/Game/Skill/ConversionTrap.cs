using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const ushort ConversionTrapBuffRow = 165; // trap_convert_buff
    static readonly ConditionalWeakTable<GameArea, HashSet<uint>> ConvertedMonsters = new();

    static bool TemporaryAllyAttackCurrent(PlayerMinions.Attack attack, double at) =>
        attack.TemporaryAllyUntil is not double until || at < until && attack.Monster.TemporaryAllyUntil == until;

    static IEnumerable<Player> AreaPlayers(Player player)
    {
        yield return player;
        HashSet<Player>? seen = null;
        foreach (var peer in player.Frames.Players())
        {
            if (ReferenceEquals(peer, player)) continue;
            seen ??= new(ReferenceEqualityComparer.Instance) { player };
            if (seen.Add(peer)) yield return peer;
        }
        foreach (var peer in player.Instance?.PlayersExcept(player.Id) ?? [])
        {
            if (ReferenceEquals(peer, player)) continue;
            seen ??= new(ReferenceEqualityComparer.Instance) { player };
            if (seen.Add(peer)) yield return peer;
        }
    }

    // One living nearby player drives a converted monster per frame so peers do not race in PlanMonsterAi.
    static bool TemporaryAllyDriverLocked(Player player, uint id, SpawnedMonster m, (int X, int Y) at, double now)
    {
        Player? winner = null;
        double bestDistance = double.MaxValue;
        foreach (var peer in AreaPlayers(player))
        {
            if (peer.Minions.Attacks.TryGetValue(id, out var pending)
                && ReferenceEquals(pending.Monster, m)
                && TemporaryAllyAttackCurrent(pending, now)
                && (peer.PlayerLife > 0 || pending.Started))
                return ReferenceEquals(peer, player);
            if (peer.Closed || peer.LeavingArea || !peer.AiEnabled || peer.Paused || peer.PlayerLife == 0
                || !ReferenceEquals(peer.Instance, player.Instance)) continue;
            if (!ReferenceEquals(peer, player) && !peer.Connection.PlayerSent) continue;
            double distance = Dist(at, peer.Walk.Position(now));
            // The peer's wake zone is the square: a Euclid distance must reach its corner to cover it.
            if (!ReferenceEquals(peer, player) && distance > WakeZoneCorner + 16) continue;
            if (winner is null || distance < bestDistance || distance == bestDistance && peer.Id < winner.Id)
                (winner, bestDistance) = (peer, distance);
        }
        return ReferenceEquals(winner, player);
    }

    static KeyValuePair<uint, SpawnedMonster> ConversionTrapTarget(Player player, PlacedSkills.Placed placed, double now)
    {
        KeyValuePair<uint, SpawnedMonster> closest = default;
        double distance = placed.TriggerRadius;
        foreach (var candidate in player.Area.MonstersNear(placed.At, placed.TriggerRadius))
        {
            if (candidate.Value is not { Hostile: true, Dead: false, Life: > 0, WorldHidden: false, Asleep: false }) continue;
            double next = Dist(candidate.Value.Walk.Position(now), placed.At);
            if (next > distance || next == distance && closest.Value is not null && candidate.Key >= closest.Key) continue;
            closest = candidate;
            distance = next;
        }
        return closest;
    }

    static void TriggerConversionTrap(Player player, PlacedSkills.Placed placed,
        KeyValuePair<uint, SpawnedMonster> target, double now, WorldRecordBuilder records)
    {
        var at = target.Value?.Walk.Position(now) ?? placed.At;
        records.World(placed.Id, InstanceProtocol.ActionStarted(placed.Id, placed.At.X, placed.At.Y,
            at.X, at.Y, 0x4000, 0xffff, 0x0408));
        if (placed.EffectDurationSeconds <= 0
            || target.Value is not { Rarity: not MonsterRarity.Unique, Invulnerable: false } monster
            || monster.Effects[Stat.CannotBeConvertedOrDominated] > 0) return;

        monster.TemporaryAllyUntil = now + placed.EffectDurationSeconds;
        ConvertedMonsters.GetOrCreateValue(player.Area).Add(target.Key);
        ResetConversionTarget(player, target.Key, monster, now);
        // The native buff takes damage, taunt chance and original team. The area owns its instance for all observers.
        player.Area.Buffs.Set(target.Key, new(ConversionTrapBuffRow, Values: [0, 0, 1],
            U68: Buff.Seconds(placed.EffectDurationSeconds)));
        records.World(target.Key, InstanceProtocol.SetTeam(target.Key, 0), "Conversion Trap sets player team");
        records.World(target.Key, InstanceProtocol.ActionEnded(target.Key), "Conversion Trap changes allegiance");
    }

    static void ExpireConversionTrapsLocked(Player player, double now, List<(byte[] Packet, string Log)> records)
    {
        if (!ConvertedMonsters.TryGetValue(player.Area, out var active) || active.Count == 0) return;
        foreach (uint id in active.ToArray())
        {
            if (!player.Area.Monsters.TryGetValue(id, out var monster))
            {
                active.Remove(id);
                player.Area.Buffs.Clear(id, ConversionTrapBuffRow);
                foreach (var peer in AreaPlayers(player)) peer.Minions.Attacks.Remove(id);
                continue;
            }
            if (monster.TemporaryAllyUntil <= 0) { active.Remove(id); continue; }
            if (now >= monster.TemporaryAllyUntil || monster.Dead || monster.Life == 0)
                EndConversionTrap(player, id, monster, now, records);
        }
    }

    static void EndConversionTrap(Player player, uint id, SpawnedMonster monster, double now,
        List<(byte[] Packet, string Log)> records)
    {
        monster.TemporaryAllyUntil = 0;
        if (ConvertedMonsters.TryGetValue(player.Area, out var active)) active.Remove(id);
        ResetConversionTarget(player, id, monster, now);
        player.Area.Buffs.Clear(id, ConversionTrapBuffRow);
        if (!monster.Dead && monster.Life > 0)
        {
            records.Add((InstanceProtocol.SetTeam(id, (ushort)(monster.OriginallyHostile ? 1 : 0)), "Conversion Trap restores team"));
            records.Add((InstanceProtocol.ActionEnded(id), "Conversion Trap allegiance ends"));
        }
    }

    static void ResetConversionTarget(Player player, uint id, SpawnedMonster monster, double now)
    {
        monster.ActionGeneration++;
        monster.Target = null;
        monster.LastTarget = null;
        monster.SwingGeneration = null;
        monster.ClientChase = null;
        monster.ClientSwingAt = null;
        monster.ClientActionWalkStop = null;
        monster.Leg = null;
        monster.DecoyTaunt = null;
        monster.Fleeing = false;
        monster.Aggro = monster.TemporarilyAllied;
        monster.BusyUntil = now;
        monster.Walk.StopAt(now);
        foreach (var peer in AreaPlayers(player))
            peer.Minions.Attacks.Remove(id);
    }
}
