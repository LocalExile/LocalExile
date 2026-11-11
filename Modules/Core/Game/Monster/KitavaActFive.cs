using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>The Cathedral Rooftop encounter. The Feeding Trough and map variants have different scripts.</summary>
static class KitavaActFive
{
    internal const string Area = "1_5_8", Root = "Metadata/Monsters/KitavaBoss/";
    internal const string First = Root + "Kitava1", Second = Root + "Kitava2", Last = Root + "Kitava3", Heart = Root + "KitavaHeart";
    const string Sign = Root + "SignOfPurity/SignOfPurity", Art = "Metadata/Effects/Spells/monsters_effects/act10/Kitava/";
    internal const string Complete = "a5q6-CompletedBoss";
    static readonly int[] Effects = [6598, 6599, 6600, 6601, 6602, 6590, 6594, 6636, 6638, 6603, 6604, 6619, 6622, 6586];
    static readonly ConditionalWeakTable<InstanceState, Fight> Fights = new();
    static readonly ConditionalWeakTable<ClientState, Exposure> Exposures = new();
    static readonly ConditionalWeakTable<AreaLayout, Anchor> Anchors = new();
    static readonly ConditionalWeakTable<ClientState, DockCleanup> Docks = new();
    static readonly ConditionalWeakTable<SkillCatalog, ConcurrentDictionary<(int, byte), (int Level, Dictionary<uint, int> Stats, double Multiplier, int Crit)>> Native = new();
    sealed record Anchor(int X, int Y, float Rotation);
    sealed class Exposure { public double Dps; }
    sealed class DockCleanup { public double Next; }
    internal static bool IsBody(MonsterCatalog.Variety? v) => v?.Id is First or Second or Last;
    internal static bool IsActor(MonsterCatalog.Variety? v) => IsBody(v) || v?.Id == Heart;
    static string Flag(string name) => Quests.InstanceFlag("KitavaAct5-" + name);
    internal static LifePhaseGate? Phases(MonsterCatalog.Variety v) => v.Id switch
    {
        First => new([50]), Second => new([75, 50]), Heart => new([50, 1]), _ => null,
    };
    internal static (int X, int Y) SpawnPoint(ClientState c, (int X, int Y) fallback) => AnchorOf(c, fallback) is var a ? (a.X, a.Y) : fallback;
    static Anchor AnchorOf(ClientState c, (int X, int Y) fallback) => Anchors.GetValue(c.Layout, _ =>
    {
        if (c.LaterFormPoints.TryGetValue(First, out var supplied)) return new(supplied.X, supplied.Y, 0);
        var points = AreaPopulation.LoadPoints(Area, c.Seed, []);
        var point = points.FirstOrDefault(p => p.Name == "force:mapboss");
        return point is null ? new(fallback.X, fallback.Y, 0) : new(point.X, point.Y, point.Angle);
    });
    static ushort Skill(int effect) => (ushort)(0x4000 + Array.IndexOf(Effects, effect));
    static (int Level, Dictionary<uint, int> Stats, double Multiplier, int Crit) Data(ClientState c, int effect, byte level) =>
        Native.GetValue(Game.SkillsFor(c), _ => new()).GetOrAdd((effect, level), p => GolemData.Skill(Game.SkillsFor(c), p.Item1, p.Item2));
    static int Stat(ClientState c, int effect, byte level, string name) =>
        Stats.Key(name) is uint key ? Data(c, effect, level).Stats.GetValueOrDefault(key) : 0;
    internal static MonsterAttack Attack(ClientState c, MonsterCatalog.Variety v, byte level, int effect)
    {
        var row = Data(c, effect, level);
        bool spell = effect == 6586;
        DamagePacket damage;
        if (spell)
        {
            damage = DamagePacket.Empty;
            foreach (var type in DamageTypes.ConversionOrder)
            {
                string slug = type.ToString().ToLowerInvariant();
                int min = Stat(c, effect, level, $"spell_minimum_base_{slug}_damage"), max = Stat(c, effect, level, $"spell_maximum_base_{slug}_damage");
                if (max > 0) damage = damage.Add(type, new(min, max));
            }
        }
        else
        {
            var (min, max) = Monsters.BaseDamage(v, level) ?? (1, 1);
            damage = DamagePacket.Of(DamageType.Physical, min, max).Scale(row.Multiplier * Math.Max(0, 1 + Stat(c, effect, level, "active_skill_damage_+%_final") / 100d));
            double fire = Math.Clamp(Stat(c, effect, level, "skill_physical_damage_%_to_convert_to_fire"), 0, 100) / 100d;
            if (fire > 0) { var physical = damage[DamageType.Physical]; damage = DamagePacket.Of(DamageType.Physical, physical.Min * (1 - fire), physical.Max * (1 - fire)).Add(DamageType.Fire, physical.Scale(fire)); }
        }
        double seconds = Game.SkillsFor(c).CastTimeMs(effect) / 1000d;
        seconds /= Math.Max(.01, 1 + Stat(c, effect, level, spell ? "active_skill_cast_speed_+%_final" : "active_skill_attack_speed_+%_final") / 100d);
        return new(Skill(effect), Math.Max(.1, seconds), 150, effect is 6619 or 6622 ? DamagePacket.Empty : damage)
        {
            Spell = spell, Scripted = true,
            Projectile = effect is 6636 or 6638 ? new("Metadata/Projectiles/KitavaDebrisThrow", 750, true, 3)
                { Count = 4, SpreadRadius = 18, AreaRadius = 10 } : Game.SkillsFor(c).MonsterProjectile(effect),
            Release = effect is 6636 or 6638 ? new(effect == 6636 ? "phase1_rangedattack_left" : "phase1_rangedattack_right", .62,
                new ContactPoint(0, -600, -323.675f), false) : null,
        };
    }
    internal static IReadOnlyList<GrantedSkill>? SkillsFor(ClientState c, MonsterCatalog.Variety v, byte level)
    {
        if (v.Id is not (First or Second)) return null;
        var catalog = Game.SkillsFor(c);
        return [.. Effects.Where(e => v.Id == Second || Array.IndexOf(Effects, e) < 11).Select(e => catalog.FindByRow(Skill(e), e,
            catalog.MaxLevelAt(e, level), WithBehaviourVariation(c, e, MonsterProjectileSkillStats(c, e, catalog.MaxLevelAt(e, level), Attack(c, v, level, e))))).OfType<GrantedSkill>()];
    }
    public static async Task Tick(ClientState c)
    {
        if (c.Layout.Id != Area || !CurrentGameWork || !c.PlayerSent || c.Closed || c.LeavingArea || c.Paused) return;
        if (c.HasQuestFlag(Complete) && c.Clock.Elapsed.TotalSeconds >= Docks.GetOrCreateValue(c).Next)
        {
            Docks.GetOrCreateValue(c).Next = c.Clock.Elapsed.TotalSeconds + 1;
            await ClearDock(c);
        }
        if (!Fights.TryGetValue(c.Instance, out var f))
        {
            KeyValuePair<uint, SpawnedMonster> found;
            lock (c.Monsters) found = c.Monsters.FirstOrDefault(p => IsBody(p.Value.Variety) && !p.Value.Dead);
            if (found.Value is null) return;
            f = Fights.GetValue(c.Instance, _ => new(found.Key, found.Value, c));
        }
        await f.Gate.WaitAsync();
        try { await f.Bind(c); if (IsMonsterRunner(c)) await f.Step(c, c.Clock.Elapsed.TotalSeconds); }
        finally { f.Gate.Release(); }
    }
    internal static List<uint> RetireDockCreatures(ClientState c)
    {
        var points = c.Layout.Level?.RoomObjects.Where(o => o.Type is "Metadata/Terrain/Act5/Area8/Objects/LandingZone"
            or "Metadata/NPC/Act5/SinKitava" or "Metadata/NPC/Act5/Lilly").Select(o => (o.X, o.Y)).ToArray() ?? [];
        if (points.Length == 0) return [];
        lock (c.Monsters)
        {
            double now = c.Clock.Elapsed.TotalSeconds;
            bool Safe((int X, int Y) at) => points.Any(a => points.Any(b => Fight.LineDistance(at, a, b) < 75));
            var gone = c.Monsters.Where(m => m.Value.Hostile && m.Value.OwnerId is null && !m.Value.Dead
                && m.Value.Rarity != MonsterRarity.Unique && Safe(m.Value.Walk.Position(now))).ToArray();
            foreach (var (id, m) in gone)
            {
                m.Dead = true; m.Life = 0; m.ActionGeneration++;
                c.Monsters.Remove(id); c.Instance.World.MonsterOrigins.TryRemove(id, out _);
            }
            return [.. gone.Select(m => m.Key)];
        }
    }
    static async Task ClearDock(ClientState c)
    {
        foreach (uint id in RetireDockCreatures(c))
        {
            c.MonsterViews.Remove(id); c.AsleepMonsters.Remove(id); c.BossBarsSeen.Remove(id);
            var w = c.Wire; await SendWorldRemoval(w.Remote, w.Stream, w.Ciphers, c, id, "[Kitava A5] dock creature retired without rewards");
        }
    }
    public static Task Leaving(ClientState c)
    {
        if (Exposures.TryGetValue(c, out var e)) lock (c.Monsters) { c.TrapDegenPerSecond = Math.Max(0, c.TrapDegenPerSecond - e.Dps); e.Dps = 0; }
        return Task.CompletedTask;
    }
    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose() { foreach (var f in Fights) f.Value.Stop(); foreach (var p in Exposures) _ = Leaving(p.Key); }
    }
    internal static bool? GateShown(ClientState c, AreaTransitionObject t)
    {
        if (c.Layout.Id != Area) return null;
        // The room includes a decorative Steam portal doodad. It is not a usable telepad.
        if (t.SourceType?.Contains("PortalToggleable", StringComparison.Ordinal) == true
            || t.Model?.Contains("Town_Portals/steam_portals/", StringComparison.OrdinalIgnoreCase) == true) return false;
        if (t.SourceType?.Contains("CathedralKitavaLadder", StringComparison.Ordinal) == true
            || AreaTransitionCatalog.IdOf(t.DestinationHash16) == "2_6_town") return c.HasQuestFlag(Complete);
        return null;
    }
    sealed record Hit(double At, int Generation, int Effect, (int X, int Y) Aim, double Radius, (int X, int Y)? End = null);
    sealed record Patch((int X, int Y) At, double From, double Until, double Radius, int Effect);
    sealed record Visual(uint Id, (int X, int Y) At, string Model, uint Clip, double Until);
    internal sealed class Fight
    {
        internal readonly SemaphoreSlim Gate = new(1);
        readonly List<ClientState> players = [];
        readonly HashSet<ClientState> bound = [];
        readonly List<Hit> hits = [];
        readonly List<Patch> patches = [];
        readonly List<Visual> visuals = [];
        readonly HashSet<uint> adds = [];
        readonly HashSet<ClientState> struckPlayers = [];
        readonly HashSet<(ClientState, uint)> struckMinions = [];
        readonly Dictionary<(ClientState, uint), double> carry = [];
        uint id, heartId, signId;
        SpawnedMonster body;
        SpawnedMonster? heart;
        readonly Anchor anchor;
        readonly (int X, int Y) signAt, heartAt;
        double deadline, ready, nextSpecial, nextDark, nextDebris, nextAdds, lastGround;
        bool stopped, initialized, championSpawned;
        int stage, rotation;
        internal int Stage => stage;
        // 0 emerge, 1 first body, 2 change form, 3 second body, 4 expose, 5 heart, 6 recover, 7 enrage, 8 beam, 9 rescue, 10 complete.
        public Fight(uint objectId, SpawnedMonster monster, ClientState c)
        {
            id = objectId; body = monster; anchor = AnchorOf(c, monster.Walk.Position(c.Clock.Elapsed.TotalSeconds));
            var sign = c.Layout.Level?.RoomObjects.FirstOrDefault(o => o.Type == Sign);
            signAt = sign is null ? (anchor.X, anchor.Y - 100) : (sign.X, sign.Y);
            heartAt = c.Layout.Near(((anchor.X + signAt.X) / 2, (anchor.Y + signAt.Y) / 2));
            lock (c.Monsters)
            {
                var h = c.Monsters.FirstOrDefault(p => p.Value.Variety?.Id == Heart && !p.Value.Dead);
                heartId = h.Key; heart = h.Value;
                foreach (var add in c.Monsters.Where(m => m.Value.SummonerId == objectId && !m.Value.Dead)) adds.Add(add.Key);
                championSpawned = c.Monsters.Values.Any(m => (m.Variety?.Id == Root + "KitavaMinionBoss1" || m.Variety?.Id == Root + "KitavaMinionBoss2") && !m.Dead);
                body.LifePhases ??= Phases(body.Variety!);
                if (body.Variety?.Id == First && body.LifePhases?.Active == true) stage = 2;
                if (body.Variety?.Id == Second) stage = body.LifePhases?.Active == true ? heart?.LifePhases?.Active == true ? 6 : heart is not null ? 5 : 4 : 3;
                if (body.Variety?.Id == Last) stage = 7;
                if (c.Instance.HasState(Flag("Complete"))) stage = 10;
            }
        }
        IEnumerable<ClientState> Present => players.Where(p => !p.Closed && !p.LeavingArea);
        bool Awake(ClientState p) => p.MonsterViews.ContainsKey(id) && !p.AsleepMonsters.Contains(id);
        public async Task Bind(ClientState c)
        {
            players.RemoveAll(p => p.Closed || p.LeavingArea);
            if (!players.Contains(c)) { players.Add(c); foreach (var fx in visuals) await CreateFx(c, fx); }
            if (bound.Add(c) && Awake(c) && SkillsFor(c, body.Variety!, body.Level) is { } skills)
            {
                var w = c.Wire;
                await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, c, id, InstanceEntities.PlayerSkillsChange(id, skills, []), "[Kitava A5] native actor skills");
            }
        }
        Task All(uint target, byte[] bytes, bool actor = false) => All(target, new[] { bytes }, actor);
        async Task All(uint target, byte[][] records, bool actor = false)
        {
            foreach (var p in Present.Where(p => !actor || Awake(p)).ToArray())
            { var w = p.Wire; await SendWorldRecords(w.Remote, w.Stream, w.Ciphers, p, target, () => records.Select(r => r.ToArray()).ToArray(), null,
                still: () => !stopped && CurrentGameWork && !p.Closed && !p.LeavingArea); }
        }
        async Task AllRemoval(uint target)
        {
            foreach (var p in Present.ToArray())
            { var w = p.Wire; await SendWorldRemoval(w.Remote, w.Stream, w.Ciphers, p, target); }
        }
        async Task Basic(ClientState c, ushort skill, double now, double seconds, uint? target = null)
        {
            lock (c.Monsters) { body.ActionGeneration++; body.SwingGeneration = null; body.BusyUntil = now + seconds; }
            await All(id, [InstanceProtocol.ActionStarted(id, anchor.X, anchor.Y, signAt.X, signAt.Y, skill, c.NextActionSequence(), 0x0408, targetObjectId: target), InstanceProtocol.ActionEnded(id)], true);
        }
        async Task Bar(bool shown)
        {
            body.BossBarShown = shown;
            if (body.States is not { } states) return;
            body.States = states.Set("boss_life_bar", shown ? 1 : 0).Set("disable_life_bar", shown ? 0 : 1);
            foreach (var p in Present)
            {
                lock (p.Monsters) if (p.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                    p.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { StateMachine = body.States } };
                if (!Awake(p)) continue;
                int component = body.Variety!.ComponentsOrBase.ToList().IndexOf("StateMachine");
                if (component >= 0) { var w = p.Wire; await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, p, id, InstanceEntities.StateMachineUpdate(id, (byte)component, body.States), null); }
                if (shown) p.BossBarsSeen.Add(id); else p.BossBarsSeen.Remove(id);
            }
        }
        async Task Swap(ClientState c, string type, double now)
        {
            uint previous = id;
            lock (c.Monsters)
            {
                body.Dead = true; body.ActionGeneration++; c.Monsters.Remove(previous); c.Instance.World.MonsterOrigins.TryRemove(previous, out _);
            }
            await AllRemoval(previous);
            foreach (var p in Present) { p.MonsterViews.Remove(previous); p.AsleepMonsters.Remove(previous); p.BossBarsSeen.Remove(previous); }
            var v = Monsters.Find(type) ?? throw new InvalidOperationException("Missing campaign Kitava form");
            id = await SpawnMonster(c.Wire.Remote, c.Wire.Stream, c.Wire.Ciphers, c, v, body.Level, MonsterRarity.Unique,
                anchor.X, anchor.Y, rotation: anchor.Rotation, scripted: true, placement: MonsterPlacement.Established);
            if (id == 0) throw new InvalidOperationException("Could not create campaign Kitava form");
            body = c.Monsters[id]; body.EntranceGrace = true; bound.Clear();
            await Bar(false);
            Console.WriteLine($"[Kitava A5] form {type.Split('/')[^1]} at ({anchor.X}, {anchor.Y})");
        }
        public async Task Step(ClientState c, double now)
        {
            if (stopped || !CurrentGameWork || c.Paused) return;
            foreach (var fx in visuals.Where(v => v.Until <= now).ToArray()) { visuals.Remove(fx); await AllRemoval(fx.Id); }
            patches.RemoveAll(p => p.Until <= now);
            await Ground(c, now);
            if (stage == 10)
            {
                foreach (var p in Present.Where(p => !p.HasQuestFlag(Complete) && p.PlayerLife > 0 && Distance(p.Walk.Position(now), signAt) < 155)) await Finish(p, now);
                return;
            }
            var target = Present.FirstOrDefault(p => p.PlayerLife > 0 && !p.Paused && Awake(p) && Distance(p.Walk.Position(now), signAt) < 155);
            if (target is null) return;
            if (!initialized)
            {
                initialized = true;
                var sign = new Visual(c.NextObjectId++, signAt, "Metadata/QuestObjects/Act5/SignOfPurity/SignOfPurity.ao", 0, double.PositiveInfinity);
                signId = sign.Id; visuals.Add(sign); foreach (var p in Present) await CreateFx(p, sign);
                if (stage == 0)
                {
                    body.EntranceGrace = true;
                    await All(id, InstanceProtocol.Teleport(id, anchor.X, anchor.Y), true);
                    lock (c.Monsters) body.Walk.Teleport(anchor.X, anchor.Y, now);
                    if (body.Variety!.ComponentsOrBase.Contains("Transitionable"))
                        await All(id, InstanceEntities.TransitionableUpdate(id, (byte)body.Variety.ComponentsOrBase.ToList().IndexOf("Transitionable"), 2), true);
                    await Basic(c, 16641, now, 7.333); deadline = now + 7.333;
                }
                else if (stage == 4) { await Expose(c, now); }
                else if (stage == 7) { await Basic(c, 16641, now, 7); deadline = now + 7; }
                else if (stage == 6) deadline = now + 6;
            }
            if (stage == 0 && now >= deadline) { stage = 1; body.EntranceGrace = false; await Bar(true); ready = now; }
            if (stage == 1 && body.LifePhases!.Begin(body.Life, body.MaxLife))
            {
                stage = 2; hits.Clear(); patches.Clear(); await Bar(false);
                await Basic(c, 9115, now, 7.5); deadline = now + 7.5; return;
            }
            if (stage == 2 && now >= deadline)
            {
                await Swap(c, Second, now); await Basic(c, 16641, now, 6); stage = 3; ready = now + 6;
                Console.WriteLine("[Kitava A5] second combat form"); return;
            }
            if (stage == 3 && now >= ready && body.EntranceGrace) { body.EntranceGrace = false; await Bar(true); }
            if (stage == 3 && body.LifePhases!.Begin(body.Life, body.MaxLife)) { await Expose(c, now); return; }
            if (stage == 4 && now >= deadline)
            {
                if (heart is null)
                {
                    var v = Monsters.Find(Heart)!;
                    heartId = await SpawnMonster(c.Wire.Remote, c.Wire.Stream, c.Wire.Ciphers, c, v, body.Level, MonsterRarity.Unique,
                        heartAt.X, heartAt.Y, rotation: anchor.Rotation, scripted: true, placement: MonsterPlacement.Established);
                    if (heartId == 0) throw new InvalidOperationException("Could not create Kitava's Heart");
                    heart = c.Monsters[heartId];
                    if (body.LifePhases!.Completed > 0)
                    {
                        heart.Life = heart.MaxLife / 2;
                        heart.LifePhases!.Begin(heart.Life, heart.MaxLife); heart.LifePhases.Finish();
                        await All(heartId, InstanceProtocol.LifeUpdate(heartId, heart.Life, 0));
                    }
                }
                heart.EntranceGrace = false; heart.LifePhases ??= Phases(heart.Variety!);
                stage = 5; nextAdds = now;
                await All(heartId, [InstanceProtocol.ActionStarted(heartId, heartAt.X, heartAt.Y, heartAt.X, heartAt.Y, 16641, c.NextActionSequence(), 0x0408), InstanceProtocol.ActionEnded(heartId)]);
                await SpawnWave(c, now);
                Console.WriteLine($"[Kitava A5] heart exposed, interval {body.LifePhases!.Completed + 1}"); return;
            }
            if (stage == 5)
            {
                if (heart!.LifePhases!.Begin(heart.Life, heart.MaxLife))
                {
                    heart.EntranceGrace = true; hits.Clear(); await RetireAdds(c);
                    await All(heartId, [InstanceProtocol.ActionStarted(heartId, heartAt.X, heartAt.Y, heartAt.X, heartAt.Y, 9115, c.NextActionSequence(), 0x0408), InstanceProtocol.ActionEnded(heartId)]);
                    stage = 6; deadline = now + 6; await Basic(c, 5831, now, 6); return;
                }
                if (now >= nextAdds) await SpawnWave(c, now);
                return;
            }
            if (stage == 6 && now >= deadline)
            {
                await AllRemoval(heartId);
                lock (c.Monsters) { c.Monsters.Remove(heartId); c.Instance.World.MonsterOrigins.TryRemove(heartId, out _); }
                foreach (var p in Present) { p.MonsterViews.Remove(heartId); p.AsleepMonsters.Remove(heartId); }
                heart = null;
                if (body.LifePhases!.Completed == 1)
                {
                    await Swap(c, Last, now); await Basic(c, 16641, now, 7);
                    stage = 7; deadline = now + 7; return;
                }
                body.LifePhases.Finish(); stage = 3; ready = now + 1; body.EntranceGrace = false;
                await Bar(true); Console.WriteLine("[Kitava A5] heart retracted; combat resumes"); return;
            }
            if (stage == 7 && now >= deadline)
            {
                await Basic(c, 33034, now, 7.333, signId);
                await Fx(c, (anchor.X, anchor.Y), Art + "p3_destructionbeam/chargeup_rig.ao", 0, now + 8);
                stage = 8; deadline = now + 7.333;
                Console.WriteLine("[Kitava A5] enraged; destroying the Sign of Purity"); return;
            }
            if (stage == 8 && now >= deadline)
            {
                await All(signId, InstanceProtocol.AnimationUpdate(signId, 0, 2, 1));
                await Fx(c, target.Walk.Position(now), Root + "InnocenceSin/SinFlyAway.ao", 0, now + 2);
                stage = 9; deadline = now + 1; return;
            }
            if (stage == 9 && now >= deadline)
            {
                stage = 10; c.Instance.SetState(Flag("Complete"));
                foreach (var p in Present.Where(p => p.PlayerLife > 0 && Distance(p.Walk.Position(now), signAt) < 155)) await Finish(p, now);
                return;
            }
            if (stage is not (1 or 3)) return;
            foreach (var hit in hits.Where(h => h.At <= now).ToArray())
            {
                hits.Remove(hit); if (hit.Generation != body.ActionGeneration) continue;
                if (hit.Effect == 6586) await Fx(c, hit.Aim, Art + "X_skill/explosionx.ao", 0, now + 2);
                foreach (var p in Present) await Damage(p, hit, now);
            }
            if (now < ready || InGracePeriod(target)) return;
            int effect = stage == 3 && now >= nextSpecial ? 6586 : stage == 3 && now >= nextDark ? (rotation % 2 == 0 ? 6619 : 6622)
                : now >= nextDebris ? (rotation % 2 == 0 ? 6636 : 6638)
                : ++rotation % 3 == 0 ? (rotation % 2 == 0 ? 6590 : 6594) : 6598 + rotation % 5;
            double cooldown = Game.SkillsFor(c).CooldownMs(effect, Game.SkillsFor(c).MaxLevelAt(effect, body.Level)) / 1000d;
            if (effect == 6586) nextSpecial = now + cooldown;
            if (effect is 6619 or 6622) nextDark = now + cooldown;
            if (effect is 6636 or 6638) nextDebris = now + cooldown;
            await Cast(target, effect, now);
        }
        async Task Expose(ClientState c, double now)
        {
            stage = 4; body.EntranceGrace = true; championSpawned = false; hits.Clear(); patches.Clear(); await Bar(false);
            await All(signId, InstanceProtocol.AnimationUpdate(signId, 0, 1, 1));
            await Basic(c, 5828, now, 5); deadline = now + 5;
        }
        async Task SpawnWave(ClientState c, double now)
        {
            // The heart's guards are real monsters: their attacks and deaths use the ordinary monster rules.
            string[] types = ["Metadata/Monsters/RaisedSkeletons/KitavaSkeleton", "Metadata/Monster/KitavaDemon/KitavaDemon"];
            if (adds.Count(a => c.Monsters.TryGetValue(a, out var m) && !m.Dead) > 12) { nextAdds = now + 3; return; }
            for (int i = 0; i < 6; i++)
            {
                bool champion = i == 0 && !championSpawned;
                string type = champion ? Root + (body.LifePhases!.Completed == 0 ? "KitavaMinionBoss1" : "KitavaMinionBoss2") : types[i % 2];
                if (Monsters.Find(type) is not { Supported: true } v || v.Id != type) continue;
                double angle = i * Math.PI / 3;
                var at = c.Layout.Near((heartAt.X + (int)(Math.Cos(angle) * 22), heartAt.Y + (int)(Math.Sin(angle) * 22)));
                uint add = await SpawnMonster(c.Wire.Remote, c.Wire.Stream, c.Wire.Ciphers, c, v, body.Level, champion ? MonsterRarity.Unique : MonsterRarity.Normal, at.X, at.Y);
                if (add != 0)
                {
                    adds.Add(add); lock (c.Monsters) if (c.Monsters.TryGetValue(add, out var m)) m.SummonerId = id;
                    if (champion) championSpawned = true;
                }
            }
            nextAdds = now + 12;
        }
        async Task RetireAdds(ClientState c)
        {
            foreach (uint add in adds)
            {
                lock (c.Monsters)
                    if (c.Monsters.TryGetValue(add, out var m)) { m.ActionGeneration++; m.Dead = true; m.Life = 0; c.Monsters.Remove(add); c.Instance.World.MonsterOrigins.TryRemove(add, out _); }
                await AllRemoval(add);
            }
            adds.Clear();
        }
        async Task Finish(ClientState p, double now)
        {
            await Leaving(p); var w = p.Wire;
            await SetQuestFlags(w.Remote, w.Stream, w.Ciphers, p, [Complete]);
            await ClearDock(p);
            var point = p.Layout.Level?.RoomObjects.FirstOrDefault(o => o.Type == "Metadata/Terrain/Act5/Area8/Objects/LandingZone");
            if (point is null) return;
            var to = p.Layout.Near((point.X, point.Y));
            lock (p.Monsters) { Interlocked.Increment(ref p.ActionCount); p.Walk.Teleport(to.X, to.Y, now); }
            await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, p, PlayerId, InstanceProtocol.Teleport(PlayerId, to.X, to.Y), $"[Kitava A5] rescued {CharacterName(p)} to Sin and Lilly at {to}");
        }
        async Task Cast(ClientState c, int effect, double now)
        {
            var aim = c.Walk.Position(now); var attack = Attack(c, body.Variety!, body.Level, effect); int generation;
            struckPlayers.Clear(); struckMinions.Clear();
            lock (c.Monsters) { generation = ++body.ActionGeneration; body.SwingGeneration = null; body.BusyUntil = ready = now + attack.Seconds + .3; }
            await All(id, [InstanceProtocol.ActionStarted(id, anchor.X, anchor.Y, aim.X, aim.Y, attack.SkillId, c.NextActionSequence(), 0x0408), InstanceProtocol.ActionEnded(id)], true);
            double contact = now + attack.Seconds * (effect is 6590 or 6594 ? .5 : effect is 6636 or 6638 ? .62 : .41);
            if (effect is 6619 or 6622)
            {
                double duration = Stat(c, effect, body.Level, "base_skill_effect_duration") / 1000d;
                patches.Add(new(aim, contact, contact + duration, 18, effect));
                await Fx(c, aim, Art + "devouring_darkness/devourer_portal/devourer_portal_rig.ao", 0, contact + duration); return;
            }
            if (effect == 6586)
            {
                hits.Add(new(contact, generation, effect, aim, 40));
                return;
            }
            if (effect is 6636 or 6638 && attack.Projectile is { } projectile)
            {
                uint sourceId = id; var source = body;
                foreach (var p in Present.Where(Awake))
                {
                    var s = ProjectilesFor(p.Wire.Remote, p.Wire.Stream, p.Wire.Ciphers, p); s.Advance();
                    s.Schedule(GameServer.Core.Projectiles.ProjectileRuntime.Time(contact), () => !stopped && MonsterActionCurrent(p, sourceId, source, generation),
                        () => MonsterProjectileUse(s, sourceId, source, attack, projectile, generation, orderedAim: aim));
                }
                return;
            }
            if (effect is 6590 or 6594)
            {
                (double X, double Y)[] sweep = effect == 6594
                    ? [(427, -55), (416, -146), (378, -244), (311, -358), (199, -497), (55, -594), (-92, -629), (-229, -608)]
                    : [(-488, -27), (-494, -116), (-479, -257), (-427, -393), (-329, -521), (-183, -630), (12, -683), (222, -645)];
                for (int i = 0; i < sweep.Length; i++) hits.Add(new(now + attack.Seconds * (.26 + i * .065), generation, effect,
                    ModelCell(sweep[i].X, sweep[i].Y), 12));
                return;
            }
            var point = effect switch
            {
                6598 => (0d, -431.673), 6599 => (-269.16, -331.021), 6600 => (242.974, -348.052),
                6601 => (-124.266, -418.828), 6602 => (141.723, -408.199),
                6603 => (-585.451, -253.418), 6604 => (589.261, -253.418), _ => (0d, -431.673),
            };
            hits.Add(new(contact, generation, effect, ModelCell(point.Item1, point.Item2), 17));
        }
        (int X, int Y) ModelCell(double x, double y)
        {
            double cos = Math.Cos(anchor.Rotation), sin = Math.Sin(anchor.Rotation);
            return (anchor.X + (int)Math.Round((x * cos - y * sin) / WorldUnits.PerCell),
                anchor.Y + (int)Math.Round((x * sin + y * cos) / WorldUnits.PerCell));
        }
        async Task Damage(ClientState p, Hit hit, double now)
        {
            var w = p.Wire; MonsterHitOutcome? outcome = null; var attack = Attack(p, body.Variety!, body.Level, hit.Effect);
            bool Inside((int X, int Y) at) => hit.Effect == 6586 ? Distance(at, hit.Aim) <= hit.Radius
                && Math.Min(Math.Abs(at.X - hit.Aim.X - at.Y + hit.Aim.Y), Math.Abs(at.X - hit.Aim.X + at.Y - hit.Aim.Y)) <= 6
                : hit.End is { } end ? LineDistance(at, hit.Aim, end) <= hit.Radius : Distance(at, hit.Aim) <= hit.Radius;
            await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, p, id, [], null, atGate: () =>
            {
                lock (p.Monsters)
                {
                    now = Math.Max(p.Clock.Elapsed.TotalSeconds, ProjectileWorldTime(p));
                    if (stopped || !CurrentGameWork || stage is not (1 or 3) || p.Closed || p.LeavingArea || p.Paused || InGracePeriod(p)) return null;
                    var ma = attack with { Damage = attack.Damage.Scale(Math.Max(0, 1 + Stat(p, hit.Effect, body.Level, "monster_penalty_against_minions_damage_+%_final_vs_player_minions") / 100d)) };
                    foreach (var g in p.Golems.All.Values.Concat(p.Reservations.Fighters.Values).Concat(p.Summons.Fighters.Values).ToArray())
                        if (!g.Monster.Dead && Inside(g.Monster.Walk.Position(now)) && struckMinions.Add((p, g.Id))) GolemTakeHitLocked(p, g, id, body, ma, now, area: true);
                    if (p.PlayerLife == 0 || !Inside(p.Walk.Position(now)) || !struckPlayers.Add(p)) return null;
                    outcome = CommitMonsterHitLocked(w.Remote, p, id, body, attack, now, p.Walk.Position(now), (anchor.X, anchor.Y), false, p.NextActionSequence());
                    return outcome?.HitPacket;
                }
            });
            if (outcome is not null) await PresentMonsterHit(w.Remote, w.Stream, w.Ciphers, p, id, outcome);
        }
        async Task Ground(ClientState c, double now)
        {
            if (now < lastGround + .25) return;
            double seconds = Math.Clamp(now - lastGround, 0, 1);
            lastGround = now;
            foreach (var p in Present)
            {
                lock (p.Monsters)
                {
                    var e = Exposures.GetValue(p, _ => new());
                    var patch = p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p) && stage is 1 or 3
                        ? patches.FirstOrDefault(g => now >= g.From && Distance(g.At, p.Walk.Position(now)) <= g.Radius) : null;
                    double dps = patch is null || p.Immune || PlayerImmunities(p).Contains(DamageType.Physical) ? 0
                        : Stat(c, patch.Effect, body.Level, "base_physical_damage_to_deal_per_minute") / 60d;
                    p.TrapDegenPerSecond = Math.Max(0, p.TrapDegenPerSecond - e.Dps + dps); e.Dps = dps;
                    foreach (var g in p.Golems.All.Values.Concat(p.Reservations.Fighters.Values).Concat(p.Summons.Fighters.Values).ToArray())
                    {
                        var m = g.Monster;
                        if (!GolemCurrent(p, g) || m.Dead || m.Invulnerable || m.Life == 0 || g.Definition.Kind is "Agony" or "SkitterCold" or "SkitterLightning") continue;
                        var ground = stage is 1 or 3 ? patches.FirstOrDefault(h => now >= h.From && Distance(h.At, m.Walk.Position(now)) <= h.Radius) : null;
                        if (ground is null) continue;
                        double rate = Stat(c, ground.Effect, body.Level, "base_physical_damage_to_deal_per_minute") / 60d;
                        rate *= Math.Max(0, 1 + Stat(c, ground.Effect, body.Level, "monster_penalty_against_minions_damage_+%_final_vs_player_minions") / 100d);
                        double value = rate * seconds * Math.Max(0, 1 + (m.Effects["base_damage_taken_+%"] + m.Effects["physical_damage_taken_+%"]) / 100d) + carry.GetValueOrDefault((p, g.Id));
                        int amount = (int)Math.Floor(value); carry[(p, g.Id)] = value - amount;
                        amount = g.ShieldPool.Damage(amount, bypass: false, now);
                        PublishEnergyShieldLocked(p, g);
                        m.Life = (uint)Math.Max(0, (long)m.Life - amount);
                        p.Minions.OutputQueue.Add(new([], "Kitava devouring darkness damages minion", g.Id, m, PlayerMinions.OutputKind.Life));
                        if (m.Life == 0) GolemDieLocked(p, g, now);
                    }
                }
            }
            await Task.CompletedTask;
        }
        async Task Fx(ClientState c, (int X, int Y) at, string model, uint clip, double until)
        {
            var fx = new Visual(c.NextObjectId++, at, model, clip, until); visuals.Add(fx);
            foreach (var p in Present) await CreateFx(p, fx);
        }
        static async Task CreateFx(ClientState p, Visual fx)
        {
            var obj = new EffectObject(fx.At.X, fx.At.Y, fx.Model);
            await SendWorldCreate(p, fx.Id, () => InstanceEntities.CreateObject(fx.Id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), obj.Blob()));
            await SendWorldRecord(p, fx.Id, () => InstanceProtocol.EnterWakeZone(fx.Id));
            await SendWorldRecord(p, fx.Id, () => InstanceProtocol.AnimationUpdate(fx.Id, 0, fx.Clip, 1));
        }
        public void Stop()
        {
            stopped = true;
            foreach (var p in Present.ToArray())
            {
                _ = Leaving(p);
                foreach (var fx in visuals) _ = SendWorldRemoval(p.Wire.Remote, p.Wire.Stream, p.Wire.Ciphers, p, fx.Id);
            }
        }
        static double Distance((int X, int Y) a, (int X, int Y) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        internal static double LineDistance((int X, int Y) p, (int X, int Y) a, (int X, int Y) b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, length = dx * dx + dy * dy;
            double t = length == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length, 0, 1);
            return Math.Sqrt(Math.Pow(p.X - a.X - t * dx, 2) + Math.Pow(p.Y - a.Y - t * dy, 2));
        }
    }
}
