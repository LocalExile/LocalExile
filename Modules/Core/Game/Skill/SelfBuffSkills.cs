using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // Self buffs a player casts by hand (CastGemSkill): the guard skills start the triggered-guard buff (TriggeredGuards).

    // Refused before the cost: a guard is running, or the guards' shared recovery after the last one has not passed
    // (display_this_skill_cooldown_does_not_recover_during_buff).
    public static string? SelfBuffRefused(ClientState conn, int effect) =>
        GuardSpellEffect(SkillsFor(conn).EffectId(effect)) && !TriggerGuardReady(conn, conn.Clock.Elapsed.TotalSeconds)
            ? "a guard skill is active or recovering" : null;

    // After the paid use: true when this was one of the self buffs (its effect has started).
    public static Task<bool> CastSelfBuff(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort skillId, int effect)
    {
        if (!GuardSpellEffect(SkillsFor(conn).EffectId(effect))) return Task.FromResult(false);
        var session = ProjectilesFor(remote, stream, ciphers, conn);
        lock (conn.Monsters) StartTriggeredGuard(conn, session, skillId, conn.Clock.Elapsed.TotalSeconds);
        return Task.FromResult(true);
    }
}
