using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;
using static ArenaHazards;

/// <summary>The bridge's celestial attacks, with server-owned visible footprints and contacts on the same clock.</summary>
internal sealed class CelestialHazards(uint casterId, SpawnedMonster caster, Player owner, Func<bool> current)
{
    const string Art = "Metadata/Effects/Spells/monsters_effects/Solaris_and_Lunaris/";
    readonly HashSet<Player> players = [];
    readonly Dictionary<Player, Exposure> exposures = [];
    readonly Dictionary<uint, Visual> visuals = [];
    readonly List<Patch> patches = [];
    readonly List<Impact> impacts = [];
    readonly List<Meteor> meteors = [];
    readonly List<Flare> flares = [];
    Beam? beam;
    double nextCast, nextGround;
    int cast;
    bool stopped;
    record Visual(uint Id, EffectObject Object, double Until, ushort? Clip = null, float Speed = 1, BeamEffectObject? Link = null);
    record Patch((int X, int Y) At, double Radius, int Effect, double Until);
    record Impact(double At, (int X, int Y) From, (int X, int Y) To, int Effect, double Inner, double Outer, bool Cone);
    record Meteor(double At, (int X, int Y) Position);
    record Flare(double At, (int X, int Y) Origin, (int X, int Y) Aim, float Facing);
    sealed class Beam((int X, int Y) from, (int X, int Y) to, double starts, uint visual)
    {
        internal readonly (int X, int Y) From = from, To = to;
        internal readonly double Starts = starts;
        internal readonly uint Visual = visual;
        internal double NextPatch;
    }
    bool Current => !stopped && CurrentGameWork && current() && !caster.Dead && caster.Life > 0;
    IEnumerable<Player> Present => players.Where(p => !p.Closed && !p.LeavingArea && ReferenceEquals(p.Instance, owner.Instance));
    internal async Task Bind(Player p)
    {
        players.Add(p);
        foreach (var v in visuals.Values.ToArray()) await Show(p, v);
    }
    async Task Show(Player p, Visual v)
    {
        lock (p.Monsters) if (p.SceneViews.Contains(v.Id)) return;
        if (await SendWorldCreate(p, v.Id, () => InstanceEntities.CreateObject(v.Id,
            InstanceEntities.ObjectTypeHash(v.Link is null ? EffectObject.TypePath : BeamEffectObject.TypePath),
            v.Link?.Blob() ?? v.Object.Blob()), "[Celestial] arena effect", wake: true))
        {
            lock (p.Monsters) p.SceneViews.Add(v.Id);
            if (v.Clip is { } clip) await SendWorldRecord(p, v.Id,
                InstanceProtocol.AnimationUpdate(v.Id, 0, clip, v.Speed), "[Celestial] effect animation");
        }
    }
    async Task<uint> Effect(Player c, (int X, int Y) at, string art, double until, float rotation = 0, float scale = 1,
        ushort? clip = null, float speed = 1)
    {
        uint id = c.Instance.World.TakeObjectId();
        var v = new Visual(id, new(at.X, at.Y, Art + art) { Rotation = rotation, Scale = scale,
            AnimatedFlags = 9, Flags0 = EffectObject.FixedRotation }, until, clip, speed);
        visuals[id] = v;
        foreach (var p in Present.ToArray()) await Show(p, v);
        return id;
    }
    async Task Drop(uint id)
    {
        if (!visuals.Remove(id)) return;
        foreach (var p in Present.ToArray())
        {
            bool shown; lock (p.Monsters) shown = p.SceneViews.Remove(id);
            if (shown) { await SendWorldRemoval(p, id, "[Celestial] arena effect ends"); }
        }
    }
    async Task LinkBeam(Player c, (int X, int Y) from, (int X, int Y) to, uint end, double until)
    {
        uint id = c.Instance.World.TakeObjectId();
        var link = new BeamEffectObject(from.X, from.Y, casterId, to.X * BeamEffectObject.WorldPerCell,
            to.Y * BeamEffectObject.WorldPerCell, end, BeamRow: 41, StartBone: "<root>"); // SolarisSkyBeamBeam.
        var v = new Visual(id, new(from.X, from.Y, ""), until, Link: link); visuals[id] = v;
        foreach (var p in Present.ToArray()) await Show(p, v);
    }
    internal static bool ConeContains((int X, int Y) at, (int X, int Y) origin, (int X, int Y) aim, double inner, double outer)
    {
        double distance = Distance(at, origin);
        if (distance <= inner || distance > outer) return false;
        double facing = Math.Atan2(aim.Y - origin.Y, aim.X - origin.X), angle = Math.Atan2(at.Y - origin.Y, at.X - origin.X);
        return Math.Abs(Math.IEEERemainder(angle - facing, Math.Tau)) <= .567232; // Native flare's 32.5-degree half angle.
    }
    internal static (int X, int Y) CascadePoint((int X, int Y) origin, double facing, int index, int a, int b, int degrees)
    {
        double angle = index * degrees * Math.PI / 180d, radius = Math.Max(0, a + b * angle);
        return ((int)Math.Round(origin.X + Math.Cos(facing + angle) * radius),
            (int)Math.Round(origin.Y + Math.Sin(facing + angle) * radius));
    }
    internal async Task Step(Player c, double now)
    {
        if (c.Paused || stopped) return;
        if (!Current) { await Clear(); return; }
        if (!Present.Any(p => p.PlayerLife > 0 && p.MonsterViews.ContainsKey(casterId) && !p.AsleepMonsters.Contains(casterId))) return;
        foreach (var p in Present.ToArray()) await Bind(p);
        foreach (var v in visuals.Values.Where(v => now >= v.Until).OrderBy(v => v.Link is null).ToArray()) await Drop(v.Id);
        patches.RemoveAll(p => now >= p.Until);
        foreach (var flare in flares.Where(f => now >= f.At).ToArray())
        {
            flares.Remove(flare);
            await Effect(c, flare.Origin, "Solaris_e/Sunflare.ao", now + 5.791, flare.Facing, 1.4f, clip: 0);
            // Sunflare's particle contact starts at 480 ms; the warning decal occupies the charge, not the explosion.
            for (int i = 0; i < 5; i++)
                impacts.Add(new(now + .480262 + i * .2, flare.Origin, flare.Aim, 6477,
                    i == 0 ? 0 : (23 + 57 * (i - 1) / 4d) * 1.4, (23 + 57 * i / 4d) * 1.4, true));
        }
        if (beam is { } sweep && now >= sweep.Starts)
        {
            double t = Math.Clamp((now - sweep.Starts) / 2, 0, 1);
            var at = ((int)Math.Round(sweep.From.X + (sweep.To.X - sweep.From.X) * t),
                (int)Math.Round(sweep.From.Y + (sweep.To.Y - sweep.From.Y) * t));
            if (visuals.TryGetValue(sweep.Visual, out var v))
            {
                visuals[sweep.Visual] = v with { Object = v.Object with { GridX = at.Item1, GridY = at.Item2 } };
                foreach (var p in Present.ToArray())
                { await SendWorldRecord(p, v.Id,
                    InstanceProtocol.MoveTo(v.Id, at.Item1, at.Item2, 30, InstanceProtocol.FollowTerrain), "[Celestial] solar beam sweeps"); }
            }
            if (now <= sweep.Starts + 2.25 && now >= sweep.NextPatch)
            {
                sweep.NextPatch = now + .25;
                double until = now + Stat(c, 6464, caster.Level, Stat.BaseSkillEffectDuration) / 1000d;
                patches.Add(new(at, 10.8, 6464, until));
                await Effect(c, at, "Solaris_e/solarbeam_groundfire_01.ao", until, clip: 2);
            }
            if (t == 1) beam = null;
        }
        foreach (var meteor in meteors.Where(m => now >= m.At).ToArray())
        {
            meteors.Remove(meteor);
            double delay = Stat(c, 6472, caster.Level, Stat.FireStormFireballDelayMs) / 1000d;
            // Meteor_v01_01.amd contacts at 999/4966 ms; fit that contact to the native skill's 300 ms delay.
            float speed = (float)(.999 / Math.Max(.01, delay));
            await Effect(c, meteor.Position, "Lunaris_e/meteor_fall_01.ao", now + 4.966 / speed, clip: 0, speed: speed);
            impacts.Add(new(now + delay, meteor.Position, meteor.Position, 6472, 0, 8, false));
        }
        foreach (var hit in impacts.Where(h => now >= h.At).ToArray())
        {
            impacts.Remove(hit);
            if (hit.Effect == 6473)
            {
                double until = hit.At + Stat(c, 6473, caster.Level, Stat.BaseSkillEffectDuration) / 1000d;
                if (now >= until) continue;
                patches.Add(new(hit.To, 12, hit.Effect, until));
                await Effect(c, hit.To, "Lunaris_e/ground_darkness_01.ao", until, clip: 2);
                continue;
            }
            var attack = BossKits.AttackFor(c, caster.Variety!, caster.Level, hit.Effect, 0x4fe0) with { Spell = true, Scripted = true };
            foreach (var p in Present.ToArray())
                await Hit(p, casterId, caster, attack,
                    at => hit.Cone ? ConeContains(at, hit.From, hit.To, hit.Inner, hit.Outer) : Distance(at, hit.To) <= hit.Outer,
                    () => Current, Math.Max(0, 1 + Stat(c, hit.Effect, caster.Level,
                        Stat.MonsterPenaltyAgainstMinionsDamagePlusPctFinalVsPlayerMinions) / 100d));
        }
        if (now >= nextGround)
        {
            nextGround = now + .25;
            foreach (var p in Present.ToArray())
            {
                if (!exposures.TryGetValue(p, out var e)) exposures[p] = e = new();
                await Ground(p, e, now, (at, minion) =>
                {
                    var packet = DamagePacket.Empty;
                    foreach (int effect in patches.Where(h => now < h.Until && Distance(at, h.At) <= h.Radius).Select(h => h.Effect).Distinct())
                        packet = packet.Add(Dot(p, effect, caster.Level, minion));
                    return packet;
                }, () => Current);
            }
        }
        if (now < nextCast) return;
        var target = Present.Where(p => p.PlayerLife > 0).MinBy(p => Distance(caster.Walk.Position(now), p.Walk.Position(now)));
        if (target is null) return;
        bool solar = caster.Variety!.Id == SolarisLunaris.Sun + "CelestialForm";
        int effectId = solar ? (cast % 2 == 0 ? 6464 : 6477) : (cast % 2 == 0 ? 6473 : 8433); cast++;
        var cat = SkillsFor(c);
        var origin = caster.Walk.Position(now); var aim = target.Walk.Position(now);
        double facing = Math.Atan2(aim.Y - origin.Y, aim.X - origin.X);
        double seconds = cat.CastTimeMs(effectId) / 1000d;
        double release = now + seconds * .299 / .966;
        nextCast = now + Math.Max(seconds, cat.CooldownMs(effectId, cat.MaxLevelAt(effectId, caster.Level)) / 1000d);
        foreach (var p in Present.ToArray())
        { await SendWorldRecord(p, casterId,
            InstanceProtocol.AnimationUpdate(casterId, 0, solar ? effectId == 6477 ? (ushort)5 : (ushort)4 : (ushort)3,
                effectId == 6477 ? 1 : (float)(.966 / Math.Max(.01, seconds))), "[Celestial] cast"); }
        if (effectId == 6464)
        {
            var from = ((int)Math.Round(aim.X - Math.Sin(facing) * 30), (int)Math.Round(aim.Y + Math.Cos(facing) * 30));
            var to = ((int)Math.Round(aim.X + Math.Sin(facing) * 30), (int)Math.Round(aim.Y - Math.Cos(facing) * 30));
            uint visual = await Effect(c, from, "Solaris_e/solarbeam_impact.ao", release + 2);
            await LinkBeam(c, origin, from, visual, release + 2);
            beam = new(from, to, release, visual);
        }
        else if (effectId == 6477)
        {
            double charge = Stat(c, effectId, caster.Level, Stat.BaseSkillEffectDuration) / 1000d;
            await Effect(c, origin, "Solaris_e/Sunflare_decal.ao", now + .5 + charge, (float)facing, 1.4f, clip: 0);
            flares.Add(new(now + charge, origin, aim, (float)facing));
        }
        else if (effectId == 6473)
        {
            double flight = Distance(origin, aim) / (750d / WorldUnits.PerCell);
            await Effect(c, aim, "Lunaris_e/orb_of_darkness.ao", release + flight, clip: 0,
                speed: (float)(3.708 / Math.Max(.01, release + flight - now)));
            impacts.Add(new(release + flight, origin, aim, effectId, 0, 12, false));
        }
        else
        {
            int count = Stat(c, effectId, caster.Level, Stat.TriggerCascadeNumberOfSpikes);
            double interval = Stat(c, effectId, caster.Level, Stat.TriggerCascadeMsBetweenSpikes) / 1000d;
            int a = Stat(c, effectId, caster.Level, global::Stat.TriggerCascadeA), b = Stat(c, effectId, caster.Level, global::Stat.TriggerCascadeB),
                degrees = Stat(c, effectId, caster.Level, Stat.TriggerCascadeAngle);
            for (int i = 0; i < count; i++) meteors.Add(new(release + i * interval, CascadePoint(aim, facing, i, a, b, degrees)));
        }
    }
    internal async Task Clear()
    {
        beam = null; patches.Clear(); impacts.Clear(); meteors.Clear(); flares.Clear();
        foreach (var v in visuals.Values.OrderBy(v => v.Link is null).ToArray()) await Drop(v.Id);
    }
    internal void Stop()
    {
        stopped = true; beam = null; patches.Clear(); impacts.Clear(); meteors.Clear(); flares.Clear();
        foreach (var p in Present.ToArray())
            foreach (uint id in visuals.Values.OrderBy(v => v.Link is null).Select(v => v.Id).ToArray())
            {
                lock (p.Monsters) p.SceneViews.Remove(id);
                _ = Send(p, InstanceProtocol.RemoveObject(id), null);
            }
        visuals.Clear();
    }
}
