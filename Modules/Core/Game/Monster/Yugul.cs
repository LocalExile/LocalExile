using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>The two campaign mirror intervals and native <c>MonsterLeapSlamBehindEnemy[807]</c>;
/// map frogs have their own encounter.</summary>
public static class Yugul
{
    public const string Area = "2_8_4", Boss = "Metadata/Monsters/Frog/FrogGod/SilverFrog",
        Image = "Metadata/Monsters/Frog/FrogGod/SilverFrogImage";
    const string LeapImpactArt = "Metadata/Effects/Spells/monsters_effects/act8/Yugul_SilverFrog/silverfrogmortar_impact.ao";
    internal const ushort HideSkill = 0x4fff, LeapBehindSkill = 0x4ffe;
    internal const int HideEffect = 8455, LeapBehindEffect = 807;
    // Frog/rig.amd: jump_into 3166 ms; jump_out 1166 ms.
    internal const double HideSeconds = 3.166, RiseSeconds = 1.166;
    static readonly Store<InstanceState, Fight> Fights = new();
    static string Done(int round) => Quests.InstanceFlag($"Yugul-Mirrors-{round}");
    static string Spawned(int round, int slot) => Quests.InstanceFlag($"Yugul-Mirrors-{round}-Image-{slot}");
    public static bool IsBoss(MonsterCatalog.Variety? v) => v?.Id == Boss;
    public static LifePhaseGate? Phases(Player c, MonsterCatalog.Variety v)
    {
        if (c.Layout.Id != Area || !IsBoss(v)) return null;
        var phases = new LifePhaseGate([66, 33]);
        for (int n = 0; n < 2 && c.Instance.HasState(Done(n)); n++) { phases.Begin(0, 1); phases.Finish(); }
        return phases;
    }
    internal static IReadOnlyList<GrantedSkill> AddSkills(Player c, MonsterCatalog.Variety v, byte level,
        IReadOnlyList<GrantedSkill> skills)
    {
        if (c.Layout.Id != Area || !IsBoss(v)) return skills;
        var catalog = SkillsFor(c);
        List<GrantedSkill> extra = [.. skills];
        void Append(ushort skillId, int effect)
        {
            if (extra.Any(s => s.Id == skillId)) return;
            int rank = catalog.MaxLevelAt(effect, level);
            var stats = MonsterActionSkillStats(c, effect, rank,
                BossKits.AttackFor(c, v, level, effect, skillId));
            if (catalog.FindByRow(skillId, effect, rank, stats) is { } granted)
                extra.Add(granted);
        }
        Append(HideSkill, HideEffect);
        Append(LeapBehindSkill, LeapBehindEffect);
        return extra;
    }
    public static async Task Tick(Player c)
    {
        if (c.Layout.Id != Area || !CurrentGameWork || !c.Connection.PlayerSent || c.Closed || c.LeavingArea || c.Paused) return;
        var found = c.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && IsBoss(p.Value.Variety));
        if (found.Value is null) return;
        var fight = Fights.GetValue(c.Instance, _ => new(found.Key, found.Value, c));
        await fight.Gate.WaitAsync();
        try { await fight.Bind(c); if (IsMonsterRunner(c)) await fight.Step(c, AreaTime(c)); }
        finally { fight.Gate.Release(); }
    }
    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable { public void Dispose() { foreach (var f in Fights) f.Value.Stop(); } }
    internal sealed class Fight
    {
        internal readonly SemaphoreSlim Gate = new(1);
        readonly uint id;
        readonly SpawnedMonster boss;
        readonly InstanceState instance;
        readonly GameArea area;
        readonly YugulHazards hazards;
        readonly List<GlyphQuest.Placement> imageSpawners;
        readonly HashSet<Player> players = [];
        readonly List<CampaignBossHelpers.TimedEffect> effects = [];
        readonly List<(double At, int Gen, (int X, int Y) Spot)> leapHits = [];
        double deadline = double.NaN, nextLeap;
        bool rising, stopped;

        public Fight(uint objectId, SpawnedMonster actor, Player owner)
        {
            id = objectId; boss = actor; instance = owner.Instance; area = owner.Area;
            hazards = new(id, boss, owner);
            // Cache FrogImageSpawner placements once per fight (never per tick).
            imageSpawners = CampaignBossHelpers.AreaPlacements(owner)
                .Of("Metadata/Monsters/Frog/FrogGod/FrogImageSpawner");
            nextLeap = AreaTime(owner) + 6;
        }

        IEnumerable<Player> Present => players.Where(p => !p.Closed && !p.LeavingArea
            && ReferenceEquals(p.Instance, instance));
        public async Task Bind(Player c)
        {
            await hazards.Bind(c);
            if (!players.Add(c) || boss.LifePhases?.Active != true || double.IsNaN(deadline)) return;
            if (!rising) await Hide(c);
        }
        async Task Hide(Player c)
        {
            if (!MonsterShown(c, id) || MonsterAsleep(c, id)) return;
            var at = boss.Walk.Position(AreaTime(c));
            await SendWorldRecord(c, id,
                InstanceProtocol.ActionStartedNow(id, at.X, at.Y, at.X, at.Y, HideSkill,
                    c.NextActionSequence(), 0x0800, id), "[Yugul] dive for mirror interval");
        }
        async Task MirrorState(Player c, int value)
        {
            boss.States = boss.States!.Set("mirror_images", value);
            if (c.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                c.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { StateMachine = boss.States } };
            int component = boss.Variety!.ComponentsOrBase.ToList().IndexOf("StateMachine");
            if (component >= 0 && Present.FirstOrDefault(p => MonsterShown(p, id) && !MonsterAsleep(p, id)) is { } p)
            {
                await SendWorldRecord(p, id,
                    InstanceEntities.StateMachineUpdate(id, (byte)component, boss.States), "[Yugul] mirror state");
            }
        }
        async Task StepLeapBehind(Player c, double now)
        {
            for (int i = leapHits.Count - 1; i >= 0; i--)
            {
                var h = leapHits[i];
                if (h.At > now) continue;
                leapHits.RemoveAt(i);
                if (boss.ActionGeneration != h.Gen || boss.WorldHidden) continue;
                var atk = BossKits.AttackFor(c, boss.Variety!, boss.Level, LeapBehindEffect, LeapBehindSkill) with { Scripted = true };
                if (atk.Damage.IsZero)
                    atk = CampaignBossHelpers.PhysicalAttack(boss, 1.35, 14);
                foreach (var p in Present.ToArray())
                    await ArenaHazards.Hit(p, id, boss, atk,
                        at => CampaignBossHelpers.Distance(at, h.Spot) <= 13,
                        () => !stopped && CampaignBossHelpers.Alive(boss));
            }
            if (now < nextLeap || now < boss.BusyUntil || now < boss.FlinchUntil || MonsterActionFactor(boss, now) == 0)
                return;
            var pos = boss.Walk.Position(now);
            var target = CampaignBossHelpers.NearestLivingTarget(c, pos, now, 55, id);
            if (target is null) return;
            var aim = target.Walk.Position(now);
            double dist = CampaignBossHelpers.Distance(pos, aim);
            if (dist < 10) return;
            double angle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
            var behind = CampaignBossHelpers.RingSpot(c, aim, angle, 10);
            var atkSpec = BossKits.AttackFor(c, boss.Variety!, boss.Level, LeapBehindEffect, LeapBehindSkill);
            double duration = Math.Max(0.9, atkSpec.Seconds);
            int gen = ++boss.ActionGeneration;
            boss.SwingGeneration = null;
            boss.BusyUntil = now + duration + 0.2;
            nextLeap = now + 7.5;
            boss.Walk.Teleport(behind.X, behind.Y, now);
            await SendMonsterAction(c, id, boss, gen, () =>
                [.. InstanceProtocol.ActionStarted(id, pos.X, pos.Y, behind.X, behind.Y, LeapBehindSkill, c.NextActionSequence(), 0x0408),
                 .. InstanceProtocol.Teleport(id, behind.X, behind.Y)],
                "[Yugul] MonsterLeapSlamBehindEnemy[807]");
            await CampaignBossHelpers.SpawnTimedEffect(c, effects, behind, LeapImpactArt, now + duration + 0.5);
            leapHits.Add((now + duration * 0.6, gen, behind));
        }
        public async Task Step(Player c, double now)
        {
            await CampaignBossHelpers.ExpireEffects(c, effects, now);
            await hazards.Step(c, now);
            if (stopped || !CurrentGameWork || c.Paused || boss.Dead || boss.Life == 0)
            {
                if (boss.Dead || boss.Life == 0)
                {
                    CampaignBossHelpers.SetInvulnerable(c, id, boss, false);
                    await CampaignBossHelpers.ClearAllEffects(c, effects);
                    leapHits.Clear();
                }
                return;
            }
            boss.LifePhases ??= Phases(c, boss.Variety!)!;
            var phase = boss.LifePhases;
            if (phase.Completed < 2)
            {
                if (double.IsNaN(deadline))
                {
                    if (Present.Any(p => p.PlayerLife > 0 && (phase.Active || MonsterShown(p, id) && !MonsterAsleep(p, id)))
                        && (phase.Active || phase.Begin(boss.Life, boss.MaxLife)))
                    {
                        boss.ActionGeneration++; boss.SwingGeneration = null;
                        boss.FlinchUntil = 0; boss.StunSerial++;
                        leapHits.Clear();
                        var at = boss.Walk.Position(now); boss.Walk.Teleport(at.X, at.Y, now);
                        CampaignBossHelpers.SetInvulnerable(c, id, boss, true);
                        boss.BusyUntil = deadline = now + HideSeconds;
                        await MirrorState(c, 1);
                        if (Present.FirstOrDefault(p => MonsterShown(p, id) && !MonsterAsleep(p, id)) is { } viewer)
                            await Hide(viewer);
                        return;
                    }
                }
                else
                {
                    if (now < deadline) return;
                    if (rising)
                    {
                        c.Instance.SetState(Done(phase.Completed)); phase.Finish();
                        CampaignBossHelpers.SetInvulnerable(c, id, boss, false);
                        boss.StageRunning = false; boss.BusyUntil = now;
                        deadline = double.NaN; rising = false;
                        nextLeap = now + 4;
                        Console.WriteLine($"[Yugul] mirror interval {phase.Completed} complete");
                        return;
                    }
                    if (imageSpawners.Count == 0) return;
                    await SetWorldHidden(c, id, boss, true);
                    int round = phase.Completed;
                    foreach (var m in c.Area.Monsters.Values.Where(m => m.Variety?.Id == Image))
                    { m.SummonerId = id; m.NoRewards = true; }
                    for (int slot = 0; slot < 10; slot++)
                    {
                        if (c.Instance.HasState(Spawned(round, slot))) continue;
                        var point = imageSpawners[slot % imageSpawners.Count]; var at = c.Layout.Near((point.X, point.Y));
                        int admittedSlot = slot;
                        uint image = await SpawnMonster(c, Monsters.Find(Image)!, boss.Level,
                            MonsterRarity.Normal, at.X, at.Y, instanceOwned: true, owned: (_, m) =>
                            {
                                m.SummonerId = id; m.NoRewards = true; m.Aggro = true;
                                c.Instance.SetState(Spawned(round, admittedSlot));
                            }, ownedCurrent: () => !stopped && phase.Active && phase.Completed == round);
                        if (image == 0) return;
                    }
                    if (c.Area.Monsters.Values.Any(m => m.Variety?.Id == Image && !m.Dead && m.Life > 0)) return;
                    // Native Hide listens for reappear, dispatched by mirror_images=0 in SilverFrog.ot.
                    await MirrorState(c, 0);
                    await SetWorldHidden(c, id, boss, false);
                    var risenAt = boss.Walk.Position(now);
                    await SendWorldRecord(c, id,
                        InstanceProtocol.ActionStartedNow(id, risenAt.X, risenAt.Y, risenAt.X, risenAt.Y, 0x0101,
                            c.NextActionSequence(), 0x0800, id), "[Yugul] emerges after mirrors");
                    rising = true; deadline = now + RiseSeconds; boss.BusyUntil = deadline;
                    return;
                }
            }
            if (!phase.Active && !boss.WorldHidden)
                await StepLeapBehind(c, now);
        }
        public void Stop()
        {
            stopped = true;
            boss.StageRunning = false;
            CampaignBossHelpers.ClearInvulnerableOnUnload(area, id, boss);
            hazards.Stop();
        }
    }
}
