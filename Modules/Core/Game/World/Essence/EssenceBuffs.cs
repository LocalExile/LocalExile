using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using static Game;
using static Program;

/// <summary>
/// The buff essences — Anguish, Loathing, Zeal and Scorn have no daemon (their display mod's MonsterMetadata is empty);
/// their behaviour is a buff on the released rare itself. Zeal grants 50% increased attack, cast and movement speed and a
/// 50% chance to avoid damage; Scorn makes it always deal critical strikes and take 75% reduced damage from critical
/// strikes; Loathing alternates between 50% more damage dealt and 25% less damage taken. The stats ride the monster's
/// <see cref="SpawnedMonster.TemporaryStats"/> (the shrine monster-aura pattern), so the ordinary monster stat-change pass
/// propagates them to the client and the walk speed; the buff icon is the essence alternating-buff visual (BuffDefinitions
/// 750 speed/dodge = Zeal, 753 damage = Loathing, 756 crit = Scorn). Anguish's Fiery Rubble summon is a separate behaviour.
/// </summary>
public static partial class MapEssences
{
    sealed record EssenceBuff(ushort BuffRow, Dictionary<string, int> Stats1, Dictionary<string, int>? Stats2, double SwitchSeconds);

    static readonly Dictionary<string, EssenceBuff> EssenceBuffs = new(StringComparer.Ordinal)
    {
        ["Zeal"] = new(750, new(StringComparer.Ordinal)
        {
            ["attack_speed_+%"] = 50, ["base_cast_speed_+%"] = 50, ["base_movement_velocity_+%"] = 50, ["avoid_damage_%"] = 50,
        }, null, 0),
        ["Loathing"] = new(753, new(StringComparer.Ordinal) { ["damage_+%"] = 50 },
            new(StringComparer.Ordinal) { ["base_damage_taken_+%"] = -25 }, 3),
        ["Scorn"] = new(756, new(StringComparer.Ordinal)
        {
            ["always_crit"] = 1, ["extra_damage_taken_from_crit_+%"] = -75,
        }, null, 0),
    };

    sealed class BuffApply { public ushort Instance; public int State; public double NextSwitch; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SpawnedMonster, Dictionary<string, BuffApply>> BuffApplied = new();

    static string EssenceTypeName(int typeKey)
    {
        var types = ClientTables.TableOrEmpty("EssenceType");
        return typeKey >= 0 && typeKey < types.Count ? types[typeKey].TextOrNull("Id") ?? "" : "";
    }

    /// <summary>Called from the essence tick: applies the buff essences' stats to the released rare and alternates
    /// Loathing. The monster stat-change pass sends the client the changed stats and walk speed.</summary>
    static void TickBuffs(ClientState conn)
    {
        if (conn.Closed || conn.LeavingArea || !Areas.TryGetValue(conn.Instance, out var area) || area.Monoliths is null) return;
        double now = Now;
        foreach (var mono in area.Monoliths.Where(m => m.Stage >= 4 && m.Monster != 0).ToArray())
        {
            SpawnedMonster? monster;
            lock (conn.Monsters) conn.Monsters.TryGetValue(mono.Monster, out monster);
            if (monster is null || monster.Dead || monster.Life <= 0) continue;
            foreach (int row in mono.Essences)
            {
                if (MapEssenceCatalog.Find(row) is not { } essence || !EssenceBuffs.TryGetValue(EssenceTypeName(essence.Type), out var buff)) continue;
                var applied = BuffApplied.GetOrCreateValue(monster);
                lock (applied)
                {
                    if (!applied.TryGetValue(buff.BuffRow.ToString(), out var state))
                        applied[buff.BuffRow.ToString()] = state = new BuffApply { State = 1, NextSwitch = now + buff.SwitchSeconds };
                    // Loathing alternates its two stat sets; the others hold one.
                    if (buff.Stats2 is not null && buff.SwitchSeconds > 0 && now >= state.NextSwitch)
                    {
                        state.State = state.State == 1 ? 2 : 1;
                        state.NextSwitch = now + buff.SwitchSeconds;
                    }
                    var stats = state.State == 2 && buff.Stats2 is not null ? buff.Stats2 : buff.Stats1;
                    monster.TemporaryStats[$"EssenceBuff/{buff.BuffRow}"] = stats;
                    if (state.Instance == 0)
                    {
                        state.Instance = conn.NextBuff++;
                        var (remote, stream, ciphers) = conn.Wire;
                        conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(mono.Monster, state.Instance, buff.BuffRow), null));
                    }
                }
            }
        }
    }
}