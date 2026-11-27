static partial class Game
{
    static double CloneArrowLandingDelay(Player player, ushort source)
    {
        var gem = SupportedSkill(player, source);
        var (fallSpeed, attackSeconds) = ArrowRainTiming(player, source, CharacterPlayerStats(player), gem.Stats);
        // The clone arrow launches at 40% of the bow action. Its ascent has an attack-time floor,
        // then the descending arrow reaches the target after a fixed quarter second.
        double launch = .4 * attackSeconds;
        double ascent = Math.Max(launch * PlayerActionFactor(player), .6 * Math.Min(2.4, 100d / (100 + fallSpeed)));
        return launch + ascent + .25;
    }

    /// <summary>A triggered summon is released at once and never repeats: it has no cast time, and a trigger support
    /// adds the Triggered type, which Spell Echo excludes.</summary>
    static async Task<bool> RepeatSummonCast(Player player, ushort source, int recovery,
        Func<bool> current, bool triggered, Func<Task<bool>> release, double? firstReleaseDelay = null)
    {
        var gem = SupportedSkill(player, source);
        int repeats = triggered ? 0 : SpellRepeats(player, source, gem.Effect);
        int seals = triggered ? 0 : UnleashSeals(player, source, gem, ProjectileWorldTime(player), spend: true);
        double interval = Math.Max(.001, (CastMs(player, gem.Effect, source) ?? 1000) / 1000d);
        double due = AreaTime(player) + (firstReleaseDelay ?? (triggered ? 0 : interval));
        var lifetime = CombatLifetime;
        bool released = false;
        for (int releaseIndex = 0; releaseIndex <= repeats + seals; releaseIndex++)
        {
            while (true)
            {
                if (!CurrentGameWork || !ReferenceEquals(lifetime, CombatLifetime) || !current() || player.Closed
                    || player.LeavingArea || player.PlayerLife == 0 || !triggered && recovery != player.ActionInterruptionGeneration
                    || !SameTriggerSource(gem, SupportedSkill(player, source))) return released;
                if (!player.Paused && AreaTime(player) >= due) break;
                await AreaWait(PlayerUpdateRole.Minions, 25);
            }
            if (!await release()) return released;
            released = true;
            due += releaseIndex < seals ? interval * .1 : interval;
        }
        return true;
    }
}
