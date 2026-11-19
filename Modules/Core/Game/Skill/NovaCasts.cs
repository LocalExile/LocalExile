using GameServer.Core.Projectiles;

public static partial class Game
{
    // Each seal reoccurs a tenth of a cast apart. Spell Echo repeats a full cast apart, sharing the original use.
    static Task ScheduleNovaReleases(ProjectileSession session, ushort skillId, int effect, int? actionToken,
        CooldownUseTicket? cooldownUse, SacrificeUse sacrifice, int repeats, int seals, Func<int, bool, Task> release)
    {
        var conn = session.Conn;
        if (CastMs(conn, effect, skillId) is not int castMs) return Task.CompletedTask;
        double cast = castMs / 1000d;
        int action = actionToken ?? cooldownUse?.ActionToken ?? conn.ActionCount;
        var releases = new List<Task>();
        using (ProjectileMutation(conn))
        {
            long first = session.Runtime.Now + ProjectileRuntime.Time(cast
                * Animations.SpellAreaRelease(SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject));
            for (int i = 0; i <= repeats + seals; i++)
            {
                int repeat = Math.Max(0, i - seals);
                bool seal = i > 0 && i <= seals;
                long at = first + ProjectileRuntime.Time(Math.Min(i, seals) * cast / 10 + repeat * cast);
                releases.Add(AtNovaTime(session, at, () => session.Active && conn.PlayerLife > 0 && sacrifice.Current
                    && (cooldownUse?.Current(conn) ?? PlayerActionCurrent(conn, action))
                    ? release(repeat, seal) : Task.CompletedTask));
            }
        }
        return Task.WhenAll(releases);
    }

    // Start the contact on the simulator's exact tick. Its network output can finish later or the session can retire.
    static async Task AtNovaTime(ProjectileSession session, long at, Func<Task> start)
    {
        var result = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Runtime.Schedule(at, () =>
        {
            try { result.SetResult(start()); }
            catch (Exception error) { result.SetException(error); }
        });
        await Task.WhenAny(result.Task, session.OutputCompleted);
        if (result.Task.IsCompleted) await result.Task.Unwrap();
    }
}
