using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    // BuffDefinitions rupture: four independent instances, UIStackMode 3; no IsRecovery values or extra word.
    public const ushort RuptureBuffRow = 1722;
    static readonly string[] RuptureClockStats = ["buff_time_passed_+%", "buff_time_passed_+%_other_than_temporal_chains", "debuff_time_passed_+%"];
    sealed class RupturePresentation
    {
        public readonly Dictionary<long, ushort> Buffs = [];
        public readonly Dictionary<string, int> ClockStats = [];
        public int ExpiryPercent;
    }
    // Optional buff payloads are not mirrored blindly: each observer owns its native instances.
    static readonly Store<ClientState, Dictionary<SpawnedMonster, RupturePresentation>> RupturePresentations = new();

    /// <summary>Reconciles the native Rupture instances under the monster lock, after settling elapsed bleeding.</summary>
    public static List<byte[]> PresentRupturesLocked(ClientState conn, uint id, SpawnedMonster monster, double now)
    {
        var set = monster.Ruptures;
        if (monster.Dead || monster.Life == 0) set.Clear();
        set.Stacks(now);
        var presentations = RupturePresentations.GetOrCreateValue(conn);
        if (!presentations.TryGetValue(monster, out var shown))
        {
            if (set.Active.Count == 0) return [];
            presentations[monster] = shown = new();
        }
        var packets = new List<byte[]>();
        // Curse presentation can belong to another player. Every recipient needs the target's clock stats before
        // adding Rupture, and later rate changes must update existing timers without restarting their progress.
        var clocks = new List<(uint, int)>();
        foreach (string name in RuptureClockStats)
        {
            int value = monster.Effects[name];
            if (shown.ClockStats.GetValueOrDefault(name) == value) continue;
            shown.ClockStats[name] = value;
            if (Stats.Key(name) is uint key) clocks.Add((key, value));
        }
        if (clocks.Count > 0) packets.Add(InstanceEntities.StatsUpdate(id, clocks));
        foreach (var (key, buff) in shown.Buffs.ToArray())
            if (!set.Active.Any(r => r.Id == key))
            {
                packets.Add(InstanceEntities.RemoveBuff(id, buff));
                shown.Buffs.Remove(key);
            }
        // This public stat drives the native bleed timer. It is not an inline value on rupture's 0x1c1.
        int expiry = 25 * set.Active.Count;
        if (shown.ExpiryPercent != expiry)
        {
            shown.ExpiryPercent = expiry;
            if (Stats.Key("rupture_debuff_bleeding_time_passed_+%_final") is uint key)
                packets.Add(InstanceEntities.StatsUpdate(id, [(key, expiry)]));
        }
        foreach (var rupture in set.Active)
            if (!shown.Buffs.ContainsKey(rupture.Id))
            {
                ushort buff = conn.NextBuff++;
                shown.Buffs.Add(rupture.Id, buff);
                packets.Add(RuptureBuff(id, buff, rupture.Until, monster.RecipientDebuffRate, now));
            }
        return packets;
    }

    static byte[] RuptureBuff(uint id, ushort buff, double until, double rate, double now) =>
        InstanceEntities.AddBuff(id, buff, RuptureBuffRow, PlayerId,
            u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, (until - now) * rate)));

}
