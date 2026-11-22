using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using static Game;
using static Program;

/// <summary>
/// The visible body of an essence's skill-carrier daemon. Greed's blood daemon and Torment's orb daemon are both the
/// client's generic <c>Metadata/Monsters/Daemon/Daemon.ao</c> with <c>UseSkillOnceDaemon</c> and <c>NoAi.ais</c> — an
/// invisible helper that casts once. Their substance is drawn separately: Greed's red bubble is the effect art
/// <c>BloodOrbBuildupEffect</c> (<c>essence/bloodball/rig.ao</c>), and Torment's orb is BuffDefinitions 706
/// <c>orb_of_elemental_essence</c> with BuffVisuals 604 of the same name. Without either, the projectiles appear to come
/// out of an empty point.
/// </summary>
public static partial class MapEssences
{
    const string BloodOrbArt = "Metadata/Effects/Spells/monsters_effects/essence/bloodball/rig.ao";
    // The client's own orb buff (706, BuffVisuals 604) carries only a DDS icon, no world art, so it draws nothing in the
    // world. A monster ball-lightning rig is this replica's visible body for the carrier until the orb's own art is found.
    const string StormOrbArt = "Metadata/Effects/Spells/monsters_effects/Breach/Lightning/ball_lightning/rig.ao";
    const string BloodDaemonVariety = "Metadata/Monsters/Daemon/EssenceBloodProjectileDaemon";
    const string OrbDaemonVariety = "Metadata/Monsters/Daemon/EssenceOrbOfStormsDaemon";

    /// <summary>Greed (2) and Torment (6) are the only types whose daemon is an invisible carrier.</summary>
    static readonly HashSet<int> CarrierTypes = [2, 6];

    sealed class OrbBody { public uint Art; public double Born = double.NaN; public bool Burst; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SpawnedMonster, OrbBody> OrbBodies = new();

    /// <summary>Called from the essence tick: gives each of the rare's invisible carrier daemons its visible body once, and
    /// takes the body away when the daemon dies. Only essence areas run this, and only while a released rare lives.</summary>
    static void TickOrbBodies(ClientState conn)
    {
        if (conn.Closed || conn.LeavingArea || !Areas.TryGetValue(conn.Instance, out var area) || area.Monoliths is null) return;
        // Only Greed and Torment have a carrier daemon, so the monster scan below runs only for a released rare that
        // actually holds one of them. Scanning every monster on every tick for any essence rare cost a map's whole
        // population per tick and lagged the server on a Misery monolith, which has no carrier at all.
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
                && m.Variety?.Id is BloodDaemonVariety or OrbDaemonVariety)];
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
                body.Art = SpawnEffect(conn, m.Walk.Position(now), m.Variety?.Id == BloodDaemonVariety ? BloodOrbArt : StormOrbArt);
            // Greed's orb is a bomb, not a shooter: it builds up, bursts once and is gone. Its nova's own cast is 1.5 s, so
            // the carrier is retired after that cast with no kill, reward or experience — it does not stand and keep firing.
            if (m.Variety?.Id == BloodDaemonVariety && !body.Burst && now >= body.Born + 1.5)
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