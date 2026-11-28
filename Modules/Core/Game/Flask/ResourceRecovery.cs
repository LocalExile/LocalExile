public static partial class Game
{
    // Recovery and linked skills settle at different points of the area frame. Keep exact completed waiver intervals
    // until the link payment that needs them; a subsequent link starts with a new payment clock.
    static readonly RuleTable<Player, Dictionary<Flasks.Effect, double>> LinkedManaWaiverEnds = new();

    static IEnumerable<Flasks.Effect> RecoveryManaWaiverEffects(Player player) =>
        player.RecoveryFlaskStats.EffectsWithStat(Stat.LocalUniqueFlaskNoManaCostWhileHealing);

    static Dictionary<Flasks.Effect, double>? ActiveRecoveryManaWaivers(Player player, double through, bool predictDeadline = false)
    {
        Dictionary<Flasks.Effect, double>? intervals = null;
        foreach (var effect in RecoveryManaWaiverEffects(player))
        {
            double? end = predictDeadline ? player.FlaskEffects.ExpectedEnd(effect, through)
                : player.FlaskEffects.Remaining(effect) > 0 ? through : null;
            if (end is double at) (intervals ??= new(ReferenceEqualityComparer.Instance))[effect] = at;
        }
        return intervals;
    }

    static void RememberLinkedManaWaiverEnd(Player player, Flasks.Effect effect, double at)
    {
        if (player.Links.Count == 0 || at <= player.Links.PaidAt) return;
        LinkedManaWaiverEnds.GetOrCreateValue(player).TryAdd(effect, at);
    }

    static double LinkedManaWaiverSeconds(Player player, double from, double to)
    {
        var windows = ActiveRecoveryManaWaivers(player, to, predictDeadline: true);
        if (LinkedManaWaiverEnds.TryGetValue(player, out var ended))
        {
            LinkedManaWaiverEnds.Remove(player);
            if (windows is null) windows = ended;
            else foreach (var (effect, at) in ended) windows[effect] = at;
        }
        return ManaWaiverSeconds(windows, from, to);
    }

    static double ManaWaiverSeconds(Dictionary<Flasks.Effect, double>? windows, double from, double to)
    {
        if (windows is null || to <= from) return 0;
        double waived = 0, coveredThrough = from;
        foreach (var (effect, until) in windows.OrderBy(interval => interval.Key.Started))
        {
            double start = Math.Max(coveredThrough, effect.Started);
            double end = Math.Min(to, until);
            if (end <= start) continue;
            waived += end - start;
            coveredThrough = end;
        }
        return waived;
    }

    public static bool ManaFlaskCopiesLife(FlaskRecovery.Use recovery, IReadOnlyDictionary<uint, int> player) =>
        recovery.Mana > 0 && (recovery.ManaSeconds ?? recovery.Seconds) > 0
        && Stat.NonInstantManaRecoveryFromFlasksAlsoRecoversLife is var key && player.GetValueOrDefault(key) != 0;

    public static void StartRecoveryEffectsLocked(Player player, IEnumerable<Flasks.Effect> effects, bool copy)
    {
        foreach (var effect in effects)
        {
            effect.CopyManaToLife = copy && effect.ManaPerSecond > 0;
            player.FlaskEffects.Start(effect);
        }
    }
    public static (double Life, double Mana, List<Flasks.Effect> Ended) AdvanceFlaskRecovery(Player player, double from, double to) =>
        AdvanceFlaskRecovery(player, from, to, false, out _);

    static (double Life, double Mana, List<Flasks.Effect> Ended) AdvanceFlaskRecovery(Player player, double from, double to,
        bool trackManaWaiver, out double waivedSeconds)
    {
        var owner = ResourceEffectsOf(player);
        double copy = 0;
        var waiver = trackManaWaiver || player.Links.Count > 0 ? ActiveRecoveryManaWaivers(player, to) : null;
        // A synchronous observer; the queue retains no rule delegate. Only continuous earned Mana is eligible.
        var result = player.FlaskEffects.Advance(from, to, (effect, seconds) =>
        {
            if (ResourceEffectsCurrent(player, owner) && effect.CopyManaToLife) copy += effect.ManaPerSecond * seconds;
        }, waiver is null ? null : (effect, at) =>
        {
            if (waiver?.ContainsKey(effect) != true) return;
            waiver[effect] = at;
            RememberLinkedManaWaiverEnd(player, effect, at);
        });
        waivedSeconds = trackManaWaiver ? ManaWaiverSeconds(waiver, from, to) : 0;
        double lifeAtEnd = result.Ended.Sum(effect => effect.RecoverLifeAtEndPct * (double)player.PlayerMaxLife / 100);
        return (result.Life + copy + lifeAtEnd, result.Mana, result.Ended);
    }
}
