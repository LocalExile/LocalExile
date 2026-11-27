using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Storage;

sealed partial class ItemEffectRules
{
    // Compile once per world snapshot. Combat only sees resolved stat keys and typed values.
    void Compile(GameDataStore.FxRuleRow row, List<ChargeGains.Rule> beforeAdditional, List<ChargeGains.Rule> afterAdditional)
    {
        try
        {
            if (row.Status != "built" || row.Sources.Count == 0 || row.When.Count != 1 || row.Do.Count != 1)
                throw new FormatException("expected a built rule with a source, one trigger and one action");
            var (trigger, triggerArgs) = row.When[0];
            var (action, args, target, targetArgs, value, timers) = row.Do[0];
            if (!Matches(triggerArgs, "{}") || !Matches(targetArgs, "{}") || !Matches(value, "{}"))
                throw new FormatException("unsupported trigger, target or value arguments");
            var conditions = row.If.ToDictionary(c => c.Condition, StringComparer.Ordinal);
            bool Take(string name, string expected, bool negate = false)
            {
                if (!conditions.Remove(name, out var c)) return false;
                if (c.Group != 0 || c.Negate != negate || !Matches(c.Args, expected))
                    throw new FormatException($"unsupported {name} condition");
                return true;
            }
            uint[] Sources(string kind) => [.. row.Sources.Select(source => source.Kind == kind
                ? StatKey(source.Key) : throw new FormatException($"expected a {kind} source"))];

            switch (trigger, action, target)
            {
                case ("kill", "buff", "self") when Matches(args, """{"buff":"onslaught"}"""):
                    var stats = Sources("stat");
                    bool roll = Take("chance", """{"percent":{"from":"roll"}}""");
                    var (seconds, durationStat, fromRoll) = Duration(timers);
                    if (stats.Length > 1 && (roll || !fromRoll))
                        throw new FormatException("multiple duration sources require a guaranteed gain from their strongest roll");
                    onKill.Add(new(stats, roll, seconds, durationStat, fromRoll));
                    break;
                case ("kill", "charge", "self") when Matches(timers, "[]"):
                    if (JsonNode.Parse(args) is not JsonObject { Count: 1 } charge ||
                        charge["charge"]?.GetValue<string>() is not string name || Charges.Parse(name) is not { } type ||
                        !Take("chance", """{"percent":{"from":"roll"}}"""))
                        throw new FormatException("expected a named charge and chance from its source stat");
                    if (row.KillChargeOrder is null or 0)
                        throw new FormatException("expected a nonzero kill_charge_order");
                    bool frozen = Take("enemy_frozen_at_death", "{}");
                    bool shield = false, dualWield = false;
                    if (conditions.Remove("equipment", out var equipment))
                    {
                        if (equipment.Group != 0 || equipment.Negate) throw new FormatException("unsupported equipment condition");
                        shield = Matches(equipment.Args, """{"is":"holding_shield"}""");
                        dualWield = Matches(equipment.Args, """{"is":"dual_wielding"}""");
                        if (!shield && !dualWield) throw new FormatException("expected holding_shield or dual_wielding");
                    }
                    int minPoisons = 0, maxPoisons = int.MaxValue;
                    if (conditions.Remove("enemy_poison_count_at_death", out var poison))
                    {
                        if (poison.Group != 0 || JsonNode.Parse(poison.Args) is not JsonObject { Count: 1 } bounds ||
                            bounds["at_least"] is not JsonValue bound || !bound.TryGetValue<int>(out int threshold) || threshold < 0)
                            throw new FormatException("enemy_poison_count_at_death requires a nonnegative integer at_least");
                        if (poison.Negate) maxPoisons = threshold - 1;
                        else minPoisons = threshold;
                    }
                    bool Applies(ChargeEvent e, WeaponProfile w) => e is ChargeEvent.Kill kill
                        && kill.Poisons >= minPoisons && kill.Poisons <= maxPoisons
                        && (!frozen || kill.Frozen) && (!shield || w.Shield) && (!dualWield || w.DualWield);
                    var destination = row.KillChargeOrder < 0 ? beforeAdditional : afterAdditional;
                    foreach (uint stat in Sources("stat")) destination.Add(new(stat, type, Applies));
                    break;
                case ("hit", "skill", "enemy") when Matches(args, """{"skill":"granted"}""") && Matches(timers, "[]"):
                    bool rare = Take("enemy_rarity", """{"is":"rare_or_unique"}""");
                    bool unmarked = Take("has_mark", "{}", negate: true);
                    if (!Take("cooldown", """{"seconds":{"from":"source","name":"cooldown"}}"""))
                        throw new FormatException("granted marks require the skill's cooldown");
                    foreach (var stat in Sources("skill")) onHit.Add(new(stat, rare, unmarked));
                    break;
                case ("block", "skill", "attacker") when Matches(args, """{"skill":"supported_socketed"}""") && Matches(timers, "[]"):
                    foreach (var stat in Sources("skill")) blockSupportStats.Add(stat);
                    break;
                default:
                    throw new FormatException($"unsupported {trigger} trigger with {action} action on {target}, args {args}, timers {timers}");
            }
            if (conditions.Count != 0)
                throw new FormatException($"unsupported condition {conditions.Keys.First()} for {trigger}");
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException($"Invalid fx rule '{row.Id}': {ex.Message}", ex);
        }
    }

    static bool Matches(string json, string expected) => JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(expected));
    static uint StatKey(string name) => Program.Stats.Key(name) ?? throw new FormatException($"unknown stat '{name}'");

    static (double Seconds, uint? Stat, bool FromRoll) Duration(string json)
    {
        if (JsonNode.Parse(json) is not JsonArray { Count: 1 } timers || timers[0] is not JsonObject { Count: 2 } timer
            || (string?)timer["timer"] != "duration" || timer["seconds"] is not JsonObject seconds)
            throw new FormatException("expected one duration timer in seconds");
        if (seconds.Count == 1 && seconds["fixed"] is JsonValue fixedValue)
            return (PositiveSeconds(fixedValue), null, false);
        if ((double?)seconds["scale"] == 0.001)
        {
            if (seconds.Count == 2 && (string?)seconds["from"] == "roll") return (0, null, true);
            if (seconds.Count == 4 && (string?)seconds["from"] == "stat" && (string?)seconds["stat"] is string stat
                && seconds["fallback"] is JsonValue fallback) return (PositiveSeconds(fallback), StatKey(stat), false);
        }
        throw new FormatException("expected fixed seconds, roll milliseconds or stat milliseconds with fallback seconds");
    }

    static double PositiveSeconds(JsonValue value) =>
        value.TryGetValue<double>(out var seconds) && double.IsFinite(seconds) && seconds > 0
            ? seconds : throw new FormatException("duration must be a finite positive number");
}
