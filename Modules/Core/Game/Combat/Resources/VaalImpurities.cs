using GameServer.Core.World;

public static partial class Game
{
    const ushort ImpurityFireEnemyRow = 1124, ImpurityIceEnemyRow = 1125, ImpurityLightningEnemyRow = 1126;

    static ushort ImpurityEnemyRow(string effect) => effect switch
    {
        "VaalImpurityOfFire" => ImpurityFireEnemyRow,
        "VaalImpurityOfIce" => ImpurityIceEnemyRow,
        "VaalImpurityOfLightning" => ImpurityLightningEnemyRow,
        _ => 0,
    };

    static string? ImpurityMark(ushort row) => row switch
    {
        ImpurityFireEnemyRow => "VaalImpurityOfFire",
        ImpurityIceEnemyRow => "VaalImpurityOfIce",
        ImpurityLightningEnemyRow => "VaalImpurityOfLightning",
        _ => null,
    };

    static Dictionary<ushort, double>? AddImpurityEnemyAuras(Player player, double now, List<PassiveBuffAuras.Aura> auras)
    {
        if (player.PlayerLife == 0 || InTown(player) || !TemporaryAuraStates.TryGetValue(player, out var state)) return null;
        Dictionary<ushort, double>? deadlines = null;
        foreach (var (skill, active) in state.Active)
        {
            ushort row = ImpurityEnemyRow(active.Aura.EffectId);
            if (row == 0 || active.Until <= now || !player.Auras.TryGetValue(skill, out var aura) || aura.Buff != active.Aura.Buff)
                continue;
            int radius = AuraRadius.Of(aura, player.Pools.AuraAreaPct, SkillsFor(player), Stats);
            auras.Add(new(0, aura.EffectId, row, radius, [3], []));
            (deadlines ??= [])[row] = active.Until;
        }
        return deadlines;
    }

    // Only hits consult the enemy aura. Its per-caster mark also ends at the deadline even before the next presentation.
    static HitResistances.Mods ImpurityHitMods(SpawnedMonster monster, double now, HitResistances.Mods mods)
    {
        foreach (var ((source, row), _) in monster.PlayerAuras)
        {
            if (ImpurityMark(row) is not { } mark || !monster.SourceMarked(source, mark, now)) continue;
            mods = row switch
            {
                ImpurityFireEnemyRow => mods with { IgnoreFire = true },
                ImpurityIceEnemyRow => mods with { IgnoreCold = true },
                ImpurityLightningEnemyRow => mods with { IgnoreLightning = true },
                _ => mods,
            };
        }
        return mods;
    }

    static DamagePacket MitigateMonsterHit(SpawnedMonster target, DamagePacket packet, double now,
        Penetration.PerType penetration = default, int cannotPenetrateOrIgnore = 0)
    {
        var mods = ImpurityHitMods(target, now, new(0, 0, 0, cannotPenetrateOrIgnore));
        return DamageModel.Mitigate(packet, type => HitResistances.Effective(type,
            MonsterHitResistance(target, type, penetration, now), default, mods));
    }
}
