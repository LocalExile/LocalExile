// Overheat Support (poedb): "Supported Skills consume all Fortification on use, dealing (6-8)% more Damage with
// Ailments per 3 Fortification consumed" (support_overheat_ailment_damage_+%_final_per_3_fortification_consumed).
// Fortification is consumed once per use, before its hit lands, and the more applies to the ailments of that use
// (including the projectiles a melee skill such as Molten Strike releases). It is stored per skill, so the next use
// without Fortification replaces it with no bonus.
public static partial class Game
{
    /// <summary>Consumes all Fortification for a supported skill's use and stores this use's more ailment damage: the
    /// support's per-3 value times the whole groups of three consumed (Fortification's 20-stack base maximum caps it at
    /// 36% at level 1 and 48% at level 3, the exceptional gem's top level). A skill without the support stores nothing.
    /// Under the Monsters lock.</summary>
    public static void OverheatConsumeLocked(ClientState conn, ushort skillId, Func<string, int> skill, double now)
    {
        int perThree = skill("support_overheat_ailment_damage_+%_final_per_3_fortification_consumed");
        if (perThree <= 0)
        {
            conn.SupportGems.OverheatMore.Remove(skillId);
            return;
        }
        int consumed = FortificationOf(conn).ConsumeAll(now);
        double more = Math.Max(0, 1 + perThree * (consumed / 3) / 100.0);
        if (more == 1) conn.SupportGems.OverheatMore.Remove(skillId);
        else conn.SupportGems.OverheatMore[skillId] = more;
    }

    /// <summary>This skill's last use's Overheat more ailment damage (1 when it consumed fewer than three stacks).</summary>
    public static double OverheatAilmentMore(ClientState conn, ushort skillId) =>
        conn.SupportGems.OverheatMore.GetValueOrDefault(skillId, 1);

    public static MonsterIgnites.Settings WithOverheat(ClientState conn, ushort skillId, MonsterIgnites.Settings settings)
    {
        double more = OverheatAilmentMore(conn, skillId);
        return more == 1 ? settings : settings with
        {
            Min = settings.Min * more, Max = settings.Max * more,
            CriticalMin = settings.CriticalMin * more, CriticalMax = settings.CriticalMax * more,
        };
    }

    public static MonsterPoisons.Settings WithOverheat(ClientState conn, ushort skillId, MonsterPoisons.Settings settings)
    {
        double more = OverheatAilmentMore(conn, skillId);
        return more == 1 ? settings : settings with
        {
            Min = settings.Min * more, Max = settings.Max * more,
            CriticalMin = settings.CriticalMin * more, CriticalMax = settings.CriticalMax * more,
        };
    }
}