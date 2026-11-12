static partial class Game
{
    static async Task<bool> RepeatSummonCast(ClientState conn, ushort source, int recovery,
        Func<bool> current, Func<Task<bool>> release)
    {
        var gem = SupportedSkill(conn, source);
        int repeats = SpellRepeats(conn, source, gem.Effect);
        double interval = Math.Max(.001, (CastMs(conn, gem.Effect, source) ?? 1000) / 1000d);
        double due = AreaTime(conn) + interval;
        var lifetime = CombatLifetime;
        for (int repeat = 0; repeat <= repeats; repeat++, due += interval)
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
        }
        return true;
    }
}
