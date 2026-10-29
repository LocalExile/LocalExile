namespace GameServer.Core.Projectiles;

public sealed partial class ProjectileRuntime
{
    void StartStages(ProjectileEntity p)
    {
        if (p.Profile.WanderInterval > 0)
        {
            void Wander()
            {
                if (!projectiles.ContainsKey(p.Id)) return;
                p.Direction = p.Direction.Rotate((p.Random.NextDouble() - .5) * Math.PI);
                Publish(ProjectileEventKind.Redirect, p);
                Schedule(Now + p.Profile.WanderInterval, Wander);
            }
            Schedule(Now + p.Profile.WanderInterval, Wander);
        }
        if (p.Form == 0 && p.Profile.FormChange is { } form)
            Schedule(Now + (long)Math.Ceiling(Math.Max(0, form.Distance - p.Distance) / p.Speed * QuantaPerSecond), () =>
            {
                if (!projectiles.ContainsKey(p.Id)) return;
                p.Form = 1; p.Speed *= form.SpeedMultiplier; p.Pierce = form.Pierce;
                p.DamageMultiplier *= form.DamageMultiplier;
                Publish(ProjectileEventKind.Redirect, p);
            });
        if (p.Profile.Pulse is { } pulse)
        {
            if (pulse.Interval <= 0) throw new ArgumentOutOfRangeException(nameof(pulse));
            void Pulse()
            {
                if (!projectiles.ContainsKey(p.Id)) return;
                foreach (var target in world.Actors(Now, Now).OrderBy(a => a.Key.Id))
                    if (target.Team != p.Use.Team && target.Key != p.Use.Owner
                        && (target.Position(Now) - p.Position).Length <= pulse.Radius + target.Radius)
                        Damage(p, target, pulse.Stage, isArea: true);
                Schedule(Now + pulse.Interval, Pulse);
            }
            Pulse();
        }
        if (p.Profile.TravelEmission is { Interval: > 0 } emission)
        {
            void Emit()
            {
                if (!projectiles.ContainsKey(p.Id)) return;
                EmitChildren(p, emission);
                Schedule(Now + emission.Interval, Emit);
            }
            Schedule(Now + emission.Interval, Emit);
        }
    }

    void EmitChildren(ProjectileEntity parent, ProjectileEmissionRecipe recipe)
    {
        var child = recipe.Child;
        Validate(child);
        if (parent.ChildEmission == 0) parent.ChildEmission = ++sequence;
        for (int i = 0; i < child.Count; i++)
        {
            double angle = recipe.RandomDirections ? parent.Random.NextDouble() * Math.Tau
                : recipe.Radial ? Math.Tau * i / child.Count + parent.EmissionNumber * 2.399963229728653
                : child.Count <= 1 ? 0 : child.SpreadRadians * (i / (double)(child.Count - 1) - .5);
            var direction = parent.Direction.Rotate(angle);
            var use = parent.Use with { Profile = child, Origin = parent.Position,
                Aim = parent.Position + direction * child.Range };
            Spawn(use, parent.UseId, parent.ChildEmission, parent.Id, parent.Position, direction);
        }
        parent.EmissionNumber++;
    }

    void TargetedHits(ProjectileEntity p, ProjectileActor primary, ProjectileTargetedRecipe recipe)
    {
        long hitEvent = ++sequence;
        foreach (var target in world.Actors(Now, Now).Where(a => a.Team != p.Use.Team && a.Key != primary.Key
            && a.Key != p.Use.Owner && (a.Position(Now) - primary.Position(Now)).Length <= recipe.Radius)
            .OrderBy(a => (a.Position(Now) - primary.Position(Now)).Length).ThenBy(a => a.Key.Id).Take(recipe.Count))
            Damage(p, target, recipe.Stage, hitEvent, new());
    }

    void Area(ProjectileEntity p, ProjectileAreaRecipe recipe, ProjectileActorKey? victim = null)
    {
        var context = recipe.Delay > 0 || recipe.Interval > 0 ? p.AreaSnapshot() : p;
        double sector = Math.Tau / Math.Max(1, recipe.Count);
        double rotation = recipe.SectorPlacement ? p.Random.NextDouble() * Math.Tau : 0;
        int areaIndex = 0;
        for (int i = 0; i < recipe.Count; i++)
        {
            // Independent area events can overlap; only the terminal collision victim shares direct admission.
            long areaId = ++sequence;
            var centre = p.Position;
            if (recipe.Offset > 0)
            {
                double angle = recipe.SectorPlacement ? rotation + sector * (i + p.Random.NextDouble())
                    : p.Random.NextDouble() * Math.Tau;
                double minimum = Math.Clamp(recipe.MinimumOffset, 0, recipe.Offset);
                centre += new ProjectileVector(1, 0).Rotate(angle)
                    * (minimum + (recipe.Offset - minimum) * p.Random.NextDouble());
                // A collision centre may be exactly on a wall. Test from the incident side of that surface.
                if (recipe.RespectTerrain && world.TraceTerrain(p.Position - p.Direction * .001, centre) is not null)
                    continue;
            }
            // Rejected centres consume their spatial sample, but no time in the surviving sequence.
            long at = checked(Now + recipe.Delay + areaIndex++ * recipe.Interval);
            if (at == Now) ResolveArea(context, recipe, centre, victim, areaId);
            else
            {
                pendingAreas.Add(areaId, context);
                Schedule(at, () =>
                {
                    try { ResolveArea(context, recipe, centre, victim, areaId); }
                    finally { pendingAreas.Remove(areaId); }
                });
            }
        }
    }

    void ResolveArea(ProjectileEntity p, ProjectileAreaRecipe recipe, ProjectileVector centre,
        ProjectileActorKey? victim, long areaId)
    {
        world.Publish(new(ProjectileEventKind.Area, p, Now, centre, Radius: recipe.Radius, Stage: recipe.Stage));
        foreach (var target in world.Actors(Now, Now).OrderBy(a => a.Key.Id).ThenBy(a => a.Key.Generation))
            if (target.Team != p.Use.Team && target.Key != p.Use.Owner
                && (!recipe.ExcludeContactTarget || target.Key != victim)
                && (target.Position(Now) - centre).Length <= recipe.Radius + target.Radius)
                if (target.Key == victim)
                    Damage(p, target, recipe.Stage, multiplier: recipe.Multiplier, admissionStage: "direct", isArea: true);
                else Damage(p, target, recipe.Stage,
                    recipe.Scope == ProjectileHitScope.Emission ? p.EmissionId : areaId, new(), recipe.Multiplier,
                    admissionStage: recipe.Scope == ProjectileHitScope.Emission
                        ? recipe.Stage + ":" + p.GroundBounces : recipe.Stage, isArea: true);
    }

    public bool TryConsume(long projectileId)
    {
        if (!projectiles.TryGetValue(projectileId, out var p)) return false;
        End(p, ProjectileEndReason.Consumed, allowReturn: false);
        return true;
    }
}
