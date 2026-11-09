using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    /// <summary>Where Flicker Strike takes the player: the monster the client's action picks (FlickerStrike) and the
    /// landing next to it. Null when no monster is in range: the client then strikes where the player stands.</summary>
    internal static (uint Id, (int X, int Y) At, (int X, int Y) Landing)? FlickerDestination(ClientState conn, int effect,
        ushort skillId, (int X, int Y) player, (int X, int Y) aim, uint clicked, double now)
    {
        var stats = SupportedSkill(conn, skillId).Stats;
        int range = FlickerStrike.Range(GolemStat(stats, "flicker_strike_teleport_range_+%"),
            GolemStat(stats, "apply_cut_debuff_on_hit") != 0);
        var monsters = new List<(uint, (int, int), int)>();
        lock (conn.Monsters)
            foreach (var (id, m) in conn.Monsters)
            {
                if (m.Dead || m.Life <= 0 || m.OwnerId is not null || conn.AsleepMonsters.Contains(id)
                    || conn.Instance is not null && !conn.MonsterViews.ContainsKey(id)) continue;
                monsters.Add((id, m.Walk.Position(now), Math.Clamp((m.Variety?.ObjectSize ?? 1) - 1, -1, 127)));
            }
        if (FlickerStrike.Pick(monsters, player, aim, range, clicked) is not { } pick) return null;
        return (pick.Id, pick.At, MovementLanding(conn, effect, player, pick.At));
    }

    // Flicker Strike's buff, BuffDefinitions 1344 flicker_strike_speed_boost ("You have increased Movement Speed",
    // StatsKeys base_movement_velocity_+%): each use gives it again for the skill's duration, with the gem's
    // flicker_strike_buff_movement_speed_+%. Network stats run no calculators, so the bonus goes into the movement speed
    // the server sends (the pools, FlickerMovementPct) while the buff lasts, as Shield Charge's does.
    const ushort FlickerSpeedBuffRow = 1344;
    sealed class FlickerSpeed { public int Percent, Token; public ushort Buff; }
    static readonly ConditionalWeakTable<ClientState, FlickerSpeed> FlickerSpeeds = new();

    // The running buff's movement bonus, for the pools (PoolsFor); 0 when none runs.
    internal static int FlickerMovementPct(ClientState conn) => FlickerSpeeds.TryGetValue(conn, out var s) ? s.Percent : 0;

    // After an accepted Flicker Strike use: the buff and the faster movement speed, and both gone when it runs out
    // (a later use's buff takes over).
    internal static async Task GrantFlickerSpeed(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId)
    {
        var own = SupportedSkill(conn, skillId).Stats;
        int percent = GolemStat(own, "flicker_strike_buff_movement_speed_+%");
        if (percent == 0) return;
        var player = CharacterPlayerStats(conn);
        int Player(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        double seconds = SkillDurationModifiers.Seconds(GolemStat(own, "base_skill_effect_duration"), Player,
            name => GolemStat(own, name));
        if (seconds <= 0) return;
        var speed = FlickerSpeeds.GetOrCreateValue(conn);
        int token;
        lock (conn.Monsters) token = ++speed.Token;
        // The use's message keeps one stats snapshot for its whole flow (PlayerStatScope), taken before this bonus, and the
        // end below runs on that flow too: both build theirs fresh, or the panel kept the old speed (live 2026-11-09:
        // 123% after the buff had ended, until a charge change sent new stats).
        using var fresh = new PlayerStatScope(conn, fresh: true);
        await Send(remote, stream, ciphers, [], $"0x01c1 Flicker Strike buff: +{percent}% movement speed for {seconds:0.##} s",
            atGate: () =>
            {
                lock (conn.Monsters)
                {
                    if (conn.Closed || conn.LeavingArea || conn.RageRetired || speed.Token != token) return null;
                    var bytes = new List<byte>();
                    if (speed.Buff != 0) bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, speed.Buff));
                    speed.Buff = conn.NextBuff++;
                    bytes.AddRange(InstanceEntities.AddBuff(PlayerId, speed.Buff, FlickerSpeedBuffRow, PlayerId,
                        sourceSkill: skillId, u68: BitConverter.SingleToUInt32Bits((float)seconds)));
                    if (speed.Percent != percent)
                    {
                        speed.Percent = percent;
                        RecomputePoolsAndSpeed(conn);
                        bytes.AddRange(PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
                    }
                    return [.. bytes];
                }
            });
        SendAfter(seconds, async () =>
        {
            using var ended = new PlayerStatScope(conn, fresh: true);
            await Send(remote, stream, ciphers, [], "0x01c2 Flicker Strike buff ends", atGate: () =>
            {
                lock (conn.Monsters)
                {
                    if (conn.Closed || conn.LeavingArea || conn.RageRetired || speed.Token != token || speed.Buff == 0) return null;
                    var bytes = new List<byte>(InstanceEntities.RemoveBuff(PlayerId, speed.Buff));
                    speed.Buff = 0;
                    speed.Percent = 0;
                    RecomputePoolsAndSpeed(conn);
                    bytes.AddRange(PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
                    return [.. bytes];
                }
            });
        });
    }
}
