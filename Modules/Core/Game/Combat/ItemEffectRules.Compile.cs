using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;

sealed partial class ItemEffectRules
{
    // Compile once per world snapshot. Combat only sees resolved stat keys and typed values.
    void Compile(GameDataStore.FxRuleRow row, ChargeRuleLists charges)
    {
        try
        {
            if (row.Status != "built" || row.Sources.Count == 0 || row.When.Count == 0 || row.Do.Count == 0)
                throw new FormatException("expected a built rule with a source, a trigger and an action");
            string trigger = row.When[0].Trigger;
            var (action, args, target, _, _, timers) = row.Do[0];
            bool chargeRule = (action, target) == ("charge", "self");
            if (row.Do.Count > 1 && !row.Do.All(d => (d.Action, d.Target) == ("charge", "self")))
                throw new FormatException("only charge rules may have several actions");
            foreach (var d in row.Do)
                if (!Matches(d.TargetArgs, "{}") || !Matches(d.Value, "{}"))
                    throw new FormatException("unsupported target or value arguments");
            var actors = new List<Actor>();
            foreach (var (eventName, eventArgs) in row.When)
            {
                if (eventName != trigger) throw new FormatException("expected one event kind per rule");
                Actor? actor = Matches(eventArgs, "{}") ? Actor.Player
                    : chargeRule && Matches(eventArgs, """{"who":"minion"}""") && trigger is "kill" or "hit" ? Actor.Minion
                    : chargeRule && Matches(eventArgs, """{"who":"monster"}""") && trigger is "hit" or "when_hit" ? Actor.Monster
                    : null;
                if (actor is not { } who || actors.Contains(who))
                    throw new FormatException($"unsupported or duplicate {trigger} trigger with args {eventArgs}");
                actors.Add(who);
            }
            if (row.Position is not null && !chargeRule) throw new FormatException($"unsupported position for {trigger} with {action}");
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
                case (_, "charge", "self") when ChargeEvents.Contains(trigger):
                    CompileCharges(row, trigger, actors, conditions, Take("chance", """{"percent":{"from":"roll"}}"""),
                        Sources("stat"), charges);
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

    static readonly HashSet<string> ChargeEvents = ["kill", "hit", "when_hit", "block", "stun", "skill_use"];

    // A charge rule: one rule per source stat and action, at the rule's position in its event for each actor. Without a
    // chance the stat is a flag: any value but 0 gains. The conditions are ANDed after the event's own type test.
    static void CompileCharges(GameDataStore.FxRuleRow row, string trigger, List<Actor> actors,
        Dictionary<string, (int Group, string Condition, bool Negate, string Args)> conditions, bool chance, uint[] sources,
        ChargeRuleLists charges)
    {
        if (row.Position is not long position) throw new FormatException("expected a position");
        bool player = actors.Contains(Actor.Player);
        static bool ActorKnows(string condition, string trigger) => (condition, trigger) is
            ("critical", "hit") or ("enemy_frozen_at_death", "kill") or ("enemy_poison_count_at_death", "kill");
        if (actors.Any(a => a != Actor.Player) && (!chance || conditions.Keys.Any(c => !ActorKnows(c, trigger))
                || actors.Contains(Actor.Monster) && conditions.Count != 0))
            throw new FormatException("a minion or monster charge rule needs a chance and supports only the conditions its actor knows");
        var checks = new List<Func<ChargeEvent, WeaponProfile, bool>>();
        foreach (var (name, (group, _, negate, args)) in conditions.ToArray())
        {
            if (group != 0) throw new FormatException($"unsupported {name} condition group");
            var check = ChargeCondition(trigger, name, JsonNode.Parse(args) as JsonObject ?? [], negate, player);
            checks.Add(negate ? (e, w) => !check(e, w) : check);
            conditions.Remove(name);
        }
        Func<ChargeEvent, bool> isEvent = trigger switch
        {
            "kill" => static e => e is ChargeEvent.Kill,
            "hit" => static e => e is ChargeEvent.Hit,
            "when_hit" => static e => e is ChargeEvent.WhenHit,
            "block" => static e => e is ChargeEvent.Block,
            "stun" => static e => e is ChargeEvent.Stun,
            _ => static e => e is ChargeEvent.SkillUse,
        };
        Func<ChargeEvent, WeaponProfile, bool>[] all = [.. checks];
        bool When(ChargeEvent e, WeaponProfile w)
        {
            if (!isEvent(e)) return false;
            foreach (var check in all)
                if (!check(e, w)) return false;
            return true;
        }
        var outcomes = row.Do.Select(d => ChargeOutcome(d.Args, d.Timers)).ToArray();
        if (actors.Any(a => a != Actor.Player) && outcomes.Any(o => o.Type is null || o.Does != ChargeGains.Outcome.Gain))
            throw new FormatException("a minion or monster charge rule gains one named charge");
        ChargeGains.Rule[] rules = [.. sources.SelectMany(stat => outcomes.Select(o => new ChargeGains.Rule(stat, o.Type, When, !chance, o.Does)))];
        foreach (var actor in actors)
        {
            foreach (uint stat in sources)
                if (!charges.Stats.Add((trigger, actor, stat)))
                    throw new FormatException($"{Stats.Name(stat)} already has a {trigger} charge rule");
            var byPosition = charges.ByEvent.TryGetValue((trigger, actor), out var known) ? known : charges.ByEvent[(trigger, actor)] = [];
            if (!byPosition.TryAdd(position, rules)) throw new FormatException($"position {position} is already taken for {trigger}");
        }
    }

    static (ChargeType? Type, ChargeGains.Outcome Does) ChargeOutcome(string args, string timers)
    {
        if (!Matches(timers, "[]") || JsonNode.Parse(args) is not JsonObject charge || charge.Count is < 1 or > 2
            || charge["charge"]?.GetValue<string>() is not string name)
            throw new FormatException("expected a named charge");
        ChargeType? type = name == "random" ? null : Charges.Parse(name) ?? throw new FormatException($"unknown charge '{name}'");
        var does = (charge.Count == 2 ? (string?)charge["does"] : "gain") switch
        {
            "gain" => ChargeGains.Outcome.Gain,
            "to_maximum" => ChargeGains.Outcome.ToMaximum,
            "lose_all" => ChargeGains.Outcome.LoseAll,
            "lose_one" => ChargeGains.Outcome.LoseOne,
            var other => throw new FormatException($"unknown charge outcome '{other}'"),
        };
        if (type is null && does != ChargeGains.Outcome.Gain) throw new FormatException("a random charge can only be gained");
        return (type, does);
    }

    // One condition of a charge rule, for the events whose facts can answer it.
    static Func<ChargeEvent, WeaponProfile, bool> ChargeCondition(string trigger, string name, JsonObject args, bool negate,
        bool player)
    {
        string? Is() => args.Count == 1 ? (string?)args["is"] : null;
        bool NoArgs() => args.Count == 0;
        Func<ChargeEvent, WeaponProfile, bool>? check = (trigger, name) switch
        {
            ("hit", "critical") when NoArgs() => static (e, _) => e is ChargeEvent.Hit { Critical: true },
            ("when_hit", "critical") when NoArgs() => static (e, _) => e is ChargeEvent.WhenHit { Critical: true },
            ("hit", "skill_type") when !negate => Is() switch
            {
                "melee" => static (e, w) => e is ChargeEvent.Hit h && ChargeGains.Melee(h, w),
                "attack" => static (e, _) => e is ChargeEvent.Hit { Spell: false },
                _ => null,
            },
            ("stun", "skill_type") when !negate && Is() == "melee" => static (e, _) => e is ChargeEvent.Stun { Melee: true },
            ("block", "skill_type") when !negate => Is() switch
            {
                "attack" => static (e, _) => e is ChargeEvent.Block { Spell: false },
                "spell" => static (e, _) => e is ChargeEvent.Block { Spell: true },
                _ => null,
            },
            ("skill_use", "skill_type") when !negate => Is() switch
            {
                "vaal" => static (e, _) => e is ChargeEvent.SkillUse { Vaal: true },
                "curse" => static (e, _) => e is ChargeEvent.SkillUse { Curse: true },
                "trap" => static (e, _) => e is ChargeEvent.SkillUse { Trap: true },
                _ => null,
            },
            ("hit" or "kill", "equipment") when !negate && player => Is() switch
            {
                "holding_shield" => static (_, w) => w.Shield,
                "dual_wielding" => static (_, w) => w.DualWield,
                "wand" => static (_, w) => w.Wand,
                "claw_or_dagger" => static (_, w) => w.Claw || w.Dagger,
                _ => null,
            },
            ("hit", "enemy_rarity") when !negate => Is() switch
            {
                "rare_or_unique" => static (e, _) => e is ChargeEvent.Hit { Rarity: MonsterRarity.Rare or MonsterRarity.Unique },
                "unique" => static (e, _) => e is ChargeEvent.Hit { Rarity: MonsterRarity.Unique },
                _ => null,
            },
            ("hit", "enemy_frozen") when !negate && NoArgs() => static (e, _) => e is ChargeEvent.Hit { Frozen: true },
            ("hit", "enemy_bleeding") when !negate && NoArgs() => static (e, _) => e is ChargeEvent.Hit { Bleeding: true },
            ("hit", "enemy_marked") when !negate && NoArgs() => static (e, _) => e is ChargeEvent.Hit { Marked: true },
            ("hit", "enemy_killed") when !negate && NoArgs() => static (e, _) => e is ChargeEvent.Hit { Killed: true },
            ("hit", "self_bleeding") when !negate && NoArgs() => static (e, _) => e is ChargeEvent.Hit { SelfBleeding: true },
            ("hit", "self_poisoned") when !negate && NoArgs() => static (e, _) => e is ChargeEvent.Hit { SelfPoisoned: true },
            ("hit", "primary_target") when !negate && NoArgs() => static (e, _) => e is ChargeEvent.Hit { Primary: true },
            ("hit", "used_skill") when !negate && args.Count == 1 && (string?)args["id"] is { Length: > 0 } id =>
                (e, _) => e is ChargeEvent.Hit h && h.SkillEffect == id,
            ("kill", "enemy_frozen_at_death") when !negate && NoArgs() => static (e, _) => e is ChargeEvent.Kill { Frozen: true },
            ("kill", "enemy_poison_count_at_death") when args.Count == 1 && args["at_least"] is JsonValue bound
                && bound.TryGetValue<int>(out int threshold) && threshold >= 0 =>
                (e, _) => e is ChargeEvent.Kill kill && kill.Poisons >= threshold,
            _ => null,
        };
        return check ?? throw new FormatException($"unsupported {name} condition for {trigger}");
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
