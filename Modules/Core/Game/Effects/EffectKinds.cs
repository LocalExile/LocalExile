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
        EffectRules.Register(new("stats", [], [], _ => { })
        {
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
            Stand = standing => GainStandingStats(standing.Conn, ((uint, int)[])standing.Data!, standing.On),
            StandOnly = true,
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
