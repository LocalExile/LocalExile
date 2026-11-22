using System.Numerics;
using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    public sealed partial class ProjectileSession
    {
        internal ProjectileVector StaticBallLightningPosition((int X, int Y) casterCell, ProjectileFiringLine line,
            bool trapped, int effect, ushort sequence, uint caster)
        {
            Vector2 selected = trapped ? line.From : line.To;
            Vector2 place = selected;
            // The action displaces the new orb once when this caster already has a Static orb within 40 world units.
            foreach (var orb in Runtime.Projectiles)
            {
                if (orb.Use.Emitter.Id != caster || orb.Use.Payload is not ProjectileOffense shot || shot.Effect != effect
                    || Vector2.DistanceSquared(ProjectileFiringLine.World(orb.Position), selected) >= 40 * 40) continue;
                var random = new AnimationVariantSelector.NativeRandom([sequence, caster]);
                place += new Vector2(random.Range(-40, 40), random.Range(-40, 40));
                break;
            }

            var target = ProjectileFiringLine.Cell(place);
            Vector2 last = selected;
            foreach (var (x, y) in ClientPathfinder.Line(casterCell, ((int)target.X, (int)target.Y)))
            {
                // Layer 1 includes both ends. If the caster cell is blocked the native action keeps its original selection.
                if (Wall(x, y))
                {
                    place = last;
                    break;
                }
                last = ProjectileFiringLine.World(new(x, y));
            }
            var cell = ProjectileFiringLine.Cell(place);
            var offset = (place - ProjectileFiringLine.World(cell)) * ProjectileFiringLine.CellsPerWorld;
            return new(cell.X + offset.X, cell.Y + offset.Y);
        }
    }
}
