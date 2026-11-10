using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>The campaign's organ gates and Black Heart encounter, not the map or gauntlet variants.</summary>
static class DepravedTrinity
{
    internal const string Area = "2_9_10_2", Boss = "Metadata/Monsters/UnholyTrio/UnholyTrio";
    const string Art = "Metadata/Effects/Spells/monsters_effects/act9/The_Trio/";
    internal static readonly string[] Souls = ["Metadata/Monsters/Doedre/DoedreSoul", "Metadata/Monsters/Maligaro/MaligaroSoul", "Metadata/Monsters/Shavronne/ShavronneSoul"];
    static readonly string[] PortalNames = ["DoedrePortal", "MaligaroPortal", "ShavronnePortal"];
    static readonly int[] Effects = [406, 209, 3101, 4995, 6446, 8479, 1490, 8480, 8546, 5573, 5574, 5606, 5607, 8878];
    static readonly ConditionalWeakTable<InstanceState, Fight> Fights = new();
    static readonly ConditionalWeakTable<ClientState, SeedScene> Seeds = new();
    sealed class CleanupClock { public double Next; }
    static readonly ConditionalWeakTable<InstanceState, CleanupClock> CleanupClocks = new();
    const string SeedType = "Metadata/QuestObjects/Act9/HarvestFinalBossAnimate";
    sealed class SeedScene
    {
        public uint Id;
        bool open;
        public async Task Tick(ClientState c)
        {
            if (c.Layout.Level?.RoomObjects.FirstOrDefault(o => o.Type == SeedType) is not { } point
                || QuestObjectTypes.Find(SeedType) is not { } info || !QuestObject.CanSerialize(info.Components)) return;
            bool wanted;
            lock (c.Monsters) wanted = Enumerable.Range(0, 3).All(n => Dead(Soul(c, n))) && c.HasQuestFlag("a9q1-BlackHeartOpen");
            var w = c.Wire;
            if (Id == 0)
            {
                lock (c.Monsters) Id = c.NextObjectId++;
                open = wanted;
                await Send(w.Remote, w.Stream, w.Ciphers, [.. InstanceEntities.CreateObject(Id, InstanceEntities.ObjectTypeHash(SeedType),
                    new QuestObject(point.X, point.Y, info.Components, point.Rotation, point.Z, point.Model, (byte)(open ? 2 : 1)).Blob()),
                    .. InstanceProtocol.EnterWakeZone(Id)], "[Rotting Core] seed of corruption", still: () => CurrentGameWork && !c.Closed && !c.LeavingArea);
            }
            else if (open != wanted)
            {
                open = wanted;
                await Send(w.Remote, w.Stream, w.Ciphers, InstanceEntities.TransitionableUpdate(Id,
                    (byte)QuestObject.TransitionableIndex(info.Components), (byte)(open ? 2 : 1)), "[Rotting Core] Black Heart opens",
                    still: () => CurrentGameWork && !c.Closed && !c.LeavingArea);
            }
        }
    }
    static readonly ConditionalWeakTable<SkillCatalog, ConcurrentDictionary<(int Effect, int Level), Dictionary<uint, int>>> CachedStats = new();
    sealed record NativeSkill(int Level, Dictionary<uint, int> Stats, double Multiplier);
    static readonly ConditionalWeakTable<SkillCatalog, ConcurrentDictionary<(int Effect, int Level), NativeSkill>> ScaledSkills = new();
    static NativeSkill Native(ClientState c, int effect, byte actorLevel)
    {
        var catalog = Game.SkillsFor(c);
        return ScaledSkills.GetValue(catalog, _ => new()).GetOrAdd((effect, actorLevel), p =>
        {
            // Monster spell FloatStats scale with actor level, not the unscaled level-one integer display values.
            var row = GolemData.Skill(catalog, p.Effect, p.Level);
            return new(row.Level, row.Stats, row.Multiplier);
        });
    }
    static int Stat(ClientState c, int effect, int level, string name)
    {
        var catalog = Game.SkillsFor(c);
        var values = CachedStats.GetValue(catalog, _ => new()).GetOrAdd((effect, level), p => catalog.EffectStats(p.Effect, p.Level).ToDictionary(s => s.Item1, s => s.Item2));
        return Stats.Key(name) is uint key ? values.GetValueOrDefault(key) : 0;
    }
    sealed class Exposure
    {
        public readonly List<double> Red = [], Purple = [];
        public readonly ushort[] Buffs = new ushort[2];
        public double NextStack;
    }
    static readonly ConditionalWeakTable<ClientState, Exposure> Exposures = new();
    public static Dictionary<uint, int> PlayerStats(ClientState c, Dictionary<uint, int> stats)
    {
        if (!Exposures.TryGetValue(c, out var e)) return stats;
        void Add(string name, int amount) { if (amount != 0 && Stats.Key(name) is uint key) stats[key] = stats.GetValueOrDefault(key) + amount; }
        Add("base_damage_taken_+%", 10 * e.Red.Count);
        Add("action_speed_-%", 7 * e.Purple.Count);
        return stats;
    }
    static async Task ExposureUpdate(ClientState c, bool red, bool purple, double now, bool clear = false)
    {
        var e = Exposures.GetValue(c, _ => new());
        int r = e.Red.Count, p = e.Purple.Count;
        e.Red.RemoveAll(end => clear || end <= now); e.Purple.RemoveAll(end => clear || end <= now);
        if (!clear && now >= e.NextStack && (red || purple))
        {
            double duration = Stat(c, 8729, 1, "base_skill_effect_duration") / 1000d;
            if (red && e.Red.Count < 10) e.Red.Add(now + duration);
            if (purple && e.Purple.Count < 10) e.Purple.Add(now + duration);
            e.NextStack = now + 1;
        }
        if (r == e.Red.Count && p == e.Purple.Count) return;
        var w = c.Wire;
        foreach (int n in new[] { 0, 1 })
        {
            if (e.Buffs[n] != 0) await Send(w.Remote, w.Stream, w.Ciphers, InstanceEntities.RemoveBuff(PlayerId, e.Buffs[n]), null);
            int count = n == 0 ? e.Red.Count : e.Purple.Count;
            e.Buffs[n] = count == 0 ? (ushort)0 : c.NextBuff++;
            if (count != 0) await Send(w.Remote, w.Stream, w.Ciphers, InstanceEntities.AddBuff(PlayerId, e.Buffs[n], (ushort)(n == 0 ? 832 : 834), stacks: (ushort)count), "[Rotting Core] effluent");
        }
        lock (c.Monsters) { c.Pools = PoolsFor(c, c.Pools.Level); c.ApplyMovementSpeed(); }
        if (!c.Closed && !c.LeavingArea) await SendPlayerStatSnapshot(w.Remote, w.Stream, w.Ciphers, c, () => PlayerStatsUpdateAtGate(c, PlayerStatsWithCleared(c)), null);
    }
    public static Task Leaving(ClientState c) => !Exposures.TryGetValue(c, out _) ? Task.CompletedTask : ExposureUpdate(c, false, false, 0, clear: true);
    static double Distance((int X, int Y) a, (int X, int Y) b) => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));
    internal static bool IsBoss(MonsterCatalog.Variety? variety) => variety?.Id == Boss;
    static ushort Skill(int effect) => (ushort)(0x4000 + Array.IndexOf(Effects, effect));
    static SpawnedMonster? Soul(ClientState c, int n) => c.Monsters.Values.FirstOrDefault(m => m.Variety?.Id == Souls[n]);
    static bool Dead(SpawnedMonster? m) => m is not null && (m.Dead || m.Life == 0);
    internal static int Portal(string? model) => Array.FindIndex(PortalNames, n => model?.Contains(n, StringComparison.OrdinalIgnoreCase) == true);
    static int ReturnPortal(ClientState c, AreaTransitionObject t)
    {
        if (Portal(t.Model) < 0 && t.SourceType?.Contains("PortalToggleable", StringComparison.Ordinal) != true
            && t.SourceType?.EndsWith("UnholyTrioSubbossPortal", StringComparison.OrdinalIgnoreCase) != true) return -1;
        return Enumerable.Range(0, 3).FirstOrDefault(i => Soul(c, i) is { } m && Distance(m.Walk.Position(c.Clock.Elapsed.TotalSeconds), (t.GridX, t.GridY)) < 180, -1);
    }
    internal static bool? GateShown(ClientState c, uint id, AreaTransitionObject t)
    {
        if (c.Layout.Id != Area) return null;
        lock (c.Monsters)
        {
            int n = ReturnPortal(c, t);
            if (n >= 0) return Dead(Soul(c, n));
            n = Portal(t.Model);
            if (n >= 0) return !Dead(Soul(c, n)) && (c.HasQuestFlag("a9q1-PortalsOpen") || c.HasQuestFlag("a9q1-BlackHeartOpen") || c.ScriptShownTransitions.Contains(id));
            if (t.SourceType == "Metadata/QuestObjects/Act9/HarvestFinalBossTransition")
                return Enumerable.Range(0, 3).All(i => Dead(Soul(c, i))) && c.HasQuestFlag("a9q1-BlackHeartOpen");
        }
        return null;
    }
    internal static (int X, int Y)? Landing(ClientState c) => c.Layout.Level?.RoomObjects.FirstOrDefault(o =>
        o.Type == "Metadata/Terrain/Act9/Area10Level2/Objects/UnholyTrioLootLocator") is { } point ? c.Layout.Near((point.X, point.Y)) : null;
    internal static void CarryLoot(ClientState c, (int X, int Y) from, (int X, int Y) to)
    {
        var ground = c.Instance.Ground;
        foreach (var (id, item) in ground.Within(from.X, from.Y, 160, _ => true))
        {
            // Take checks the exact object atomically: a concurrent pickup wins rather than being restored or duplicated.
            // Private quest items keep their ownership and place. Existing drops move silently, not as new filter events.
            if (!ground.Origin(id).Shared || ground.Take(id, item) is null) continue;
            double scale = Math.Min(1, 18 / Math.Max(1, Distance((item.GridX, item.GridY), from)));
            var at = c.Layout.Near((to.X + (int)((item.GridX - from.X) * scale), to.Y + (int)((item.GridY - from.Y) * scale)));
            ground.Add(item with { GridX = at.X, GridY = at.Y }, null, shared: true, justDropped: false);
        }
    }
    internal static List<uint> RetireCreatures(ClientState c)
    {
        (int X, int Y)[] safe;
        lock (c.Npcs) safe = [.. c.Npcs.Values.Where(n => n.Npc.Id is "Metadata/NPC/Act9/SinCore" or "Metadata/NPC/Act9/Lilly").Select(n => (n.X, n.Y))];
        lock (c.Monsters)
        {
            var dead = c.Monsters.Where(m => Dead(m.Value) && (IsBoss(m.Value.Variety) || Souls.Contains(m.Value.Variety?.Id))).Select(m => m.Key).ToHashSet();
            double now = c.Clock.Elapsed.TotalSeconds;
            var gone = c.Monsters.Where(m => m.Value.Hostile && m.Value.OwnerId is null && !Dead(m.Value)
                && (m.Value.SummonerId is uint parent && dead.Contains(parent)
                    || m.Value.Rarity != MonsterRarity.Unique && safe.Any(at => Distance(at, m.Value.Walk.Position(now)) < 65))).ToArray();
            foreach (var (id, monster) in gone)
            {
                monster.Dead = true; monster.Life = 0; monster.ActionGeneration++;
                c.Monsters.Remove(id); c.Instance.World.MonsterOrigins.TryRemove(id, out _);
            }
            return [.. gone.Select(m => m.Key)];
        }
    }
    internal static (int X, int Y)? Destination(ClientState c, AreaTransitionObject t)
    {
        if (c.Layout.Id != Area) return null;
        lock (c.Monsters)
        {
            int n = ReturnPortal(c, t);
            if (n >= 0 && c.Layout.AreaTransitions.FirstOrDefault(p => Portal(p.Transition.Model) == n && ReturnPortal(c, p.Transition) < 0).Transition is { } home)
                return c.Layout.Near((home.GridX + 10, home.GridY));
            n = Portal(t.Model);
            if (n >= 0 && Soul(c, n) is { } soul)
            { var p = soul.Walk.Position(c.Clock.Elapsed.TotalSeconds); return c.Layout.Near((p.X - 18, p.Y - 12)); }
            if (t.SourceType == "Metadata/QuestObjects/Act9/HarvestFinalBossTransition")
            {
                if (c.Monsters.Values.Any(m => IsBoss(m.Variety) && Dead(m))) return Landing(c);
                if (c.LaterFormPoints.TryGetValue(Boss, out var heart)) return c.Layout.Near((heart.X, heart.Y - 28));
            }
        }
        return null;
    }
    public static async Task<bool> Use(ClientState c, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (c.Layout.Id != Area || c.Layout.AreaTransitions.FirstOrDefault(t => t.Id == use.TargetId).Transition is not { } gate) return false;
        if (Destination(c, gate) is not { } destination) return false;
        if (!TransitionShown(c, use.TargetId, gate)) return true;
        if (!TryBeginObjectWalk(c, requestedAt, gate.GridX, gate.GridY, AreaTransitionObjectSize, out var walk)) return true;
        var (action, now, here, stop, arrive) = walk; var w = c.Wire;
        if (!use.Started) await Send(w.Remote, w.Stream, w.Ciphers, InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, gate.GridX, gate.GridY, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId), null, still: () => PlayerActionCurrent(c, action));
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (!PlayerActionCurrent(c, action)) return true;
        try
        {
            if (!CurrentGameWork || !TransitionShown(c, use.TargetId, gate)
                || !PlayerWalk.InWalkUpRange(stop, (gate.GridX, gate.GridY), AreaTransitionObjectSize)) return true;
            lock (c.Monsters) c.Walk.Teleport(destination.X, destination.Y, ProjectileWorldTime(c));
            await Send(w.Remote, w.Stream, w.Ciphers, InstanceProtocol.Teleport(PlayerId, destination.X, destination.Y), $"[Rotting Core] gate 0x{use.TargetId:x} -> {destination}", still: () => PlayerActionCurrent(c, action));
        }
        finally { await Send(w.Remote, w.Stream, w.Ciphers, InstanceProtocol.ActionEnded(PlayerId), null, still: () => PlayerActionCurrent(c, action)); }
        return true;
    }
    internal static MonsterAttack Attack(ClientState c, MonsterCatalog.Variety variety, byte level, int effect)
    {
        var catalog = Game.SkillsFor(c); var native = Native(c, effect, level); int l = native.Level;
        int Raw(string name) => Stats.Key(name) is uint key ? native.Stats.GetValueOrDefault(key) : 0;
        bool spell = effect is not (406 or 209 or 5606 or 5607);
        var damage = DamagePacket.Empty;
        if (spell)
            foreach (var type in DamageTypes.ConversionOrder)
            {
                string slug = type.ToString().ToLowerInvariant();
                int min = Raw($"spell_minimum_base_{slug}_damage"), max = Raw($"spell_maximum_base_{slug}_damage");
                if (max > 0) damage = damage.Add(type, new(min, max));
            }
        if (!spell)
        {
            var (min, max) = Monsters.BaseDamage(variety, level) ?? (1, 1);
            int more = Stat(c, effect, l, "active_skill_damage_+%_final");
            damage = DamagePacket.Of(DamageType.Physical, min, max).Scale(native.Multiplier * Math.Max(0, 1 + more / 100d));
        }
        double seconds = (spell ? catalog.CastTimeMs(effect) / 1000d : variety.AttackMs / 1000d)
            / Math.Max(.01, 1 + Raw(spell ? "active_skill_cast_speed_+%_final" : "active_skill_attack_speed_+%_final") / 100d);
        return new(Skill(effect), Math.Max(.25, seconds), 110, effect is 8546 or 8479 or 8480 ? DamagePacket.Empty : damage)
        { Spell = spell, Scripted = true, Projectile = effect is 5573 or 5574
            ? new(effect == 5573 ? "Metadata/Projectiles/TrioMortarRed" : "Metadata/Projectiles/TrioMortarPurple", 750, true, 2)
            : catalog.MonsterProjectile(effect) };
    }
    internal static IReadOnlyList<GrantedSkill>? SkillsFor(ClientState c, MonsterCatalog.Variety v, byte level) => !IsBoss(v) ? null
        : [.. Effects.Select(e => Game.SkillsFor(c).FindByRow(Skill(e), e, Game.SkillsFor(c).MaxLevelAt(e, level),
            WithBehaviourVariation(c, e, MonsterProjectileSkillStats(c, e, Game.SkillsFor(c).MaxLevelAt(e, level), Attack(c, v, level, e))))).OfType<GrantedSkill>()];
    public static async Task Tick(ClientState c)
    {
        if (c.Layout.Id != Area || !CurrentGameWork || !c.PlayerSent || c.Closed || c.LeavingArea || c.Paused) return;
        // Flags and deaths may change the gates for another member of the same instance as well.
        var w = c.Wire; await UpdateShownTransitions(w.Remote, w.Stream, w.Ciphers, c, "Rotting Core encounter");
        await Seeds.GetValue(c, _ => new()).Tick(c);
        if (IsMonsterRunner(c))
        {
            var clock = CleanupClocks.GetValue(c.Instance, _ => new());
            double now = c.Clock.Elapsed.TotalSeconds;
            if (now >= clock.Next)
            {
                clock.Next = now + 1;
                foreach (uint id in RetireCreatures(c))
                {
                    lock (c.Monsters) { c.MonsterViews.Remove(id); c.AsleepMonsters.Remove(id); }
                    await Send(w.Remote, w.Stream, w.Ciphers, InstanceProtocol.RemoveObject(id), "[Rotting Core] encounter creature retired");
                }
            }
        }
        (uint Key, SpawnedMonster Value) found;
        lock (c.Monsters) { var p = c.Monsters.FirstOrDefault(p => IsBoss(p.Value.Variety)); if (p.Value is null) return; found = (p.Key, p.Value); }
        var f = Fights.GetValue(c.Instance, _ => new(found.Key, found.Value, c));
        await f.Gate.WaitAsync();
        try { await f.Bind(c); if (IsMonsterRunner(c)) await f.Step(c, c.Clock.Elapsed.TotalSeconds); }
        finally { f.Gate.Release(); }
    }
    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var f in Fights) f.Value.Stop();
            foreach (var e in Exposures) _ = Leaving(e.Key);
            foreach (var s in Seeds)
                if (s.Value.Id != 0 && !s.Key.Closed && !s.Key.LeavingArea)
                    _ = Send(s.Key.Wire.Remote, s.Key.Wire.Stream, s.Key.Wire.Ciphers, InstanceProtocol.RemoveObject(s.Value.Id), null);
        }
    }
    sealed record Hit(double At, int Effect, (int X, int Y) Aim, double Radius, string Model, double Duration = 0, int Colour = -1, int Generation = 0,
        (int X, int Y)? Facing = null, double VisualSeconds = 1);
    sealed record Ground((int X, int Y) At, double Radius, double Until, int Colour);
    sealed record Visual(uint Id, (int X, int Y) At, string Model, double Until, uint Animation);
    internal sealed class Fight
    {
        public readonly SemaphoreSlim Gate = new(1);
        public bool Stopped;
        readonly uint id; readonly SpawnedMonster boss;
        readonly (int X, int Y) centre;
        readonly List<ClientState> players = [];
        readonly HashSet<ClientState> actors = [];
        readonly List<Hit> hits = [];
        readonly List<Visual> effects = [];
        readonly List<Ground> pools = [];
        readonly List<(int Generation, double At)> releases = [];
        readonly HashSet<int> released = [];
        readonly HashSet<uint> adds = [];
        double ready, hiddenUntil, hideAt, nextSpecial, nextBlood, nextSludge, deathAt = double.NaN, evacuationAt = double.NaN;
        bool started, submerged, hideApplied, returned;
        int rotation;
        public Fight(uint objectId, SpawnedMonster monster, ClientState c)
        {
            id = objectId; boss = monster;
            centre = c.LaterFormPoints.GetValueOrDefault(Boss, monster.Walk.Position(c.Clock.Elapsed.TotalSeconds));
            boss.LifePhases ??= new([66, 33]);
            started = !boss.EntranceGrace; submerged = boss.LifePhases.Active;
            hideApplied = submerged;
            if (submerged) foreach (var add in c.Monsters.Where(m => m.Value.Variety?.Id == "Metadata/Monsters/SpiderPlated/SpiderPlatedUnholyEmerge" && !Dead(m.Value))) adds.Add(add.Key);
        }
        IEnumerable<ClientState> Present => players.Where(p => !p.Closed && !p.LeavingArea);
        bool Awake(ClientState p) => p.MonsterViews.ContainsKey(id) && !p.AsleepMonsters.Contains(id);
        async Task All(byte[] bytes, bool actor = false)
        {
            foreach (var p in Present.Where(p => !actor || Awake(p)).ToArray())
            { var w = p.Wire; await Send(w.Remote, w.Stream, w.Ciphers, [.. bytes], null, still: () => !Stopped && CurrentGameWork && !p.Closed && !p.LeavingArea); }
        }
        public async Task Bind(ClientState c)
        {
            players.RemoveAll(p => p.Closed || p.LeavingArea);
            if (!players.Contains(c))
            {
                players.Add(c);
                foreach (var fx in effects) await CreateFx(c, fx);
            }
            if (boss.Dead || !Awake(c) || !actors.Add(c)) return;
            var w = c.Wire;
            await Send(w.Remote, w.Stream, w.Ciphers, InstanceEntities.PlayerSkillsChange(id, SkillsFor(c, boss.Variety!, boss.Level)!, []), "[Rotting Core] Trinity native skills");
        }
        async Task Visibility(bool visible)
        {
            if (boss.States is not { } states) return;
            boss.States = states.Set("visible", visible ? 1 : 0);
            foreach (var p in Present)
                lock (p.Monsters) if (p.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                    p.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { StateMachine = boss.States } };
            int component = boss.Variety!.ComponentsOrBase.ToList().IndexOf("StateMachine");
            if (component >= 0) await All(InstanceEntities.StateMachineUpdate(id, (byte)component, boss.States), true);
        }
        public async Task Step(ClientState c, double now)
        {
            if (Stopped || !CurrentGameWork) return;
            foreach (var fx in effects.Where(f => f.Until <= now).ToArray()) { effects.Remove(fx); await All(InstanceProtocol.RemoveObject(fx.Id)); }
            pools.RemoveAll(p => p.Until <= now);
            foreach (var p in Present)
            {
                var at = p.Walk.Position(now);
                bool active = !boss.Dead && p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p);
                await ExposureUpdate(p, active && pools.Any(h => h.Colour == 0 && Distance(at, h.At) <= h.Radius),
                    active && pools.Any(h => h.Colour == 1 && Distance(at, h.At) <= h.Radius), now, clear: boss.Dead || p.PlayerLife == 0);
            }
            if (boss.Dead || boss.Life == 0)
            {
                hits.Clear(); pools.Clear();
                if (double.IsNaN(deathAt)) { deathAt = now; Console.WriteLine("[Rotting Core] Trinity defeated; blood evacuation starts"); }
                if (double.IsNaN(evacuationAt) && now >= deathAt + 3)
                { await Fx(c, centre, Art + "blood_vortex/rig.ao", now + 8); evacuationAt = now + 2.5; }
                if (!returned && now >= evacuationAt && Landing(c) is { } landing)
                {
                    CarryLoot(c, centre, landing);
                    returned = true;
                    foreach (var p in Present.Where(p => p.PlayerLife > 0 && Distance(p.Walk.Position(now), centre) < 160).ToArray())
                        await BloodReturn(p, now);
                }
                return;
            }
            var target = Present.Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p) && Awake(p)
                && Distance(p.Walk.Position(now), centre) < 130).MinBy(p => Distance(p.Walk.Position(now), centre));
            if (target is null) { hits.Clear(); releases.Clear(); return; }
            boss.BossBarShown = true;
            foreach (var p in Present.Where(p => Awake(p) && p.BossBarsSeen.Add(id)))
            { var w = p.Wire; await ShowBossLifeBar(w.Remote, w.Stream, w.Ciphers, id, boss, p); }
            if (!started)
            {
                started = true; ready = now + 3;
                await All(InstanceEntities.TransitionableUpdate(id, (byte)boss.Variety!.ComponentsOrBase.ToList().IndexOf("Transitionable"), 2), true);
                lock (c.Monsters)
                    if (c.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                        c.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { TransitionState = 2, StateMachine = boss.States } };
                boss.EntranceGrace = false;
                await Cast(target, 5606, now, target.Walk.Position(now));
                nextSpecial = ready; nextBlood = ready + 2; nextSludge = ready + 5;
                Console.WriteLine("[Rotting Core] Trinity emerged");
            }
            if (!submerged && boss.LifePhases!.Begin(boss.Life, boss.MaxLife))
            {
                submerged = true; hiddenUntil = now + 3; hits.Clear(); releases.Clear(); released.Clear();
                hideApplied = false; hideAt = now + Attack(c, boss.Variety!, boss.Level, 8546).Seconds;
                await Cast(target, 8546, now, centre);
                var markers = c.Layout.Level?.RoomObjects.Where(o => o.Type == "Metadata/Monsters/SpiderPlated/SpiderPlatedUnholyEmergeSpawner").ToArray() ?? [];
                var v = Monsters.Find("Metadata/Monsters/SpiderPlated/SpiderPlatedUnholyEmerge");
                if (v is { Supported: true })
                    foreach (var point in markers)
                    { uint add = await SpawnMonster(c.Wire.Remote, c.Wire.Stream, c.Wire.Ciphers, c, v, boss.Level, MonsterRarity.Normal, point.X, point.Y); if (add != 0) adds.Add(add); }
                Console.WriteLine($"[Rotting Core] scorpion phase {boss.LifePhases.Completed + 1}: {adds.Count} aberrations");
            }
            if (submerged)
            {
                if (!hideApplied && now >= hideAt) { hideApplied = true; await Visibility(false); }
                if (now < hiddenUntil || adds.Any(a => c.Monsters.TryGetValue(a, out var m) && !Dead(m))) return;
                adds.Clear(); submerged = false; boss.LifePhases!.Finish(); await Visibility(true);
                var aim = target.Walk.Position(now); var floor = c.Layout.Near((aim.X + 12, aim.Y + 12));
                lock (c.Monsters) boss.Walk.Teleport(floor.X, floor.Y, now);
                await All(InstanceProtocol.Teleport(id, floor.X, floor.Y), true);
                await Cast(target, 5606, now, aim); ready = now + 2;
                Console.WriteLine("[Rotting Core] Trinity resurfaced"); return;
            }
            foreach (var release in releases.Where(r => r.At <= now).ToArray())
            {
                releases.Remove(release);
                if (boss.ActionGeneration == release.Generation && now >= boss.FlinchUntil && MonsterActionFactor(boss, now) > 0) released.Add(release.Generation);
                else hits.RemoveAll(h => h.Generation == release.Generation);
            }
            foreach (var hit in hits.Where(h => h.At <= now && released.Contains(h.Generation)).ToArray())
            {
                hits.Remove(hit);
                if (hit.Model.Length > 0) await Fx(c, hit.Aim, hit.Model, now + Math.Max(hit.VisualSeconds, hit.Duration));
                foreach (var p in Present) await Damage(p, hit, now);
                if (hit.Duration > 0) pools.Add(new(hit.Aim, hit.Radius, now + hit.Duration, hit.Colour));
            }
            released.RemoveWhere(g => !hits.Any(h => h.Generation == g));
            if (now < ready || now < boss.BusyUntil || now < boss.FlinchUntil || MonsterActionFactor(boss, now) == 0) return;
            var to = target.Walk.Position(now); boss.Target = target; boss.Aggro = true;
            double distance = Distance(boss.Walk.Position(now), to);
            if (distance > 35 && now < nextSpecial && now < nextSludge && now < nextBlood)
            {
                int generation;
                lock (c.Monsters) generation = ++boss.ActionGeneration;
                await SendMonsterAction(c.Wire.Remote, c.Wire.Stream, c.Wire.Ciphers, c, id, boss, generation, () =>
                {
                    double at = c.Clock.Elapsed.TotalSeconds; var here = boss.Walk.Position(at);
                    boss.Walk.Order(to.X, to.Y, at);
                    return InstanceProtocol.ActionStarted(id, here.X, here.Y, to.X, to.Y, 0x2909, c.NextActionSequence(), c.MonsterOrderFlags);
                }, "[Rotting Core] Trinity follows its target");
                ready = now + .7; return;
            }
            int effect = now >= nextSpecial ? (++rotation % 3 == 0 ? 8878 : rotation % 3 == 1 ? 8479 : 8480)
                : now >= nextSludge ? (rotation % 2 == 0 ? 5573 : 5574)
                : now >= nextBlood ? 4995 : distance < 10 ? 406 : distance < 25 ? 209 : 5607;
            double cooldown = Game.SkillsFor(c).CooldownMs(effect, Game.SkillsFor(c).MaxLevelAt(effect, boss.Level)) / 1000d;
            if (effect is 8479 or 8480 or 8878) nextSpecial = now + cooldown;
            if (effect is 5573 or 5574) nextSludge = now + cooldown;
            if (effect == 4995) nextBlood = now + cooldown;
            await Cast(target, effect, now, to);
        }
        async Task Cast(ClientState c, int effect, double now, (int X, int Y) aim)
        {
            var attack = Attack(c, boss.Variety!, boss.Level, effect); var from = boss.Walk.Position(now); int generation;
            lock (c.Monsters) { generation = ++boss.ActionGeneration; boss.SwingGeneration = null; boss.Walk.Order(from.X, from.Y, now); ready = boss.BusyUntil = now + attack.Seconds + .3; }
            await SendMonsterAction(c.Wire.Remote, c.Wire.Stream, c.Wire.Ciphers, c, id, boss, generation,
                () => [.. InstanceProtocol.ActionStarted(id, from.X, from.Y, aim.X, aim.Y, Skill(effect), c.NextActionSequence(), 0x0408), .. InstanceProtocol.ActionEnded(id)],
                $"[Rotting Core] {Game.SkillsFor(c).EffectId(effect)}");
            if (effect == 8546) return;
            releases.Add((generation, now + attack.Seconds * .6));
            if (effect is 8479 or 8480)
            {
                int hitEffect = effect == 8479 ? 1490 : 3101, count = Stat(c, effect, 1, "trigger_cascade_number_of_spikes"), arms = Stat(c, effect, 1, "trigger_cascade_number_of_arms");
                double interval = Stat(c, effect, 1, "trigger_cascade_ms_between_spikes") / 1000d;
                double between = Stat(c, effect, 1, "trigger_cascade_angle_between_arms") * Math.PI / 180;
                double turn = Stat(c, effect, 1, "trigger_cascade_angle") * Math.PI / 180;
                double facing = Math.Atan2(aim.Y - from.Y, aim.X - from.X);
                for (int arm = 0; arm < arms; arm++) for (int step = 0; step < count; step++)
                { double angle = facing + arm * between + step * turn; var at = c.Layout.Near((from.X + (int)(Math.Cos(angle) * (step + 1) * 5), from.Y + (int)(Math.Sin(angle) * (step + 1) * 5)));
                    hits.Add(new(now + attack.Seconds + interval * step, hitEffect, at, 10,
                        Art + (effect == 8479 ? "Maligaro/blade_blossom/poof.ao" : "Boss/death_ball_lightning/lightning_bolt.ao"), Generation: generation)); }
            }
            else if (effect is 4995 or 5573 or 5574)
            {
                int count = attack.Projectile?.Count ?? 1;
                for (int i = 0; i < count; i++)
                { double angle = i * 2 * Math.PI / count; var at = c.Layout.Near((aim.X + (int)(Math.Cos(angle) * (count == 1 ? 0 : 18)), aim.Y + (int)(Math.Sin(angle) * (count == 1 ? 0 : 18))));
                    double landing = now + attack.Seconds * .6;
                    if (attack.Projectile is { } projectile)
                    {
                        landing += Math.Max(.5, Distance(from, at) - 1.5) / ProjectileSpeed.CellsPerSecond(projectile.Speed, _ => 0, _ => 0);
                        foreach (var p in Present.Where(Awake))
                        {
                            var s = ProjectilesFor(p.Wire.Remote, p.Wire.Stream, p.Wire.Ciphers, p); s.Advance();
                            s.Schedule(GameServer.Core.Projectiles.ProjectileRuntime.Time(now + attack.Seconds * .6), () => !Stopped && MonsterActionCurrent(p, id, boss, generation), () =>
                            {
                                var shot = MonsterProjectileUse(s, id, boss, attack with { Damage = DamagePacket.Empty }, projectile with { Count = 1, MinimumRange = 0 }, generation, orderedAim: at);
                                return shot with { Profile = shot.Profile with { DirectDamage = false, EndArea = null } };
                            });
                        }
                    }
                    hits.Add(new(landing, effect, at, 9, effect == 4995
                        ? "Metadata/Effects/Spells/monsters_effects/act8/Doedre/room_red/bubble1.ao"
                        : Art + "Boss/sludge_mortar/pool_" + (effect == 5574 ? "purple" : "red") + ".ao",
                        effect == 4995 ? 0 : Stat(c, effect == 5573 ? 8729 : 8730, 1, "base_secondary_skill_effect_duration") / 1000d,
                        effect == 5574 ? 1 : 0, generation, VisualSeconds: effect == 4995 ? 2 : 1));
                    if (effect == 4995) hits.Add(new(landing + 2, 6446, at, 13,
                        "Metadata/Effects/Spells/monsters_effects/act8/Doedre/kinetic_blast/bubble/explosion_aoe.ao", Generation: generation)); }
            }
            else if (effect == 8878)
            {
                var projectile = Game.SkillsFor(c).MonsterProjectile(effect) ?? new("Metadata/Projectiles/UnholyTrioBallLightning", 400, false, 2);
                foreach (var p in Present.Where(Awake))
                { var s = ProjectilesFor(p.Wire.Remote, p.Wire.Stream, p.Wire.Ciphers, p); s.Advance();
                    s.Schedule(GameServer.Core.Projectiles.ProjectileRuntime.Time(now + 1.5), () => !Stopped && MonsterActionCurrent(p, id, boss, generation),
                        () => MonsterProjectileUse(s, id, boss, attack with { Projectile = projectile }, projectile, generation, orderedAim: aim)); }
            }
            else hits.Add(new(now + attack.Seconds * .6, effect, from, effect == 406 ? 12 : effect == 209 ? 25 : 30,
                effect is 406 or 209 ? "" : Art + "Boss/slam_impact/slam_impact.ao", Generation: generation, Facing: effect == 209 ? aim : null));
        }
        async Task Damage(ClientState p, Hit hit, double now)
        {
            MonsterHitOutcome? outcome = null; var w = p.Wire; var attack = Attack(p, boss.Variety!, boss.Level, hit.Effect);
            await Send(w.Remote, w.Stream, w.Ciphers, [], null, atGate: () =>
            {
                lock (p.Monsters)
                {
                    if (Stopped || !CurrentGameWork || boss.Dead || submerged || p.Closed || p.LeavingArea || p.Paused || InGracePeriod(p)) return null;
                    bool Inside((int X, int Y) at) => Distance(at, hit.Aim) <= hit.Radius && (hit.Facing is not { } facing
                        || (long)(at.X - hit.Aim.X) * (facing.X - hit.Aim.X) + (long)(at.Y - hit.Aim.Y) * (facing.Y - hit.Aim.Y) >= 0);
                    var minionAttack = attack with { Damage = attack.Damage.Scale(Math.Max(0, 1 + Stat(p, hit.Effect,
                        Game.SkillsFor(p).MaxLevelAt(hit.Effect, boss.Level), "monster_penalty_against_minions_damage_+%_final_vs_player_minions") / 100d)) };
                    foreach (var minion in p.Golems.All.Values.Concat(p.Reservations.Fighters.Values).ToArray())
                        if (!minion.Monster.Dead && Inside(minion.Monster.Walk.Position(now))) GolemTakeHitLocked(p, minion, id, boss, minionAttack, now, area: true);
                    if (p.PlayerLife == 0 || !Inside(p.Walk.Position(now))) return null;
                    outcome = CommitMonsterHitLocked(w.Remote, p, id, boss, attack, now, p.Walk.Position(now), boss.Walk.Position(now), false, p.NextActionSequence());
                    return outcome?.HitPacket;
                }
            });
            if (outcome is not null) await PresentMonsterHit(w.Remote, w.Stream, w.Ciphers, p, id, outcome);
        }
        async Task Fx(ClientState c, (int X, int Y) at, string model, double until, uint animation = 0)
        {
            uint fx; lock (c.Monsters) fx = c.NextObjectId++;
            var visual = new Visual(fx, at, model, until, animation); effects.Add(visual);
            foreach (var p in Present) await CreateFx(p, visual);
        }
        static async Task CreateFx(ClientState p, Visual fx)
        {
            var w = p.Wire;
            await Send(w.Remote, w.Stream, w.Ciphers, [.. InstanceEntities.CreateObject(fx.Id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), new EffectObject(fx.At.X, fx.At.Y, fx.Model).Blob()),
                .. InstanceProtocol.EnterWakeZone(fx.Id), .. InstanceProtocol.AnimationUpdate(fx.Id, 0, fx.Animation, 1)], null,
                still: () => !p.Closed && !p.LeavingArea && CurrentGameWork);
        }
        public void Stop()
        {
            Stopped = true;
            foreach (var p in Present.ToArray())
                foreach (var fx in effects.ToArray())
                    _ = Send(p.Wire.Remote, p.Wire.Stream, p.Wire.Ciphers, InstanceProtocol.RemoveObject(fx.Id), null, still: () => !p.Closed && !p.LeavingArea);
        }
        async Task BloodReturn(ClientState p, double now)
        {
            if (Landing(p) is not { } to) return;
            // The tick's evacuation deadline follows the covering vortex; never block the area tick on a timer.
            if (Stopped || !CurrentGameWork || p.Closed || p.LeavingArea || p.PlayerLife == 0) return;
            var w = p.Wire;
            lock (p.Monsters) { Interlocked.Increment(ref p.ActionCount); p.Walk.Teleport(to.X, to.Y, ProjectileWorldTime(p)); }
            await Send(w.Remote, w.Stream, w.Ciphers, InstanceProtocol.Teleport(PlayerId, to.X, to.Y), $"[Rotting Core] blood carried {CharacterName(p)} to Lilly at {to}");
            await Fx(p, to, Art + "blood_vortex/rig.ao", p.Clock.Elapsed.TotalSeconds + 4, 1);
        }
    }
}
