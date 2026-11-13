using System.Runtime.CompilerServices;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>The campaign cauldron encounter; the map variants have independent scripts.</summary>
public static class DoedreSewers
{
    public const string Boss = "Metadata/Monsters/Doedre/DoedreSewers";
    const string Area = "2_8_2_2", Objects = "Metadata/Monsters/Doedre/DoedreSewer/";
    const string Art = "Metadata/Effects/Spells/monsters_effects/act8/Doedre/";
    const int Melee = 406, Wand = 6437, Burst = 6440, Warp = 6447, Emerge = 6742, Drain = 6743;
    static readonly int[] Effects = [Melee, Warp, Drain, Wand, Burst, Emerge];
    static readonly string[] Colours = ["Red", "Purple", "Green"];
    static readonly RuleTable<InstanceState, Fight> Fights = new();
    static readonly RuleTable<ClientState, Exposure> Exposures = new();
    sealed class Exposure
    {
        public int Red, Green, Purple;
        public readonly ushort[] Buffs = new ushort[4];
        public double Dps, NextStack;
    }
    public static bool IsBoss(MonsterCatalog.Variety? v) => v?.Id == Boss;
    public static StateMachineData? StartStates(MonsterCatalog.Variety v) => IsBoss(v)
        ? v.StateMachine?.Set("disable_life_bar", 1).Set("boss_life_bar", 0) : null;
    static ushort Skill(int effect) => (ushort)(0x4000 + Array.IndexOf(Effects, effect));
    public static Dictionary<uint, int> PlayerStats(ClientState c, Dictionary<uint, int> stats)
    {
        if (!Exposures.TryGetValue(c, out var e)) return stats;
        void Add(string name, int value) { if (value != 0 && Stats.Key(name) is uint k) stats[k] = stats.GetValueOrDefault(k) + value; }
        Add("base_damage_taken_+%", 10 * e.Red);
        Add("doedre_aura_damage_+%_final", -7 * e.Green);
        Add("action_speed_-%", 7 * e.Purple);
        return stats;
    }
    public static double MinionDamageFactor(ClientState c) => Exposures.TryGetValue(c, out var e) ? Math.Max(0, 1 - .07 * e.Green) : 1;
    public static IReadOnlyList<GrantedSkill>? SkillsFor(ClientState c, MonsterCatalog.Variety v, byte level, int phase = 0)
    {
        if (!IsBoss(v)) return null;
        var catalog = Game.SkillsFor(c);
        return [.. Effects.Select(effect => catalog.FindByRow(Skill(effect), effect, catalog.MaxLevelAt(effect, level),
            WithBehaviourVariation(c, effect, Attack(c, v, level, effect, phase).InPlaceSkillStats()))).OfType<GrantedSkill>()];
    }
    static MonsterAttack Attack(ClientState c, MonsterCatalog.Variety v, byte level, int effect, int phase)
    {
        var catalog = Game.SkillsFor(c); int l = catalog.MaxLevelAt(effect, level);
        var (min, max) = Monsters.BaseDamage(v, level) ?? (1, 1);
        bool spell = effect != Melee;
        var damage = spell ? catalog.HitDamage(effect, l) : DamagePacket.Of(DamageType.Physical, min, max);
        double seconds = effect == Melee ? v.AttackMs / 1000d : catalog.CastTimeMs(effect) / 1000d;
        if (effect is not (Drain or Emerge)) { if (phase == 1) seconds /= 1.5; if (phase == 2) damage = damage.Scale(1.5); }
        return new(Skill(effect), Math.Max(.1, seconds), effect == Melee ? v.AttackRange : 85, damage) { Spell = spell, Scripted = true };
    }
    public static async Task Leaving(ClientState c) => await Clear(c);
    static async Task Refresh(ClientState c)
    {
        lock (c.Monsters) { c.Pools = PoolsFor(c, c.Pools.Level); c.ApplyMovementSpeed(); }
        var w = c.Wire;
        await SendPlayerStatSnapshot(w.Remote, w.Stream, w.Ciphers, c, () => PlayerStatsUpdateAtGate(c, PlayerStatsWithCleared(c)), null);
    }
    static async Task Clear(ClientState c)
    {
        if (!Exposures.TryGetValue(c, out var e)) return;
        if (e.Red == 0 && e.Green == 0 && e.Purple == 0 && e.Dps == 0 && e.Buffs.All(b => b == 0)) return;
        lock (c.Monsters)
        {
            c.TrapDegenPerSecond = Math.Max(0, c.TrapDegenPerSecond - e.Dps);
            e.Dps = 0; e.Red = e.Green = e.Purple = 0; e.NextStack = 0;
        }
        var w = c.Wire;
        for (int i = 0; i < e.Buffs.Length; i++)
            if (e.Buffs[i] != 0)
            {
                ushort buff = e.Buffs[i]; e.Buffs[i] = 0;
                if (!c.Closed) await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, c, PlayerId, InstanceEntities.RemoveBuff(PlayerId, buff), null);
            }
        if (!c.Closed && !c.LeavingArea) await Refresh(c);
    }
    static async Task Stack(ClientState c, int phase)
    {
        if (c.Closed || c.LeavingArea || c.Paused || c.PlayerLife == 0 || InGracePeriod(c)) return;
        var e = Exposures.GetValue(c, _ => new());
        int index = phase == 0 ? 0 : phase == 2 ? 1 : 2;
        int count;
        lock (c.Monsters)
        {
            int old = index == 0 ? e.Red : index == 1 ? e.Green : e.Purple;
            count = Math.Min(10, old + 1); if (old == count) return;
            if (index == 0) e.Red = count; else if (index == 1) e.Green = count; else e.Purple = count;
        }
        var w = c.Wire;
        if (e.Buffs[index] != 0) await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, c, PlayerId, InstanceEntities.RemoveBuff(PlayerId, e.Buffs[index]), null);
        e.Buffs[index] = c.NextBuff++;
        await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, c, PlayerId, InstanceEntities.AddBuff(PlayerId, e.Buffs[index], (ushort)(832 + index), stacks: (ushort)count), "[Doedre] effluent stacks");
        await Refresh(c);
    }
    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights) pair.Value.Stop();
            foreach (var pair in Exposures) _ = Clear(pair.Key);
        }
    }
    public static async Task Tick(ClientState c)
    {
        if (c.Layout.Id != Area || !c.PlayerSent || c.Closed || c.LeavingArea) return;
        if (c.PlayerLife == 0) await Clear(c);
        if (!Fights.TryGetValue(c.Instance, out var fight))
        {
            KeyValuePair<uint, SpawnedMonster> found;
            lock (c.Monsters) found = c.Monsters.FirstOrDefault(p => IsBoss(p.Value.Variety));
            if (found.Value is null) return;
            fight = Fights.GetValue(c.Instance, _ => new(found.Key, found.Value, c));
        }
        await fight.Gate.WaitAsync();
        try { await fight.Bind(c); if (IsMonsterRunner(c)) await fight.Step(c, c.Clock.Elapsed.TotalSeconds); }
        finally { fight.Gate.Release(); }
    }
    public static async Task<bool> Use(ClientState c, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (c.Layout.Id != Area || !Fights.TryGetValue(c.Instance, out var f) || use.TargetId != f.ValveId) return false;
        if (!TryBeginObjectWalk(c, requestedAt, f.ValveAt.X, f.ValveAt.Y, 3, out var walk)) return true;
        var (action, now, here, stop, arrive) = walk; var w = c.Wire;
        if (!use.Started) await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, c, PlayerId,
            InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, f.ValveAt.X, f.ValveAt.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId), null,
            still: () => PlayerActionCurrent(c, action));
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (!PlayerActionCurrent(c, action)) return true;
        try
        {
            if (!PlayerWalk.InReach(stop, f.ValveAt, 3, c.Layout.Walkable)) return true;
            await f.Gate.WaitAsync();
            try { await f.Turn(c, c.Clock.Elapsed.TotalSeconds); } finally { f.Gate.Release(); }
        }
        finally { await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, c, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null); }
        return true;
    }
    sealed record Scene(uint Id, string Type, GlyphQuest.Placement At, QuestObjectTypes.Info Info);
    sealed record Hazard(uint Id, (int X, int Y) At, double Ends, int Kind, string Model,
        (int X, int Y)? To = null, double Moved = 0, bool Loop = false)
    {
        public (int X, int Y) Position(double now)
        {
            if (To is not { } to) return At;
            double t = Math.Clamp(now - Moved, 0, 1);
            return ((int)Math.Round(At.X + (to.X - At.X) * t), (int)Math.Round(At.Y + (to.Y - At.Y) * t));
        }
    }
    sealed record Contact(double At, int Generation, int Effect, (int X, int Y) From, (int X, int Y) To, double Radius, bool Line = false);
    sealed class Fight
    {
        public readonly SemaphoreSlim Gate = new(1);
        readonly uint id; readonly SpawnedMonster boss;
        readonly List<ClientState> players = [];
        readonly HashSet<ClientState> bound = [], actors = [];
        readonly List<Scene> scene = [];
        readonly List<Hazard> hazards = [];
        readonly List<Contact> contacts = [];
        readonly HashSet<uint> adds = [];
        readonly (int X, int Y) centre;
        readonly double speed;
        readonly double cloudDps;
        public uint ValveId;
        public (int X, int Y) ValveAt;
        int phase = -1;
        bool started, draining, finished, stopped;
        double ready, nextHazard, nextWarp, nextBurst, nextGround, nextCloudMove;
        ushort bossBuff = 0xf100;
        public Fight(uint objectId, SpawnedMonster monster, ClientState c)
        {
            id = objectId; boss = monster; speed = boss.Walk.Speed;
            var catalog = Game.SkillsFor(c);
            cloudDps = catalog.EffectStats(744, catalog.MaxLevelAt(744, boss.Level))
                .Where(s => Stats.Name(s.Item1) == "base_chaos_damage_to_deal_per_minute").Sum(s => s.Item2) / 60d;
            var placements = new QuestObjectTypes.AreaPlacements(Area, c.Seed, []);
            centre = boss.Walk.Position(c.Clock.Elapsed.TotalSeconds);
            foreach (string name in new[] { "DoedreCauldronValve", "DoedreSewersCauldron", "DoedreSewerFaceDoor" })
                foreach (var at in placements.Of(Objects + name))
                {
                    // The tile-only face door is absent from older object catalogs; its native .ot has no parents.
                    var info = QuestObjectTypes.Find(at.Type) ?? (name == "DoedreSewerFaceDoor"
                        ? new QuestObjectTypes.Info(at.Type, ["Positioned", "BaseEvents", "Animated", "Transitionable", "TriggerableBlockage"], 3, 3, false, null) { OwnModel = false } : null);
                    if (info is not null && QuestObject.CanSerialize(info.Components))
                    {
                        uint obj = c.TakeObjectId();
                        var placed = at;
                        if (name == "DoedreCauldronValve" && ServerLevels.For(Area, c.Seed)?.RoomObjects
                            .FirstOrDefault(o => o.Type == at.Type && o.X == at.X && o.Y == at.Y) is { } roomValve)
                            placed = at with { Z = roomValve.Z };
                        scene.Add(new(obj, at.Type, placed, info));
                        if (name == "DoedreCauldronValve") { ValveId = obj; ValveAt = (at.X, at.Y); }
                    }
                }
            if (scene.FirstOrDefault(s => s.Type.EndsWith("Cauldron")) is { } cauldron)
            {
                centre = (cauldron.At.X, cauldron.At.Y);
                hazards.Add(new(c.TakeObjectId(), centre, double.PositiveInfinity, 3, Art + "cauldron/cauldron_brown.ao", Moved: c.Clock.Elapsed.TotalSeconds));
                hazards.Add(new(c.TakeObjectId(), centre, double.PositiveInfinity, 5, Art + "pipe/pipe_brown.ao", Moved: c.Clock.Elapsed.TotalSeconds));
            }
            boss.LifePhases ??= new([]);
            if (!boss.Dead && boss.Life > 0)
            {
                boss.EntranceGrace = true; boss.BossBarShown = false;
                boss.States = (boss.States ?? boss.Variety!.StateMachine)?.Set("disable_life_bar", 1).Set("boss_life_bar", 0);
            }
            Console.WriteLine($"[gateway] Doedre controller: {scene.Count} arena objects, valve 0x{ValveId:x} at {ValveAt}");
        }
        bool Awake(ClientState c) => c.MonsterViews.ContainsKey(id) && !c.AsleepMonsters.Contains(id);
        IEnumerable<ClientState> Present => players.Where(p => !p.Closed && !p.LeavingArea);
        byte State(Scene s) => s.Type.EndsWith("Valve") ? (byte)(finished ? 3 : draining ? 1 : 2)
            : s.Type.EndsWith("FaceDoor") ? (byte)(finished ? 3 : 1) : (byte)1;
        async Task Create(ClientState c, Scene s)
        {
            var a = s.At; var w = c.Wire;
            string? model = s.Info.OwnModel ? null : a.Model ?? s.Info.Model;
            await Send(w.Remote, w.Stream, w.Ciphers, InstanceEntities.CreateObject(s.Id, InstanceEntities.ObjectTypeHash(s.Type),
                new QuestObject(a.X, a.Y, s.Info.Components, a.Rotation, a.Z, model, State(s), Blocked: s.Type.EndsWith("FaceDoor") && !finished).Blob()), "[Doedre] arena object");
            await Send(w.Remote, w.Stream, w.Ciphers, InstanceProtocol.EnterWakeZone(s.Id), null);
        }
        public async Task Bind(ClientState c)
        {
            players.RemoveAll(p => p.Closed || p.LeavingArea);
            if (!players.Contains(c)) players.Add(c);
            if (bound.Add(c))
            {
                foreach (var s in scene) await Create(c, s);
                foreach (var h in hazards) await CreateHazard(c, h with { At = h.Position(c.Clock.Elapsed.TotalSeconds) });
            }
            if (Awake(c) && !boss.Dead && actors.Add(c))
            {
                var skills = SkillsFor(c, boss.Variety!, boss.Level, Math.Max(0, phase))!;
                lock (c.Monsters)
                    if (c.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                        c.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { Skills = skills } };
                var w = c.Wire;
                await Send(w.Remote, w.Stream, w.Ciphers, InstanceEntities.PlayerSkillsChange(id, skills, []), "[Doedre] complete actor skill list");
                await Send(w.Remote, w.Stream, w.Ciphers, InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, started ? 0 : 1)]), "[Doedre] overhead life bar");
                if (boss.States is { } states)
                    await Send(w.Remote, w.Stream, w.Ciphers, InstanceEntities.StateMachineUpdate(id,
                        (byte)boss.Variety!.ComponentsOrBase.ToList().IndexOf("StateMachine"), states), null);
                if (phase >= 0) await Send(w.Remote, w.Stream, w.Ciphers, InstanceEntities.AddBuff(id, bossBuff, (ushort)(phase == 0 ? 835 : phase == 1 ? 837 : 836)), null);
            }
        }
        async Task All(byte[] packet, bool actor = false)
        {
            foreach (var p in Present.Where(p => !actor || Awake(p)).ToArray())
            { var w = p.Wire; await Send(w.Remote, w.Stream, w.Ciphers, [.. packet], null, still: () => !stopped && !p.Closed && !p.LeavingArea); }
        }
        async Task States()
        {
            foreach (var s in scene.Where(s => s.Info.Components.Contains("Transitionable")))
            {
                await All(InstanceEntities.TransitionableUpdate(s.Id, (byte)QuestObject.TransitionableIndex(s.Info.Components), State(s)));
                if (s.Info.Components.Contains("TriggerableBlockage")) await All(InstanceEntities.BlockageUpdate(s.Id, !finished));
            }
        }
        public async Task Turn(ClientState c, double now)
        {
            if (stopped || finished || draining || boss.Dead || boss.Life == 0 || ValveId == 0) return;
            draining = true;
            lock (c.Monsters)
            {
                boss.EntranceGrace = true; boss.ActionGeneration++; boss.SwingGeneration = null;
                boss.Walk.Teleport(centre.X, centre.Y, now);
            }
            contacts.Clear();
            await ClearHazards(c, drainLiquid: true);
            foreach (var p in Present) await Clear(p);
            if (phase >= 0) await All(InstanceEntities.RemoveBuff(id, bossBuff), true);
            await All(InstanceProtocol.Teleport(id, centre.X, centre.Y), true);
            await Cast(c, started ? Drain : Emerge, now, centre);
            ready = now + Attack(c, boss.Variety!, boss.Level, started ? Drain : Emerge, phase).Seconds;
            started = true;
            boss.BossBarShown = true;
            boss.States = boss.States?.Set("disable_life_bar", 0).Set("boss_life_bar", 1).Set("aggro", 1);
            lock (c.Monsters)
                if (c.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                    c.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { StateMachine = boss.States,
                        Stats = (origin.Object.Stats ?? []).Where(s => s.Item1 != StatKeys.HideMiniLifeBar).Append((StatKeys.HideMiniLifeBar, 0)).ToList() } };
            await All(InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, 0)]), true);
            if (boss.States is { } states)
                await All(InstanceEntities.StateMachineUpdate(id, (byte)boss.Variety!.ComponentsOrBase.ToList().IndexOf("StateMachine"), states), true);
            await States();
            Console.WriteLine($"[gateway] Doedre: valve turned, cauldron drains until {ready:0.000}");
        }
        public async Task Step(ClientState c, double now)
        {
            if (stopped) return;
            if (boss.Dead || boss.Life == 0)
            {
                if (!finished)
                {
                    finished = true; draining = false; contacts.Clear();
                    await ClearHazards(c);
                    foreach (var p in Present) await Clear(p);
                    await States();
                    Console.WriteLine("[gateway] Doedre: defeated, valve disabled, effluent cleared");
                }
                return;
            }
            if (!started) return;
            if (draining)
            {
                if (now < ready) return;
                draining = false; phase = (phase + 1) % 3;
                lock (c.Monsters)
                {
                    boss.EntranceGrace = false;
                    boss.LifePhases!.DamageTakenPercent = phase == 0 ? 50 : 100;
                    boss.Walk.SetSpeed(speed * (phase == 1 ? 1.5 : 1), now);
                }
                await All(InstanceEntities.AddBuff(id, bossBuff, (ushort)(phase == 0 ? 835 : phase == 1 ? 837 : 836)), true);
                var skills = SkillsFor(c, boss.Variety!, boss.Level, phase)!;
                await All(InstanceEntities.PlayerSkillsChange(id, skills, []), true);
                byte tr = (byte)boss.Variety!.ComponentsOrBase.ToList().IndexOf("Transitionable");
                await All(InstanceEntities.TransitionableUpdate(id, tr, 2), true);
                lock (c.Monsters)
                    if (c.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                        c.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { TransitionState = 2, Skills = skills } };
                await Liquid(c, now);
                await States(); nextHazard = now; nextWarp = now + 6; nextBurst = now + 3;
                Console.WriteLine($"[gateway] Doedre: {Colours[phase]} effluent active");
            }
            await Hazards(c, now);
            await Contacts(c, now);
            if (now < boss.BusyUntil || now < boss.FlinchUntil || MonsterActionFactor(boss, now) == 0) return;
            var pos = boss.Walk.Position(now);
            var target = Present.Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p) && Awake(p)).MinBy(p => Distance(pos, p.Walk.Position(now)));
            if (target is null || Distance(pos, target.Walk.Position(now)) > 110) return;
            boss.Target = target; boss.Aggro = true;
            if (!boss.BossBarShown)
            { boss.BossBarShown = true; var w = target.Wire; await ShowBossLifeBar(w.Remote, w.Stream, w.Ciphers, id, boss, target); }
            int effect = now >= nextWarp ? Warp : now >= nextBurst ? Burst : Distance(pos, target.Walk.Position(now)) < 10 && Random.Shared.Next(3) == 0 ? Melee : Wand;
            if (effect == Warp) nextWarp = now + 9;
            if (effect == Burst) nextBurst = now + 5;
            await Cast(target, effect, now, target.Walk.Position(now));
        }
        async Task Cast(ClientState c, int effect, double now, (int X, int Y) aim)
        {
            var attack = Attack(c, boss.Variety!, boss.Level, effect, phase);
            var pos = boss.Walk.Position(now); int generation;
            lock (c.Monsters) { generation = ++boss.ActionGeneration; boss.SwingGeneration = null; boss.BusyUntil = now + attack.Seconds + .2; boss.Walk.Order(pos.X, pos.Y, now); }
            var w = c.Wire;
            await SendMonsterAction(w.Remote, w.Stream, w.Ciphers, c, id, boss, generation, () =>
                [.. InstanceProtocol.ActionStarted(id, pos.X, pos.Y, aim.X, aim.Y, Skill(effect), c.NextActionSequence(), 0x0408), .. InstanceProtocol.ActionEnded(id)],
                $"[Doedre] {Game.SkillsFor(c).EffectId(effect)}");
            if (effect is Drain or Emerge) return;
            if (effect == Melee) contacts.Add(new(now + attack.Seconds * .4, generation, effect, pos, aim, 9));
            else if (effect == Warp)
            {
                contacts.Add(new(now + attack.Seconds * .4, generation, effect, pos, pos, 13));
                contacts.Add(new(now + attack.Seconds * .6132, generation, effect, pos, Floor(c, aim), 13));
                await Effect(c, aim, Art + "blood_teleport/teleport_appear.ao", now + attack.Seconds, 4);
            }
            else
            {
                var projectile = new SkillCatalog.MonsterProjectileInfo(effect == Wand ? "Metadata/Projectiles/DoedreBloodBall" : "Metadata/Projectiles/DoedreWandStrike", 750, false, 2)
                    { Count = effect == Wand ? 3 : 1, SpreadRadians = Math.PI / 9 };
                foreach (var p in Present.Where(Awake))
                {
                    var wire = p.Wire; var session = ProjectilesFor(wire.Remote, wire.Stream, wire.Ciphers, p);
                    session.Advance();
                    session.Schedule(ProjectileRuntime.Time(now + attack.Seconds * (effect == Wand ? .4 : .5)),
                        () => !stopped && !draining && MonsterActionCurrent(p, id, boss, generation), () =>
                    {
                        var use = MonsterProjectileUse(session, id, boss, attack with { Projectile = projectile }, projectile, generation, orderedAim: aim);
                        return use with { Profile = use.Profile with { Pierce = int.MaxValue,
                            ContactArea = effect == Burst ? new ProjectileAreaRecipe("Doedre bubble burst", 7, 5, 8) : null } };
                    });
                }
            }
        }
        async Task Contacts(ClientState c, double now)
        {
            foreach (var hit in contacts.Where(h => h.At <= now).ToArray())
            {
                contacts.Remove(hit);
                if (hit.Generation != boss.ActionGeneration || MonsterActionFactor(boss, now) == 0) continue;
                if (hit.Effect == Warp && hit.To != hit.From)
                { lock (c.Monsters) boss.Walk.Teleport(hit.To.X, hit.To.Y, now); await All(InstanceProtocol.Teleport(id, hit.To.X, hit.To.Y), true); }
                foreach (var p in Present) await Hit(p, Attack(c, boss.Variety!, boss.Level, hit.Effect, phase), hit);
            }
        }
        async Task Hit(ClientState p, MonsterAttack attack, Contact hit)
        {
            var w = p.Wire; MonsterHitOutcome? outcome = null;
            bool Contains((int X, int Y) at) => hit.Line ? LineDistance(at, hit.From, hit.To) <= hit.Radius : Distance(at, hit.To) <= hit.Radius;
            await Send(w.Remote, w.Stream, w.Ciphers, [], null, atGate: () =>
            {
                lock (p.Monsters)
                {
                    if (!CurrentGameWork || stopped || boss.Dead || draining || p.Closed || p.LeavingArea || p.Paused || InGracePeriod(p)) return null;
                    double now = p.Clock.Elapsed.TotalSeconds;
                    foreach (var g in p.Golems.All.Values.Concat(p.Reservations.Fighters.Values).ToArray())
                        if (!g.Monster.Dead && Contains(g.Monster.Walk.Position(now))) GolemTakeHitLocked(p, g, id, boss, attack with { Damage = attack.Damage.Scale(.6) }, now, area: true);
                    if (p.PlayerLife == 0 || !Contains(p.Walk.Position(now))) return null;
                    outcome = CommitMonsterHitLocked(w.Remote, p, id, boss, attack, now, p.Walk.Position(now), hit.From, false, p.NextActionSequence());
                    return outcome?.HitPacket;
                }
            });
            if (outcome is not null) await PresentMonsterHit(w.Remote, w.Stream, w.Ciphers, p, id, outcome);
        }
        async Task Liquid(ClientState c, double now)
        {
            foreach (var h in hazards.Where(h => h.Kind is 3 or 5).ToArray()) { hazards.Remove(h); await All(InstanceProtocol.RemoveObject(h.Id)); }
            var cauldron = scene.FirstOrDefault(s => s.Type.EndsWith("Cauldron"));
            var at = cauldron is null ? centre : (cauldron.At.X, cauldron.At.Y);
            await Effect(c, at, Art + $"cauldron/cauldron_{Colours[phase].ToLowerInvariant()}.ao", double.PositiveInfinity, 3);
            await Effect(c, at, Art + $"pipe/pipe_{Colours[phase].ToLowerInvariant()}.ao", double.PositiveInfinity, 5);
        }
        async Task<uint> Effect(ClientState c, (int X, int Y) at, string model, double ends, int kind)
        {
            uint fx = c.TakeObjectId(); var h = new Hazard(fx, at, ends, kind, model, Moved: c.Clock.Elapsed.TotalSeconds); hazards.Add(h);
            foreach (var p in Present) await CreateHazard(p, h, model);
            return fx;
        }
        async Task CreateHazard(ClientState c, Hazard h, string? model = null)
        {
            model ??= h.Model;
            var w = c.Wire;
            if (h.Kind is 3 or 5 && QuestObjectTypes.Find(LiquidType(h)) is { } info && scene.FirstOrDefault(s => s.Type.EndsWith("Cauldron")) is { } cauldron)
                await Create(c, new(h.Id, info.Type, cauldron.At with { Type = info.Type }, info));
            else
            {
                await Send(w.Remote, w.Stream, w.Ciphers, InstanceEntities.CreateObject(h.Id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), new EffectObject(h.At.X, h.At.Y, model).Blob()), null);
                await Send(w.Remote, w.Stream, w.Ciphers, InstanceProtocol.EnterWakeZone(h.Id), null);
            }
            await Send(w.Remote, w.Stream, w.Ciphers, InstanceProtocol.AnimationUpdate(h.Id, AnimatedIndex(h), h.Kind is 3 or 5 ? h.Loop ? 2u : 1u : 0u, 1), null);
        }
        static string LiquidType(Hazard h) => Objects + (h.Kind == 5 ? "DoedreSewersPipeLiquid" : "DoedreSewersCauldronLiquid")
            + (h.Model.Contains("_red.") ? "Red" : h.Model.Contains("_purple.") ? "Purple" : h.Model.Contains("_green.") ? "Green" : "Neutral");
        static byte AnimatedIndex(Hazard h) => h.Kind is 3 or 5 && QuestObjectTypes.Find(LiquidType(h)) is { } info ? (byte)info.Components.ToList().IndexOf("Animated") : (byte)0;
        async Task Hazards(ClientState c, double now)
        {
            foreach (var liquid in hazards.Where(h => h.Kind is 3 or 5 && !h.Loop && now >= h.Moved + 3).ToArray())
            {
                hazards[hazards.IndexOf(liquid)] = liquid with { Loop = true };
                await All(InstanceProtocol.AnimationUpdate(liquid.Id, AnimatedIndex(liquid), 2, 1));
            }
            foreach (var h in hazards.Where(h => h.Ends <= now).ToArray())
            {
                hazards.Remove(h); await All(InstanceProtocol.RemoveObject(h.Id));
                if (h.Kind == 0)
                {
                    await Effect(c, h.At, Art + "room_red/explosion_aoe.ao", now + 1, 4);
                    foreach (var p in Present.Where(p => Distance(p.Walk.Position(now), h.At) <= 12)) await Stack(p, 0);
                }
            }
            foreach (uint add in adds.ToArray())
            {
                SpawnedMonster? m; lock (c.Monsters) m = c.Monsters.GetValueOrDefault(add);
                if (m is null || !m.Dead && m.Life > 0) continue;
                adds.Remove(add);
                if (phase == 2 && m is not null)
                    foreach (var p in Present.Where(p => Distance(p.Walk.Position(now), m.Walk.Position(now)) <= 18)) await Stack(p, 2);
            }
            if (now >= nextHazard)
            {
                nextHazard = now + (phase == 2 ? 3 : 2);
                var target = Present.Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)).MinBy(p => Distance(p.Walk.Position(now), centre));
                if (target is not null)
                {
                    var aim = target.Walk.Position(now);
                    double angle = Random.Shared.NextDouble() * Math.Tau;
                    var at = Floor(c, (aim.X + (int)(Math.Cos(angle) * 8), aim.Y + (int)(Math.Sin(angle) * 8)));
                    if (Distance(at, centre) <= 65)
                    {
                        if (phase == 2 && adds.Count < 8 && Monsters.Find("Metadata/Monsters/ZombieDismember/DoedreZombieMinionEmerge") is { Supported: true } v)
                        {
                            var w = c.Wire;
                            await SpawnMonster(w.Remote, w.Stream, w.Ciphers, c, v, boss.Level, MonsterRarity.Normal, at.Item1, at.Item2,
                                owned: (add, m) => { m.SummonerId = id; m.NoRewards = Game.SkillsFor(c).MonsterSummon(4058, 1)?.NoRewards ?? false; adds.Add(add); });
                        }
                        else if (phase is 0 or 1) await Effect(c, at, Art + (phase == 0 ? "room_red/bubble1.ao" : "moving_smog/moving_smog.ao"), now + (phase == 0 ? 2 : 10), phase);
                    }
                }
            }
            if (now < nextGround) return;
            nextGround = now + .25;
            if (now >= nextCloudMove)
            {
                nextCloudMove = now + 1;
                foreach (var cloud in hazards.Where(h => h.Kind == 1).ToArray())
                {
                    var from = cloud.Position(now);
                    var target = Present.Where(p => p.PlayerLife > 0).MinBy(p => Distance(from, p.Walk.Position(now)));
                    if (target is null) continue;
                    var at = target.Walk.Position(now); double d = Math.Max(1, Distance(from, at));
                    var to = Floor(c, (from.X + (int)((at.X - from.X) / d * Math.Min(3, d)), from.Y + (int)((at.Y - from.Y) / d * Math.Min(3, d))));
                    hazards[hazards.IndexOf(cloud)] = cloud with { At = from, To = to, Moved = now };
                    await All(InstanceProtocol.MoveTo(cloud.Id, to.X, to.Y, 1, InstanceProtocol.FollowTerrain));
                }
            }
            foreach (var p in Present)
            {
                var e = Exposures.GetValue(p, _ => new());
                bool InCloud((int X, int Y) at) => hazards.Any(h => h.Kind == 1 && Distance(at, h.Position(now)) <= 9);
                bool inside = phase == 1 && p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p) && InCloud(p.Walk.Position(now));
                if (inside && now >= e.NextStack) { e.NextStack = now + 1; await Stack(p, 1); }
                double raw = cloudDps;
                double dps = inside && !PlayerImmunities(p).Contains(DamageType.Chaos) ? raw * (1 - Math.Min(MaxResistance(p, DamageType.Chaos), Resistance(p, DamageType.Chaos)) / 100d) : 0;
                lock (p.Monsters)
                {
                    p.TrapDegenPerSecond = Math.Max(0, p.TrapDegenPerSecond - e.Dps + dps); e.Dps = dps;
                    if (!p.Paused && !InGracePeriod(p))
                        DoedreCloudMinionsLocked(p, now, raw, at => phase == 1 && InCloud(at), minionDotCarry);
                }
                if (inside == (e.Buffs[3] != 0)) continue;
                var w = p.Wire;
                if (inside) { e.Buffs[3] = p.NextBuff++; await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, p, PlayerId, InstanceEntities.AddBuff(PlayerId, e.Buffs[3], 25), null); }
                else { await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, p, PlayerId, InstanceEntities.RemoveBuff(PlayerId, e.Buffs[3]), null); e.Buffs[3] = 0; }
            }
        }
        readonly Dictionary<uint, double> minionDotCarry = [];
        async Task ClearHazards(ClientState c, bool drainLiquid = false)
        {
            foreach (var h in hazards.ToArray())
            {
                if (drainLiquid && h.Kind is 3 or 5) { await All(InstanceProtocol.AnimationUpdate(h.Id, AnimatedIndex(h), 0, 1)); continue; }
                await All(InstanceProtocol.RemoveObject(h.Id)); hazards.Remove(h);
            }
            foreach (uint add in adds)
            {
                lock (c.Monsters)
                    if (c.Monsters.TryGetValue(add, out var m)) { m.Dead = true; m.Life = 0; }
                await All(InstanceProtocol.RemoveObject(add));
            }
            adds.Clear();
        }
        public void Stop()
        {
            stopped = true; boss.EntranceGrace = false;
            boss.LifePhases!.DamageTakenPercent = 100;
            foreach (var p in Present)
            {
                var w = p.Wire;
                lock (p.Monsters)
                    foreach (uint add in adds)
                        if (p.Monsters.TryGetValue(add, out var m)) { m.Dead = true; m.Life = 0; }
                foreach (uint add in adds) _ = Send(w.Remote, w.Stream, w.Ciphers, InstanceProtocol.RemoveObject(add), null);
                foreach (var s in scene) _ = Send(w.Remote, w.Stream, w.Ciphers, InstanceProtocol.RemoveObject(s.Id), null);
                foreach (var h in hazards) _ = Send(w.Remote, w.Stream, w.Ciphers, InstanceProtocol.RemoveObject(h.Id), null);
                if (Awake(p)) _ = Send(w.Remote, w.Stream, w.Ciphers, InstanceEntities.RemoveBuff(id, bossBuff), null);
            }
        }
        static (int X, int Y) Floor(ClientState c, (int X, int Y) at) => c.Layout.Walkable is { } walk && !walk(at.X, at.Y) ? c.Layout.Near(at) : at;
        static double Distance((int X, int Y) a, (int X, int Y) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        static double LineDistance((int X, int Y) p, (int X, int Y) a, (int X, int Y) b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / Math.Max(1, dx * dx + dy * dy), 0, 1);
            return Math.Sqrt(Math.Pow(p.X - a.X - t * dx, 2) + Math.Pow(p.Y - a.Y - t * dy, 2));
        }
    }
}

public static partial class Game
{
    public static void DoedreCloudMinionsLocked(ClientState c, double now, double rawDps,
        Func<(int X, int Y), bool> inside, Dictionary<uint, double> carry)
    {
        foreach (var g in c.Golems.All.Values.Concat(c.Reservations.Fighters.Values).ToArray())
        {
            var m = g.Monster;
            if (!GolemCurrent(c, g) || m.Dead || m.Life == 0 || g.Definition.Kind is "Agony" or "SkitterCold" or "SkitterLightning"
                || !inside(m.Walk.Position(now))) continue;
            var res = MonsterHitResistance(m, DamageType.Chaos, default);
            double damage = rawDps * .25 * Math.Max(0, 1 - Math.Min(res.Value, res.Maximum) / 100d) + carry.GetValueOrDefault(g.Id);
            int amount = (int)Math.Floor(damage); carry[g.Id] = damage - amount;
            m.Life = (uint)Math.Max(0, (long)m.Life - amount);
            c.Minions.OutputQueue.Add(new([], "Doedre sewage cloud damages minion", g.Id, m, PlayerMinions.OutputKind.Life));
            if (m.Life == 0) GolemDieLocked(c, g, now);
        }
    }
}
