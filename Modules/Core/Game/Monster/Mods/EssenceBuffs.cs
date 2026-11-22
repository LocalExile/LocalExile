using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using static Game;
using static Program;

/// <summary>
/// The buff essences — Anguish, Loathing, Zeal and Scorn have no daemon on their display mod; their behaviour is a buff on
/// the released rare itself. Each is an <b>alternating</b> buff: the client's BuffDefinitions row named
/// <c>essence_alternating_buffs_…_alternator</c> (750 Zeal, 753 Loathing, 756 Scorn) is the invisible alternator, and the two
/// rows beside it (<c>…_1</c> / <c>…_2</c>) are the halves it switches between, each with its own stats and its own
/// BuffVisuals. Only one half is active at a time:
/// <list type="bullet">
/// <item>Zeal: 751 attack/cast/movement speed +50% (visual 640) ↔ 752 avoid damage 50% (641);</item>
/// <item>Loathing: 754 active_skill_damage_+%_final +50 (642) ↔ 755 base_damage_taken_+% −25 (639);</item>
/// <item>Scorn: 757 base_self_critical_strike_multiplier_-% 75 (643) ↔ 758 always_crit (644).</item>
/// </list>
/// Applying both halves at once, or sending the alternator alone, showed no icon and gave the monster every bonus together.
/// The halves are Invisible rows with a BuffVisualsKey, so the icon is an AddBuff visual override — the same hand-over the
/// encased monster's frozen effect uses. The stats ride <see cref="SpawnedMonster.TemporaryStats"/> (the shrine monster-aura
/// pattern), so the ordinary monster stat-change pass propagates them and the walk speed. Anguish's own behaviour is its
/// meteor Firestorm (a monster-mod daemon) and its on-death swarm, not a buff.
/// </summary>
public static partial class MapEssences
{
    sealed record EssenceBuffHalf(ushort Row, ushort Visual, Dictionary<string, int> Stats);
    sealed record EssenceBuff(EssenceBuffHalf First, EssenceBuffHalf Second, double SwitchSeconds);

    // The alternation period is not in the tables (the alternator row carries no duration stat): 3 s is this replica's
    // choice, to be calibrated against retail.
    const double BuffSwitchSeconds = 3;

    static Dictionary<string, int> BuffStats(params (string Name, int Value)[] pairs)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, value) in pairs) d[name] = value;
        return d;
    }

    static readonly Dictionary<string, EssenceBuff> EssenceBuffs = new(StringComparer.Ordinal)
    {
        ["Zeal"] = new(
            new(751, 640, BuffStats(("attack_speed_+%", 50), ("base_cast_speed_+%", 50), ("base_movement_velocity_+%", 50))),
            new(752, 641, BuffStats(("avoid_damage_%", 50))), BuffSwitchSeconds),
        ["Loathing"] = new(
            new(754, 642, BuffStats(("active_skill_damage_+%_final", 50))),
            new(755, 639, BuffStats(("base_damage_taken_+%", -25))), BuffSwitchSeconds),
        ["Scorn"] = new(
            new(757, 643, BuffStats(("base_self_critical_strike_multiplier_-%", 75))),
            new(758, 644, BuffStats(("always_crit", 1))), BuffSwitchSeconds),
    };

    sealed class BuffApply { public ushort Instance; public int State; public double NextSwitch; public ushort SentRow; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SpawnedMonster, Dictionary<string, BuffApply>> BuffApplied = new();

    static string EssenceTypeName(int typeKey)
    {
        var types = ClientTables.TableOrEmpty("EssenceType");
        return typeKey >= 0 && typeKey < types.Count ? types[typeKey].TextOrNull("Id") ?? "" : "";
    }

    /// <summary>Called from the essence tick: alternates each buff essence's two halves on the released rare. The monster
    /// stat-change pass sends the client the changed stats and walk speed; the icon is re-sent only when the half changes.</summary>
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
                ushort send = 0; ushort remove = 0; ushort instance = 0; EssenceBuffHalf half;
                lock (applied)
                {
                    string key = buff.First.Row.ToString();
                    if (!applied.TryGetValue(key, out var state))
                        applied[key] = state = new BuffApply { State = 1, NextSwitch = now + buff.SwitchSeconds };
                    if (now >= state.NextSwitch)
                    {
                        state.State = state.State == 1 ? 2 : 1;
                        state.NextSwitch = now + buff.SwitchSeconds;
                    }
                    half = state.State == 2 ? buff.Second : buff.First;
                    monster.TemporaryStats[$"EssenceBuff/{buff.First.Row}"] = half.Stats;
                    if (state.Instance == 0) state.Instance = conn.NextBuff++;
                    instance = state.Instance;
                    if (state.SentRow != half.Row)
                    {
                        remove = state.SentRow == 0 ? (ushort)0 : state.Instance;
                        send = half.Row;
                        state.SentRow = half.Row;
                    }
                }
                if (remove != 0) conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(mono.Monster, remove), null));
                if (send != 0)
                    conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(mono.Monster, instance, send, visual: half.Visual), null));
            }
        }
    }
}