using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const ushort ConversionTrapBuffRow = 165; // trap_convert_buff in BuffDefinitions
    // IsRecovery reads three GrantedStats values: damage, taunt chance and the original team (hostile).
    static readonly IReadOnlyList<uint> ConversionTrapBuffValues = [0, 0, 1];

    static KeyValuePair<uint, SpawnedMonster> ConversionTrapTriggerLocked(ClientState conn,
        PlacedSkills.Placed placed, double now) =>
        conn.Monsters.Where(p => p.Value is { Hostile: true, Dead: false, Life: > 0, WorldHidden: false, Asleep: false }
                && Dist(p.Value.Walk.Position(now), placed.At) <= placed.TriggerRadius)
            .OrderBy(p => Dist(p.Value.Walk.Position(now), placed.At)).FirstOrDefault();

    static void TriggerConversionTrapLocked(ClientState conn, PlacedSkills.Placed placed,
        KeyValuePair<uint, SpawnedMonster> trigger, double now, WorldRecordBuilder bytes)
    {
        var at = trigger.Value?.Walk.Position(now) ?? placed.At;
        bytes.World(placed.Id, InstanceProtocol.ActionStarted(placed.Id, placed.At.X, placed.At.Y,
            at.X, at.Y, 0x4000, 0xffff, 0x0408));
        if (placed.EffectDurationSeconds <= 0
            || trigger.Value is not { Rarity: not MonsterRarity.Unique, Invulnerable: false } monster
            || monster.Effects["cannot_be_converted_or_dominated"] > 0) return;

        monster.ConversionUntil = now + placed.EffectDurationSeconds;
        ResetConversionTargetLocked(conn, trigger.Key, monster, now);
        bytes.World(trigger.Key, InstanceProtocol.ActionEnded(trigger.Key), "Conversion Trap changes allegiance");
    }

    static void ConversionTrapChangesLocked(ClientState conn, uint id, SpawnedMonster monster, double now,
        List<(byte[] Packet, string Log)> packets)
    {
        if (monster.ConversionUntil is not double until || now < until && !monster.Dead && monster.Life > 0) return;

        monster.ConversionUntil = null;
        ResetConversionTargetLocked(conn, id, monster, now);
        if (!monster.Dead && monster.Life > 0)
            packets.Add((InstanceProtocol.ActionEnded(id), "Conversion Trap allegiance ends"));
    }

    static (double Until, ushort Id)? PresentConversionTrapLocked(ClientState conn, uint id, SpawnedMonster monster,
        double now, WorldRecordBuilder packets, (double Until, ushort Id)? shown = null)
    {
        double? until = monster.ConversionUntil is double end && now < end && !monster.Dead && monster.Life > 0 ? end : null;
        if (shown?.Until == until) return shown;
        if (shown is { } previous) packets.Presentation(id, InstanceEntities.RemoveBuff(id, previous.Id));
        if (until is not double expiry) return null;

        ushort buff = conn.NextBuff++;
        packets.Presentation(id, InstanceEntities.AddBuff(id, buff, ConversionTrapBuffRow, values: ConversionTrapBuffValues,
            u68: BitConverter.SingleToUInt32Bits((float)(expiry - now))), "Conversion Trap buff");
        return (expiry, buff);
    }

    static void ResetConversionTargetLocked(ClientState conn, uint id, SpawnedMonster monster, double now)
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
        monster.Aggro = monster.ConversionUntil is not null;
        monster.BusyUntil = now;
        monster.Walk.StopAt(now);
        conn.Minions.Attacks.Remove(id);
    }
}
