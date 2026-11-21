using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using static Game;
using static Program;

public static partial class MapStrongboxes
{
    const string VaalSparkEffect = "ChestVaalSpark";
    const string SparkProjectile = "Metadata/Projectiles/Spark";

    public sealed record VaalSpark(long Started, string Daemon, int AimX, int AimY, uint[] Objects, SparkCell[] Walls)
        : VesselProjectileCast(Started, Daemon, AimX, AimY, Walls);

    sealed record SparkSkill(VesselSkill Action, VesselImpact Hit, ProjectileProfile Profile, long ReleaseTicks,
        long Lifetime, ProjectilePresentation? Art) : VesselProjectileSkill(Action, Hit, Profile, ReleaseTicks, Lifetime)
    {
        public override ProjectileProfile FlightProfile(ClientState c, uint actor, ProjectileVector aim) =>
            ClientDrawnProfile(c, Profile, Action.Get, MonsterActionSequence, aim, emitter: actor) with
            {
                Legs = index => new SparkLegs(MonsterActionSequence, index, actor,
                    Lifetime / (double)ProjectileRuntime.QuantaPerSecond, 0, Profile.Speed),
                LegLeash = 150
            };
    }

    static SparkSkill ReadSpark(ClientState c)
    {
        var catalog = SkillsFor(c);
        var controller = ReadVesselSpell(c, "EASChestVaalSpark");
        controller.Timing!.Value.ApplyTo(controller.Stats, Stats);
        var grant = catalog.FindMonsterSkill(controller.Action, controller.Effect, controller.ActorLevel,
            [.. controller.Stats.Select(s => (s.Key, s.Value))])!;
        var spell = ReadVesselSpell(c, VaalSparkEffect) with { PrecedingGrant = grant };
        double speed = ProjectileSpeed.CellsPerSecond(catalog.Projectile(SparkProjectile)!.Value.Speed, _ => 0, spell.Get);
        // The daemon's damaging grant uses the ordinary Spark action, with the chest's own stats.
        var profile = GatewayProjectileSkillProfiles.Resolve("Spark", SparkProjectile, spell.Get, speed, attack: false);
        spell.Stats[Stats.Key("total_number_of_projectiles_to_fire")!.Value] = profile.Count;
        int ms = spell.Required("base_skill_effect_duration");
        return new(spell, spell.Impact(0), profile, TimeSpan.FromSeconds(spell.Seconds).Ticks, ProjectileRuntime.Time(ms / 1000d),
            MonsterProjectilePresentation(catalog.Clips(SparkProjectile), ms / 1000d));
    }

    static VaalSpark PrepareSpark(ClientState c, Box box, string daemon, SparkSkill skill)
    {
        var (started, x, y, walls) = VesselProjectileStart(c, box);
        return new(started, daemon, x, y, [.. Enumerable.Range(0, skill.Profile.Count).Select(_ => c.TakeObjectId())], walls);
    }

    static Task TickSparks(ClientState conn, Area area, Box box, TimeSpan now, bool currentView)
    {
        var skill = area.SparkSkill ??= ReadSpark(conn);
        return TickVesselProjectiles(conn, area, box, now, box.Sparks, skill,
            cast => Task.WhenAll(Members(conn).Select(p => PresentSparks(conn.Instance, p, cast, skill.Art))), currentView);
    }

    static Task PresentSparks(InstanceState instance, ClientState player, VaalSpark cast, ProjectilePresentation? art) =>
        PublishInstanceOutput(instance, player, async current =>
        {
            var view = ViewFor(player);
            var flight = cast.Flight!;
            var wire = player.Wire;
            bool needsCaster;
            lock (player.Monsters) needsCaster = player.PlayerSent && view.Entered.HasValue && !player.MonsterViews.ContainsKey(cast.Actor);
            if (needsCaster) await SyncInstanceMonsters(wire.Remote, wire.Stream, wire.Ciphers, player, reveal: false);
            bool visible;
            lock (player.Monsters)
                visible = player.PlayerSent && view.Entered.HasValue && player.MonsterViews.ContainsKey(cast.Actor)
                    && !player.AsleepMonsters.Contains(cast.Actor);
            await Task.WhenAll(Enumerable.Range(0, cast.Objects.Length).Select(Present));

            async Task Present(int index)
            {
                uint id = cast.Objects[index];
                int prior;
                lock (view.SparkLegs) prior = view.SparkLegs.GetValueOrDefault(id);
                var p = visible ? flight.Runtime.Projectiles.FirstOrDefault(s => s.VolleyIndex == index) : null;
                if (p is null)
                {
                    if (prior != 0)
                        await SendWorldRemoval(wire.Remote, wire.Stream, wire.Ciphers, player, id, "vessel spark ends", current,
                            () =>
                            {
                                lock (view.SparkLegs) return view.SparkLegs.Remove(id) ? InstanceProtocol.RemoveObject(id) : null;
                            });
                    return;
                }
                int leg = flight.Legs[index];
                if (prior == leg) return;
                var at = Cell(p.Position);
                var end = p.Position + p.Direction * p.Remaining;
                if (flight.TraceTerrain(p.Position, end) is { } wall) end = p.Position + (end - p.Position) * wall.Fraction;
                var to = Cell(end);
                var height = ProjectileHeight.Terrain;
                byte[] move = height.MoveTo(id, to.X, to.Y, (float)Math.Max(.001, (end - p.Position).Length / p.Speed));
                if (prior == 0)
                    await SendWorldCreate(player, id, () =>
                    {
                        lock (view.SparkLegs) view.SparkLegs[id] = leg;
                        return CreateProjectileObject(id, new(SparkProjectile, at.X, at.Y, (float)Math.Atan2(p.Direction.X, -p.Direction.Y), Team: 1));
                    }, "vessel spark", current, awaitBirth: false, afterCreation: () => art?.FlightClip is { } clip
                        ? [InstanceProtocol.AnimationUpdate(id, 0, clip, art.FlightClipSpeed), move] : [move]);
                else
                    await SendWorldRecords(wire.Remote, wire.Stream, wire.Ciphers, player, id, () =>
                    {
                        lock (view.SparkLegs) view.SparkLegs[id] = leg;
                        return [height.StopAt(id, at.X, at.Y), move];
                    }, still: current);
            }
        });
}
