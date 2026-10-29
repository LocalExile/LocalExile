using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

/// <summary>
/// Buffs a passive applies around the player (Agents/docs/protocol/11-skills.md §12, "Passive auras on enemies"):
/// PassiveSkills.dat <c>PassiveSkillBuffs</c> (<c>BuffsHardmode</c> for a Ruthless alternate) → BuffTemplates. Only the
/// templates in <see cref="Supported"/> act, because their effect has consumers; a passive's other templates do nothing
/// yet. <c>SaboteurBlind</c> (Polytheist's Devout of Solaris, the Saboteur's Born in the Shadows) is
/// <c>generic_buff_aura</c> (BuffDefinitions 1521: invisible, no stats, cancelled on death) with AuraRadius 35, in cells
/// like the other radii (the wiki's distance table gives this template 3.5 m): every living hostile monster within it of
/// the living player is Blinded (<see cref="Program.Blinded"/>), shown with BuffDefinitions <c>blinded</c> (179) on the
/// monster as the flask smoke cloud does. The invisible stat-less aura buff on the player is not sent: the client has
/// nothing to show or derive from it.
/// </summary>
static class PassiveBuffAuras
{
    public enum Effect { Blind }

    /// <summary>A passive's aura: its BuffTemplates Id, its radius in cells and what it does to the enemies in it.</summary>
    public sealed record Aura(string Template, int Radius, Effect Effect);

    // The templates the server applies: their effect has consumers (Program.Blinded's).
    static readonly Dictionary<string, Effect> Supported = new() { ["SaboteurBlind"] = Effect.Blind };

    static readonly Lazy<Dictionary<ushort, Aura[]>> Normal = new(() => Load("data", ruthless: false));
    static readonly Lazy<Dictionary<ushort, Aura[]>> Ruthless = new(() => Load("data", ruthless: true));

    /// <summary>The supported auras of these nodes (PassiveSkillGraphIds), one per template however many nodes grant it.</summary>
    public static IReadOnlyList<Aura> For(IEnumerable<ushort> nodes, bool ruthless) =>
        [.. nodes.SelectMany(n => (ruthless ? Ruthless : Normal).Value.GetValueOrDefault(n) ?? []).DistinctBy(a => a.Template)];

    /// <summary>The largest blind radius of these nodes, or null without one.</summary>
    public static int? BlindRadius(IEnumerable<ushort> nodes, bool ruthless) =>
        For(nodes, ruthless).Where(a => a.Effect == Effect.Blind).Select(a => (int?)a.Radius).Max();

    static Dictionary<ushort, Aura[]> Load(string folder, bool ruthless)
    {
        string Table(string name) => Path.Combine(folder, "tables", $"{name}.json");
        var result = new Dictionary<ushort, Aura[]>();
        if (!File.Exists(Table("PassiveSkills")) || !File.Exists(Table("BuffTemplates"))) return result;
        using var passives = JsonDocument.Parse(File.ReadAllBytes(Table("PassiveSkills")));
        using var templates = JsonDocument.Parse(File.ReadAllBytes(Table("BuffTemplates")));
        foreach (var row in passives.RootElement.EnumerateArray())
        {
            if (row.GetProperty("PassiveSkillGraphId").GetInt32() is not (> 0 and <= ushort.MaxValue and var node)) continue;
            // As PassiveTree reads the stats: a Ruthless catalog takes the alternate columns only for a marked row.
            bool alternate = ruthless && row.TryGetProperty("HasHardmodeAlternate", out var marked) && marked.GetBoolean();
            if (!row.TryGetProperty(alternate ? "BuffsHardmode" : "PassiveSkillBuffs", out var buffs) || buffs.ValueKind != JsonValueKind.Array) continue;
            var auras = new List<Aura>();
            foreach (var key in buffs.EnumerateArray())
            {
                if (key.GetInt32() is not (>= 0 and var t) || t >= templates.RootElement.GetArrayLength()) continue;
                var template = templates.RootElement[t];
                if (template.GetProperty("Id").GetString() is { } id && Supported.TryGetValue(id, out var effect)
                    && template.GetProperty("AuraRadius").GetInt32() is > 0 and var radius)
                    auras.Add(new(id, radius, effect));
            }
            if (auras.Count > 0) result[(ushort)node] = [.. auras];
        }
        return result;
    }

    /// <summary>One connection's monsters blinded by the player's passive aura, with the ids of the buffs that show it.
    /// Its own lock; the connection's Monsters lock may be held when it is taken, never the other way round.</summary>
    public sealed class State
    {
        readonly Dictionary<uint, ushort> _blinded = [];
        readonly Lock _lock = new();

        public bool Blinded(uint monster)
        {
            lock (_lock) return _blinded.ContainsKey(monster);
        }

        /// <summary>The buffs to add and to remove so that exactly <paramref name="inside"/> are blinded. A monster that
        /// is gone (<paramref name="exists"/> false) is forgotten without a message. Call under the Monsters lock, which
        /// <paramref name="next"/> needs.</summary>
        public List<(uint Monster, ushort Buff, bool Added)> Reconcile(IReadOnlySet<uint> inside, Func<uint, bool> exists, Func<ushort> next)
        {
            lock (_lock)
            {
                var changes = new List<(uint, ushort, bool)>();
                foreach (var (id, buff) in _blinded.Where(kv => !inside.Contains(kv.Key)).ToList())
                {
                    _blinded.Remove(id);
                    if (exists(id)) changes.Add((id, buff, false));
                }
                foreach (uint id in inside.Where(id => !_blinded.ContainsKey(id)))
                    changes.Add((id, _blinded[id] = next(), true));
                return changes;
            }
        }
    }
}

partial class Program
{
    // Every player tick (250 ms, not while paused): the passive blind aura's buffs follow the living hostile monsters within
    // its radius of the living player. A death (CancelOnDeath), a refund or leaving the area takes them off: a dead player
    // and an aura no longer allocated blind nothing, and a new area is a new connection.
    internal static async Task TickPassiveAuras(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        List<(uint Monster, ushort Buff, bool Added)> changes;
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea) return;
            double now = conn.Clock.Elapsed.TotalSeconds;
            var player = conn.Walk.Position(now);
            int? radius = conn.PlayerLife > 0
                ? PassiveBuffAuras.BlindRadius([.. conn.Passives, .. conn.MainAscendancy.Allocated], conn.Ruthless) : null;
            HashSet<uint> inside = radius is int r
                ? conn.Monsters.Where(kv => kv.Value is { Hostile: true, Dead: false, Life: > 0 } && Dist(kv.Value.Walk.Position(now), player) <= r)
                    .Select(kv => kv.Key).ToHashSet()
                : [];
            changes = conn.PassiveAuras.Reconcile(inside, conn.Monsters.ContainsKey, () => conn.NextBuff++);
        }
        foreach (var (id, buff, added) in changes)
            await Send(remote, stream, ciphers, added
                ? InstanceEntities.AddBuff(id, buff, FlaskGrounds.BlindedBuffRow, PlayerId)
                : InstanceEntities.RemoveBuff(id, buff), $"0x{(added ? "01c1" : "01c2")} monster 0x{id:x} {(added ? "blinded by the passive aura" : "out of the blind aura")}");
    }
}
