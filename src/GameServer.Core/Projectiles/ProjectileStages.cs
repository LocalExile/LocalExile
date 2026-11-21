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
                Redirect(p, p.Position);
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
                var targets = ActorsInOrder(generations: false, out int listed);
                try
                {
                    for (int i = 0; i < listed; i++)
                    {
                        var target = targets[i];
                        if (target.Team != p.Use.Team && target.Key != p.Use.Owner
                            && (target.Position(Now) - p.Position).Length <= pulse.Radius + target.Radius)
                            Damage(p, target, pulse.Stage, isArea: true);
                    }
                }
                finally { Return(targets, listed); }
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
        var layout = recipe.FlightLayout?.Invoke(parent, child.Count);
        var branches = recipe.InheritForkOrChain ? parent.Branches : default;
        // A single clone keeps its firing point. A group receives new positions at the child origin.
        if (child.Count > 1) branches.FiringPointChains = 0;
        if (recipe.Sweep is { } sweep && child.Count > 1)
        {
            var startDirection = sweep.StartDirection(parent);
            var origin = parent.Position;
            double distance = parent.Distance;
            for (int i = 0; i < child.Count; i++)
            {
                double fraction = i / (double)(child.Count - 1);
                var direction = startDirection.Rotate(sweep.Radians * fraction);
                int index = i;
                void Emit() => SpawnChild(parent, child, ++sequence, origin, direction, distance, branches,
                    layout?.Invoke(index, parent.Release.Spawned));
                if (i == 0) Emit();
                else Schedule(Now + (long)Math.Round(sweep.Duration * fraction), Emit);
            }
            return;
        }
        if (parent.ChildEmission == 0) parent.ChildEmission = ++sequence;
        var baseDirection = recipe.BaseDirection?.Invoke(parent) ?? parent.Direction;
        for (int i = 0; i < child.Count; i++)
        {
            double angle = recipe.RandomDirections ? parent.Random.NextDouble() * Math.Tau
                : recipe.Radial ? Math.Tau * i / child.Count + parent.EmissionNumber * 2.399963229728653
                : child.Count <= 1 ? 0 : child.SpreadRadians * (i / (double)(child.Count - 1) - .5);
            var direction = recipe.RandomDirections ? parent.Direction.Rotate(angle) : baseDirection.Rotate(angle);
            SpawnChild(parent, child, parent.ChildEmission, parent.Position, direction, parent.Distance, branches,
                layout?.Invoke(i, parent.Release.Spawned));
        }
        parent.EmissionNumber++;
    }

    void SpawnChild(ProjectileEntity parent, ProjectileProfile child, long emission,
        ProjectileVector origin, ProjectileVector direction, double distance, ProjectileBranchState branches, ProjectileFlightPath? path)
    {
        var launch = path.GetValueOrDefault().Launch;
        if (path is { } flight)
        {
            origin = flight.Origin;
            if (launch.CheckSourcePath
                && world.TraceTerrain(parent.Use.LaunchSourceAt?.Invoke(Now) ?? parent.Use.LaunchSource, origin) is not null)
                return;
            direction = flight.Aim - origin;
            child = child with { Range = direction.Length,
                Returns = child.Returns || launch.GrantsReturn && !child.CannotReturn };
            branches.FiringPointChains += launch.AdditionalChains;
        }
        var use = parent.Use with { Profile = child, Origin = origin, Aim = path?.Aim ?? origin + direction * child.Range };
        var spawned = Spawn(use, parent.Release, emission, parent.Id, origin, direction,
            piercesAll: launch.PiercesAllTargets, additionalForks: launch.AdditionalForks, branches: branches);
        // Point Blank/Far Shot measure the whole continuation from the original release.
        spawned.Distance = distance;
    }

    void TargetedHits(ProjectileEntity p, ProjectileActor primary, ProjectileTargetedRecipe recipe)
    {
        long hitEvent = ++sequence;
        foreach (var target in world.Actors(Now, Now).Where(a => a.Team != p.Use.Team && a.Key != primary.Key
            && a.Key != p.Use.Owner && (a.Position(Now) - primary.Position(Now)).Length <= recipe.Radius)
            .OrderBy(a => (a.Position(Now) - primary.Position(Now)).Length).ThenBy(a => a.Key.Id).Take(recipe.Count))
            Damage(p, target, recipe.Stage, hitEvent, new());
    }

    /// <summary>Resolve a grounded projectile impact without a horizontal flight or a direct-hit victim.</summary>
    public void Impact(ProjectileUse use, ProjectileVector centre, ProjectileAreaRecipe area)
    {
        long id = ++sequence;
        var impact = new ProjectileEntity(id, new(id), id, 0, use, centre, centre - use.Origin, Now)
            { Distance = (centre - use.Origin).Length };
        Area(impact, area);
    }

    void Area(ProjectileEntity p, ProjectileAreaRecipe recipe, ProjectileActorKey? victim = null)
    {
        var context = recipe.Delay > 0 || recipe.Interval > 0 ? p.AreaSnapshot() : p;
        int count = recipe.Count;
        // The client always samples the extra-area roll; the comparison is strict.
        if (recipe.ExtraCountChancePercent is int chance && chance > (float)(p.Random.NextDouble() * 100)) count++;
        double sector = Math.Tau / Math.Max(1, count);
        double rotation = recipe.SectorPlacement ? p.Random.NextDouble() * Math.Tau : 0;
        List<(ProjectileVector Centre, long Id)>? timed = recipe.UpdateTimer ? [] : null;
        int areaIndex = 0;
        for (int i = 0; i < count; i++)
        {
            // Independent area events can overlap; only the terminal collision victim shares direct admission.
            long areaId = ++sequence;
            var centre = p.Position;
            if (recipe.Offset > 0)
            {
                double minimum = Math.Clamp(recipe.MinimumOffset, 0, recipe.Offset);
                bool placed = false;
                // Client: a counter of attempts + 1 is decremented after each draw and before the tests.
                for (int left = Math.Max(1, recipe.PlacementAttempts) + 1; ;)
                {
                    // Sector i spans [phase + i*W - W/2, phase + i*W + W/2).
                    double angle = recipe.SectorPlacement
                        ? rotation + sector * i - sector * .5 + sector * p.Random.NextDouble()
                        : p.Random.NextDouble() * Math.Tau;
                    if (--left == 0) break;
                    // A fixed placement radius takes no radial draw.
                    double distance = minimum < recipe.Offset
                        ? minimum + (recipe.Offset - minimum) * p.Random.NextDouble() : recipe.Offset;
                    centre = p.Position + new ProjectileVector(1, 0).Rotate(angle) * distance;
                    // A collision centre may be exactly on a wall. Test from the incident side of that surface.
                    if (!recipe.RespectTerrain || world.TraceTerrain(p.Position - p.Direction * .001, centre) is null)
                    {
                        placed = true;
                        break;
                    }
                }
                if (!placed) continue;
            }
            if (timed is not null)
            {
                timed.Add((centre, areaId));
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
        if (timed is not null) StartAreaTimer(context, recipe, timed, victim);
    }

    /// <summary>The client's cluster timer: each runtime update subtracts its elapsed time; when the timer is due it
    /// emits exactly one area and adds the interval, keeping any overdue time. The first update is immediate, and a
    /// later update retires the group after its last area. Omitted centres leave no time slot.</summary>
    void StartAreaTimer(ProjectileEntity context, ProjectileAreaRecipe recipe,
        List<(ProjectileVector Centre, long Id)> centres, ProjectileActorKey? victim)
    {
        long groupId = ++sequence;
        var clock = new ProjectileAreaTimer(recipe.Delay, recipe.Interval, centres.Count);
        pendingAreas.Add(groupId, context);
        long last = Now;
        void Update()
        {
            if (!pendingAreas.ContainsKey(groupId)) return;
            int? index = clock.Update(Now - last);
            last = Now;
            if (clock.Retired)
            {
                pendingAreas.Remove(groupId);
                return;
            }
            if (index is int k) ResolveArea(context, recipe, centres[k].Centre, victim, centres[k].Id);
            Schedule((Now / TickQuanta + 1) * TickQuanta, Update);
        }
        Update();
    }

    void ResolveArea(ProjectileEntity p, ProjectileAreaRecipe recipe, ProjectileVector centre,
        ProjectileActorKey? victim, long areaId)
    {
        world.Publish(new(ProjectileEventKind.Area, p, Now, centre, Radius: recipe.Radius, Stage: recipe.Stage));
        var targets = ActorsInOrder(generations: true, out int listed);
        try
        {
            for (int i = 0; i < listed; i++)
            {
                var target = targets[i];
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
        }
        finally { Return(targets, listed); }
    }

    /// <summary>
    /// The actors as they are now, in the order an area's or a pulse's hits are decided in: by id, and with
    /// <paramref name="generations"/> then by generation. It is a copy, taken before the first hit: a hit may change the
    /// host's list. A host that lists them in that order already (the Gateway's session does) costs the copy alone; any
    /// other list is sorted as before, keeping the listed order of equal keys. The array is rented: hand it back with
    /// <see cref="Return"/>. (An area sorted every actor of the instance for each of its explosions.)
    /// </summary>
    ProjectileActor[] ActorsInOrder(bool generations, out int count)
    {
        var actors = world.Actors(Now, Now);
        count = actors.Count;
        var copy = System.Buffers.ArrayPool<ProjectileActor>.Shared.Rent(count);
        bool ordered = true;
        for (int i = 0; i < count; i++)
        {
            var actor = copy[i] = actors[i];
            if (i == 0 || !ordered) continue;
            var before = copy[i - 1].Key;
            ordered = before.Id != actor.Key.Id ? before.Id < actor.Key.Id
                : !generations || before.Generation <= actor.Key.Generation;
        }
        if (ordered) return copy;
        int at = 0;
        var byId = actors.OrderBy(a => a.Key.Id);
        foreach (var actor in generations ? byId.ThenBy(a => a.Key.Generation) : byId) copy[at++] = actor;
        return copy;
    }

    static void Return(ProjectileActor[] actors, int count)
    {
        Array.Clear(actors, 0, count);
        System.Buffers.ArrayPool<ProjectileActor>.Shared.Return(actors);
    }

    public bool TryConsume(long projectileId)
    {
        if (!projectiles.TryGetValue(projectileId, out var p)) return false;
        End(p, ProjectileEndReason.Consumed, allowReturn: false);
        return true;
    }
}
