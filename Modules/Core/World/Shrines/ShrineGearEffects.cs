using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Shrines;

/// <summary>
/// Permanent and conditional gear/passive shrine buffs (<c>Blunderbore</c>, <c>Soulcord</c>), scaled by
/// <c>shrine_buff_effect_on_self_+% </c> and reflected on both character stats and the client buff bar.
/// </summary>
public static class ShrineGearEffects
{
    static readonly (uint Stat, string ShrineId)[] Sources =
    [
        (global::Stat.HasUniqueMassiveShrineEffect, "MassiveLesser"),
        (global::Stat.HasUniqueBrutalShrineEffect, "BrutalLesser"),
        (global::Stat.SoulcordHaveMassiveShrineEffectIfAffectedByNoFlasks, "Massive"),
        (global::Stat.SoulcordHaveDiamondShrineEffectIfAffectedByNoFlasks, "Criticals"),
        (global::Stat.SoulcordHaveResonatingShrineEffectIfAffectedByNoFlasks, "Resonating"),
        (global::Stat.SoulcordHaveBrutalShrineEffectIfAffectedByNoFlasks, "Brutal"),
        (global::Stat.SoulcordHaveReplenishingShrineEffectIfAffectedByNoFlasks, "Regen"),
        (global::Stat.SoulcordHaveImpenetrableShrineEffectIfAffectedByNoFlasks, "Defence"),
        (global::Stat.SoulcordHaveResistanceShrineEffectIfAffectedByNoFlasks, "Resistance"),
        (global::Stat.SoulcordHaveShockingShrineEffectIfAffectedByNoFlasks, "Lightning"),
        (global::Stat.SoulcordHaveChillingShrineEffectIfAffectedByNoFlasks, "Cold"),
        (global::Stat.SoulcordHaveSkeletonShrineEffectIfAffectedByNoFlasks, "Skeletons"),
        (global::Stat.SoulcordHaveGloomShrineEffectIfAffectedByNoFlasks, "Gloom"),
        (global::Stat.SoulcordHaveAccelerationShrineEffectIfAffectedByNoFlasks, "Acceleration"),
        (global::Stat.SoulcordHaveEchoingShrineEffectIfAffectedByNoFlasks, "Multi"),
    ];

    static readonly ConditionalWeakTable<Player, Dictionary<ushort, string>> ActiveBuffs = new();

    static bool IsFlagStat(uint key) => key is
        global::Stat.HasShrineEffect or global::Stat.AlwaysCrit or global::Stat.BaseCannotBeDamaged
        or global::Stat.GlobalKnockback or global::Stat.ExtraGore or global::Stat.SkillRepeatCount;

    public static IReadOnlyList<ShrineCatalog.ShrineDefinition> ActiveDefinitions(IReadOnlyDictionary<uint, int> stats)
    {
        var seen = new HashSet<ushort>();
        var list = new List<ShrineCatalog.ShrineDefinition>();
        foreach (var (key, shrineId) in Sources)
        {
            if (stats.GetValueOrDefault(key) <= 0) continue;
            if (ShrineCatalog.Find(shrineId) is not { } def || !seen.Add(def.BuffRow)) continue;
            list.Add(def);
        }
        return list;
    }

    public static int EffectiveShrineEffectPct(Player player, Func<uint, int> stat, bool mapGranted = false)
    {
        int reservedLifePct = player.Pools.Life > 0
            ? (int)Math.Clamp(global::Game.LifeReservationOf(player).Required * 100L / player.Pools.Life, 0L, 100L)
            : 0;
        return stat(global::Stat.ShrineBuffEffectOnSelfPlusPct)
            + stat(global::Stat.ShrineBuffEffectOnSelfPlusPctPer5PctMaximumLifeReserved) * (reservedLifePct / 5)
            + (mapGranted ? global::Game.MapModsOf(player)?.Get(global::Stat.MapShrineGrantedPlayerBuffEffectPlusPct) ?? 0 : 0)
            + (global::Game.MapModsOf(player)?.Get(global::Stat.MapPlayerShrineBuffEffectOnSelfPlusPct) ?? 0);
    }

    public static Dictionary<uint, int> AddStats(Player player, Dictionary<uint, int> stats)
    {
        var defs = ActiveDefinitions(stats);
        if (defs.Count == 0) return stats;

        int Stat(uint key) => stats.GetValueOrDefault(key);
        int effectPercent = EffectiveShrineEffectPct(player, Stat);

        var next = new Dictionary<uint, int>(stats);
        foreach (var def in defs)
        {
            foreach (var (key, raw) in def.Stats)
            {
                if (IsFlagStat(key))
                {
                    if (raw != 0) next[key] = Math.Max(next.GetValueOrDefault(key), raw);
                }
                else
                {
                    int scaled = effectPercent == 0
                        ? raw
                        : (int)Math.Clamp((long)raw * Math.Max(0L, 100L + effectPercent) / 100, int.MinValue, int.MaxValue);
                    next[key] = (int)Math.Clamp((long)next.GetValueOrDefault(key) + scaled, int.MinValue, int.MaxValue);
                }
            }
        }
        return next;
    }

    public static Task Tick(Player player)
    {
        if (player.Closed || player.LeavingArea) return Task.CompletedTask;
        var playerStats = global::Game.CharacterPlayerStats(player);
        var desired = ActiveDefinitions(playerStats);
        var shown = ActiveBuffs.GetOrCreateValue(player);
        var desiredRows = desired.ToDictionary(d => d.BuffRow);

        foreach (ushort row in shown.Keys.Where(r => !desiredRows.ContainsKey(r)).ToArray())
        {
            shown.Remove(row);
            player.Area.Buffs.Clear(player.Id, row, player.Id);
        }
        foreach (var def in desired)
        {
            if (shown.ContainsKey(def.BuffRow)) continue;
            shown[def.BuffRow] = def.Id;
            player.Area.Buffs.Set(player.Id, new(def.BuffRow, player.Id));
        }
        return Task.CompletedTask;
    }

    public static void LeaveArea(Player player) => ActiveBuffs.Remove(player);
}
