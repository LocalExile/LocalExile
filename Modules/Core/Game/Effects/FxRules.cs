using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Storage;
using GameServer.Core;

/// <summary>
/// Turns the fx rule tables (fx_rules, fx_rule_sources, fx_rule_when, fx_rule_if, fx_rule_do) into rules of the engine
/// (<see cref="EffectRules"/>). It runs when the game data loads, never during play: what comes out is the same rule
/// object a row of effect_rules gives, so a rule from the tables costs an event exactly what one from JSON does.
/// A row the engine cannot carry out yet is refused with the reason (a part with no code, groups it cannot combine).
/// Agents/docs/dev/effect-rules.md, "The fx tables".
/// </summary>
public static class FxRules
{
    /// <summary>One engine rule from the tables: the stat that turns it on, its place among that stat's rules, and
    /// the rule as the engine reads it.</summary>
    public sealed record Compiled(string Rule, string Stat, int Ord, string Json);

    static readonly Dictionary<string, string> Triggers = new(StringComparer.Ordinal)
    {
        ["hit"] = "hit", ["kill"] = "kill", ["when_hit"] = "when_hit", ["block"] = "block", ["stun"] = "stun",
        ["skill_use"] = "skill_use", ["active"] = "worn", ["skill_hit"] = "skill_hit",
    };

    /// <summary>Every enabled fx rule the engine can carry out, one entry per stat that turns it on.
    /// <paramref name="refused"/> is told each rule left out, and why.</summary>
    public static List<Compiled> Compile(IReadOnlyList<GameDataStore.FxRuleRow> rows, Action<string, string>? refused = null)
    {
        var result = new List<Compiled>();
        var ords = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Status == "not built") continue;
            // A rule on a timer is not an event rule: the system that keeps the timers reads it (Periodics).
            if (row.When.Count == 1 && row.When[0].Trigger == "interval") continue;
            // A rule of the area's is not an actor's rule: the area gives it where its monsters are made and where its
            // players' stats are built (AreaGrants).
            if (row.Sources.Any(s => s.Kind == "area")) continue;
            try
            {
                var rule = Engine(row);
                // A summed chance is one rule for all its stats (its sources), not one for each.
                bool summed = rule["chance"] is JsonObject chance && chance.ContainsKey("sum");
                foreach (var (kind, key) in summed ? row.Sources.Take(1) : row.Sources)
                {
                    if (kind is not ("stat" or "skill" or "item")) throw new FormatException($"a source of kind \"{kind}\" has no code yet");
                    var json = (JsonObject)rule.DeepClone();
                    if (kind == "skill") json["carrier"] = "skill";
                    else if (json.ContainsKey("only")) throw new FormatException("\"source_is\" names a skill, and the source is not a skill's stat");
                    // An "item" source: a stat that belongs to its item (Stats IsLocal: "Nearby Enemies have -9% to
                    // Fire Resistance" is one). It never reaches the character's own stats, so the worn item is the
                    // rule's carrier, and the rule is live once for each worn item that has the stat.
                    else if (kind == "item")
                        json["carrier"] = LocalStat(key) ? Game.ItemCarrier
                            : throw new FormatException($"an \"item\" source reads the item's own (local) stats, and \"{key}\" is the character's: its kind is \"stat\"");
                    int ord = ords.GetValueOrDefault(key);
                    ords[key] = ord + 1;
                    result.Add(new(row.Id, key, ord, json.ToJsonString()));
                }
                if (row.Sources.Count == 0) throw new FormatException("no source");
            }
            catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException or KeyNotFoundException)
            {
                refused?.Invoke(row.Id, e.Message);
            }
        }
        return result;
    }

    /// <summary>The "interval" rules that give or take charges, as the charge system's own timers read them
    /// (ChargeGains.Periodic): the stat, the charge type (null: a random one each), the one condition the timers know,
    /// and the period and count worked out from the stat's value. <paramref name="refused"/> is told each one left out.</summary>
    public static List<ChargeGains.Periodic> Periodics(IReadOnlyList<GameDataStore.FxRuleRow> rows, Action<string, string>? refused = null)
    {
        var result = new List<ChargeGains.Periodic>();
        foreach (var row in rows)
        {
            if (row.Status == "not built" || row.When.Count != 1 || row.When[0].Trigger != "interval") continue;
            try
            {
                if (row.Sources.Count != 1 || row.Sources[0].Kind != "stat") throw new FormatException("a timed rule is turned on by one stat");
                uint stat = Program.Stats.Key(row.Sources[0].Key) ?? throw new FormatException($"unknown stat \"{row.Sources[0].Key}\"");
                if (row.Do.Count != 1 || row.Do[0].Action != "charge" || row.Do[0].Target != "self")
                    throw new FormatException("a timed rule gives or takes the actor's charges only so far");
                // A number of the rule: fixed, or the stat's own value times a scale.
                static Func<int, double> Number(JsonNode? node, string what)
                {
                    if (node is JsonValue plain) { double n = plain.GetValue<double>(); return _ => n; }
                    if (node is not JsonObject written) throw new FormatException($"a timed rule needs {what}");
                    if (written["fixed"] is { } given) { double n = given.GetValue<double>(); return _ => n; }
                    if (written["from"]?.GetValue<string>() != "roll") throw new FormatException($"{what} is fixed or the stat's own value (\"from\":\"roll\")");
                    double scale = written["scale"]?.GetValue<double>() ?? 1;
                    return value => value * scale;
                }
                var seconds = Number(Object(row.When[0].Args)["seconds"], "\"seconds\"");
                var with = Object(row.Do[0].Args);
                string word = with["type"]?.GetValue<string>() ?? throw new FormatException("the action \"charge\" needs \"type\"");
                ChargeType? type = word == "random" ? null
                    : Enum.TryParse(word, ignoreCase: true, out ChargeType known) ? known : throw new FormatException($"unknown charge type \"{word}\"");
                var count = with["does"]?.GetValue<string>() switch
                {
                    null or "gain" => Number(JsonNode.Parse(row.Do[0].Value), "a count"),
                    "lose_one" => _ => -1,
                    var other => throw new FormatException($"a timed rule cannot \"{other}\" yet"),
                };
                var condition = ChargeGains.PeriodicCondition.Always;
                if (row.If.Count > 1) throw new FormatException("a timed rule has one condition at most so far");
                if (row.If.Count == 1)
                {
                    var (_, name, negate, text) = row.If[0];
                    var asked = Object(text);
                    condition = (name, asked["is"]?.GetValue<string>() ?? asked["event"]?.GetValue<string>(), negate) switch
                    {
                        ("recently", "been_hit", false) => ChargeGains.PeriodicCondition.HitRecently,
                        ("self_state", "stationary", false) => ChargeGains.PeriodicCondition.Stationary,
                        ("self_state", "moving", false) => ChargeGains.PeriodicCondition.Moving,
                        ("recently", "detonated_mines", true) => ChargeGains.PeriodicCondition.NoMineDetonatedRecently,
                        ("recently", "lost_charge", true) => ChargeGains.PeriodicCondition.NotLostRecently,
                        _ => throw new FormatException($"the condition \"{name}\" has no code yet for a timed rule"),
                    };
                }
                result.Add(new(stat, type, condition, value => (seconds(value), (int)Math.Round(count(value)))));
            }
            catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException or KeyNotFoundException)
            {
                refused?.Invoke(row.Id, e.Message);
            }
        }
        return result;
    }

    /// <summary>One thing an area gives because of a stat of its own (a map modifier): a bundle for its monsters or for
    /// its players. The bundle's roll and effect are a fixed number plus so much for each point of the area's stat.</summary>
    public sealed record AreaGrant(string Rule, uint Stat, Game.StatBundle Bundle, bool Players,
        double Roll, double RollPerPoint, double Effect, double EffectPerPoint);

    /// <summary>
    /// The rules whose source is the area (kind "area", key: a stat the area has, a map modifier's): active, with one
    /// "grant" to enemies_area (the area's monsters) or players_area (its players). Read when the game data loads.
    /// </summary>
    public static List<AreaGrant> AreaGrants(IReadOnlyList<GameDataStore.FxRuleRow> rows, Action<string, string>? refused = null)
    {
        var result = new List<AreaGrant>();
        foreach (var row in rows)
        {
            if (row.Status == "not built" || !row.Sources.Any(s => s.Kind == "area")) continue;
            try
            {
                if (row.Sources.Any(s => s.Kind != "area")) throw new FormatException("an area's rule has only \"area\" sources");
                if (row.When.Count != 1 || row.When[0].Trigger != "active") throw new FormatException("an area's rule is \"active\": it holds while the area has the stat");
                if (row.If.Count > 0) throw new FormatException("an area's rule has no conditions yet");
                if (row.Do.Count != 1 || row.Do[0].Action != "grant") throw new FormatException("an area's rule does one \"grant\" so far");
                var (_, text, target, _, _, _) = row.Do[0];
                if (target is not ("enemies_area" or "players_area")) throw new FormatException($"an area gives to \"enemies_area\" or \"players_area\", not to \"{target}\"");
                var with = Object(text);
                var bundle = Game.BundleOf(JsonDocument.Parse((with["bundle"] ?? throw new FormatException("the action \"grant\" needs \"bundle\"")).ToJsonString()).RootElement);
                if (bundle.OfLine) throw new FormatException("an area has no line: name the template");
                // A number: fixed, or the area's own value for the stat ({"from":"roll"}) times a scale.
                (double Fixed, double PerPoint) Number(string name, double byDefault)
                {
                    if (with[name] is not { } node) return (byDefault, 0);
                    if (node is not JsonObject written) return (node.GetValue<double>(), 0);
                    if (written["fixed"] is { } value) return (value.GetValue<double>(), 0);
                    if (written["from"]?.GetValue<string>() is "roll" or "stat") return (0, written["scale"]?.GetValue<double>() ?? 1);
                    throw new FormatException($"the \"{name}\" of an area's grant is a number or {{\"from\":\"roll\"}}");
                }
                var (roll, rollPer) = Number("roll", 100);
                var (effect, effectPer) = Number("effect", 100);
                foreach (var (_, key) in row.Sources)
                    result.Add(new(row.Id, Program.Stats.Key(key) ?? throw new FormatException($"unknown stat \"{key}\""), bundle,
                        target == "players_area", roll, rollPer, effect, effectPer));
            }
            catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException or KeyNotFoundException)
            {
                refused?.Invoke(row.Id, e.Message);
            }
        }
        return result;
    }

    static HashSet<string>? _localStats;
    static bool LocalStat(string stat) =>
        (_localStats ??= [.. ClientTables.Table("Stats").Where(r => r.Bool("IsLocal")).Select(r => r.Text("Id") ?? "")]).Contains(stat);

    // The rule without its source, in the engine's own form (EffectRules.Parse reads it).
    static JsonObject Engine(GameDataStore.FxRuleRow row)
    {
        if (row.When.Count != 1) throw new FormatException(row.When.Count == 0 ? "no trigger" : "several triggers have no code yet");
        var (trigger, whenArgs) = row.When[0];
        if (!Triggers.TryGetValue(trigger, out string? when)) throw new FormatException($"the trigger \"{trigger}\" has no code yet");
        var rule = new JsonObject { ["when"] = when };
        var args = Object(whenArgs);
        foreach (var (name, value) in args)
            if (name is "who" or "depth") rule[name] = value?.DeepClone();
            else throw new FormatException($"the trigger \"{trigger}\" takes no \"{name}\"");

        // Conditions: the event's facts (all of one group, or one each of several groups), and the ones worked out
        // once for the actor (a chance, a cooldown, a needed stat, the one skill the rule is for).
        var facts = new SortedDictionary<int, List<string>>();
        var needs = new JsonArray();
        foreach (var (group, condition, negate, text) in row.If)
        {
            var with = Object(text);
            string? Word(string name) => with.TryGetPropertyValue(name, out var node) ? node?.GetValue<string>() : null;
            string Fact(string fact) => negate ? "!" + fact : fact;
            void Whole(string what) { if (negate) throw new FormatException($"\"{what}\" cannot be negated"); }
            switch (condition)
            {
                case "chance":
                    Whole(condition);
                    // {"from":"sum","of":[{"stat":...,"when":"attack"}, ...],"skill":true}: several stats, one roll.
                    if (with["percent"] is JsonObject { } percent && percent["from"]?.GetValue<string>() == "sum")
                    {
                        var terms = new JsonArray();
                        foreach (var term in percent["of"]?.AsArray() ?? throw new FormatException("a summed chance needs \"of\""))
                        {
                            var one = new JsonObject { ["stat"] = term?["stat"]?.DeepClone() ?? throw new FormatException("a summed chance's term needs \"stat\"") };
                            if (term["when"] is { } only) one["if"] = only.DeepClone();
                            terms.Add(one);
                        }
                        var summed = new JsonObject { ["sum"] = terms };
                        if (percent["skill"] is { } adds) summed["skill"] = adds.DeepClone();
                        rule["chance"] = summed;
                    }
                    else rule["chance"] = Value(with["percent"]);
                    break;
                case "cooldown":
                    Whole(condition);
                    rule["cooldown"] = Value(with["seconds"]);
                    if (with.ContainsKey("uses")) rule["uses"] = Value(with["uses"]);
                    break;
                case "source_is": Whole(condition); rule["only"] = Word("name") ?? throw new FormatException("\"source_is\" needs \"name\""); break;
                case "stat_at_least":
                    Whole(condition);
                    var need = new JsonObject { ["stat"] = Word("stat") ?? throw new FormatException("\"stat_at_least\" needs \"stat\"") };
                    if (with.ContainsKey("min")) need["min"] = with["min"]!.DeepClone();
                    if (with.ContainsKey("max")) need["max"] = with["max"]!.DeepClone();
                    needs.Add(need);
                    break;
                case "critical" or "has_mark": Add(facts, group, Fact(condition)); break;
                case "enemy_rarity" or "enemy_state" or "skill_type":
                    Add(facts, group, Fact(Word("is") ?? throw new FormatException($"\"{condition}\" needs \"is\"")));
                    break;
                case "damage_type":
                    Add(facts, group, Fact("deals_" + (Word("is") ?? throw new FormatException("\"damage_type\" needs \"is\""))));
                    break;
                case "self_state":
                    Add(facts, group, Fact("self_" + (Word("is") ?? throw new FormatException("\"self_state\" needs \"is\""))));
                    break;
                case "weapon":
                    Add(facts, group, Fact(Word("is") switch
                    {
                        "shield" => "shield", "dual wielding" => "dual_wield", "wand" => "wand", "claw or dagger" => "claw_or_dagger",
                        var other => throw new FormatException($"the weapon \"{other}\" has no code yet for a rule"),
                    }));
                    break;
                default: throw new FormatException($"the condition \"{condition}\" has no code yet");
            }
        }
        if (needs.Count > 0) rule["need"] = needs;
        if (facts.Count == 1) rule["checks"] = Array(facts.Values.First());
        else if (facts.Count > 1)
        {
            if (facts.Values.Any(group => group.Count != 1)) throw new FormatException("several groups of more than one condition have no code yet");
            rule["checks"] = Array(facts.Values.Select(group => group[0]));
            rule["any"] = true;
        }

        var effects = new JsonArray();
        foreach (var (action, text, target, targetArgs, value, timers) in row.Do)
        {
            var with = Object(text);
            var effect = new JsonObject();
            JsonNode? Duration()
            {
                foreach (var timer in JsonNode.Parse(timers)?.AsArray() ?? [])
                    if (timer?["timer"]?.GetValue<string>() == "duration") return Value(timer["seconds"]);
                return null;
            }
            switch (action)
            {
                case "skill":
                    if (with.Count != 1 || with["skill"]?.GetValue<string>() != "granted") throw new FormatException("the action \"skill\" releases the granted skill only so far: {\"skill\":\"granted\"}");
                    effect["kind"] = "skill";
                    effect["at"] = target switch { "enemy" => "monster", "self" => "self", _ => throw new FormatException($"the action \"skill\" cannot target \"{target}\" yet") };
                    break;
                case "buff" when with["buff"]?.GetValue<string>() == "onslaught":
                    if (target != "self") throw new FormatException("Onslaught is given to the actor itself");
                    effect["kind"] = "onslaught";
                    effect["seconds"] = Duration() ?? throw new FormatException("Onslaught needs a \"duration\" timer");
                    break;
                case "buff" when with["buff"]?.GetValue<string>() == "phasing":
                    if (target != "self") throw new FormatException("Phasing is given to the actor itself");
                    effect["kind"] = "phasing";
                    effect["seconds"] = Duration() ?? throw new FormatException("Phasing needs a \"duration\" timer");
                    break;
                case "buff" when with["buff"]?.GetValue<string>() == "arcane_surge":
                    if (target != "self") throw new FormatException("Arcane Surge is given to the actor itself");
                    effect["kind"] = "arcane_surge";
                    break;
                case "recover":
                    if (target != "self") throw new FormatException("the actor itself recovers or loses so far");
                    effect["kind"] = "recover";
                    effect["pool"] = (with["pool"]?.GetValue<string>() ?? throw new FormatException("the action \"recover\" needs \"pool\"")).Replace(' ', '_');
                    if (with.ContainsKey("does")) effect["does"] = with["does"]!.DeepClone();
                    // The value is flat, or a percent of the pool's maximum with "of": "maximum".
                    if (Value(JsonNode.Parse(value)) is { } recovered) effect[with["of"]?.GetValue<string>() == "maximum" ? "percent" : "amount"] = recovered;
                    break;
                case "raise":
                    if (target != "self") throw new FormatException("an event is recorded for the actor itself");
                    effect["kind"] = "remember";
                    effect["event"] = with["event"]?.DeepClone() ?? throw new FormatException("the action \"raise\" needs \"event\"");
                    if (Duration() is { } remembered) effect["seconds"] = remembered;
                    break;
                case "ailment":
                    if (target != "enemy") throw new FormatException("an ailment is put on the trigger's enemy");
                    effect["kind"] = "ailment";
                    effect["ailment"] = with["ailment"]?.DeepClone() ?? throw new FormatException("the action \"ailment\" needs \"ailment\"");
                    if (Duration() is { } lasts) effect["seconds"] = lasts;
                    if (Value(JsonNode.Parse(value)) is { } stated) effect["amount"] = stated;
                    break;
                case "curse":
                    if (target != "enemy") throw new FormatException("a curse is put on the trigger's enemy");
                    effect["kind"] = "curse";
                    effect["curse"] = with["curse"]?.DeepClone() ?? throw new FormatException("the action \"curse\" needs \"curse\"");
                    if (with.ContainsKey("refresh_after")) effect["refresh_after"] = with["refresh_after"]!.DeepClone();
                    if (Value(JsonNode.Parse(value)) is { } level) effect["level"] = level;
                    break;
                case "charge" when with["type"]?.GetValue<string>() == "spirit":
                    if (target != "self") throw new FormatException("charges are given to the actor itself");
                    effect["kind"] = "spirit_charge";
                    break;
                case "buff":
                    if (target != "self") throw new FormatException("a buff is given to the actor itself so far");
                    effect["kind"] = "buff";
                    foreach (var (name, node) in with) effect[name] = name == "effect" ? Value(node) : node?.DeepClone();
                    effect["seconds"] = Duration() ?? throw new FormatException("a buff needs a \"duration\" timer");
                    break;
                case "quantity":
                    if (target != "self" || !with.ContainsKey("stats")) throw new FormatException("the action \"quantity\" gives the actor \"stats\" while the rule is active so far");
                    effect["kind"] = "stats";
                    effect["stats"] = with["stats"]!.DeepClone();
                    // The value, when there is one, is a percent of the listed numbers (the line's roll can be it).
                    if (Value(JsonNode.Parse(value)) is { } percent) effect["percent"] = percent;
                    break;
                case "grant":
                    // A bundle of stats for other actors. Standing ("active") for the enemies of the area or within a
                    // radius; for the "duration" timer on the enemy of an event.
                    effect["kind"] = "grant";
                    effect["bundle"] = with["bundle"]?.DeepClone() ?? throw new FormatException("the action \"grant\" needs \"bundle\"");
                    foreach (string number in (string[])["roll", "effect"])
                        if (with.ContainsKey(number)) effect[number] = Value(with[number]) ?? throw new FormatException($"the action \"grant\" has an empty \"{number}\"");
                    foreach (var (name, _) in with)
                        if (name is not ("bundle" or "roll" or "effect")) throw new FormatException($"the action \"grant\" has no \"{name}\"");
                    effect["to"] = target;
                    // The line's own rolled value, for a bundle stat that takes it.
                    effect["own"] = new JsonObject { ["from"] = "stat" };
                    switch (target)
                    {
                        case "enemies_area" or "enemies_near" or "players_area" when when != "worn":
                            throw new FormatException($"a grant to \"{target}\" stands while its rule does: its trigger is \"active\"");
                        case "enemies_area" or "players_area": break;
                        case "enemies_near":
                            // The reach: the row's own, or the bundle's (a template says how far its aura goes).
                            if (Value(Object(targetArgs)["radius"]) is { } reach) effect["radius"] = reach;
                            else if (with["bundle"]?["template"] is null) throw new FormatException("a grant to \"enemies_near\" needs a \"radius\" in its target_args, or a template that has one");
                            break;
                        case "enemy" when when == "worn": throw new FormatException("a grant to \"enemy\" is for an event: an active rule has no enemy");
                        case "enemy": effect["seconds"] = Duration() ?? throw new FormatException("a grant to the event's enemy needs a \"duration\" timer"); break;
                        case "self" when when == "worn": break;
                        case "self": effect["seconds"] = Duration() ?? throw new FormatException("a grant to the actor from an event needs a \"duration\" timer"); break;
                        default: throw new FormatException($"the action \"grant\" cannot target \"{target}\" yet");
                    }
                    break;
                case "debuff":
                    if (target != "enemy") throw new FormatException("a debuff is put on the trigger's enemy");
                    effect["kind"] = "debuff";
                    effect["debuff"] = with["debuff"]?.DeepClone() ?? throw new FormatException("the action \"debuff\" needs \"debuff\"");
                    effect["seconds"] = Duration() ?? throw new FormatException("a debuff needs a \"duration\" timer");
                    // Its base magnitude, for a debuff that has one (an Exposure's resistance).
                    if (Value(JsonNode.Parse(value)) is { } amount) effect["amount"] = amount;
                    break;
                case "charge":
                    if (target != "self") throw new FormatException("charges are given to the actor itself");
                    effect["kind"] = "charge";
                    effect["type"] = with["type"]?.DeepClone() ?? throw new FormatException("the action \"charge\" needs \"type\"");
                    if (with.ContainsKey("does")) effect["does"] = with["does"]!.DeepClone();
                    effect["count"] = Value(JsonNode.Parse(value)) ?? 1;
                    break;
                default: throw new FormatException($"the action \"{action}\" has no code yet");
            }
            effects.Add(effect);
        }
        rule["do"] = effects;
        return rule;
    }

    // A value as the tables write it ({"from":"roll"}, {"from":"stat","stat":...}, {"from":"source","name":...},
    // {"fixed":n} or a bare number), as the engine reads it.
    static JsonNode? Value(JsonNode? node)
    {
        if (node is null) return null;
        if (node is not JsonObject written) return node.DeepClone();
        if (written.Count == 0) return null;
        var value = new JsonObject();
        string? from = written["from"]?.GetValue<string>();
        foreach (var (name, part) in written)
            switch (name)
            {
                case "from": break;
                case "stat" when from == "stat": value["stat"] = part?.DeepClone(); break;
                case "name" when from == "source": value["data"] = part?.DeepClone(); break;
                case "fixed" or "scale" or "fallback" or "min" or "max" or "skill": value[name] = part?.DeepClone(); break;
                default: throw new FormatException($"a value has no \"{name}\"" + (from is null ? "" : $" from \"{from}\""));
            }
        if (from == "roll") value["from"] = "stat";
        else if (from is not (null or "stat" or "source")) throw new FormatException($"a value from \"{from}\" has no code yet");
        return value;
    }

    static JsonObject Object(string text) => JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text) as JsonObject
        ?? throw new FormatException("inputs must be a JSON object");

    static JsonArray Array(IEnumerable<string> words)
    {
        var array = new JsonArray();
        foreach (string word in words) array.Add(word);
        return array;
    }

    static void Add(SortedDictionary<int, List<string>> groups, int group, string fact)
    {
        if (!groups.TryGetValue(group, out var list)) groups[group] = list = [];
        list.Add(fact);
    }
}
