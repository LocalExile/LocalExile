using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

/// <summary>
/// A monster's delayed strikes, for any monster skill whose area lands after its cast (MonsterAttack.Strike: Storm
/// Call's): the action's contact sets a marker at its aim with the skill's marker art, and when the marker's time is up
/// its area hit lands there with the owner's stats, the impact art drawn at the area's size. A strike that calls the
/// other markers down calls every marker of the same skill its owner has out. Markers leave with their owner.
/// </summary>
public static partial class Game
{
    static async Task PlaceMonsterMarker(Player conn, uint id, SpawnedMonster m, MonsterAttack a,
        MonsterDelayedStrike strike, (int X, int Y) aim)
    {
        if (a.Effect is not int effect) return;
        uint placed = 0;
        await SendWorldBatch(conn, [], admission =>
        {
            lock (conn.Monsters)
            {
                if (!MonsterPresent(conn, id, m)) return null;
                double now = AreaTime(conn);
                uint marker = conn.TakeObjectId();
                conn.MonsterPlaced.Markers[marker] = new(marker, id, m, effect, a with { Strike = null }, strike, aim, now + strike.Seconds);
                placed = marker;
                if (strike.MarkerArt is not { } art) return null;
                var bytes = new WorldRecordBuilder(conn, admission);
                bytes.Create(marker, InstanceEntities.CreateObject(marker, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                    new EffectObject(aim.X, aim.Y, art) { Flags0 = EffectObject.FixedRotation }.Blob()));
                bytes.Presentation(marker, InstanceProtocol.EnterWakeZone(marker));
                return bytes.Build();
            }
        });
        if (placed != 0)
            Console.WriteLine($"[gateway] {conn.Remote} monster 0x{id:x} ({m.Label}) sets marker 0x{placed:x} at ({aim.X}, {aim.Y}), "
                + $"strikes in {strike.Seconds:0.00}s, radius {a.AreaRadius:0.#}; art {strike.MarkerArt ?? "none"}");
    }

    /// <summary>Each tick: markers whose owner is gone leave, due markers (with the others they call down) strike, and
    /// impact art whose time is up leaves.</summary>
    static async Task MonsterMarkersTick(Player conn)
    {
        var placed = conn.MonsterPlaced;
        if (placed.Markers.Count == 0 && placed.Impacts.Count == 0) return;
        List<MonsterPlacedObjects.Marker> struck = [];
        await SendWorldBatch(conn, [], admission =>
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.Paused) return null;
                double now = AreaTime(conn);
                var bytes = new WorldRecordBuilder(conn, admission);
                foreach (var (impact, until) in placed.Impacts.ToArray())
                    if (now >= until)
                    {
                        placed.Impacts.Remove(impact);
                        bytes.Remove(impact);
                    }
                foreach (var marker in placed.Markers.Values.ToArray())
                {
                    if (!placed.Markers.ContainsKey(marker.Id)) continue;
                    bool owner = ReferenceEquals(conn.Monsters.GetValueOrDefault(marker.Owner), marker.Monster)
                        && !marker.Monster.Dead && marker.Monster.Life > 0;
                    if (owner && now < marker.StrikeAt) continue;
                    var going = !owner || !marker.Strike.StrikesTogether ? [marker]
                        : placed.Markers.Values.Where(o => o.Owner == marker.Owner && o.Effect == marker.Effect).ToList();
                    foreach (var gone in going)
                    {
                        placed.Markers.Remove(gone.Id);
                        if (gone.Strike.MarkerArt is not null) bytes.Remove(gone.Id);
                        if (!owner) continue;
                        struck.Add(gone);
                        if (gone.Strike.ImpactArt is not { } art) continue;
                        uint impact = conn.TakeObjectId();
                        placed.Impacts[impact] = now + gone.Strike.ImpactSeconds;
                        bytes.Create(impact, InstanceEntities.CreateObject(impact, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                            new EffectObject(gone.At.X, gone.At.Y, art)
                            { Flags0 = EffectObject.FixedRotation, Scale = AreaArtScale(gone.Payload.AreaRadius, art) }.Blob()));
                        bytes.Presentation(impact, InstanceProtocol.EnterWakeZone(impact));
                    }
                }
                return bytes.Count > 0 ? bytes.Build() : null;
            }
        });
        if (struck.Count == 0) return;
        Console.WriteLine($"[gateway] {conn.Remote} monster markers strike: " + string.Join(", ", struck.Select(s => $"0x{s.Id:x} of 0x{s.Owner:x}")));
        await Task.WhenAll(struck.Select(s =>
        {
            bool Owner() { lock (conn.Monsters) return ReferenceEquals(conn.Monsters.GetValueOrDefault(s.Owner), s.Monster); }
            return MonsterDetachedAreaHit(conn, s.Owner, s.Monster, s.Payload, s.At, s.Payload.AreaRadius,
                conn.NextActionSequence(), Owner);
        }));
    }
}
