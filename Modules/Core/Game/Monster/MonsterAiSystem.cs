using System.Runtime.CompilerServices;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// The monsters' AI as the area's system: every frame, for each player in the
/// order they joined, the pass of the monsters that act for that player (a monster acts for its target,
/// MonsterTargetsLocked), decided on the area's frame instead of each player's own AI loop. The plan is then sent as the
/// loop sent it, as the player's task: each order is built at its frame's gate (SendMonsterAction), one after another,
/// and the next pass waits until the last one is sent. The plan runs on the player's frame time (AreaTime), which the area
/// step shares since the frame begins before it (AreaFrames.BeginStep): planned a frame ahead (live 2026-11-18), a chase
/// that followed a monster's walk-up swing reached the client while its swing still ran, and the monster stood for about
/// 2 s on the client while the server walked it, up to 11 cells apart.
/// </summary>
public static partial class Game
{
    // Each player's pass: its cadence, and the sending of the last plan (never two at once).
    sealed class AreaAi
    {
        public readonly MonsterAiCadence Cadence = new();
        public Task Pass = Task.CompletedTask;
    }
    static readonly ConditionalWeakTable<ClientState, AreaAi> AreaAis = new();

    public static void StartMonsterAi(GameArea area, ClientState conn, object source)
    {
        var frames = conn.Frames;
        area.Systems.Add("monster ai", AreaPhase.Ai, a =>
        {
            foreach (var player in frames.Players()) AreaMonsterAi(a, player);
        }, source: source);
    }


    static void AreaMonsterAi(GameArea area, ClientState player)
    {
        if (!CurrentGameWork || player.Instance is null || player.Closed || player.LeavingArea || player.PendingTransfer is not null || !player.PlayerSent
            || player.PlayerUpdatesState is not PlayerUpdatesState updates || !PlayerUpdatesCurrent(player, updates)) return;
        var ai = AreaAis.GetValue(player, _ => new());
        // The last pass is still sending (each order waits for its frame's gate): as in the AI loop, the next waits.
        if (!ai.Pass.IsCompleted) return;
        MonsterAiPlan plan;
        // Planned as the player's loop planned: on their frame time (AreaTime) and output guard.
        var (context, guard) = (AreaUpdateContext.Value, Program.ProjectileOutputGuard.Value);
        AreaUpdateContext.Value = updates;
        Program.ProjectileOutputGuard.Value = () => PlayerUpdatesCurrent(player, updates);
        try
        {
            using (ProjectileMutation(player, phase: "ai"))
                plan = PlanMonsterAi(player, ai.Cadence, Math.Max(ProjectileWorldTime(player), AreaTime(player)));
        }
        finally { (AreaUpdateContext.Value, Program.ProjectileOutputGuard.Value) = (context, guard); }
        var conn = player;
        ai.Pass = AsPlayer(player, updates, async () =>
        {
            await SendMonsterAiPlan(player, ai.Cadence, plan);
            // The next pass's upkeep, before its plan as in the AI loop.
            await MonsterAiUpkeep(player);
        });
    }

    // The plan's records and actions, sent as the AI loop sent them: each order built at its frame's gate.
    static async Task SendMonsterAiPlan(ClientState conn, MonsterAiCadence cadence, MonsterAiPlan plan)
    {
        foreach (var (packet, log) in plan.Ailments) await Send(conn, packet, log);
        foreach (uint id in plan.EndedActions)
            await SendWorldRecord(conn, id, InstanceProtocol.ActionEnded(id), ServerTrace.Ai ? $"0x01ac monster 0x{id:x} action ended" : null);
        foreach (var (id, m, action, packet, log) in MonsterAiOrders(conn, plan))
            await SendMonsterAction(conn, id, m, action, packet, log);
        await MonsterAiActions(conn, cadence, plan);
    }

    // The pass's upkeep, before its plan as in the AI loop: summons running out, traps, markers, grounds, debuffs.
    static async Task MonsterAiUpkeep(ClientState conn)
    {
        await ExpireSummonedMonsters(conn);
        await MonsterTrapsTick(conn);
        await MonsterMarkersTick(conn);
        await HostileGroundsTick(conn);
        await PlayerDebuffsTick(conn);
        if (DamageTriggers.TryGetValue(conn, out _))
            ReleaseDamageTakenTriggers(conn, ProjectilesFor(conn));
        conn.Performance.MaybeLog(conn.Remote);
    }

    // What the pass starts besides its orders: boss bars, swings (each waits for its contact on the player's frames).
    static async Task MonsterAiActions(ClientState conn, MonsterAiCadence cadence, MonsterAiPlan plan)
    {
        foreach (var (id, m) in plan.BossBars)
        {
            lock (conn.Monsters) { if (!MonsterPresent(conn, id, m)) continue; conn.BossBarsSeen.Add(id); }
            await ShowBossLifeBar(id, m, conn);
        }
        if (!CurrentGameWork) return;
        foreach (var (id, m, action) in plan.Swings)
            _ = RunPlannedMonsterSwing(conn, cadence, id, m, action);
        foreach (var (id, m, action, begun) in plan.ArrivedSwings)
            _ = RunPlannedMonsterSwing(conn, cadence, id, m, action, begun);
        foreach (var attack in plan.MinionSwings)
            StartMinionSwing(conn, attack);
        foreach (var (id, m, action, target, weapon) in plan.WeaponSwings)
            _ = RunPlannedRampageWeaponSwing(conn, cadence, id, m, action, target, weapon);
    }

    // Work of the area's pass that sends, run as the player's: on their frame waits (AreaUpdateContext) and output guard.
    static async Task AsPlayer(ClientState conn, PlayerUpdatesState updates, Func<Task> work)
    {
        AreaUpdateContext.Value = updates;
        Program.ProjectileOutputGuard.Value = () => PlayerUpdatesCurrent(conn, updates);
        Program.ActingPlayer.Value = conn;
        try { await work(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine($"[gateway] {conn.Wire.Remote} area AI work failed: {ServerLog.Describe(ex)}"); }
    }
}
