using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    static bool BlindImmune(ClientState conn)
    {
        if (Stats.Key("cannot_be_blinded") is not uint key) return false;
        return conn.PlayerStats.TryGetValue(key, out int value) ? value != 0
            : CharacterStatSources(conn).Stats.GetValueOrDefault(key) != 0;
    }
    internal static int PlayerAccuracy(ClientState conn) => PlayerBlindAccuracy(conn, conn.Pools.Accuracy);
    static int PlayerBlindAccuracy(ClientState conn, int rating)
    { lock (conn.Monsters) return TimedBlind.Apply(rating, PlayerBlindValue(conn).Less); }
    internal static int PlayerEvasion(ClientState conn)
    { lock (conn.Monsters) return TimedBlind.Apply(conn.Pools.Evasion, PlayerBlindValue(conn).Less); }
    static (int Less, double Until) PlayerBlindValue(ClientState conn)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        var value = conn.Blind.Read(now, conn.PlayerLife == 0);
        return value.Less > 0 && BlindImmune(conn) ? conn.Blind.Read(now, true) : value;
    }
    internal static bool InflictPlayerBlind(ClientState conn, double seconds, int effect = 0)
    {
        lock (conn.Monsters)
            return conn.PlayerLife > 0 && !conn.Closed && !conn.LeavingArea && !conn.Paused
                && conn.Blind.Inflict(conn.Clock.Elapsed.TotalSeconds, seconds, effect, BlindImmune(conn));
    }
    static int SmokeBlindLess(ClientState conn, uint id)
    {
        if (conn.FlaskGrounds.Accuracy(id, 100) == 100) return 0;
        var stats = CharacterPlayerStats(conn);
        int effect = Stats.Key("blind_effect_+%") is uint key ? stats.GetValueOrDefault(key) : 0;
        return (int)Math.Clamp(20L * Math.Max(0L, 100L + effect) / 100, 0, 100);
    }
    static int MonsterBlindRating(ClientState conn, uint id, SpawnedMonster target, int rating, double now)
    {
        if (target.Mods.Effects["cannot_be_blinded"] != 0) return rating;
        return TimedBlind.Apply(rating, Math.Max(SmokeBlindLess(conn, id), target.Blind.Read(now).Less));
    }
    static (int Chance, int Effect, double Seconds) BlindInputs(IReadOnlyDictionary<uint, int> stats,
        IReadOnlyDictionary<uint, int>? own = null)
    {
        int Get(string name) => Stats.Key(name) is uint key ? (int)Math.Clamp((long)stats.GetValueOrDefault(key)
            + (own?.GetValueOrDefault(key) ?? 0), int.MinValue, int.MaxValue) : 0;
        return (Math.Clamp(Get("global_chance_to_blind_on_hit_%"), 0, 100), Get("blind_effect_+%"),
            4 * Math.Max(0d, 1 + Get("blind_duration_+%") / 100d));
    }
    internal static bool InflictMonsterBlind(SpawnedMonster target, double now,
        (int Chance, int Effect, double Seconds) input, Random? random = null)
    {
        if (target.Dead || target.Life == 0 || input.Chance <= 0
            || input.Chance < 100 && (random ?? Random.Shared).Next(100) >= input.Chance) return false;
        return target.Blind.Inflict(now, input.Seconds, input.Effect, target.Mods.Effects["cannot_be_blinded"] != 0);
    }
    internal static byte[]? BlindPresentation(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || conn.RageRetired) return null;
            double now = conn.Clock.Elapsed.TotalSeconds;
            var bytes = new List<byte>();
            void Present(uint id, TimedBlind state, bool immune, bool player)
            {
                var own = state.Read(now, immune);
                var value = own;
                if (!player && !immune && SmokeBlindLess(conn, id) is int smoke && smoke > value.Less)
                    value = (smoke, 0);
                if (value == state.Sent) return;
                if (state.Buff != 0) bytes.AddRange(InstanceEntities.RemoveBuff(id, state.Buff));
                state.Buff = 0;
                if (own.Less > 0)
                    bytes.AddRange(InstanceEntities.AddBuff(id, state.Buff = conn.NextBuff++, 179, PlayerId,
                        u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, own.Until - now))));
                bytes.AddRange(InstanceEntities.StatsUpdate(id, [(973u, value.Less > 0 ? 1 : 0),
                    (15023u, -value.Less), (15024u, -value.Less)]));
                if (player) bytes.AddRange(PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
                state.Sent = value;
                Console.WriteLine($"[gateway] Blind 0x{id:x}: {value.Less}% less Accuracy/Evasion, until {value.Until:0.###}");
            }
            Present(PlayerId, conn.Blind, conn.PlayerLife == 0
                || conn.Blind.Read(now).Less > 0 && BlindImmune(conn), true);
            foreach (var (id, target) in conn.Monsters)
                Present(id, target.Blind, target.Dead || target.Life == 0 || target.Mods.Effects["cannot_be_blinded"] != 0, false);
            return bytes.Count == 0 ? null : [.. bytes];
        }
    }
    static Task SendBlind(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        Send(remote, stream, ciphers, [], null, atGate: () => BlindPresentation(conn));
}
