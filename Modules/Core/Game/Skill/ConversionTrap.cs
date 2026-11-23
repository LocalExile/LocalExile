using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const ushort ConversionTrapBuffRow = 165; // trap_convert_buff

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
        ResetConversionTarget(player, target.Key, monster, now);
        // The native buff takes damage, taunt chance and original team. The area owns its instance for all observers.
        player.Area.Buffs.Set(target.Key, new(ConversionTrapBuffRow, Values: [0, 0, 1],
            U68: Buff.Seconds(placed.EffectDurationSeconds)));
        records.World(target.Key, InstanceProtocol.ActionEnded(target.Key), "Conversion Trap changes allegiance");
    }

    static void EndConversionTrap(Player player, uint id, SpawnedMonster monster, double now,
        List<(byte[] Packet, string Log)> records)
    {
        monster.TemporaryAllyUntil = 0;
        ResetConversionTarget(player, id, monster, now);
        player.Area.Buffs.Clear(id, ConversionTrapBuffRow);
        if (!monster.Dead && monster.Life > 0)
            records.Add((InstanceProtocol.ActionEnded(id), "Conversion Trap allegiance ends"));
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
        player.Minions.Attacks.Remove(id);
    }
}
