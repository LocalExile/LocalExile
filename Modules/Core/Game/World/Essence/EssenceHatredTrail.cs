using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>Hatred's Creeping Frost chilling trail: the ice crystals the nova's projectiles leave on the ground. Kept apart
/// from the nova and the other essence spells so a change here cannot disturb them.</summary>
public static partial class MapEssences
{
    /// <summary>Hatred's Creeping Frost trail: a line of small chilling-ground ice crystals along each nova projectile's
    /// radial path (the flame-dash trail pattern), lasting the skill's chilling duration. A player walking over them is
    /// chilled (the strongbox ice-nova ground pattern, <see cref="TickTrail"/>).</summary>
    const string TrailArt = "Metadata/Effects/Spells/ground_effects/ice/ice.ao";
    sealed record TrailZone((int X, int Y) At, int Radius, double Until, uint Art);

    static void FrostTrail(ClientState conn, SpawnedMonster monster, int count)
    {
        if (!Areas.TryGetValue(conn.Instance, out var area)) return;
        double now = conn.Clock.Elapsed.TotalSeconds;
        var at = monster.Walk.Position(now);
        var aim = conn.Walk.Position(now);
        double facing = Math.Atan2(aim.Y - at.Y, aim.X - at.X);
        double until = Now + 7.5; // base_skill_effect_duration of EssenceArcticBreathNova
        // The crystals appear as the projectile flies over each spot, not all at once: the ring at distance d shows when
        // the shot reaches d, at the ArcticBreath flight speed the nova fires it with.
        int speed = SkillsFor(conn).Projectile("Metadata/Projectiles/ArcticBreath")?.Speed ?? 844;
        double cellsPerSecond = ProjectileSpeed.CellsPerSecond(speed, _ => 0, _ => 0);
        for (double d = 6; d <= 56; d += 10)
            _ = TrailRing(conn, area, at, facing, count, d, until, d / cellsPerSecond);
    }

    /// <summary>One distance-ring of the trail, spawned when the projectile reaches it: a crystal on each of the nova's
    /// bearings at distance <paramref name="d"/>. Spaced so neighbouring crystals meet into a near-continuous ice path.</summary>
    static async Task TrailRing(ClientState conn, Area area, (int X, int Y) at, double facing, int count, double d, double until, double delay)
    {
        await Task.Delay(TimeSpan.FromSeconds(delay));
        if (conn.Closed || conn.LeavingArea) return;
        for (int i = 0; i < count; i++)
        {
            double angle = facing + Math.Tau * (i / (double)count - .5);
            var spot = conn.Layout.Near((at.X + (int)Math.Round(Math.Cos(angle) * d), at.Y + (int)Math.Round(Math.Sin(angle) * d)));
            uint id = conn.TakeObjectId();
            lock (conn.Monsters)
            {
                conn.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                    new EffectObject(spot.X, spot.Y, TrailArt) { Flags0 = EffectObject.FixedRotation, Scale = 1.05f }.Blob()),
                    null, WorldId: id, CreatesObject: true));
                conn.Minions.OutputQueue.Add(new(InstanceProtocol.EnterWakeZone(id), null, WorldId: id));
            }
            lock (area.Trail) area.Trail.Add(new(spot, 5, until, id));
        }
    }

    /// <summary>Called from the essence tick: expires the trail's ice crystals (their art removed) and chills a player who
    /// stands on one (the strongbox chilling-ground pattern). The chill refreshes while they remain on the ice.</summary>
    static void TickTrail(ClientState conn)
    {
        if (!Areas.TryGetValue(conn.Instance, out var area)) return;
        double now = Now;
        TrailZone[] zones;
        lock (area.Trail)
        {
            foreach (var gone in area.Trail.Where(z => z.Until <= now).ToArray())
            {
                area.Trail.Remove(gone);
                lock (conn.Monsters)
                    conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(gone.Art), null, WorldId: gone.Art, RemovesObject: true));
            }
            zones = [.. area.Trail];
        }
        if (zones.Length == 0 || conn.PlayerLife <= 0 || conn.Paused || conn.Immune || InGracePeriod(conn)) return;
        double at = conn.Clock.Elapsed.TotalSeconds;
        var here = conn.Walk.Position(at);
        if (!zones.Any(z => Dist(here, z.At) <= z.Radius)) return;
        const int chill = 20;
        if (conn.ElementalAilments.Magnitude(PlayerElementalAilments.Kind.Chill, at) >= chill) return;
        var stats = CharacterPlayerStats(conn);
        lock (conn.Monsters)
            ApplyPlayerAilmentsLocked(conn, at, a => a.Inflict(PlayerElementalAilments.Kind.Chill, chill, .3, at,
                name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0, conn.IncomingHitRandom));
    }
}