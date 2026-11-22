using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

/// <summary>
/// The shared timed buff: any buff of the client's BuffDefinitions on the player for a time, with the stats it gives.
/// A gain starts the buff or lengthens it and never shortens the one running; death ends it; its stats count while the
/// client has been told (as the kill Onslaught's do), so the icon and the numbers switch together. One buff per
/// BuffDefinitions Id: a later gain with other numbers replaces them when it lengthens the buff.
/// Effect rules use it through the "buff" kind; a mod can call <see cref="Game.GainTimedBuff"/>.
/// </summary>
public static partial class Game
{
    /// <summary>A buff and the stats it gives: its BuffDefinitions Id and row, and stat keys with their values.</summary>
    public sealed record BuffSpec(string Id, ushort Row, (uint Key, int Value)[] Stats)
    {
        public override string ToString() => $"buff={Id}";
    }

    sealed class TimedBuff
    {
        public BuffSpec Spec = null!;
        public double Effect = 1;
        public double Until = double.NegativeInfinity, SentUntil = double.NegativeInfinity;
        public ushort Instance;
        public bool StatsChanged;
        public bool Applied => double.IsFinite(SentUntil);
    }
    sealed class TimedBuffSet { public readonly Dictionary<string, TimedBuff> ById = new(StringComparer.Ordinal); }
    static readonly RuleTable<ClientState, TimedBuffSet> TimedBuffs = new();
    // A running buff is sent again only when its end moves by at least this, as the kill Onslaught's is.
    const double TimedBuffRepresentSeconds = 1;

    /// <summary>A buff with the values of one of the client's BuffTemplates rows ("SentinelAdrenaline": Adrenaline
    /// with 100% more damage, 25% speeds, 10% physical damage reduction).</summary>
    public static BuffSpec BuffFromTemplate(string template)
    {
        foreach (var row in ClientTables.Table("BuffTemplates"))
            if (row.Text("Id") == template)
                return ClientTables.Table("BuffDefinitions").Row((int)row.Long("BuffDefinitionsKey")) is { } definition
                    ? BuffWithValues(definition, row.Ints("Buff_StatValues"))
                    : throw new FormatException($"the buff template \"{template}\" names no buff");
        throw new FormatException($"unknown buff template \"{template}\"");
    }

    /// <summary>A buff by its BuffDefinitions Id, with one value for each of the stats that row lists, in its order.</summary>
    public static BuffSpec BuffWithValues(string buff, int[] values)
    {
        foreach (var row in ClientTables.Table("BuffDefinitions"))
            if (row.Text("Id") == buff) return BuffWithValues(row, values);
        throw new FormatException($"unknown buff \"{buff}\"");
    }

    static BuffSpec BuffWithValues(ClientRow definition, int[] values)
    {
        int[] statRows = definition.Ints("StatsKeys");
        if (values.Length != statRows.Length)
            throw new FormatException($"the buff \"{definition.Text("Id")}\" has {statRows.Length} stat(s), and {values.Length} value(s) were given");
        var names = ClientTables.Table("Stats");
        var stats = new List<(uint, int)>();
        for (int i = 0; i < statRows.Length; i++)
            if (values[i] != 0 && names.Row(statRows[i])?.Text("Id") is { } name && Program.Stats.Key(name) is uint key)
                stats.Add((key, values[i]));
        return new(definition.Text("Id") ?? "", (ushort)definition.Index, [.. stats]);
    }

    // The "buff" effect kind's own keys: "template", or "buff" with "values".
    static object BuffSpecOf(JsonElement effect)
    {
        if (effect.TryGetProperty("template", out var template)) return BuffFromTemplate(template.GetString() ?? "");
        if (effect.TryGetProperty("buff", out var buff) && effect.TryGetProperty("values", out var values))
            return BuffWithValues(buff.GetString() ?? "", [.. values.EnumerateArray().Select(v => v.GetInt32())]);
        throw new FormatException("the effect \"buff\" needs \"template\", or \"buff\" with \"values\"");
    }

    /// <summary>The player gains the buff for <paramref name="seconds"/>, its stats times <paramref name="effect"/>
    /// (1 = as given). Safe from anywhere: the buff goes out from the thread pool when the client would see a change,
    /// and on the player tick otherwise.</summary>
    public static void GainTimedBuff(ClientState conn, BuffSpec spec, double seconds, double effect = 1)
    {
        bool gained;
        lock (conn.Monsters)
            gained = GainTimedBuffLocked(conn, spec, seconds, effect, conn.Clock.Elapsed.TotalSeconds);
        if (gained && conn.Wire is (var remote, { } stream, var ciphers))
            _ = Task.Run(() => SendTimedBuffs(remote, stream, ciphers, conn));
    }

    static bool GainTimedBuffLocked(ClientState conn, BuffSpec spec, double seconds, double effect, double now)
    {
        if (seconds <= 0 || conn.PlayerLife == 0) return false;
        var set = TimedBuffs.GetOrCreateValue(conn);
        if (!set.ById.TryGetValue(spec.Id, out var buff)) set.ById[spec.Id] = buff = new();
        if (now + seconds <= buff.Until) return false;
        if (buff.Applied && (!ReferenceEquals(buff.Spec, spec) || buff.Effect != effect)) buff.StatsChanged = true;
        buff.Spec = spec;
        buff.Effect = effect;
        buff.Until = now + seconds;
        // Whether the client would be told something new now; a gain on every hit otherwise waits for the player tick.
        return !buff.Applied || buff.StatsChanged || buff.Until - buff.SentUntil >= TimedBuffRepresentSeconds;
    }

    /// <summary>What the client is told now about the timed buffs: buffs to remove and add, and, when one starts, ends
    /// or changes its numbers, the pools, walk speed, stats and skills. Null when nothing changed.</summary>
    public static byte[]? TimedBuffPresentation(ClientState conn)
    {
        if (!TimedBuffs.TryGetValue(conn, out var set)) return null;
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || conn.RageRetired || set.ById.Count == 0) return null;
            double now = conn.Clock.Elapsed.TotalSeconds;
            bool alive = conn.PlayerLife > 0, stats = false;
            List<byte>? bytes = null;
            List<string>? ended = null;
            foreach (var (id, buff) in set.ById)
            {
                if (!alive) buff.Until = double.NegativeInfinity;
                double until = now < buff.Until ? buff.Until : double.NegativeInfinity;
                bool show = double.IsFinite(until);
                bool remove = buff.Instance != 0 && !show;
                bool add = show && (buff.Instance == 0 || until - buff.SentUntil >= TimedBuffRepresentSeconds);
                bool flipped = show != buff.Applied;
                if (!show) (ended ??= []).Add(id);
                if (!remove && !add && !flipped && !buff.StatsChanged) continue;
                bytes ??= [];
                if (buff.Instance != 0 && (remove || add))
                {
                    bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, buff.Instance));
                    buff.Instance = 0;
                }
                if (add)
                    bytes.AddRange(InstanceEntities.AddBuff(PlayerId, buff.Instance = conn.NextBuff++, buff.Spec.Row, PlayerId,
                        u68: BitConverter.SingleToUInt32Bits((float)(until - now))));
                if (flipped || buff.StatsChanged) stats = true;
                buff.StatsChanged = false;
                buff.SentUntil = show ? until : double.NegativeInfinity;
                if (flipped) Console.WriteLine($"[gateway] buff {id} {(show ? "started" : "ended")} (timed buff)");
            }
            if (ended is not null) foreach (string id in ended) set.ById.Remove(id);
            if (bytes is null) return null;
            if (stats)
            {
                RecomputePoolsAndSpeed(conn);
                bytes.AddRange(PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
                bytes.AddRange(PlayerSkillsIfChanged(conn, PlayerSkills(conn)));
            }
            return [.. bytes];
        }
    }

    public static Task SendTimedBuffs(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        TimedBuffs.TryGetValue(conn, out var set) && set.ById.Count > 0
            ? Send(remote, stream, ciphers, [], null, atGate: () => TimedBuffPresentation(conn) ?? [])
            : Task.CompletedTask;

    // The stats of the timed buffs the client has been told about (CharacterStatSources).
    static Dictionary<uint, int> WithTimedBuffs(ClientState conn, Dictionary<uint, int> sources)
    {
        if (!TimedBuffs.TryGetValue(conn, out var set)) return sources;
        lock (conn.Monsters)
        {
            if (conn.PlayerLife == 0) return sources;
            foreach (var buff in set.ById.Values)
                if (buff.Applied)
                    foreach (var (key, value) in buff.Spec.Stats)
                        sources[key] = ActorScale.Saturate((long)sources.GetValueOrDefault(key) + (long)Math.Round(value * buff.Effect));
        }
        return sources;
    }
}
