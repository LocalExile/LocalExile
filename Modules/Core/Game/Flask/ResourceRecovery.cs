public static partial class Game
{
    public static bool ManaFlaskCopiesLife(FlaskRecovery.Use recovery, IReadOnlyDictionary<uint, int> player) =>
        recovery.Mana > 0 && (recovery.ManaSeconds ?? recovery.Seconds) > 0
        && Stats.Key("non_instant_mana_recovery_from_flasks_also_recovers_life") is uint key && player.GetValueOrDefault(key) != 0;

    public static void StartRecoveryEffectsLocked(ClientState conn, IEnumerable<Flasks.Effect> effects, bool copy)
    {
        foreach (var effect in effects)
        {
            effect.CopyManaToLife = copy && effect.ManaPerSecond > 0;
            conn.FlaskEffects.Start(effect);
        }
    }
    public static (double Life, double Mana, List<Flasks.Effect> Ended) AdvanceFlaskRecovery(ClientState conn, double from, double to)
    {
        var owner = ResourceEffectsOf(conn);
        double copy = 0;
        // A synchronous observer; the queue retains no rule delegate. Only continuous earned Mana is eligible.
        var result = conn.FlaskEffects.Advance(from, to, (effect, seconds) =>
        {
            if (ResourceEffectsCurrent(conn, owner) && effect.CopyManaToLife) copy += effect.ManaPerSecond * seconds;
        });
        return (result.Life + copy, result.Mana, result.Ended);
    }
}
