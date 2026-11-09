using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    internal static readonly Lazy<Dictionary<FlaskGrounds.Kind, (string Ao, int ArtRadius)>> FlaskGroundArt = new(() => FlaskGrounds.LoadArt("data"));

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
            conn.FlaskGrounds.Add(new(kind, at.X, at.Y, radius, now + seconds,
                QueueGroundArtLocked(conn, kind, at, radius)));
        }
    }

    internal static Task MaybeCreateProfaneGround(string remote, NetworkStream stream, ConnectionCiphers ciphers,
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
    }

    static uint? QueueGroundArtLocked(ClientState conn, FlaskGrounds.Kind kind, (int X, int Y) at, int radius)
    {
        if (!FlaskGroundArt.Value.TryGetValue(kind, out var art)) return null;
        uint obj = conn.NextObjectId++;
        float scale = (float)(radius * (250.0 / 23) / Math.Max(1, art.ArtRadius));
        var effect = new EffectObject(at.X, at.Y, art.Ao) { Flags0 = EffectObject.FixedRotation, Scale = scale };
        var packets = Automation(conn).Packets;
        packets.Add(new(InstanceEntities.CreateObject(obj, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), effect.Blob()),
            $"0x02b9 create effect 0x{obj:x} {art.Ao} at ({at.X}, {at.Y}), scale {scale:0.###} for radius {radius}", WorldId: obj, CreatesObject: true));
        packets.Add(new(InstanceProtocol.EnterWakeZone(obj), WorldId: obj));
        return obj;
    }

    // The current area claim advances flask and stationary grounds together: expiry, smoke, linger and
    // presentation cannot be stranded in an old timer when Game is replaced.
    internal static void AdvanceFlaskGrounds(ClientState conn, PlayerUpdatesState updates)
    {
        if (conn.FlaskGrounds.Any) _ = FlaskGroundArt.Value;
        lock (conn.Monsters)
        {
            if (!PlayerUpdatesCurrent(conn, updates) || conn.LeavingArea || conn.Paused || !conn.FlaskGrounds.Any) return;
            double now = updates.Schedule.Now(conn);
            var state = conn.FlaskGrounds;
            var pending = Automation(conn);
            foreach (var ground in state.Expire(now))
                if (ground.Object is uint obj) pending.Packets.Add(new(InstanceProtocol.RemoveObject(obj),
                    $"0x02bb remove effect 0x{obj:x} ({ground.Kind} ends)", WorldId: obj, RemovesObject: true));
            foreach (uint gone in state.BlindedMonsters().Except(conn.Monsters.Keys)) state.SetBlinded(gone, false, () => 0);
            foreach (uint gone in state.ProfaneMonsters().Except(conn.Monsters.Keys))
                state.SetProfane(gone, false, false, now, () => 0);
            foreach (var (id, monster) in conn.Monsters)
            {
                bool inside = monster.Hostile && !monster.Dead && monster.Life > 0 && monster.Mods.Effects["cannot_be_blinded"] == 0
                    && state.Covers(FlaskGrounds.Kind.SmokeCloud, monster.Walk.Position(now));
                if (state.SetBlinded(id, inside, () => conn.NextBuff++) is { } change)
                    pending.Packets.Add(new(change.Added
                        ? InstanceEntities.AddBuff(id, change.Buff, FlaskGrounds.BlindedBuffRow, PlayerId)
                        : InstanceEntities.RemoveBuff(id, change.Buff),
                        $"0x{(change.Added ? "01c1" : "01c2")} monster 0x{id:x} {(change.Added ? "blinded in the smoke cloud" : "no longer blinded")}", WorldId: id));
                bool aliveMonster = monster.Hostile && !monster.Dead && monster.Life > 0;
                bool inProfane = aliveMonster && state.Covers(FlaskGrounds.Kind.ProfaneGround, monster.Walk.Position(now));
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
                $"0x02bb remove effect 0x{old:x} (stationary consecrated ground)", WorldId: old, RemovesObject: true));
            if (make > 0) state.PlaceStationary(new(FlaskGrounds.Kind.ConsecratedGround, here.X, here.Y, make,
                double.PositiveInfinity, QueueGroundArtLocked(conn, FlaskGrounds.Kind.ConsecratedGround, here, make)));
            if (state.SetConsecrated(state.OnConsecrated(alive, here, now), () => conn.NextBuff++) is { } consecrated)
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

    internal static async Task RunFlaskGrounds(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, PlayerUpdatesState updates)
    {
        while (PlayerUpdatesCurrent(conn, updates))
        {
            await updates.Schedule.Wait(PlayerUpdateRole.FlaskGrounds, 100, updates.Stop.Token);
            AdvanceFlaskGrounds(conn, updates);
            if (!PlayerUpdatesCurrent(conn, updates)) return;
            await PublishFlaskChanges(remote, stream, ciphers, conn);
        }
    }
}
