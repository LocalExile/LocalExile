using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>The campaign Arakaali controller: full combat ownership, Silk transition with invulnerability,
/// native marker-targeted corner-to-corner relocation (<c>ArakaaliHide[6885]</c> targeting created
/// <c>TeleportMarkerSerialized1..4</c> scene objects, with server-hidden fallback when no markers are placed),
/// hatching spiderling eggs (<c>5567</c>/<c>5570</c>), and slowing web pools (<c>5221</c>/<c>5222</c>).</summary>
public static class Arakaali
{
    public const string Area = "2_7_12_2", Boss = "Metadata/Monsters/VaalSpiderGod/Arakaali";
    // Distinct from ArakaaliLasers' "Arakaali/" turret prefix so SceneObjects queries never overlap.
    internal const string MarkerPrefix = "ArakaaliMarker/";
    const string EggVariety = "Metadata/Monsters/SpiderlingEgg/SpiderlingEggArakaali",
        HatchVariety = "Metadata/Monsters/SpiderPlague/SpiderArakaaliHatch";
    const string WebBurstArt = "Metadata/Effects/Spells/summon_spider_arakaalisfang/summon_poof.ao",
        BeamNovaArt = "Metadata/Effects/Spells/monsters_effects/act7/Arakaali/beam/beam_nova/beamnova.ao";
    public static readonly string SecondPhase = Quests.InstanceFlag("ArakaaliSecondPhase");
    // VaalSpiderGod/rig.amd: phase_01_transition_silk_01, index 10, 6333 ms.
    internal const uint SilkClip = 10;
    internal const double SilkSeconds = 6.333, HideSeconds = 1.4, EggHatchSeconds = 4.8;
    // Actor stances are zero-based ACT ordinals; this actor's named stance2 follows its base stance.
    internal const byte SecondStance = 1;
    // Native BuffDefinitions row 842: spider_god_spiders ("Covered in Webs", Stat 179 movement velocity).
    const ushort WebBuffRow = 842;
    static readonly IReadOnlyDictionary<uint, int> WebSlowStats =
        new Dictionary<uint, int> { [Stat.BaseMovementVelocityPlusPct] = -40 };

    const int CleaveEffect = 208,
        TendrilsCenter = 4259, TendrilsLeft = 4260, TendrilsRight = 4261,
        MortarEffect = 4438,
        WebP1Effect = 5221, WebP2Effect = 5222,
        EggP1Effect = 5567, EggP2Effect = 5570,
        ComboEffect = 6545,
        LaserCenter = 6549, LaserLeft = 6550, LaserRight = 6551,
        RiptideEffect = 6555, SuperRiptideEffect = 6557,
        HideEffect = 6885;

    // ArakaaliHide[6885] (ActionType 724, vtable 0x143262830): OnStart (0x141b00f40) dereferences targetEntity+0x98
    // (Positioned) unchecked to copy target Rotation, OnHideContact (0x141b00f60) relocates & rotates the actor, and
    // OnAnimEnd (0x141b01000) plays Emerge. Only cast with a valid, client-bound TeleportMarkerSerialized1..4 id.
    // Silk transition variants 6886/6887 remain excluded (Silk transition uses SilkClip directly).
    static readonly int[] Effects =
    [
        CleaveEffect, TendrilsCenter, TendrilsLeft, TendrilsRight, MortarEffect,
        WebP1Effect, WebP2Effect, EggP1Effect, EggP2Effect,
        ComboEffect, LaserCenter, LaserLeft, LaserRight,
        RiptideEffect, SuperRiptideEffect, HideEffect,
    ];

    static ushort Skill(int effect) => (ushort)(0x4000 + Array.IndexOf(Effects, effect));
    static readonly Store<InstanceState, Fight> Fights = new();

    public static bool IsBoss(MonsterCatalog.Variety? variety) => variety?.Id == Boss;
    public static byte Stance(Player c, MonsterCatalog.Variety v) =>
        IsBoss(v) && c.Instance.HasState(SecondPhase) ? SecondStance : (byte)0;

    public static LifePhaseGate? Phases(Player c, MonsterCatalog.Variety v)
    {
        if (!IsBoss(v) || c.Layout.Id != Area) return null;
        var phases = new LifePhaseGate([50]);
        if (c.Instance.HasState(SecondPhase)) { phases.Begin(0, 1); phases.Finish(); }
        return phases;
    }

    public static bool Allowed(SpawnedMonster m, int effect)
    {
        if (!IsBoss(m.Variety) || m.LifePhases is null) return true;
        bool second = m.LifePhases?.Completed > 0;
        return effect switch
        {
            6555 or 6557 or 6545 or 6549 or 6550 or 6551 or 5570 or 5222 => second,
            4259 or 4260 or 4261 or 5567 or 5221 => !second,
            _ => true,
        };
    }

    public static IReadOnlyList<GrantedSkill>? SkillsFor(Player c, MonsterCatalog.Variety v, byte level)
    {
        if (!IsBoss(v) || c.Layout.Id != Area) return null;
        var catalog = Game.SkillsFor(c);
        return [.. Effects.Select(effect =>
        {
            int rank = catalog.MaxLevelAt(effect, level);
            var atk = Attack(c, v, level, effect);
            return catalog.FindByRow(Skill(effect), effect, rank, MonsterActionSkillStats(c, effect, rank, atk));
        }).OfType<GrantedSkill>()];
    }

    static MonsterAttack Attack(Player c, MonsterCatalog.Variety v, byte level, int effect)
    {
        var atk = BossKits.AttackFor(c, v, level, effect, Skill(effect));
        if (atk.Damage.IsZero && effect is not (EggP1Effect or EggP2Effect or WebP1Effect or WebP2Effect or HideEffect))
        {
            var (min, max) = Monsters.BaseDamage(v, level) ?? (15, 25);
            var type = effect is TendrilsCenter or TendrilsLeft or TendrilsRight ? DamageType.Lightning
                : effect is LaserCenter or LaserLeft or LaserRight or RiptideEffect or SuperRiptideEffect ? DamageType.Chaos
                : DamageType.Physical;
            return new(Skill(effect), Math.Max(0.8, atk.Seconds), 90, DamagePacket.Of(type, min, max))
            { Spell = effect is not (CleaveEffect or ComboEffect), Scripted = true };
        }
        return atk with { Scripted = true };
    }

    // A sleeping observer keeps its Actor; waking does not recreate it from the instance snapshot.
    // Commit its stance with the action at the send gate, under the monster lock.
    internal static byte[]? StanceUpdateLocked(Player c, uint id, double now)
    {
        if (c.Closed || c.LeavingArea || MonsterAsleep(c, id)
            || !c.Area.Monsters.TryGetValue(id, out var boss) || !IsBoss(boss.Variety)
            || StancesShown(c).Contains(id)
            || !c.Instance.World.MonsterOrigins.TryGetValue(id, out var origin) || origin.Object.ActorStance != SecondStance) return null;
        var at = boss.Walk.Position(now);
        StancesShown(c).Add(id);
        return InstanceProtocol.ActionStartedNow(id, at.X, at.Y, at.X, at.Y,
            5828, c.NextActionSequence(), 0x0800, id);
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
    sealed class Cleanup : IDisposable
    {
        public void Dispose() { foreach (var fight in Fights) fight.Value.Stop(); }
    }

    sealed record EggEntry(uint Id, (int X, int Y) At, double HatchAt);
    sealed record WebPool((int X, int Y) At, double Radius, double Until);
    sealed record PendingHit(double At, int Gen, int Effect, (int X, int Y) Spot, (int X, int Y)? Origin, double Radius, bool Cone);
    sealed record CornerPoint(int X, int Y, uint MarkerId);

    internal sealed class Fight
    {
        internal readonly SemaphoreSlim Gate = new(1);
        readonly uint id;
        readonly SpawnedMonster boss;
        readonly InstanceState instance;
        readonly GameArea area;
        readonly HashSet<Player> players = [];
        readonly HashSet<Player> webAffected = [];
        readonly object webHolder = new();
        readonly List<CampaignBossHelpers.ArenaMarker> markers;
        readonly List<CornerPoint> corners = [];
        readonly List<EggEntry> eggs = [];
        readonly HashSet<uint> spiders = [];
        readonly List<WebPool> webs = [];
        readonly List<CampaignBossHelpers.TimedEffect> effects = [];
        readonly List<PendingHit> hits = [];
        double silkDeadline = double.NaN;
        double hideDeadline = double.NaN;
        (int X, int Y) hideDestination;
        bool hideUsedNative;
        double nextRelocate, nextEgg, nextWeb, nextSpecial, nextWebCheck;
        int cornerIndex, castStep;
        bool engaged, stopped, cleaned;

        public Fight(uint objectId, SpawnedMonster monster, Player owner)
        {
            id = objectId; boss = monster; instance = owner.Instance; area = owner.Area;
            var placed = CampaignBossHelpers.AreaPlacements(owner);
            List<GlyphQuest.Placement> markerPlacements = [];
            for (int n = 1; n <= 4; n++)
                markerPlacements.AddRange(placed.Of($"Metadata/MiscellaneousObjects/TeleportMarkerSerialized{n}"));
            markers = CampaignBossHelpers.RegisterSceneMarkers(instance, MarkerPrefix, markerPlacements);
            foreach (var m in markers)
                corners.Add(new(m.X, m.Y, m.Id));
            if (corners.Count < 2)
            {
                var mid = placed.Of("Metadata/Terrain/Act7/Area12Level2/Objects/ArakaaliArenaMiddle").FirstOrDefault();
                var center = mid is not null ? (mid.X, mid.Y) : boss.Walk.Position(AreaTime(owner));
                corners.Clear();
                corners.Add(new(center.X - 32, center.Y - 32, 0));
                corners.Add(new(center.X + 32, center.Y - 32, 0));
                corners.Add(new(center.X + 32, center.Y + 32, 0));
                corners.Add(new(center.X - 32, center.Y + 32, 0));
            }
            var startAt = boss.Walk.Position(AreaTime(owner));
            cornerIndex = Enumerable.Range(0, corners.Count)
                .MinBy(i => CampaignBossHelpers.Distance(startAt, (corners[i].X, corners[i].Y)));
        }

        public async Task Bind(Player c)
        {
            players.Add(c);
            if (markers.Count > 0 && MonsterShown(c, id) && !MonsterAsleep(c, id))
                await CampaignBossHelpers.BindSceneMarkers(c, markers, "[Arakaali] arena teleport marker");
        }
        IEnumerable<Player> Present => players.Where(p => !p.Closed && !p.LeavingArea && ReferenceEquals(p.Instance, instance));

        async Task All(Func<Player, byte[]?> message)
        {
            var observers = Present.Concat(InGame.Keys.Where(p => ReferenceEquals(p.Instance, instance)
                && !p.Closed && !p.LeavingArea)).Distinct().ToArray();
            if (observers.FirstOrDefault(p => MonsterShown(p, id) && !MonsterAsleep(p, id)) is { } p)
            {
                await SendWorldRecord(p, id, [], null, atGate: () =>
                    !stopped && CurrentGameWork && !p.Closed && !p.LeavingArea
                        && MonsterShown(p, id) && !MonsterAsleep(p, id) ? message(p) : null);
            }
        }

        public async Task Step(Player c, double now)
        {
            if (stopped || !CurrentGameWork || c.Paused) return;
            await CampaignBossHelpers.ExpireEffects(c, effects, now);
            webs.RemoveAll(w => now >= w.Until);

            if (boss.Dead || boss.Life == 0)
            {
                if (!cleaned)
                {
                    cleaned = true;
                    hideDeadline = double.NaN;
                    CampaignBossHelpers.SetInvulnerable(c, id, boss, false);
                    await ClearWebDebuffs();
                    await CampaignBossHelpers.ClearAllEffects(c, effects);
                    await CampaignBossHelpers.RetireMonsters(c, eggs.Select(e => e.Id).Concat(spiders));
                    eggs.Clear(); spiders.Clear(); hits.Clear(); webs.Clear();
                }
                return;
            }

            await UpdateWebDebuffs(now);
            await TickEggs(c, now);
            await ResolveHits(c, now);

            boss.LifePhases ??= Phases(c, boss.Variety!)!;
            var phases = boss.LifePhases;

            // Handle Silk 50% phase transition with SetInvulnerable.
            if (phases.Completed == 0)
            {
                if (!double.IsNaN(silkDeadline))
                {
                    if (now < silkDeadline) return;
                    c.Instance.SetState(SecondPhase);
                    boss.Life = Math.Max(boss.Life, (uint)((ulong)boss.MaxLife * 60 / 100));
                    if (c.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                        c.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { ActorStance = SecondStance } };
                    await All(p => StanceUpdateLocked(p, id, now));
                    await SendWorldRecord(c, id, InstanceProtocol.LifeUpdate(id, boss.Life), "[Arakaali] heals on consuming Silk");
                    if (stopped || !CurrentGameWork) return;
                    CampaignBossHelpers.SetInvulnerable(c, id, boss, false);
                    phases.Finish();
                    silkDeadline = double.NaN;
                    boss.BusyUntil = now + 0.5;
                    nextRelocate = now + 10;
                    nextEgg = now + 4;
                    nextWeb = now + 3;
                    Console.WriteLine("[Arakaali] second stance ready");
                    return;
                }
                if (phases.Active || phases.Begin(boss.Life, boss.MaxLife))
                {
                    if (!Present.Any(p => p.PlayerLife > 0 && MonsterShown(p, id) && !MonsterAsleep(p, id))) return;
                    boss.ActionGeneration++; boss.SwingGeneration = null;
                    boss.FlinchUntil = 0; boss.StunSerial++;
                    hits.Clear();
                    CampaignBossHelpers.SetInvulnerable(c, id, boss, true);
                    boss.BusyUntil = silkDeadline = now + SilkSeconds;
                    await All(_ => InstanceProtocol.AnimationUpdate(id, 0, SilkClip, 1));
                    Console.WriteLine("[Arakaali] consumes Silk before the second stance");
                    return;
                }
            }

            // Handle active corner-to-corner relocation.
            if (!double.IsNaN(hideDeadline))
            {
                if (now < hideDeadline) return;
                hideDeadline = double.NaN;
                boss.Walk.Teleport(hideDestination.X, hideDestination.Y, now);
                if (c.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                    c.Instance.World.MonsterOrigins[id] = origin with
                    {
                        Object = origin.Object with
                        {
                            GridX = hideDestination.X,
                            GridY = hideDestination.Y,
                            StateMachine = boss.States ?? origin.Object.StateMachine,
                        }
                    };
                if (!hideUsedNative)
                {
                    await SetWorldHidden(c, id, boss, false);
                    await CampaignBossHelpers.SpawnTimedEffect(c, effects, hideDestination, WebBurstArt, now + 1.2, scale: 1.3f);
                }
                hideUsedNative = false;
                CampaignBossHelpers.SetInvulnerable(c, id, boss, false);
                boss.BusyUntil = now + 0.6;
                Console.WriteLine($"[Arakaali] relocated to corner {hideDestination}");
                return;
            }

            var pos = boss.Walk.Position(now);
            var target = CampaignBossHelpers.NearestLivingTarget(c, pos, now, 140, id);
            if (target is null) return;
            boss.Target = target;
            boss.Aggro = true;
            if (!engaged)
            {
                engaged = true;
                nextRelocate = now + 18;
                nextEgg = now + 7;
                nextWeb = now + 5;
                nextSpecial = now + 1.5;
            }
            if (!boss.BossBarShown && boss.Variety is { BossHealthBar: true })
            {
                boss.BossBarShown = true;
                foreach (var p in Present.Where(p => MonsterShown(p, id) && !MonsterAsleep(p, id) && BossBars(p).Add(id)))
                    await ShowBossLifeBar(id, boss, p);
            }

            if (now < boss.BusyUntil || now < boss.FlinchUntil || MonsterActionFactor(boss, now) == 0) return;

            bool second = phases.Completed > 0;
            var aim = target.Walk.Position(now);
            double dist = CampaignBossHelpers.Distance(pos, aim);

            // Corner-to-corner relocation: use native ArakaaliHide[6885] targeting the created TeleportMarkerSerialized
            // scene object once bound on the area; fall back to server-hidden travel only if no placed markers exist.
            if (now >= nextRelocate && corners.Count >= 2)
            {
                nextRelocate = now + (second ? 16 : 21);
                int nextCorner = (cornerIndex + 1 + AreaRng(c).Next(corners.Count - 1)) % corners.Count;
                cornerIndex = nextCorner;
                var corner = corners[cornerIndex];
                if (corner.MarkerId != 0 && !Scenes(c).Contains(corner.MarkerId))
                    await CampaignBossHelpers.BindSceneMarkers(c, markers, "[Arakaali] arena teleport marker");
                bool nativeHide = corner.MarkerId != 0 && Scenes(c).Contains(corner.MarkerId);
                boss.ActionGeneration++; boss.SwingGeneration = null;
                boss.FlinchUntil = 0; boss.StunSerial++;
                boss.Walk.Order(pos.X, pos.Y, now);
                if (nativeHide)
                {
                    hideDestination = (corner.X, corner.Y);
                    hideUsedNative = true;
                    CampaignBossHelpers.SetInvulnerable(c, id, boss, true);
                    bool sent = await SendMonsterAction(c, id, boss, boss.ActionGeneration, () =>
                        InstanceProtocol.ActionStarted(id, pos.X, pos.Y, corner.X, corner.Y,
                            Skill(HideEffect), c.NextActionSequence(), 0x0408, targetObjectId: corner.MarkerId),
                        "[Arakaali] ArakaaliHide");
                    if (!sent)
                    {
                        hideUsedNative = false;
                        CampaignBossHelpers.SetInvulnerable(c, id, boss, false);
                        return;
                    }
                    hideDeadline = now + HideSeconds;
                    boss.BusyUntil = hideDeadline;
                    Console.WriteLine($"[Arakaali] native ArakaaliHide to corner {cornerIndex + 1} (marker 0x{corner.MarkerId:x}) at {hideDestination}");
                    return;
                }
                hideDestination = CampaignBossHelpers.WalkableNear(c, (corner.X, corner.Y));
                hideUsedNative = false;
                await CampaignBossHelpers.SpawnTimedEffect(c, effects, pos, WebBurstArt, now + 1.2, scale: 1.3f);
                CampaignBossHelpers.SetInvulnerable(c, id, boss, true);
                await SetWorldHidden(c, id, boss, true);
                hideDeadline = now + HideSeconds;
                boss.BusyUntil = hideDeadline;
                Console.WriteLine($"[Arakaali] hiding (fallback) to relocate to corner {cornerIndex + 1} at {hideDestination}");
                return;
            }

            // Hatching spiderling eggs (5567 P1 / 5570 P2).
            if (now >= nextEgg && eggs.Count < 4)
            {
                int eggEffect = second ? EggP2Effect : EggP1Effect;
                nextEgg = now + (second ? 15 : 11);
                if (await Cast(c, eggEffect, aim, now) is not null)
                {
                    int count = second ? 3 : 2;
                    for (int i = 0; i < count; i++)
                    {
                        double angle = AreaRng(c).NextDouble() * Math.Tau;
                        var spot = CampaignBossHelpers.RingSpot(c, aim, angle, 10 + AreaRng(c).Next(14));
                        uint eggId = await CampaignBossHelpers.SpawnAdd(c, EggVariety, boss.Level, spot, id,
                            entranceGrace: true, ownedCurrent: () => !stopped && !boss.Dead);
                        if (eggId != 0)
                            eggs.Add(new(eggId, spot, now + EggHatchSeconds));
                    }
                    return;
                }
            }

            // Slowing web pools (5221 P1 / 5222 P2).
            if (now >= nextWeb)
            {
                int webEffect = second ? WebP2Effect : WebP1Effect;
                nextWeb = now + (second ? 7 : 10);
                if (await Cast(c, webEffect, aim, now) is { } gen)
                {
                    int count = second ? 2 : 1;
                    for (int i = 0; i < count; i++)
                    {
                        var spot = i == 0 ? aim : CampaignBossHelpers.RingSpot(c, aim, AreaRng(c).NextDouble() * Math.Tau, 14);
                        webs.Add(new(spot, 11, now + 10));
                        await CampaignBossHelpers.SpawnTimedEffect(c, effects, spot, WebBurstArt, now + 10, scale: 1.3f);
                        hits.Add(new(now + 0.55, gen, webEffect, spot, pos, 10, false));
                    }
                    return;
                }
            }

            // Primary Phase 1 / Phase 2 combat rotation.
            if (now < nextSpecial) return;
            int step = castStep++;
            int chosen;
            if (!second)
            {
                chosen = dist <= 18 && step % 3 == 0 ? CleaveEffect
                    : step % 2 == 0 ? (step % 6 == 0 ? TendrilsLeft : step % 6 == 2 ? TendrilsRight : TendrilsCenter)
                    : MortarEffect;
            }
            else
            {
                chosen = dist <= 20 && step % 4 == 0 ? ComboEffect
                    : step % 3 == 0 ? (step % 6 == 0 ? SuperRiptideEffect : RiptideEffect)
                    : (step % 3 == 1 ? LaserCenter : AreaRng(c).Next(2) == 0 ? LaserLeft : LaserRight);
            }

            if (await Cast(c, chosen, aim, now) is { } actionGen)
            {
                var atk = Attack(c, boss.Variety!, boss.Level, chosen);
                nextSpecial = now + Math.Max(1.2, atk.Seconds * 0.8);
                if (chosen is CleaveEffect or ComboEffect)
                {
                    hits.Add(new(now + atk.Seconds * 0.45, actionGen, chosen, aim, pos, 20, true));
                }
                else if (chosen is TendrilsCenter or TendrilsLeft or TendrilsRight)
                {
                    for (int pulse = 0; pulse < 3; pulse++)
                        hits.Add(new(now + 0.55 + pulse * 0.7, actionGen, chosen, aim, pos, 46, true));
                }
                else if (chosen is LaserCenter or LaserLeft or LaserRight)
                {
                    await CampaignBossHelpers.SpawnTimedEffect(c, effects, aim, BeamNovaArt, now + atk.Seconds);
                    for (int pulse = 0; pulse < 4; pulse++)
                        hits.Add(new(now + 0.65 + pulse * 0.65, actionGen, chosen, aim, pos, 62, true));
                }
                else if (chosen is RiptideEffect or SuperRiptideEffect)
                {
                    int waves = chosen == SuperRiptideEffect ? 3 : 1;
                    for (int w = 0; w < waves; w++)
                    {
                        var burst = CampaignBossHelpers.RingSpot(c, aim, w * 2.09, w == 0 ? 0 : 12);
                        await CampaignBossHelpers.SpawnTimedEffect(c, effects, burst, BeamNovaArt, now + 1.2 + w * 0.55);
                        hits.Add(new(now + 0.6 + w * 0.55, actionGen, chosen, burst, pos, 18, false));
                    }
                }
                else if (chosen == MortarEffect)
                {
                    for (int m = 0; m < 3; m++)
                    {
                        var drop = m == 0 ? aim : CampaignBossHelpers.RingSpot(c, aim, m * Math.PI, 11);
                        await CampaignBossHelpers.SpawnTimedEffect(c, effects, drop, WebBurstArt, now + 1.2);
                        hits.Add(new(now + 0.75 + m * 0.15, actionGen, chosen, drop, pos, 11, false));
                    }
                }
            }
        }

        async Task<int?> Cast(Player c, int effect, (int X, int Y) aim, double now)
        {
            var atk = Attack(c, boss.Variety!, boss.Level, effect);
            var pos = boss.Walk.Position(now);
            boss.Walk.Order(pos.X, pos.Y, now);
            int generation = ++boss.ActionGeneration;
            boss.SwingGeneration = null;
            boss.BusyUntil = now + Math.Max(0.7, atk.Seconds) + 0.15;
            bool sent = await SendMonsterAction(c, id, boss, generation, () =>
                InstanceProtocol.ActionStarted(id, pos.X, pos.Y, aim.X, aim.Y, Skill(effect), c.NextActionSequence(), 0x0408),
                $"[Arakaali] {Game.SkillsFor(c).EffectId(effect)}");
            return sent ? generation : null;
        }

        async Task TickEggs(Player c, double now)
        {
            spiders.RemoveWhere(sid => !c.Area.Monsters.TryGetValue(sid, out var m) || !CampaignBossHelpers.Alive(m));
            for (int i = eggs.Count - 1; i >= 0; i--)
            {
                var egg = eggs[i];
                if (!c.Area.Monsters.TryGetValue(egg.Id, out var m) || !CampaignBossHelpers.Alive(m))
                {
                    eggs.RemoveAt(i);
                    continue;
                }
                if (now < egg.HatchAt) continue;
                eggs.RemoveAt(i);
                var at = m.Walk.Position(now);
                await CampaignBossHelpers.RetireMonsters(c, [egg.Id]);
                int room = Math.Max(0, 12 - spiders.Count);
                int toSpawn = Math.Min(2, room);
                for (int k = 0; k < toSpawn; k++)
                {
                    var spot = CampaignBossHelpers.RingSpot(c, at, k * Math.PI, 4);
                    await CampaignBossHelpers.SpawnAdd(c, HatchVariety, boss.Level, spot, id, spiders,
                        ownedCurrent: () => !stopped && !boss.Dead);
                }
            }
        }

        async Task ResolveHits(Player c, double now)
        {
            for (int i = hits.Count - 1; i >= 0; i--)
            {
                var h = hits[i];
                if (h.At > now) continue;
                hits.RemoveAt(i);
                if (h.Gen != boss.ActionGeneration || MonsterActionFactor(boss, now) == 0 || boss.WorldHidden) continue;
                var atk = Attack(c, boss.Variety!, boss.Level, h.Effect);
                if (atk.Damage.IsZero) continue;
                foreach (var p in Present.ToArray())
                    await ArenaHazards.Hit(p, id, boss, atk, at =>
                    {
                        if (!h.Cone || h.Origin is not { } origin)
                            return CampaignBossHelpers.Distance(at, h.Spot) <= h.Radius;
                        double d = CampaignBossHelpers.Distance(at, origin);
                        if (d > h.Radius) return false;
                        double facing = Math.Atan2(h.Spot.Y - origin.Y, h.Spot.X - origin.X);
                        double angle = Math.Atan2(at.Y - origin.Y, at.X - origin.X);
                        return Math.Abs(Math.IEEERemainder(angle - facing, Math.Tau)) <= 0.65;
                    }, () => !stopped && CampaignBossHelpers.Alive(boss));
            }
        }

        async Task UpdateWebDebuffs(double now)
        {
            if (now < nextWebCheck) return;
            nextWebCheck = now + 0.25;
            foreach (var p in Present.ToArray())
            {
                bool inWeb = p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)
                    && webs.Any(w => now < w.Until && CampaignBossHelpers.Distance(p.Walk.Position(now), w.At) <= w.Radius);
                if (inWeb)
                {
                    webAffected.Add(p);
                    await HoldPlayerDebuff(p, webHolder, WebBuffRow, id, WebSlowStats);
                }
                else if (webAffected.Remove(p))
                {
                    await ReleasePlayerDebuff(p, webHolder);
                }
            }
        }

        async Task ClearWebDebuffs()
        {
            foreach (var p in webAffected.ToArray())
                await ReleasePlayerDebuff(p, webHolder);
            webAffected.Clear();
        }

        public void Stop()
        {
            stopped = true;
            CampaignBossHelpers.ClearInvulnerableOnUnload(area, id, boss);
            foreach (var p in webAffected.ToArray())
                _ = ReleasePlayerDebuff(p, webHolder);
            webAffected.Clear();
        }
    }
}
