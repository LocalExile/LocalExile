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
using System.Text.Json;

/// <summary>
/// Buffs a passive applies around the player: PassiveSkills.dat PassiveSkillBuffs → BuffTemplates → BuffDefinitions.
/// Ruthless reads BuffsHardmode only for a row with HasHardmodeAlternate, as PassiveTree does for the stats: every other
/// row's BuffsHardmode is either a copy or empty (Elemental Valiance, Unwavering Crusade, Heartstopper), and Ruthless
/// relics do trigger (3.23.0: Unwavering Crusade's relics capped at level 80 there).
/// A template's Buff_StatValues are the values of its definition's GrantedStats, in order (Stats row + 1 is the client
/// key); AuraRadius is the "nearby" radius in cells (60: 6 m); __col4 lists who gets
/// it, read as 1 the player, 2 allies, 3 enemies (every Augury of Penitence, Occultist and Saboteur enemy aura has [3],
/// the monster auras that buff their pack [1, 2] or [2]; not confirmed in the client).
/// Only the templates listed here are applied, because their stats have a consumer: the enemy auras in
/// <see cref="EnemyAuraTemplates"/> (Program.EnemyDebuffsTick puts them on the monsters, Program.MonsterTakesMitigated
/// reads them) and the player-and-ally auras in <see cref="AllyAuraTemplates"/> (Program.TriggerElementalRelicLocked).
/// </summary>
static class PassiveBuffAuras
{
    /// <summary>An aura a passive puts around the player: its template, BuffDefinitions row, radius (cells) and stats.</summary>
    internal sealed record Aura(string Template, ushort Definition, int Radius, IReadOnlyList<(uint Key, int Value)> Stats);

    // On every hostile living monster within the radius while the player lives.
    static readonly HashSet<string> EnemyAuraTemplates =
    [
        "CustomAscendancyNearbyEnemiesIncreasedElementalDamageTaken", // Paladin Light of Divinity: 2551 Augury of Penitence
    ];

    // On the player and his allies within the radius.
    static readonly HashSet<string> AllyAuraTemplates =
    [
        // Paladin Elemental Valiance and the Guardian's Unwavering Crusade: 2258 trigger_allied_guardian_relic_on_kill
        "GuardianAlliesSpawnRelicsForYouOnKill",
    ];

    const int PlayerTarget = 1, AlliesTarget = 2, EnemiesTarget = 3;

    // One parse of the three tables per mode gives both maps.
    sealed record Maps(IReadOnlyDictionary<ushort, Aura[]> Enemy, IReadOnlyDictionary<ushort, Aura[]> Ally);
    static readonly Lazy<Maps> normal = new(() => LoadAuras("data", ruthless: false));
    static readonly Lazy<Maps> ruthlessMaps = new(() => LoadAuras("data", ruthless: true));

    /// <summary>The enemy auras of these nodes (PassiveSkillGraphIds), one per template however many nodes grant it.</summary>
    internal static IReadOnlyList<Aura> EnemyAurasFor(IEnumerable<ushort> nodes, bool ruthless) =>
        For(nodes, (ruthless ? ruthlessMaps : normal).Value.Enemy);

    /// <summary>The player-and-ally auras of these nodes, one per template.</summary>
    internal static IReadOnlyList<Aura> AllyAurasFor(IEnumerable<ushort> nodes, bool ruthless) =>
        For(nodes, (ruthless ? ruthlessMaps : normal).Value.Ally);

    static IReadOnlyList<Aura> For(IEnumerable<ushort> nodes, IReadOnlyDictionary<ushort, Aura[]> auras) =>
        [.. nodes.SelectMany(n => auras.GetValueOrDefault(n) ?? []).DistinctBy(a => a.Template)];

    static Maps LoadAuras(string folder, bool ruthless)
    {
        string Table(string name) => Path.Combine(folder, "tables", $"{name}.json");
        var enemy = new Dictionary<ushort, Aura[]>();
        var ally = new Dictionary<ushort, Aura[]>();
        if (new[] { "PassiveSkills", "BuffTemplates", "BuffDefinitions" }.Any(t => !File.Exists(Table(t)))) return new(enemy, ally);
        using var passives = JsonDocument.Parse(File.ReadAllBytes(Table("PassiveSkills")));
        using var templates = JsonDocument.Parse(File.ReadAllBytes(Table("BuffTemplates")));
        using var definitions = JsonDocument.Parse(File.ReadAllBytes(Table("BuffDefinitions")));
        Aura? Template(int row, HashSet<string> allowed, int[] targets)
        {
            if (row < 0 || row >= templates.RootElement.GetArrayLength()) return null;
            var t = templates.RootElement[row];
            if (t.GetProperty("Id").GetString() is not { } id || !allowed.Contains(id)
                || t.GetProperty("AuraRadius").GetInt32() is not (> 0 and var radius)
                || !t.GetProperty("__col4").EnumerateArray().Select(v => v.GetInt32()).SequenceEqual(targets)
                || t.GetProperty("BuffDefinitionsKey") is not { ValueKind: JsonValueKind.Number } key) return null;
            int definition = key.GetInt32();
            if (definition < 0 || definition > ushort.MaxValue || definition >= definitions.RootElement.GetArrayLength()) return null;
            var granted = definitions.RootElement[definition].GetProperty("GrantedStats").EnumerateArray().Select(s => s.GetInt32()).ToList();
            var values = t.GetProperty("Buff_StatValues").EnumerateArray().Select(v => v.GetInt32()).ToList();
            // A template whose values do not line up with its definition's stats is left out rather than misapplied.
            if (granted.Count != values.Count) return null;
            return new(id, (ushort)definition, radius, [.. granted.Zip(values, (stat, value) => ((uint)(stat + 1), value))]);
        }
        foreach (var row in passives.RootElement.EnumerateArray())
        {
            if (row.GetProperty("PassiveSkillGraphId").GetInt32() is not (> 0 and <= ushort.MaxValue and var node)) continue;
            bool alternate = ruthless && row.TryGetProperty("HasHardmodeAlternate", out var hardmode) && hardmode.GetBoolean();
            if (!row.TryGetProperty(alternate ? "BuffsHardmode" : "PassiveSkillBuffs", out var buffs)
                || buffs.ValueKind != JsonValueKind.Array) continue;
            int[] rows = [.. buffs.EnumerateArray().Select(b => b.GetInt32())];
            void Add(Dictionary<ushort, Aura[]> map, HashSet<string> allowed, int[] targets)
            {
                if (rows.Select(r => Template(r, allowed, targets)).OfType<Aura>().ToArray() is { Length: > 0 } auras)
                    map.TryAdd((ushort)node, auras);
            }
            Add(enemy, EnemyAuraTemplates, [EnemiesTarget]);
            Add(ally, AllyAuraTemplates, [PlayerTarget, AlliesTarget]);
        }
        return new(enemy, ally);
    // Every player tick (250 ms, not while paused): the passive blind aura's buffs follow the living hostile monsters within
    // its radius of the living player. A death (CancelOnDeath), a refund or leaving the area takes them off: a dead player
    // and an aura no longer allocated blind nothing, and a new area is a new connection.
    internal static async Task TickPassiveAuras(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        List<(uint Monster, ushort Buff, bool Added)> changes;
        List<ushort> nodes;
        lock (conn.Monsters) nodes = [.. conn.Passives, .. conn.MainAscendancy.Allocated];
        // Outside the lock: the first call loads the aura table from PassiveSkills.
        int? blindRadius = PassiveBuffAuras.BlindRadius(nodes, conn.Ruthless);
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea) return;
            double now = conn.Clock.Elapsed.TotalSeconds;
            var player = conn.Walk.Position(now);
            int? radius = conn.PlayerLife > 0 ? blindRadius : null;
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
