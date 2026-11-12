static partial class Game
{
    static bool TriggerSummonSpell(ClientState conn, SupportedGem gem)
    {
        string? effect = SkillsFor(conn).EffectId(gem.Effect);
        return effect == "Desecrate" || GolemData.Find(effect) is not null || SummonData.Find(effect) is not null;
    }

    internal static async Task ReleaseTriggeredSummon(ClientState conn, ProjectileSession session, ushort source,
        (int X, int Y) aim, Func<bool> current)
    {
        var gem = SupportedSkill(conn, source);
        var lifetime = CombatLifetime;
        bool Current() => current() && session.Active && CurrentGameWork && ReferenceEquals(lifetime, CombatLifetime)
            && !conn.Closed && !conn.LeavingArea && conn.PendingTransfer is null && !conn.Paused && conn.PlayerLife > 0
            && SameTriggerSource(gem, SupportedSkill(conn, source));
        if (!Current() || !gem.RequirementsMet) return;
        string? effect = SkillsFor(conn).EffectId(gem.Effect);
        if (effect == "Desecrate")
            await Desecrate(session.remote, session.stream, session.ciphers, conn, source, aim, Current, triggered: true);
        else if (GolemData.Find(effect) is { } golem)
            await CastGolem(session.remote, session.stream, session.ciphers, conn, source, golem, aim,
                conn.ActionInterruptionGeneration, Current, triggered: true);
        else if (SummonData.Find(effect) is { } summon)
            await CastSummon(session.remote, session.stream, session.ciphers, conn, source, summon, aim, 0,
                conn.ActionInterruptionGeneration, Current, triggered: true);
    }
}
