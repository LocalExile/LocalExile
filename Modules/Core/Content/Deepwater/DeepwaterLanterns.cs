using GameServer.Core;
using GameServer.Core.Protocol.Instance;
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
    /// <summary>The client's reach from the player to the new lantern (<c>|C − player|² &lt; 10001</c>).</summary>
    const int PlayerReach = 100;

    static readonly Lazy<StateMachineData?> States = new(() => ObjectTypes.All().Where(t => t.Type == DeepwaterConstants.Lantern)
        .Select(t => ObjectTypes.StateMachine(t.Row, ObjectTypes.SharedStates())).FirstOrDefault());

    /// <summary>The run's lantern count: a Chart's or a Voyage's, a Voyage 40 more once the league's first Voyage is done
    /// (league quest flag 1893; G that this flag is the one, the wiki gives 65).</summary>
    public static int Total(ClientState conn, bool voyage)
    {
        if (!voyage) return DeepwaterConstants.BaseLanternCount1x1;
        bool more;
        lock (conn.Monsters) more = conn.LeagueQuestFlags.Contains(DeepwaterConstants.VoyagesEnabledLeagueFlag);
        return DeepwaterConstants.BaseLanternCountVoyage + (more ? DeepwaterConstants.VoyagesEnabledExtraLanterns : 0);
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

    /// <summary>Why the server refuses a lantern at (x, y) asked for by this player, or null to place it: the sender holds
    /// the lanterns (QuestFlags 3550), the run is still exploring and has lanterns left, the player is alive and in
    /// light, the cell is walkable and within reach of the player, one lit lantern is at the placement range and none
    /// is nearer than the light radius (the client's own snap rule, <c>0x141fc5800</c> and <c>0x141fc5d00</c>).</summary>
    public static string? Refusal(ClientState conn, DeepwaterRun run, int x, int y)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        if (!CharacterName(conn).Equals(run.Controller, StringComparison.OrdinalIgnoreCase)
            || !ClientHasQuestFlag(conn, DeepwaterConstants.LanternController)) return "not the lantern controller";
        if (run.Phase != DeepwaterRun.RunPhase.Exploring) return $"the run is {run.Phase}";
        lock (run.Gate) if (run.Placed >= run.Total) return $"no lanterns left ({run.Placed}/{run.Total})";
        if (conn.PlayerLife == 0) return "dead";
        var at = conn.Walk.Position(now);
        if (!run.InLight(at, now)) return $"the player at ({at.X}, {at.Y}) is not in light";
        if (conn.Layout.Walkable?.Invoke(x, y) == false) return "not walkable";
        long reach = PlayerReach + Slack;
        long pdx = x - at.X, pdy = y - at.Y;
        if (pdx * pdx + pdy * pdy > reach * reach) return $"{Math.Sqrt(pdx * pdx + pdy * pdy):0} cells from the player at ({at.X}, {at.Y})";
        int range = DeepwaterConstants.BaseLanternPlacementRange, apart = range - 5;
        bool anchored = false;
        foreach (var l in run.Snapshot())
        {
            if (l.Radius(now) <= 0) continue;
            double d = Math.Sqrt((double)(x - l.X) * (x - l.X) + (double)(y - l.Y) * (y - l.Y));
            if (d < apart - Slack) return $"{d:0} cells from lantern {l.Order} (at least {apart})";
            if (Math.Abs(d - range) <= Slack) anchored = true;
        }
        return anchored ? null : $"no lit lantern {range} cells away";
    }
}
