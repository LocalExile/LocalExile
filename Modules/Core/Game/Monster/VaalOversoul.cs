using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>The Act 2 encounter; the Atziri, map and Machinarium actors have different skills.</summary>
public static class VaalOversoul
{
    public const string Boss = "Metadata/Monsters/IncaShadowBoss/BossIncaShadow", Area = "1_2_14_3";
    public static readonly string AltarUsed = Quests.InstanceFlag("VaalAltarUsed"), Defeated = Quests.InstanceFlag("VaalOversoulDefeated");
    public const int Slam = 1118, Summon = 1122, Laser = 1125, Travel = 1130, Ice = 1132, Cold = 1135, Rocks = 1137, RockHit = 1139;
    static readonly int[] Effects = [Rocks, Ice, Travel, Slam, Summon, Laser, RockHit, Cold];
    const string Art = "Metadata/Effects/Spells/monsters_effects/act2/inca_boss/";
    static readonly RuleTable<InstanceState, Fight> Fights = new();
    static readonly RuleTable<ClientState, GroundExposure> Exposures = new();
    static readonly RuleTable<SkillCatalog, ConcurrentDictionary<(int Effect, int Level), IReadOnlyDictionary<uint, int>>> SkillStats = new();
    sealed class GroundExposure { public double Dps; }
    public static bool IsBoss(MonsterCatalog.Variety? v) => v?.Id == Boss;
    static ushort Skill(int effect) => (ushort)(0x4000 + Array.IndexOf(Effects, effect));
    public static int Level(SkillCatalog catalog, int effect, int monsterLevel) => catalog.MaxLevelAt(effect, monsterLevel);
    public static int Stat(ClientState conn, int effect, int level, string name)
    {
        var catalog = Game.SkillsFor(conn);
        var values = SkillStats.GetValue(catalog, _ => new()).GetOrAdd((effect, level), pair =>
            catalog.EffectStats(pair.Effect, Level(catalog, pair.Effect, pair.Level)).ToDictionary(p => p.Item1, p => p.Item2));
        return Stats.Key(name) is uint key ? values.GetValueOrDefault(key) : 0;
    }
    static MonsterAttack Attack(ClientState conn, MonsterCatalog.Variety v, byte level, int effect)
    {
        var catalog = Game.SkillsFor(conn);
        var damage = catalog.HitDamage(effect, Level(catalog, effect, level));
        if (effect == Slam)
        {
            var (min, max) = Monsters.BaseDamage(v, level) ?? (1, 1);
            damage = DamagePacket.Of(DamageType.Physical, min, max)
                .Scale(Math.Max(0, 1 + Stat(conn, effect, level, "active_skill_damage_+%_final") / 100d));
            var physical = damage[DamageType.Physical];
            damage = DamagePacket.Of(DamageType.Physical, physical.Min / 2, physical.Max / 2)
                .Add(DamageType.Fire, physical.Scale(.5));
        }
        double seconds = Math.Max(.1, catalog.CastTimeMs(effect) / 1000d);
        if (effect is Summon or Laser)
            seconds /= 1 + Stat(conn, effect, level, "active_skill_cast_speed_+%_final") / 100d;
        return new(Skill(effect), seconds, 105, effect is Travel or Ice ? DamagePacket.Empty : damage) { Spell = true, Scripted = true };
    }
    public static IReadOnlyList<GrantedSkill>? SkillsFor(ClientState conn, MonsterCatalog.Variety v, byte level)
    {
        if (!IsBoss(v)) return null;
        var catalog = Game.SkillsFor(conn);
        return [.. Effects.Select(effect =>
        {
            var stats = Attack(conn, v, level, effect).SkillStats().ToList();
            // The controller aims each of the three shots separately; the geometry must not repeat each shot again.
            if (effect == Laser && Stats.Key("skill_repeat_count") is uint repeat) stats.Add((repeat, 0));
            return catalog.FindByRow(Skill(effect), effect, Level(catalog, effect, level), Game.WithBehaviourVariation(conn, effect, stats));
        }).OfType<GrantedSkill>()];
    }
    public static void LaserShock(ClientState conn, SpawnedMonster m, MonsterAttack attack, DamagePacket taken, bool blocked, double now,
        IReadOnlyDictionary<uint, int> stats)
    {
        if (!IsBoss(m.Variety) || attack.SkillId != Skill(Laser) || blocked || conn.PlayerLife == 0 || InGracePeriod(conn)) return;
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        double magnitude = 50 * Math.Pow(taken[DamageType.Lightning].Min / Math.Max(1, conn.PlayerMaxLife), .4);
        conn.ElementalAilments.Inflict(PlayerElementalAilments.Kind.Shock, magnitude, 4, now, Get, conn.IncomingHitRandom);
    }
    public static bool CannotCrit(SpawnedMonster m, MonsterAttack attack) => IsBoss(m.Variety) && attack.SkillId == Skill(Slam);

    public static async Task Tick(ClientState conn)
    {
        if (conn.Layout.Id != Area || !conn.PlayerSent || conn.Closed || conn.LeavingArea) return;
        (uint Id, SpawnedMonster M)? found;
        lock (conn.Monsters) found = conn.Monsters.Where(p => IsBoss(p.Value.Variety)).Select(p => ((uint, SpawnedMonster)?)(p.Key, p.Value)).FirstOrDefault();
        if (found is null) return;
        var fight = Fights.GetValue(conn.Instance, _ => new(found.Value.Id, found.Value.M, conn));
        await fight.Gate.WaitAsync();
        try { await fight.Bind(conn); if (IsMonsterRunner(conn)) await fight.Step(conn, conn.Clock.Elapsed.TotalSeconds); }
        finally { fight.Gate.Release(); }
    }
    public static Task Leaving(ClientState conn) { ClearExposure(conn); return Task.CompletedTask; }
    static void ClearExposure(ClientState conn)
    {
        if (!Exposures.TryGetValue(conn, out var exposure)) return;
        lock (conn.Monsters) { conn.TrapDegenPerSecond = Math.Max(0, conn.TrapDegenPerSecond - exposure.Dps); exposure.Dps = 0; }
    }
    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights) pair.Value.Stop();
            foreach (var pair in Exposures) ClearExposure(pair.Key);
        }
    }

    sealed class Fight
    {
        public readonly SemaphoreSlim Gate = new(1);
        readonly uint id;
        readonly SpawnedMonster boss;
        readonly (int X, int Y) centre;
        readonly List<ClientState> players = [];
        readonly HashSet<ClientState> bound = [];
        readonly List<(uint Id, double Ends)> effects = [];
        readonly List<(double At, int Generation, int Kind, (int X, int Y) Aim, int Index)> events = [];
        readonly List<(double At, (int X, int Y) Aim, int Effect, double Radius, int? Generation)> hits = [];
        readonly List<(uint Id, (int X, int Y) At, double Ends)> cold = [];
        readonly Dictionary<uint, double> minionDotCarry = [];
        double readyAt, surfaceAt, nextSummon, nextGround;
        double lastTick;
        int stance = 1, pending, laserLeft;
        bool rising, burrowing, stopped, dead, exitOpened;
        (int X, int Y) destination;
        public Fight(uint objectId, SpawnedMonster monster, ClientState conn)
        {
            (id, boss) = (objectId, monster);
            centre = boss.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            rising = boss.EntranceGrace;
            readyAt = rising ? double.NaN : conn.Clock.Elapsed.TotalSeconds;
            nextSummon = readyAt;
            lastTick = conn.Clock.Elapsed.TotalSeconds;
        }
        bool Awake(ClientState c) { lock (c.Monsters) return c.MonsterViews.ContainsKey(id) && !c.AsleepMonsters.Contains(id); }
        public async Task Bind(ClientState conn)
        {
            players.RemoveAll(p => p.Closed || p.LeavingArea);
            bound.RemoveWhere(p => p.Closed || p.LeavingArea);
            if (!players.Contains(conn)) players.Add(conn);
            if (boss.Dead || !Awake(conn) || !bound.Add(conn)) return;
            var wire = conn.Wire;
            await Send(wire.Remote, wire.Stream, wire.Ciphers, InstanceEntities.PlayerSkillsChange(id, SkillsFor(conn, boss.Variety!, boss.Level)!,
                boss.Attack is { } attack && !Effects.Any(e => Skill(e) == attack.SkillId) ? [attack.SkillId] : []), "[Vaal Oversoul] native skill list");
            if (!rising) await Send(wire.Remote, wire.Stream, wire.Ciphers,
                InstanceEntities.TransitionableUpdate(id, TransitionIndex(), 2), "[Vaal Oversoul] targetable actor");
            if (!burrowing) await Send(wire.Remote, wire.Stream, wire.Ciphers, ModelScale(), "[Vaal Oversoul] model scale");
        }
        public async Task Step(ClientState runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            if (!rising && !burrowing && boss.BusyUntil > lastTick)
            {
                double shift = Math.Max(0, now - lastTick) * (1 - MonsterActionFactor(boss, now));
                boss.BusyUntil += shift;
                for (int i = 0; i < events.Count; i++)
                    if (events[i].Generation == boss.ActionGeneration) events[i] = events[i] with { At = events[i].At + shift };
                for (int i = 0; i < hits.Count; i++)
                    if (hits[i].Generation == boss.ActionGeneration) hits[i] = hits[i] with { At = hits[i].At + shift };
            }
            lastTick = now;
            await Expire(now);
            if (boss.Dead || boss.Life == 0)
            {
                if (!dead)
                {
                    dead = true; events.Clear(); hits.Clear(); cold.Clear();
                    readyAt = now + 20; // All three native death clips last 20 seconds.
                    foreach (var p in players) ClearExposure(p);
                    foreach (var fx in effects.ToArray()) await All(InstanceProtocol.RemoveObject(fx.Id));
                    effects.Clear();
                }
                if (!exitOpened && now >= readyAt)
                {
                    exitOpened = true;
                    runner.Instance.SetState(Defeated);
                    foreach (var p in players.Where(p => !p.Closed && !p.LeavingArea))
                    {
                        ClearExposure(p);
                        var w = p.Wire;
                        await UpdateQuestObjectStates(w.Remote, w.Stream, w.Ciphers, p, [Defeated]);
                        await UpdateShownTransitions(w.Remote, w.Stream, w.Ciphers, p, "Vaal Oversoul defeated in this instance");
                    }
                    Console.WriteLine("[gateway] Vaal Oversoul: defeated; arena exit opened");
                }
                return;
            }
            if (rising && double.IsNaN(readyAt))
            {
                await Basic(runner, 0x0101, now, 27);
                readyAt = now + 27;
                Console.WriteLine("[gateway] Vaal Oversoul: 27 s awakening");
            }
            await Events(runner, now);
            await LandHits(runner, now);
            await Ground(now);
            if (rising)
            {
                if (now < readyAt) return;
                rising = false; boss.EntranceGrace = false; nextSummon = now;
                await All(ModelScale(), true);
                UpdateOriginTransition(runner, 2);
                await All(InstanceEntities.TransitionableUpdate(id, TransitionIndex(), 2), true);
                Console.WriteLine("[gateway] Vaal Oversoul: awakened, damage enabled");
            }
            if (burrowing)
            {
                if (now < surfaceAt) return;
                burrowing = false;
                lock (runner.Monsters) boss.Walk.Teleport(destination.X, destination.Y, now);
                await All(InstanceProtocol.Teleport(id, destination.X, destination.Y), true);
                await All(InstanceProtocol.AnimationUpdate(id, 0, 20, 1), true);
                await All(ModelScale(), true);
                readyAt = now + .8;
                Console.WriteLine($"[gateway] Vaal Oversoul: re-emerged at {destination}");
            }
            if (now < readyAt || now < boss.BusyUntil || now < boss.FlinchUntil || MonsterActionFactor(boss, now) == 0) return;
            boss.EntranceGrace = false;
            var pos = boss.Walk.Position(now);
            var target = players.Where(p => !p.Closed && !p.LeavingArea && p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p) && Awake(p))
                .MinBy(p => Distance(pos, p.Walk.Position(now)));
            if (target is null || Distance(pos, target.Walk.Position(now)) > 105) return;
            boss.Target = target; boss.Aggro = true;
            if (!boss.BossBarShown)
            {
                boss.BossBarShown = true; var w = target.Wire;
                await ShowBossLifeBar(w.Remote, w.Stream, w.Ciphers, id, boss, target);
            }
            if (pending != 0) { int effect = pending; pending = 0; if (effect == Laser) laserLeft = 2; await Cast(target, effect, now); return; }
            if (laserLeft > 0) { laserLeft--; await Cast(target, Laser, now); return; }
            int minions;
            lock (runner.Monsters) minions = runner.Monsters.Values.Count(m => m.SummonerId == id && !m.Dead && m.Life > 0);
            int selected;
            if (now >= nextSummon && minions < 8) selected = Summon;
            else
            {
                int roll = Random.Shared.Next(100);
                selected = Distance(pos, target.Walk.Position(now)) < 20 && roll < 40 ? Slam : roll < 55 ? Laser : roll < 78 ? Ice : Rocks;
            }
            int required = selected == Summon ? 1 : selected is Slam or Rocks ? 2 : 3;
            if (stance != required)
            {
                stance = required; pending = selected;
                await Basic(target, (ushort)(required == 1 ? 5831 : required == 2 ? 5828 : 5829), now, 1.133);
                return;
            }
            if (selected == Laser) laserLeft = 2;
            await Cast(target, selected, now);
        }
        async Task Basic(ClientState c, ushort skill, double now, double seconds)
        {
            int generation;
            lock (c.Monsters) { generation = ++boss.ActionGeneration; boss.SwingGeneration = null; boss.BusyUntil = now + seconds; }
            var w = c.Wire;
            await SendMonsterAction(w.Remote, w.Stream, w.Ciphers, c, id, boss, generation, () =>
            { var p = boss.Walk.Position(c.Clock.Elapsed.TotalSeconds); return [.. InstanceProtocol.ActionStarted(id, p.X, p.Y, p.X, p.Y, skill, c.NextActionSequence(), 0x0408), .. InstanceProtocol.ActionEnded(id)]; },
                $"[Vaal Oversoul] basic 0x{skill:x4}, stance {stance}");
        }
        async Task Cast(ClientState c, int effect, double now, (int X, int Y)? overrideAim = null)
        {
            var attack = Attack(c, boss.Variety!, boss.Level, effect);
            var pos = boss.Walk.Position(now); var aim = overrideAim ?? c.Walk.Position(now);
            int generation;
            lock (c.Monsters) { generation = ++boss.ActionGeneration; boss.BusyUntil = now + attack.Seconds + .2; boss.SwingGeneration = null; }
            var w = c.Wire;
            await SendMonsterAction(w.Remote, w.Stream, w.Ciphers, c, id, boss, generation, () =>
                [.. InstanceProtocol.ActionStarted(id, pos.X, pos.Y, aim.X, aim.Y, Skill(effect), c.NextActionSequence(), 0x0408), .. InstanceProtocol.ActionEnded(id)],
                $"[Vaal Oversoul] {Game.SkillsFor(c).EffectId(effect)}, stance {stance}");
            if (effect != Travel) await All(ModelScale(), true);
            if (effect == Laser)
                events.Add((now + attack.Seconds * .499 / 2.666, generation, Laser, aim, 0));
            else if (effect == Slam)
            {
                double d = Math.Max(1, Distance(pos, aim));
                var hit = ((int)Math.Round(pos.X + (aim.X - pos.X) / d * 8), (int)Math.Round(pos.Y + (aim.Y - pos.Y) / d * 8));
                hits.Add((now + attack.Seconds * 1.699 / 2.666, hit, Slam, 12, generation));
            }
            else if (effect is Summon or Ice)
            {
                double[] contacts = effect == Summon ? [.633,1.633,2.599,3.533,4.599,5.699,6.766,7.666] : [.466,.966,1.566,2.099,2.733,3.233,3.832,4.466];
                for (int i = 0; i < contacts.Length; i++) events.Add((now + contacts[i] / (effect == Summon ? 9.333 : 6.666) * attack.Seconds, generation, effect, aim, i));
                if (effect == Summon) { nextSummon = now + 20; boss.BusyUntil = now + attack.Seconds + .5; events.Add((now + attack.Seconds + .25, generation, Travel, aim, 0)); }
            }
            else if (effect == Rocks)
            {
                events.Add((now + .3, generation, Rocks, aim, 0));
            }
        }
        async Task Events(ClientState c, double now)
        {
            foreach (var ev in events.Where(e => e.At <= now).ToArray())
            {
                events.Remove(ev);
                if (ev.Generation >= 0 && (ev.Generation != boss.ActionGeneration || MonsterActionFactor(boss, now) == 0)) continue;
                var pos = boss.Walk.Position(now);
                if (ev.Kind == Laser)
                {
                    // The native laser geometry draws the beam. Its hit tests the captured line, not the later cursor.
                    double distance = Math.Max(1, Distance(pos, ev.Aim));
                    var end = ((int)Math.Round(pos.X + (ev.Aim.X - pos.X) / distance * 105),
                        (int)Math.Round(pos.Y + (ev.Aim.Y - pos.Y) / distance * 105));
                    foreach (var p in players.Where(p => !p.Closed && !p.LeavingArea))
                        await Hit(p, Attack(c, boss.Variety!, boss.Level, Laser), pos, end, 3, line: true);
                }
                else if (ev.Kind == Rocks)
                {
                    double duration = Math.Max(1, Stat(c, Rocks, boss.Level, "base_skill_effect_duration") / 1000d);
                    for (int i = 0; i < duration / .2; i++) events.Add((now + i * .2, -1, RockHit, ev.Aim, i));
                }
                else if (ev.Kind == RockHit)
                {
                    double angle = Random.Shared.NextDouble() * Math.Tau, range = 42 * Math.Sqrt(Random.Shared.NextDouble());
                    var at = ((int)Math.Round(centre.X + Math.Cos(angle) * range), (int)Math.Round(centre.Y + Math.Sin(angle) * range));
                    await Effect(c, at, Art + $"rock_fall/rockdrop_0{1 + ev.Index % 4}.ao", now + 2, 0);
                    hits.Add((now + 1.26, at, RockHit, Math.Max(1, Stat(c, RockHit, boss.Level, "active_skill_base_area_of_effect_radius")), null));
                }
                else if (ev.Kind is Summon or Ice)
                {
                    double angle = Math.Atan2(ev.Aim.Y - pos.Y, ev.Aim.X - pos.X) + (ev.Index - 3.5) * .20;
                    var at = Floor(c, ((int)Math.Round(pos.X + Math.Cos(angle) * 24), (int)Math.Round(pos.Y + Math.Sin(angle) * 24)));
                    double flight = Distance(pos, at) / (750 / WorldUnits.PerCell);
                    uint fx = await Effect(c, pos, Art + (ev.Kind == Summon ? "summon_monster_proj.ao" : "iceshoot.ao"), now + flight, ev.Kind == Summon ? 0u : 1u);
                    await All(InstanceProtocol.MoveTo(fx, at.X, at.Y, (float)flight, InstanceProtocol.FollowTerrain));
                    events.Add((now + flight, -1, ev.Kind == Summon ? -Summon : -Ice, at, ev.Index));
                }
                else if (ev.Kind == -Summon)
                {
                    await Effect(c, ev.Aim, Art + "summon_monster_proj.ao", now + 2, 1);
                    int living; lock (c.Monsters) living = c.Monsters.Values.Count(m => m.SummonerId == id && !m.Dead && m.Life > 0);
                    if (living >= 8) continue;
                    string kind = ev.Index % 2 == 0 ? "Metadata/Monsters/incaminion/ShadowBossMinion" : "Metadata/Monsters/Skeletons/SkeletonMeleeKnightElementalSummoned";
                    if (Monsters.Find(kind) is not { Supported: true } v) continue;
                    var w = c.Wire;
                    await SpawnMonster(w.Remote, w.Stream, w.Ciphers, c, v, boss.Level, MonsterRarity.Normal, ev.Aim.X, ev.Aim.Y,
                        owned: (_, m) => m.SummonerId = id);
                }
                else if (ev.Kind == -Ice)
                {
                    await Effect(c, ev.Aim, Art + "iceshoot.ao", now + 2, 0);
                    double seconds = Math.Max(1, Stat(c, Cold, boss.Level, "base_skill_effect_duration") / 1000d);
                    uint fx = await Effect(c, ev.Aim, "Metadata/Effects/Spells/cold_snap_02/grd_fx.ao", now + seconds, 0, .5f);
                    cold.Add((fx, ev.Aim, now + seconds));
                }
                else if (ev.Kind == Travel)
                {
                    if (stance != 1) { stance = 1; await Basic(c, 5831, now, 1.133); readyAt = now + 1.25; events.Add((now + 1.2, boss.ActionGeneration, Travel, ev.Aim, 0)); continue; }
                    boss.EntranceGrace = true; burrowing = true;
                    double angle = Random.Shared.NextDouble() * Math.Tau;
                    destination = Floor(c, ((int)Math.Round(centre.X + Math.Cos(angle) * 18), (int)Math.Round(centre.Y + Math.Sin(angle) * 18)));
                    surfaceAt = now + Math.Max(1, Stat(c, Travel, boss.Level, "base_skill_effect_duration") / 1000d);
                    await Cast(c, Travel, now, destination);
                    boss.BusyUntil = surfaceAt; readyAt = surfaceAt;
                    Console.WriteLine($"[gateway] Vaal Oversoul: submerged until {surfaceAt:0.000}, destination {destination}");
                }
            }
        }
        async Task LandHits(ClientState c, double now)
        {
            foreach (var hit in hits.Where(h => h.At <= now).ToArray())
            {
                hits.Remove(hit);
                if (hit.Generation is int generation && (generation != boss.ActionGeneration || MonsterActionFactor(boss, now) == 0)) continue;
                foreach (var p in players.Where(p => !p.Closed && !p.LeavingArea))
                    await Hit(p, Attack(c, boss.Variety!, boss.Level, hit.Effect), hit.Aim, hit.Aim, hit.Radius);
            }
        }
        async Task Hit(ClientState p, MonsterAttack attack, (int X, int Y) from, (int X, int Y) to, double radius, bool line = false)
        {
            var w = p.Wire; Game.MonsterHitOutcome? outcome = null;
            bool Contains((int X, int Y) at) => line ? LineDistance(at, from, to) <= radius : Distance(at, to) <= radius;
            await Send(w.Remote, w.Stream, w.Ciphers, [], null, atGate: () =>
            {
                lock (p.Monsters)
                {
                    if (!CurrentGameWork || stopped || boss.Dead || p.Closed || p.LeavingArea || p.Paused || InGracePeriod(p)) return null;
                    double now = Math.Max(ProjectileWorldTime(p), p.Clock.Elapsed.TotalSeconds);
                    int effect = Effects[attack.SkillId - 0x4000];
                    var minionAttack = attack with { Damage = attack.Damage.Scale(Math.Max(0,
                        1 + Stat(p, effect, boss.Level, "monster_penalty_against_minions_damage_+%_final_vs_player_minions") / 100d)) };
                    foreach (var minion in p.Golems.All.Values.Concat(p.Reservations.Fighters.Values).ToArray())
                        if (!minion.Monster.Dead && Contains(minion.Monster.Walk.Position(now))) GolemTakeHitLocked(p, minion, id, boss, minionAttack, now, area: true);
                    if (p.PlayerLife == 0 || !Contains(p.Walk.Position(now))) return null;
                    outcome = CommitMonsterHitLocked(w.Remote, p, id, boss, attack, now, p.Walk.Position(now), from, false, p.NextActionSequence());
                    return outcome?.HitPacket;
                }
            });
            if (outcome is not null) await PresentMonsterHit(w.Remote, w.Stream, w.Ciphers, p, id, outcome);
        }
        async Task Ground(double now)
        {
            cold.RemoveAll(p => p.Ends <= now);
            if (now < nextGround) return;
            nextGround = now + .25;
            foreach (var p in players)
            {
                if (p.Closed || p.LeavingArea || p.PlayerLife == 0) { ClearExposure(p); continue; }
                lock (p.Monsters)
                {
                    bool inside = !InGracePeriod(p) && cold.Any(g => Distance(g.At, p.Walk.Position(now)) <= 7.5);
                    var exposure = Exposures.GetValue(p, _ => new());
                    double dps = inside && !PlayerImmunities(p).Contains(DamageType.Cold)
                        ? Stat(p, Cold, boss.Level, "base_cold_damage_to_deal_per_minute") / 60d * (1 - Math.Min(MaxResistance(p, DamageType.Cold), Resistance(p, DamageType.Cold)) / 100d) : 0;
                    p.TrapDegenPerSecond = Math.Max(0, p.TrapDegenPerSecond - exposure.Dps + dps); exposure.Dps = dps;
                    if (inside)
                    {
                        var stats = CharacterPlayerStats(p);
                        int Get(string n) => Stats.Key(n) is uint k ? stats.GetValueOrDefault(k) : 0;
                        double before = p.ElementalAilments.ActionFactor(now);
                        p.ElementalAilments.Inflict(PlayerElementalAilments.Kind.Chill, 30, .5, now, Get, p.IncomingHitRandom);
                        PlayerAilmentsChangedLocked(p, before, now);
                    }
                    VaalGroundMinionsLocked(p, now, Stat(p, Cold, boss.Level, "base_cold_damage_to_deal_per_minute") / 60d,
                        at => cold.Any(patch => Distance(patch.At, at) <= 7.5), minionDotCarry);
                }
            }
            await Task.CompletedTask;
        }
        static (int X, int Y) Floor(ClientState c, (int X, int Y) at) => c.Layout.Walkable is { } walk && !walk(at.X, at.Y) ? c.Layout.Near(at) : at;
        async Task<uint> Effect(ClientState c, (int X, int Y) at, string art, double expires, uint animation, float scale = 1)
        {
            uint fx; lock (c.Monsters) fx = c.NextObjectId++;
            await All(InstanceEntities.CreateObject(fx, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), new EffectObject(at.X, at.Y, art) { Scale = scale }.Blob()));
            await All(InstanceProtocol.EnterWakeZone(fx));
            await All(InstanceProtocol.AnimationUpdate(fx, 0, animation, 1));
            effects.Add((fx, expires)); return fx;
        }
        async Task Expire(double now)
        {
            foreach (var fx in effects.Where(f => f.Ends <= now).ToArray()) { effects.Remove(fx); await All(InstanceProtocol.RemoveObject(fx.Id)); }
        }
        async Task All(byte[] message, bool monster = false)
        {
            foreach (var p in players.Where(p => !p.Closed && !p.LeavingArea && (!monster || Awake(p))).ToArray())
            { var w = p.Wire; await Send(w.Remote, w.Stream, w.Ciphers, [.. message], null, still: () => !stopped && !p.Closed && !p.LeavingArea && (!monster || Awake(p))); }
        }
        byte TransitionIndex() => (byte)boss.Variety!.ComponentsOrBase.ToList().IndexOf("Transitionable");
        // The travel action squashes x/y to zero underground; restoring only its clip leaves an invisible actor.
        byte[] ModelScale() => InstanceEntities.ComponentUpdate(id, 0,
            new BlobWriter().U16(0x20).F32(1).F32(1).F32(1).ToArray());
        void UpdateOriginTransition(ClientState c, byte state)
        {
            lock (c.Monsters) if (c.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                c.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { TransitionState = state } };
        }
        public void Stop()
        {
            stopped = true; boss.EntranceGrace = false;
            foreach (var p in players) ClearExposure(p);
            foreach (var fx in effects)
                foreach (var p in players.Where(p => !p.Closed && !p.LeavingArea))
                { var w = p.Wire; _ = Send(w.Remote, w.Stream, w.Ciphers, InstanceProtocol.RemoveObject(fx.Id), null); }
        }
        static double Distance((int X, int Y) a, (int X, int Y) b) => Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2));
        static double LineDistance((int X, int Y) p, (int X, int Y) a, (int X, int Y) b)
        {
            double dx=b.X-a.X,dy=b.Y-a.Y;
            double t=Math.Clamp(((p.X-a.X)*dx+(p.Y-a.Y)*dy)/Math.Max(1,dx*dx+dy*dy),0,1);
            return Math.Sqrt(Math.Pow(p.X-a.X-t*dx,2)+Math.Pow(p.Y-a.Y-t*dy,2));
        }
    }
}

public static partial class Game
{
    public static void VaalGroundMinionsLocked(ClientState conn, double now, double rawDps,
        Func<(int X, int Y), bool> inside, Dictionary<uint, double> carry)
    {
        foreach (var g in conn.Golems.All.Values.Concat(conn.Reservations.Fighters.Values).ToArray())
        {
            var m = g.Monster;
            if (!GolemCurrent(conn, g) || m.Dead || m.Life == 0 || g.Definition.Kind is "Agony" or "SkitterCold" or "SkitterLightning"
                || !inside(m.Walk.Position(now))) continue;
            int Player(string n) => GolemStat(g.Player, n);
            if (g.Definition.IsGolem && (Player("golem_immunity_to_elemental_damage") != 0
                || g.Definition.Kind is "Flame" or "Ice" or "Lightning" && Player("elemental_golem_immunity_to_elemental_damage") != 0)) continue;
            var res = MonsterHitResistance(m, DamageType.Cold, default);
            double damage = rawDps * .25 * Math.Max(0, 1 - Math.Min(res.Value, res.Maximum) / 100d) + carry.GetValueOrDefault(g.Id);
            int amount = (int)Math.Floor(damage); carry[g.Id] = damage - amount;
            double absorbed = Math.Min(g.Shield, amount); g.Shield -= absorbed; amount -= (int)absorbed;
            m.Life = (uint)Math.Max(0, (long)m.Life - amount);
            m.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Chill, .5, now, _ => 0, n => m.Effects[n], stated: 30);
            conn.Minions.OutputQueue.Add(new([], "Vaal chilled ground damages minion", g.Id, m, PlayerMinions.OutputKind.Life));
            if (m.Life == 0) GolemDieLocked(conn, g, now);
        }
    }
}
