using GameServer.Core;
using GameServer.Core.Projectiles;
using GameServer.Core.World;
using static Game;
using static Program;

public static partial class MapStrongboxes
{
    const string VaalCausticArrowEffect = "ChestVaalCausticArrow";
    const string VaalCausticProjectile = "Metadata/Projectiles/VaalCausticArrow";
    const int ArrowStepCells = 5;

    // A path and its ground ids belong to the instance. A runner can leave before the next segment or a recipient's hit is published.
    public sealed record ArrowLeg(double X, double Y, double EndX, double EndY, double Fraction, uint Ground)
    {
        internal ProjectileVector Start => new(X, Y);
        internal ProjectileVector End => new(EndX, EndY);
    }
    public sealed record VaalCausticArrow(long Started, string Daemon, int AimX, int AimY, uint OriginGround, ArrowLeg[][] Steps)
        : VesselCast(Started, Daemon)
    {
        public HashSet<uint> Struck { get; init; } = [];
        internal override (int X, int Y) Target(Box box) => (AimX, AimY);
    }

    sealed record CausticArrowSkill(VesselSkill Skill, VesselImpact Impact, ProjectileProfile Profile, long ReleaseTicks,
        long StepTicks, long GroundTicks, double GroundDps, int GroundRadius, GroundArt Art);

    static CausticArrowSkill ReadCausticArrow(ClientState c, string daemon)
    {
        var catalog = SkillsFor(c);
        int effect = catalog.EffectRow(VaalCausticArrowEffect);
        byte level = (byte)AreaLevel(c);
        var variety = Monsters.Find(daemon)!;
        var native = GolemData.Skill(catalog, effect, level);
        int Get(string key) => Stats.Key(key) is uint stat ? native.Stats.GetValueOrDefault(stat) : 0;
        int Required(string key) => native.Stats[Stats.Key(key)!.Value];
        var (min, max) = Monsters.BaseDamage(variety, level)!.Value;
        var conversion = new DamageConversion();
        conversion.AddSkill(DamageType.Physical, DamageType.Chaos, Required("skill_physical_damage_%_to_convert_to_chaos"));
        var damage = DamagePacket.Of(DamageType.Physical, min, max).Scale(native.Multiplier).Convert(conversion);
        ushort action = (ushort)(catalog.ActionHash(effect)!.Value & 0x3fff);
        double seconds = variety.AttackMs / (1000d * (1 + catalog.AttackSpeedMultiplier(effect, native.Level) / 100d));
        int speed = catalog.Projectile(VaalCausticProjectile)!.Value.Speed;
        int cells = catalog.ProjectileContactCells(VaalCausticProjectile)!.Value;
        var projectile = new SkillCatalog.MonsterProjectileInfo(VaalCausticProjectile, speed, false, cells) { AnimationKey = "Puncture" };
        ApplyProjectilePanelStats(effect, native.Stats);
        int count = Required("total_number_of_projectiles_to_fire");
        var profile = ClientDrawnProfile(c, new(VaalCausticProjectile) { Count = count, Speed = ProjectileSpeed.CellsPerSecond(speed, _ => 0, Get) },
            Get, MonsterActionSequence, new(1, 0));
        var attack = new MonsterAttack(action, seconds, profile.Range, damage) { Ranged = true, CannotBeEvaded = Get("global_always_hit") != 0,
            Projectile = projectile, Ailments = IncomingAilmentSettings(Get) };
        var skill = new VesselSkill(effect, action, level, native.Stats, attack, native.Crit);
        double release = MonsterReleaseFor(c, variety, effect, level, projectile)?.Fraction ?? .5;
        int groundRadius = SkillArea.Radius(20, Get, spell: false);
        return new(skill, new(attack, damage), profile, TimeSpan.FromSeconds(seconds * release).Ticks,
            TimeSpan.FromSeconds(ArrowStepCells / profile.Speed).Ticks, Required("base_skill_effect_duration") * TimeSpan.TicksPerMillisecond,
            Required("base_chaos_damage_to_deal_per_minute") / 60d, groundRadius,
            GroundArt.Of("CausticCloud", Required("vaal_caustic_arrow_ground_art_variation"))!);
    }

    static VaalCausticArrow PrepareCausticArrow(ClientState conn, Box box, string daemon, CausticArrowSkill skill)
    {
        var now = conn.Clock.Elapsed;
        var aim = conn.Walk.Position(now.TotalSeconds);
        var origin = new ProjectileVector(box.X, box.Y);
        var heading = (new ProjectileVector(aim.X, aim.Y) - origin).Unit;
        if (aim == (box.X, box.Y)) aim = (box.X + 1, box.Y);
        var grid = ProjectileGrid.Of(conn.Layout.Id, conn.Seed);
        HashSet<(int, int)> walls;
        lock (conn.Monsters) walls = [.. conn.ProjectileWalls];
        bool Blocked(int x, int y) => ProjectileGrid.Blocks(conn.Layout, grid, walls, x, y);
        var steps = new List<List<ArrowLeg>>();
        var profile = skill.Profile;
        for (int arrow = 0; arrow < profile.Count; arrow++)
        {
            var basis = heading.Rotate(profile.NativeVolley!.Angle(arrow, profile.Count, 1));
            var at = origin;
            for (int i = 0; i * ArrowStepCells < profile.Range; i++)
            {
                double length = Math.Min(ArrowStepCells, profile.Range - i * ArrowStepCells);
                // ActiveSkills supplies the serpentine shape. Its exact curve is unmeasured; bend the heading by up to
                // 30 degrees over a 40-cell cycle and keep each leg's speed and total travelled distance constant.
                double bend = Math.PI / 6 * Math.Sin(Math.Tau * (i * ArrowStepCells + length / 2) / 40);
                var delta = basis.Rotate(bend) * length;
                var wall = ProjectileGeometry.TraceGrid(at, at + delta, Blocked);
                double part = wall?.Fraction ?? 1;
                // Keep a terminal ground centre on the near side of the blocking cell's half-cell boundary.
                var end = at + delta * (wall.HasValue ? Math.Max(0, part - .00001) : part);
                bool terminal = wall.HasValue || (i + 1) * ArrowStepCells >= profile.Range;
                uint ground = (i % 2 == 1 || terminal) && (end - origin).Length > .5 ? conn.TakeObjectId() : 0;
                if (i == steps.Count) steps.Add([]);
                steps[i].Add(new(at.X, at.Y, end.X, end.Y, part * length / ArrowStepCells, ground));
                if (terminal) break;
                at = end;
            }
        }
        return new(now.Ticks, daemon, aim.X, aim.Y, conn.TakeObjectId(), [.. steps.Select(legs => legs.ToArray())]);
    }

    static async Task TickCausticArrows(ClientState conn, Area area, Box box, TimeSpan now, bool currentView)
    {
        foreach (var cast in box.CausticArrows.ToArray())
        {
            var skill = area.CausticArrowSkill ??= ReadCausticArrow(conn, cast.Daemon);
            if (!await PublishVesselHits(conn, area, cast)) return;
            long release = cast.Started + skill.ReleaseTicks;
            if (now.Ticks < release)
            {
                if (currentView) await ShowVesselCast(conn, area, box, cast, skill.Skill);
                continue;
            }
            int count = (int)Math.Min(cast.Steps.Length, (now.Ticks - release) / skill.StepTicks);
            lock (area.Zones)
            {
                Ground(cast.OriginGround, (box.X, box.Y), release);
                for (int i = 0; i < count; i++)
                    foreach (var leg in cast.Steps[i])
                        if (leg.Ground != 0) Ground(leg.Ground, Cell(leg.End), release + i * skill.StepTicks + (long)(leg.Fraction * skill.StepTicks));
            }
            while (cast.Landed < count)
            {
                long start = release + cast.Landed * skill.StepTicks;
                if (!await LandVesselImpact(conn, area, box, cast, skill.Skill, skill.Impact, (box.X, box.Y), start + skill.StepTicks, Contact)) return;

                (int X, int Y)? Contact(ClientState p, (int X, int Y) here)
                {
                    if (cast.Struck.Contains(p.PeerId)) return null;
                    double entered = Math.Max(0, (ViewFor(p).Entered!.Value - start) / (double)skill.StepTicks);
                    var target = new ProjectileVector(here.X, here.Y);
                    foreach (var leg in cast.Steps[cast.Landed])
                    {
                        if (entered > leg.Fraction) continue;
                        var from = entered == 0 ? leg.Start : leg.Start + (leg.End - leg.Start) * (entered / leg.Fraction);
                        double reach = skill.Profile.ContactCells!.Value + LivingCollision.PlayerSize + .5;
                        if (ProjectileGeometry.SquareContact(from - target, leg.End - target, reach) is not double t) continue;
                        cast.Struck.Add(p.PeerId); // A piercing volley hits each player once and continues through them.
                        return Cell(from + (leg.End - from) * t);
                    }
                    return null;
                }
            }
            if (cast.Landed == cast.Steps.Length && cast.Actor != 0)
            {
                if (!await RemoveVesselActor(conn, area, box, cast)) return;
                cast.Actor = 0;
                Keep(conn, area);
            }
            if (now.Ticks >= release + cast.Steps.Length * skill.StepTicks + skill.GroundTicks)
            {
                box.CausticArrows.Remove(cast);
                Keep(conn, area);
            }
            void Ground(uint id, (int X, int Y) at, long born)
            {
                long until = born + skill.GroundTicks;
                if (until <= now.Ticks || area.Zones.ContainsKey(id)) return;
                area.Zones.Add(id, new(new(at, skill.GroundRadius, until / (double)TimeSpan.TicksPerSecond, DamageType.Chaos,
                    skill.GroundDps, null, id), skill.Art.Pieces.Single(), born / (double)TimeSpan.TicksPerSecond,
                    (float)(skill.GroundRadius * WorldUnits.PerCell / skill.Art.PieceRadius)));
            }
        }
    }
    // Resource recovery owns chaos damage and ES bypass. Equal ground from all vessels in an area deals one
    // rate for the union of the covered time. Never charge the interval before the player entered or the trail formed.
    internal static int AdvanceCausticGroundLocked(ClientState p, double from, double until, IReadOnlyDictionary<uint, int> stats,
        bool immune, ref double otherDotStart)
    {
        if (!ZoneShare.TryGetValue(p, out var share) || share.Caustic.Length == 0 || p.Paused || p.Immune || immune
            || InGracePeriod(p) || ViewFor(p).Entered is not long entered) return 0;
        int Get(string name) => Stats.Key(name) is uint stat ? stats.GetValueOrDefault(stat) : 0;
        if (Get("base_cannot_be_damaged") != 0) return 0;
        from = Math.Max(from, Math.Max(share.CausticThrough, entered / (double)TimeSpan.TicksPerSecond));
        if (from >= until) return 0;
        var here = p.Walk.Position(until);
        double raw = 0;
        foreach (var patch in share.Caustic.Where(patch => Distance(here, patch.Zone.At) <= patch.Zone.Radius).OrderBy(patch => patch.CausticFrom))
        {
            double start = Math.Max(from, patch.CausticFrom!.Value), end = Math.Min(until, patch.Zone.Until);
            if (start >= end) continue;
            if (share.CausticThrough < start) share.CausticStarted = start;
            otherDotStart = Math.Max(otherDotStart, share.CausticStarted);
            double halved = Get("keystone_lethe_shade") == 0 ? 0 : Math.Clamp(otherDotStart + 1 - start, 0, end - start);
            raw += patch.Zone.Dps * (end - start - halved / 2);
            from = share.CausticThrough = end;
        }
        double taken = DamageModel.Mitigate(raw, Resistance(p, DamageType.Chaos), MaxResistance(p, DamageType.Chaos))
            * p.ElementalAilments.DamageTakenFactor(until, 0);
        int damage = IncomingResourceDefences.TakenOverTime(taken, Get, ref share.TakenCarry, IncomingResourceDefences.LessTakenPct(p.Pools, Get),
            typedIncreasedPct: Get("chaos_damage_taken_+%"), finalTakenPct: Get(IncomingResourceDefences.UnhingedSaneDamageFinal));
        return IncomingResourceDefences.DamageOverTime(p.EnergyShield, damage, ChaosDamageBypassesEnergyShield(p, stats) ? 100 : 0,
            until, ref p.ChaosDotBypassCarry);
    }

    static (int X, int Y) Cell(ProjectileVector point) => ((int)Math.Round(point.X), (int)Math.Round(point.Y));
}
