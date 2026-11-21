using GameServer.Core.Projectiles;
using static Game;

public static partial class MapStrongboxes
{
    const string VortexEffect = "ChestVaalBladeVortex";
    const string MovingVortex = "Metadata/Monsters/Daemons/VaalBladeVortexDaemon";

    public sealed record VaalBladeVortex(long Started, string Daemon, int AimX, int AimY, VesselTerrain Terrain) : VesselCast(Started, Daemon)
    {
        public double X { get; set; }
        public double Y { get; set; }
        // A prefix and this suffix can start in the same instance tick.
        internal override object PositionKey(Box box) => (box.Id, Started, VortexEffect);
        internal override (int X, int Y) Target(Box box) => (AimX, AimY);
    }

    sealed record BladeVortexSkill(VesselSkill Spell, VesselImpact Hit, long ReleaseTicks, long DurationTicks,
        long IntervalTicks, int Speed, int AggroRange, ProjectileGrid.Area? Grid)
    {
        public int Pulses => (int)(DurationTicks / IntervalTicks);
        public long Pulse(VaalBladeVortex cast, int step) => cast.Started + ReleaseTicks + step * IntervalTicks;
    }

    static BladeVortexSkill ReadBladeVortex(ClientState conn)
    {
        var spell = ReadVesselSpell(conn, VortexEffect);
        // The chest has the Vaal gem's action and its own radius and critical modifiers.
        // The exported stat set omits the base radius. Path of Building gives the Vaal action 15 cells.
        int radius = SkillArea.Radius(15, spell.Get, spell: true);
        spell = spell with { Critical = spell.Critical * (100 + spell.Get("critical_strike_chance_+%")) / 100 };
        var vortex = Monsters.Find(MovingVortex)!;
        return new(spell, spell.Impact(radius), TimeSpan.FromSeconds(spell.Seconds).Ticks,
            spell.Required("base_skill_effect_duration") * TimeSpan.TicksPerMillisecond,
            spell.Required("base_blade_vortex_hit_rate_ms") * TimeSpan.TicksPerMillisecond,
            vortex.MovementSpeed, vortex.AggroRange, ProjectileGrid.Of(conn.Layout.Id, conn.Seed));
    }

    static async Task TickBladeVortices(ClientState conn, Area area, Box box, TimeSpan now, bool currentView)
    {
        var skill = area.VortexSkill ??= ReadBladeVortex(conn);
        foreach (var cast in box.Vortices.ToArray())
        {
            if (!await PublishVesselHits(conn, area, cast)) return;
            if (now.Ticks < skill.Pulse(cast, 0))
            {
                if (currentView) await ShowVesselCast(conn, area, box, cast, skill.Spell);
                continue;
            }
            while (cast.Landed < skill.Pulses)
            {
                long due = skill.Pulse(cast, cast.Landed + 1);
                if (due > now.Ticks) break;
                ProjectileVector next;
                lock (conn.Monsters) next = AdvanceVortex(conn, box, cast, skill, due - skill.IntervalTicks);
                var cell = Cell(next);
                if (!await LandVesselImpact(conn, area, box, cast, skill.Spell, skill.Hit, cell, due,
                    (_, here) => (new ProjectileVector(here.X, here.Y) - next).Length <= skill.Hit.Attack.Range ? cell : null,
                    onCommit: () => { cast.X = next.X; cast.Y = next.Y; })) return;
            }
            if (now.Ticks < skill.Pulse(cast, 0) + skill.DurationTicks) continue;
            if (cast.Actor != 0 && !await RemoveVesselActor(conn, area, box, cast)) return;
            box.Vortices.Remove(cast);
            Keep(conn, area);
        }
    }

    static ProjectileVector AdvanceVortex(ClientState conn, Box box, VaalBladeVortex cast, BladeVortexSkill skill, long start)
    {
        var origin = new ProjectileVector(cast.X, cast.Y);
        if (VesselTargetAt(conn, box, cast, start, origin, skill.AggroRange) is not { } target) return origin;
        var delta = new ProjectileVector(target.X, target.Y) - origin;
        double travel = Math.Min(delta.Length, skill.Speed * skill.IntervalTicks / (double)TimeSpan.TicksPerSecond);
        var direction = delta.Unit;
        var end = origin + direction * travel;
        // The moving daemon has a projectile collision hull and phases through actors.
        var wall = ProjectileGeometry.TraceGrid(origin, end, (x, y) => ProjectileGrid.Blocks(conn.Layout, skill.Grid, cast.Terrain.Cells, x, y));
        return wall is { } stop ? origin + direction * Math.Max(0, travel * stop.Fraction - .001) : end;
    }
}
