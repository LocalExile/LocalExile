using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    public sealed partial class ProjectileSession
    {
        BladefallTrarthusState? knifeBlades;

        void ClearProjectileBladeSources()
        {
            foreach (var source in new[] { knifeBlades, placedBladefallBlades })
            {
                if (source is null) continue;
                Player.LingeringBladeSources.Remove(source);
                foreach (var blade in source.Blades) RetireSkillEffectObject(blade.Id);
                source.Blades.Clear();
            }
            knifeBlades = null;
            placedBladefallBlades = null;
        }

        void LeaveKnifeBlade(ProjectileEvent value)
        {
            var flight = value.Projectile;
            if (flight.ParentId != 0 || flight.Use.Payload is not ProjectileOffense { GroundBlade: { } spec } shot
                || (flight.VolleyIndex + 1) % spec.EveryProjectile != 0
                || value.Reason is not (ProjectileEndReason.Enemy or ProjectileEndReason.Terrain or ProjectileEndReason.Distance
                    or ProjectileEndReason.Lifetime or ProjectileEndReason.Returned or ProjectileEndReason.Fork or ProjectileEndReason.Split)) return;

            (int X, int Y) Cell(ProjectileVector point) => ((int)Math.Round(point.X), (int)Math.Round(point.Y));
            var at = Cell(value.Position);
            if (!Player.Layout.WalkableFor(at.X, at.Y, 0))
            {
                at = Cell(value.Position - flight.Direction);
                if (!Player.Layout.WalkableFor(at.X, at.Y, 0)) return;
            }

            var state = knifeBlades ??= new(0, 0, shot.SourceSkill) { Ended = true };
            float rotation = (float)Math.Atan2(flight.Direction.X, -flight.Direction.Y);
            int bladeType = value.Reason is ProjectileEndReason.Enemy or ProjectileEndReason.Terrain ? 1 : 2;
            LeaveProjectileBlade(state, at, rotation, bladeType, spec.Maximum, value.At + spec.Lifetime);
        }

        void LeaveProjectileBlade(BladefallTrarthusState state, (int X, int Y) at, float rotation,
            int bladeType, int maximum, long expires)
        {
            if (maximum <= 0 || Art.MiscAnimatedRow("BladefallGroundBlade") is not int art) return;
            if (state.Blades.Count == 0 && !Player.LingeringBladeSources.Contains(state))
                Player.LingeringBladeSources.Add(state);
            while (state.Blades.Count >= maximum)
            {
                uint oldest = state.Blades[0].Id;
                state.Blades.RemoveAt(0);
                RemoveProjectileBlade(oldest);
            }
            uint id = Player.TakeObjectId();
            state.Blades.Add((id, at, 0, expires / (double)ProjectileRuntime.QuantaPerSecond));
            var blade = new GroundBladeObject(at.X, at.Y, rotation, (uint)art, bladeType);
            Player.TrackProjectileObject(id);
            Create(id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(BladefallVolleys.GroundBlade), blade.Blob()));
            World(id, InstanceEntities.ComponentUpdate(id, GroundBladeObject.AnimatedIndex, GroundBladeObject.Landed()));
            Runtime.Schedule(expires, () =>
            {
                if (state.Blades.RemoveAll(entry => entry.Id == id) > 0)
                    RemoveProjectileBlade(id);
                if (state.Blades.Count == 0) Player.LingeringBladeSources.Remove(state);
            });
        }

        void RemoveProjectileBlade(uint id)
        {
            Player.UntrackProjectileObject(id);
            WorldRecords(id, () => [InstanceProtocol.RemoveObject(id)], remove: true);
        }
    }
}
