using GameServer.Core.Projectiles;
using GameServer.Core.World;
using static Game;
using static Program;

public static partial class MapStrongboxes
{
    const string FissureTrigger = "Metadata/Monsters/Daemon/ChestDaemonVaalTriggerVolcanicFissure_";
    const string FissureCaster = "Metadata/Monsters/Daemon/ChestDaemonVaalUseVolcanicFissure";
    const string FissureEffect = "ChestVaalVolcanicFissure";

    public sealed record FissurePlayer(uint Peer, int Epoch);
    public sealed record FissureLeg(int Index, int FromX, int FromY, int EndX, int EndY, int StartFrame)
    {
        internal (int X, int Y) From => (FromX, FromY);
        internal (int X, int Y) End => (EndX, EndY);
        internal double Length => Dist(From, End);
        internal int TravelFrames => FissureFrames(Length);
        internal int Samples => 1 + (Index == 0 ? (int)Math.Ceiling(Length) : TravelFrames);
        internal double Reach(int step) => Index == 0 ? step : step * GroundSlamWave.CellsPerSecond / FissureRate;
    }

    public sealed record FissureLanding(int Frame, double X, double Y);
    public sealed record VaalFissure(long Started, string Daemon, int AimX, int AimY, int EndX, int EndY) : VesselCast(Started, Daemon)
    {
        public HashSet<FissurePlayer> Swept { get; init; } = [];
        public FissureLeg? Repeat { get; set; }
        // Null while the crack travels. An empty volley means every mortar met terrain.
        public FissureLanding[]? Landings { get; set; }
        internal override (int X, int Y) Target(Box box) => (AimX, AimY);
        internal FissureLeg First(Box box) => new(0, box.X, box.Y, EndX, EndY, 0);
    }

    const int FissureRate = (int)(ProjectileRuntime.QuantaPerSecond / ProjectileRuntime.TickQuanta);
    static int FissureFrames(double distance) => (int)Math.Ceiling(GroundSlamWave.AdvanceSeconds(distance) * FissureRate);

    sealed record FissureSkill(VesselSkill Slam, int Radius, long ReleaseTicks, int Repeats, int AggroRange,
        ProjectileProfile Profile, VesselImpact Mortar)
    {
        public VesselImpact Ground { get; } = new(Slam.ActionRecord, Slam.ActionRecord.Damage);
        public int FlightFrames => (int)Math.Ceiling(VolcanicFissureSweep.MortarDistance / Profile.Speed * FissureRate);
        public long Frame(VaalFissure cast, int frame) => cast.Started + ReleaseTicks + frame * TimeSpan.TicksPerSecond / FissureRate;
        public long Sample(VaalFissure cast, FissureLeg leg, int step) => leg.Index == 0
            ? cast.Started + ReleaseTicks + TimeSpan.FromSeconds(GroundSlamWave.AdvanceSeconds(Math.Min(step, leg.Length))).Ticks
            : Frame(cast, leg.StartFrame + step);

        public IEnumerable<double> WatchTimes(VaalFissure cast, Box box)
        {
            var first = cast.First(box);
            // Repeats can turn after the player moves. Register their possible frames before movement history is pruned.
            int last = (Repeats + 1) * (FissureFrames(GroundSlamWave.Length(Radius) + 1) + FlightFrames);
            return Enumerable.Range(0, first.Samples).Select(i => Sample(cast, first, i))
                .Concat(Enumerable.Range(0, last + 1).Select(i => Frame(cast, i)))
                .Distinct().Select(ticks => ticks / (double)TimeSpan.TicksPerSecond);
        }
    }

    static FissureSkill ReadFissure(ClientState c)
    {
        var catalog = SkillsFor(c);
        var variety = Monsters.Find(FissureCaster)!;
        int effect = catalog.EffectRow(FissureEffect);
        byte level = (byte)AreaLevel(c);
        var native = GolemData.Skill(catalog, effect, level);
        int Get(string id) => Stats.Key(id) is uint key ? native.Stats.GetValueOrDefault(key) : 0;
        var (min, max) = Monsters.BaseDamage(variety, level)!.Value;
        var conversion = new DamageConversion();
        conversion.AddSkill(DamageType.Physical, DamageType.Fire, native.Stats[Stats.Key("skill_physical_damage_%_to_convert_to_fire")!.Value]);
        var damage = DamagePacket.Of(DamageType.Physical, min, max).Scale(native.Multiplier).Convert(conversion);
        double seconds = variety.AttackMs / (1000d * (1 + catalog.AttackSpeedMultiplier(effect, native.Level) / 100d));
        ushort action = (ushort)(catalog.ActionHash(effect)!.Value & 0x3fff);
        int radius = SkillArea.Radius(SkillArea.BaseRadius(FissureEffect, Get), Get, spell: false);
        var attack = new MonsterAttack(action, seconds, radius, damage) { Ailments = IncomingAilmentSettings(Get) };
        var slam = new VesselSkill(effect, action, level, native.Stats, attack, native.Crit);
        // The daemon rig has no VolcanicSlam contact measurement. Use the ordinary monster attack midpoint.
        double contact = catalog.MonsterContact(variety.Id, "VolcanicSlam", MonsterActionSequence)?.ContactFraction ?? .5;
        var grant = catalog.FindMonsterSkill(action, effect, level, [.. native.Stats.Select(p => (p.Key, p.Value))])!;
        var secondary = grant.ExtraStatSets.Single().Stats.ToDictionary(s => s.Key, s => s.Value);
        int Combined(string id) => Stats.Key(id) is uint key ? native.Stats.GetValueOrDefault(key) + secondary.GetValueOrDefault(key) : 0;
        var projectile = catalog.Projectile(FissureMortar)!.Value;
        double speed = ProjectileSpeed.CellsPerSecond(projectile.Speed, _ => 0, Combined);
        var profile = GatewayProjectileSkillProfiles.Resolve("VolcanicFissure", FissureMortar, Combined, speed, attack: true);
        int mortarRadius = SkillArea.Radius(SkillArea.BaseRadius(FissureEffect, Combined), Combined, spell: false);
        profile = profile with { EndArea = profile.EndArea! with { Radius = mortarRadius } };
        native.Stats[Stats.Key("total_number_of_projectiles_to_fire")!.Value] = profile.Count;
        var mortarDamage = damage.Scale(1 + Combined("active_skill_hit_ailment_damage_with_projectile_+%_final") / 100d);
        var mortar = attack with { Range = mortarRadius, Damage = mortarDamage, Ranged = true,
            Projectile = new(FissureMortar, projectile.Speed, profile.Lobbed, catalog.ProjectileContactCells(FissureMortar)!.Value) };
        return new(slam, radius, TimeSpan.FromSeconds(seconds * contact).Ticks, slam.Required("vaal_volcanic_fissure_crack_repeat_count"),
            variety.AggroRange, profile, new(mortar, mortarDamage));
    }

    static VaalFissure PrepareFissure(ClientState c, Box box, FissureSkill skill)
    {
        var now = c.Clock.Elapsed;
        var aim = c.Walk.Position(now.TotalSeconds);
        var end = VolcanicFissureSweep.End(c.Layout, (box.X, box.Y), aim, skill.Radius);
        return new(now.Ticks, FissureCaster, aim.X, aim.Y, end.X, end.Y);
    }

    static async Task TickFissures(ClientState conn, Area area, Box box, TimeSpan now)
    {
        var skill = area.FissureSkill ??= ReadFissure(conn);
        foreach (var cast in box.Fissures.ToArray())
        {
            if (!await PublishVesselHits(conn, area, cast)) return;
            if (now.Ticks < cast.Started + skill.ReleaseTicks)
            {
                await ShowVesselCast(conn, area, box, cast, skill.Slam);
                continue;
            }

            if (cast.Removals is null)
            {
                bool complete = await AdvanceFissure(conn, area, box, cast, skill, now.Ticks);
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea) return;
                if (!complete) continue;
            }
            if (cast.Actor != 0 && !await RemoveVesselActor(conn, area, box, cast)) return;
            box.Fissures.Remove(cast);
            Keep(conn, area);
        }
    }

    static async Task<bool> AdvanceFissure(ClientState conn, Area area, Box box, VaalFissure cast, FissureSkill skill, long now)
    {
        while (true)
        {
            var leg = cast.Repeat ?? cast.First(box);
            int samples = leg.Samples;
            var direction = GroundSlamWave.Direction(leg.From, leg.End);
            while (cast.Landed < samples)
            {
                int step = cast.Landed;
                long due = skill.Sample(cast, leg, step);
                if (now < due || !await LandVesselImpact(conn, area, box, cast, skill.Slam, skill.Ground, leg.From, due, Contact)) return false;

                (int X, int Y)? Contact(ClientState player, (int X, int Y) here)
                {
                    double along = VolcanicFissureSweep.ContactDistance(leg.From, leg.End, here, skill.Radius);
                    if (along > leg.Reach(step) || (step > 0 && along <= leg.Reach(step - 1))) return null;
                    if (!cast.Swept.Add(new(player.PeerId, player.IncomingHitEpoch))) return null;
                    return (leg.FromX + (int)Math.Round(direction.X * along), leg.FromY + (int)Math.Round(direction.Y * along));
                }
            }

            int eruption = leg.StartFrame + leg.TravelFrames;
            if (now < skill.Frame(cast, eruption)) return false;
            if (cast.Landings is null)
            {
                lock (conn.Monsters) cast.Landings = FissureMortars(conn, cast.Actor, leg, eruption, skill);
                Keep(conn, area);
            }
            while (cast.Landed - samples < cast.Landings.Length)
            {
                var hit = cast.Landings[cast.Landed - samples];
                long due = skill.Frame(cast, hit.Frame);
                var centre = new ProjectileVector(hit.X, hit.Y);
                var cell = Cell(centre);
                if (now < due || !await LandVesselImpact(conn, area, box, cast, skill.Slam, skill.Mortar, cell, due,
                    (_, here) => (new ProjectileVector(here.X, here.Y) - centre).Length <= skill.Mortar.Attack.Range ? cell : null)) return false;
            }

            int next = eruption + skill.FlightFrames;
            long decision = skill.Frame(cast, next);
            if (now < decision) return false;
            if (leg.Index == skill.Repeats) return true;
            lock (conn.Monsters)
            {
                var end = NextFissureEnd(conn, box, cast, leg, skill, decision);
                cast.Repeat = new(leg.Index + 1, leg.EndX, leg.EndY, end.X, end.Y, next);
                cast.Swept.Clear();
                cast.Landings = null;
                cast.Landed = 0;
                Keep(conn, area);
            }
        }
    }

    static (int X, int Y) NextFissureEnd(ClientState conn, Box box, VaalFissure cast, FissureLeg leg, FissureSkill skill, long due)
    {
        var time = due / (double)TimeSpan.TicksPerSecond;
        var targets = from player in Members(conn)
                      where player.PlayerSent && ViewFor(player).Entered is long entered && entered <= due
                          && !player.Paused && player.PlayerLife > 0 && !player.Immune && !InGracePeriod(player)
                      let at = player.Walk.WatchedPosition((box.Id, cast.Started), time)
                      let distance = Dist(leg.End, at)
                      where distance <= skill.AggroRange
                      orderby distance == 0, distance, player.PeerId
                      select ((int X, int Y)?)at;
        // With no opponent in range the remaining cracks carry on along the last heading.
        var heading = (new ProjectileVector(leg.EndX - leg.FromX, leg.EndY - leg.FromY)).Unit;
        var aim = targets.FirstOrDefault() ?? Cell(new ProjectileVector(leg.EndX, leg.EndY) + heading * GroundSlamWave.Length(skill.Radius));
        return VolcanicFissureSweep.End(conn.Layout, leg.End, aim, skill.Radius);
    }
}
