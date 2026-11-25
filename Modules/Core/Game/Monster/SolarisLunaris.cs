using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>The Harbour Bridge's corporeal/celestial handoff, isolated from standalone/map variants.</summary>
public static class SolarisLunaris
{
    public const string Area = "2_8_13", Root = "Metadata/Monsters/LunarisSolaris/",
        Sun = Root + "Solaris", Moon = Root + "Lunaris";
    internal const ushort TransformSkill = 0x4ffe,
        DodgeLeftSkill = 0x4ff8, DodgeRightSkill = 0x4ff9, DodgeBackSkill = 0x4ffa,
        BackflipSkill = 0x4ffb, SkyBeamSkill = 0x4ffc, CycloneSkill = 0x4ffd;
    const int DodgeLeftEffect = 4399, DodgeRightEffect = 4400, DodgeBackEffect = 4401,
        SolarisBackflipEffect = 6544, SolarisSkyBeamEffect = 6462, LunarisCycloneEffect = 2542;
    const string Art = "Metadata/Effects/Spells/monsters_effects/Solaris_and_Lunaris/";
    // Rotation cadence is a development calibration; the native OT supplies the 1.6-second entrance timer.
    internal const double RotationSeconds = 20, EntranceSeconds = 1.6, TransformSeconds = 1.8;
    static readonly string MoonActive = Quests.InstanceFlag("Sisters-MoonActive");
    static readonly Store<InstanceState, Fight> Fights = new();
    public static bool IsBody(MonsterCatalog.Variety? v) => v?.Id is Sun or Moon;
    public static bool IsCelestial(MonsterCatalog.Variety? v) => v?.Id is Root + "SolarisCelestialForm" or Root + "LunarisCelestialForm";
    public static bool Held(Player c, SpawnedMonster m) => c.Layout.Id == Area && IsBody(m.Variety) && m.EntranceGrace;
    public static byte StartState(Player c, MonsterCatalog.Variety v, byte otherwise) =>
        c.Layout.Id == Area && IsBody(v) ? (byte)1 : otherwise;
    internal static IReadOnlyList<GrantedSkill> AddSkills(Player c, MonsterCatalog.Variety v, byte level,
        IReadOnlyList<GrantedSkill> skills)
    {
        if (c.Layout.Id != Area || !IsBody(v)) return skills;
        var catalog = SkillsFor(c);
        List<GrantedSkill> extra = [.. skills];
        void Append(ushort skillId, int effect)
        {
            if (extra.Any(s => s.Id == skillId)) return;
            int rank = catalog.MaxLevelAt(effect, level);
            var stats = MonsterActionSkillStats(c, effect, rank, BossKits.AttackFor(c, v, level, effect, skillId));
            if (catalog.FindByRow(skillId, effect, rank, stats) is { } granted)
                extra.Add(granted);
        }
        bool isSun = v.Id == Sun;
        Append(TransformSkill, isSun ? 6739 : 6740);
        Append(DodgeLeftSkill, DodgeLeftEffect);
        Append(DodgeRightSkill, DodgeRightEffect);
        if (isSun)
        {
            Append(DodgeBackSkill, DodgeBackEffect);
            Append(BackflipSkill, SolarisBackflipEffect);
            Append(SkyBeamSkill, SolarisSkyBeamEffect);
        }
        else
        {
            Append(CycloneSkill, LunarisCycloneEffect);
        }
        return extra;
    }
    public static async Task Tick(Player c)
    {
        if (c.Layout.Id != Area || !CurrentGameWork || !c.Connection.PlayerSent || c.Closed || c.LeavingArea || c.Paused) return;
        if (!Fights.TryGetValue(c.Instance, out var f))
        {
            var sun = c.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == Sun);
            var moon = c.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == Moon);
            // Allow solo-survivor initialization when SavedMonster portal re-entry restores only one sister.
            if (sun.Value is null && moon.Value is null) return;
            f = Fights.GetValue(c.Instance, _ => new(sun.Key, sun.Value, moon.Key, moon.Value, c));
        }
        await f.Gate.WaitAsync();
        try { await f.Bind(c); if (IsMonsterRunner(c)) await f.Step(c, AreaTime(c)); }
        finally { f.Gate.Release(); }
    }
    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable { public void Dispose() { foreach (var f in Fights) f.Value.Stop(); } }
    internal sealed class Fight(uint sunId, SpawnedMonster? sun, uint moonId, SpawnedMonster? moon, Player owner)
    {
        internal readonly SemaphoreSlim Gate = new(1);
        readonly InstanceState instance = owner.Instance;
        readonly GameArea area = owner.Area;
        readonly HashSet<Player> players = [];
        readonly List<CampaignBossHelpers.TimedEffect> effects = [];
        readonly List<(double At, uint SourceId, SpawnedMonster Source, int Effect, ushort SkillId, (int X, int Y) Spot, double Radius, int Gen)> pendingHits = [];
        int active = owner.Instance.HasState(MoonActive) ? 1 : 0;
        bool initialized, changing, stopped, entering;
        CelestialHazards? celestial;
        double nextSwap, deadline, nextCorporeal;
        int corporealStep;
        (uint Id, SpawnedMonster? Monster) Body(int index) => index == 0 ? (sunId, sun) : (moonId, moon);
        IEnumerable<Player> Present => players.Where(p => !p.Closed && !p.LeavingArea && ReferenceEquals(p.Instance, instance));
        static bool Alive(SpawnedMonster? m) => m is { Dead: false, Life: > 0 };
        public async Task Bind(Player c) { players.Add(c); if (celestial is not null) await celestial.Bind(c); }
        async Task State(Player c, int index, byte state)
        {
            var (id, m) = Body(index);
            if (m is null || id == 0) return;
            if (c.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                c.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { TransitionState = state } };
            int component = m.Variety!.ComponentsOrBase.ToList().IndexOf("Transitionable");
            if (component >= 0 && Present.FirstOrDefault(p => MonsterShown(p, id) && !MonsterAsleep(p, id)) is { } p)
            {
                await SendWorldRecord(p, id,
                    InstanceEntities.TransitionableUpdate(id, (byte)component, state), $"[Sisters] {m.Variety.Name} state {state}");
            }
            await SetWorldHidden(c, id, m, state == 4);
        }
        static void Hold(Player c, uint id, SpawnedMonster? m, bool held, double now)
        {
            if (m is null || id == 0) return;
            m.EntranceGrace = held;
            CampaignBossHelpers.SetInvulnerable(c, id, m, held);
            if (!held) { m.BusyUntil = now; return; }
            m.ActionGeneration++; m.SwingGeneration = null; m.StunSerial++; m.FlinchUntil = 0;
            m.Leg = null;
            var at = m.Walk.Position(now); m.Walk.Teleport(at.X, at.Y, now);
        }
        async Task RemoveOrbs(Player c)
        {
            if (celestial is not null) { await celestial.Clear(c); celestial.Stop(); celestial = null; }
            uint[] ids = [.. c.Area.Monsters.Where(p => IsCelestial(p.Value.Variety)).Select(p => p.Key)];
            foreach (uint id in ids)
            {
                if (c.Area.Monsters.Remove(id, out var m))
                    CampaignBossHelpers.ClearInvulnerableOnUnload(c.Area, id, m);
                c.Instance.World.MonsterOrigins.TryRemove(id, out _);
            }
            foreach (uint id in ids) await SendWorldRemoval(c, id, "[Sisters] celestial form leaves");
        }
        async Task Orb(Player c, int index, double now)
        {
            var (_, body) = Body(index);
            if (body is null) return;
            string type = body.Variety!.Id + "CelestialForm";
            if (c.Area.Monsters.Values.Any(m => m.Variety?.Id == type && Alive(m))) return;
            var at = body.Walk.Position(now);
            uint id = await SpawnMonster(c, Monsters.Find(type)!, body.Level,
                MonsterRarity.Normal, at.X, at.Y, scripted: true, owned: (mid, m) =>
                {
                    m.NoRewards = true; m.EntranceGrace = true; m.Aggro = true;
                }, ownedCurrent: () => !stopped);
            if (id != 0 && c.Area.Monsters.TryGetValue(id, out var actor))
            {
                CampaignBossHelpers.SetInvulnerable(c, id, actor, true);
                celestial = new(id, actor, c, () => !stopped && Alive(sun) && Alive(moon), finalForm: false);
                foreach (var p in Present.ToArray()) await celestial.Bind(p);
            }
        }
        async Task StartSoloFinaleHazards(Player c)
        {
            var (id, survivor) = Body(active);
            if (survivor is null || id == 0) return;
            int other = 1 - active;
            // Activate SolarisFinalFormDaemon / LunarisFinalFormDaemon solo finale hazards in state 5.
            celestial = new(id, survivor, c, () => !stopped && Alive(survivor) && !Alive(Body(other).Monster), finalForm: true);
            foreach (var p in Present.ToArray()) await celestial.Bind(p);
            Console.WriteLine($"[Sisters] {survivor.Variety?.Name} solo finale (state 5) daemon hazards active");
        }
        async Task Activate(Player c, double now)
        {
            await RemoveOrbs(c);
            int other = 1 - active;
            bool otherAlive = Alive(Body(other).Monster);
            if (otherAlive)
            { await State(c, other, 4); await Orb(c, other, now); }
            await State(c, active, otherAlive ? (byte)2 : (byte)5);
            if (!otherAlive) await StartSoloFinaleHazards(c);
            entering = true; changing = false; deadline = now + EntranceSeconds;
            nextSwap = deadline + RotationSeconds;
            nextCorporeal = deadline + 4.5;
            if (active == 1) c.Instance.SetState(MoonActive); else c.Instance.ClearState(MoonActive);
        }
        async Task StepCorporeal(Player c, uint id, SpawnedMonster body, bool solo, double now)
        {
            if (now < nextCorporeal || now < body.BusyUntil || now < body.FlinchUntil || MonsterActionFactor(body, now) == 0)
                return;
            var pos = body.Walk.Position(now);
            var target = CampaignBossHelpers.NearestLivingTarget(c, pos, now, 110, id);
            if (target is null) return;
            var aim = target.Walk.Position(now);
            double dist = CampaignBossHelpers.Distance(pos, aim);
            bool isSun = active == 0;
            double cadence = solo ? 5.5 : 8.0;
            int step = corporealStep++ % 3;
            int effect;
            ushort skillId;
            if (isSun)
            {
                if (step == 0) { effect = SolarisSkyBeamEffect; skillId = SkyBeamSkill; }
                else if (step == 1 && dist <= 32) { effect = SolarisBackflipEffect; skillId = BackflipSkill; }
                else
                {
                    int pick = AreaRng(c).Next(3);
                    (effect, skillId) = pick == 0 ? (DodgeLeftEffect, DodgeLeftSkill)
                        : pick == 1 ? (DodgeRightEffect, DodgeRightSkill)
                        : (DodgeBackEffect, DodgeBackSkill);
                }
            }
            else
            {
                if (step != 2 && dist <= 45) { effect = LunarisCycloneEffect; skillId = CycloneSkill; }
                else
                {
                    bool left = AreaRng(c).Next(2) == 0;
                    (effect, skillId) = left ? (DodgeLeftEffect, DodgeLeftSkill) : (DodgeRightEffect, DodgeRightSkill);
                }
            }
            var attack = BossKits.AttackFor(c, body.Variety!, body.Level, effect, skillId);
            double duration = Math.Max(0.8, attack.Seconds);
            int generation = ++body.ActionGeneration;
            body.SwingGeneration = null;
            body.BusyUntil = now + duration + 0.15;
            nextCorporeal = now + cadence;
            double facing = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);

            if (effect is DodgeLeftEffect or DodgeRightEffect or DodgeBackEffect or SolarisBackflipEffect)
            {
                double offsetAngle = effect == DodgeLeftEffect ? facing + Math.PI / 2
                    : effect == DodgeRightEffect ? facing - Math.PI / 2
                    : facing + Math.PI;
                double hop = effect == SolarisBackflipEffect ? 24 : 16;
                var dest = CampaignBossHelpers.RingSpot(c, pos, offsetAngle, hop);
                body.Walk.Teleport(dest.X, dest.Y, now);
                await SendMonsterAction(c, id, body, generation, () =>
                    [.. InstanceProtocol.ActionStarted(id, pos.X, pos.Y, dest.X, dest.Y, skillId, c.NextActionSequence(), 0x0408),
                     .. InstanceProtocol.Teleport(id, dest.X, dest.Y)],
                    $"[Sisters] {body.Variety?.Name} evade {effect}");
                if (effect == SolarisBackflipEffect)
                {
                    await CampaignBossHelpers.SpawnTimedEffect(c, effects, aim, Art + "Solaris_e/solarspear_main.ao", now + 1.4);
                    pendingHits.Add((now + duration * 0.45, id, body, effect, skillId, aim, 13, generation));
                }
            }
            else if (effect == SolarisSkyBeamEffect)
            {
                body.Walk.Order(pos.X, pos.Y, now);
                await SendMonsterAction(c, id, body, generation, () =>
                    InstanceProtocol.ActionStarted(id, pos.X, pos.Y, aim.X, aim.Y, skillId, c.NextActionSequence(), 0x0408),
                    "[Sisters] Solaris sky beam");
                await CampaignBossHelpers.SpawnTimedEffect(c, effects, aim, Art + "Solaris_e/solarbeam_impact.ao", now + duration + 0.6);
                await CampaignBossHelpers.SpawnTimedEffect(c, effects, aim, Art + "Solaris_e/solarbeam_groundfire_01.ao", now + duration + 3.0);
                pendingHits.Add((now + duration * 0.55, id, body, effect, skillId, aim, 14, generation));
            }
            else if (effect == LunarisCycloneEffect)
            {
                var dest = CampaignBossHelpers.WalkableNear(c, aim);
                body.Walk.Order(dest.X, dest.Y, now);
                await SendMonsterAction(c, id, body, generation, () =>
                    InstanceProtocol.ActionStarted(id, pos.X, pos.Y, dest.X, dest.Y, skillId, c.NextActionSequence(), 0x0408),
                    "[Sisters] Lunaris cyclone");
                await CampaignBossHelpers.SpawnTimedEffect(c, effects, dest, Art + "Lunaris_e/lunaris_cyclone_new.ao", now + duration);
                var mid = ((pos.X + dest.X) / 2, (pos.Y + dest.Y) / 2);
                pendingHits.Add((now + duration * 0.35, id, body, effect, skillId, mid, 12, generation));
                pendingHits.Add((now + duration * 0.70, id, body, effect, skillId, dest, 12, generation));
            }
        }
        async Task ResolvePendingHits(Player c, double now)
        {
            for (int i = pendingHits.Count - 1; i >= 0; i--)
            {
                var h = pendingHits[i];
                if (h.At > now) continue;
                pendingHits.RemoveAt(i);
                if (!Alive(h.Source) || h.Source.ActionGeneration != h.Gen) continue;
                var atk = BossKits.AttackFor(c, h.Source.Variety!, h.Source.Level, h.Effect, h.SkillId) with { Scripted = true };
                foreach (var p in Present.ToArray())
                    await ArenaHazards.Hit(p, h.SourceId, h.Source, atk,
                        at => CampaignBossHelpers.Distance(at, h.Spot) <= h.Radius,
                        () => !stopped && Alive(h.Source));
            }
        }
        public async Task Step(Player c, double now)
        {
            if (stopped || !CurrentGameWork || c.Paused) return;
            await CampaignBossHelpers.ExpireEffects(c, effects, now);
            await ResolvePendingHits(c, now);
            if (celestial is not null) await celestial.Step(c, now);
            if (!Alive(sun) && !Alive(moon))
            {
                await RemoveOrbs(c);
                await CampaignBossHelpers.ClearAllEffects(c, effects);
                pendingHits.Clear();
                return;
            }
            if (!Present.Any(p => p.PlayerLife > 0 && ((sunId != 0 && MonsterShown(p, sunId)) || (moonId != 0 && MonsterShown(p, moonId))))) return;
            if (!initialized)
            {
                initialized = true;
                Hold(c, sunId, sun, true, now);
                Hold(c, moonId, moon, true, now);
                if (!Alive(Body(active).Monster)) active = 1 - active;
                await Activate(c, now); return;
            }
            if (!Alive(Body(active).Monster))
            {
                Hold(c, sunId, sun, true, now);
                Hold(c, moonId, moon, true, now);
                active = 1 - active; await Activate(c, now); return;
            }
            var (activeId, activeBody) = Body(active);
            if (activeBody is null) return;
            if (entering)
            {
                if (now < deadline) return;
                entering = false; Hold(c, activeId, activeBody, false, now);
            }
            if (changing)
            {
                if (now < deadline) return;
                active = 1 - active; await Activate(c, now); return;
            }
            bool otherAlive = Alive(Body(1 - active).Monster);
            await StepCorporeal(c, activeId, activeBody, !otherAlive, now);
            if (!otherAlive || now < nextSwap) return;
            // Stop admitting new AI orders while the current action finishes, or continuous attacks can starve the handoff.
            activeBody.EntranceGrace = true;
            if (now < activeBody.BusyUntil) return;
            Hold(c, activeId, activeBody, true, now); await State(c, active, 3);
            var at = activeBody.Walk.Position(now);
            if (Present.FirstOrDefault(p => MonsterShown(p, activeId) && !MonsterAsleep(p, activeId)) is { } p)
            {
                await SendWorldRecord(p, activeId,
                    InstanceProtocol.ActionStartedNow(activeId, at.X, at.Y, at.X, at.Y, TransformSkill,
                        p.NextActionSequence(), 0x0800, activeId), "[Sisters] corporeal form submerges");
            }
            changing = true; deadline = now + TransformSeconds;
        }
        public void Stop()
        {
            stopped = true;
            celestial?.Stop();
            if (sun is not null && sunId != 0) CampaignBossHelpers.ClearInvulnerableOnUnload(area, sunId, sun);
            if (moon is not null && moonId != 0) CampaignBossHelpers.ClearInvulnerableOnUnload(area, moonId, moon);
        }
    }
}
