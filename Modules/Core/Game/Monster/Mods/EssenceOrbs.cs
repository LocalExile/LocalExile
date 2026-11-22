using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using static Game;
using static Program;

/// <summary>
/// The visible body of Greed's skill-carrier daemon (<c>EssenceBloodProjectileDaemon</c>): the client's generic
/// <c>Metadata/Monsters/Daemon/Daemon.ao</c> with <c>UseSkillOnceDaemon</c> and <c>NoAi.ais</c> is an invisible helper
/// that casts once. Its red bubble is drawn separately via <c>BloodOrbBuildupEffect</c> (<c>essence/bloodball/rig.ao</c>),
/// then retired after its 1.5 s burst. (Torment's Ball Lightning casts directly through <c>EssenceTorment.cs</c>.)
/// </summary>
public static partial class MapEssences
{
    const string BloodOrbArt = "Metadata/Effects/Spells/monsters_effects/essence/bloodball/rig.ao";
    const string BloodDaemonVariety = "Metadata/Monsters/Daemon/EssenceBloodProjectileDaemon";

    /// <summary>Greed (2) is the essence type whose daemon is an invisible blood-orb carrier.</summary>
    static readonly HashSet<int> CarrierTypes = [2];

    sealed class OrbBody { public uint Art; public double Born = double.NaN; public bool Burst; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SpawnedMonster, OrbBody> OrbBodies = new();

    /// <summary>Called from the essence tick: gives each of the rare's invisible blood-orb carrier daemons its visible body
    /// once, bursts it after its 1.5 s cast and takes the body away when it dies.</summary>
    static void TickOrbBodies(ClientState conn)
    {
        if (conn.Closed || conn.LeavingArea || !Areas.TryGetValue(conn.Instance, out var area) || area.Monoliths is null) return;
        var rares = new HashSet<uint>();
        foreach (var mono in area.Monoliths)
        {
            if (mono.Stage < 4 || mono.Monster == 0) continue;
            foreach (int row in mono.Essences)
                if (MapEssenceCatalog.Find(row) is { } e && CarrierTypes.Contains(e.Type)) { rares.Add(mono.Monster); break; }
        }
        if (rares.Count == 0) return;
        SpawnedMonster[] carriers;
        lock (conn.Monsters)
            carriers = [.. conn.Monsters.Values.Where(m => m.SummonerId is uint s && rares.Contains(s)
                && m.Variety?.Id == BloodDaemonVariety)];
        foreach (var m in carriers)
        {
            var body = OrbBodies.GetOrCreateValue(m);
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (double.IsNaN(body.Born)) body.Born = now;
            if (m.Dead || m.Life == 0)
            {
                if (body.Art != 0) { RemoveEffect(conn, body.Art); body.Art = 0; }
                continue;
            }
            if (body.Art == 0)
                body.Art = SpawnEffect(conn, m.Walk.Position(now), BloodOrbArt);
            // Greed's orb is a bomb, not a shooter: it builds up, bursts once and is gone. Its nova's own cast is 1.5 s, so
            // the carrier is retired after that cast with no kill, reward or experience — it does not stand and keep firing.
            if (!body.Burst && now >= body.Born + 1.5)
            {
                body.Burst = true;
                uint boom = SpawnEffect(conn, m.Walk.Position(now), BloodOrbArt, 1.6f);
                _ = RemovePulseLater(conn, boom, 0.8);
                if (body.Art != 0) { RemoveEffect(conn, body.Art); body.Art = 0; }
                lock (conn.Monsters)
                {
                    m.NoRewards = true; m.Dead = true; m.Life = 0; m.ActionGeneration++;
                    conn.Monsters.Remove(m.ObjectId);
                    conn.MonsterViews.Remove(m.ObjectId); conn.AsleepMonsters.Remove(m.ObjectId);
                    conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(m.ObjectId), "its blood orb burst", m.ObjectId, m,
                        PlayerMinions.OutputKind.Remove, WorldId: m.ObjectId, RemovesObject: true));
                }
            }
        }
    }
}