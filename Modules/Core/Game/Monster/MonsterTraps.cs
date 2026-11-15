using System.Net.Sockets;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

/// <summary>
/// A monster's traps, for any monster whose skill is a trap (ActiveSkillTypes Trapped): the same object, throw, landing,
/// arming and lifetime as the player's (PlacedSkills: PlacedObjectPath, PlacedFlightSeconds, PlacedArmSeconds,
/// PlacedDurationSeconds, TrapTriggerRadius), on the monsters' team. Set off by the player or a minion of theirs within
/// its trigger radius, it plays its skill from where it lies (MonsterAttack.Trap: an area hit or its projectiles), with
/// its owner's stats, whatever its owner does meanwhile.
/// </summary>
public static partial class Game
{
    /// <summary>Metadata/Monsters/Monster.ot: base_number_of_traps_allowed 3, every monster's.</summary>
    public const int MonsterTrapsAllowed = 3;

    // The trap's skill: its own stats with the trap flags cleared, as the player's proxy is (PlaceSkill), so the object
    // plays the skill rather than another throw.
    static GrantedSkill? MonsterTrapSkill(ClientState conn, uint owner, MonsterAttack a, int effect, byte level,
        IReadOnlyDictionary<uint, int> stats)
    {
        var payload = new Dictionary<uint, int>(stats);
        foreach (string flag in new[] { "is_trap", "is_remote_mine", "base_skill_is_trapped", "base_skill_is_mined" })
            if (Stats.Key(flag) is uint key) payload[key] = 0;
        var catalog = SkillsFor(conn);
        return catalog.FindByRow(0x4000, effect, catalog.MaxLevelAt(effect, level), [.. payload.Select(p => (p.Key, p.Value))]) is { } skill
            ? skill with { Parent = (owner, a.SkillId) } : null;
    }

    /// <summary>The throw at the action's contact: 1 + number_of_additional_traps_to_throw traps toward the aim, the oldest of
    /// the owner's beyond Monster.ot's three (plus number_of_additional_traps_allowed) taken away.</summary>
    static async Task ThrowMonsterTraps(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        uint id, SpawnedMonster m, MonsterAttack a, MonsterAttack payload, (int X, int Y) aim)
    {
        if (a.Effect is not int effect) return;
        var stats = GolemData.Skill(SkillsFor(conn), effect, m.Level).Stats;
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        int count = Math.Clamp(1 + Get("number_of_additional_traps_to_throw"), 1, 20);
        int limit = Math.Max(1, MonsterTrapsAllowed + Get("number_of_additional_traps_allowed"));
        double flight = PlacedFlightSeconds(false, Get);
        string path = PlacedObjectPath(false, Get);
        if (MonsterTrapSkill(conn, id, a, effect, m.Level, stats) is not { } skill) return;
        for (int i = 0; i < count; i++)
        {
            uint born = 0;
            (int X, int Y) from = default;
            double landsAt = 0;
            await SendWorldBatch(conn, [], admission =>
            {
                lock (conn.Monsters)
                {
                    if (!MonsterPresent(conn, id, m) || !conn.Layout.WalkableFor(aim.X, aim.Y, 0)) return null;
                    double now = AreaTime(conn);
                    from = m.Walk.Position(now);
                    var bytes = new WorldRecordBuilder(conn, admission);
                    var owned = conn.MonsterPlaced.Traps.Values.Where(t => t.Owner == id).OrderBy(t => t.Armed).ToList();
                    for (int k = 0; k + limit <= owned.Count; k++)
                    {
                        conn.MonsterPlaced.Traps.Remove(owned[k].Id);
                        bytes.Remove(owned[k].Id);
                    }
                    uint trap = conn.TakeObjectId();
                    landsAt = now + flight;
                    conn.MonsterPlaced.Traps[trap] = new(trap, id, m, payload, aim, landsAt, landsAt + PlacedArmSeconds,
                        landsAt + PlacedDurationSeconds(false, Get), TrapTriggerRadius(Get)) { Landed = false };
                    bytes.Create(trap, InstanceEntities.CreateObject(trap, InstanceEntities.ObjectTypeHash(path),
                        new TrapMineObject(from.X, from.Y, false, [skill], PlacedObjectStats(false, flight), Team: 1).Blob()));
                    bytes.Presentation(trap, InstanceProtocol.EnterWakeZone(trap));
                    if (!conn.LockstepClient)
                        foreach (var record in PlacedThrowRecords(conn, trap, from, aim, flight)) bytes.Presentation(trap, record);
                    born = trap;
                    return bytes.Build();
                }
            });
            if (born == 0) return;
            if (conn.LockstepClient)
                await SendWorldRecords(remote, stream, ciphers, conn, born, () =>
                {
                    lock (conn.Monsters)
                        return conn.MonsterPlaced.Traps.ContainsKey(born) && !conn.Closed && !conn.LeavingArea
                            ? [.. PlacedThrowRecords(conn, born, from, aim, landsAt - AreaTime(conn))] : null;
                }, "throw a monster's trap after birth");
            Console.WriteLine($"[gateway] {remote} monster 0x{id:x} ({m.Label}) throws trap 0x{born:x} {path} to ({aim.X}, {aim.Y})");
        }
    }

    /// <summary>Each tick: traps land and arm, leave when their time is up or their owner is gone, and go off when the
    /// player or one of their minions stands within the trigger radius.</summary>
    static async Task MonsterTrapsTick(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (conn.MonsterPlaced.Traps.Count == 0) return;
        List<(MonsterPlacedObjects.Trap Trap, (int X, int Y) Aim)> fired = [];
        await SendWorldBatch(conn, [], admission =>
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.Paused) return null;
                double now = AreaTime(conn);
                var bytes = new WorldRecordBuilder(conn, admission);
                foreach (var trap in conn.MonsterPlaced.Traps.Values.ToArray())
                {
                    if (conn.LockstepClient && !conn.WorldOutput.PublishedObject(trap.Id).IsReady) continue;
                    bool owner = ReferenceEquals(conn.Monsters.GetValueOrDefault(trap.Owner), trap.Monster);
                    if (!owner || now >= trap.Until)
                    {
                        conn.MonsterPlaced.Traps.Remove(trap.Id);
                        bytes.Remove(trap.Id);
                        continue;
                    }
                    if (!trap.Landed && now >= trap.LandAt)
                    {
                        trap.Landed = true;
                        PlacedLandRecords(conn, trap.Id, trap.At, bytes);
                    }
                    if (now < trap.Armed || conn.PlayerLife == 0 || InGracePeriod(conn)) continue;
                    (int X, int Y)? enemy = Dist(conn.Walk.Position(now), trap.At) <= trap.TriggerRadius ? conn.Walk.Position(now)
                        : conn.Golems.All.Values.Concat(conn.Reservations.Fighters.Values).Concat(conn.Summons.Fighters.Values)
                            .Where(g => !g.Monster.Dead && g.Monster.Life > 0)
                            .Select(g => g.Monster.Walk.Position(now))
                            .Where(p => Dist(p, trap.At) <= trap.TriggerRadius)
                            .Select(p => ((int X, int Y)?)p).FirstOrDefault();
                    if (enemy is not { } aim) continue;
                    bytes.World(trap.Id, InstanceProtocol.ActionStarted(trap.Id, trap.At.X, trap.At.Y, aim.X, aim.Y, 0x4000, 0xffff, 0x0408));
                    conn.MonsterPlaced.Traps.Remove(trap.Id);
                    fired.Add((trap, conn.Walk.Position(now)));
                }
                return bytes.Count > 0 ? bytes.Build() : null;
            }
        });
        foreach (var (trap, aim) in fired)
        {
            Console.WriteLine($"[gateway] {remote} trap 0x{trap.Id:x} of monster 0x{trap.Owner:x} ({trap.Monster.Label}) is set off");
            bool Owner() { lock (conn.Monsters) return ReferenceEquals(conn.Monsters.GetValueOrDefault(trap.Owner), trap.Monster); }
            ushort sequence = conn.NextActionSequence();
            if (trap.Payload.Projectile is { } projectile)
                LaunchDetachedMonsterProjectile(remote, stream, ciphers, conn, trap.Owner, trap.Monster, trap.Payload, projectile,
                    trap.At, Owner, sequence);
            else if (trap.Payload.AreaRadius > 0)
                await MonsterDetachedAreaHit(remote, stream, ciphers, conn, trap.Owner, trap.Monster, trap.Payload,
                    trap.Payload.AreaAtSelf ? trap.At : aim, trap.Payload.AreaRadius, sequence, Owner);
            await SendWorldRemoval(remote, stream, ciphers, conn, trap.Id, $"0x0013 trap 0x{trap.Id:x} set off");
        }
    }
}
