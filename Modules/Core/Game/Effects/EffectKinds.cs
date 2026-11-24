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

        // A timed debuff on the monster of the event (the one hit) for "seconds": intimidated, unnerved, crushed or
        // hindered, timed from the hit as its site times it; a later one restarts it.
        EffectRules.Register(new("debuff", ["seconds"], ["debuff"], fire =>
        {
            if (fire.Event.Monster is { } monster)
                ApplyNamedDebuffLocked(monster, fire.Word("debuff") ?? "", fire.Number("seconds"), (fire.Event as HitLanded)?.At ?? fire.Now);
        }, (_, word) => word is "intimidated" or "unnerved" or "crushed" or "hindered"));

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
