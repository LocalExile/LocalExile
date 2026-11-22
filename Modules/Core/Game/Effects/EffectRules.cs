using System.Text.Json;

/// <summary>
/// Effect rules: a stat turns a rule on for an actor, an event fires it, its checks, chance and cooldown gate it, and
/// its effects run. The rows are world.db's <c>effect_rules</c> (<see cref="GameData.EffectRules"/>); a rule is JSON:
/// <code>
/// {"when":"kill", "checks":["frozen","!critical"], "any":false, "chance":VALUE, "cooldown":VALUE,
///  "do":[{"kind":"onslaught","seconds":VALUE}]}
/// </code>
/// <c>when</c>: hit, kill, when_hit, block, stun, skill_use. <c>checks</c>: facts of the event that must hold (all of
/// them, or one with <c>any</c>), <c>!</c> for "not". VALUE: <c>{"fixed":4}</c>, <c>{"from":"stat"}</c> (the rule's own
/// stat) or <c>{"stat":"another_stat"}</c>, with optional <c>scale</c> (multiplies a stat's value), <c>fallback</c>
/// (used while the stat is 0), <c>min</c> and <c>max</c>. No number is fixed in code: <c>chance</c> (default 100) and
/// <c>cooldown</c> (seconds, default none) are values, and so is every number an effect takes.
/// A row that names a part this file does not know is refused with a line in the log, never run half-understood.
/// Only a player's own events run rules so far; a minion's kill (the accepted-contact batch) does not.
/// An actor's live rules (those whose stat it has) are worked out when its stats are built
/// (<see cref="Configure"/>), so an event with no live rule costs one table lookup and one length check.
/// </summary>
public static class EffectRules
{
    public enum What { Hit, Kill, WhenHit, Block, Stun, SkillUse }
    static readonly string[] WhatNames = ["hit", "kill", "when_hit", "block", "stun", "skill_use"];

    /// <summary>Turns every rule off (the code a rule replaced runs again from the next stat build). A testing switch.</summary>
    public static volatile bool Enabled = true;

    /// <summary>A rule fired (its roll passed): for tools that show it. Text is built only while someone listens.</summary>
    public static readonly GameEvent<Action<ClientState, string>> Fired = new();

    /// <summary>A number a rule uses and where it comes from.</summary>
    public sealed record Value(double Fixed, string? Stat, bool OwnStat, double Scale, double Fallback, double? Min, double? Max)
    {
        public static readonly Value Always = new(100, null, false, 1, 0, null, null);

        public double Resolve(int own, Func<string, int> get)
        {
            double value = Fixed;
            if (OwnStat || Stat is not null)
            {
                int raw = OwnStat ? own : get(Stat!);
                value = raw == 0 ? Fallback : raw * Scale;
            }
            if (Min is double min) value = Math.Max(min, value);
            if (Max is double max) value = Math.Min(max, value);
            return value;
        }

        public override string ToString() => OwnStat ? Scaled("the stat") : Stat is not null ? Scaled(Stat) : $"{Fixed:0.###}";
        string Scaled(string from) => (Scale == 1 ? from : $"{from} x {Scale:0.######}") + (Fallback != 0 ? $" (else {Fallback:0.###})" : "");
    }

    public sealed record Check(string Fact, bool Not);

    /// <summary>What an effect kind takes and does: its value parameters (numbers) and name parameters (words), by name.</summary>
    public sealed record Kind(string Name, string[] Values, string[] Names, Action<Fire> Run, Func<string, string, bool>? NameOk = null)
    {
        /// <summary>Value parameters a row may leave out, with the number used then.</summary>
        public IReadOnlyDictionary<string, double> Defaults { get; init; } = new Dictionary<string, double>();
        /// <summary>Further keys the kind reads itself in <see cref="Prepare"/>.</summary>
        public string[] Extra { get; init; } = [];
        /// <summary>Reads the kind's own keys when a row loads: what it returns is handed to <see cref="Fire.Data"/>, and
        /// a <see cref="FormatException"/> refuses the row.</summary>
        public Func<JsonElement, object?>? Prepare { get; init; }
    }

    public sealed record EffectSpec(Kind Kind, Value[] Values, string?[] Names, object? Data);
    /// <summary>Who: whose event fires the rule (CombatActors: "self" unless the row says otherwise); null for anyone's.</summary>
    public sealed record Rule(string Stat, int Ord, What When, string? Who, Check[] Checks, bool Any, Value Chance, Value? Cooldown, EffectSpec[] Do);

    /// <summary>One effect of a rule that fired, with its numbers resolved for this actor.</summary>
    public readonly record struct Fire(ClientState Conn, CombatEvent Event, double Now, Kind Kind, double[] Values, string?[] Names, object? Data)
    {
        public double Number(string name) => Values[Array.IndexOf(Kind.Values, name)];
        public string? Word(string name) => Names[Array.IndexOf(Kind.Names, name)];
    }

    sealed record LiveEffect(Kind Kind, double[] Values, string?[] Names, object? Data);
    sealed record LiveRule(Rule Rule, double Chance, double Cooldown, LiveEffect[] Do);
    sealed class Live
    {
        public LiveRule[][] ByWhat = NoRules;
        public Dictionary<Rule, double>? ReadyAt;
    }
    sealed record Table(int Revision, Rule[] Rules, HashSet<string> Stats);

    static readonly LiveRule[][] NoRules = [.. WhatNames.Select(_ => Array.Empty<LiveRule>())];
    static readonly RuleTable<ClientState, Live> Lives = new();
    static readonly Dictionary<string, Kind> Kinds = new(StringComparer.Ordinal);
    static readonly object Gate = new();
    static Table _table = new(-1, [], []);

    /// <summary>The facts each event can answer, for a rule's checks.</summary>
    static readonly Dictionary<What, string[]> Facts = new()
    {
        [What.Hit] = ["critical", "spell", "attack", "killed", "frozen", "bleeding", "marked", "normal", "magic", "rare", "unique", "rare_or_unique"],
        [What.Kill] = ["frozen", "poisoned", "normal", "magic", "rare", "unique", "rare_or_unique"],
        [What.WhenHit] = ["critical"],
        [What.Block] = ["spell", "attack"],
        [What.Stun] = ["melee"],
        [What.SkillUse] = ["vaal", "curse", "trap"],
    };

    /// <summary>Adds an effect kind. The game module registers its own when it loads; a mod can add more.</summary>
    public static void Register(Kind kind)
    {
        lock (Gate) { Kinds[kind.Name] = kind; _table = new(-1, [], []); }
    }

    /// <summary>Whether an effect rule drives this stat now, so the code that read it before leaves it alone.</summary>
    public static bool Handles(string stat) => Enabled && Current().Stats.Contains(stat);

    /// <summary>A stat lookup that reads 0 for every stat a rule drives: what the replaced code is given.</summary>
    public static Func<string, int> Unhandled(Func<string, int> get)
    {
        if (!Enabled) return get;
        var stats = Current().Stats;
        return stats.Count == 0 ? get : name => stats.Contains(name) ? 0 : get(name);
    }

    static Table Current()
    {
        var table = Volatile.Read(ref _table);
        int revision = GameData.Current.Revision;
        if (table.Revision == revision) return table;
        lock (Gate)
        {
            if (_table.Revision == revision) return _table;
            Game.EnsureEffectKinds();
            var rules = new List<Rule>();
            foreach (var row in GameData.EffectRules)
            {
                try { rules.Add(Parse(row.Stat, row.Ord, row.Rule)); }
                catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException or KeyNotFoundException)
                { Console.WriteLine($"[rules] refused the rule of {row.Stat} #{row.Ord}: {e.Message}"); }
            }
            Console.WriteLine($"[rules] {rules.Count} effect rule(s) loaded, {Kinds.Count} effect kind(s) (game data revision {revision})");
            Volatile.Write(ref _table, new(revision, [.. rules], [.. rules.Select(r => r.Stat)]));
            return _table;
        }
    }

    static Rule Parse(string stat, int ord, string json)
    {
        if (Program.Stats.Key(stat) is null) throw new FormatException("unknown stat");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        foreach (var property in root.EnumerateObject())
            if (property.Name is not ("when" or "who" or "checks" or "any" or "chance" or "cooldown" or "do"))
                throw new FormatException($"unknown key \"{property.Name}\"");
        string whenName = root.GetProperty("when").GetString() ?? "";
        int when = Array.IndexOf(WhatNames, whenName);
        if (when < 0) throw new FormatException($"unknown event \"{whenName}\"");
        // Whose event: the character's own unless the row names another actor of its side, or "any".
        string? who = root.TryGetProperty("who", out var whose) ? whose.GetString() : CombatActors.Self;
        if (string.IsNullOrEmpty(who)) throw new FormatException("\"who\" is empty");
        if (who == "any") who = null;
        var checks = new List<Check>();
        if (root.TryGetProperty("checks", out var list))
            foreach (var item in list.EnumerateArray())
            {
                string text = item.GetString() ?? "";
                bool not = text.StartsWith('!');
                string fact = not ? text[1..] : text;
                if (!Facts[(What)when].Contains(fact)) throw new FormatException($"the event \"{whenName}\" has no fact \"{fact}\"");
                checks.Add(new(fact, not));
            }
        bool any = root.TryGetProperty("any", out var anyOf) && anyOf.GetBoolean();
        var chance = root.TryGetProperty("chance", out var c) ? ParseValue(c) : Value.Always;
        var cooldown = root.TryGetProperty("cooldown", out var cd) ? ParseValue(cd) : null;
        var effects = new List<EffectSpec>();
        foreach (var effect in root.GetProperty("do").EnumerateArray())
        {
            string kindName = effect.GetProperty("kind").GetString() ?? "";
            if (!Kinds.TryGetValue(kindName, out var kind)) throw new FormatException($"unknown effect kind \"{kindName}\"");
            foreach (var property in effect.EnumerateObject())
                if (property.Name != "kind" && !kind.Values.Contains(property.Name) && !kind.Names.Contains(property.Name)
                    && !kind.Extra.Contains(property.Name))
                    throw new FormatException($"the effect \"{kindName}\" has no parameter \"{property.Name}\"");
            var values = new Value[kind.Values.Length];
            for (int i = 0; i < values.Length; i++)
                values[i] = effect.TryGetProperty(kind.Values[i], out var v) ? ParseValue(v)
                    : kind.Defaults.TryGetValue(kind.Values[i], out double byDefault) ? new(byDefault, null, false, 1, 0, null, null)
                    : throw new FormatException($"the effect \"{kindName}\" needs \"{kind.Values[i]}\"");
            var names = new string?[kind.Names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                names[i] = effect.TryGetProperty(kind.Names[i], out var n) ? n.GetString()
                    : throw new FormatException($"the effect \"{kindName}\" needs \"{kind.Names[i]}\"");
                if (kind.NameOk is { } ok && !ok(kind.Names[i], names[i] ?? ""))
                    throw new FormatException($"the effect \"{kindName}\" does not know {kind.Names[i]} \"{names[i]}\"");
            }
            effects.Add(new(kind, values, names, kind.Prepare?.Invoke(effect)));
        }
        if (effects.Count == 0) throw new FormatException("\"do\" is empty");
        return new(stat, ord, (What)when, who, [.. checks], any, chance, cooldown, [.. effects]);
    }

    static Value ParseValue(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number) return new(e.GetDouble(), null, false, 1, 0, null, null);
        double? Number(string name) => e.TryGetProperty(name, out var n) ? n.GetDouble() : null;
        foreach (var property in e.EnumerateObject())
            if (property.Name is not ("fixed" or "from" or "stat" or "scale" or "fallback" or "min" or "max"))
                throw new FormatException($"unknown value key \"{property.Name}\"");
        bool own = e.TryGetProperty("from", out var from)
            && (from.GetString() == "stat" ? true : throw new FormatException($"unknown value source \"{from.GetString()}\""));
        string? stat = e.TryGetProperty("stat", out var s) ? s.GetString() : null;
        double? value = Number("fixed");
        if ((own ? 1 : 0) + (stat is not null ? 1 : 0) + (value is not null ? 1 : 0) != 1)
            throw new FormatException("a value needs exactly one of \"fixed\", \"from\" and \"stat\"");
        if (stat is not null && Program.Stats.Key(stat) is null) throw new FormatException($"unknown stat \"{stat}\"");
        return new(value ?? 0, stat, own, Number("scale") ?? 1, Number("fallback") ?? 0, Number("min"), Number("max"));
    }

    /// <summary>Works out the actor's live rules from its stats. Called where the player stats are built.</summary>
    public static void Configure(ClientState conn, Func<string, int> get)
    {
        var table = Enabled ? Current() : null;
        List<LiveRule>?[]? lists = null;
        if (table is not null)
            foreach (var rule in table.Rules)
            {
                int own = get(rule.Stat);
                if (own == 0) continue;
                double chance = rule.Chance.Resolve(own, get);
                if (chance <= 0) continue;
                var effects = new LiveEffect[rule.Do.Length];
                for (int i = 0; i < effects.Length; i++)
                {
                    var spec = rule.Do[i];
                    var numbers = new double[spec.Values.Length];
                    for (int n = 0; n < numbers.Length; n++) numbers[n] = spec.Values[n].Resolve(own, get);
                    effects[i] = new(spec.Kind, numbers, spec.Names, spec.Data);
                }
                lists ??= new List<LiveRule>?[WhatNames.Length];
                (lists[(int)rule.When] ??= []).Add(new(rule, chance, rule.Cooldown?.Resolve(own, get) ?? 0, effects));
            }
        // No live rule and nothing kept from before: the usual case, and it makes nothing.
        if (lists is null && !Lives.TryGetValue(conn, out _)) return;
        var live = Lives.GetOrCreateValue(conn);
        Volatile.Write(ref live.ByWhat, lists is null ? NoRules : [.. lists.Select(l => l?.ToArray() ?? [])]);
    }

    /// <summary>Whether the actor has a live rule for this event: what a per-hit caller asks before it builds one.</summary>
    public static bool Listens(ClientState conn, What what) =>
        Enabled && Lives.TryGetValue(conn, out var live) && Volatile.Read(ref live.ByWhat)[(int)what].Length > 0;

    /// <summary>Runs the actor's live rules for a combat event of its side. A hit is <see cref="HitLanded"/>, raised
    /// where every hit passes; the charge system's own hit record (ChargeEvent.Hit) is not an event here.</summary>
    public static void Run(ClientState conn, CombatEvent e)
    {
        if (!Enabled || !Lives.TryGetValue(conn, out var live)) return;
        int what = e switch
        {
            HitLanded => 0, ChargeEvent.Kill => 1, ChargeEvent.WhenHit => 2, ChargeEvent.Block => 3,
            ChargeEvent.Stun => 4, ChargeEvent.SkillUse => 5, _ => -1,
        };
        if (what < 0) return;
        var rules = Volatile.Read(ref live.ByWhat)[what];
        if (rules.Length == 0) return;
        double now = conn.Clock.Elapsed.TotalSeconds;
        foreach (var rule in rules)
        {
            if (rule.Rule.Who is { } who && who != e.Who) continue;
            if (!Passes(rule.Rule, e, conn)) continue;
            if (rule.Cooldown > 0)
                lock (live)
                    if (live.ReadyAt is { } ready && ready.TryGetValue(rule.Rule, out double at) && now < at) continue;
            if (rule.Chance < 100 && Random.Shared.NextDouble() * 100 >= rule.Chance) continue;
            if (rule.Cooldown > 0)
                lock (live) (live.ReadyAt ??= [])[rule.Rule] = now + rule.Cooldown;
            foreach (var effect in rule.Do)
                try { effect.Kind.Run(new(conn, e, now, effect.Kind, effect.Values, effect.Names, effect.Data)); }
                catch (Exception ex) { Console.WriteLine($"[rules] {rule.Rule.Stat}: the effect {effect.Kind.Name} failed: {ex.Message}"); }
            if (Fired.Any)
            {
                string text = $"{rule.Rule.Stat} on {WhatNames[what]}: {Describe(rule)}";
                foreach (var (owner, run) in Fired.Subscribers(conn))
                    try { run(conn, text); }
                    catch (Exception ex) { Console.WriteLine($"[events] {owner}: EffectRules.Fired failed: {ex.Message}"); }
            }
        }
    }

    static bool Passes(Rule rule, CombatEvent e, ClientState conn)
    {
        if (rule.Checks.Length == 0) return true;
        foreach (var check in rule.Checks)
        {
            bool holds = Fact(check.Fact, e, conn) != check.Not;
            if (holds && rule.Any) return true;
            if (!holds && !rule.Any) return false;
        }
        return !rule.Any;
    }

    // A hit's facts about the monster are read from the monster when a rule asks (the hit is raised inside the
    // Monsters lock): nothing is worked out for a hit no rule checks.
    static bool Fact(string fact, CombatEvent e, ClientState conn) => (fact, e) switch
    {
        ("critical", HitLanded h) => h.Critical,
        ("critical", ChargeEvent.WhenHit w) => w.Critical,
        ("spell", HitLanded h) => h.Spell,
        ("spell", ChargeEvent.Block b) => b.Spell,
        ("attack", HitLanded h) => !h.Spell,
        ("attack", ChargeEvent.Block b) => !b.Spell,
        ("killed", HitLanded h) => h.Killed,
        ("frozen", HitLanded { Monster: { } m }) => Game.MonsterFrozenNow(conn, m),
        ("frozen", ChargeEvent.Kill k) => k.Frozen,
        ("bleeding", HitLanded { Monster: { } m }) => m.Bleeds.Burning,
        ("marked", HitLanded { Monster: { } m }) => Game.MarkedBy(conn, m),
        ("poisoned", ChargeEvent.Kill k) => k.Poisons > 0,
        ("melee", ChargeEvent.Stun s) => s.Melee,
        ("vaal", ChargeEvent.SkillUse u) => u.Vaal,
        ("curse", ChargeEvent.SkillUse u) => u.Curse,
        ("trap", ChargeEvent.SkillUse u) => u.Trap,
        (_, HitLanded { Monster: { } m }) => Rarity(fact, m.Rarity),
        (_, ChargeEvent.Kill k) => Rarity(fact, k.Rarity),
        _ => false,
    };

    static bool Rarity(string fact, GameServer.Core.Protocol.Instance.MonsterRarity rarity) => fact switch
    {
        "normal" => rarity == GameServer.Core.Protocol.Instance.MonsterRarity.Normal,
        "magic" => rarity == GameServer.Core.Protocol.Instance.MonsterRarity.Magic,
        "rare" => rarity == GameServer.Core.Protocol.Instance.MonsterRarity.Rare,
        "unique" => rarity == GameServer.Core.Protocol.Instance.MonsterRarity.Unique,
        "rare_or_unique" => rarity is GameServer.Core.Protocol.Instance.MonsterRarity.Rare or GameServer.Core.Protocol.Instance.MonsterRarity.Unique,
        _ => false,
    };

    static string Describe(LiveRule rule) =>
        (rule.Rule.Who == CombatActors.Self ? "" : $"by {rule.Rule.Who ?? "anyone"}, ")
        + (rule.Chance < 100 ? $"{rule.Chance:0.##}% chance, " : "")
        + (rule.Cooldown > 0 ? $"cooldown {rule.Cooldown:0.###} s, " : "")
        + string.Join(" + ", rule.Do.Select(d => d.Kind.Name
            + string.Concat(d.Kind.Names.Select((n, i) => $" {n}={d.Names[i]}"))
            + (d.Data is { } data ? $" {data}" : "")
            + string.Concat(d.Kind.Values.Select((n, i) => $" {n}={d.Values[i]:0.###}"))));

    /// <summary>The actor's live rules as text, one line each: for tools that show them.</summary>
    public static IReadOnlyList<string> Describe(ClientState conn)
    {
        if (!Lives.TryGetValue(conn, out var live)) return [];
        var lines = new List<string>();
        var byWhat = Volatile.Read(ref live.ByWhat);
        for (int what = 0; what < byWhat.Length; what++)
            foreach (var rule in byWhat[what])
                lines.Add($"{rule.Rule.Stat} on {WhatNames[what]}"
                    + (rule.Rule.Checks.Length > 0 ? $" if {string.Join(rule.Rule.Any ? " or " : " and ", rule.Rule.Checks.Select(c => (c.Not ? "not " : "") + c.Fact))}" : "")
                    + $": {Describe(rule)}");
        return lines;
    }

    /// <summary>How many rules are loaded and how many effect kinds there are.</summary>
    public static (int Rules, int Kinds) Counts() { var table = Current(); lock (Gate) return (table.Rules.Length, Kinds.Count); }
}
