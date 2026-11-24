using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Act9To10Encounters;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Act 9 encounter controllers for:
/// <list type="bullet">
/// <item><b>General Adus (<c>2_9_9</c> The Refinery — <c>GeneralAdus[6211]</c>)</b>: <c>LivingBombLightningAdus[4740]</c>
/// ticking detonation orbs, regular <c>QuakeSlamGeneralAdus[5473]</c> vs supercharged
/// <c>QuakeSlamGeneralAdusSupercharged[5474]</c> electric quakes, energized arena lightning conduits
/// (<c>LightningConductor</c>/<c>LightningGrid1..2</c>), and <c>Refinery_Boss_Door</c> arena gate control.</item>
/// <item><b>The Basilisk (<c>2_9_6</c> The Boiling Lake — <c>IguanaBossPhoenix[6150]</c>)</b>:
/// <c>EmptyActionSpellBasilisk[7871]</c> Petrifying Gaze cone telegraph applying Petrified freeze debuff
/// (<c>petrified[613]</c>), sweeping acid-flame breath (<c>8731/8732</c>), caustic acid pools, and awakening
/// petrified bandit guards (<c>Bandit*Petrified</c>, <c>NoRewards = true</c>).</item>
/// <item><b>Storm Blade Mummy Event (<c>2_9_3</c> The Vastiri Desert — <c>MummyEventChest</c>)</b>: Interactive
/// click-to-start chest (<c>StateMachine "open"</c> 0-&gt;1-&gt;2) launching 3 escalating mummy waves
/// (<c>GarukhanMinionSmallMummyEvent</c>/<c>Large</c>, <c>NoRewards = true</c>) before opening (<c>open=2</c>) and
/// dropping <c>StormSword</c>.</item>
/// </list>
/// </summary>
public static class GeneralAdusBasiliskEncounters
{
    public const string Refinery = "2_9_9";
    public const string BoilingLake = "2_9_6";
    public const string VastiriDesert = "2_9_3";
    public static readonly string[] Areas = [Refinery, BoilingLake, VastiriDesert];

    public const string GeneralAdus = "Metadata/Monsters/GeneralAdus/GeneralAdus";                      // [6211]
    public const string Basilisk = "Metadata/Monsters/GemMonster/IguanaBossPhoenix";                    // [6150]
    public const string MummySmall = "Metadata/Monsters/Garukhan/GarukhanMinionSmallMummyEvent";        // [6133]
    public const string MummyLarge = "Metadata/Monsters/Garukhan/GarukhanMinionLargeMummyEvent";        // [6136]
    public static readonly string[] PetrifiedBandits =
    [
        "Metadata/Monsters/Bandits/BanditSwordShieldPetrified",       // [6156]
        "Metadata/Monsters/Bandits/BanditRangedBarragePetrified",     // [6157]
        "Metadata/Monsters/Bandits/BanditRangedTornadoShotPetrified", // [6158]
    ];

    public const string RefineryBossDoorType = "Metadata/QuestObjects/Act9/Refinery_Boss_Door";
    public const string MummyEventChestType = "Metadata/QuestObjects/Act9/MummyEventChest";
    public const string StormSwordItem = "Metadata/Items/QuestItems/Act9/StormSword";
    public const string StormBeginFlag = "a9q3-BeginEncounter";
    public const string StormFinishFlag = "a9q3-FinishEncounter";

    // Verified GrantedEffects rows in client.db:
    const int AdusMelee = 406;
    const int AdusQuake = 5473;              // QuakeSlamGeneralAdus
    const int AdusQuakeSuper = 5474;         // QuakeSlamGeneralAdusSupercharged
    const int AdusLivingBomb = 4740;         // LivingBombLightningAdus
    static readonly int[] AdusEffects = [AdusMelee, AdusQuake, AdusQuakeSuper, AdusLivingBomb];

    const int BasiliskMelee = 420;           // MeleeAtAnimationSpeedUnique
    const int BasiliskSpit = 2748;           // IguanaProjectileBasilisk
    const int BasiliskGaze = 7871;           // EmptyActionSpellBasilisk
    const int BasiliskFlameLR = 8731;        // IguanaFlamethrowerLeftToRight
    const int BasiliskFlameRL = 8732;        // IguanaFlamethrowerRightToLeft
    static readonly int[] BasiliskEffects = [BasiliskMelee, BasiliskSpit, BasiliskGaze, BasiliskFlameLR, BasiliskFlameRL];

    // Verified BuffDefinitions rows in client.db:
    const ushort SuperchargedBuff = 911;     // general_adus_supercharged
    const ushort PetrifiedBuff = 613;        // petrified
    const ushort StoneGazeSlowBuff = 903;    // basilisk_slow ("Stone Gaze")
    const ushort CausticCloudBuff = 484;     // caustic_cloud

    // Verified .ao visual paths in game-files.zip:
    const string AdusBeamArt = "Metadata/Effects/Spells/monsters_effects/general_adus/generaladusbuffbeam.ao";
    const string AdusImpactArt = "Metadata/Effects/Spells/monsters_effects/general_adus/generaladusbuffimpact.ao";
    const string ShockNovaArt = "Metadata/Effects/Spells/shock_nova/new/shock_nova.ao";
    const string StoneGazeArt = "Metadata/Effects/Spells/monsters_effects/atlasofworldsbosses/petrification_statue/on_act/stonegaze_start_01.ao";
    const string FireSpineArt = "Metadata/Effects/Spells/monsters_effects/act9/basilisk/fire_spine.ao";
    const string BanditBreakoutArt = "Metadata/Effects/Spells/monsters_effects/act9/basilisk/banditbreakout.ao";
    const string CausticPoolArt = "Metadata/Effects/Spells/ground_effects/caustic/caustic_02.ao";
    const string SwirlingSandArt = "Metadata/Effects/Spells/monsters_effects/act9/mummyevent/swirlingsand.ao";
    const string ReleaseSandArt = "Metadata/Effects/Spells/monsters_effects/act9/mummyevent/release_sand.ao";

    static readonly IReadOnlyDictionary<uint, int> AdusSuperchargedStats = new Dictionary<uint, int>
    {
        [Stat.AttackSpeedPlusPct] = 25,
        [Stat.BaseMovementVelocityPlusPct] = 20,
    };

    static readonly IReadOnlyDictionary<uint, int> PetrifiedFreezeStats = new Dictionary<uint, int>
    {
        [Stat.ActionSpeedMinusPct] = 90,
        [Stat.BaseMovementVelocityPlusPct] = -100,
    };

    static readonly IReadOnlyDictionary<uint, int> StoneGazeSlowStats = new Dictionary<uint, int>
    {
        [Stat.BaseMovementVelocityPlusPct] = -35,
    };

    static readonly RuleTable<InstanceState, AreaState> States = new();

    public static async Task OnPlayerEntered(Player player)
    {
        if (player.Layout.Id != VastiriDesert || !CurrentGameWork || player.Closed || player.LeavingArea) return;
        var state = States.GetValue(player.Instance, _ => new AreaState(player));
        await state.Gate.WaitAsync();
        try { await state.EnsureMummyChest(player); }
        finally { state.Gate.Release(); }
    }

    public static async Task<bool> Use(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (player.Layout.Id != VastiriDesert || !States.TryGetValue(player.Instance, out var state)
            || state.ChestId == 0 || use.TargetId != state.ChestId)
            return false;
        var at = state.ChestAt;
        if (!TryBeginObjectWalk(player, requestedAt, at.X, at.Y, 6, out var walk)) return true;
        var (action, now, here, stop, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, at.X, at.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
                null, still: () => PlayerActionCurrent(player, action));
        if (arrive > now) await AreaDelay(player, arrive - now);
        if (!PlayerActionCurrent(player, action)) return true;
        try
        {
            if (!PlayerWalk.InReach(stop, at, 8, player.Layout.Walkable)) return true;
            await state.Gate.WaitAsync();
            try { await state.BeginMummyEvent(player, AreaTime(player)); }
            finally { state.Gate.Release(); }
        }
        finally { await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null); }
        return true;
    }

    public static async Task Tick(Player player)
    {
        if (!CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        string area = player.Layout.Id;
        if (area is not (Refinery or BoilingLake or VastiriDesert)) return;
        var state = States.GetValue(player.Instance, _ => new AreaState(player));
        await state.Gate.WaitAsync();
        try
        {
            if (area == VastiriDesert)
                await state.EnsureMummyChest(player);
            if (area == Refinery)
                SyncGatesForPlayer(player, state.Gates);
            if (IsMonsterRunner(player))
                await state.Step(player, AreaTime(player));
        }
        finally { state.Gate.Release(); }
    }

    public static async Task Leaving(Player player)
    {
        if (player.Layout.Id is not (Refinery or BoilingLake or VastiriDesert)) return;
        if (States.TryGetValue(player.Instance, out var state))
        {
            ReleaseGatesForPlayer(player, state.Gates);
            await state.ReleasePlayerDebuffs(player);
        }
    }

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in States) pair.Value.StopAndClear();
            ReleaseAllGatesOnUnload();
        }
    }

    sealed record DelayedBurst(double At, int Gen, uint SourceId, SpawnedMonster Source, MonsterAttack Attack,
        (int X, int Y) Spot, double Radius, string? BurstAo, bool LeavesAcid = false);
    sealed record GazeTelegraph(double At, int Gen, uint SourceId, SpawnedMonster Source, (int X, int Y) Origin, double Facing);
    sealed class FlameBreath(uint sourceId, SpawnedMonster source, (int X, int Y) origin, double startAngle, double sweepRad, double started, double ends)
    {
        public readonly uint SourceId = sourceId;
        public readonly SpawnedMonster Source = source;
        public readonly (int X, int Y) Origin = origin;
        public readonly double StartAngle = startAngle;
        public readonly double SweepRad = sweepRad;
        public readonly double Started = started;
        public readonly double Ends = ends;
        public double NextPulse = started;
    }
    sealed record AcidPool((int X, int Y) At, double Radius, double Until);

    sealed class AreaState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly List<ArenaGate> Gates = [];
        public uint ChestId;
        public (int X, int Y) ChestAt;

        readonly GameArea gameArea;
        readonly string areaId;
        readonly List<TimedEffect> effects = [];
        readonly List<DelayedBurst> bursts = [];
        readonly List<GazeTelegraph> gazes = [];
        readonly List<AcidPool> acidPools = [];
        readonly HashSet<uint> boundBosses = [];
        readonly HashSet<uint> adds = [];
        readonly Dictionary<Player, ArenaHazards.Exposure> acidExposures = [];
        readonly object acidHolder = new();

        // Refinery (General Adus) placements
        readonly List<(int X, int Y)> conduitSpots = [];
        readonly List<GlyphQuest.Placement> refineryDoorPlacements = [];
        uint adusId;
        bool adusGatesInit, adusGatesLocked, adusInitialized, adusCleaned;
        double superchargedUntil, nextConduitAt, nextBombAt, nextQuakeAt;

        // Boiling Lake (Basilisk) placements
        readonly List<(int X, int Y)> petrifiedSpots = [];
        uint basiliskId;
        bool basiliskInitialized, basiliskCleaned;
        double nextGazeAt, nextBreathAt, nextSpitAt, nextAcidTick;
        FlameBreath? activeBreath;

        // Vastiri Desert (Storm Blade Mummy Event) state
        readonly GlyphQuest.Placement? chestPlacement;
        readonly List<(int X, int Y)> mummySpawners = [];
        int chestStateMachineIdx = -1;
        StateMachineData? chestStateMachine;
        int chestOpenState; // 0 = ready, 1 = active ambush, 2 = opened
        int mummyWave;
        double nextMummyWaveAt;
        readonly HashSet<uint> mummyWaveAdds = [];
        bool stopped;

        public AreaState(Player owner)
        {
            gameArea = owner.Area;
            areaId = owner.Layout.Id;
            var placed = AreaPlacements(owner);
            if (areaId == Refinery)
            {
                conduitSpots = [.. placed.Of("Metadata/Monsters/GeneralAdus/LightningConductor")
                    .Concat(placed.Of("Metadata/Monsters/GeneralAdus/LightningGrid1"))
                    .Concat(placed.Of("Metadata/Monsters/GeneralAdus/LightningGrid2"))
                    .Select(p => (p.X, p.Y))];
                refineryDoorPlacements = placed.Of(RefineryBossDoorType);
            }
            else if (areaId == BoilingLake)
            {
                petrifiedSpots = [.. PetrifiedBandits.SelectMany(placed.Of).Select(p => (p.X, p.Y))];
            }
            else if (areaId == VastiriDesert)
            {
                chestPlacement = placed.Of(MummyEventChestType).FirstOrDefault();
                mummySpawners = [.. placed.Of("Metadata/QuestObjects/Act9/MummyEventSpawnerSmall")
                    .Concat(placed.Of("Metadata/QuestObjects/Act9/MummyEventSpawnerMedium"))
                    .Concat(placed.Of("Metadata/QuestObjects/Act9/MummyEventSpawnerLarge"))
                    .Select(p => (p.X, p.Y))];
            }
        }

        public void StopAndClear()
        {
            stopped = true;
            if (adusId != 0 && gameArea.Monsters.TryGetValue(adusId, out var adus))
            {
                adus.RemoveTemporaryStats("adus_supercharged");
                adus.RebuildStats();
                gameArea.Buffs.Clear(adusId, SuperchargedBuff, adusId);
            }
            foreach (var p in acidExposures.Keys.ToArray())
                _ = ReleasePlayerDebuff(p, acidHolder);
            acidExposures.Clear();
        }

        public async Task ReleasePlayerDebuffs(Player p)
        {
            acidExposures.Remove(p);
            await ReleasePlayerDebuff(p, acidHolder);
        }

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            await StepBursts(runner, now);

            switch (areaId)
            {
                case Refinery: await StepAdus(runner, now); break;
                case BoilingLake: await StepBasilisk(runner, now); break;
                case VastiriDesert: await StepMummyEvent(runner, now); break;
            }
        }

        // ---- General Adus (2_9_9 Refinery) ----

        async Task StepAdus(Player runner, double now)
        {
            var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Variety?.Id == GeneralAdus);
            if (entry.Value is not { } boss) return;
            uint bossId = entry.Key;
            adusId = bossId;

            if (!adusGatesInit)
            {
                adusGatesInit = true;
                foreach (var dp in refineryDoorPlacements)
                    if (await CreateGate(runner, RefineryBossDoorType, dp, startLocked: false, lockedState: 2, unlockedState: 1) is { } g)
                        Gates.Add(g);
            }

            if (!Alive(boss))
            {
                if (!adusCleaned)
                {
                    adusCleaned = true;
                    adusGatesLocked = false;
                    foreach (var g in Gates) await SetGateLocked(runner, g, false);
                    boss.RemoveTemporaryStats("adus_supercharged");
                    runner.Area.Buffs.Clear(bossId, SuperchargedBuff, bossId);
                    bursts.Clear();
                    await ClearAllEffects(runner, effects);
                    Console.WriteLine("[gateway] General Adus defeated; Refinery gates unlocked and conduits cleared");
                }
                return;
            }

            await EnsureNativeSkills(runner, bossId, boss, AdusEffects, boundBosses);
            SyncSavedPhases(runner.Instance, boss, "GeneralAdus", [65, 35]);

            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 130, awakeId: bossId);
            if (target is null)
            {
                if (adusGatesLocked)
                {
                    adusGatesLocked = false;
                    foreach (var g in Gates) await SetGateLocked(runner, g, false);
                }
                return;
            }

            if (!adusGatesLocked)
            {
                adusGatesLocked = true;
                foreach (var g in Gates) await SetGateLocked(runner, g, true);
            }

            await EnsureBossBar(runner, bossId, boss);
            boss.Target = target;
            boss.Aggro = true;
            boss.EntranceGrace = false;
            MarkStarted(runner.Instance, "GeneralAdus");

            if (!adusInitialized)
            {
                adusInitialized = true;
                nextConduitAt = now + 7.0;
                nextBombAt = now + 4.5;
                nextQuakeAt = now + 3.0;
            }

            // Expire supercharged buff when timer finishes.
            bool supercharged = now < superchargedUntil;
            if (!supercharged && superchargedUntil > 0)
            {
                superchargedUntil = 0;
                boss.RemoveTemporaryStats("adus_supercharged");
                boss.RebuildStats();
                runner.Area.Buffs.Clear(bossId, SuperchargedBuff, bossId);
            }

            // Arena lightning conduits periodically energize, arcing between conduits and supercharging Adus.
            if (now >= nextConduitAt)
            {
                nextConduitAt = now + 13.0;
                superchargedUntil = now + 7.5;
                boss.SetTemporaryStats("adus_supercharged", AdusSuperchargedStats);
                boss.RebuildStats();
                runner.Area.Buffs.Set(bossId, new(SuperchargedBuff, bossId, U68: Buff.Seconds(7.5)));
                await SpawnTimedEffect(runner, effects, pos, AdusBeamArt, now + 2.5, scale: 1.25f);

                var shockAtk = ElementalSpellAttack(boss, DamageType.Lightning, 1.0, radius: 11);
                var conduits = conduitSpots.Count > 0
                    ? conduitSpots.Take(4).ToArray()
                    : Enumerable.Range(0, 3).Select(i => RingSpot(runner, pos, i * (Math.Tau / 3.0), 18)).ToArray();
                foreach (var cSpot in conduits)
                {
                    await SpawnTimedEffect(runner, effects, cSpot, AdusBeamArt, now + 2.2, scale: 1.15f);
                    bursts.Add(new(now + 1.4, boss.ActionGeneration, bossId, boss, shockAtk, cSpot, 11, ShockNovaArt));
                }
            }

            if (now < boss.BusyUntil || now < boss.FlinchUntil || MonsterActionFactor(boss, now) == 0) return;

            var aim = target.Walk.Position(now);
            double dist = Distance(pos, aim);

            // LivingBombLightningAdus[4740]: ticking detonation orbs near players.
            if (now >= nextBombAt)
            {
                nextBombAt = now + (supercharged ? 6.5 : 8.5);
                if (await CastAction(runner, bossId, boss, AdusEffects, AdusLivingBomb, aim, now, DamageType.Lightning, 1.35, 14, "GeneralAdus", forceSpell: true) is { } bombCast)
                {
                    var rng = AreaRng(runner);
                    int orbCount = supercharged ? 3 : 2;
                    for (int i = 0; i < orbCount; i++)
                    {
                        var orbAt = i == 0 ? aim : RingSpot(runner, aim, rng.NextDouble() * Math.Tau, 12);
                        await SpawnTimedEffect(runner, effects, orbAt, AdusBeamArt, now + 2.6, scale: 1.1f);
                        bursts.Add(new(now + 2.4, bombCast.Generation, bossId, boss, bombCast.Attack, orbAt, 13, AdusImpactArt));
                    }
                    return;
                }
            }

            // QuakeSlamGeneralAdus[5473] vs QuakeSlamGeneralAdusSupercharged[5474].
            if (now >= nextQuakeAt && dist <= 26)
            {
                int quakeEffect = supercharged ? AdusQuakeSuper : AdusQuake;
                nextQuakeAt = now + (supercharged ? 4.2 : 5.5);
                if (await CastAction(runner, bossId, boss, AdusEffects, quakeEffect, aim, now, DamageType.Lightning, supercharged ? 1.55 : 1.25, supercharged ? 19 : 15, "GeneralAdus", forceSpell: false) is { } quake)
                {
                    await SpawnTimedEffect(runner, effects, aim, ShockNovaArt, now + 1.2, scale: supercharged ? 1.5f : 1.15f);
                    bursts.Add(new(now + 0.75, quake.Generation, bossId, boss, quake.Attack, aim, supercharged ? 18 : 14, AdusImpactArt));
                    if (supercharged)
                        bursts.Add(new(now + 1.85, quake.Generation, bossId, boss, quake.Attack, aim, 21, ShockNovaArt));
                    return;
                }
            }

            if (dist <= 15)
            {
                if (await CastAction(runner, bossId, boss, AdusEffects, AdusMelee, aim, now, DamageType.Physical, 1.0, 14, "GeneralAdus", forceSpell: false) is { } melee)
                    bursts.Add(new(now + melee.Attack.Seconds * 0.45, melee.Generation, bossId, boss, melee.Attack, aim, 13, null));
            }
            else
            {
                await Chase(runner, bossId, boss, aim, now, "GeneralAdus");
            }
        }

        // ---- The Basilisk (2_9_6 Boiling Lake) ----

        async Task StepBasilisk(Player runner, double now)
        {
            acidPools.RemoveAll(p => now >= p.Until);
            await StepGazes(runner, now);
            await StepFlameBreath(runner, now);

            var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Variety?.Id == Basilisk);
            if (entry.Value is not { } boss) return;
            uint bossId = entry.Key;
            basiliskId = bossId;

            if (!Alive(boss))
            {
                if (!basiliskCleaned)
                {
                    basiliskCleaned = true;
                    activeBreath = null;
                    gazes.Clear();
                    acidPools.Clear();
                    bursts.Clear();
                    await RetireMonsters(runner, adds);
                    adds.Clear();
                    foreach (var p in ActivePlayers(runner))
                        await ReleasePlayerDebuffs(p);
                    await ClearAllEffects(runner, effects);
                    Console.WriteLine("[gateway] The Basilisk defeated; acid pools and petrified guards cleared");
                }
                return;
            }

            await EnsureNativeSkills(runner, bossId, boss, BasiliskEffects, boundBosses);
            SyncSavedPhases(runner.Instance, boss, "Basilisk", [70, 35]);
            var phases = boss.LifePhases!;
            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 130, awakeId: bossId);
            if (target is null) return;

            await EnsureBossBar(runner, bossId, boss);
            boss.Target = target;
            boss.Aggro = true;
            boss.EntranceGrace = false;
            MarkStarted(runner.Instance, "Basilisk");

            if (!basiliskInitialized)
            {
                basiliskInitialized = true;
                nextGazeAt = now + 5.5;
                nextBreathAt = now + 9.0;
                nextSpitAt = now + 3.0;
            }

            // Awaken petrified bandit guards at 70% and 35% HP.
            if (phases.Completed < 2 && phases.Begin(boss.Life, boss.MaxLife))
            {
                int idx = phases.Completed;
                await AwakenPetrifiedGuards(runner, bossId, boss.Level, pos, now);
                MarkPhaseCompleted(runner.Instance, "Basilisk", idx);
                phases.Finish();
            }

            await StepAcidPools(runner, bossId, boss, now);

            if (activeBreath is not null || now < boss.BusyUntil || now < boss.FlinchUntil || MonsterActionFactor(boss, now) == 0)
                return;

            var aim = target.Walk.Position(now);
            double dist = Distance(pos, aim);

            // EmptyActionSpellBasilisk[7871]: Petrifying Gaze cone telegraph applying Petrified freeze debuff.
            if (now >= nextGazeAt)
            {
                nextGazeAt = now + 11.5;
                if (await CastAction(runner, bossId, boss, BasiliskEffects, BasiliskGaze, aim, now, DamageType.Physical, 0.7, 36, "Basilisk", forceSpell: true) is { } gazeCast)
                {
                    double facing = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                    var teleAt = RingSpot(runner, pos, facing, 14);
                    await SpawnTimedEffect(runner, effects, teleAt, StoneGazeArt, now + 1.6, scale: 1.4f);
                    gazes.Add(new(now + 1.35, gazeCast.Generation, bossId, boss, pos, facing));
                    return;
                }
            }

            // Sweeping acid-flame breath (8731 LeftToRight / 8732 RightToLeft).
            if (now >= nextBreathAt)
            {
                bool leftToRight = AreaRng(runner).Next(2) == 0;
                int breathEffect = leftToRight ? BasiliskFlameLR : BasiliskFlameRL;
                nextBreathAt = now + 10.5;
                if (await CastAction(runner, bossId, boss, BasiliskEffects, breathEffect, aim, now, DamageType.Fire, 1.0, 38, "Basilisk", forceSpell: true) is not null)
                {
                    double center = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                    double startAngle = leftToRight ? center - 0.85 : center + 0.85;
                    double sweep = leftToRight ? 1.7 : -1.7;
                    await SpawnTimedEffect(runner, effects, pos, FireSpineArt, now + 3.2, scale: 1.25f);
                    activeBreath = new(bossId, boss, pos, startAngle, sweep, now + 0.65, now + 3.15);
                    return;
                }
            }

            // Acid spit leaving caustic pools.
            if (now >= nextSpitAt && dist >= 12)
            {
                nextSpitAt = now + 4.8;
                if (await CastAction(runner, bossId, boss, BasiliskEffects, BasiliskSpit, aim, now, DamageType.Chaos, 1.15, 11, "Basilisk", forceSpell: true) is { } spit)
                {
                    bursts.Add(new(now + 0.75, spit.Generation, bossId, boss, spit.Attack, aim, 10, CausticPoolArt, LeavesAcid: true));
                    return;
                }
            }

            if (dist <= 15)
            {
                if (await CastAction(runner, bossId, boss, BasiliskEffects, BasiliskMelee, aim, now, DamageType.Physical, 1.05, 14, "Basilisk", forceSpell: false) is { } melee)
                    bursts.Add(new(now + melee.Attack.Seconds * 0.45, melee.Generation, bossId, boss, melee.Attack, aim, 13, null));
            }
            else
            {
                await Chase(runner, bossId, boss, aim, now, "Basilisk");
            }
        }

        async Task AwakenPetrifiedGuards(Player runner, uint bossId, byte level, (int X, int Y) center, double now)
        {
            int living = adds.Count(id => runner.Area.Monsters.TryGetValue(id, out var m) && Alive(m));
            var rng = AreaRng(runner);
            for (int i = 0; i < 3 && living + i < 6; i++)
            {
                var spot = petrifiedSpots.Count > 0
                    ? petrifiedSpots[rng.Next(petrifiedSpots.Count)]
                    : RingSpot(runner, center, i * (Math.Tau / 3.0), 20);
                await SpawnTimedEffect(runner, effects, spot, BanditBreakoutArt, now + 1.8, scale: 1.1f);
                await SpawnAdd(runner, PetrifiedBandits[i % PetrifiedBandits.Length], level, spot, bossId, adds,
                    ownedCurrent: () => !stopped && !basiliskCleaned);
            }
            Console.WriteLine("[gateway] The Basilisk awakened petrified bandit guards");
        }

        async Task StepGazes(Player runner, double now)
        {
            for (int i = gazes.Count - 1; i >= 0; i--)
            {
                var g = gazes[i];
                if (now < g.At) continue;
                gazes.RemoveAt(i);
                if (!Alive(g.Source)) continue;
                foreach (var p in ActivePlayers(runner))
                {
                    if (p.PlayerLife == 0 || p.Paused || InGracePeriod(p)) continue;
                    var at = p.Walk.Position(now);
                    double d = Distance(at, g.Origin);
                    if (d > 42) continue;
                    double angle = Math.Atan2(at.Y - g.Origin.Y, at.X - g.Origin.X);
                    if (d <= 5 || Math.Abs(Math.IEEERemainder(angle - g.Facing, Math.Tau)) <= 0.58)
                    {
                        await AddPlayerDebuff(p, PetrifiedBuff, g.SourceId, PetrifiedFreezeStats, 2.2);
                        await AddPlayerDebuff(p, StoneGazeSlowBuff, g.SourceId, StoneGazeSlowStats, 4.5);
                    }
                }
            }
        }

        async Task StepFlameBreath(Player runner, double now)
        {
            if (activeBreath is not { } breath) return;
            if (now >= breath.Ends || !Alive(breath.Source))
            {
                activeBreath = null;
                return;
            }
            if (now < breath.NextPulse) return;
            breath.NextPulse = now + 0.4;

            double t = Math.Clamp((now - breath.Started) / Math.Max(0.1, breath.Ends - breath.Started), 0, 1);
            double angle = breath.StartAngle + breath.SweepRad * t;
            var atk = ElementalSpellAttack(breath.Source, DamageType.Fire, 0.8, radius: 42);
            await HitCone(runner, breath.SourceId, breath.Source, atk, breath.Origin, angle, 0.35, 42, () => !stopped && Alive(breath.Source));

            var poolAt = RingSpot(runner, breath.Origin, angle, 18 + (t * 14));
            if (acidPools.Count < 10)
            {
                acidPools.Add(new(poolAt, 9, now + 8.0));
                await SpawnTimedEffect(runner, effects, poolAt, CausticPoolArt, now + 8.0, scale: 1.1f);
            }
        }

        async Task StepAcidPools(Player runner, uint bossId, SpawnedMonster boss, double now)
        {
            if (now < nextAcidTick) return;
            nextAcidTick = now + 0.25;
            var (minDmg, maxDmg) = Monsters.BaseDamage(boss.Variety!, boss.Level) ?? (25, 40);
            double acidDps = (minDmg + maxDmg) * 0.5;

            foreach (var p in ActivePlayers(runner))
            {
                var at = p.Walk.Position(now);
                bool inAcid = p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p) && acidPools.Any(pool => Distance(at, pool.At) <= pool.Radius);
                if (inAcid)
                    await HoldPlayerDebuff(p, acidHolder, CausticCloudBuff, bossId, EmptyStats);
                else
                    await ReleasePlayerDebuff(p, acidHolder);

                if (!acidExposures.TryGetValue(p, out var exp))
                    acidExposures[p] = exp = new();
                await ArenaHazards.Ground(p, exp, now, (pt, _) =>
                    acidPools.Any(pool => Distance(pt, pool.At) <= pool.Radius)
                        ? DamagePacket.Of(DamageType.Chaos, acidDps, acidDps)
                        : DamagePacket.Empty,
                    () => !stopped && !basiliskCleaned);
            }
        }

        // ---- Storm Blade Mummy Event (2_9_3 Vastiri Desert) ----

        public async Task EnsureMummyChest(Player player)
        {
            if (ChestId != 0 || chestPlacement is not { } cp) return;
            if (QuestObjectTypes.Find(MummyEventChestType) is not { } info) return;
            int smIdx = info.Components.ToList().IndexOf("StateMachine");
            if (smIdx < 0 || !QuestObject.CanSerialize(info.Components, haveStateMachine: true)) return;
            var otEntry = ObjectTypes.All().FirstOrDefault(t => t.Type.Equals(MummyEventChestType, StringComparison.OrdinalIgnoreCase));
            var sm = otEntry.Type is not null ? ObjectTypes.StateMachine(otEntry.Row, ObjectTypes.SharedStates()) : null;
            if (sm is null) return;

            chestStateMachineIdx = smIdx;
            chestStateMachine = sm;
            ChestAt = (cp.X, cp.Y);
            ChestId = player.TakeObjectId();
            bool alreadyDone = player.Instance.HasState(Quests.InstanceFlag("mummy-event-done"))
                || ClientHasQuestFlag(player, StormFinishFlag);
            chestOpenState = alreadyDone ? 2 : 0;

            string? model = info.OwnModel ? null : (string.IsNullOrEmpty(cp.Model) ? info.Model : cp.Model);
            var obj = new QuestObject(cp.X, cp.Y, info.Components, Rotation: cp.Rotation, Height: cp.Z, Model: model)
            {
                StateMachine = sm.Set("open", chestOpenState),
            };
            var blob = obj.Blob();
            await SendWorldCreate(player, ChestId,
                () => InstanceEntities.CreateObject(ChestId, InstanceEntities.ObjectTypeHash(MummyEventChestType), blob),
                $"0x02bb MummyEventChest 0x{ChestId:x} at {ChestAt} (open={chestOpenState})", wake: true, awaitBirth: false);
        }

        public async Task BeginMummyEvent(Player player, double now)
        {
            if (chestOpenState != 0 || ChestId == 0 || chestStateMachine is null) return;
            chestOpenState = 1;
            mummyWave = 0;
            nextMummyWaveAt = now + 0.5;
            await SendWorldRecord(player, ChestId,
                InstanceEntities.StateMachineUpdate(ChestId, (byte)chestStateMachineIdx, chestStateMachine.Set("open", 1)),
                "[Vastiri Desert] MummyEventChest activated (open=1)");
            await SpawnTimedEffect(player, effects, ChestAt, SwirlingSandArt, now + 40, scale: 1.35f);
            foreach (var p in ActivePlayers(player))
                if (!ClientHasQuestFlag(p, StormBeginFlag))
                    await SetQuestFlags(p, [StormBeginFlag]);
            Console.WriteLine("[gateway] Vastiri Desert: Storm Blade Mummy Event started (3 waves)");
        }

        async Task StepMummyEvent(Player runner, double now)
        {
            if (chestOpenState != 1 || ChestId == 0 || chestStateMachine is null) return;

            bool livingAdds = mummyWaveAdds.Any(id => runner.Area.Monsters.TryGetValue(id, out var m) && Alive(m));
            if (mummyWave < 3)
            {
                if (mummyWave > 0 && livingAdds && now < nextMummyWaveAt) return;
                mummyWave++;
                nextMummyWaveAt = now + 14.0;
                byte level = (byte)Math.Clamp(ServerLevels.AreaLevel(runner.Layout.Id), 1, 100);
                int smallCount = 2 + mummyWave;
                int largeCount = mummyWave >= 2 ? mummyWave - 1 : 0;
                var rng = AreaRng(runner);
                for (int i = 0; i < smallCount; i++)
                {
                    var spot = mummySpawners.Count > 0
                        ? mummySpawners[rng.Next(mummySpawners.Count)]
                        : RingSpot(runner, ChestAt, rng.NextDouble() * Math.Tau, 18);
                    await SpawnAdd(runner, MummySmall, level, spot, ChestId, mummyWaveAdds, ownedCurrent: () => !stopped);
                }
                for (int i = 0; i < largeCount; i++)
                {
                    var spot = mummySpawners.Count > 0
                        ? mummySpawners[rng.Next(mummySpawners.Count)]
                        : RingSpot(runner, ChestAt, rng.NextDouble() * Math.Tau, 22);
                    await SpawnAdd(runner, MummyLarge, level, spot, ChestId, mummyWaveAdds, ownedCurrent: () => !stopped);
                }
                Console.WriteLine($"[gateway] Vastiri Desert: Mummy Event wave {mummyWave}/3 spawned ({smallCount} warriors, {largeCount} goliaths)");
                return;
            }

            if (livingAdds) return;

            // All 3 waves cleared: open chest (open=2) and drop Storm Blade!
            chestOpenState = 2;
            runner.Instance.SetState(Quests.InstanceFlag("mummy-event-done"));
            await SendWorldRecord(runner, ChestId,
                InstanceEntities.StateMachineUpdate(ChestId, (byte)chestStateMachineIdx, chestStateMachine.Set("open", 2)),
                "[Vastiri Desert] MummyEventChest opened (open=2)");
            await SpawnTimedEffect(runner, effects, ChestAt, ReleaseSandArt, now + 2.5, scale: 1.3f);

            foreach (var p in ActivePlayers(runner))
            {
                if (!ClientHasQuestFlag(p, StormFinishFlag))
                {
                    await SetQuestFlags(p, [StormFinishFlag]);
                    await UpdateQuestObjectStates(p, [StormFinishFlag]);
                }
                await DropQuestItem(p, StormSwordItem, ChestAt, "MummyEventChest");
            }
            Console.WriteLine("[gateway] Vastiri Desert: Mummy Event cleared; chest opened and Storm Blade dropped");
        }

        async Task StepBursts(Player runner, double now)
        {
            for (int i = bursts.Count - 1; i >= 0; i--)
            {
                var b = bursts[i];
                if (now < b.At) continue;
                bursts.RemoveAt(i);
                if (!Alive(b.Source)) continue;
                if (b.BurstAo is { } ao)
                    await SpawnTimedEffect(runner, effects, b.Spot, ao, now + 1.3, scale: 1.2f);
                await HitCircle(runner, b.SourceId, b.Source, b.Attack, b.Spot, b.Radius, () => !stopped && Alive(b.Source));
                if (b.LeavesAcid && acidPools.Count < 10)
                {
                    acidPools.Add(new(b.Spot, 9.5, now + 9.0));
                    await SpawnTimedEffect(runner, effects, b.Spot, CausticPoolArt, now + 9.0, scale: 1.15f);
                }
            }
        }
    }
}
