using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

static partial class Sirus
{
    static readonly RuleTable<ClientState, Exposure> Exposures = new();
    sealed class Exposure
    {
        public double Dps, BeamUntil, NextBeam, BloodUntil, BloodDps;
        public int BeamStacks, BloodStacks;
        public ushort BeamBuff, BloodBuff;
        public readonly double[] DotCarry = new double[5];
    }
    static ushort[] ClearExposure(ClientState c)
    {
        if (!Exposures.TryGetValue(c, out var e)) return [];
        lock (c.Monsters)
        {
            c.TrapDegenPerSecond = Math.Max(0, c.TrapDegenPerSecond - e.Dps);
            e.Dps = 0; e.BeamStacks = e.BloodStacks = 0; e.BeamUntil = e.BloodUntil = 0;
            ushort[] buffs = [e.BeamBuff, e.BloodBuff]; e.BeamBuff = e.BloodBuff = 0;
            Array.Clear(e.DotCarry); return buffs;
        }
    }
    static async Task RemoveExposure(ClientState c)
    {
        ushort[] buffs = ClearExposure(c);
        if (buffs.Length == 0 || c.Closed || c.LeavingArea) return;
        var w = c.Wire;
        foreach (ushort buff in buffs.Where(b => b != 0))
            await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, c, PlayerId, InstanceEntities.RemoveBuff(PlayerId, buff), null);
        await RefreshPlayerStats(c);
    }
    static async Task RefreshPlayerStats(ClientState c)
    {
        lock (c.Monsters) { c.Pools = PoolsFor(c, c.Pools.Level); c.ApplyMovementSpeed(); }
        var w = c.Wire;
        await SendPlayerStatSnapshot(w.Remote, w.Stream, w.Ciphers, c, () => PlayerStatsUpdateAtGate(c, PlayerStatsWithCleared(c)), null);
    }
    internal static Dictionary<uint, int> PlayerStats(ClientState c, Dictionary<uint, int> stats)
    {
        if (Exposures.TryGetValue(c, out var e) && e.BeamUntil > c.Clock.Elapsed.TotalSeconds)
        {
            foreach (var (name, value) in new[] { ("action_speed_-%", e.BeamStacks * 6), ("base_damage_taken_+%", e.BeamStacks * 10) })
                if (Stats.Key(name) is uint key) stats[key] = stats.GetValueOrDefault(key) + value;
        }
        return stats;
    }
    sealed record Deferred(double At, int Generation, ClientState Target, (int X, int Y) From, (int X, int Y) Aim);
    sealed record TimedAnimation(double At, int Generation, byte Animation, float Seconds);
    sealed record Hit(double At, int Generation, int Group, int Effect, (int X, int Y) From, (int X, int Y) To,
        double Radius, bool Beam = false, ClientState? Track = null, bool ApparitionHit = false);
    sealed record Visual(uint Id, (int X, int Y) At, string Model, uint? Clip, double Until, float Rotation, bool Blocking, bool Temporary);
    sealed class StormState(uint id, SpawnedMonster monster, double born, double angle)
    {
        public readonly uint Id = id; public readonly SpawnedMonster Monster = monster;
        public readonly double Born = born, Angle = angle;
        public double Next;
        public bool Mobile => Monster.Variety?.Id == Storm;
    }
    sealed record Patch((int X, int Y) At, double Until, double Radius, int Effect);
    sealed class Quad((int X, int Y) at, double from, double until, double angle)
    {
        public readonly (int X, int Y) At = at;
        public readonly double From = from, Until = until, Angle = angle;
        public uint[]? Beams;
    }
    internal sealed partial class Fight
    {
        readonly List<Hit> hits = [];
        readonly List<Deferred> deferred = [];
        readonly List<TimedAnimation> animations = [];
        readonly List<Visual> visuals = [];
        readonly List<StormState> storms = [];
        readonly List<Patch> patches = [];
        readonly List<Quad> quads = [];
        readonly HashSet<(int, ClientState, uint)> struck = [];
        readonly Dictionary<(ClientState, uint), double> carry = [];
        int group, special, apparitionRotation;
        (int X, int Y) mazeCentre, corridorEnd;
        double specialUntil, lastGround;
        void CancelCombat() { hits.Clear(); deferred.Clear(); animations.Clear(); struck.Clear(); special = 0; if (body is not null) { body.ActionGeneration++; body.SwingGeneration = null; } }
        void RegisterStorm(uint key, SpawnedMonster m, double now)
        {
            if (storms.Any(s => s.Id == key)) return;
            var at = m.Walk.Position(now);
            double a = Math.Atan2(at.Y - scene.Centre.Y, at.X - scene.Centre.X);
            if (Distance(at, scene.Centre) < 10) a = Math.PI * -.3 + Round * .7;
            storms.Add(new(key, m, now, a));
        }
        async Task Retire(ClientState c, uint actor)
        {
            if (actor == 0) return;
            lock (c.Monsters)
            {
                if (c.Monsters.Remove(actor, out var m)) { m.NoRewards = true; m.Life = 0; m.Dead = true; m.ActionGeneration++; }
                c.Instance.World.MonsterOrigins.TryRemove(actor, out _);
            }
            await Remove(actor);
        }
        async Task ClearNearbyStorms(ClientState c, (int X, int Y) at, double radius)
        {
            foreach (var s in storms.Where(s => Distance(s.Monster.Walk.Position(c.Clock.Elapsed.TotalSeconds), at) < radius).ToArray())
            { storms.Remove(s); await Retire(c, s.Id); }
        }
        async Task ClearWalls(ClientState c)
        {
            foreach (var fx in visuals.Where(v => v.Blocking).ToArray())
            { visuals.Remove(fx); foreach (var p in players) p.RemoveBlocker(fx.Id); await Remove(fx.Id); }
        }
        async Task ClearTemporary(ClientState c)
        {
            hits.Clear(); patches.Clear(); quads.Clear();
            foreach (var v in visuals.Where(v => v.Temporary).ToArray())
            { visuals.Remove(v); foreach (var p in players) p.RemoveBlocker(v.Id); await Remove(v.Id); }
        }
        async Task ClearHazards()
        {
            ClearAllExposure();
            foreach (var p in players) foreach (var fx in visuals) p.RemoveBlocker(fx.Id);
            foreach (var fx in visuals.ToArray()) await Remove(fx.Id);
            visuals.Clear();
            foreach (var s in storms.ToArray()) await Retire(players[0], s.Id);
            storms.Clear(); patches.Clear(); quads.Clear();
        }
        async Task Cast(ClientState c, uint actor, int effect, (int X, int Y) aim, double now)
        {
            var m = c.Monsters[actor]; var from = m.Walk.Position(now);
            var attack = Attack(c, m.Variety!, m.Level, effect);
            lock (c.Monsters) { m.ActionGeneration++; m.SwingGeneration = null; m.BusyUntil = now + attack.Seconds; }
            await All(actor, [InstanceProtocol.ActionStarted(actor, from.X, from.Y, aim.X, aim.Y, attack.SkillId, c.NextActionSequence(), 0x0408),
                InstanceProtocol.ActionEnded(actor)], true);
        }
        async Task Beam(ClientState c, Hit hit, (int X, int Y) aim, double now)
        {
            double angle = Math.Atan2(aim.Y - hit.From.Y, aim.X - hit.From.X);
            string model = hit.ApparitionHit ? "fusion_beam.ao" : Round == 3 ? "spam_beam_final.ao" : "spam_beam.ao";
            await Fx(c, hit.From, Art + model, now + .6, rotation: (float)(angle + Math.PI / 2));
        }
        async Task Combat(ClientState c, double now)
        {
            if (special == 1 && Distance(c.Walk.Position(now), Position(now)) < 10)
            {
                CancelCombat(); body!.LifePhases!.DamageTakenPercent = 100;
                await Basic(c, id, "ChangeToStance" + (Round == 3 ? "2Immediate" : "1Immediate"), now, .1);
                await ClearWalls(c); ready = now + 1; return;
            }
            if (special != 0 && now >= specialUntil)
            { special = 0; body!.LifePhases!.DamageTakenPercent = 100; }
            if (now < ready || special != 0 || InGracePeriod(c)) return;
            int[] cycle = Round == 3 ? [0, 1, 4, 0, 5, 1, 2, 0, 3, 1] : [0, 1, 0, 2, 0, 3, 1];
            int kind = cycle[rotation++ % cycle.Length];
            if (kind is 2 or 3 && storms.Any(s => Distance(Position(now), s.Monster.Walk.Position(now)) < 90)) kind = 1;
            var from = Position(now); var aim = c.Walk.Position(now);
            body!.LifePhases!.DamageTakenPercent = 100;
            if (kind == 0)
            {
                int effect = Round == 3 ? 7012 : 7008;
                await Cast(c, id, effect, aim, now);
                hits.Add(new(now + .672 * Attack(c, body.Variety!, body.Level, effect).Seconds / 1.5, body.ActionGeneration, ++group,
                    effect, from, Extend(from, aim, 150), 3, Beam: true));
                ready = now + Attack(c, body.Variety!, body.Level, effect).Seconds + .3;
            }
            else if (kind == 1)
            {
                var at = RingPoint(c, aim, 30 + Random.Shared.Next(20), now);
                await Cast(c, id, 6809, at, now); await Teleport(c, id, at, now);
                deferred.Add(new(now + .5, body.ActionGeneration, c, at, aim));
                ready = now + .5 + (Round == 3 ? 2.5 : 2) + 1;
            }
            else if (kind == 2) await Maze(c, now);
            else if (kind == 3) await Corridor(c, now);
            else if (kind == 4) await QuadBeam(c, now);
            else await Clones(c, now);
            Console.WriteLine($"[Sirus] phase {Round + 1}: {new[] { "beam", "teleport / DIE", "meteor maze", "corridor", "rotating beams", "clone circle" }[kind]}");
        }
        static (int X, int Y) Extend((int X, int Y) from, (int X, int Y) to, double length)
        {
            double a = Math.Atan2(to.Y - from.Y, to.X - from.X);
            return (from.X + (int)(Math.Cos(a) * length), from.Y + (int)(Math.Sin(a) * length));
        }
        (int X, int Y) RingPoint(ClientState c, (int X, int Y) origin, int radius, double now)
        {
            for (int i = 0; i < 16; i++)
            {
                double a = Random.Shared.NextDouble() * Math.Tau;
                var at = (X: origin.X + (int)(Math.Cos(a) * radius), Y: origin.Y + (int)(Math.Sin(a) * radius));
                if (c.Layout.WalkableFor(at.X, at.Y, 2) && Distance(at, scene.Centre) < 110
                    && storms.All(s => Distance(at, s.Monster.Walk.Position(now)) > 50)) return at;
            }
            return Position(now);
        }
        async Task Maze(ClientState c, double now)
        {
            special = 2; int rings = Math.Min(3, Round + 1); double duration = 5 + (rings - 1) * 3;
            specialUntil = ready = now + duration + 1.5; mazeCentre = Position(now);
            await Voice(5677); await All(id, InstanceProtocol.ActorAnimation(id, 1.5f, 36), true);
            animations.Add(new(now + 1.5, body!.ActionGeneration, 37, (float)(duration - 1.5)));
            animations.Add(new(now + duration, body.ActionGeneration, 38, 1.5f));
            await Fx(c, mazeCentre, Art + "maze_warp.ao", now + 1);
            foreach (var p in Present.Where(p => p.PlayerLife > 0 && Distance(p.Walk.Position(now), mazeCentre) < 180).ToArray())
            {
                var at = p.Layout.Near((mazeCentre.X + 3, mazeCentre.Y + 3));
                lock (p.Monsters) p.Walk.Teleport(at.X, at.Y, now);
                var w = p.Wire; await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, p, PlayerId, InstanceProtocol.Teleport(PlayerId, at.X, at.Y), "[Sirus] meteor maze");
            }
            for (int ring = 0; ring < rings; ring++)
            {
                double radius = 17 + ring * 15, gap = .7 + ring * 2.15;
                for (double a = 0; a < Math.Tau; a += .15)
                {
                    if (Math.Abs(Math.IEEERemainder(a - gap, Math.Tau)) < .32) continue;
                    var at = (mazeCentre.X + (int)(Math.Cos(a) * radius), mazeCentre.Y + (int)(Math.Sin(a) * radius));
                    await Fx(c, at, Art + "blocking_01.ao", now + duration, clip: 2, rotation: (float)(a + Math.PI / 2), blocking: true);
                }
                var exit = (mazeCentre.X + (int)(Math.Cos(gap) * radius), mazeCentre.Y + (int)(Math.Sin(gap) * radius));
                await Fx(c, exit, Art + "maze_warp.ao", now + duration, clip: null);
            }
            await Fx(c, mazeCentre, Art + "maze_meteor_incoming.ao", now + duration, clip: null);
            hits.Add(new(now + duration, body!.ActionGeneration, ++group, rings == 1 ? 10949 : rings == 2 ? 10950 : 10951,
                mazeCentre, mazeCentre, 17 + (rings - 1) * 15));
        }
        async Task Corridor(ClientState c, double now)
        {
            special = 1; specialUntil = ready = now + 7.5;
            var aim = c.Walk.Position(now); var at = RingPoint(c, aim, 42, now);
            await Teleport(c, id, at, now); await Cast(c, id, 10948, aim, now); await Voice(5678);
            body!.LifePhases!.DamageTakenPercent = 20;
            corridorEnd = Extend(at, aim, 100);
            double angle = Math.Atan2(aim.Y - at.Y, aim.X - at.X), dx = Math.Cos(angle + Math.PI / 2) * 11, dy = Math.Sin(angle + Math.PI / 2) * 11;
            for (int d = 8; d < 100; d += 6)
                foreach (int side in new[] { -1, 1 })
                {
                    var wall = (at.X + (int)(Math.Cos(angle) * d + side * dx), at.Y + (int)(Math.Sin(angle) * d + side * dy));
                    await Fx(c, wall, Art + "blocking_02.ao", specialUntil, clip: 2, rotation: (float)(angle + Math.PI / 2), blocking: true);
                }
            foreach (double contact in new[] { 1.173, 1.825, 2.485, 3.156, 3.818, 4.497, 5.167, 5.812 })
                hits.Add(new(now + contact, body.ActionGeneration, ++group, 10948, at, corridorEnd, 7, Beam: true));
        }
        async Task QuadBeam(ClientState c, double now)
        {
            await Voice(5667); var at = Position(now); await Cast(c, id, 10973, at, now);
            double start = now + Attack(c, body!.Variety!, body.Level, 10973).Seconds;
            double until = start + Stat(c, 10973, body.Level, "base_skill_effect_duration") / 1000d;
            await Fx(c, at, Art + "beam_core.ao", until, clip: null);
            quads.Add(new(at, start, until, Random.Shared.NextDouble() * Math.Tau));
            for (int i = 0; i < 4; i++)
            {
                double a = i * Math.PI / 2;
                await Fx(c, (at.X + (int)(Math.Cos(a) * 15), at.Y + (int)(Math.Sin(a) * 15)), Root + "AtlasExile5_BeamClone.ao",
                    start, 49, (float)(a + Math.PI / 2));
            }
            ready = start + .8;
        }
        async Task Clones(ClientState c, double now)
        {
            special = 3; specialUntil = ready = now + 5.33; var aim = c.Walk.Position(now);
            int real = Random.Shared.Next(12);
            double angle = Math.Tau * real / 12;
            var at = c.Layout.Near((aim.X + (int)(Math.Cos(angle) * 35), aim.Y + (int)(Math.Sin(angle) * 35)));
            await Teleport(c, id, at, now);
            await Cast(c, id, 8100, aim, now); await Voice(5688);
            for (int i = 0; i < 12; i++)
            {
                if (i == real) continue;
                double a = Math.Tau * i / 12;
                var clone = (aim.X + (int)(Math.Cos(a) * 35), aim.Y + (int)(Math.Sin(a) * 35));
                if (!c.Layout.WalkableFor(clone.Item1, clone.Item2, 1)) continue;
                await Fx(c, clone, Root + "AtlasExile5_BeamClone.ao", specialUntil, clip: 14,
                    rotation: (float)(Math.Atan2(aim.Y - clone.Item2, aim.X - clone.Item1) + Math.PI / 2));
            }
            hits.Add(new(now + 4.7, body!.ActionGeneration, ++group, 7031, at, Extend(at, aim, 180), 6, Beam: true, Track: c));
        }
        async Task ApparitionAttack(ClientState c, double now)
        {
            if (!c.Monsters.ContainsKey(apparition)) return;
            var aim = c.Walk.Position(now); var from = Position(now);
            await Teleport(c, apparition, from, now);
            int kind = apparitionRotation++ % 3;
            int effect = kind == 0 ? 10967 : kind == 1 ? 10954 : 10956;
            await Cast(c, apparition, effect, aim, now); await Voice(kind == 0 ? (ushort)5665 : kind == 1 ? (ushort)5663 : (ushort)5664);
            if (kind == 0)
            {
                for (int i = -2; i <= 2; i++)
                {
                    var to = (aim.X + i * 12, aim.Y);
                    hits.Add(new(now + 1 + (i + 2) * .35, c.Monsters[apparition].ActionGeneration, ++group, 10967, from, Extend(from, to, 150), 9, Beam: true, ApparitionHit: true));
                }
            }
            else
                for (int i = 0; i < (kind == 1 ? 5 : 9); i++)
                {
                    var at = c.Layout.Near((aim.X + Random.Shared.Next(-24, 25), aim.Y + Random.Shared.Next(-24, 25)));
                    double contact = now + 1.4 + i * .28;
                    await Fx(c, at, Art + "maze_meteor_incoming.ao", contact, clip: null);
                    hits.Add(new(contact, c.Monsters[apparition].ActionGeneration, ++group, kind == 1 ? 7032 : 10956, at, at, kind == 1 ? 9 : 12, ApparitionHit: true));
                    patches.Add(new(at, contact + 8, 8, 10945));
                }
        }
        async Task Maintenance(ClientState c, double now)
        {
            foreach (var fx in visuals.Where(v => v.Until <= now).ToArray())
            { visuals.Remove(fx); foreach (var p in players) p.RemoveBlocker(fx.Id); await Remove(fx.Id); }
            patches.RemoveAll(p => p.Until <= now); quads.RemoveAll(q => q.Until <= now);
            if (stage is 7 or 8) return;
            foreach (var animation in animations.Where(a => a.At <= now).ToArray())
            {
                animations.Remove(animation);
                if (stage == 4 && body?.ActionGeneration == animation.Generation)
                    await All(id, InstanceProtocol.ActorAnimation(id, animation.Seconds, animation.Animation), true);
            }
            foreach (var s in storms.Where(s => s.Mobile && now > s.Born + 3 && now >= s.Next).ToArray())
            {
                var at = s.Monster.Walk.Position(now); var to = (at.X + (int)(Math.Cos(s.Angle) * 2), at.Y + (int)(Math.Sin(s.Angle) * 2));
                if (c.Layout.WalkableFor(to.Item1, to.Item2, 1)) await Teleport(c, s.Id, to, now);
                s.Next = now + .75;
            }
            foreach (var cast in deferred.Where(d => now >= d.At).ToArray())
            {
                deferred.Remove(cast);
                if (body is null || stage != 4 || body.ActionGeneration != cast.Generation) continue;
                await Cast(c, id, Round == 3 ? 8099 : 8098, cast.Aim, now);
                await Voice(Round == 3 ? (ushort)5673 : (ushort)5670);
                double[] contacts = Round == 3 ? [.841, .991, 1.098, 1.241, 1.370, 1.516] : [.841, 1.098, 1.370];
                for (int i = 0; i < contacts.Length; i++)
                    hits.Add(new(now + contacts[i], body.ActionGeneration, ++group, 7019 + (i % 4) * 3, cast.From, Extend(cast.From, cast.Aim, 180), 4,
                        Beam: true, Track: cast.Target));
            }
            foreach (var hit in hits.Where(h => h.At <= now).ToArray())
            {
                hits.Remove(hit);
                if (body is null || stage != 4 || !c.Monsters.TryGetValue(hit.ApparitionHit ? apparition : id, out var caster)
                    || hit.Generation != caster.ActionGeneration) continue;
                var aim = hit.Track is { Closed: false, LeavingArea: false } p ? Extend(hit.From, p.Walk.Position(now), 180) : hit.To;
                if (hit.Beam) await Beam(c, hit, aim, now);
                else await Fx(c, hit.To, Art + "maze_meteor_impact.ao", now + 2, clip: null);
                foreach (var player in Present.ToArray()) await Damage(player, hit with { To = aim });
            }
            // Four rotating arms continue while Sirus performs other final-phase attacks.
            foreach (var q in quads.Where(q => now >= q.From).ToArray())
            {
                if (now - lastGround < .25) continue;
                double angle = q.Angle + (now - q.From) * .32;
                for (int i = 0; i < 4; i++)
                {
                    double a = angle + i * Math.PI / 2;
                    var end = (q.At.X + (int)(Math.Cos(a) * 80), q.At.Y + (int)(Math.Sin(a) * 80));
                    q.Beams ??= new uint[4];
                    if (q.Beams[i] == 0) q.Beams[i] = await Fx(c, q.At, Art + "clone_trap_beam.ao", q.Until, clip: null, rotation: (float)(a + Math.PI / 2));
                    else await All(q.Beams[i], InstanceProtocol.Orientation(q.Beams[i], (float)(a + Math.PI / 2)));
                    foreach (var p in Present.Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)
                        && KitavaActTen.LineDistance(p.Walk.Position(now), q.At, end) < 3))
                    {
                        var e = Exposures.GetValue(p, _ => new());
                        if (e.NextBeam > now) continue;
                        int duration = Stat(c, 10973, body!.Level, "base_secondary_skill_effect_duration");
                        e.BeamStacks = e.BeamUntil > now ? Math.Min(8, e.BeamStacks + 1) : 1; e.BeamUntil = now + duration / 1000d; e.NextBeam = now + .5;
                        var w = p.Wire;
                        if (e.BeamBuff != 0) await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, p, PlayerId, InstanceEntities.RemoveBuff(PlayerId, e.BeamBuff), null);
                        e.BeamBuff = p.NextBuff++;
                        await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, p, PlayerId, InstanceEntities.AddBuff(PlayerId, e.BeamBuff, 1525, id,
                            values: [0, 0], u68: BitConverter.SingleToUInt32Bits(duration / 1000f), stacks: (ushort)e.BeamStacks), null);
                        await RefreshPlayerStats(p);
                    }
                }
            }
            foreach (var p in Present.ToArray())
                if (Exposures.TryGetValue(p, out var e) && e.BeamStacks > 0 && now >= e.BeamUntil)
                { e.BeamStacks = 0; if (e.BeamBuff != 0) { var w = p.Wire; await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, p, PlayerId, InstanceEntities.RemoveBuff(PlayerId, e.BeamBuff), null); e.BeamBuff = 0; } await RefreshPlayerStats(p); }
            if (now - lastGround < .25) return;
            double seconds = Math.Clamp(now - lastGround, 0, 1); lastGround = now;
            foreach (var p in Present.ToArray()) await GroundDamage(p, now, seconds);
        }
        async Task Damage(ClientState p, Hit hit)
        {
            uint actor = hit.ApparitionHit ? apparition : id;
            if (body is null || !p.Monsters.TryGetValue(actor, out var caster)) return;
            var w = p.Wire; MonsterHitOutcome? outcome = null;
            var attack = Attack(p, caster.Variety!, caster.Level, hit.Effect);
            bool Inside((int X, int Y) at) => hit.Beam ? KitavaActTen.LineDistance(at, hit.From, hit.To) <= hit.Radius : Distance(at, hit.To) <= hit.Radius;
            await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, p, actor, [], null, atGate: () =>
            {
                lock (p.Monsters)
                {
                    double now = Math.Max(p.Clock.Elapsed.TotalSeconds, ProjectileWorldTime(p));
                    if (stopped || !CurrentGameWork || stage != 4 || p.Closed || p.LeavingArea || p.Paused || InGracePeriod(p)) return null;
                    var ma = attack with { Damage = attack.Damage.Scale(Math.Max(0, 1 + Stat(p, hit.Effect, body.Level,
                        "monster_penalty_against_minions_damage_+%_final_vs_player_minions") / 100d)) };
                    foreach (var g in p.Golems.All.Values.Concat(p.Reservations.Fighters.Values).Concat(p.Summons.Fighters.Values).ToArray())
                        if (!g.Monster.Dead && Inside(g.Monster.Walk.Position(now)) && struck.Add((hit.Group, p, g.Id)))
                            GolemTakeHitLocked(p, g, actor, caster, ma, now, area: true);
                    if (p.PlayerLife == 0 || !Inside(p.Walk.Position(now)) || !struck.Add((hit.Group, p, PlayerId))) return null;
                    outcome = CommitMonsterHitLocked(w.Remote, p, actor, caster, attack, now, p.Walk.Position(now), hit.To, false, p.NextActionSequence());
                    return outcome?.HitPacket;
                }
            });
            if (outcome is not null) await PresentMonsterHit(w.Remote, w.Stream, w.Ciphers, p, actor, outcome);
        }
        async Task<uint> Fx(ClientState c, (int X, int Y) at, string model, double until, uint? clip = null, float rotation = 0, bool blocking = false, bool temporary = true)
        {
            var fx = new Visual(c.Instance.World.NewMonsterId(), at, model, clip, until, rotation, blocking, temporary); visuals.Add(fx);
            foreach (var p in Present.ToArray()) await CreateFx(p, fx);
            return fx.Id;
        }
        static async Task CreateFx(ClientState p, Visual fx)
        {
            if (fx.Blocking) p.SetBlocker(fx.Id, fx.At.X, fx.At.Y, 2);
            var obj = new EffectObject(fx.At.X, fx.At.Y, fx.Model) { Rotation = fx.Rotation };
            await SendWorldCreate(p, fx.Id, () => InstanceEntities.CreateObject(fx.Id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), obj.Blob()));
            await SendWorldRecord(p, fx.Id, () => InstanceProtocol.EnterWakeZone(fx.Id));
            if (fx.Clip is uint clip) await SendWorldRecord(p, fx.Id, () => InstanceProtocol.AnimationUpdate(fx.Id, 0, clip, 1));
        }
    }
}
