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

    /// <summary>The loop guard: an effect kind that can cause events of its own (<see cref="Kind.Chains"/>: a released
    /// skill) does not run for an event deeper than its rule allows ("depth" on the row, 0 unless it says otherwise:
    /// a triggered skill does not trigger another). Off: every event fires, whatever caused it.</summary>
    public static volatile bool LoopGuard = true;

    /// <summary>A rule fired (its roll passed): for tools that show it. Text is built only while someone listens.</summary>
    public static readonly GameEvent<Action<Player, string>> Fired = new();

    /// <summary>A number a rule uses and where it comes from.</summary>
    public sealed record Value(double Fixed, string? Stat, bool OwnStat, double Scale, double Fallback, double? Min, double? Max)
    {
        public static readonly Value Always = new(100, null, false, 1, 0, null, null);

        /// <summary>A number of the rule's carrier by name (a granted skill's "cooldown" or "level"), in place of a stat.</summary>
        public string? Data { get; init; }
        /// <summary>The id of <see cref="Stat"/>: what the lookups take.</summary>
        public uint StatKey { get; init; }

        public double Resolve(int own, Func<uint, int> get, IReadOnlyDictionary<string, double>? data = null)
        {
            double value = Fixed;
            if (Data is not null)
            {
                double raw = data is not null && data.TryGetValue(Data, out double found) ? found : 0;
                value = raw == 0 ? Fallback : raw * Scale;
            }
            else if (OwnStat || Stat is not null)
            {
                int raw = OwnStat ? own : get(StatKey);
                value = raw == 0 ? Fallback : raw * Scale;
            }
            if (Min is double min) value = Math.Max(min, value);
            if (Max is double max) value = Math.Min(max, value);
            return value;
        }

        public override string ToString() => Data is not null ? Scaled($"the carrier's {Data}")
            : OwnStat ? Scaled("the stat") : Stat is not null ? Scaled(Stat) : $"{Fixed:0.###}";
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
        /// <summary>What the kind does can cause combat events of its own (it releases a skill): the loop guard applies.</summary>
        public bool Chains { get; init; }
        /// <summary>Name parameters a row may leave out, with the word used then.</summary>
        public IReadOnlyDictionary<string, string> NameDefaults { get; init; } = new Dictionary<string, string>();
        /// <summary>Reads the kind's own keys when a row loads: what it returns is handed to <see cref="Fire.Data"/>, and
        /// a <see cref="FormatException"/> refuses the row.</summary>
        public Func<JsonElement, object?>? Prepare { get; init; }
    }

    public sealed record EffectSpec(Kind Kind, Value[] Values, string?[] Names, object? Data);
    /// <summary>Who: whose event fires the rule (CombatActors: "self" unless the row says otherwise); null for anyone's.
    /// Carrier: where the rule's stat is read. Null: the actor's own summed stats. Otherwise a kind of
    /// <see cref="Source"/> ("skill": each skill an item grants), and the rule is live once for each one that has it.</summary>
    public sealed record Rule(string Stat, int Ord, What When, string? Who, Check[] Checks, bool Any, Value Chance, Value? Cooldown, EffectSpec[] Do)
    {
        public string? Carrier { get; init; }
        /// <summary>How many uses the cooldown holds: each comes back one cooldown after the one before. 1 when the row
        /// does not say.</summary>
        public Value? Uses { get; init; }
        /// <summary>The deepest event (CombatEvent.Depth) the rule's chaining effects run for (LoopGuard).</summary>
        public int MaxDepth { get; init; }
        public bool Chains { get; init; }
        /// <summary>What the actor's own stats must be for the rule to be live ("need").</summary>
        public Need[] Needs { get; init; } = [];
        /// <summary>The id of <see cref="Stat"/>: what the lookups take.</summary>
        public uint Key { get; init; }
        /// <summary>The one carrier (Source.Name) the rule is for, when many carriers share its stat and each has its
        /// own trigger ("only").</summary>
        public string? Only { get; init; }
    }

    /// <summary>
    /// Something of an actor that carries stats of its own and can turn rules on for it: a skill an item grants today
    /// (later a buff, a minion, a ground patch). Key tells two of them apart and keeps a cooldown across stat builds;
    /// Get reads its stats; Data are its own named numbers a row's values may use; Thing is what an effect kind acts on.
    /// </summary>
    public sealed record Source(string Carrier, object Key, Func<uint, int> Get, IReadOnlyDictionary<string, double> Data,
        object Thing, string Label)
    {
        /// <summary>What the carrier is called in the game's data (a granted skill's GrantedEffects Id), for a rule
        /// that is for one carrier only ("only").</summary>
        public string? Name { get; init; }
    }

    /// <summary>One effect of a rule that fired, with its numbers resolved for this actor.</summary>
    public readonly record struct Fire(Player Conn, CombatEvent Event, double Now, Kind Kind, double[] Values, string?[] Names, object? Data)
    {
        /// <summary>The carrier whose stat turned the rule on, for a rule that has one.</summary>
        public Source? Source { get; init; }
        public double Number(string name) => Values[Array.IndexOf(Kind.Values, name)];
        public string? Word(string name) => Names[Array.IndexOf(Kind.Names, name)];
    }

    sealed record LiveEffect(Kind Kind, double[] Values, string?[] Names, object? Data);
    public sealed record Need(string Stat, uint Key, double? Min, double? Max);
    sealed record LiveRule(Rule Rule, double Chance, double Cooldown, LiveEffect[] Do, Source? Source = null, int Uses = 1);
    sealed class Live
    {
        public LiveRule[][] ByWhat = NoRules;
        // A rule's cooldown uses: how many are held and when the next comes back.
        public Dictionary<(Rule Rule, object? Source), (int Held, double NextAt)>? Cooldowns;

        // Takes one use at now, or says there is none. Uses come back one at a time, a cooldown apart.
        public bool TakeUse(LiveRule rule, double now)
        {
            var key = (rule.Rule, rule.Source?.Key);
            int uses = Math.Max(1, rule.Uses);
            var (held, nextAt) = (Cooldowns ??= []).TryGetValue(key, out var state) ? state : (uses, 0d);
            held = Math.Min(held, uses);
            while (held < uses && now >= nextAt) { held++; nextAt += rule.Cooldown; }
            if (held == 0) { Cooldowns[key] = (held, nextAt); return false; }
            if (held == uses) nextAt = now + rule.Cooldown;
            Cooldowns[key] = (held - 1, nextAt);
            return true;
        }
    }
    sealed record Table(int Revision, Rule[] Rules, HashSet<uint> Stats, Dictionary<string, Rule[]> ByCarrier);

    static readonly LiveRule[][] NoRules = [.. WhatNames.Select(_ => Array.Empty<LiveRule>())];
    static readonly RuleTable<Player, Live> Lives = new();
    static readonly Dictionary<string, Kind> Kinds = new(StringComparer.Ordinal);
    static readonly object Gate = new();
    static Table _table = new(-1, [], [], new());
    static readonly HashSet<string> KnownCarriers = new(StringComparer.Ordinal);

    /// <summary>The facts each event can answer, for a rule's checks.</summary>
    static readonly Dictionary<What, string[]> Facts = new()
    {
        [What.Hit] = ["critical", "spell", "attack", "melee", "bow", "killed", "frozen", "ignited", "bleeding", "marked", "has_mark", "normal", "magic", "rare", "unique", "rare_or_unique"],
        [What.Kill] = ["frozen", "ignited", "bleeding", "poisoned", "normal", "magic", "rare", "unique", "rare_or_unique"],
        [What.WhenHit] = ["critical"],
        [What.Block] = ["spell", "attack"],
        [What.Stun] = ["melee"],
        [What.SkillUse] = ["vaal", "curse", "trap", "attack", "spell", "melee", "bow", "travel", "movement", "warcry", "strike", "slam", "channelled", "brand", "aura", "guard"],
    };

    /// <summary>Adds an effect kind. The game module registers its own when it loads; a mod can add more.</summary>
    public static void Register(Kind kind)
    {
        lock (Gate) { Kinds[kind.Name] = kind; _table = new(-1, [], [], new()); }
    }

    /// <summary>Makes a carrier name known, so rows may use it ("carrier":"skill").</summary>
    public static void RegisterCarrier(string carrier)
    {
        lock (Gate) { KnownCarriers.Add(carrier); _table = new(-1, [], [], new()); }
    }

    /// <summary>Whether a loaded rule reads its stat from this kind of carrier: what the carrier's owner asks before it
    /// gathers its sources.</summary>
    public static bool HasCarrierRules(string carrier) => Enabled && Current().ByCarrier.ContainsKey(carrier);

    /// <summary>Whether a loaded rule of this carrier is turned on by one of these stats.</summary>
    public static bool CarrierRuleFor(string carrier, Func<uint, int> get, string? name = null)
    {
        if (!Enabled || !Current().ByCarrier.TryGetValue(carrier, out var rules)) return false;
        foreach (var rule in rules)
            if ((rule.Only is null || rule.Only == name) && get(rule.Key) != 0) return true;
        return false;
    }

    /// <summary>Changes whenever the loaded rules do (and with the on/off switch): what a carrier's owner keys a
    /// remembered answer about them by.</summary>
    public static int Revision => Enabled ? Current().Revision : -1;

    /// <summary>Whether an effect rule drives this stat now, so the code that read it before leaves it alone.</summary>
    public static bool Handles(uint stat) => Enabled && Current().Stats.Contains(stat);

    /// <summary>A stat lookup that reads 0 for every stat a rule drives: what the replaced code is given.</summary>
    public static Func<uint, int> Unhandled(Func<uint, int> get)
    {
        if (!Enabled) return get;
        var stats = Current().Stats;
        return stats.Count == 0 ? get : key => stats.Contains(key) ? 0 : get(key);
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
            Volatile.Write(ref _table, new(revision, [.. rules], [.. rules.Where(r => r.Carrier is null).Select(r => r.Key)],
                rules.Where(r => r.Carrier is not null).GroupBy(r => r.Carrier!).ToDictionary(g => g.Key, g => g.ToArray())));
            return _table;
        }
    }

    static Rule Parse(string stat, int ord, string json)
    {
        if (Program.Stats.Key(stat) is not uint statKey) throw new FormatException("unknown stat");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        foreach (var property in root.EnumerateObject())
            if (property.Name is not ("when" or "who" or "carrier" or "checks" or "any" or "need" or "only" or "chance" or "cooldown" or "uses" or "depth" or "do"))
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
                    : kind.NameDefaults.TryGetValue(kind.Names[i], out string? word) ? word
                    : throw new FormatException($"the effect \"{kindName}\" needs \"{kind.Names[i]}\"");
                if (kind.NameOk is { } ok && !ok(kind.Names[i], names[i] ?? ""))
                    throw new FormatException($"the effect \"{kindName}\" does not know {kind.Names[i]} \"{names[i]}\"");
            }
            effects.Add(new(kind, values, names, kind.Prepare?.Invoke(effect)));
        }
        if (effects.Count == 0) throw new FormatException("\"do\" is empty");
        string? carrier = root.TryGetProperty("carrier", out var carried) ? carried.GetString() : null;
        if (carrier is not null && !KnownCarriers.Contains(carrier)) throw new FormatException($"unknown carrier \"{carrier}\"");
        var uses = root.TryGetProperty("uses", out var u) ? ParseValue(u) : null;
        if (carrier is null && (chance.Data ?? cooldown?.Data ?? uses?.Data ?? effects.SelectMany(e => e.Values).FirstOrDefault(v => v.Data is not null)?.Data) is { } data)
            throw new FormatException($"the value \"{data}\" is a carrier's, and the rule has no \"carrier\"");
        // What the actor itself must have for the rule to be live at all ("with at least 150 Strength"): stats of the
        // actor, read where its rules are worked out, so nothing is checked per event.
        var needs = new List<Need>();
        if (root.TryGetProperty("need", out var needed))
            foreach (var item in needed.EnumerateArray())
            {
                foreach (var property in item.EnumerateObject())
                    if (property.Name is not ("stat" or "min" or "max")) throw new FormatException($"unknown need key \"{property.Name}\"");
                string needStat = item.GetProperty("stat").GetString() ?? "";
                if (Program.Stats.Key(needStat) is not uint needKey) throw new FormatException($"unknown stat \"{needStat}\"");
                needs.Add(new(needStat, needKey, item.TryGetProperty("min", out var low) ? low.GetDouble() : null,
                    item.TryGetProperty("max", out var high) ? high.GetDouble() : null));
            }
        int depth = root.TryGetProperty("depth", out var deep) ? deep.GetInt32() : 0;
        if (depth < 0) throw new FormatException("\"depth\" is negative");
        return new(stat, ord, (What)when, who, [.. checks], any, chance, cooldown, [.. effects])
        { Key = statKey, Carrier = carrier, Uses = uses, MaxDepth = depth, Chains = effects.Exists(e => e.Kind.Chains), Needs = [.. needs],
          Only = root.TryGetProperty("only", out var only) ? carrier is not null ? only.GetString() : throw new FormatException("\"only\" names a carrier, and the rule has no \"carrier\"") : null };
    }

    static Value ParseValue(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number) return new(e.GetDouble(), null, false, 1, 0, null, null);
        double? Number(string name) => e.TryGetProperty(name, out var n) ? n.GetDouble() : null;
        foreach (var property in e.EnumerateObject())
            if (property.Name is not ("fixed" or "from" or "stat" or "data" or "scale" or "fallback" or "min" or "max"))
                throw new FormatException($"unknown value key \"{property.Name}\"");
        bool own = e.TryGetProperty("from", out var from)
            && (from.GetString() == "stat" ? true : throw new FormatException($"unknown value source \"{from.GetString()}\""));
        string? stat = e.TryGetProperty("stat", out var s) ? s.GetString() : null;
        double? value = Number("fixed");
        string? data = e.TryGetProperty("data", out var d) ? d.GetString() : null;
        if ((own ? 1 : 0) + (stat is not null ? 1 : 0) + (value is not null ? 1 : 0) + (data is not null ? 1 : 0) != 1)
            throw new FormatException("a value needs exactly one of \"fixed\", \"from\", \"stat\" and \"data\"");
        uint key = 0;
        if (stat is not null && Program.Stats.Key(stat) is not uint known) throw new FormatException($"unknown stat \"{stat}\"");
        else if (stat is not null) key = Program.Stats.Key(stat)!.Value;
        return new(value ?? 0, stat, own, Number("scale") ?? 1, Number("fallback") ?? 0, Number("min"), Number("max")) { Data = data, StatKey = key };
    }

    /// <summary>Works out the actor's live rules from its stats, and from the stats of its carriers
    /// (<paramref name="sources"/>) for the rules that name one. Called where the player stats are built.</summary>
    public static void Configure(Player player, Func<uint, int> get, IReadOnlyList<Source>? sources = null)
    {
        var table = Enabled ? Current() : null;
        List<LiveRule>?[]? lists = null;
        void Add(Rule rule, int own, Source? source)
        {
            // A carrier's own stats come first for a value that names a stat; the actor's are behind them.
            Func<uint, int> read = source is null ? get : key => source.Get(key) is var held and not 0 ? held : get(key);
            double chance = rule.Chance.Resolve(own, read, source?.Data);
            if (chance <= 0) return;
            foreach (var need in rule.Needs)
                if (get(need.Key) is var has && (has < need.Min || has > need.Max)) return;
            var effects = new LiveEffect[rule.Do.Length];
            for (int i = 0; i < effects.Length; i++)
            {
                var spec = rule.Do[i];
                var numbers = new double[spec.Values.Length];
                for (int n = 0; n < numbers.Length; n++) numbers[n] = spec.Values[n].Resolve(own, read, source?.Data);
                effects[i] = new(spec.Kind, numbers, spec.Names, spec.Data);
            }
            lists ??= new List<LiveRule>?[WhatNames.Length];
            (lists[(int)rule.When] ??= []).Add(new(rule, chance, rule.Cooldown?.Resolve(own, read, source?.Data) ?? 0, effects, source,
                (int)Math.Round(rule.Uses?.Resolve(own, read, source?.Data) ?? 1)));
        }
        if (table is not null)
            foreach (var rule in table.Rules)
            {
                if (rule.Carrier is null)
                {
                    if (get(rule.Key) is var own and not 0) Add(rule, own, null);
                }
                else if (sources is not null)
                    foreach (var source in sources)
                        if (source.Carrier == rule.Carrier && (rule.Only is null || rule.Only == source.Name)
                            && source.Get(rule.Key) is var held and not 0) Add(rule, held, source);
            }
        // No live rule and nothing kept from before: the usual case, and it makes nothing.
        if (lists is null && !Lives.TryGetValue(player, out _)) return;
        var live = Lives.GetOrCreateValue(player);
        Volatile.Write(ref live.ByWhat, lists is null ? NoRules : [.. lists.Select(l => l?.ToArray() ?? [])]);
    }

    /// <summary>Whether the actor has a live rule for this event: what a per-hit caller asks before it builds one.</summary>
    public static bool Listens(Player player, What what) =>
        Enabled && Lives.TryGetValue(player, out var live) && Volatile.Read(ref live.ByWhat)[(int)what].Length > 0;

    /// <summary>Runs the actor's live rules for a combat event of its side. A hit is <see cref="HitLanded"/>, raised
    /// where every hit passes; the charge system's own hit record (ChargeEvent.Hit) is not an event here.</summary>
    public static void Run(Player player, CombatEvent e)
    {
        if (!Enabled || !Lives.TryGetValue(player, out var live)) return;
        int what = e switch
        {
            HitLanded => 0, ChargeEvent.Kill => 1, ChargeEvent.WhenHit => 2, ChargeEvent.Block => 3,
            ChargeEvent.Stun => 4, ChargeEvent.SkillUse => 5, _ => -1,
        };
        if (what < 0) return;
        var rules = Volatile.Read(ref live.ByWhat)[what];
        if (rules.Length == 0) return;
        double now = Game.AreaTime(player);
        foreach (var rule in rules)
        {
            if (rule.Rule.Who is { } who && who != e.Who) continue;
            if (rule.Rule.Chains && e.Depth > rule.Rule.MaxDepth && LoopGuard) continue;
            if (!Passes(rule.Rule, e, player)) continue;
            if (rule.Chance < 100 && Game.AreaRng(player).NextDouble() * 100 >= rule.Chance) continue;
            if (rule.Cooldown > 0)
                lock (live)
                    if (!live.TakeUse(rule, now)) continue;
            foreach (var effect in rule.Do)
                try { effect.Kind.Run(new(player, e, now, effect.Kind, effect.Values, effect.Names, effect.Data) { Source = rule.Source }); }
                catch (Exception ex) { Console.WriteLine($"[rules] {rule.Rule.Stat}: the effect {effect.Kind.Name} failed: {ex.Message}"); }
            if (Fired.Any)
            {
                string text = $"{(rule.Source is { } from ? from.Label + ": " : "")}{rule.Rule.Stat} on {WhatNames[what]}: {Describe(rule)}";
                foreach (var (owner, run) in Fired.Subscribers(player))
                    try { run(player, text); }
                    catch (Exception ex) { Console.WriteLine($"[events] {owner}: EffectRules.Fired failed: {ex.Message}"); }
            }
        }
    }

    static bool Passes(Rule rule, CombatEvent e, Player player)
    {
        if (rule.Checks.Length == 0) return true;
        foreach (var check in rule.Checks)
        {
            bool holds = Fact(check.Fact, e, player) != check.Not;
            if (holds && rule.Any) return true;
            if (!holds && !rule.Any) return false;
        }
        return !rule.Any;
    }

    // A hit's facts about the monster are read from the monster when a rule asks (the hit is raised inside the
    // Monsters lock): nothing is worked out for a hit no rule checks.
    static bool Fact(string fact, CombatEvent e, Player player) => (fact, e) switch
    {
        ("critical", HitLanded h) => h.Critical,
        ("critical", ChargeEvent.WhenHit w) => w.Critical,
        ("spell", HitLanded h) => h.Spell,
        ("spell", ChargeEvent.Block b) => b.Spell,
        ("attack", HitLanded h) => !h.Spell,
        ("attack", ChargeEvent.Block b) => !b.Spell,
        ("killed", HitLanded h) => h.Killed,
        ("frozen", HitLanded { Monster: { } m }) => Game.MonsterFrozenNow(player, m),
        ("frozen", ChargeEvent.Kill k) => k.Frozen,
        ("bleeding", HitLanded { Monster: { } m }) => m.Bleeds.Burning,
        ("ignited", HitLanded { Monster: { } m }) => m.Ignites.Burning,
        // A kill is read after the death, which ends the monster's ignites and bleeds: they keep that it died with one.
        ("ignited", ChargeEvent.Kill { Monster: { } m }) => DiedWith(player, m.Ignites),
        ("bleeding", ChargeEvent.Kill { Monster: { } m }) => DiedWith(player, m.Bleeds),
        ("marked", HitLanded { Monster: { } m }) => Game.MarkedBy(player, m),
        ("has_mark", HitLanded) => Game.HasMarkedEnemy(player),
        ("poisoned", ChargeEvent.Kill k) => k.Poisons > 0,
        ("melee", ChargeEvent.Stun s) => s.Melee,
        ("melee", HitLanded h) => Game.MeleeHit(player, h),
        ("bow", HitLanded h) => !h.Spell && Game.BowWorn(player),
        ("vaal", ChargeEvent.SkillUse u) => u.Vaal,
        (_, ChargeEvent.SkillUse { Effect: >= 0 } u) when fact is not ("curse" or "trap") => UseFact(fact, u.Effect, player),
        ("curse", ChargeEvent.SkillUse u) => u.Curse,
        ("trap", ChargeEvent.SkillUse u) => u.Trap,
        (_, HitLanded { Monster: { } m }) => Rarity(fact, m.Rarity),
        (_, ChargeEvent.Kill k) => Rarity(fact, k.Rarity),
        _ => false,
    };

    // What kind of skill was used: the catalog's own skill types.
    static bool UseFact(string fact, int effect, Player player)
    {
        var skills = Game.SkillsFor(player);
        return fact switch
        {
            "attack" => skills.IsAttack(effect),
            "spell" => skills.IsSpell(effect),
            "melee" => skills.IsMelee(effect),
            "bow" => skills.IsAttack(effect) && Game.BowWorn(player),
            "travel" => skills.IsTravel(effect),
            "movement" => skills.IsMovement(effect),
            "warcry" => skills.IsWarcry(effect),
            "strike" => skills.IsStrike(effect),
            "slam" => skills.HasType(effect, "Slam"),
            "channelled" => skills.IsChannelled(effect),
            "brand" => skills.IsBrand(effect),
            "aura" => skills.IsAura(effect),
            "guard" => skills.IsGuard(effect),
            _ => false,
        };
    }

    static bool DiedWith(Player player, MonsterIgnites burns)
    {
        return burns.Burning || burns.DiedBurning;
    }

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
        + (rule.Cooldown > 0 ? $"cooldown {rule.Cooldown:0.###} s{(rule.Uses > 1 ? $" x {rule.Uses} uses" : "")}, " : "")
        + string.Join(" + ", rule.Do.Select(d => d.Kind.Name
            + string.Concat(d.Kind.Names.Select((n, i) => $" {n}={d.Names[i]}"))
            + (d.Data is { } data ? $" {data}" : "")
            + string.Concat(d.Kind.Values.Select((n, i) => $" {n}={d.Values[i]:0.###}"))));

    /// <summary>The actor's live rules as text, one line each: for tools that show them.</summary>
    public static IReadOnlyList<string> Describe(Player player)
    {
        if (!Lives.TryGetValue(player, out var live)) return [];
        var lines = new List<string>();
        var byWhat = Volatile.Read(ref live.ByWhat);
        for (int what = 0; what < byWhat.Length; what++)
            foreach (var rule in byWhat[what])
                lines.Add($"{(rule.Source is { } from ? from.Label + ": " : "")}{rule.Rule.Stat} on {WhatNames[what]}"
                    + (rule.Rule.Checks.Length > 0 ? $" if {string.Join(rule.Rule.Any ? " or " : " and ", rule.Rule.Checks.Select(c => (c.Not ? "not " : "") + c.Fact))}" : "")
                    + $": {Describe(rule)}");
        return lines;
    }

    /// <summary>How many rules are loaded and how many effect kinds there are.</summary>
    public static (int Rules, int Kinds) Counts() { var table = Current(); lock (Gate) return (table.Rules.Length, Kinds.Count); }
}
