using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;
using static ArenaHazards;

internal sealed class YugulHazards(uint bossId, SpawnedMonster boss, Player owner)
{
    const string Root = "Metadata/Monsters/Frog/FrogGod/";
    const ushort TurretSkill = 0x4ff0;
    internal const double PoolRadius = 12, FearRadius = 8;
    readonly List<Player> players = [];
    readonly Dictionary<Player, Exposure> exposure = [];
    readonly HashSet<uint> owned = [];
    readonly HashSet<ProjectileSession> flights = [];
    readonly List<uint> turrets = [], orbs = [];
    readonly Dictionary<uint, double> nextTurret = [];
    readonly Dictionary<uint, double> channelEnds = [];
    readonly Dictionary<uint, double> orbReady = [];
    readonly List<(uint Id, string Type, QuestObject Object)> scene = [];
    List<GlyphQuest.Placement>? pools;
    bool stopped, initialized;
    double nextMove, nextGround;
    IEnumerable<Player> Present => players.Where(p => !p.Closed && !p.LeavingArea && ReferenceEquals(p.Instance, owner.Instance));
    bool Current => !stopped && CurrentGameWork && !boss.Dead && boss.Life > 0;
    internal async Task Bind(Player c)
    {
        if (!players.Contains(c)) players.Add(c);
        foreach (var s in scene)
        {
            lock (c.Monsters) if (c.SceneViews.Contains(s.Id)) continue;
            if (await SendWorldCreate(c, s.Id, () => InstanceEntities.CreateObject(s.Id,
                InstanceEntities.ObjectTypeHash(s.Type), s.Object.Blob()), "[Yugul] native statue", wake: true))
                lock (c.Monsters) c.SceneViews.Add(s.Id);
        }
    }
    async Task All(uint id, byte[] bytes, string label)
    {
        foreach (var p in Present.ToArray())
        { await SendWorldRecord(p, id, [.. bytes], label); }
    }
    async Task<uint> Spawn(Player c, string type, (int X, int Y) at, IReadOnlyList<GrantedSkill>? skills = null)
    {
        uint id = await SpawnMonster(c, Monsters.Find(type)!, boss.Level,
            MonsterRarity.Normal, at.X, at.Y, unrisen: false, scripted: true, ownedSkills: skills,
            ownedMaximumLife: 1, owned: (id, m) => { m.SummonerId = bossId; m.NoRewards = true; m.EntranceGrace = true; owned.Add(id); },
            ownedCurrent: () => Current);
        foreach (var p in Present.Where(p => !ReferenceEquals(p, c)).ToArray())
            await SyncInstanceMonsters(p);
        return id;
    }
    async Task Initialize(Player c)
    {
        pools = new QuestObjectTypes.AreaPlacements(Yugul.Area, c.Seed, []).Of(Root + "SilverPool");
        var placements = new QuestObjectTypes.AreaPlacements(Yugul.Area, c.Seed, []);
        foreach (var point in placements.Of(Root + "SilverStatue"))
        {
            var info = QuestObjectTypes.Find(point.Type);
            if (info is null || !QuestObject.CanSerialize(info.Components, false)) continue;
            scene.Add((c.Instance.World.TakeObjectId(), point.Type,
                new(point.X, point.Y, info.Components, point.Rotation, point.Z, point.Model, 3)));
        }
        foreach (var p in Present.ToArray()) await Bind(p);
        // Existing owned actors are retired on module unload; no controller adopts unrelated summons.
        initialized = true;
    }
    internal async Task Step(Player c, double now)
    {
        if (stopped || c.Paused) return;
        if (!Current) { await Clear(); return; }
        if (!boss.Aggro || !Present.Any(p => p.PlayerLife > 0 && p.MonsterViews.ContainsKey(bossId) && !p.AsleepMonsters.Contains(bossId)) && !boss.WorldHidden) return;
        if (!initialized) await Initialize(c);
        int phase = boss.LifePhases?.Completed ?? 0;
        // TriggerSpawnersYugul1/2/3 add 1, 1 and 2 native turrets cumulatively.
        int poolCount = Enumerable.Range(0, phase + 1).Sum(n => Stat(c, 2976 + n, boss.Level, Stat.TriggerSpawnersMaxCount));
        var placements = new QuestObjectTypes.AreaPlacements(Yugul.Area, c.Seed, []).Of(Root + "SilverPoolTurret");
        while (turrets.Count < Math.Min(poolCount, placements.Count))
        {
            var point = placements[turrets.Count]; var v = Monsters.Find(Root + "SilverPoolTurret")!;
            var cat = SkillsFor(c); int rank = cat.MaxLevelAt(6582, boss.Level);
            var attack = BossKits.AttackFor(c, v, boss.Level, 6582, TurretSkill);
            var skill = cat.FindByRow(TurretSkill, 6582, rank, MonsterActionSkillStats(c, 6582, rank, attack));
            uint id = await Spawn(c, v.Id, (point.X, point.Y), skill is null ? [] : [skill]);
            if (id == 0) return;
            turrets.Add(id); nextTurret[id] = now + .8;
            await All(id, InstanceProtocol.AnimationUpdate(id, 0, 2, 1), "[Yugul] turret emerges");
        }
        int wanted = boss.Life < boss.MaxLife ? phase + 1 : 0;
        while (orbs.Count < wanted)
        {
            var at = placements.Count > 0 ? placements[orbs.Count % placements.Count] : null;
            var pos = at is null ? boss.Walk.Position(now) : (at.X, at.Y);
            uint id = await Spawn(c, Root + "SilverOrb", pos);
            if (id == 0) return;
            orbs.Add(id); orbReady[id] = now + 1.3; // FearOrb.amd emerge.
            await All(id, InstanceProtocol.AnimationUpdate(id, 0, 2, 1), "[Yugul] fear orb emerges");
        }
        foreach (uint id in turrets.ToArray())
        {
            if (channelEnds.TryGetValue(id, out var channelEnd) && now >= channelEnd)
            {
                channelEnds.Remove(id);
                await All(id, InstanceProtocol.AnimationUpdate(id, 0, 1, 1), "[Yugul] turret channel ends");
            }
            if (now < nextTurret[id] || !c.Monsters.TryGetValue(id, out var m)) continue;
            double interval = .35 / .6; // Native YugulStormBurst: 350 ms, 40% reduced cast speed.
            double end = now + interval * 6;
            channelEnds[id] = end;
            nextTurret[id] = end + Stat(c, 4858, boss.Level, Stat.BaseSkillEffectDuration) / 1000d;
            await All(id, InstanceProtocol.AnimationUpdate(id, 0, 0, (float)(.166 / interval)), "[Yugul] turret channel");
            var attack = BossKits.AttackFor(c, m.Variety!, boss.Level, 6582, TurretSkill) with { Spell = true, Scripted = true,
                Ailments = IncomingAilmentSettings(name => Stat(c, 6582, boss.Level, name)) };
            var projectile = new SkillCatalog.MonsterProjectileInfo("Metadata/Projectiles/YugulChromeProjectile", 400, false, 2); // Native speed 1000, 60% reduced.
            var target = Present.Where(p => p.PlayerLife > 0).MinBy(p => Distance(m.Walk.Position(now), p.Walk.Position(now)));
            if (target is null) continue;
            var aim = target.Walk.Position(now);
            foreach (var p in Present.ToArray())
            {
                var session = ProjectilesFor(p); flights.Add(session); session.Advance();
                for (int i = 0; i < 6; i++)
                {
                    double release = now + interval * (i + .066 / .166);
                    session.Schedule(ProjectileRuntime.Time(release), () => Current && !m.Dead, () =>
                    {
                        var use = MonsterProjectileUse(session, id, m, attack with { Projectile = projectile }, projectile, m.ActionGeneration, orderedAim: aim);
                        return use with { Payload = ((HostileProjectileOffense)use.Payload!) with { MinionDamageFactor = Math.Max(0,
                            1 + Stat(p, 6582, boss.Level, Stat.MonsterPenaltyAgainstMinionsDamagePlusPctFinalVsPlayerMinions) / 100d) },
                            Profile = use.Profile with { Pierce = int.MaxValue, Range = 150,
                            Lifetime = ProjectileRuntime.Time(Math.Max(.01, end - release)),
                            EndArea = new("Yugul burst", 10.8, Multiplier: 2),
                            EndAreaReasons = new HashSet<ProjectileEndReason> { ProjectileEndReason.Lifetime, ProjectileEndReason.Terrain, ProjectileEndReason.Distance } } };
                    });
                }
            }
        }
        if (now >= nextMove)
        {
            nextMove = now + .25;
            foreach (uint id in orbs)
            {
                if (!c.Monsters.TryGetValue(id, out var m)) continue;
                if (orbReady.TryGetValue(id, out var ready))
                {
                    if (now < ready) continue;
                    orbReady.Remove(id);
                    await All(id, InstanceProtocol.AnimationUpdate(id, 0, 7, 1), "[Yugul] fear orb chases");
                }
                var from = m.Walk.Position(now);
                var target = Present.Where(p => p.PlayerLife > 0).MinBy(p => Distance(from, p.Walk.Position(now)));
                if (target is null) continue;
                double speed = 12 * (phase == 2 ? 1.5 : 1);
                var to = Toward(from, target.Walk.Position(now), speed * .5);
                lock (c.Monsters) { m.Walk.SetSpeed(speed, now); m.Walk.Order(to.X, to.Y, now); }
                await All(id, InstanceProtocol.MoveTo(id, to.X, to.Y, (float)speed, InstanceProtocol.FollowTerrain), "[Yugul] fear orb follows");
            }
        }
        if (now < nextGround) return;
        nextGround = now + .25;
        foreach (var p in Present.ToArray())
        {
            if (!exposure.TryGetValue(p, out var e)) exposure[p] = e = new();
            bool InPool((int X, int Y) at) => pools!.Any(point => Distance(at, (point.X, point.Y)) <= PoolRadius);
            int Fears((int X, int Y) at) => orbs.Count(id => c.Monsters.TryGetValue(id, out var m) && !m.Dead && Distance(at, m.Walk.Position(now)) <= FearRadius);
            lock (p.Monsters)
            {
                if (p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p) && InPool(p.Walk.Position(now)))
                {
                    var stats = CharacterPlayerStats(p);
                    int Get(uint k) => stats.GetValueOrDefault(k);
                    ApplyPlayerAilmentsLocked(p, now, a => a.Inflict(PlayerElementalAilments.Kind.Chill, 30, .5, now, Get, p.IncomingHitRandom));
                }
            }
            // Server-only Fearful magnitudes are absent from client tables. This level-scaled fallback is calibration.
            var baseDamage = Monsters.BaseDamage(boss.Variety!, boss.Level) ?? (1, 1);
            double perOrb = (baseDamage.Item1 + baseDamage.Item2) * .25;
            int fear = p.PlayerLife > 0 && !InGracePeriod(p) ? Fears(p.Walk.Position(now)) : 0;
            if (fear != e.FearValue)
            {
                if (e.Fear != 0) await AllBuff(p, InstanceEntities.RemoveBuff(PlayerId, e.Fear));
                e.Fear = fear == 0 ? (ushort)0 : p.NextBuff++; e.FearValue = fear;
                // Fearful is not IsRecovery: its wire record carries no GrantedStats values.
                if (fear > 0) await AllBuff(p, InstanceEntities.AddBuff(PlayerId, e.Fear, 870, stacks: (ushort)fear));
            }
            await Ground(p, e, now, (at, minion) => DamagePacket.Of(DamageType.Cold, perOrb * Fears(at) * (minion ? .5 : 1), perOrb * Fears(at) * (minion ? .5 : 1)), () => Current);
        }
    }
    static Task AllBuff(Player p, byte[] bytes)
    { return SendWorldRecord(p, PlayerId, bytes, "[Yugul] Fearful"); }
    internal async Task Clear()
    {
        RetireShots();
        foreach (var p in players.ToArray())
        {
            if (exposure.TryGetValue(p, out var e) && e.Fear != 0) { await AllBuff(p, InstanceEntities.RemoveBuff(PlayerId, e.Fear)); e.Fear = 0; e.FearValue = 0; }
            foreach (uint id in owned.Concat(scene.Select(s => s.Id)).ToArray())
            {
                bool shown;
                lock (p.Monsters) { shown = p.MonsterViews.Remove(id) | p.SceneViews.Remove(id); p.AsleepMonsters.Remove(id); }
                if (shown && !p.Closed && !p.LeavingArea) { await SendWorldRemoval(p, id, "[Yugul] arena actor leaves"); }
            }
        }
        lock (owner.Monsters) foreach (uint id in owned) { owner.Monsters.Remove(id); owner.Instance.World.MonsterOrigins.TryRemove(id, out _); }
        owned.Clear(); scene.Clear(); orbs.Clear(); turrets.Clear(); channelEnds.Clear(); orbReady.Clear();
    }
    void RetireShots()
    {
        foreach (var flight in flights)
            lock (flight.Conn.Monsters)
                flight.Runtime.Discard(use => use.Payload is HostileProjectileOffense shot && owned.Contains(shot.MonsterId));
        flights.Clear();
    }
    internal void Stop()
    {
        stopped = true;
        RetireShots();
        foreach (var p in Present.ToArray())
        {
            if (exposure.TryGetValue(p, out var e) && e.Fear != 0)
            { _ = Send(p, InstanceEntities.RemoveBuff(PlayerId, e.Fear), null); e.Fear = 0; }
            foreach (uint id in owned.Concat(scene.Select(s => s.Id)).ToArray())
            {
                lock (p.Monsters) { p.MonsterViews.Remove(id); p.SceneViews.Remove(id); p.AsleepMonsters.Remove(id); }
                _ = Send(p, InstanceProtocol.RemoveObject(id), null);
            }
        }
        lock (owner.Monsters) foreach (uint id in owned)
        { owner.Monsters.Remove(id); owner.Instance.World.MonsterOrigins.TryRemove(id, out _); }
        owned.Clear(); scene.Clear(); orbs.Clear(); turrets.Clear(); channelEnds.Clear(); orbReady.Clear();
    }
}
