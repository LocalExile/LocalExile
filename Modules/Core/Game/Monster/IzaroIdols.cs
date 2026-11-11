using System.Net.Sockets;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using static Game;
using static Program;

/// <summary>
/// An idol of Izaro's Chrono Statues (IzaroChronos) while it is up: it channels ChronosStatuesCast while its life fills
/// (the client poses it and lights its glow by that life), then casts its circles, which burst <see cref="IzaroChronos.FuseSeconds"/>
/// later, and starts again from 1 life. It cannot die: its life stops at 1 % (LifePhaseGate).
/// </summary>
static class IzaroIdols
{
    // The idol's actor skill for its channel (the official monsters' ids start at 0x4000, KingInMists.Kit).
    const ushort ChannelSkillId = 0x4000;

    // How often its life is filled and sent.
    const double TickSeconds = 0.2;

    public static bool IsIdol(MonsterCatalog.Variety? variety) => variety is not null && IzaroChronos.IsIdol(variety.Id);

    // Its effects: the beacons (its first, the actor's default) and the channel (ChronosStatuesCast).
    static int ChannelEffect(MonsterCatalog.Variety variety) => variety.GrantedEffects is [_, int channel, ..] ? channel : -1;

    /// <summary>The actor's skill list: its default skill, then the channel, with the 0x1ab id it names.</summary>
    internal static IReadOnlyList<GrantedSkill>? SkillsFor(ClientState conn, MonsterCatalog.Variety variety, byte level, MonsterAttack attack, GrantedSkill? skill)
    {
        if (!IsIdol(variety) || skill is null || ChannelEffect(variety) is not (>= 0 and var effect)) return null;
        var catalog = Game.SkillsFor(conn);
        // A spell's cast-time stats: the client's cast-duration path crashed on an actor record with only attack stats
        // (Monsters.cs, the King's Death Effigy).
        var stats = (attack with { SkillId = ChannelSkillId, Spell = true, Seconds = catalog.CastTimeMs(effect) / 1000.0 }).SkillStats();
        return catalog.FindByRow(ChannelSkillId, effect, catalog.MonsterEffectLevel(effect, level), stats) is { } channel ? [skill, channel] : null;
    }

    /// <summary>Its fill and cast, until <paramref name="up"/> says it is no longer up.</summary>
    internal static async Task Run(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster idol,
        int element, double fillSeconds, Func<bool> up)
    {
        if (idol.Variety is not { } variety || idol.Attack is not { } plain || variety.GrantedEffects is not [int beacon, ..]) return;
        var catalog = Game.SkillsFor(conn);
        int beaconLevel = catalog.MonsterEffectLevel(beacon, idol.Level);
        var burst = plain with { Damage = catalog.HitDamage(beacon, beaconLevel), Spell = true, Scripted = true, Projectile = null, Release = null };
        try
        {
            while (up())
            {
                int action;
                uint life;
                (int X, int Y) at;
                lock (conn.Monsters)
                {
                    action = ++idol.ActionGeneration;
                    idol.Life = life = Math.Min(idol.Life, 1u);
                    at = idol.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                }
                await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(id, life), null);
                if (!await SendMonsterAction(remote, stream, ciphers, conn, id, idol, action, () =>
                    InstanceProtocol.ActionStarted(id, at.X, at.Y, at.X, at.Y, ChannelSkillId, conn.NextActionSequence(), conn.MonsterOrderFlags),
                    $"0x01ab idol 0x{id:x} fills")) return;
                // Its life fills; hits take it down meanwhile.
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(TickSeconds));
                    if (!up()) return;
                    bool full;
                    lock (conn.Monsters)
                    {
                        if (idol.Dead) return;
                        idol.Life = life = (uint)Math.Min(idol.MaxLife, idol.Life + Math.Ceiling(idol.MaxLife * TickSeconds / fillSeconds));
                        full = life >= idol.MaxLife;
                    }
                    await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(id, life), null);
                    if (full) break;
                }
                // Full: it casts its circles (its act's SpellAreaOfEffect: Deactivate_cast and the gold nova) and its life goes to 1.
                List<(int X, int Y)> spots;
                lock (conn.Monsters)
                {
                    idol.Life = 1;
                    spots = Spots(conn);
                }
                await Send(remote, stream, ciphers, [.. InstanceProtocol.ActionEnded(id), .. InstanceProtocol.ActorAnimation(id, 1, SummonAnimation),
                    .. InstanceProtocol.LifeUpdate(id, 1)], $"idol 0x{id:x} casts {spots.Count} circles");
                _ = Circles(remote, stream, ciphers, conn, id, idol, burst, element, spots);
                await Task.Delay(TimeSpan.FromSeconds(IzaroChronos.SitSeconds));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} idol 0x{id:x} stopped: {ServerLog.Describe(ex)}"); }
    }

    // Where its circles go: one on the player, the rest at walkable cells round the player within the placement radius,
    // apart from each other. Under the Monsters lock.
    static List<(int X, int Y)> Spots(ClientState conn)
    {
        var player = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        var spots = new List<(int X, int Y)> { player };
        for (int tries = 0; spots.Count < IzaroChronos.Circles && tries < 200; tries++)
        {
            double angle = Random.Shared.NextDouble() * Math.PI * 2, reach = Math.Sqrt(Random.Shared.NextDouble()) * IzaroChronos.PlacementRadius;
            (int X, int Y) cell = ((int)Math.Round(player.X + Math.Cos(angle) * reach), (int)Math.Round(player.Y + Math.Sin(angle) * reach));
            if (!conn.Layout.WalkableFor(cell.X, cell.Y, 0) || conn.Barrier.Contains(cell)) continue;
            if (spots.Any(s => Distance(s, cell) < IzaroChronos.CircleSpacing)) continue;
            spots.Add(cell);
        }
        return spots;
    }

    static double Distance((int X, int Y) a, (int X, int Y) b) => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    // The circles show for their fuse, then burst: the player inside one takes its hit (one hit, however many).
    static async Task Circles(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster idol,
        MonsterAttack burst, int element, List<(int X, int Y)> spots)
    {
        var layout = conn.Layout;
        bool Gone() => conn.Closed || conn.LeavingArea || !ReferenceEquals(conn.Layout, layout);
        try
        {
            var circles = new List<uint>();
            List<byte> show = [];
            lock (conn.Monsters)
                foreach (var s in spots)
                {
                    uint effect = conn.NextObjectId++;
                    circles.Add(effect);
                    show.AddRange([.. InstanceEntities.CreateObject(effect, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                        new EffectObject(s.X, s.Y, IzaroChronos.CircleArt(element)).Blob()), .. InstanceProtocol.EnterWakeZone(effect)]);
                }
            await Send(remote, stream, ciphers, [.. show], null);
            await Task.Delay(TimeSpan.FromSeconds(IzaroChronos.FuseSeconds));
            if (Gone()) return;
            var bursts = new List<uint>();
            List<byte> burstBytes = [.. circles.SelectMany(InstanceProtocol.RemoveObject)];
            lock (conn.Monsters)
                foreach (var s in spots)
                {
                    uint effect = conn.NextObjectId++;
                    bursts.Add(effect);
                    burstBytes.AddRange([.. InstanceEntities.CreateObject(effect, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                        new EffectObject(s.X, s.Y, IzaroChronos.BurstArt(element)).Blob()), .. InstanceProtocol.EnterWakeZone(effect)]);
                }
            await Send(remote, stream, ciphers, [.. burstBytes], null);
            MonsterHitOutcome? outcome = null;
            await Send(remote, stream, ciphers, [], $"idol 0x{id:x}'s circles burst", atGate: () =>
            {
                lock (conn.Monsters)
                {
                    if (InGracePeriod(conn) || !MonsterPresent(conn, id, idol)) return null;
                    double now = Math.Max(ProjectileWorldTime(conn), conn.Clock.Elapsed.TotalSeconds);
                    var here = conn.Walk.Position(now);
                    var nearest = spots.MinBy(s => Distance(s, here));
                    if (Distance(nearest, here) > IzaroChronos.CircleRadius) return null;
                    outcome = CommitMonsterHitLocked(remote, conn, id, idol, burst, now, here, nearest, checkRange: false, sequence: conn.NextActionSequence());
                    return outcome?.HitPacket;
                }
            });
            if (outcome is not null) await PresentMonsterHit(remote, stream, ciphers, conn, id, outcome);
            await Task.Delay(TimeSpan.FromSeconds(IzaroChronos.ExplodeSeconds));
            if (!Gone()) await Send(remote, stream, ciphers, [.. bursts.SelectMany(InstanceProtocol.RemoveObject)], null);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} idol 0x{id:x}'s circles stopped: {ServerLog.Describe(ex)}"); }
    }
}
