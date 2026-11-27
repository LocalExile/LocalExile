public static partial class Game
{
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
    public static (double Life, double Mana, List<Flasks.Effect> Ended) AdvanceFlaskRecovery(Player player, double from, double to)
    {
        var owner = ResourceEffectsOf(player);
        double copy = 0;
        // A synchronous observer; the queue retains no rule delegate. Only continuous earned Mana is eligible.
        var result = player.FlaskEffects.Advance(from, to, (effect, seconds) =>
        {
            if (ResourceEffectsCurrent(player, owner) && effect.CopyManaToLife) copy += effect.ManaPerSecond * seconds;
        });
        double lifeAtEnd = result.Ended.Sum(effect => effect.RecoverLifeAtEndPct * (double)player.PlayerMaxLife / 100);
        return (result.Life + copy + lifeAtEnd, result.Mana, result.Ended);
    }
}
