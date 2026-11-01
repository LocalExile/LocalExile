using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    // Shield Charge's movement (11-skills.md "Shield Charge"). The client moves the charge at the player's own movement
    // speed: 0x141e7c0b0 reads movement_velocity_+% from the player's Stats, not the skill's. The game adds the gem's
    // base_movement_velocity_+% through the invisible buff shield_charge_movement_speed_while_charging (BuffDefinitions
    // 1352) for as long as the charge runs, and network stats run no calculators, so the server puts that bonus into the
    // movement_velocity_+% it sends (the pools) from the use until its own walk arrives, and walks its estimate to the aimed
    // cell at that speed instead of the generic movement skill's teleport. Without it the charge ran at walk speed.
    sealed class ShieldChargeRun { public int Percent, Token; }
    static readonly ConditionalWeakTable<ClientState, ShieldChargeRun> ShieldChargeRuns = new();

    // The running charge's movement bonus, for the pools (PoolsFor); 0 when none runs.
    internal static int ShieldChargeMovementPct(ClientState conn) => ShieldChargeRuns.TryGetValue(conn, out var run) ? run.Percent : 0;

    // A movement skill that needs a shield and carries its own movement bonus: it walks there faster, it does not blink.
    internal static int ShieldChargeBonusPct(ClientState conn, ushort skillId, int effect) =>
        SkillsFor(conn).IsMovement(effect) && SkillsFor(conn).RequiresShield(effect)
            && Stats.Key("base_movement_velocity_+%") is uint key ? SupportedSkill(conn, skillId).Stats.GetValueOrDefault(key) : 0;

    // In the use's StartAction: the bonus on, the pools and walk speed with it, and the walk to the aimed cell. Returns the
    // run's token, which only the latest charge's end may clear.
    internal static int BeginShieldCharge(ClientState conn, int percent, int x, int y, double at)
    {
        var run = ShieldChargeRuns.GetOrCreateValue(conn);
        lock (conn.Monsters)
        {
            run.Percent = percent;
            int token = ++run.Token;
            RecomputePoolsAndSpeed(conn);
            conn.Walk.Order(x, y, at);
            return token;
        }
    }

    // The client's charge hits (Frida 2026-11-01, scripts/re/frida/trace-charge-hits.js): a path hit at its position as it
    // starts and then about every 100 ms (every ~8 cells at charge speed), each within 8 cells, and a final hit within
    // 16 cells where it arrives. The server lands them on its own walk at the same cadence; each monster takes one path hit
    // per charge (neighbouring path areas overlap), and the final hit reaches every monster in its area.
    const double ShieldChargePathSeconds = 0.1;
    const int ShieldChargePathRadius = 8, ShieldChargeEndRadius = 16;

    // After the use's echo: the faster movement speed to the client, the path hits while the server's walk runs, the final
    // hit and the speed back where it arrives (a later charge takes over the run, its hits and its end).
    internal static async Task RunShieldCharge(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        int token, double arrivesAt, ushort skillId, ushort sequence, int actionToken, CooldownUseTicket? cooldownUse)
    {
        await SendPlayerStatSnapshot(remote, stream, ciphers, conn, () => PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)),
            $"0x01b3 player stats (Shield Charge starts: movement_velocity_+% {conn.Pools.MovementVelocityPct}, " +
            $"+{ShieldChargeMovementPct(conn)} while charging, until t={arrivesAt:0.000})");
        bool Current() => ShieldChargeRuns.TryGetValue(conn, out var r) && r.Token == token && !conn.Closed;
        var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId);
        var pathHit = new HashSet<uint>();
        Task Hit((int X, int Y) at, int radius, ISet<uint>? once) => gem.Name is null ? Task.CompletedTask
            : AreaSkillHit(remote, stream, ciphers, conn, skillId, gem, at, at, immediate: true, sequence: sequence,
                actionToken: actionToken, cooldownUse: cooldownUse, radiusOverride: radius, once: once);
        for (double tick = conn.Clock.Elapsed.TotalSeconds; tick < arrivesAt; tick += ShieldChargePathSeconds)
        {
            double wait = tick - conn.Clock.Elapsed.TotalSeconds;
            if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait));
            await WhilePaused(conn);
            if (!Current()) return;
            await Hit(conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds), ShieldChargePathRadius, pathHit);
        }
        double rest = arrivesAt - conn.Clock.Elapsed.TotalSeconds;
        if (rest > 0) await Task.Delay(TimeSpan.FromSeconds(rest));
        if (!ShieldChargeRuns.TryGetValue(conn, out var run) || run.Token != token || conn.Closed) return;
        await Hit(conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds), ShieldChargeEndRadius, null);
        lock (conn.Monsters)
        {
            run.Percent = 0;
            RecomputePoolsAndSpeed(conn);
        }
        await SendPlayerStatSnapshot(remote, stream, ciphers, conn, () => PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)),
            $"0x01b3 player stats (Shield Charge ends: movement_velocity_+% {conn.Pools.MovementVelocityPct})");
    }
}
