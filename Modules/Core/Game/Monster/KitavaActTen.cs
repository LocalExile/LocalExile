using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>The Feeding Trough campaign encounter; map variants have a different script.</summary>
static partial class KitavaActTen
{
    internal const string Area = "2_10_6", Root = "Metadata/Monsters/KitavaBoss/", Body = Root + "KitavaFinal",
        Heart = Root + "KitavaFinalHeart", Sin = Root + "InnocenceSin/Sin", Innocence = Root + "InnocenceSin/Innocence";
    const string PreSin = "Metadata/NPC/Act10/SinTrough", PreInnocence = "Metadata/NPC/Act10/InnocenceTrough";
    internal const string Started = "SinPreKitavaTalk", Talked = "SinOnKitavaDead";
    static readonly Store<InstanceState, Fight> Fights = new();
    static readonly RuleTable<AreaLayout, Anchor> Anchors = new();
    internal sealed record Anchor(int X, int Y, float Rotation);
    internal static bool IsBody(MonsterCatalog.Variety? v) => v?.Id == Body;
    internal static bool IsActor(MonsterCatalog.Variety? v) => v?.Id is Body or Heart or Sin or Innocence;
    internal static LifePhaseGate? Phases(MonsterCatalog.Variety v) => v.Id is Body or Heart ? new([66, 33, 1]) : null;
    internal const string Epilogue = "2_11_endgame_town", Docks = "2_10_town",
        PortalType = "Metadata/QuestObjects/Act10/EpiloguePortal";
    internal const uint EpilogueLabel = 410179532; // ClientStrings.EpilogueActName2; kept for reference.

    // Retail 3.29 (Option A): Sin's post-Kitava portal routes back to Oriath Docks (2_10_town)
    // so players claim Lani's reward, finish town talks, and sail with Lilly (A11q0) to Karui Shores.
    internal static AreaTransitionObject PortalFor(Player c, AreaTransitionObject portal) =>
        c.Layout.Id == Area && portal.SourceType == PortalType && AreaTransitionCatalog.HashOf(Docks) is { } destination
            ? portal with { DestinationHash16 = destination, Label = null } : portal;

    // Keep Karui Shores shore-entrance mapping for direct epilogue arrival helper compatibility
    // (no-op when PortalFor routes EpiloguePortal to Oriath Docks 2_10_town).
    internal static ushort? ArrivalSource(string area, ushort destination) =>
        area == Area && destination == AreaTransitionCatalog.HashOf(Epilogue)
            ? AreaTransitionCatalog.HashOf(Docks) : null;

    // Replace only this connection's exit so cached layouts and already-open arenas behave alike after a module reload.
    internal static IReadOnlyList<uint> ConfigurePortal(Player c)
    {
        if (c.Layout.Id != Area) return [];
        List<uint> changed = [];
        foreach (var (objectId, portal) in c.Layout.AreaTransitions)
        {
            var routed = PortalFor(c, portal);
            if (routed == portal) continue;
            c.ReplaceAreaTransition(objectId, routed);
            changed.Add(objectId);
        }
        return changed;
    }

    static async Task RefreshPortal(Player c)
    {
        var changed = ConfigurePortal(c);
        foreach (uint objectId in changed)
        {
            bool shown;
            lock (c.Connection.ShownTransitions) shown = c.Connection.ShownTransitions.Remove(objectId);
            if (shown)
            {
                await SendWorldRemoval(c, objectId, "Kitava portal now leads to Oriath Docks");
            }
        }
        if (changed.Count > 0)
        {
            await UpdateShownTransitions(c, "Kitava Oriath Docks portal");
        }
    }
    static string Flag(string name) => Quests.InstanceFlag("KitavaAct10-" + name);
    internal static bool PreFightNpcShown(Player c, string type) => c.Layout.Id != Area || type is not (PreSin or PreInnocence)
        || !c.Instance.HasState(Flag("Started"));
    internal static bool? GateShown(Player c, AreaTransitionObject t)
    {
        if (c.Layout.Id != Area || t.SourceType != PortalType) return null;
        return c.Instance.HasState(Flag("Won")) && c.Character.HasQuestFlag(Talked);
    }
    static Anchor AnchorOf(Player c, (int X, int Y) fallback) => Anchors.GetValue(c.Layout, _ =>
    {
        if (c.LaterFormPoints.TryGetValue(Body, out var supplied)) return new(supplied.X, supplied.Y, 0);
        var at = AreaPopulation.LoadPoints(Area, c.Seed, []).FirstOrDefault(p => p.Name == "force:mapboss");
        return at is null ? new(fallback.X, fallback.Y, 0) : new(at.X, at.Y, at.Angle);
    });
    public static async Task Tick(Player c)
    {
        if (c.Layout.Id != Area || !CurrentGameWork || !c.Connection.PlayerSent || c.Closed || c.LeavingArea || c.Paused) return;
        await RefreshPortal(c);
        if (!Fights.TryGetValue(c.Instance, out var f))
        {
            var found = c.Area.Monsters.FirstOrDefault(m => IsBody(m.Value.Variety));
            if (found.Value is null) return;
            f = Fights.GetValue(c.Instance, _ => new(found.Key, found.Value, c));
        }
        await f.Gate.WaitAsync();
        try { await f.Bind(c); if (IsMonsterRunner(c)) await f.Step(c, AreaTime(c)); }
        finally { f.Gate.Release(); }
    }
    public static Task Leaving(Player c)
    {
        if (Exposures.TryGetValue(c, out var e)) { c.Degens[e] = 0; e.Dps = 0; }
        return Task.CompletedTask;
    }
    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose() { foreach (var f in Fights) f.Value.Stop(); foreach (var e in Exposures) _ = Leaving(e.Key); }
    }
    internal sealed partial class Fight
    {
        internal readonly SemaphoreSlim Gate = new(1);
        readonly List<Player> players = [];
        readonly HashSet<Player> bound = [];
        readonly HashSet<uint> guards = [];
        readonly uint id;
        readonly SpawnedMonster body;
        readonly GameArea area;
        readonly Anchor anchor;
        readonly (int X, int Y) heartAt, sinAt, innocenceAt;
        uint heartId, sinId, innocenceId;
        SpawnedMonster? heart;
        double deadline, ready, nextWave, nextAlly, guardAt;
        bool initialized, stopped;
        int stage, guardStep;
        internal int Stage => stage;
        internal int Round => body.LifePhases!.Completed;
        // 0 waiting, 1 emerge, 2 body, 3 expose, 4 heart, 5 recover, 6 grab Sin, 7 grab Innocence,
        // 8 release, 9 death, 10 complete. The original fight has three body/heart intervals.
        public Fight(uint objectId, SpawnedMonster monster, Player c)
        {
            id = objectId; body = monster; area = c.Area; body.LifePhases ??= Phases(monster.Variety!);
            anchor = AnchorOf(c, body.Walk.Position(AreaTime(c)));
            heartAt = c.Layout.Near((anchor.X, anchor.Y - 48));
            (int X, int Y) AllyPoint(string type, int x) => c.Layout.Level?.RoomObjects.FirstOrDefault(p => p.Type == type) is { } p
                ? (p.X, p.Y) : c.Layout.Near((anchor.X + x, anchor.Y - 70));
            sinAt = AllyPoint(Sin, -25); innocenceAt = AllyPoint(Innocence, 33);
            foreach (var (key, m) in c.Area.Monsters)
            {
                if (m.SummonerId == id && m.Hostile && !IsActor(m.Variety) && !m.Dead) guards.Add(key);
                if (m.Variety?.Id == Heart && (!m.Dead || c.Instance.HasState(Flag("Dying")))) { heartId = key; heart = m; }
                if (m.Variety?.Id == Sin) sinId = key;
                if (m.Variety?.Id == Innocence) innocenceId = key;
            }
            if (c.Instance.HasState(Flag("Won"))) stage = 10;
            else if (c.Instance.HasState(Flag("Dying"))) stage = 9;
            else if (body.LifePhases!.Active) stage = heart?.LifePhases?.Active == true ? Round == 2 ? 8 : 5 : heart is null ? 3 : 4;
            else if (c.Instance.HasState(Flag("Started"))) stage = Round == 2 && !c.Instance.HasState(Flag("Grabbed")) ? 6 : 2;
            while (c.Instance.HasState(Flag($"Guard-{Round}-{guardStep}"))) guardStep++;
            SyncInvulnerability(c);
        }
        void SyncInvulnerability(Player c)
        {
            bool bodyInvuln = stage is 0 or 1 or 3 or 4 or 5 or 6 or 7 or 8;
            CampaignBossHelpers.SetInvulnerable(c, id, body, bodyInvuln);
            if (heart is not null && heartId != 0)
                CampaignBossHelpers.SetInvulnerable(c, heartId, heart, stage != 4);
        }
        IEnumerable<Player> Present => players.Where(p => !p.Closed && !p.LeavingArea);
        bool Awake(Player p) => MonsterShown(p, id) && !MonsterAsleep(p, id);
        public async Task Bind(Player c)
        {
            players.RemoveAll(p => p.Closed || p.LeavingArea);
            if (!players.Contains(c)) players.Add(c);
            if (bound.Add(c) && Awake(c))
            {
                if (SkillsFor(c, body.Variety!, body.Level) is { } skills)
                { await SendWorldRecord(c, id, InstanceEntities.PlayerSkillsChange(id, skills, []), "[Kitava A10] native skill roster"); }
                foreach (uint ally in new[] { sinId, innocenceId }.Where(ally => ally != 0))
                {
                    KeepAllyVisible(c, ally);
                    if (MonsterShown(c, ally) && c.Area.Monsters.TryGetValue(ally, out var m)
                        && m.Variety!.ComponentsOrBase.ToList().IndexOf("Transitionable") is int component and >= 0)
                    { await SendWorldRecord(c, ally,
                        InstanceEntities.TransitionableUpdate(ally, (byte)component, 2), null); }
                }
                if (stage == 10) RegisterSin(c);
            }
        }
        Task All(uint target, byte[] bytes, bool actor = false) => All(target, new[] { bytes }, actor);
        async Task All(uint target, byte[][] records, bool actor = false)
        {
            if (Present.FirstOrDefault(p => !actor || Awake(p)) is { } p)
            {
                await SendWorldRecords(p, target, () => records.Select(r => r.ToArray()).ToArray(), null,
                    still: () => !stopped && CurrentGameWork && !p.Closed && !p.LeavingArea);
            }
        }
        async Task AllRemoval(uint target)
        {
            if (Present.FirstOrDefault() is { } p)
                await SendWorldRemoval(p, target);
        }
        async Task Basic(Player c, uint actor, ushort action, double now, double seconds, uint? target = null)
        {
            if (c.Area.Monsters.TryGetValue(actor, out var m)) { m.ActionGeneration++; m.SwingGeneration = null; m.BusyUntil = now + seconds; }
            var from = actor == id ? (anchor.X, anchor.Y) : actor == sinId ? sinAt : innocenceAt;
            await All(actor, [InstanceProtocol.ActionStarted(actor, from.Item1, from.Item2, anchor.X, anchor.Y, action, c.NextActionSequence(), 0x0408, targetObjectId: target), InstanceProtocol.ActionEnded(actor)], true);
        }
        async Task Bar(bool shown)
        {
            body.BossBarShown = shown;
            if (body.States is not { } states) return;
            body.States = states.Set("boss_life_bar", shown ? 1 : 0).Set("disable_life_bar", shown ? 0 : 1);
            var present = Present.ToArray();
            if (present.FirstOrDefault() is { } first && first.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                first.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { StateMachine = body.States } };
            if (present.FirstOrDefault(Awake) is { } awake
                && body.Variety!.ComponentsOrBase.ToList().IndexOf("StateMachine") is int component and >= 0)
            {
                await SendWorldRecord(awake, id, InstanceEntities.StateMachineUpdate(id, (byte)component, body.States), null);
            }
            foreach (var p in present)
            {
                if (shown) BossBars(p).Add(id); else BossBars(p).Remove(id);
            }
        }
        async Task<uint> Ally(Player c, string type, (int X, int Y) at)
        {
            var v = Monsters.Find(type) ?? throw new InvalidOperationException("Missing Kitava ally");
            uint obj = await SpawnMonster(c, v, body.Level, MonsterRarity.Unique,
                at.X, at.Y, friendly: true, rotation: anchor.Rotation, scripted: true, instanceOwned: true);
            if (obj == 0) throw new InvalidOperationException("Unsupported Kitava ally");
            var m = c.Area.Monsters[obj]; m.SummonerId = id; m.EntranceGrace = true;
            return obj;
        }
        async Task Allies(Player c, double now)
        {
            if (sinId == 0) sinId = await Ally(c, Sin, sinAt);
            if (innocenceId == 0) innocenceId = await Ally(c, Innocence, innocenceAt);
            foreach (uint obj in new[] { sinId, innocenceId })
            {
                KeepAllyVisible(c, obj);
                var m = c.Area.Monsters[obj]; int index = m.Variety!.ComponentsOrBase.ToList().IndexOf("Transitionable");
                if (index >= 0) await All(obj, InstanceEntities.TransitionableUpdate(obj, (byte)index, 2));
                await Basic(c, obj, 16641, now, 4.833, id);
            }
        }
        static void KeepAllyVisible(Player c, uint id)
        {
            if (c.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                c.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { TransitionState = 2 } };
        }
        public async Task Step(Player c, double now)
        {
            if (stopped || !CurrentGameWork || c.Paused) return;
            await CombatMaintenance(c, now);
            if (stage == 10) { foreach (var p in Present) RegisterSin(p); return; }
            var target = Present.FirstOrDefault(p => p.PlayerLife > 0 && !p.Paused && Awake(p) && Distance(p.Walk.Position(now), heartAt) < 170);
            if (target is null) return;
            if (!initialized)
            {
                initialized = true;
                if (stage == 0)
                {
                    if (!Present.Any(p => p.Character.HasQuestFlag(Started))) { initialized = false; return; }
                    c.Instance.SetState(Flag("Started")); body.EntranceGrace = true;
                    foreach (var p in Present) { await UpdateQuestNpcs(p, "Kitava fight begins"); }
                    await Allies(c, now); await Bar(false); await Basic(c, id, 16641, now, 4.833);
                    stage = 1; deadline = now + 4.833;
                    SyncInvulnerability(c);
                    Console.WriteLine("[Kitava A10] encounter begins"); return;
                }
                SyncInvulnerability(c);
                if (stage is 5 or 8) deadline = now + (stage == 5 ? 6 : 5);
                if (stage == 6) { await Basic(c, id, 38927, now, 5, sinId); deadline = now + 5; }
                if (stage == 9) deadline = now + 16.8;
                if (stage == 3) { await Expose(c, now); return; }
                if (stage == 4) { guardAt = now + 4; nextWave = now; }
            }
            if (stage == 1 && now >= deadline)
            {
                stage = 2; body.EntranceGrace = false; ready = now;
                SyncInvulnerability(c);
                await Bar(true);
            }
            if (stage == 2)
            {
                if (body.LifePhases!.Begin(body.Life, body.MaxLife)) { await Expose(c, now); return; }
                await Combat(target, now); return;
            }
            if (stage == 3 && now >= deadline)
            {
                if (heart is null)
                {
                    heartId = await SpawnMonster(c, Monsters.Find(Heart)!, body.Level,
                        MonsterRarity.Unique, heartAt.X, heartAt.Y, rotation: anchor.Rotation, scripted: true);
                    if (heartId == 0) throw new InvalidOperationException("Unsupported final Kitava heart");
                    heart = c.Area.Monsters[heartId]; heart.SummonerId = id; heart.LifePhases ??= Phases(heart.Variety!);
                    for (int i = 0; i < Round; i++) { heart.Life = (uint)((ulong)heart.MaxLife * (uint)heart.LifePhases!.NextPercent / 100); heart.LifePhases.Begin(heart.Life, heart.MaxLife); heart.LifePhases.Finish(); }
                }
                heart.EntranceGrace = false; stage = 4; nextWave = now; guardAt = now; guardStep = 0;
                SyncInvulnerability(c);
                while (c.Instance.HasState(Flag($"Guard-{Round}-{guardStep}"))) guardStep++;
                await All(heartId, [InstanceProtocol.ActionStarted(heartId, heartAt.X, heartAt.Y, heartAt.X, heartAt.Y, 16641, c.NextActionSequence(), 0x0408), InstanceProtocol.ActionEnded(heartId)]);
                Console.WriteLine($"[Kitava A10] heart interval {Round + 1}");
            }
            if (stage == 4)
            {
                if (heart!.LifePhases!.Begin(heart.Life, heart.MaxLife))
                {
                    heart.EntranceGrace = true; await RetireGuards(c); CancelCombat();
                    if (Round == 2)
                    {
                        await Basic(c, id, 59397, now, 5); stage = 8; deadline = now + 5;
                        SyncInvulnerability(c);
                        Console.WriteLine("[Kitava A10] final heart defeated; releasing allies"); return;
                    }
                    await Basic(c, id, 5831, now, 6); stage = 5; deadline = now + 6;
                    SyncInvulnerability(c);
                    return;
                }
                if (now >= nextWave) await Wave(c, now);
                if (now >= guardAt && guardStep < (Round == 2 ? 3 : 2))
                {
                    string kind = guardStep == 0 ? "KitavaMinionBoss2" : guardStep == 1 ? "KitavaMinionBoss1" : "KitavaMinionBoss6";
                    await Guard(c, Root + kind, guardStep, MonsterRarity.Unique);
                    c.Instance.SetState(Flag($"Guard-{Round}-{guardStep}")); guardStep++; guardAt = now + 8;
                }
                return;
            }
            if (stage == 5 && now >= deadline)
            {
                await RemoveHeart(c);
                body.LifePhases!.Finish();
                if (Round == 2 && !c.Instance.HasState(Flag("Grabbed")))
                {
                    await Basic(c, id, 38927, now, 5, sinId); stage = 6; deadline = now + 5;
                    SyncInvulnerability(c);
                    return;
                }
                stage = 2; body.EntranceGrace = false; ready = now + 1;
                SyncInvulnerability(c);
                await Bar(true);
                Console.WriteLine($"[Kitava A10] body interval {Round + 1}"); return;
            }
            if (stage == 6 && now >= deadline)
            {
                await Basic(c, id, 39515, now, 5, innocenceId); stage = 7; deadline = now + 5;
                SyncInvulnerability(c);
                return;
            }
            if (stage == 7 && now >= deadline)
            {
                c.Instance.SetState(Flag("Grabbed")); stage = 2; body.EntranceGrace = false; ready = now + 1;
                SyncInvulnerability(c);
                await Bar(true);
                Console.WriteLine("[Kitava A10] both allies captured; final body interval"); return;
            }
            if (stage == 8 && now >= deadline)
            {
                c.Instance.SetState(Flag("Dying"));
                foreach (uint ally in new[] { sinId, innocenceId }) await Basic(c, ally, 5775, now, 16.8, id);
                CampaignBossHelpers.SetInvulnerable(c, id, body, false);
                body.Life = 0; body.Dead = true; body.ActionGeneration++;
                if (heart is not null)
                {
                    if (heartId != 0) CampaignBossHelpers.SetInvulnerable(c, heartId, heart, false);
                    heart.Life = 0; heart.Dead = true;
                }
                await Bar(false);
                stage = 9; deadline = now + 16.8;
                Console.WriteLine("[Kitava A10] dark ember and native death sequence"); return;
            }
            if (stage == 9 && now >= deadline)
            {
                c.Instance.SetState(Flag("Won")); stage = 10;
                if (heart is not null && c.Instance.SetState(Flag("Rewards")))
                    await MonsterKilled(c, heartId, heart);
                await RemoveHeart(c);
                // Broadcast Sin's dialogue object transformation ONCE for the whole instance.
                await ReadySin(c);
                // Dead in-arena party members also earn EndGameAvailable.
                foreach (var p in Present.Where(p => Distance(p.Walk.Position(now), heartAt) < 180).ToArray())
                {
                    await Leaving(p);
                    await SetQuestFlags(p, [Quests.EndGameAvailable]);
                    RegisterSin(p);
                    await UpdateShownTransitions(p, "Kitava defeated");
                }
                Console.WriteLine("[Kitava A10] victory; Sin can open the Oriath Docks portal");
            }
        }
        async Task Expose(Player c, double now)
        {
            CancelCombat(); body.EntranceGrace = true; await Bar(false);
            foreach (uint ally in new[] { sinId, innocenceId }) if (ally != 0) await Basic(c, ally, 9281, now, 5, id);
            await Basic(c, id, 5828, now, 5); stage = 3; deadline = now + 5;
            SyncInvulnerability(c);
        }
        async Task Guard(Player c, string type, int ordinal, MonsterRarity rarity)
        {
            var v = Monsters.Find(type); if (v is not { Supported: true } || v.Id != type) throw new InvalidOperationException($"Unsupported Kitava guard {type}");
            double a = ordinal * 2.1;
            var at = c.Layout.Near((heartAt.X + (int)(Math.Cos(a) * 23), heartAt.Y + (int)(Math.Sin(a) * 23)));
            await SpawnMonster(c, v, body.Level, rarity, at.X, at.Y,
                instanceOwned: true,
                owned: (obj, m) => { m.SummonerId = id; m.NoRewards = true; m.Aggro = true; guards.Add(obj); },
                ownedCurrent: () => !stopped);
        }
        async Task Wave(Player c, double now)
        {
            if (guards.Count(g => c.Area.Monsters.TryGetValue(g, out var m) && !m.Dead) < 14)
                for (int i = 0; i < 6; i++)
                    await Guard(c, (i % 3) switch
                    {
                        0 => "Metadata/Monster/KitavaDemon/KitavaDemon",
                        1 => "Metadata/Monsters/RaisedSkeletons/RaisedSkeletonKitavaCaster",
                        _ => "Metadata/Monsters/RaisedSkeletons/KitavaSkeleton",
                    }, i, MonsterRarity.Normal);
            nextWave = now + 8;
        }
        async Task RetireGuards(Player c)
        {
            foreach (uint obj in guards.ToArray())
            {
                if (c.Area.Monsters.Remove(obj, out var m)) { m.Dead = true; m.Life = 0; m.NoRewards = true; m.ActionGeneration++; c.Instance.World.MonsterOrigins.TryRemove(obj, out _); }
                await AllRemoval(obj);
                foreach (var p in Present) { BossBars(p).Remove(obj); }
            }
            guards.Clear();
        }
        async Task RemoveHeart(Player c)
        {
            if (heartId == 0) return;
            if (c.Area.Monsters.Remove(heartId, out var h))
                CampaignBossHelpers.ClearInvulnerableOnUnload(c.Area, heartId, h);
            c.Instance.World.MonsterOrigins.TryRemove(heartId, out _);
            await AllRemoval(heartId);
            foreach (var p in Present) { BossBars(p).Remove(heartId); }
            heart = null; heartId = 0;
        }
        void RegisterSin(Player p)
        {
            if (sinId != 0 && Npcs.Find(Sin) is { } npc)
                lock (p.Npcs) p.Npcs.TryAdd(sinId, new(npc, sinAt.X, sinAt.Y));
        }
        async Task ReadySin(Player runner)
        {
            if (sinId == 0 || !runner.Instance.World.MonsterOrigins.TryGetValue(sinId, out var origin)) return;
            var states = origin.Object.StateMachine?.Set("finished", 1);
            var obj = origin.Object with { StateMachine = states, HasDialogue = true, TransitionState = 2 };
            runner.Instance.World.MonsterOrigins[sinId] = origin with { Object = obj };
            if (runner.Area.Monsters.TryGetValue(sinId, out var m)) m.States = states;
            await SendWorldRemoval(runner, sinId);
            await SendWorldCreate(runner, sinId, () => InstanceEntities.CreateObject(sinId, origin.Type, obj.Blob()),
                "[Kitava A10] Sin's post-fight dialogue", wake: true);
        }
        public void Stop()
        {
            stopped = true;
            CampaignBossHelpers.ClearInvulnerableOnUnload(area, id, body);
            if (heart is not null && heartId != 0)
                CampaignBossHelpers.ClearInvulnerableOnUnload(area, heartId, heart);
            foreach (var p in Present.ToArray()) _ = Leaving(p);
            // The effects go once, for every client (the area's records).
            if (Present.FirstOrDefault() is { } any)
                foreach (var fx in visuals) _ = SendWorldRemoval(any, fx.Id);
        }
    }
    static double Distance((int X, int Y) a, (int X, int Y) b) => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));
}
