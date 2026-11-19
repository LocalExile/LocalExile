using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

/// <summary>The run's lanterns: the Lantern object with its <c>DeepwaterLantern</c> light task, the ruleset's anchor
/// (0x2c9 mode 0) and counter (mode 2), and the placement the client asks for (0x2ca op 0).</summary>
internal static class DeepwaterLanterns
{
    /// <summary>How far the server's check may stray from the client's snap: the client puts a lantern exactly
    /// <see cref="DeepwaterConstants.BaseLanternPlacementRange"/> from an anchor, but moves a cell that is not walkable
    /// up to 9 cells (<c>0x141fc5800</c>'s ring search), and the server's estimate of the player trails the client's.</summary>
    const int Slack = 10;
    /// <summary>The client's reach from the player to the new lantern (<c>|P − player|² &lt; 10001</c>).</summary>
    const int PlayerReach = 100;
    /// <summary>How far behind the player (away from the cursor) a lantern may still go (<c>|P − player|² &lt; 901</c>).</summary>
    const int Behind = 30;
    /// <summary>A candidate at least 60 cells from the cursor is dropped (<c>d2 &lt; 3601</c>).</summary>
    const long CursorReach = 3601;

    static readonly Lazy<StateMachineData?> States = new(() => ObjectTypes.All().Where(t => t.Type == DeepwaterConstants.Lantern)
        .Select(t => ObjectTypes.StateMachine(t.Row, ObjectTypes.SharedStates())).FirstOrDefault());

    /// <summary>The run's lantern count: a Chart's or a Voyage's, a Voyage 40 more once the league's first Voyage is done
    /// (league quest flag 1893; G that this flag is the one, the wiki gives 65), plus ship upgrade row 0
    /// (<c>LQF 1829</c>, <c>map_deepwater_max_lanterns +1</c>).</summary>
    public static int Total(ClientState conn, bool voyage)
    {
        int extra = DeepwaterUpgrades.ExtraLanterns(conn);
        if (!voyage) return DeepwaterConstants.BaseLanternCount1x1 + extra;
        bool more;
        lock (conn.Monsters) more = conn.LeagueQuestFlags.Contains(DeepwaterConstants.VoyagesEnabledLeagueFlag);
        return DeepwaterConstants.BaseLanternCountVoyage + (more ? DeepwaterConstants.VoyagesEnabledExtraLanterns : 0) + extra;
    }

    static readonly ConditionalWeakTable<ClientState, StrongBox<double>> AutoCooldowns = new();
    const double AutoCooldownSeconds = 0.5;
    const int AutoEdgeMargin = 20;

    /// <summary>Clears any cached Auto-Lanterns cooldown when a connection leaves an area.</summary>
    public static void ForgetAuto(ClientState conn) => AutoCooldowns.Remove(conn);

    /// <summary>Server-side Auto-Lanterns (<c>LQF 1841</c>): when enabled, on a <c>0.5 s</c> cooldown while exploring,
    /// places a lantern automatically as the controller approaches the edge of light (<c>BaseLanternRadius − 20</c>),
    /// outside the deck and Velka's boss arena. Zero allocations when <c>LQF 1841</c> is off.</summary>
    public static (int X, int Y)? TryAutoLantern(ClientState conn, DeepwaterRun run, double now, out string note)
    {
        note = "";
        if (run.Phase != DeepwaterRun.RunPhase.Exploring || conn.PlayerLife == 0 || conn.Closed || conn.LeavingArea)
            return null;
        bool auto;
        lock (conn.Monsters) auto = conn.LeagueQuestFlags.Contains(DeepwaterConstants.AutoLanternsLeagueFlag);
        if (!auto) return null;
        if (!CharacterName(conn).Equals(run.Controller, StringComparison.OrdinalIgnoreCase)
            || !ClientHasQuestFlag(conn, DeepwaterConstants.LanternController))
            return null;
        lock (run.Gate) if (run.Placed >= run.Total) return null;

        var gate = AutoCooldowns.GetValue(conn, _ => new StrongBox<double>(0));
        if (now < gate.Value) return null;

        var player = conn.Walk.Position(now);
        if (run.Board.OnDeck(player) || DeepwaterVelka.InArenaLight(run, player)) return null;

        int threshold = Math.Max(DeepwaterConstants.MinimumLanternRadius, DeepwaterConstants.BaseLanternRadius - AutoEdgeMargin);
        long thresholdSq = (long)threshold * threshold;
        var anchors = run.Snapshot();
        for (int i = 0; i < anchors.Length; i++)
        {
            var l = anchors[i];
            if (l.Radius(now) <= 0) continue;
            long dx = player.X - l.X, dy = player.Y - l.Y;
            if (dx * dx + dy * dy <= thresholdSq) return null;
        }

        gate.Value = now + AutoCooldownSeconds;
        if (Snap(conn, run, player, out note) is { } snapped) return snapped;

        // Fallback: project BaseLanternPlacementRange from the closest anchor toward the player when the player walked
        // slightly past Snap's cursor reach.
        int range = DeepwaterConstants.BaseLanternPlacementRange;
        long apart = (long)(range - 5) * (range - 5);
        long maxReach = (long)(PlayerReach + Slack) * (PlayerReach + Slack);
        (int X, int Y)? best = null;
        long bestDistSq = long.MaxValue;
        for (int i = 0; i < anchors.Length; i++)
        {
            var a = anchors[i];
            int vx = player.X - a.X, vy = player.Y - a.Y;
            float len = MathF.Sqrt((float)vx * vx + (float)vy * vy);
            if (len < 1f) continue;
            float s = range / len;
            var p = (X: a.X + (int)MathF.Floor(vx * s + 0.5f), Y: a.Y + (int)MathF.Floor(vy * s + 0.5f));
            long dx = p.X - player.X, dy = p.Y - player.Y;
            long d2 = dx * dx + dy * dy;
            if (d2 > maxReach || d2 >= bestDistSq) continue;
            bool tooClose = false;
            for (int j = 0; j < anchors.Length; j++)
            {
                long bx = anchors[j].X - p.X, by = anchors[j].Y - p.Y;
                if (bx * bx + by * by <= apart) { tooClose = true; break; }
            }
            if (!tooClose) (best, bestDistSq) = (p, d2);
        }
        if (best is not { } target) return null;
        if (conn.Layout.WalkableFor(target.X, target.Y, 3)) return target;
        for (int r = 1; r <= 9; r++)
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                    if (conn.Layout.WalkableFor(target.X + dx, target.Y + dy, 3))
                        return (target.X + dx, target.Y + dy);
                }
        return null;
    }

    /// <summary>Creates a lantern on this connection: the object (StateMachine <c>drop</c> = the animation for a new one,
    /// 0 for one shown again; <c>dropped</c> = the animation either way, which goes straight to idle; its collapse states)
    /// with its light task as it stands now (none once it went out), then its anchor. False when the type is not written
    /// (its components or states are missing).</summary>
    public static async Task<bool> Show(IModuleContext module, ClientState conn, DeepwaterRun run, DeepwaterRun.Lantern lantern, bool dropping)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        lantern = lantern.Restated(now);
        bool shrinking, extinguished, collected;
        lock (run.Gate)
        {
            var shrink = run.Shrinks.GetValueOrDefault(lantern.Id);
            (shrinking, extinguished, collected) = (shrink?.Started == true, shrink?.Out == true, run.Flared.Contains(lantern.Id));
        }
        var player = PlayerOf(conn);
        if (QuestObjectTypes.Find(DeepwaterConstants.Lantern) is not { } info || States.Value is not { } states
            || !QuestObject.CanSerialize(info.Components, true))
        {
            module.Log($"{player.Character}: the Lantern's components are not written ({string.Join(", ", QuestObjectTypes.Find(DeepwaterConstants.Lantern)?.Components ?? [])})");
            return false;
        }
        var blob = new QuestObject(lantern.X, lantern.Y, info.Components, 0, null, info.Model)
        {
            StateMachine = states.Set("drop", dropping ? lantern.Drop : 0).Set("dropped", lantern.Drop).Set("collected_resource", collected ? 1 : 0)
                .Set("shrinking", shrinking ? 1 : 0).Set("extinguished", extinguished ? 1 : 0),
            Tasks = extinguished ? [] : [(DeepwaterConstants.LanternTask, lantern.TaskData())],
        }.Blob();
        if (!await global::Game.SendWorldCreate(player, lantern.Id, InstanceEntities.CreateObject(lantern.Id, InstanceEntities.ObjectTypeHash(DeepwaterConstants.Lantern), blob),
            $"0x02bb Allflame lantern {lantern.Order} 0x{lantern.Id:x} at ({lantern.X}, {lantern.Y}), task (a {lantern.A}, {lantern.RateA}/s, b {lantern.B}, {lantern.RateB}/s)"))
            return false;
        await global::Game.SendWorldRecord(player, lantern.Id, InstanceProtocol.EnterWakeZone(lantern.Id));
        WorldOf(player).SetBlocker(lantern.Id, lantern.X, lantern.Y, Math.Max(1, info.Size));
        if (DeepwaterRuleset.Anchor(lantern.X, lantern.Y, lantern.Id) is { } anchor)
            await player.Send(anchor, $"0x02c9 ruleset DeepwaterRules mode 0: lantern anchor ({lantern.X}, {lantern.Y}) 0x{lantern.Id:x}");
        return true;
    }

    /// <summary>The lantern's <c>collected_resource</c> = 1 (0x2bc to its StateMachine): its collection flare, the beam to
    /// the Sulphur it gathered.</summary>
    public static Task Flare(ClientState conn, DeepwaterRun.Lantern lantern) =>
        SendStates(conn, lantern, collected: true, shrinking: false, extinguished: false, "collected_resource = 1");

    /// <summary>The lantern's StateMachine (0x2bc) with every state the server sets: the drop animation, the collection
    /// flare and the collapse's <c>shrinking</c> (the flicker) and <c>extinguished</c>.</summary>
    public static async Task SendStates(ClientState conn, DeepwaterRun.Lantern lantern, bool collected, bool shrinking, bool extinguished, string why)
    {
        if (QuestObjectTypes.Find(DeepwaterConstants.Lantern) is not { } info || States.Value is not { } states) return;
        int index = info.Components.ToList().IndexOf("StateMachine");
        if (index < 0) return;
        var update = states.Set("drop", lantern.Drop).Set("dropped", lantern.Drop).Set("collected_resource", collected ? 1 : 0)
            .Set("shrinking", shrinking ? 1 : 0).Set("extinguished", extinguished ? 1 : 0);
        await global::Game.SendWorldRecord(PlayerOf(conn), lantern.Id, InstanceEntities.StateMachineUpdate(lantern.Id, (byte)index, update),
            $"0x02bc Allflame lantern {lantern.Order} 0x{lantern.Id:x} {why}");
    }

    /// <summary>S→C 0x2c3: the lantern's light task restarts from its new data (a, rate_a, b, rate_b).</summary>
    public static Task SendTask(ClientState conn, DeepwaterRun.Lantern lantern, string why) =>
        global::Game.SendWorldRecord(PlayerOf(conn), lantern.Id, InstanceEntities.UpdateTask(lantern.Id, DeepwaterConstants.LanternTask, lantern.TaskData()),
            $"0x02c3 Allflame lantern {lantern.Order} 0x{lantern.Id:x} task (a {lantern.A:0.###}, {lantern.RateA:0.###}/s, b {lantern.B:0.###}, {lantern.RateB:0.###}/s): {why}");

    /// <summary>S→C 0x2c5: the lantern's light task ends (it went out).</summary>
    public static Task RemoveTask(ClientState conn, DeepwaterRun.Lantern lantern) =>
        global::Game.SendWorldRecord(PlayerOf(conn), lantern.Id, InstanceEntities.RemoveTask(lantern.Id, DeepwaterConstants.LanternTask),
            $"0x02c5 Allflame lantern {lantern.Order} 0x{lantern.Id:x} task removed (extinguished)");

    public static async Task SendCount(ClientState conn, DeepwaterRun run)
    {
        int placed, total;
        lock (run.Gate) (placed, total) = (run.Placed, run.Total);
        if (DeepwaterRuleset.Lanterns(placed, total) is { } count)
            await PlayerOf(conn).Send(count, $"0x02c9 ruleset DeepwaterRules mode 2: lanterns {placed}/{total}");
    }

    /// <summary>A new lantern of the run at (x, y): lit at full radius for good (task (1, 0, 1, 0)), a random drop animation
    /// 1-3 (G: the .ot has three, which one the real server picked is unknown).</summary>
    public static DeepwaterRun.Lantern Add(ClientState conn, DeepwaterRun run, int x, int y, bool counts)
    {
        lock (run.Gate)
        {
            var lantern = new DeepwaterRun.Lantern(conn.TakeObjectId(), x, y, 1, 0, 1, 0, conn.Clock.Elapsed.TotalSeconds,
                Random.Shared.Next(1, 4), run.Lanterns.Count);
            run.Lanterns.Add(lantern);
            if (counts) run.Placed++;
            return lantern;
        }
    }

    /// <summary>Why the server refuses a lantern asked for by this player, or null to place it: the sender holds the
    /// lanterns (QuestFlags 3550), the run is still exploring and has lanterns left, the player is alive, and the
    /// client's snap (<see cref="Snap"/>) finds a cell for the cursor. The client does not ask for the player to stand in
    /// light, so neither does the server: a player who has just stepped into the dark may still place one to save
    /// themselves.</summary>
    public static string? Refusal(ClientState conn, DeepwaterRun run, (int X, int Y) cursor, out (int X, int Y) at, out string note)
    {
        (at, note) = (cursor, "");
        if (!CharacterName(conn).Equals(run.Controller, StringComparison.OrdinalIgnoreCase)
            || !ClientHasQuestFlag(conn, DeepwaterConstants.LanternController)) return "not the lantern controller";
        if (run.Phase != DeepwaterRun.RunPhase.Exploring) return $"the run is {run.Phase}";
        lock (run.Gate) if (run.Placed >= run.Total) return $"no lanterns left ({run.Placed}/{run.Total})";
        if (conn.PlayerLife == 0) return "dead";
        if (Snap(conn, run, cursor, out string why) is not { } snapped) return why;
        (at, note) = (snapped, why);
        return null;
    }

    /// <summary>The cell the client puts a lantern at for the cursor cell its 0x2ca carries (the client sends the cursor,
    /// not its snap): <c>0x141fc5800</c> with its candidate filter <c>0x141fc5d00</c>. Each lantern of the run, lit or
    /// not (the client never removes an anchor), proposes the cell <see cref="DeepwaterConstants.BaseLanternPlacementRange"/>
    /// away towards the cursor (rounded as <c>0x140672360</c>); a candidate is dropped 60 or more cells from the cursor,
    /// 100 or more from the player, behind the player and 30 or more from them, or within range − 5 of any lantern. The
    /// nearest to the cursor wins (only it is tried); a cell that is not walkable (client layer 0 below 4,
    /// <c>0x141d888f0</c>) moves to the first walkable one of rings 1-9 around it. Null with the reason when none fits.
    /// The player-distance tests run with the client's limits, then, when no candidate fits, with <see cref="Slack"/>
    /// more, as the server's estimate of the player trails the client's.
    /// With a cell, <paramref name="why"/> is a note for the log (empty, or the blocked line).</summary>
    public static (int X, int Y)? Snap(ClientState conn, DeepwaterRun run, (int X, int Y) cursor, out string why)
    {
        var player = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        int range = DeepwaterConstants.BaseLanternPlacementRange;
        long apart = (long)(range - 5) * (range - 5);
        var anchors = run.Snapshot();
        // The client's limits first, so the server picks the client's candidate whenever its estimate of the player is
        // close; the slackened ones only when that finds none (the estimate trails the client's).
        var best = Pick(PlayerReach, Behind) ?? Pick(PlayerReach + Slack, Behind + Slack);
        if (best is not { } at)
        {
            why = $"no lantern in reach of the cursor ({cursor.X}, {cursor.Y}) with the player at ({player.X}, {player.Y})";
            return null;
        }
        if (!Walkable(at.X, at.Y))
        {
            (int X, int Y)? found = null;
            for (int r = 1; r <= 9 && found is null; r++)
                for (int dy = -r; dy <= r && found is null; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                        if (Walkable(at.X + dx, at.Y + dy)) { found = (at.X + dx, at.Y + dy); break; }
                    }
            if (found is not { } moved)
            {
                why = $"no walkable cell within 9 of ({at.X}, {at.Y})";
                return null;
            }
            at = moved;
        }
        // The client also refuses a line from the player crossing its layer 1 below 1 (0x141d8a7f0). G: with the
        // server's trailing estimate of the player that test would refuse lanterns the client allowed, so it is logged.
        why = conn.Layout.Sight is { } sight && ClientPathfinder.LineBelow(player, at, sight, 1)
            ? $" (the line from the player's estimate ({player.X}, {player.Y}) is blocked; placed anyway)" : "";
        return at;

        (int X, int Y)? Pick(int reach, int back)
        {
            long far = (long)reach * reach + 1, behind = (long)back * back + 1;
            (int X, int Y)? best = null;
            long bestD2 = long.MaxValue;
            foreach (var a in anchors)
            {
                int vx = cursor.X - a.X, vy = cursor.Y - a.Y;
                float length = MathF.Sqrt((float)vx * vx + (float)vy * vy);
                float s = length > 0 ? range / length : 0;
                (int X, int Y) p = (a.X + (int)MathF.Floor(vx * s + 0.5f), a.Y + (int)MathF.Floor(vy * s + 0.5f));
                long d2 = Square(p.X - cursor.X, p.Y - cursor.Y);
                if (d2 >= CursorReach) continue;
                long wx = p.X - player.X, wy = p.Y - player.Y, w2 = wx * wx + wy * wy;
                if (w2 >= far) continue;
                if (wx * (cursor.X - player.X) + wy * (cursor.Y - player.Y) < 0 && w2 >= behind) continue;
                if (anchors.Any(b => Square(b.X - p.X, b.Y - p.Y) <= apart)) continue;
                // The client sorts by d2 ascending (0x141fd6240); the first kept of equals wins here (G for ties).
                if (d2 < bestD2) (best, bestD2) = (p, d2);
            }
            return best;
        }
        bool Walkable(int x, int y) => conn.Layout.WalkableFor(x, y, 3);
        static long Square(long x, long y) => x * x + y * y;
    }
}
