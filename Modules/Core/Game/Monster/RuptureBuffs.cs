using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    // BuffDefinitions rupture: four independent instances, UIStackMode 3; no IsRecovery values or extra word.
    public const ushort RuptureBuffRow = 1722;
    static readonly uint[] RuptureClockStats = [Stat.BuffTimePassedPlusPct, Stat.BuffTimePassedPlusPctOtherThanTemporalChains, Stat.DebuffTimePassedPlusPct];
    /// <summary>A monster's Rupture buffs (four independent instances, each its own: its tag) and the clock stats the native
    /// bleed timer reads; a rate change updates the timers without restarting them.</summary>
    public static List<byte[]> PresentRupturesLocked(Player player, uint id, SpawnedMonster monster, double now)
    {
        var set = monster.Ruptures;
        if (monster.Dead || monster.Life == 0) set.Clear();
        set.Stacks(now);
        if (set.Active.Count == 0 && !player.Area.Buffs.HasRow(id, RuptureBuffRow)) return [];
        // Curse presentation can belong to another player: the target's clock stats go with the Rupture.
        SetStats(player, id, RuptureClockStats.Select(name => (name, monster.Effects[name])));
        // This public stat drives the native bleed timer. It is not an inline value on rupture's 0x1c1.
        SetStats(player, id, [(Stat.RuptureDebuffBleedingTimePassedPlusPctFinal, 25 * set.Active.Count)]);
        player.Area.Buffs.Replace(id, b => b.Row == RuptureBuffRow, set.Active.Select(r => new Buff(RuptureBuffRow, player.Id,
            U68: Buff.Seconds((r.Until - now) * monster.RecipientDebuffRate), Tag: unchecked((ushort)r.Id))));
        return [];
    }

}
