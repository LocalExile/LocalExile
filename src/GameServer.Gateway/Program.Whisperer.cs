using System.Collections.Concurrent;

partial class Program
{
    // After block/mitigation: Ward, physical ES absorption, other non-chaos ES, chaos bypass, then physical mana before Life.
    // Recoup snapshots the mitigated hit, including the portion taken by ES/Mana; costs and DoT never enter here.
    internal static int WhispererDamagePlayerHit(ClientState conn, int physical, int other, int chaos, double now,
        IReadOnlyDictionary<uint, int> stats)
    {
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        (physical, other, chaos) = AbsorbPlayerWard(conn, physical, other, chaos, now, stats);
        int physicalLife = conn.EnergyShield.Damage(physical, false, now);
        int rest = conn.EnergyShield.Damage(other, false, now) + conn.EnergyShield.Damage(chaos, true, now);
        uint mana = WhispererMana.FromMana(physicalLife, conn.PlayerMana, Get("physical_damage_%_taken_from_mana_before_life"));
        conn.PlayerMana -= mana;
        int hit = (int)Math.Min(int.MaxValue, (long)physical + other + chaos);
        conn.Whisperer.Hit(now, hit, Get("damage_taken_goes_to_mana_%"));
        return (int)Math.Min(int.MaxValue, physicalLife - (long)mana + rest);
    }

    static void WhispererManaSpentLocked(ClientState conn, uint cost)
    {
        if (conn.Closed || conn.LeavingArea || conn.RageRetired || conn.PlayerLife == 0) return;
        var stats = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        long gained = conn.Whisperer.Spent(cost, Get(WhispererMana.RagePer200));
        if (gained > 0)
            conn.Rage.Gain(gained, ProjectileWorldTime(conn), RageStats.Inputs(Get), Get("has_soul_gain_prevention") != 0);
    }

    sealed record WhispererTransfer(string Character, DateTime At, WhispererManaState.Carried State,
        (double Seconds, int Less)[] Blind);
    static readonly ConcurrentDictionary<uint, WhispererTransfer> CarriedWhisperer = new();
    internal static void CarryWhisperer(ClientState conn, uint ticket)
    {
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.RageRetired) return;
            if (conn.PlayerLife == 0) conn.Whisperer.Clear();
            foreach (var (old, entry) in CarriedWhisperer)
                if (DateTime.UtcNow - entry.At > TimeSpan.FromMinutes(5)) CarriedWhisperer.TryRemove(old, out _);
            CarriedWhisperer.TryAdd(ticket, new(CharacterName(conn), DateTime.UtcNow,
                conn.Whisperer.Carry(conn.Clock.Elapsed.TotalSeconds), conn.Blind.Carry(conn.Clock.Elapsed.TotalSeconds)));
        }
    }
    internal static void RestoreWhisperer(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (!CarriedWhisperer.TryGetValue(conn.Ticket, out var entry) || entry.Character != CharacterName(conn)
                || !CarriedWhisperer.TryRemove(new KeyValuePair<uint, WhispererTransfer>(conn.Ticket, entry))) return;
            if (conn.PlayerLife > 0 && DateTime.UtcNow - entry.At <= TimeSpan.FromMinutes(5))
            {
                conn.Whisperer.Restore(entry.State, conn.Clock.Elapsed.TotalSeconds);
                conn.Blind.Restore(entry.Blind, conn.Clock.Elapsed.TotalSeconds);
            }
        }
    }
}
