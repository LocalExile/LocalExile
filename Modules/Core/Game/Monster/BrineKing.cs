using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>The campaign Brine King. The map variants have separate encounters.</summary>
static class BrineKing
{
    internal const string Boss = "Metadata/Monsters/BrineKing/BrineKing";
    const string Wave = "Metadata/Effects/Spells/monsters_effects/act6/Brine_King/tidal_wave/tidal_wave";
    const string Art = "Metadata/Effects/Spells/monsters_effects/act6/Brine_King/";
    internal const int MeleeEffect = 406;
    static readonly int[] Effects = [6354, 6359, 6670, 6668, 6361, MeleeEffect, 4365, 6759, 6761, 6360, 6355];
    static readonly ConditionalWeakTable<InstanceState, Fight> Fights = new();
    static readonly ConditionalWeakTable<ClientState, Tide> Tides = new();
    // Row 856 is not IsRecovery: its GrantedStats do not travel in 0x1c1 (11-skills.md §12).
    internal static byte[] TideBuff(uint player, ushort instance) => InstanceEntities.AddBuff(player, instance, 856);
    sealed class Tide
    {
        public bool Active;
        public ushort Buff;
        public double Degen;
        public double MaimUntil;
        public bool MaimActive;
        public ushort MaimBuff;
    }
    public static Dictionary<uint, int> PlayerStats(ClientState conn, Dictionary<uint, int> sources)
    {
        if (Tides.TryGetValue(conn, out var tide) && tide.Active && Stats.Key("action_speed_-%") is uint key)
            sources[key] = sources.GetValueOrDefault(key) + 30;
        if (Tides.TryGetValue(conn, out tide) && tide.MaimUntil > conn.Clock.Elapsed.TotalSeconds && Stats.Key("movement_velocity_+%") is uint movement)
            sources[movement] = sources.GetValueOrDefault(movement) - 30;
        return sources;
    }
    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights) pair.Value.Stop();
            foreach (var pair in Tides)
            {
                lock (pair.Key.Monsters)
                {
                    pair.Key.TrapDegenPerSecond = Math.Max(0, pair.Key.TrapDegenPerSecond - pair.Value.Degen);
                    pair.Value.Degen = 0; pair.Value.Active = false;
                }
                _ = RemoveTideBuff(pair.Key, pair.Value.Buff);
                _ = RemoveTideBuff(pair.Key, pair.Value.MaimBuff);
            }
        }
    }
    static async Task RemoveTideBuff(ClientState conn, ushort buff)
    {
        if (conn.Closed || buff == 0) return;
        try { var (remote, stream, ciphers) = conn.Wire; await Program.Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, buff), null); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }
    public static async Task Leaving(ClientState conn)
    {
        await TideAt(conn, false, 0);
        if (Tides.TryGetValue(conn, out var tide))
        { tide.MaimUntil = 0; tide.MaimActive = false; await RemoveTideBuff(conn, tide.MaimBuff); }
    }
    static async Task Maim(ClientState conn)
    {
        var stats = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        if (Get("cannot_be_maimed") != 0 || Random.Shared.Next(100) < Get("base_avoid_maim_%")) return;
        var tide = Tides.GetValue(conn, _ => new());
        if (tide.MaimActive) await RemoveTideBuff(conn, tide.MaimBuff);
        tide.MaimActive = true; tide.MaimUntil = conn.Clock.Elapsed.TotalSeconds + 4; tide.MaimBuff = conn.NextBuff++;
        var wire = conn.Wire;
        await Send(wire.Remote, wire.Stream, wire.Ciphers, InstanceEntities.AddBuff(PlayerId, tide.MaimBuff, 513,
            u68: BitConverter.SingleToUInt32Bits(4)), "[Brine King] cleave maim");
        await RefreshPlayerStats(conn);
    }
    static async Task RefreshPlayerStats(ClientState conn)
    {
        lock (conn.Monsters) { conn.Pools = PoolsFor(conn, conn.Pools.Level); conn.ApplyMovementSpeed(); }
        var wire = conn.Wire;
        await SendPlayerStatSnapshot(wire.Remote, wire.Stream, wire.Ciphers, conn, () => PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)), null);
    }
    static async Task TideAt(ClientState conn, bool active, double rawDps)
    {
        var tide = Tides.GetValue(conn, _ => new());
        bool changed;
        lock (conn.Monsters)
        {
            double dps = active ? rawDps * (1 - Math.Min(MaxResistance(conn, DamageType.Cold), Resistance(conn, DamageType.Cold)) / 100.0) : 0;
            conn.TrapDegenPerSecond = Math.Max(0, conn.TrapDegenPerSecond - tide.Degen + dps);
            tide.Degen = dps;
            changed = tide.Active != active;
            tide.Active = active;
            if (!changed) return;
            if (active) tide.Buff = conn.NextBuff++;
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
        }
        var (remote, stream, ciphers) = conn.Wire;
        await Send(remote, stream, ciphers, active
            ? TideBuff(PlayerId, tide.Buff)
            : InstanceEntities.RemoveBuff(PlayerId, tide.Buff), "[Brine King] tidal wave slow/degen");
        await SendPlayerStatSnapshot(remote, stream, ciphers, conn, () => PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)), null);
    }
    public static bool IsBoss(MonsterCatalog.Variety? variety) => variety?.Id == Boss;
    static ushort Skill(int effect) => (ushort)(0x4000 + Array.IndexOf(Effects, effect));
    public static StateMachineData? StartStates(MonsterCatalog.Variety variety) =>
        IsBoss(variety) ? variety.StateMachine?.Set("rock_mode", 1).Set("tide_rush", 0) : null;

    internal static IReadOnlyList<GrantedSkill>? SkillsFor(ClientState conn, MonsterCatalog.Variety variety, byte level, MonsterAttack attack)
    {
        if (!IsBoss(variety)) return null;
        var catalog = Game.SkillsFor(conn);
        return [.. Effects.Select(effect =>
        {
            var use = Attack(conn, variety, level, effect);
            return catalog.FindByRow(Skill(effect), effect, level, use.SkillStats());
        }).OfType<GrantedSkill>()];
    }

    static MonsterAttack Attack(ClientState conn, MonsterCatalog.Variety variety, byte level, int effect)
    {
        var catalog = Game.SkillsFor(conn);
        var (min, max) = Monsters.BaseDamage(variety, level) ?? (1, 1);
        var damage = DamagePacket.Of(DamageType.Physical, min, max);
        bool spell = catalog.IsSpell(effect);
        if (spell) damage = catalog.HitDamage(effect, level);
        double seconds = effect == MeleeEffect ? variety.AttackMs / 1000.0 : Math.Max(0.5, catalog.CastTimeMs(effect) / 1000.0);
        if (effect is 6668 or 6670)
        {
            // The slam's ACT clip is slowed by its own attack-speed stat (−50% / −60%).
            seconds = effect == 6670 ? 3.1 : 2.4;
            damage = damage.Scale(2).Select((t, r) => t == DamageType.Physical ? r.Scale(0.5) : r)
                .Add(DamageType.Cold, new DamageRange(min, max));
        }
        if (effect is 6354 or 6355 or 6361) damage = DamagePacket.Empty;
        return new(Skill(effect), seconds, effect == MeleeEffect ? variety.AttackRange : 45, damage)
        { Spell = spell, Scripted = true };
    }

    public static async Task Tick(ClientState conn)
    {
        if (Tides.TryGetValue(conn, out var tide) && tide.MaimActive && (conn.PlayerLife == 0 || conn.Clock.Elapsed.TotalSeconds >= tide.MaimUntil))
        { tide.MaimActive = false; tide.MaimUntil = 0; await RemoveTideBuff(conn, tide.MaimBuff); await RefreshPlayerStats(conn); }
        if (conn.Layout.Id != "2_6_15" || !conn.PlayerSent || conn.Closed || conn.LeavingArea) return;
        (uint Id, SpawnedMonster Monster)? found;
        lock (conn.Monsters)
            found = conn.Monsters.Where(p => IsBoss(p.Value.Variety)).Select(p => ((uint, SpawnedMonster)?)new(p.Key, p.Value)).FirstOrDefault();
        if (found is not { } && !Fights.TryGetValue(conn.Instance, out _)) return;
        var fight = Fights.GetValue(conn.Instance, _ => new(found!.Value.Id, found.Value.Monster, conn));
        await fight.Gate.WaitAsync();
        try
        {
            await fight.Bind(conn);
            if (IsMonsterRunner(conn)) await fight.Step(conn, conn.Clock.Elapsed.TotalSeconds);
        }
        finally { fight.Gate.Release(); }
    }

    internal sealed class Fight
    {
        internal readonly SemaphoreSlim Gate = new(1);
        readonly uint id;
        readonly SpawnedMonster boss;
        readonly List<ClientState> players = [];
        readonly HashSet<ClientState> bound = [];
        readonly HashSet<ClientState> waveViews = [];
        readonly List<(uint Id, double Ends)> effects = [];
        readonly List<(double At, (int X, int Y) Position, double Radius, MonsterAttack Attack, int? Generation)> hits = [];
        sealed class Surf(uint id, (int X, int Y) from, (int X, int Y) to, double started, double ends)
        {
            public readonly uint Id = id;
            public readonly (int X, int Y) From = from, To = to;
            public readonly double Started = started, Ends = ends;
            public readonly HashSet<ClientState> Hit = [];
            public (double X, double Y) Position(double now)
            { double t = Math.Clamp((now - Started) / (Ends - Started), 0, 1); return (From.X + (To.X - From.X) * t, From.Y + (To.Y - From.Y) * t); }
        }
        readonly List<Surf> surfs = [];
        readonly (int X, int Y) center;
        readonly List<GlyphQuest.Placement> spawners;
        readonly double radius, speed;
        double floodAt = double.NaN, nextSlam, nextRush, rushUntil, nextMinion, nextChase, nextStorm, nextOrb, nextWaveHit, expandUntil;
        int floodStep;
        uint waveId;
        byte waveState;
        ushort? immunity;
        bool cleaned;
        bool stopped;
        LifePhaseGate Phases => boss.LifePhases!;

        public Fight(uint objectId, SpawnedMonster monster, ClientState conn)
        {
            (id, boss) = (objectId, monster);
            var placed = new QuestObjectTypes.AreaPlacements(conn.Layout.Id, conn.Seed, []);
            var middle = placed.Of("Metadata/MiscellaneousObjects/ArenaMiddle").FirstOrDefault();
            center = middle is null ? boss.Walk.Position(conn.Clock.Elapsed.TotalSeconds) : (middle.X, middle.Y);
            spawners = placed.Of("Metadata/Monsters/BrineKing/Minion/BrineKingMinionSpawner");
            radius = spawners.Count == 0 ? 67 : spawners.Average(p => Distance((p.X, p.Y), center));
            speed = boss.Walk.Speed;
            lock (conn.Monsters) boss.LifePhases ??= new([75, 50, 25]);
            lock (conn.QuestObjects)
                waveId = conn.QuestObjects.Where(p => p.Value.Rule.ObjectType == Wave).Select(p => p.Key).FirstOrDefault();
            waveState = (byte)(Phases.Active ? 3 + 4 * Phases.Completed : Phases.Completed == 1 ? 5 : Phases.Completed == 2 ? 9 : 11);
            double now = conn.Clock.Elapsed.TotalSeconds;
            nextSlam = now + 2; nextRush = now + 8; nextStorm = now + 1; nextOrb = now + 2;
            Console.WriteLine($"[gateway] Brine King controller: arena {center}, radius {radius:0.0}, {spawners.Count} minion markers");
        }

        public async Task Bind(ClientState conn)
        {
            players.RemoveAll(p => p.Closed || p.LeavingArea);
            if (!players.Contains(conn)) players.Add(conn);
            // A joining client's first tick precedes SyncInstanceMonsters. Never update an Actor it has not
            // received yet, or one outside its wake zone; defer the rebind until that client can read it.
            if (!boss.Dead && boss.Life > 0 && Awake(conn) && bound.Add(conn))
            {
                var skills = SkillsFor(conn, boss.Variety!, boss.Level, boss.Attack!)!;
                var (remote, stream, ciphers) = conn.Wire;
                lock (conn.Monsters)
                {
                    boss.ActionGeneration++; boss.SwingGeneration = null;
                    boss.States = (boss.States ?? boss.Variety!.StateMachine)?.Set("rock_mode", 1);
                    if (conn.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                        conn.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { Skills = skills, StateMachine = boss.States } };
                }
                await Send(remote, stream, ciphers, InstanceEntities.PlayerSkillsChange(id, skills,
                    boss.Attack is { } old && !skills.Any(s => s.Id == old.SkillId) ? [old.SkillId] : []), "[Brine King] full actor skill list");
                await State(conn, "rock_mode", 1);
            }
            if (waveId != 0 && waveViews.Add(conn))
            {
                bool exists;
                lock (conn.QuestObjects) exists = conn.QuestObjects.ContainsKey(waveId);
                if (exists) { var w = conn.Wire; await Send(w.Remote, w.Stream, w.Ciphers, InstanceEntities.TransitionableUpdate(waveId, 3, waveState), null); }
                else await CreateWave(conn);
            }
        }

        public async Task Step(ClientState runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            if (boss.Dead || boss.Life == 0)
            {
                if (!cleaned)
                {
                    cleaned = true;
                    if (waveId != 0) { await WaveState(12); effects.Add((waveId, now + 3)); waveId = 0; }
                    hits.Clear(); surfs.Clear();
                    foreach (var participant in players) await TideAt(participant, false, 0);
                }
                await Expire(now);
                return;
            }
            await Expire(now);
            var pos = boss.Walk.Position(now);
            ClientState? conn;
            lock (runner.Monsters)
            {
                // The runner owns the update loop, not the fight's target. A party can keep fighting while its
                // first entrant waits at the entrance or lies dead; only a client with the boss awake gets actions.
                conn = players.Where(p => !p.Closed && !p.LeavingArea && p.PlayerSent && p.PlayerLife > 0
                        && !p.Paused && !InGracePeriod(p) && Awake(p))
                    .MinBy(p => Distance(pos, p.Walk.Position(now)));
                if (conn is null) return;
                boss.Target = conn;
            }
            await LandHits(conn, now);
            var player = conn.Walk.Position(now);
            if (!boss.Aggro)
            {
                if (Distance(pos, player) > boss.AggroRange) return;
                boss.Aggro = true;
            }
            if (!boss.BossBarShown && boss.Variety is { BossHealthBar: true } && boss.States?.IndexOf("boss_life_bar") >= 0)
            {
                lock (runner.Monsters) { boss.BossBarShown = true; conn.BossBarsSeen.Add(id); }
                var wire = conn.Wire;
                await ShowBossLifeBar(wire.Remote, wire.Stream, wire.Ciphers, id, boss, conn);
            }
            bool begin;
            lock (conn.Monsters) begin = Phases.Begin(boss.Life, boss.MaxLife);
            if (begin || Phases.Active && double.IsNaN(floodAt))
            {
                Phases.DamageTakenPercent = 100; rushUntil = 0;
                boss.Walk.SetSpeed(speed, now);
                boss.ActionGeneration++; boss.SwingGeneration = null;
                var (remote, stream, ciphers) = conn.Wire;
                immunity = conn.NextBuff++;
                await All(InstanceEntities.AddBuff(id, immunity.Value, 350, id,
                    u68: BitConverter.SingleToUInt32Bits((float)(16 + Distance(pos, center) / Math.Max(1, speed)))), monster: true);
                await State(conn, "tide_rush", 0);
                double travel = Distance(pos, center) / Math.Max(1, speed);
                await Move(conn, center, now);
                floodAt = now + travel;
                floodStep = 0;
                Console.WriteLine($"[gateway] Brine King: flood {Phases.Completed + 1} at {Phases.NextPercent}% life");
            }
            if (Phases.Active)
            {
                if (now >= floodAt && floodStep == 0)
                {
                    await Cast(conn, 6361, center, now);
                    await WaveState((byte)(2 + 4 * Phases.Completed));
                    floodStep = 1;
                }
                if (now >= floodAt + 3 && floodStep == 1)
                {
                    await WaveState((byte)(3 + 4 * Phases.Completed));
                    floodStep = 2;
                }
                if (now >= floodAt + 13)
                {
                    if (Phases.Completed < 2)
                    {
                        await WaveState((byte)(4 + 4 * Phases.Completed));
                        expandUntil = now + 3;
                    }
                    if (immunity is { } buff) await All(InstanceEntities.RemoveBuff(id, buff), monster: true);
                    immunity = null;
                    lock (conn.Monsters) Phases.Finish();
                    floodAt = double.NaN;
                    nextSlam = now + 1; nextRush = now + 8;
                    Console.WriteLine($"[gateway] Brine King: flood {Phases.Completed} finished, damage unlocked");
                }
                await Hazards(conn, now);
                return;
            }
            if (expandUntil > 0 && now >= expandUntil)
            {
                await WaveState((byte)(Phases.Completed == 1 ? 5 : 9));
                expandUntil = 0;
            }
            await Hazards(conn, now);
            if (rushUntil > 0 && now >= rushUntil)
            {
                rushUntil = 0; Phases.DamageTakenPercent = 100;
                boss.Walk.SetSpeed(speed, now);
                await State(conn, "tide_rush", 0);
                await Cast(conn, 6355, pos, now);
                return;
            }
            if (now < boss.BusyUntil || now < boss.FlinchUntil) return;
            if (rushUntil > now)
            {
                if (now >= nextMinion) { nextMinion = now + 1; await Minion(conn); }
            }
            else if (Phases.Completed < 3 && now >= nextRush)
            {
                nextRush = now + 15; rushUntil = now + 5; Phases.DamageTakenPercent = 20;
                boss.Walk.SetSpeed(speed * 0.6, now);
                await State(conn, "tide_rush", 1);
                await Cast(conn, 6354, pos, now);
                return;
            }
            double distance = Distance(pos, player);
            if (rushUntil <= now && now >= nextSlam && distance <= 48)
            {
                nextSlam = now + 5;
                int effect = Phases.Completed >= 2 && Random.Shared.Next(2) == 0 ? (Random.Shared.Next(2) == 0 ? 6759 : 6761) : 6668;
                await Cast(conn, effect, player, now);
                var attack = Attack(conn, boss.Variety!, boss.Level, effect);
                hits.Add((now + attack.Seconds * 0.65, player, effect == 6668 ? 12 : 18, attack, boss.ActionGeneration));
                return;
            }
            if (distance <= boss.Variety!.AttackRange + 1)
            {
                await Cast(conn, MeleeEffect, player, now);
                var attack = Attack(conn, boss.Variety!, boss.Level, MeleeEffect);
                hits.Add((now + attack.Seconds * 0.5, player, boss.Variety.AttackRange + 2, attack, boss.ActionGeneration));
            }
            else if (now >= nextChase)
            {
                nextChase = now + 0.75;
                double fraction = Math.Clamp((distance - boss.Variety.AttackRange + 1) / distance, 0, 1);
                var target = conn.Layout.Near(((int)Math.Round(pos.X + (player.X - pos.X) * fraction), (int)Math.Round(pos.Y + (player.Y - pos.Y) * fraction)));
                await Move(conn, target, now);
            }
        }

        async Task Cast(ClientState conn, int effect, (int X, int Y) target, double now)
        {
            var attack = Attack(conn, boss.Variety!, boss.Level, effect);
            int generation;
            lock (conn.Monsters)
            {
                var p = boss.Walk.Position(now); boss.Walk.Order(p.X, p.Y, now);
                generation = ++boss.ActionGeneration;
                boss.BusyUntil = now + attack.Seconds + 0.2;
            }
            var (remote, stream, ciphers) = conn.Wire;
            await SendMonsterAction(remote, stream, ciphers, conn, id, boss, generation, () =>
            {
                var p = boss.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                return InstanceProtocol.ActionStarted(id, p.X, p.Y, target.X, target.Y, Skill(effect), conn.NextActionSequence(), 0x0408);
            }, $"[Brine King] skill {effect}, phase {Phases.Completed}");
        }

        async Task Move(ClientState conn, (int X, int Y) target, double now)
        {
            var (remote, stream, ciphers) = conn.Wire;
            int generation;
            lock (conn.Monsters) generation = ++boss.ActionGeneration;
            await SendMonsterAction(remote, stream, ciphers, conn, id, boss, generation, () =>
            {
                var here = boss.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                boss.Walk.Order(target.X, target.Y, conn.Clock.Elapsed.TotalSeconds);
                return InstanceProtocol.ActionStarted(id, here.X, here.Y, target.X, target.Y, 0x2909, conn.NextActionSequence(), 0x0408);
            }, "[Brine King] move");
        }

        async Task State(ClientState conn, string name, int value)
        {
            if (boss.States is not { } states) return;
            boss.States = states.Set(name, value);
            if (conn.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                conn.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { StateMachine = boss.States } };
            int component = boss.Variety!.ComponentsOrBase.ToList().IndexOf("StateMachine");
            if (component >= 0) await All(InstanceEntities.StateMachineUpdate(id, (byte)component, boss.States), monster: true);
        }

        async Task WaveState(byte state)
        {
            waveState = state;
            if (waveId == 0)
            {
                var conn = players[0];
                lock (conn.Monsters) waveId = conn.NextObjectId++;
                foreach (var player in players) { await CreateWave(player); waveViews.Add(player); }
            }
            else await All(InstanceEntities.TransitionableUpdate(waveId, 3, state));
        }

        async Task CreateWave(ClientState conn)
        {
            var (remote, stream, ciphers) = conn.Wire;
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(waveId, InstanceEntities.ObjectTypeHash(Wave),
                new QuestObject(center.X, center.Y, ["Positioned", "BaseEvents", "Animated", "Transitionable"], TransitionState: waveState).Blob()),
                $"[Brine King] tidal wall {waveState} at {center}");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(waveId), null);
            if (QuestObjectTypes.Find(Wave) is { } info)
                lock (conn.QuestObjects) conn.QuestObjects[waveId] = (new Quests.ObjectRule(conn.Layout.Id, Wave, null), new(Wave, "", center.X, center.Y, 0, null, 0), info);
        }

        async Task Hazards(ClientState conn, double now)
        {
            int stage = Phases.Completed + (Phases.Active && now >= floodAt ? 1 : 0);
            if (stage == 0) return;
            // The first storm includes water bolts; the second flood has neither; the last keeps lightning.
            if ((stage == 3 || stage == 1 && Phases.Active) && now >= nextStorm)
            {
                nextStorm = now + 3;
                for (int i = 0; i < 5; i++)
                {
                    double angle = Random.Shared.NextDouble() * Math.Tau;
                    double reach = radius * 0.45 * Math.Sqrt(Random.Shared.NextDouble());
                    var at = i == 0 ? conn.Walk.Position(now)
                        : ((int)Math.Round(center.X + Math.Cos(angle) * reach), (int)Math.Round(center.Y + Math.Sin(angle) * reach));
                    await Effect(conn, at, Art + "lightning_strike/buildup.ao", now + 0.9);
                    hits.Add((now + 0.9, at, 5, Attack(conn, boss.Variety!, boss.Level, 6360), null));
                }
            }
            if (stage == 1 && now >= nextOrb)
            {
                nextOrb = now + 2.4;
                var target = conn.Walk.Position(now);
                double angle = Random.Shared.NextDouble() * Math.Tau;
                var from = ((int)Math.Round(center.X + Math.Cos(angle) * radius * 0.75), (int)Math.Round(center.Y + Math.Sin(angle) * radius * 0.75));
                double distance = Math.Max(1, Distance(from, target));
                var to = ((int)Math.Round(from.Item1 + (target.X - from.Item1) / distance * radius * 1.5),
                    (int)Math.Round(from.Item2 + (target.Y - from.Item2) / distance * radius * 1.5));
                double seconds = Distance(from, to) / 24;
                uint orb = await Effect(conn, from, Art + "surf_cascade/surf_cascade.ao", now + seconds);
                await All(InstanceProtocol.MoveTo(orb, to.Item1, to.Item2, (float)seconds, InstanceProtocol.FollowTerrain));
                surfs.Add(new(orb, from, to, now, now + seconds));
            }
            foreach (var surf in surfs.ToArray())
            {
                if (now >= surf.Ends) { surfs.Remove(surf); continue; }
                var p = surf.Position(now);
                var before = surf.Position(now - 0.5);
                foreach (var participant in players.Where(p => p.PlayerLife > 0 && !surf.Hit.Contains(p)))
                {
                    var at = participant.Walk.Position(now);
                    double dx = p.X - before.X, dy = p.Y - before.Y;
                    double t = Math.Clamp(((at.X - before.X) * dx + (at.Y - before.Y) * dy) / Math.Max(1e-9, dx * dx + dy * dy), 0, 1);
                    if (Math.Pow(at.X - before.X - t * dx, 2) + Math.Pow(at.Y - before.Y - t * dy, 2) > 16) continue;
                    surf.Hit.Add(participant);
                    var damage = Monsters.BaseDamage(boss.Variety!, boss.Level) ?? (50, 70);
                    await Hit(participant, ((int)p.X, (int)p.Y), new(Skill(6359), 1, 4,
                        DamagePacket.Of(DamageType.Cold, damage.Item1, damage.Item2)) { Spell = true, Scripted = true });
                }
            }
            double open = Phases.Active ? 0.25 : stage == 1 ? 0.75 : stage == 2 ? 0.50 : 0.25;
            if (Phases.Active && now < floodAt + 3)
            {
                double previous = Phases.Completed == 0 ? 1 : Phases.Completed == 1 ? 0.75 : 0.50;
                open = previous + (0.25 - previous) * Math.Clamp((now - floodAt) / 3, 0, 1);
            }
            else if (expandUntil > now) open = 0.25 + (open - 0.25) * (1 - (expandUntil - now) / 3);
            if (now >= nextWaveHit)
            {
                nextWaveHit = now + 0.5;
                foreach (var player in players.Where(p => p.PlayerLife > 0))
                {
                    var at = player.Walk.Position(now);
                    var damage = Monsters.BaseDamage(boss.Variety!, boss.Level) ?? (100, 150);
                    await TideAt(player, Distance(at, center) > radius * open, (damage.Item1 + damage.Item2) * 0.75);
                }
            }
        }

        async Task LandHits(ClientState conn, double now)
        {
            foreach (var hit in hits.Where(h => h.At <= now).ToArray())
            {
                hits.Remove(hit);
                if (hit.Generation is int generation && (generation != boss.ActionGeneration || MonsterActionFactor(boss, now) == 0)) continue;
                string art = hit.Attack.Damage[DamageType.Lightning].Max > 0 ? "lightning_strike/lightning_strike.ao" : "surf_cascade/surf_cascade.ao";
                if (hit.Attack.Spell) await Effect(conn, hit.Position, Art + art, now + 1.5);
                foreach (var player in players.Where(p => p.PlayerLife > 0 && Distance(p.Walk.Position(now), hit.Position) <= hit.Radius))
                {
                    if (hit.Attack.SkillId == Skill(MeleeEffect) && Distance(player.Walk.Position(now), boss.Walk.Position(now)) > hit.Attack.Range + 2) continue;
                    await Hit(player, hit.Position, hit.Attack);
                }
            }
        }

        async Task Hit(ClientState conn, (int X, int Y) from, MonsterAttack attack)
        {
            var (remote, stream, ciphers) = conn.Wire;
            Game.MonsterHitOutcome? outcome = null;
            await Send(remote, stream, ciphers, [], null, atGate: () =>
            {
                lock (conn.Monsters)
                {
                    if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.Paused || conn.PlayerLife == 0 || InGracePeriod(conn) || boss.Dead) return null;
                    double now = Math.Max(ProjectileWorldTime(conn), conn.Clock.Elapsed.TotalSeconds);
                    outcome = CommitMonsterHitLocked(remote, conn, id, boss, attack, now, conn.Walk.Position(now), from, false, conn.NextActionSequence());
                    return outcome?.HitPacket;
                }
            });
            if (outcome is not null)
            {
                await PresentMonsterHit(remote, stream, ciphers, conn, id, outcome);
                if (outcome.Damage > 0 && attack.SkillId is var skill && (skill == Skill(6759) || skill == Skill(6761))) await Maim(conn);
            }
        }

        async Task Minion(ClientState conn)
        {
            var variety = Monsters.Find("Metadata/Monsters/BrineKing/BrineKingMinion_");
            if (variety is not { Supported: true }) return;
            lock (conn.Monsters) if (conn.Monsters.Values.Count(m => m.SummonerId == id && !m.Dead && m.Life > 0) >= 20) return;
            var source = spawners.Count == 0 ? null : spawners[Random.Shared.Next(spawners.Count)];
            var at = conn.Layout.Near(source is null ? center : (source.X, source.Y));
            var (remote, stream, ciphers) = conn.Wire;
            uint spawned = await SpawnMonster(remote, stream, ciphers, conn, variety, boss.Level, MonsterRarity.Normal, at.X, at.Y);
            lock (conn.Monsters) if (conn.Monsters.TryGetValue(spawned, out var minion)) minion.SummonerId = id;
        }

        async Task<uint> Effect(ClientState conn, (int X, int Y) at, string ao, double expires)
        {
            uint effect;
            lock (conn.Monsters) effect = conn.NextObjectId++;
            var obj = new EffectObject(at.X, at.Y, ao);
            await All(InstanceEntities.CreateObject(effect, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), obj.Blob()));
            await All(InstanceProtocol.EnterWakeZone(effect));
            effects.Add((effect, expires));
            return effect;
        }
        async Task Expire(double now)
        {
            foreach (var effect in effects.Where(e => e.Ends <= now).ToArray())
            {
                effects.Remove(effect);
                foreach (var player in players) lock (player.QuestObjects) player.QuestObjects.Remove(effect.Id);
                await All(InstanceProtocol.RemoveObject(effect.Id));
            }
        }
        bool Awake(ClientState conn)
        {
            lock (conn.Monsters) return conn.MonsterViews.ContainsKey(id) && !conn.AsleepMonsters.Contains(id);
        }
        async Task All(byte[] bytes, bool monster = false)
        {
            if (stopped) return;
            foreach (var conn in players.Where(p => !p.Closed && !p.LeavingArea && (!monster || Awake(p))).ToArray())
            {
                // Send encrypts in place: every recipient needs its own plaintext, including wave/effect broadcasts.
                var (remote, stream, ciphers) = conn.Wire;
                await Send(remote, stream, ciphers, [.. bytes], null,
                    still: () => !stopped && !conn.Closed && !conn.LeavingArea && (!monster || Awake(conn)));
            }
        }
        internal void Stop()
        {
            stopped = true;
            Phases.DamageTakenPercent = 100;
            if (players.FirstOrDefault() is { } conn)
                lock (conn.Monsters) boss.Walk.SetSpeed(speed, conn.Clock.Elapsed.TotalSeconds);
        }
        static double Distance((int X, int Y) a, (int X, int Y) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    }
}
