static partial class Game
{
    /// <summary>A triggered summon is released at once and never repeats: it has no cast time, and a trigger support
    /// adds the Triggered type, which Spell Echo excludes.</summary>
    static async Task<bool> RepeatSummonCast(Player player, ushort source, int recovery,
        Func<bool> current, bool triggered, Func<Task<bool>> release)
    {
        var gem = SupportedSkill(player, source);
        int repeats = triggered ? 0 : SpellRepeats(player, source, gem.Effect);
        int seals = triggered ? 0 : UnleashSeals(player, source, gem, ProjectileWorldTime(player), spend: true);
        double interval = Math.Max(.001, (CastMs(player, gem.Effect, source) ?? 1000) / 1000d);
        double due = AreaTime(player) + (triggered ? 0 : interval);
        var lifetime = CombatLifetime;
        for (int releaseIndex = 0; releaseIndex <= repeats + seals; releaseIndex++)
        {
            while (true)
            {
                if (!CurrentGameWork || !ReferenceEquals(lifetime, CombatLifetime) || !current() || player.Closed
                    || player.LeavingArea || player.PlayerLife == 0 || !triggered && recovery != player.ActionInterruptionGeneration
                    || !SameTriggerSource(gem, SupportedSkill(player, source))) return false;
                if (!player.Paused && AreaTime(player) >= due) break;
                await AreaWait(PlayerUpdateRole.Minions, 25);
            }
            if (!await release()) return false;
            due += releaseIndex < seals ? interval * .1 : interval;
        }
        return true;
    }
}
