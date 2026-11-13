static partial class Game
{
    /// <summary>A triggered summon is released at once and never repeats: it has no cast time, and a trigger support
    /// adds the Triggered type, which Spell Echo excludes.</summary>
    static async Task<bool> RepeatSummonCast(ClientState conn, ushort source, int recovery,
        Func<bool> current, bool triggered, Func<Task<bool>> release)
    {
        var gem = SupportedSkill(conn, source);
        int repeats = triggered ? 0 : SpellRepeats(conn, source, gem.Effect);
        int seals = triggered ? 0 : UnleashSeals(conn, source, gem, ProjectileWorldTime(conn), spend: true);
        double interval = Math.Max(.001, (CastMs(conn, gem.Effect, source) ?? 1000) / 1000d);
        double due = AreaTime(conn) + (triggered ? 0 : interval);
        var lifetime = CombatLifetime;
        for (int releaseIndex = 0; releaseIndex <= repeats + seals; releaseIndex++)
        {
            while (true)
            {
                lock (conn.Monsters)
                {
                    if (!CurrentGameWork || !ReferenceEquals(lifetime, CombatLifetime) || !current() || conn.Closed
                        || conn.LeavingArea || conn.PlayerLife == 0 || recovery != conn.ActionInterruptionGeneration
                        || !SameTriggerSource(gem, SupportedSkill(conn, source))) return false;
                    if (!conn.Paused && AreaTime(conn) >= due) break;
                }
                await AreaWait(PlayerUpdateRole.Minions, 25);
            }
            if (!await release()) return false;
            due += releaseIndex < seals ? interval * .1 : interval;
        }
        return true;
    }
}
