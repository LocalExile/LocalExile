using GameServer.Core.Projectiles;
using static Game;

public static partial class MapStrongboxes
{
    const string FissureMortar = "Metadata/Projectiles/VolcanicSlamMortar";

    // These projectiles have no direct hit. Trace their terrain flight once and settle each landing
    // against the party positions at its due frame, including players who enter during flight.
    sealed class MortarTerrain(ClientState conn, VesselTerrain terrain, int eruption) : IProjectileWorld
    {
        readonly ProjectileGrid.Area? grid = ProjectileGrid.Of(conn.Layout.Id, conn.Seed);
        public List<FissureLanding> Landings { get; } = [];
        public IReadOnlyList<ProjectileActor> Actors(long from, long until) => [];
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) =>
            ProjectileGeometry.TraceGrid(from, to, (x, y) => ProjectileGrid.Blocks(conn.Layout, grid, terrain.Cells, x, y));
        public ProjectileHitResult Hit(ProjectileDamage damage) => throw new InvalidOperationException("Terrain-only mortar flight has no actors.");
        public void Publish(ProjectileEvent e)
        {
            if (e.Kind != ProjectileEventKind.Area) return;
            int frame = (int)((e.At + ProjectileRuntime.TickQuanta - 1) / ProjectileRuntime.TickQuanta);
            Landings.Add(new(eruption + frame, e.Position.X, e.Position.Y));
        }
    }

    static FissureLanding[] FissureMortars(ClientState conn, uint actor, FissureLeg leg, int eruption, FissureSkill skill, VesselTerrain obstacles)
    {
        var origin = new ProjectileVector(leg.EndX, leg.EndY);
        var aim = VolcanicFissureSweep.MortarAim(leg.From, leg.End);
        var profile = VolcanicFissureSweep.MortarVolley(skill.Profile, MonsterActionSequence, actor, aim - origin, leg.Index);
        var terrain = new MortarTerrain(conn, obstacles, eruption);
        var flight = new ProjectileRuntime(terrain);
        var owner = new ProjectileActorKey(actor);
        flight.Release(new(profile, owner, owner, 2, origin, aim) { Sequence = MonsterActionSequence });
        flight.AdvanceTo(skill.FlightFrames * ProjectileRuntime.TickQuanta);
        return [.. terrain.Landings];
    }
}
