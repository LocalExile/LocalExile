using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;

/// <summary>
/// Sirus, Awakener of Worlds (MonsterVarieties <c>Metadata/Monsters/AtlasExiles/AtlasExile5</c>, WorldAreas
/// AtlasExilesBoss5 "Eye of the Storm", level 84; the Gauntlet and standalone arenas are the same fight). The 36 Orion
/// effects in his GrantedEffectsKeys are the kit played here: the beam blast he spams, the corridor "Die" beam, the five
/// circle-maze blasts that make up one sweep, his teleport, the multi-beam barrage, the clone beam, the meteor arena and
/// the ground it leaves burning.
/// <para>Damage comes from each effect's own row (<see cref="AtlasBosses.SirusDamage"/>, the effect's base physical
/// damage read as a fraction of the variety's damage the way a monster spell scales), so the numbers move with the
/// area's level. The beams carry no projectile record: their reach is the attack's own range, and the corridor, the
/// meteors and the sweep fan decide their own hit from the geometry the controller draws.</para>
/// <para>He never swings a weapon, so his record's basic skill is the beam blast and the rotation is what keeps him
/// moving: a blink when the player closes in, a blink back into reach when they run away. A blink moves him and plays
/// <c>teleporting/orion_teleport.ao</c> at both ends with no action of its own, because an action would also land its
/// contact.</para>
/// </summary>
sealed class SirusFight(uint id, SpawnedMonster boss, ClientState conn) : AtlasBossFight(id, boss, conn)
{
    protected override string Name => "[Sirus]";
    protected override ushort ImmunityRow => AtlasBosses.SirusImmunityRow;
    protected override string ShieldArt => AtlasBosses.SirusShieldArt;
    protected override string ShieldOffArt => AtlasBosses.SirusShieldOffArt;
    // Where he goes for an intermission: the throne he starts the fight on, i.e. the point this fight was made at.
    (int X, int Y)? stage;
    // The seated stage stays where he sat; the walking Sirus goes back to the arena's middle marker, not to his own
    // spawn cell and never to the map entry.
    protected override (int X, int Y) Stage(ClientState conn) => stage ??= seated ? centre : ArenaCentre(conn, boss);

    readonly (int X, int Y) centre = Pose(boss, conn);
    // The throne row is the arena's first stage (AtlasBosses.IsSirusSeated): he casts from the throne and does not blink
    // off it. Killing him forms the walking Sirus and the four storms, and that one moves.
    readonly bool seated = AtlasBosses.IsSirusSeated(boss.Variety);
    double nextBeam, nextDie, nextMeteor, nextMaze, nextTriple, nextBomb, nextBlink, nextChase;

    // What a cast already owes the fight: a corridor, a meteor, a beam of a fan. From == To makes it a circle.
    readonly List<(double At, MonsterAttack Attack, (int X, int Y) From, (int X, int Y) To, double Radius)> pending = [];
    // The five circle-maze blasts leave one after another, each with a clip of its own.
    readonly Queue<(double At, int Effect, double Angle)> sweep = new();

    // The desolation the meteor arena leaves: a burning patch the player has to step out of.
    double groundUntil, groundNext;
    (int X, int Y) groundAt;
    MonsterAttack? groundTick;

    bool intermission;
    int waves;
    double waveAt;

    // His position on entry, the point the intermission returns him to.
    static (int X, int Y) Pose(SpawnedMonster boss, ClientState conn)
    {
        lock (conn.Monsters) return boss.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
    }

    protected override async Task Ticks(ClientState conn, double now)
    {
        while (sweep.Count > 0 && sweep.Peek().At <= now)
        {
            var (at, effect, angle) = sweep.Dequeue();
            var from = boss.Walk.Position(now);
            var to = conn.Layout.Near((from.X + (int)Math.Round(Math.Cos(angle) * 26),
                from.Y + (int)Math.Round(Math.Sin(angle) * 26)));
            var attack = Attack(conn, effect);
            pending.Add((at + 0.5, attack, from, to, 4.5));
            SwingWith(conn, attack with { Seconds = 0.9 }, effect);
        }
        foreach (var hit in pending.Where(h => h.At <= now).ToArray())
        {
            pending.Remove(hit);
            foreach (var player in Watchers())
                if (InReach(player.Walk.Position(now), hit.From, hit.To, hit.Radius)) await Hit(player, hit.Attack, hit.From);
        }
        if (groundTick is null) return;
        if (now >= groundUntil) { groundTick = null; return; }
        if (now < groundNext) return;
        groundNext = now + 0.5;
        foreach (var player in Watchers())
            if (Distance(player.Walk.Position(now), groundAt) <= 8) await Hit(player, groundTick, groundAt);
    }

    protected override async Task Act(ClientState conn, double now)
    {
        var watchers = Watchers();
        if (watchers.Length == 0) return;
        var pos = boss.Walk.Position(now);
        var target = watchers.MinBy(p => Distance(pos, p.Walk.Position(now)))!;
        var player = target.Walk.Position(now);
        double distance = Distance(pos, player);

        // A player in his face is blinked away from; one that runs out of reach is blinked after. Both are the teleport
        // clip's visual (orion_teleport at both ends), no action: a teleport that played an attack would also hit.
        if (!seated && distance < 12 && now >= nextBlink)
        {
            nextBlink = now + 10;
            await Blink(conn, player, now, away: true);
            return;
        }
        if (!seated && distance > 55 && now >= nextBlink)
        {
            nextBlink = now + 8;
            await Blink(conn, player, now, away: false);
            return;
        }
        if (now >= nextDie) { nextDie = now + 19; await Die(conn, player, now); return; }
        if (now >= nextMeteor) { nextMeteor = now + 17; await Meteors(conn, player, now); return; }
        if (now >= nextMaze) { nextMaze = now + 23; await Maze(conn, player, now); return; }
        if (now >= nextTriple) { nextTriple = now + 11; await Triple(conn, player, now); return; }
        if (now >= nextBomb) { nextBomb = now + 15; await Bomb(conn, player, now); return; }
        // The beam blast: his record's own skill, the one the ordinary swing played before this controller existed.
        var basic = Attack(conn, AtlasBosses.SirusBeamBlastEffect);
        if (distance <= basic.Range && now >= nextBeam)
        {
            nextBeam = now + basic.Seconds + 0.4;
            Swing(conn, AtlasBosses.SirusBeamBlastEffect);
            return;
        }
        if (!seated && distance > basic.Range && now >= nextChase)
        {
            nextChase = now + 0.75;
            double fraction = Math.Clamp((distance - basic.Range + 2) / distance, 0, 1);
            var chase = ((int)Math.Round(pos.X + (player.X - pos.X) * fraction),
                (int)Math.Round(pos.Y + (player.Y - pos.Y) * fraction));
            await Move(conn, conn.Layout.Near(chase), now);
        }
    }

    /// <summary>The corridor "Die" beam (10948): the cast, the blast at the far end, and the corridor between them that
    /// the player has 1.1 s to step out of. Its 7.5 s clip is the encounter's own, not a telegraph a fight can wait for,
    /// so the action is cut to the two seconds the corridor stays dangerous.</summary>
    async Task Die(ClientState conn, (int X, int Y) player, double now)
    {
        var from = boss.Walk.Position(now);
        double angle = Math.Atan2(player.Y - from.Y, player.X - from.X);
        var to = conn.Layout.Near((from.X + (int)Math.Round(Math.Cos(angle) * 32),
            from.Y + (int)Math.Round(Math.Sin(angle) * 32)));
        await Effect(conn, from, AtlasBosses.SirusCorridorArt, 3.4);
        await Effect(conn, to, AtlasBosses.SirusNukeBeamArt, 3.4, 1.2f);
        var attack = Attack(conn, AtlasBosses.SirusCorridorBlastEffect);
        pending.Add((now + 1.1, attack, from, to, 5.5));
        SwingWith(conn, attack with { Seconds = 2.0 }, AtlasBosses.SirusCorridorBlastEffect);
    }

    /// <summary>The meteor arena (10141): eight meteors around the player, then the desolation underfoot (10945) that
    /// burns for four seconds. The arena is what the player has to keep moving through.</summary>
    async Task Meteors(ClientState conn, (int X, int Y) player, double now)
    {
        var attack = Attack(conn, AtlasBosses.SirusMeteorEffect);
        for (int i = 0; i < 8; i++)
        {
            double angle = Math.Tau * i / 8 + Random.Shared.NextDouble() * 0.4;
            double radius = i == 0 ? 0 : 4 + Random.Shared.NextDouble() * 16;
            var at = conn.Layout.Near((player.X + (int)Math.Round(Math.Cos(angle) * radius),
                player.Y + (int)Math.Round(Math.Sin(angle) * radius)));
            double land = now + 1.0 + i * 0.35;
            pending.Add((land, attack, at, at, 6));
            await Effect(conn, at, AtlasBosses.SirusMeteorArt, land - now + 0.2, 1.4f);
        }
        groundAt = conn.Layout.Near((player.X, player.Y));
        groundUntil = now + 5;
        groundNext = now + 1.5;
        var burn = Attack(conn, AtlasBosses.SirusMeteorGroundEffect);
        groundTick = burn with { Seconds = 0.5, Damage = burn.Damage.Scale(0.5) };
        await Effect(conn, groundAt, AtlasBosses.SirusMeteorGroundArt, 5, 1.2f);
        SwingWith(conn, attack with { Seconds = 2.0 }, AtlasBosses.SirusMeteorEffect);
    }

    /// <summary>The circle maze (10949-10953): the carpet of beams leaving as one sweep, five blasts at a fifth of a turn
    /// each, so the player walks around them.</summary>
    async Task Maze(ClientState conn, (int X, int Y) player, double now)
    {
        var from = boss.Walk.Position(now);
        double angle = Math.Atan2(player.Y - from.Y, player.X - from.X);
        await Effect(conn, from, AtlasBosses.SirusCarpetArt, 4, 1.4f);
        for (int i = 0; i < 5; i++)
            sweep.Enqueue((now + 0.35 + i * 0.45, AtlasBosses.SirusCircleMazeEffect + i, angle + i * Math.Tau / 5));
        SwingWith(conn, Attack(conn, AtlasBosses.SirusCircleMazeEffect) with { Seconds = 0.9 },
            AtlasBosses.SirusCircleMazeEffect);
    }

    /// <summary>The multi-beam barrage (8098): three beams of the clone's kind leave in a fan (the clip plays the whole
    /// fan; the hits are the geometry the player has to sidestep).</summary>
    async Task Triple(ClientState conn, (int X, int Y) player, double now)
    {
        var from = boss.Walk.Position(now);
        double angle = Math.Atan2(player.Y - from.Y, player.X - from.X);
        await Effect(conn, from, AtlasBosses.SirusTripleBeamArt, 1.8, 1.2f);
        var beam = Attack(conn, AtlasBosses.SirusCloneBeamEffect);
        for (int i = -1; i <= 1; i++)
        {
            double a = angle + i * 0.35;
            var to = conn.Layout.Near((from.X + (int)Math.Round(Math.Cos(a) * 30),
                from.Y + (int)Math.Round(Math.Sin(a) * 30)));
            pending.Add((now + 1.0, beam, from, to, 4));
        }
        SwingWith(conn, Attack(conn, AtlasBosses.SirusMultiBeamEffect) with { Seconds = 1.6 },
            AtlasBosses.SirusMultiBeamEffect);
    }

    /// <summary>The beam bomb (10973): the doom geyser under the player, a 1.3 s warning and then an eight unit blast.</summary>
    async Task Bomb(ClientState conn, (int X, int Y) player, double now)
    {
        var at = conn.Layout.Near((player.X, player.Y));
        await Effect(conn, at, AtlasBosses.SirusDoomGeyserArt, 2.6, 1.2f);
        var attack = Attack(conn, AtlasBosses.SirusBeamBombEffect);
        pending.Add((now + 1.3, attack, at, at, 8));
        SwingWith(conn, attack with { Seconds = 1.6 }, AtlasBosses.SirusBeamBombEffect);
    }

    /// <summary>His teleport (6809): the clip is played at both ends by the art, and the walk estimate and the client's
    /// actor move. No action goes out — a teleport that played an attack would land that attack's contact.</summary>
    async Task Blink(ClientState conn, (int X, int Y) player, double now, bool away)
    {
        var from = boss.Walk.Position(now);
        double angle = Math.Atan2(from.Y - player.Y, from.X - player.X);
        double distance = away ? 22 : 18;
        var to = conn.Layout.Near((from.X + (int)Math.Round(Math.Cos(angle) * distance),
            from.Y + (int)Math.Round(Math.Sin(angle) * distance)));
        await Effect(conn, from, AtlasBosses.SirusTeleportArt, 1);
        await Teleport(conn, to, now);
        await Effect(conn, to, AtlasBosses.SirusTeleportArt, 1);
    }

    /// <summary>A threshold's intermission: the protective shield (atlas_orion_immunity), the return to the throne and
    /// three barrage fans around it, the way his storms take the arena while he cannot be hurt.</summary>
    protected override async Task Intermission(ClientState conn, double now)
    {
        if (!intermission)
        {
            intermission = true;
            waves = 0;
            await Shield(conn, true);
            await Teleport(conn, Stage(conn), now);
            await Effect(conn, Stage(conn), AtlasBosses.SirusShieldArt, 2.4, 1.8f);
            waveAt = now + 1.2;
            busyUntil = now + 1.2;
            return;
        }
        if (now < waveAt) return;
        if (waves == 3)
        {
            intermission = false;
            await Shield(conn, false);
            EndIntermission();
            nextBeam = now + 2;
            busyUntil = now + 1;
            return;
        }
        waves++;
        var from = Stage(conn);
        var effect = waves % 2 == 1 ? AtlasBosses.SirusMultiBeamEffect : AtlasBosses.SirusMultiBeam2Effect;
        await Effect(conn, from, waves % 2 == 1 ? AtlasBosses.SirusTripleBeamArt : AtlasBosses.SirusTripleBeam2Art, 1.8, 1.4f);
        var target = Watchers().Select(p => p.Walk.Position(now)).DefaultIfEmpty(from)
            .MinBy(p => Distance(from, p));
        double angle = Math.Atan2(target.Y - from.Y, target.X - from.X);
        var beam = Attack(conn, effect);
        for (int i = 0; i < 6; i++)
        {
            double a = angle + i * Math.Tau / 6;
            var to = conn.Layout.Near((from.X + (int)Math.Round(Math.Cos(a) * 30),
                from.Y + (int)Math.Round(Math.Sin(a) * 30)));
            pending.Add((now + 1.2, beam, from, to, 4));
        }
        SwingWith(conn, beam with { Seconds = 1.6 }, effect);
        waveAt = now + 1.8;
        busyUntil = now + 1.4;
    }

    // A hit's geometry: a circle when From == To, otherwise the corridor between them.
    static bool InReach((int X, int Y) at, (int X, int Y) from, (int X, int Y) to, double radius) =>
        from == to ? Distance(at, from) <= radius : DistanceToSegment(at, from, to) <= radius;

    static double DistanceToSegment((int X, int Y) at, (int X, int Y) from, (int X, int Y) to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double length = dx * dx + dy * dy;
        if (length <= 0) return Distance(at, from);
        double t = Math.Clamp(((at.X - from.X) * dx + (at.Y - from.Y) * dy) / length, 0, 1);
        return Distance(at, ((int)Math.Round(from.X + t * dx), (int)Math.Round(from.Y + t * dy)));
    }
}
