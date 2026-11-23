using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

/// <summary>
/// The shared timed buff: any buff of the client's BuffDefinitions on the player for a time, with the stats it gives.
/// A gain starts the buff or lengthens it and never shortens the one running; death ends it; its stats count while the
/// buff runs. One buff per
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
        public bool Applied => double.IsFinite(SentUntil);
    }
    sealed class TimedBuffSet { public readonly Dictionary<string, TimedBuff> ById = new(StringComparer.Ordinal); }
    static readonly RuleTable<Player, TimedBuffSet> TimedBuffs = new();
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
    /// (1 = as given). Call it in the area's frame; the buff and the stats are presented after the next Stats phase.</summary>
    public static void GainTimedBuff(Player player, BuffSpec spec, double seconds, double effect = 1) =>
        GainTimedBuffLocked(player, spec, seconds, effect, AreaTime(player));

    static bool GainTimedBuffLocked(Player player, BuffSpec spec, double seconds, double effect, double now)
    {
        if (seconds <= 0 || player.PlayerLife == 0) return false;
        var set = TimedBuffs.GetOrCreateValue(player);
        if (!set.ById.TryGetValue(spec.Id, out var buff)) set.ById[spec.Id] = buff = new();
        if (now + seconds <= buff.Until) return false;
        bool running = now < buff.Until;
        // Its stats start to count, or change, with this gain: the sources step reads them again.
        if (!running || !ReferenceEquals(buff.Spec, spec) || buff.Effect != effect) MarkPlayerStats(player, PlayerStatStep.Sources);
        buff.Spec = spec;
        buff.Effect = effect;
        buff.Until = now + seconds;
        // The client is told when the buff starts or its end moved enough; a gain on every hit otherwise costs nothing.
        if (running && buff.Until - buff.SentUntil < TimedBuffRepresentSeconds) return false;
        PresentStats(player);
        return true;
    }

    /// <summary>States the timed buffs the player has to the area's Buffs component (which writes what changed), and
    /// drops the ones that ran out or that death ended.</summary>
    public static Task SendTimedBuffs(Player player)
    {
        if (!TimedBuffs.TryGetValue(player, out var set) || set.ById.Count == 0) return Task.CompletedTask;
        if (player.Closed || player.LeavingArea) return Task.CompletedTask;
        double now = AreaTime(player);
        bool alive = player.PlayerLife > 0;
        List<string>? ended = null;
        foreach (var (id, buff) in set.ById)
        {
            if (!alive || now >= buff.Until)
            {
                player.Area.Buffs.Clear(player.Id, buff.Spec.Row, player.Id);
                (ended ??= []).Add(id);
                Console.WriteLine($"[gateway] buff {id} ended (timed buff)");
                continue;
            }
            if (buff.Applied && buff.Until - buff.SentUntil < TimedBuffRepresentSeconds) continue;
            if (!buff.Applied) Console.WriteLine($"[gateway] buff {id} started (timed buff)");
            player.Area.Buffs.Set(player.Id, new(buff.Spec.Row, player.Id, U68: Buff.Seconds(buff.Until - now)));
            buff.SentUntil = buff.Until;
            // Its end is a change of state nothing else announces: the presentations run again then.
            PresentIn(player, buff.Until - now);
        }
        if (ended is not null)
        {
            foreach (string id in ended) set.ById.Remove(id);
            MarkPlayerStats(player, PlayerStatStep.Sources);
        }
        return Task.CompletedTask;
    }

    // The stats of the running timed buffs (CharacterStatSources). The step runs again when the first of them ends.
    static Dictionary<uint, int> WithTimedBuffs(Player player, Dictionary<uint, int> sources)
    {
        if (!TimedBuffs.TryGetValue(player, out var set) || player.PlayerLife == 0) return sources;
        double now = AreaTime(player);
        foreach (var buff in set.ById.Values)
        {
            if (now >= buff.Until) continue;
            StatsChangeIn(buff.Until - now);
            foreach (var (key, value) in buff.Spec.Stats)
                sources[key] = ActorScale.Saturate((long)sources.GetValueOrDefault(key) + (long)Math.Round(value * buff.Effect));
        }
        return sources;
    }
}
