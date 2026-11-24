using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    public sealed partial class ProjectileSession
    {
        BladefallTrarthusState? knifeBlades;

        void ClearKnifeBlades()
        {
            if (knifeBlades is not { } blades) return;
            Player.LingeringBladeSources.Remove(blades);
            blades.Blades.Clear();
            knifeBlades = null;
        }

        void LeaveKnifeBlade(ProjectileEvent value)
        {
            var flight = value.Projectile;
            if (flight.ParentId != 0 || flight.Use.Payload is not ProjectileOffense { GroundBlade: { } spec } shot
                || (flight.VolleyIndex + 1) % spec.EveryProjectile != 0
                || value.Reason is not (ProjectileEndReason.Enemy or ProjectileEndReason.Terrain or ProjectileEndReason.Distance
                    or ProjectileEndReason.Lifetime or ProjectileEndReason.Returned or ProjectileEndReason.Fork or ProjectileEndReason.Split)
                || Art.MiscAnimatedRow("BladefallGroundBlade") is not int art) return;

            (int X, int Y) Cell(ProjectileVector point) => ((int)Math.Round(point.X), (int)Math.Round(point.Y));
            var at = Cell(value.Position);
            if (!Player.Layout.WalkableFor(at.X, at.Y, 0))
            {
                at = Cell(value.Position - flight.Direction);
                if (!Player.Layout.WalkableFor(at.X, at.Y, 0)) return;
            }

            if (knifeBlades is null)
            {
                knifeBlades = new(0, 0, shot.SourceSkill) { Ended = true };
                Player.LingeringBladeSources.Add(knifeBlades);
            }
            var state = knifeBlades;
            while (state.Blades.Count >= spec.Maximum)
            {
                uint oldest = state.Blades[0].Id;
                state.Blades.RemoveAt(0);
                World(oldest, InstanceProtocol.RemoveObject(oldest), null);
            }
            uint id = Player.TakeObjectId();
            long expires = value.At + spec.Lifetime;
            state.Blades.Add((id, at, 0, expires / (double)ProjectileRuntime.QuantaPerSecond));
            float rotation = (float)Math.Atan2(flight.Direction.X, -flight.Direction.Y);
            int bladeType = value.Reason is ProjectileEndReason.Enemy or ProjectileEndReason.Terrain ? 1 : 2;
            var blade = new GroundBladeObject(at.X, at.Y, rotation, (uint)art, bladeType);
            World(id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(BladefallVolleys.GroundBlade), blade.Blob()), null);
            World(id, InstanceEntities.ComponentUpdate(id, GroundBladeObject.AnimatedIndex, GroundBladeObject.Landed()), null);
            Runtime.Schedule(expires, () =>
            {
                if (state.Blades.RemoveAll(entry => entry.Id == id) > 0)
                    World(id, InstanceProtocol.RemoveObject(id), null);
                if (ReferenceEquals(knifeBlades, state) && state.Blades.Count == 0)
                    ClearKnifeBlades();
            });
        }
    }
}
