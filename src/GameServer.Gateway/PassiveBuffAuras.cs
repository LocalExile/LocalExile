using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using DamageConversion = GameServer.Core.World.DamageConversion;

/// <summary>
/// Buffs a passive skill applies around the player (Agents/docs/protocol/11-skills.md §10e): PassiveSkills
/// <c>PassiveSkillBuffs</c> → BuffTemplates → BuffDefinitions. A template names the BuffDefinitions row, the values of
/// that row's <c>StatsKeys</c> in order (<c>Buff_StatValues</c>), its <c>AuraRadius</c> in cells and whom it affects
/// (column 4: 3 = enemies, the value of every enemy debuff aura such as Slayer's Overwhelm and Occultist's; 1 and 2 on
/// the self and ally ones such as Tailwind). Only enemy auras whose every stat the server applies act yet
/// (Program.EnemyAuraActs: so far the hit conversions, Born under Fire's): their buff goes on every living hostile
/// monster within the radius while the player lives (<see cref="State"/>, Program.RunPassiveAuras), and their stats act
/// on its hits (Program.EnemyAuraConversionLocked).
/// </summary>
static class PassiveBuffAuras
{
    public sealed record Aura(int Template, ushort BuffRow, int Radius, bool Enemies, IReadOnlyList<(uint Stat, int Value)> Stats);

    /// <summary>The auras of the allocated passives (PassiveSkillGraphId), in node order.</summary>
    public static IReadOnlyList<Aura> Of(IEnumerable<ushort> allocated) =>
        [.. allocated.SelectMany(n => ByNode.Value.TryGetValue(n, out var auras) ? auras : [])];

    static readonly Lazy<Dictionary<ushort, Aura[]>> ByNode = new(() => Load("data/tables"));

    static Dictionary<ushort, Aura[]> Load(string tables)
    {
        var result = new Dictionary<ushort, Aura[]>();
        string Path(string name) => System.IO.Path.Combine(tables, name + ".json");
        if (!File.Exists(Path("PassiveSkills")) || !File.Exists(Path("BuffTemplates")) || !File.Exists(Path("BuffDefinitions"))) return result;
        using var passives = JsonDocument.Parse(File.ReadAllBytes(Path("PassiveSkills")));
        using var templates = JsonDocument.Parse(File.ReadAllBytes(Path("BuffTemplates")));
        using var definitions = JsonDocument.Parse(File.ReadAllBytes(Path("BuffDefinitions")));
        static IEnumerable<int> Ints(JsonElement row, string column) => row.TryGetProperty(column, out var v)
            && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetInt32()) : [];
        foreach (var passive in passives.RootElement.EnumerateArray())
        {
            int graph = passive.GetProperty("PassiveSkillGraphId").GetInt32();
            var auras = new List<Aura>();
            foreach (int t in Ints(passive, "PassiveSkillBuffs"))
            {
                var template = templates.RootElement[t];
                if (template.GetProperty("BuffDefinitionsKey") is not { ValueKind: JsonValueKind.Number } key) continue;
                var definition = definitions.RootElement[key.GetInt32()];
                var stats = Ints(definition, "StatsKeys").Select(k => (uint)(k + 1)).Zip(Ints(template, "Buff_StatValues")).ToList();
                auras.Add(new(t, (ushort)key.GetInt32(), template.GetProperty("AuraRadius").GetInt32(),
                    Ints(template, "__col4").Contains(3), stats));
            }
            if (auras.Count > 0 && graph is > 0 and <= ushort.MaxValue) result[(ushort)graph] = [.. auras];
        }
        return result;
    }

    /// <summary>Whether a cell is within the radius of the player (the server's Euclidean cell distance).</summary>
    public static bool Within((int X, int Y) at, (int X, int Y) player, int radius) =>
        (long)(at.X - player.X) * (at.X - player.X) + (long)(at.Y - player.Y) * (at.Y - player.Y) <= (long)radius * radius;

    /// <summary>The enemy auras' buffs on monsters (monster id, template → buff instance). Guarded by the Monsters
    /// lock.</summary>
    public sealed class State
    {
        readonly Dictionary<(uint Monster, int Template), ushort> on = [];

        /// <summary>The buffs to add (Added) and to remove so that each living hostile monster within an enemy aura's
        /// radius of the living player has that aura's buff and no other has it. A dead or vanished monster's buff is
        /// forgotten without a message: the buff rows are CancelOnDeath and a removed object has none.</summary>
        public List<(uint Monster, ushort Buff, ushort Row, bool Added)> Reconcile(IReadOnlyList<Aura> auras,
            IReadOnlyDictionary<uint, SpawnedMonster> monsters, (int X, int Y) player, bool alive, double now, Func<ushort> next)
        {
            var changes = new List<(uint, ushort, ushort, bool)>();
            var wanted = new HashSet<(uint, int)>();
            if (alive)
                foreach (var aura in auras.Where(a => a.Enemies && a.Radius > 0))
                    foreach (var (id, m) in monsters)
                        if (m.Hostile && !m.Dead && m.Life > 0 && Within(m.Walk.Position(now), player, aura.Radius)
                            && wanted.Add((id, aura.Template)) && !on.ContainsKey((id, aura.Template)))
                            changes.Add((id, on[(id, aura.Template)] = next(), aura.BuffRow, true));
            foreach (var ((id, template), buff) in on.Where(kv => !wanted.Contains(kv.Key)).ToList())
            {
                on.Remove((id, template));
                if (monsters.TryGetValue(id, out var m) && !m.Dead && m.Life > 0)
                    changes.Add((id, buff, auras.FirstOrDefault(a => a.Template == template)?.BuffRow ?? 0, false));
            }
            return changes;
        }
    }
}

partial class Program
{
    // Every 100 ms of game time: the player's passive enemy auras' buffs follow the monsters within their radius
    // (PassiveBuffAuras.State). The hits read the radius themselves at contact (EnemyAuraConversionLocked).
    static async Task RunPassiveAuras(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        try
        {
            while (!conn.Closed)
            {
                await Task.Delay(100);
                if (!conn.Paused && !conn.LeavingArea) await TickPassiveAuras(remote, stream, ciphers, conn);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    internal static async Task TickPassiveAuras(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        List<(uint Monster, ushort Buff, ushort Row, bool Added)> changes;
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            changes = conn.PassiveAuras.Reconcile([.. ActingEnemyAuras(conn)], conn.Monsters, conn.Walk.Position(now),
                conn.PlayerLife > 0, now, () => conn.NextBuff++);
        }
        foreach (var (id, buff, row, added) in changes)
            await Send(remote, stream, ciphers, added ? InstanceEntities.AddBuff(id, buff, row, PlayerId) : InstanceEntities.RemoveBuff(id, buff),
                $"0x{(added ? "01c1" : "01c2")} monster 0x{id:x} passive aura buff {row} {(added ? "on" : "off")}");
    }

    // The conversion the player's passive enemy auras give this monster's hit when it stands within their radius of the
    // player at contact (Born under Fire: base_physical_damage_%_to_convert_to_fire), by the player's own conversion rules
    // (DamageStats.Conversion); null when none applies. Call under the Monsters lock.
    internal static DamageConversion? EnemyAuraConversionLocked(ClientState conn, SpawnedMonster m, (int X, int Y) player, double now)
    {
        if (!m.Hostile || conn.PlayerLife == 0) return null;
        var at = m.Walk.Position(now);
        var stats = new Dictionary<uint, int>();
        foreach (var aura in ActingEnemyAuras(conn))
            if (PassiveBuffAuras.Within(at, player, aura.Radius))
                foreach (var (stat, value) in aura.Stats) stats[stat] = stats.GetValueOrDefault(stat) + value;
        var conversion = DamageStats.Conversion(stats, Stats, attack: true);
        return conversion.IsEmpty ? null : conversion;
    }

    static IEnumerable<PassiveBuffAuras.Aura> ActingEnemyAuras(ClientState conn) =>
        PassiveBuffAuras.Of([.. conn.Passives, .. conn.MainAscendancy.Allocated]).Where(EnemyAuraActs);

    // Whether the server applies every stat of this enemy aura (so far: each is a hit conversion). The others' buffs would
    // show effects that do not happen, so they are not sent until their stats have consumers.
    internal static bool EnemyAuraActs(PassiveBuffAuras.Aura aura) => aura.Enemies && aura.Radius > 0 && aura.Stats.Count > 0
        && aura.Stats.All(s => !DamageStats.Conversion(new Dictionary<uint, int> { [s.Stat] = s.Value }, Stats, attack: true).IsEmpty);
}
