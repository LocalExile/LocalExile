public static partial class Game
{
    sealed class GlacialHammerSequence
    {
        internal ushort Skill;
        internal int Effect, Action, Interruption, Epoch, Strikes;
        internal (double Moving, int Teleports) Travel;
    }

    static readonly RuleTable<Player, GlacialHammerSequence> GlacialHammerSequences = new();

    // Count completed swings, including repeats and misses. All contacts of a swing share its result.
    // An interrupted swing never reaches here. A new action in between, travel, or a new area breaks the sequence.
    static bool ThirdGlacialHammerStrikeLocked(Player player, ushort skill, int effect, int action, double now)
    {
        var sequence = GlacialHammerSequences.GetValue(player, static _ => new());
        var travel = player.Walk.ObserveTravel(now);
        bool successive = sequence.Skill == skill && sequence.Effect == effect
            && (action == sequence.Action || action == unchecked(sequence.Action + 1))
            && sequence.Interruption == player.ActionInterruptionGeneration
            && sequence.Epoch == player.ProjectileEpoch && sequence.Travel == travel;
        sequence.Skill = skill;
        sequence.Effect = effect;
        sequence.Action = action;
        sequence.Interruption = player.ActionInterruptionGeneration;
        sequence.Epoch = player.ProjectileEpoch;
        sequence.Travel = travel;
        sequence.Strikes = successive ? (sequence.Strikes + 1) % 3 : 1;
        return sequence.Strikes == 0;
    }
}
