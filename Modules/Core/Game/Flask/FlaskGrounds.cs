using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadFlaskGroundsData()
    {
        FlaskGroundArt = FlaskGrounds.LoadArt("data");
    }

    public static Dictionary<FlaskGrounds.Kind, (string Ao, int ArtRadius)> FlaskGroundArt = default!;

    // Ground creation belongs to the accepted charge transaction. Its art receipts and deadline survive
    // replacement, while taunt uses the same accepted character snapshot as the utility effect.
    static void MakeFlaskGroundsLocked(ClientState conn, IReadOnlyDictionary<string, int> local, double seconds,
        double now, IReadOnlyDictionary<uint, int> player)
    {
        var at = conn.Walk.Position(now);
        foreach (var (kind, radius) in FlaskGrounds.OnUse(local))
        {
            if (kind == FlaskGrounds.Kind.Taunt)
            {
                double duration = TauntSeconds(name => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0);
                TauntLocked(conn, at, radius, now, duration);
                continue;
            }
            int taken = kind == FlaskGrounds.Kind.ConsecratedGround ? ConsecratedEnemyDamageTakenNow(conn) : 0;
            conn.FlaskGrounds.Add(new(kind, at.X, at.Y, radius, now + seconds,
                QueueGroundArtLocked(conn, kind, at, radius), taken));
            PresentNearby(conn);
            Console.WriteLine($"[gateway] flask ground {kind} at ({at.X}, {at.Y}) radius {radius} for {seconds:0.##} s" +
                (taken > 0 ? $", enemies on it take {taken}% increased damage" : ""));
        }
    }

    /// <summary>Bottled Faith: the increased damage taken a consecrated ground created now applies to enemies on it
    /// (the running flask effects' strongest).</summary>
    static int ConsecratedEnemyDamageTakenNow(ClientState conn) =>
        UtilityFlaskStat(conn, FlaskGrounds.EnemyDamageTakenStat);

    /// <summary>The strongest value of a stat the running utility flasks grant (their "during effect" mods).</summary>
    public static int UtilityFlaskStat(ClientState conn, string stat) =>
        conn.UtilityFlaskEffects.MaximumStat(stat);

    public static Task MaybeCreateProfaneGround(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, (int X, int Y) at)
    {
        lock (conn.Monsters)
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return Task.CompletedTask;
            var player = CharacterPlayerStats(conn);
            MaybeCreateProfaneGroundLocked(conn, at, player);
        }
        return PublishFlaskChanges(remote, stream, ciphers, conn);
    }

    static void MaybeCreateProfaneGroundLocked(ClientState conn, (int X, int Y) at, IReadOnlyDictionary<uint, int> player)
    {
        int chance = FlaskGrounds.ProfaneChance(name => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0);
        if (chance <= 0 || Random.Shared.Next(100) >= chance) return;
        conn.FlaskGrounds.Add(FlaskGrounds.ProfaneGround(at, AreaTime(conn),
            QueueGroundArtLocked(conn, FlaskGrounds.Kind.ProfaneGround, at, FlaskGrounds.ProfaneRadius)));
        PresentNearby(conn);
    }

    static uint? QueueGroundArtLocked(ClientState conn, FlaskGrounds.Kind kind, (int X, int Y) at, int radius)
    {
        if (!FlaskGroundArt.TryGetValue(kind, out var art)) return null;
        uint obj = conn.TakeObjectId();
        float scale = (float)(radius * (250.0 / 23) / Math.Max(1, art.ArtRadius));
        var effect = new EffectObject(at.X, at.Y, art.Ao) { Flags0 = EffectObject.FixedRotation, Scale = scale };
        var packets = Automation(conn).Packets;
        packets.Add(new(InstanceEntities.CreateObject(obj, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), effect.Blob()),
            $"0x02bb create effect 0x{obj:x} {art.Ao} at ({at.X}, {at.Y}), scale {scale:0.###} for radius {radius}", WorldId: obj, CreatesObject: true));
        packets.Add(new(InstanceProtocol.EnterWakeZone(obj), WorldId: obj));
        return obj;
    }

    // The current area claim advances flask and stationary grounds together: expiry, smoke, linger and
    // presentation cannot be stranded in an old timer when Game is replaced.
    // The grounds are zones (one per ground, at it) reaching the player and the hostile monsters; a walker in or out
    // presents them again.
    const string FlaskGroundZones = "flask grounds";

    static void SyncFlaskGroundZones(ClientState conn)
    {
        var hostile = ReachesHostileMonsters(conn, body: false);
        SyncZones(conn, conn.PeerId, FlaskGroundZones, conn.FlaskGrounds.Grounds.Select(g => ((object)g, g.Radius, (g.X, g.Y))),
            new(id => id.Value == conn.PeerId ? 0 : hostile(id), Changed: _ => PresentNearby(conn)));
    }

    // Whether a ground of the kind covers the monster.
    static bool InFlaskGround(ClientState conn, FlaskGrounds.Kind kind, uint id) =>
        conn.FlaskGrounds.Grounds.Any(g => g.Kind == kind && InZone(conn, conn.PeerId, FlaskGroundZones, g, id));

    // The increased damage taken a monster gets from the consecrated grounds covering it: the strongest one.
    static int ConsecratedEnemyDamageTaken(ClientState conn, uint id) => conn.FlaskGrounds.Grounds
        .Where(g => g is { Kind: FlaskGrounds.Kind.ConsecratedGround, EnemyDamageTakenPct: > 0 } && InZone(conn, conn.PeerId, FlaskGroundZones, g, id))
        .Select(g => g.EnemyDamageTakenPct).DefaultIfEmpty(0).Max();

    public static void AdvanceFlaskGrounds(ClientState conn, PlayerUpdatesState updates)
    {
        if (conn.FlaskGrounds.Any) _ = FlaskGroundArt;
        lock (conn.Monsters)
        {
            if (!PlayerUpdatesCurrent(conn, updates) || conn.LeavingArea || conn.Paused || !conn.FlaskGrounds.Any) return;
            double now = updates.Schedule.Now(conn);
            var state = conn.FlaskGrounds;
            var pending = Automation(conn);
            foreach (var ground in state.Expire(now))
                if (ground.Object is uint obj) pending.Packets.Add(new(InstanceProtocol.RemoveObject(obj),
                    $"0x02bd remove effect 0x{obj:x} ({ground.Kind} ends)", WorldId: obj, RemovesObject: true));
            foreach (uint gone in state.BlindedMonsters().Except(conn.Monsters.Keys)) state.SetBlinded(gone, false, () => 0);
            foreach (uint gone in state.ProfaneMonsters().Except(conn.Monsters.Keys))
                state.SetProfane(gone, false, false, now, () => 0);
            SyncFlaskGroundZones(conn);
            foreach (var (id, monster) in conn.Monsters)
            {
                bool inside = monster.Hostile && !monster.Dead && monster.Life > 0 && monster.Mods.Effects["cannot_be_blinded"] == 0
                    && InFlaskGround(conn, FlaskGrounds.Kind.SmokeCloud, id);
                if (state.SetBlinded(id, inside, () => conn.NextBuff++) is { } change)
                    pending.Packets.Add(new(change.Added
                        ? InstanceEntities.AddBuff(id, change.Buff, FlaskGrounds.BlindedBuffRow, PlayerId)
                        : InstanceEntities.RemoveBuff(id, change.Buff),
                        $"0x{(change.Added ? "01c1" : "01c2")} monster 0x{id:x} {(change.Added ? "blinded in the smoke cloud" : "no longer blinded")}", WorldId: id));
                bool aliveMonster = monster.Hostile && !monster.Dead && monster.Life > 0;
                bool inProfane = aliveMonster && InFlaskGround(conn, FlaskGrounds.Kind.ProfaneGround, id);
                if (state.SetProfane(id, inProfane, aliveMonster, now, () => conn.NextBuff++) is { } profane)
                    pending.Packets.Add(new(profane.Added
                        ? InstanceEntities.AddBuff(id, profane.Buff, FlaskGrounds.ProfaneBuffRow, PlayerId)
                        : InstanceEntities.RemoveBuff(id, profane.Buff),
                        $"profane ground monster 0x{id:x}: {profane.Added}", WorldId: id));
            }
            bool alive = conn.PlayerLife > 0;
            var here = conn.Walk.Position(now);
            var (stale, make) = state.Stationary(alive && !conn.Walk.Moving(now) && !InTown(conn), here);
            if (stale?.Object is uint old) pending.Packets.Add(new(InstanceProtocol.RemoveObject(old),
                $"0x02bd remove effect 0x{old:x} (stationary consecrated ground)", WorldId: old, RemovesObject: true));
            if (make > 0)
            {
                state.PlaceStationary(new(FlaskGrounds.Kind.ConsecratedGround, here.X, here.Y, make,
                    double.PositiveInfinity, QueueGroundArtLocked(conn, FlaskGrounds.Kind.ConsecratedGround, here, make),
                    ConsecratedEnemyDamageTakenNow(conn)));
                SyncFlaskGroundZones(conn);
            }
            // A ground placed this step has its members from the next movement update; until then its cells decide.
            bool covered = state.Grounds.Any(g => g.Kind == FlaskGrounds.Kind.ConsecratedGround
                && (InZone(conn, conn.PeerId, FlaskGroundZones, g, conn.PeerId) || g.Covers(here)));
            if (state.SetConsecrated(state.OnConsecrated(alive, covered, now), () => conn.NextBuff++) is { } consecrated)
            {
                pending.Packets.Add(new(consecrated.Added
                    ? InstanceEntities.AddBuff(PlayerId, consecrated.Buff, FlaskGrounds.ConsecratedBuffRow, PlayerId)
                    : InstanceEntities.RemoveBuff(PlayerId, consecrated.Buff),
                    $"0x{(consecrated.Added ? "01c1" : "01c2")} player {(consecrated.Added ? "on" : "off")} consecrated ground", WorldId: PlayerId));
                pending.StatRevision++;
            }
            state.StopTickerIfIdle();
        }
    }

    // A nearby presentation: grounds placed (PresentNearby), walkers in or out of them, the player still or moving (a stat
    // condition), and each ground's end or linger (PresentIn).
    static async Task SendFlaskGrounds(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (conn.PlayerUpdatesState is not PlayerUpdatesState updates) return;
        AdvanceFlaskGrounds(conn, updates);
        double now = updates.Schedule.Now(conn);
        if (conn.FlaskGrounds.NextChangeAt(now) is double next) PresentIn(conn, next - now + 1e-6);
        if (!PlayerUpdatesCurrent(conn, updates)) return;
        await PublishFlaskChanges(remote, stream, ciphers, conn);
    }
}
