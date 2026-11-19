using GameServer.Core.Projectiles;

public static partial class Game
{
    // Each seal reoccurs a tenth of a cast apart. Spell Echo repeats a full cast apart, sharing the original use.
    static Task ScheduleNovaReleases(ProjectileSession session, ushort skillId, int effect, int? actionToken,
        CooldownUseTicket? cooldownUse, SacrificeUse sacrifice, int repeats, int seals, Func<int, bool, Task> release,
        Func<Action?>? startUse = null)
    {
        var conn = session.Conn;
        if (CastMs(conn, effect, skillId) is not int castMs) return Task.CompletedTask;
        double cast = castMs / 1000d;
        int action = actionToken ?? cooldownUse?.ActionToken ?? conn.ActionCount;
        var releases = new List<Task>();
        using (ProjectileMutation(conn))
        {
            bool Current() => session.Active && conn.PlayerLife > 0 && sacrifice.Current
                && (cooldownUse?.Current(conn) ?? PlayerActionCurrent(conn, action));
            if (!Current()) return Task.CompletedTask;
            var beginRepeat = startUse?.Invoke();
            long start = session.Runtime.Now;
            long contact = ProjectileRuntime.Time(cast * Animations.SpellAreaRelease(SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject));
            for (int i = 0; i <= repeats + seals; i++)
            {
                int repeat = Math.Max(0, i - seals);
                bool seal = i > 0 && i <= seals;
                long at = start + ProjectileRuntime.Time(Math.Min(i, seals) * cast / 10 + repeat * cast);
                if (repeat > 0 && beginRepeat is not null)
                    session.Runtime.Schedule(at, () => { if (Current()) beginRepeat(); });
                releases.Add(AtNovaTime(session, at + contact, () => Current() ? release(repeat, seal) : Task.CompletedTask));
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
