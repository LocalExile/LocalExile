namespace GameServer.Core.Projectiles;

public sealed partial class ProjectileRuntime
{
    void StartStages(ProjectileEntity p)
    {
        if (p.Phase == 0 && p.Profile.Steering is { } steering) StartSteering(p, steering);
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
            var rule = pulse.HitRule ?? p.Profile.HitRule;
            long group = rule.Scope == ProjectileHitScope.Projectile ? p.Id : p.EmissionId;
            void Pulse()
            {
                if (!projectiles.ContainsKey(p.Id)) return;
                var targets = ActorsInOrder(world.ActorsNear(p.Position, pulse.Radius + 2, Now), generations: false, out int listed);
                try
                {
                    for (int i = 0; i < listed; i++)
                    {
                        var target = targets[i];
                        if (target.Team == p.Use.Team || target.Key == p.Use.Owner
                            || !ledger.CanHit(group, 0, pulse.Stage, target.Key, Now, p.CreatedAt, rule)) continue;
                        var delta = target.Position(Now) - p.Position;
                        double limit = pulse.Radius + target.Radius;
                        if (Math.Abs(delta.X) <= limit && Math.Abs(delta.Y) <= limit
                            && delta.X * delta.X + delta.Y * delta.Y <= limit * limit)
                            Damage(p, target, pulse.Stage, group: group, rule: rule, isArea: pulse.AreaDamage, admissionPhase: 0);
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
                if (emission.TravelGroups is not int limit || p.EmissionNumber < limit)
                    Schedule(Now + emission.Interval, Emit);
            }
            Schedule(Now + emission.Interval, Emit);
        }
    }

    void StartSteering(ProjectileEntity p, ProjectileSteeringRecipe steering)
    {
        var desired = steering.Direction(p, Now);
        long nextTarget = Now + steering.TargetInterval;

        void Turn()
        {
            if (!projectiles.ContainsKey(p.Id) || p.Phase != 0) return;
            if (Now >= nextTarget)
            {
                desired = steering.Direction(p, Now);
                nextTarget = Now + steering.TargetInterval;
            }
            if (desired is { Length: > 0 } target)
            {
                var direction = target.Unit;
                double cross = p.Direction.X * direction.Y - p.Direction.Y * direction.X;
                double delta = Math.Atan2(cross, p.Direction.Dot(direction));
                if (Math.Abs(delta) > 1e-6)
                {
                    p.Direction = Math.Abs(delta) <= steering.MaximumTurn ? direction
                        : p.Direction.Rotate(Math.CopySign(steering.MaximumTurn, delta));
                    Publish(ProjectileEventKind.Redirect, p);
                }
            }
            Schedule(Now + steering.TurnInterval, Turn);
        }

        Schedule(Now + steering.TurnInterval, Turn);
    }

    void EmitAfterEnd(ProjectileEntity parent, ProjectileEmissionRecipe recipe, ProjectileActorKey? excluded)
    {
        if (recipe.Waves is { } waves)
        {
            foreach (var (delay, count) in waves)
                Schedule(Now + delay, () =>
                {
                    ResetChildEmission(parent);
                    EmitChildren(parent, recipe with { Child = recipe.Child with { Count = count } }, excluded);
                });
            return;
        }
        if (recipe.Delay > 0) Schedule(Now + recipe.Delay, () => { ResetChildEmission(parent); EmitChildren(parent, recipe, excluded); });
        else { ResetChildEmission(parent); EmitChildren(parent, recipe, excluded); }
    }

    // Dropping a child emission group takes it out of the active set just as a removal does.
    void ResetChildEmission(ProjectileEntity parent)
    {
        if (parent.ChildEmission != 0) removals++;
        parent.ChildEmission = 0;
    }

    void EmitChildren(ProjectileEntity parent, ProjectileEmissionRecipe recipe, ProjectileActorKey? excluded = null)
    {
        var child = recipe.Child;
        Validate(child);
        if (parent.ChildEmission == 0) parent.ChildEmission = ++sequence;
        var baseDirection = recipe.BaseDirection?.Invoke(parent) ?? parent.Direction;
        var payload = recipe.ChildPayload is { } childPayload ? childPayload(parent.Use.Payload) : parent.Use.Payload;
        for (int i = 0; i < child.Count; i++)
        {
            double angle = recipe.RandomDirections ? parent.Random.NextDouble() * Math.Tau
                : recipe.Radial ? Math.Tau * i / child.Count + parent.EmissionNumber * 2.399963229728653
                : child.Count <= 1 ? 0 : child.SpreadRadians * (i / (double)(child.Count - 1) - .5);
            var direction = recipe.RandomDirections ? parent.Direction.Rotate(angle) : baseDirection.Rotate(angle);
            var use = parent.Use with { Profile = child, Origin = parent.Position, Payload = payload,
                Aim = parent.Position + direction * child.Range };
            var spawned = Spawn(use, parent.UseId, parent.ChildEmission, parent.Id, parent.Position, direction);
            if (excluded is { } target) spawned.TurnaroundTarget = target;
            // Point Blank/Far Shot measure the whole continuation, not just the child's own segment.
            spawned.Distance = parent.Distance;
        }
        parent.EmissionNumber++;
    }

    void TargetedHits(ProjectileEntity p, ProjectileActor primary, ProjectileTargetedRecipe recipe)
    {
        long hitEvent = ++sequence;
        var origin = primary.Position(Now);
        foreach (var target in world.ActorsNear(origin, recipe.Radius + 2, Now).Where(a => a.Team != p.Use.Team && a.Key != primary.Key
            && a.Key != p.Use.Owner && (a.Position(Now) - origin).Length <= recipe.Radius)
            .OrderBy(a => (a.Position(Now) - origin).Length).ThenBy(a => a.Key.Id).Take(recipe.Count))
            Damage(p, target, recipe.Stage, hitEvent, new());
        removals++; // The hit event group is finished and was never active.
    }

    /// <summary>Resolve a grounded projectile impact without a horizontal flight or a direct-hit victim.</summary>
    public void Impact(ProjectileUse use, ProjectileVector centre, ProjectileAreaRecipe area)
        { Area(ImpactEntity(use, centre), area); removals++; }

    /// <summary>Resolve area hits within halfWidth of the segment. Distance modifiers use the path to its start.</summary>
    public void ImpactAlong(ProjectileUse use, ProjectileVector start, ProjectileVector end, double halfWidth, string stage)
    {
        var halfSegment = (end - start) * .5;
        var impact = ImpactEntity(use, start);
        ResolveArea(impact, new(stage, halfWidth), start + halfSegment, null, impact.Id, halfSegment);
        removals++;
    }

    /// <summary>Resolve a staged contact against one selected target without a horizontal flight.</summary>
    public ProjectileHitResult ImpactTarget(ProjectileUse use, ProjectileActor target, string stage)
    {
        var result = Damage(ImpactEntity(use, target.Position(Now)), target, stage);
        removals++; // The impact group is finished and was never active.
        return result;
    }

    ProjectileEntity ImpactEntity(ProjectileUse use, ProjectileVector centre)
    {
        long id = ++sequence;
        return new ProjectileEntity(id, id, id, 0, use, centre, centre - use.Origin, Now)
            { Distance = (centre - use.Origin).Length };
    }

    void Area(ProjectileEntity p, ProjectileAreaRecipe recipe, ProjectileActorKey? victim = null,
        ProjectileVector? contactCentre = null)
    {
        var context = recipe.Delay > 0 || recipe.Interval > 0 ? p.AreaSnapshot() : p;
        int count = recipe.Count;
        // The client always samples the extra-area roll; the comparison is strict.
        if (recipe.ExtraCountChancePercent is int chance && chance > (float)(p.Random.NextDouble() * 100)) count++;
        double sector = Math.Tau / Math.Max(1, count);
        double rotation = recipe.SectorPlacement ? p.Random.NextDouble() * Math.Tau : 0;
        List<(ProjectileVector Centre, long Id, double AxisRotation)>? timed = recipe.UpdateTimer ? [] : null;
        int areaIndex = 0;
        for (int i = 0; i < count; i++)
        {
            // Independent area events can overlap; only the terminal collision victim shares direct admission.
            long areaId = ++sequence;
            var centre = contactCentre ?? p.Position;
            double axisRotation = recipe.ConeAxisRotation + i * recipe.ConeAxisStep;
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
                timed.Add((centre, areaId, axisRotation));
                continue;
            }
            // Rejected centres consume their spatial sample, but no time in the surviving sequence.
            long at = checked(Now + recipe.Delay + areaIndex++ * recipe.Interval);
            if (at == Now)
            {
                ResolveArea(context, recipe, centre, victim, areaId, axisRotation: axisRotation);
                removals++; // Resolved at once: this area group was never active.
            }
            else
            {
                pendingAreas.Add(areaId, context);
                Schedule(at, () =>
                {
                    // A discarded area must not resolve: Discard already took it out of
                    // pendingAreas, so resolving here would hit for a dead group and leave
                    // orphaned ledger entries behind. Same guard as StartAreaTimer.
                    if (!pendingAreas.ContainsKey(areaId)) return;
                    try { ResolveArea(context, recipe, centre, victim, areaId, axisRotation: axisRotation); }
                    finally { if (pendingAreas.Remove(areaId)) removals++; }
                });
            }
        }
        if (timed is not null) StartAreaTimer(context, recipe, timed, victim);
    }

    /// <summary>The client's cluster timer: each runtime update subtracts its elapsed time; when the timer is due it
    /// emits exactly one area and adds the interval, keeping any overdue time. The first update is immediate, and a
    /// later update retires the group after its last area. Omitted centres leave no time slot.</summary>
    void StartAreaTimer(ProjectileEntity context, ProjectileAreaRecipe recipe,
        List<(ProjectileVector Centre, long Id, double AxisRotation)> centres, ProjectileActorKey? victim)
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
                if (pendingAreas.Remove(groupId)) removals++;
                return;
            }
            if (index is int k)
            {
                var (centre, id, axisRotation) = centres[k];
                ResolveArea(context, recipe, centre, victim, id, axisRotation: axisRotation);
            }
            Schedule((Now / TickQuanta + 1) * TickQuanta, Update);
        }
        Update();
    }

    void ResolveArea(ProjectileEntity p, ProjectileAreaRecipe recipe, ProjectileVector centre,
        ProjectileActorKey? victim, long areaId, ProjectileVector halfSegment = default, double axisRotation = 0)
    {
        double radius = recipe.RadiusForTravel?.Invoke(p.Distance)
            ?? recipe.RadiusForGroundBounce?.Invoke(p.GroundBounces) ?? recipe.Radius;
        world.Publish(new(ProjectileEventKind.Area, p, Now, centre, Radius: radius, Stage: recipe.Stage));
        double lengthSquared = halfSegment.Dot(halfSegment);
        double halfLength = lengthSquared > 0 ? Math.Sqrt(lengthSquared) : 0;
        double inverseLengthSquared = lengthSquared > 0 ? 1 / lengthSquared : 0;
        var targets = ActorsInOrder(world.ActorsNear(centre, radius + halfLength + 2, Now), generations: true, out int listed);
        double coneEdge = recipe.ConeHalfAngle > 0 ? Math.Cos(recipe.ConeHalfAngle) : -1;
        var axis = coneEdge > -1 && axisRotation != 0 ? p.Direction.Rotate(axisRotation) : p.Direction;
        try
        {
            for (int i = 0; i < listed; i++)
            {
                var target = targets[i];
                if (target.Team == p.Use.Team || target.Key == p.Use.Owner
                    || (recipe.ExcludeContactTarget && target.Key == victim)) continue;
                var delta = target.Position(Now) - centre;
                if (inverseLengthSquared > 0)
                    delta -= halfSegment * Math.Clamp(delta.Dot(halfSegment) * inverseLengthSquared, -1, 1);
                double limit = radius + target.Radius;
                if (Math.Abs(delta.X) <= limit && Math.Abs(delta.Y) <= limit
                    && delta.X * delta.X + delta.Y * delta.Y <= limit * limit
                    && (coneEdge <= -1 || delta.Dot(axis) >= coneEdge * delta.Length))
                {
                    if (target.Key == victim)
                        Damage(p, target, recipe.Stage, multiplier: recipe.Multiplier, admissionStage: "direct", isArea: true);
                    else Damage(p, target, recipe.Stage,
                        recipe.Scope == ProjectileHitScope.Emission ? p.EmissionId : areaId, new(), recipe.Multiplier,
                        admissionStage: recipe.Scope == ProjectileHitScope.Emission
                            ? recipe.Stage + ":" + p.GroundBounces : recipe.Stage, isArea: true);
                }
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
    ProjectileActor[] ActorsInOrder(IReadOnlyList<ProjectileActor> actors, bool generations, out int count)
    {
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
