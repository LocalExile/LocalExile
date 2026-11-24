/// <summary>The game's own effect kinds for <see cref="EffectRules"/>: what a rule can do, each with its named
/// parameters. A new kind is one entry here (or a mod's <see cref="EffectRules.Register"/>), then any row can use it.</summary>
public static partial class Game
{
    static bool _effectKinds;

    /// <summary>Registers the kinds below, once. EffectRules calls this before it reads its rows.</summary>
    public static void EnsureEffectKinds()
    {
        if (_effectKinds) return;
        _effectKinds = true;

        // Onslaught for "seconds": starts it or lengthens it, never shortens the one running (KillOnslaught). A rule on
        // every hit presents only when the client would see a change.
        EffectRules.Register(new("onslaught", ["seconds"], [], fire =>
        {
            var player = fire.Conn;
            var onslaught = KillOnslaughtOf(player);
            if (player.PlayerLife == 0 || !onslaught.Extend(fire.Now, fire.Number("seconds"))) return;
            MarkPlayerStats(player, PlayerStatStep.Onslaught);
            if (onslaught.PresentationDue) PresentStats(player);
        }));

        // A buff for "seconds", by one of the client's BuffTemplates rows ("template") or by its BuffDefinitions Id
        // with a value per stat ("buff", "values"); "effect" scales its stats (percent, 100 = as given).
        EffectRules.Register(new("buff", ["seconds", "effect"], [], fire =>
            GainTimedBuffLocked(fire.Conn, (BuffSpec)fire.Data!, fire.Number("seconds"), fire.Number("effect") / 100, fire.Now))
        {
            Defaults = new Dictionary<string, double> { ["effect"] = 100 },
            Extra = ["template", "buff", "values"],
            Prepare = BuffSpecOf,
        });

        // Stats the actor has while a "worn" rule stands ("stats": stat name to value): a skill that is simply on while
        // its item is worn and whose whole effect is a stat the server already acts on (Blinding Aura: nearby enemies
        // are blinded). For "worn" rules only.
        // "percent": how much of each listed number is given (100 unless said). It is a value like any other, so it can
        // be the line's own roll: {"stats": {"x": 1}, "percent": {"from": "stat", "scale": 100}} gives x the roll.
        EffectRules.Register(new("stats", ["percent"], [], _ => { })
        {
            Defaults = new Dictionary<string, double> { ["percent"] = 100 },
            Extra = ["stats"],
            Prepare = effect =>
            {
                if (!effect.TryGetProperty("stats", out var listed) || listed.ValueKind != System.Text.Json.JsonValueKind.Object)
                    throw new FormatException("the effect \"stats\" needs \"stats\": stat name to value");
                List<(uint, int)> stats = [];
                foreach (var stat in listed.EnumerateObject())
                    stats.Add((Program.Stats.Key(stat.Name) ?? throw new FormatException($"unknown stat \"{stat.Name}\""), stat.Value.GetInt32()));
                return stats.ToArray();
            },
            Stand = standing =>
            {
                var listed = ((uint, int)[])standing.Data!;
                double percent = standing.Values[0];
                GainStandingStats(standing.Conn, percent == 100 ? listed
                    : [.. listed.Select(s => (s.Item1, ActorScale.Saturate((long)Math.Round(s.Item2 * percent / 100, MidpointRounding.AwayFromZero))))], standing.On);
            },
            StandOnly = true,
        });

        // A bundle of stats (Bundles.cs) for an actor, the rule's own or others (Grants.cs). "to": enemies_area,
        // enemies_near ("radius", in cells; the bundle's own reach when 0) or players_area for a "worn" rule, which
        // holds while the rule stands; enemy for an event's rule (the one hit, or the one that hit the actor), which
        // lasts "seconds"; self for either. "roll": where in its ranges the bundle sits, 0 to 100 (100,
        // the best roll, unless said); "effect": a percent of the whole bundle (100 unless said). Both are values, so
        // either can be the line's own roll: an item line that rolls 50 to 100 can give 50 to 100% of the bundle.
        // "own": the giving line's rolled value, for a bundle stat that takes it ({"from": "roll"}).
        EffectRules.Register(new("grant", ["roll", "effect", "radius", "seconds", "own"], ["to"], GrantOnEvent,
            (_, word) => word is "enemies_area" or "enemies_near" or "enemy" or "self" or "players_area")
        {
            Defaults = new Dictionary<string, double> { ["roll"] = 100, ["effect"] = 100, ["radius"] = 0, ["seconds"] = 0, ["own"] = 0 },
            Extra = ["bundle"],
            Prepare = effect => BundleOf(effect.TryGetProperty("bundle", out var bundle) ? bundle
                : throw new FormatException("the effect \"grant\" needs \"bundle\"")),
            Stand = StandGrant,
        });

        EnsureTriggeredSkills();

        // A debuff on the monster of the event (the one hit) for "seconds" (its base duration), timed from the hit as
        // its site times it; a later one restarts it, and Withered adds a stack. "amount": its base magnitude, for the
        // ones that have one (an Exposure's resistance). The names and what each asks of the monster and of the
        // hitting side's other stats: ApplyNamedDebuffLocked.
        EffectRules.Register(new("debuff", ["seconds", "amount"], ["debuff"], fire =>
        {
            if (fire.Event.Monster is not { } monster) return;
            // A hit of the actor's side carries its stats; so does a monster's hit that reached the actor (when hit,
            // on block), where the monster is the attacker.
            var hit = fire.Event as HitLanded;
            var taken = fire.Event as ChargeEvent;
            var (own, skill) = (hit?.PlayerStats ?? taken?.PlayerStats, hit?.SkillStats);
            ApplyNamedDebuffLocked(fire.Conn, monster, hit?.MonsterId ?? taken?.MonsterId ?? 0, fire.Word("debuff") ?? "",
                fire.Number("seconds"), fire.Number("amount"), hit?.At ?? taken?.HitAt ?? fire.Now,
                key => (own?.GetValueOrDefault(key) ?? 0) + (skill?.GetValueOrDefault(key) ?? 0));
        }, (_, word) => NamedDebuff(word))
        {
            Defaults = new Dictionary<string, double> { ["amount"] = 0 },
        });

        // A hex on the monster of the hit, as a cast of it at gem "level" would put it (curse limit, hexproof). "curse":
        // its GrantedEffects Id. "refresh_after": a hit within this many seconds of the last application leaves the
        // running curse alone (0: every hit applies it again).
        EffectRules.Register(new("curse", ["level", "refresh_after"], ["curse"], fire =>
        {
            if (fire.Event is HitLanded { Monster: { } target } hit && hit.PlayerStats is { } hitStats)
                CurseOnHitLocked(fire.Conn, target, fire.Word("curse") ?? "", (int)Math.Round(fire.Number("level")), hitStats,
                    hit.At ?? fire.Now, fire.Number("refresh_after"));
            // A monster's hit that reached the actor (when hit, on block): the attacker is cursed.
            else if (fire.Event is ChargeEvent { Monster: { } attacker } taken and (ChargeEvent.WhenHit or ChargeEvent.Block))
                CurseOnHitLocked(fire.Conn, attacker, fire.Word("curse") ?? "", (int)Math.Round(fire.Number("level")),
                    taken.PlayerStats ?? StatSourceValues(fire.Conn), taken.HitAt ?? fire.Now, fire.Number("refresh_after"));
        })
        {
            Defaults = new Dictionary<string, double> { ["level"] = 1, ["refresh_after"] = 1 },
        });

        // An ailment without damage on the monster whose hit reached the actor (when hit, on block): "ailment" chill,
        // shock or freeze for "seconds", or scorch, brittle or sap at their own length. "amount": a chill's own effect
        // when its source states one ("reducing their Action Speed by 30%").
        EffectRules.Register(new("ailment", ["seconds", "amount"], ["ailment"], fire =>
        {
            if (fire.Event is not ChargeEvent { Monster: { } attacker } hit) return;
            var stats = hit.PlayerStats ?? StatSourceValues(fire.Conn);
            AilmentOnAttackerLocked(fire.Conn, attacker, fire.Word("ailment") ?? "", fire.Number("seconds"), fire.Number("amount"),
                key => stats.GetValueOrDefault(key), hit.HitAt ?? fire.Now);
        }, (_, word) => AttackerAilment(word))
        {
            Defaults = new Dictionary<string, double> { ["seconds"] = 4, ["amount"] = 0 },
        });

        // Life, mana or energy shield at once: "pool" life, mana or energy_shield; "does" gain (the default) or lose;
        // "amount" flat and "percent" of the pool's maximum.
        EffectRules.Register(new("recover", ["amount", "percent"], ["pool", "does"],
            fire => RecoverByRuleLocked(fire.Conn, fire.Word("pool") ?? "", fire.Word("does") == "lose", fire.Number("amount"), fire.Number("percent")),
            (parameter, word) => parameter == "does" ? word is "gain" or "lose" : word is "life" or "mana" or "energy_shield")
        {
            Defaults = new Dictionary<string, double> { ["amount"] = 0, ["percent"] = 0 },
            NameDefaults = new Dictionary<string, string> { ["does"] = "gain" },
        });

        // Records that something happened now, for "seconds" (4: "Recently"): "event" is a name the modifier table's
        // "recently" conditions can ask for. The rule is the event's recorder, for the characters it is live for.
        EffectRules.Register(new("remember", ["seconds"], [], fire =>
            fire.Conn.RecentEvents.Record((string)fire.Data!, ProjectileWorldTime(fire.Conn), fire.Number("seconds") is var seconds && seconds != 4 ? seconds : null))
        {
            Defaults = new Dictionary<string, double> { ["seconds"] = 4 },
            Extra = ["event"],
            Prepare = effect =>
            {
                string name = effect.TryGetProperty("event", out var given) ? given.GetString() ?? "" : "";
                if (name.Length == 0) throw new FormatException("the effect \"remember\" needs \"event\"");
                TrackRecentEvents([name]);
                return name;
            },
        });

        // Arcane Surge, as its gains on hit give it (the standard duration, cast speed and mana regeneration).
        EffectRules.Register(new("arcane_surge", [], [], fire =>
            GainArcaneSurgeLocked(fire.Conn, (fire.Event as HitLanded)?.At ?? fire.Now)));

        // Phasing for "seconds": a running one is lengthened, never shortened.
        EffectRules.Register(new("phasing", ["seconds"], [], fire => GainPhasing(fire.Conn, fire.Number("seconds"))));

        // One more Spirit Charge, up to the character's limit.
        EffectRules.Register(new("spirit_charge", [], [], fire => GainSpiritChargeLocked(fire.Conn, fire.Now)));

        // Charges of "type" (endurance, frenzy, power, or random: one of the three, drawn when the rule fires), through
        // the charge rules at their next step, with the maximum and the refresh they give any gain. "does": gain "count"
        // of them (the default), to_maximum, lose_all (down to the minimum) or lose_one.
        EffectRules.Register(new("charge", ["count"], ["type", "does"], fire =>
        {
            string word = fire.Word("type") ?? "";
            ChargeType type;
            if (word == "random") type = Charges.Types[AreaRng(fire.Conn).Next(Charges.Types.Length)];
            else if (!Enum.TryParse(word, ignoreCase: true, out type)) return;
            int count = (int)Math.Round(fire.Number("count"));
            ChargeGains.Gain? gain = fire.Word("does") switch
            {
                "to_maximum" => new(type, 1, "effect rule", ToMaximum: true),
                "lose_all" => new(type, ChargeGains.Gain.All, "effect rule"),
                "lose_one" => new(type, -1, "effect rule"),
                _ => count > 0 ? new(type, count, "effect rule") : null,
            };
            if (gain is { } gained) ChargesOf(fire.Conn).Enqueue(new ChargeEvent.RuleGain(gained));
        }, (parameter, word) => parameter == "does" ? word is "gain" or "to_maximum" or "lose_all" or "lose_one"
            : word == "random" || Enum.TryParse<ChargeType>(word, ignoreCase: true, out ChargeType known))
        {
            Defaults = new Dictionary<string, double> { ["count"] = 1 },
            NameDefaults = new Dictionary<string, string> { ["does"] = "gain" },
        });
    }
}
