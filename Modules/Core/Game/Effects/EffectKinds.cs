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

        EnsureTriggeredSkills();

        // "count" charges of "type" (endurance, frenzy, power), as a skill's own grant: the charge rules apply it at
        // their next step, with the maximum and the refresh they give any gain.
        EffectRules.Register(new("charge", ["count"], ["type"], fire =>
        {
            int count = (int)Math.Round(fire.Number("count"));
            if (count > 0 && Enum.TryParse<ChargeType>(fire.Word("type"), ignoreCase: true, out var type))
                ChargesOf(fire.Conn).Enqueue(new ChargeEvent.Grant(type, count, "effect rule"));
        }, (parameter, word) => Enum.TryParse<ChargeType>(word, ignoreCase: true, out ChargeType known)));
    }
}
