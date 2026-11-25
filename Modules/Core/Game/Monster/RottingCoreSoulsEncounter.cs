using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Act9To10Encounters;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Act 9 — The Rotting Core (<c>2_9_10_2</c>) dedicated controllers for the three Organ Souls (cooperating with
/// <see cref="DepravedTrinity"/> for portals and <c>UnholyTrio</c>):
/// <list type="bullet">
/// <item><b>Doedre, Darksoul (<c>DoedreSoul[6159]</c>)</b>: Rotating Vulnerability (<c>2981</c>), Temporal Chains
/// (<c>2982</c>), and Enfeeble (<c>2983</c>) stone pillars, telegraphed <c>DoedreScreech[8696]</c> chaos burst,
/// <c>DoedreFormWarp[6449]</c>, and <c>DoedreZombieMinionEmergeUnholy[1265]</c> zombie summons (<c>NoRewards = true</c>).</item>
/// <item><b>Maligaro, The Broken (<c>MaligaroSoul[6175]</c>)</b>: <c>MaligaroSummonClones[6683]</c> spawning
/// <c>MaligaroSoulClone1..3</c> (<c>NoRewards = true</c>), <c>MaligaroTeleportSlam[6412]</c>, and Bladefall /
/// Blade Vortex cascade (<c>UnholyTrioTriggerCascade2Maligaro[8481]</c>).</item>
/// <item><b>Shavronne, Unbound (<c>ShavronneSoul[6169]</c>)</b>: Roaming <c>ShavronneTornadoDaemon</c> lightning
/// tornado, <c>ShavronneTeleportUnholyTrio[5068]</c>, <c>ShavronneGhostBrutusSlam[8514]</c> telegraph, and
/// <c>ShavronneStormBurst[6583]</c> / <c>ShavronneBallLightning[8515]</c>.</item>
/// </list>
/// </summary>
public static class RottingCoreSoulsEncounter
{
    public const string Area = "2_9_10_2";
    public const string DoedreSoul = "Metadata/Monsters/Doedre/DoedreSoul";                  // [6159]
    public const string MaligaroSoul = "Metadata/Monsters/Maligaro/MaligaroSoul";            // [6175]
    public const string ShavronneSoul = "Metadata/Monsters/Shavronne/ShavronneSoul";         // [6169]

    public const string DoedreZombie = "Metadata/Monsters/ZombieDismember/DoedreZombieMinionEmergeUnholy"; // [1265]
    public static readonly string[] MaligaroClones =
    [
        "Metadata/Monsters/Maligaro/MaligaroSoulClone1",  // [6181]
        "Metadata/Monsters/Maligaro/MaligaroSoulClone2_", // [6183]
        "Metadata/Monsters/Maligaro/MaligaroSoulClone3",  // [6185]
    ];

    // Verified GrantedEffects rows in client.db:
    const int DoedreWand = 6439;             // WandStrikeTrio
    const int DoedreWarp = 6449;             // DoedreFormWarp
    const int DoedrePillarVuln = 2981;       // TriggerDoedreStonePillarVulnerability
    const int DoedrePillarTemp = 2982;       // TriggerDoedreStonePillarTemporalChains
    const int DoedrePillarEnfeeble = 2983;   // TriggerDoedreStonePillarEnfeeble
    const int DoedreScreech = 8696;          // DoedreScreech
    const int DoedreZombies = 4965;          // DoedreUnholySummonZombiesHandCast
    static readonly int[] DoedreEffects =
        [DoedreWand, DoedreWarp, DoedrePillarVuln, DoedrePillarTemp, DoedrePillarEnfeeble, DoedreScreech, DoedreZombies];

    const int MaligaroMelee = 406;
    const int MaligaroCombo = 6417;          // MaligaroSoulComboAttack
    const int MaligaroBladefall = 1488;      // MaligaroBladefallFirestorm1
    const int MaligaroCascade = 8481;        // UnholyTrioTriggerCascade2Maligaro
    const int MaligaroBoomerang = 6772;      // MaligaroBoomerangProjectile
    const int MaligaroTeleSlam = 6412;       // MaligaroTeleportSlam
    const int MaligaroClonesSkill = 6683;    // MaligaroSummonClones
    static readonly int[] MaligaroEffects =
        [MaligaroMelee, MaligaroCombo, MaligaroBladefall, MaligaroCascade, MaligaroBoomerang, MaligaroTeleSlam, MaligaroClonesSkill];

    const int ShavTeleport = 5068;           // ShavronneTeleportUnholyTrio
    const int ShavMinions = 4963;            // ShavronneSummonLightningMinions
    const int ShavBrutusSlam = 8514;         // ShavronneGhostBrutusSlam
    const int ShavStormBurst = 6583;         // ShavronneStormBurst
    const int ShavBallLightning = 8515;      // ShavronneBallLightning
    static readonly int[] ShavronneEffects =
        [ShavTeleport, ShavMinions, ShavBrutusSlam, ShavStormBurst, ShavBallLightning];

    // Verified BuffDefinitions rows in client.db:
    const ushort VulnBuff = 79;              // curse_vulnerability
    const ushort TempChainsBuff = 69;        // curse_temporal_chains
    const ushort EnfeebleBuff = 76;          // curse_enfeeble

    // Verified .ao visual paths in game-files.zip:
    const string DoedreScreechArt = "Metadata/Effects/Spells/monsters_effects/act9/the_trio/doedre/epic_screech/full_screech.ao";
    const string PillarVulnArt = "Metadata/Effects/Spells/monsters_effects/act9/the_trio/doedre/stone_pillar/curse_aura/vulnerability.ao";
    const string PillarTempArt = "Metadata/Effects/Spells/monsters_effects/act9/the_trio/doedre/stone_pillar/curse_aura/temporal_chains.ao";
    const string PillarEnfeebleArt = "Metadata/Effects/Spells/monsters_effects/act9/the_trio/doedre/stone_pillar/curse_aura/enfeeble.ao";

    const string MaligaroTrailArt = "Metadata/Effects/Spells/monsters_effects/act9/the_trio/maligarospectral/maligarotrail.ao";
    const string MaligaroBladePoofArt = "Metadata/Effects/Spells/monsters_effects/act9/the_trio/maligaro/blade_blossom/poof.ao";
    const string MaligaroVortexArt = "Metadata/Effects/Spells/monsters_effects/act9/the_trio/maligaro/blade_vortex/blade_vortex.ao";

    const string ShavTornadoArt = "Metadata/Effects/Spells/monsters_effects/act9/the_trio/shavronne/living_storms/lightning_cyclone.ao";
    const string ShavBrutusPrepArt = "Metadata/Effects/Spells/monsters_effects/act9/the_trio/shavronne/brutus_slam/brutus_slam.ao";
    const string ShavBrutusBigArt = "Metadata/Effects/Spells/monsters_effects/act9/the_trio/shavronne/brutus_slam/bruteslam_big.ao";
    const string ShavStormBurstArt = "Metadata/Effects/Spells/monsters_effects/act9/the_trio/shavronne/storm_burst/projectile_explode.ao";
    const string ShavBallChargeArt = "Metadata/Effects/Spells/monsters_effects/act9/the_trio/shavronne/ultimate_ball_lightning/chargeup.ao";
    const string ShavLightningBoltArt = "Metadata/Effects/Spells/monsters_effects/act9/the_trio/shavronne/ultimate_ball_lightning/lightning_bolt.ao";

    static readonly IReadOnlyDictionary<uint, int> VulnStats = new Dictionary<uint, int>
    {
        [Stat.BaseDamageTakenPlusPct] = 22,
    };

    static readonly IReadOnlyDictionary<uint, int> TempChainsStats = new Dictionary<uint, int>
    {
        [Stat.ActionSpeedMinusPct] = 22,
    };

    static readonly IReadOnlyDictionary<uint, int> EnfeebleStats = new Dictionary<uint, int>
    {
        [Stat.DoedreAuraDamagePlusPctFinal] = -18,
    };

    static readonly RuleTable<InstanceState, SoulsState> States = new();

    public static async Task Tick(Player player)
    {
        if (player.Layout.Id != Area || !CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        if (!States.TryGetValue(player.Instance, out var state))
        {
            bool present = player.Area.Monsters.Values.Any(m => m.Variety?.Id is DoedreSoul or MaligaroSoul or ShavronneSoul);
            if (!present) return;
            state = States.GetValue(player.Instance, _ => new SoulsState(player));
        }
        await state.Gate.WaitAsync();
        try
        {
            if (IsMonsterRunner(player))
                await state.Step(player, AreaTime(player));
        }
        finally { state.Gate.Release(); }
    }

    public static async Task Leaving(Player player)
    {
        if (player.Layout.Id != Area) return;
        if (States.TryGetValue(player.Instance, out var state))
            await state.ReleasePlayerDebuffs(player);
    }

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in States) pair.Value.StopAndClear();
        }
    }

    sealed record DelayedHit(double At, uint SourceId, SpawnedMonster Source, MonsterAttack Attack, (int X, int Y) Spot, double Radius, string? BurstAo);

    sealed class SoulsState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly List<TimedEffect> effects = [];
        readonly List<DelayedHit> hits = [];
        readonly HashSet<uint> boundBosses = [];
        readonly HashSet<uint> doedreAdds = [];
        readonly HashSet<uint> maligaroClones = [];
        readonly HashSet<Player> cursedPlayers = [];
        readonly object curseHolder = new();

        // Cached organ room placements (loaded ONCE per instance).
        readonly (int X, int Y)?[] pillarSpots = new (int X, int Y)?[3];
        readonly List<(int X, int Y)> shavTeleportSpots = [];

        // Doedre state
        bool doedreInit, doedreCleaned;
        int activePillar = -1;
        (int X, int Y) activePillarPos;
        double nextPillarSwitch, nextScreech, nextZombies, nextDoedreWarp, nextCurseCheck;

        // Maligaro state
        bool maligaroInit, maligaroCleaned;
        double nextClones, nextTeleSlam, nextCascade, vortexUntil, nextVortexPulse;
        (int X, int Y) vortexOrigin;

        // Shavronne state
        bool shavInit, shavCleaned;
        double nextShavTeleport, nextBrutusSlam, nextStormBurst, nextBallLightning, nextTornadoTick;
        double tornadoX, tornadoY;
        bool tornadoActive;
        double nextTornadoVisual;

        bool stopped;

        public SoulsState(Player owner)
        {
            var placed = AreaPlacements(owner);
            pillarSpots[0] = placed.Of("Metadata/Monsters/Doedre/DoedreStonePillarSpawnerVulnerability").FirstOrDefault() is { } p0 ? (p0.X, p0.Y) : null;
            pillarSpots[1] = placed.Of("Metadata/Monsters/Doedre/DoedreStonePillarSpawnerTemporalChains").FirstOrDefault() is { } p1 ? (p1.X, p1.Y) : null;
            pillarSpots[2] = placed.Of("Metadata/Monsters/Doedre/DoedreStonePillarSpawnerEnfeeble").FirstOrDefault() is { } p2 ? (p2.X, p2.Y) : null;
            shavTeleportSpots = [.. placed.Of("Metadata/MiscellaneousObjects/TeleportMarker").Select(p => (p.X, p.Y))];
        }

        public void StopAndClear()
        {
            stopped = true;
            foreach (var p in cursedPlayers.ToArray())
                _ = ReleasePlayerDebuff(p, curseHolder);
            cursedPlayers.Clear();
        }

        public async Task ReleasePlayerDebuffs(Player p)
        {
            cursedPlayers.Remove(p);
            await ReleasePlayerDebuff(p, curseHolder);
        }

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            await StepHits(runner, now);

            await StepDoedre(runner, now);
            await StepMaligaro(runner, now);
            await StepShavronne(runner, now);
        }

        // ---- 1. Doedre, Darksoul ----

        async Task StepDoedre(Player runner, double now)
        {
            var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Variety?.Id == DoedreSoul);
            if (entry.Value is not { } boss) return;
            uint bossId = entry.Key;

            if (!Alive(boss))
            {
                if (!doedreCleaned)
                {
                    doedreCleaned = true;
                    activePillar = -1;
                    await RetireMonsters(runner, doedreAdds);
                    doedreAdds.Clear();
                    foreach (var p in cursedPlayers.ToArray())
                        await ReleasePlayerDebuff(p, curseHolder);
                    cursedPlayers.Clear();
                }
                return;
            }

            await EnsureNativeSkills(runner, bossId, boss, DoedreEffects, boundBosses);
            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 115, awakeId: bossId);
            if (target is null) return;

            await EnsureBossBar(runner, bossId, boss);
            boss.Target = target;
            boss.Aggro = true;
            boss.EntranceGrace = false;

            if (!doedreInit)
            {
                doedreInit = true;
                nextPillarSwitch = now + 2.0;
                nextScreech = now + 7.5;
                nextZombies = now + 6.0;
                nextDoedreWarp = now + 10.0;
            }

            // Apply active stone pillar curse aura to players inside Doedre's arena.
            if (activePillar >= 0 && now >= nextCurseCheck)
            {
                nextCurseCheck = now + 0.35;
                ushort buffRow = activePillar == 0 ? VulnBuff : activePillar == 1 ? TempChainsBuff : EnfeebleBuff;
                var stats = activePillar == 0 ? VulnStats : activePillar == 1 ? TempChainsStats : EnfeebleStats;
                foreach (var p in ActivePlayers(runner))
                {
                    bool inAura = p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)
                        && Distance(p.Walk.Position(now), activePillarPos) <= 42;
                    if (inAura)
                    {
                        cursedPlayers.Add(p);
                        await HoldPlayerDebuff(p, curseHolder, buffRow, bossId, stats);
                    }
                    else if (cursedPlayers.Remove(p))
                    {
                        await ReleasePlayerDebuff(p, curseHolder);
                    }
                }
            }

            if (now < boss.BusyUntil || now < boss.FlinchUntil || MonsterActionFactor(boss, now) == 0) return;

            var aim = target.Walk.Position(now);

            // Rotate Vulnerability / Temporal Chains / Enfeeble stone pillars.
            if (now >= nextPillarSwitch)
            {
                nextPillarSwitch = now + 12.5;
                activePillar = (activePillar + 1) % 3;
                int pillarEffect = activePillar == 0 ? DoedrePillarVuln : activePillar == 1 ? DoedrePillarTemp : DoedrePillarEnfeeble;
                string pillarArt = activePillar == 0 ? PillarVulnArt : activePillar == 1 ? PillarTempArt : PillarEnfeebleArt;
                activePillarPos = pillarSpots[activePillar] ?? RingSpot(runner, pos, activePillar * (Math.Tau / 3.0), 16);
                await CastAction(runner, bossId, boss, DoedreEffects, pillarEffect, activePillarPos, now, DamageType.Chaos, 0.7, 16, "DoedreSoul");
                await SpawnTimedEffect(runner, effects, activePillarPos, pillarArt, now + 12.5, scale: 1.4f);
                return;
            }

            // DoedreScreech[8696]: telegraphed expanding chaos/phys burst.
            if (now >= nextScreech)
            {
                nextScreech = now + 11.0;
                // The native action reads the target's height when creating the caster-centred screech.
                if (await CastAction(runner, bossId, boss, DoedreEffects, DoedreScreech, pos, now, DamageType.Chaos, 1.5, 28,
                    "DoedreSoul", forceSpell: true, targetObjectId: bossId) is { } screech)
                {
                    await SpawnTimedEffect(runner, effects, pos, DoedreScreechArt, now + 2.2, scale: 1.4f);
                    hits.Add(new(now + 1.4, bossId, boss, screech.Attack, pos, 26, null));
                    return;
                }
            }

            // Summon Doedre's Loyal zombies (NoRewards = true, SummonerId = bossId).
            if (now >= nextZombies)
            {
                nextZombies = now + 11.5;
                int living = doedreAdds.Count(aid => runner.Area.Monsters.TryGetValue(aid, out var m) && Alive(m));
                if (living < 5)
                {
                    await CastAction(runner, bossId, boss, DoedreEffects, DoedreZombies, aim, now, DamageType.Chaos, 0.5, 10, "DoedreSoul");
                    var rng = AreaRng(runner);
                    for (int i = 0; i < 3 && living + i < 5; i++)
                    {
                        var zSpot = RingSpot(runner, aim, rng.NextDouble() * Math.Tau, 14);
                        await SpawnAdd(runner, DoedreZombie, boss.Level, zSpot, bossId, doedreAdds, ownedCurrent: () => !stopped && !doedreCleaned);
                    }
                    return;
                }
            }

            if (now >= nextDoedreWarp && Distance(pos, aim) < 18)
            {
                nextDoedreWarp = now + 12.0;
                var warpTo = RingSpot(runner, aim, AreaRng(runner).NextDouble() * Math.Tau, 24);
                await CastAction(runner, bossId, boss, DoedreEffects, DoedreWarp, warpTo, now, DamageType.Chaos, 0.9, 12, "DoedreSoul");
                await TeleportBoss(runner, bossId, boss, warpTo, now);
                return;
            }

            if (Distance(pos, aim) <= 44)
            {
                if (await CastAction(runner, bossId, boss, DoedreEffects, DoedreWand, aim, now, DamageType.Chaos, 1.0, 12, "DoedreSoul", forceSpell: true) is { } wand)
                    hits.Add(new(now + 0.55, bossId, boss, wand.Attack, aim, 9, null));
            }
            else
            {
                await Chase(runner, bossId, boss, aim, now, "DoedreSoul");
            }
        }

        // ---- 2. Maligaro, The Broken ----

        async Task StepMaligaro(Player runner, double now)
        {
            var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Variety?.Id == MaligaroSoul);
            if (entry.Value is not { } boss) return;
            uint bossId = entry.Key;

            if (!Alive(boss))
            {
                if (!maligaroCleaned)
                {
                    maligaroCleaned = true;
                    vortexUntil = 0;
                    await RetireMonsters(runner, maligaroClones);
                    maligaroClones.Clear();
                }
                return;
            }

            await EnsureNativeSkills(runner, bossId, boss, MaligaroEffects, boundBosses);
            SyncSavedPhases(runner.Instance, boss, "MaligaroSoul", [66, 33]);
            var phases = boss.LifePhases!;
            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 115, awakeId: bossId);
            if (target is null) return;

            await EnsureBossBar(runner, bossId, boss);
            boss.Target = target;
            boss.Aggro = true;
            boss.EntranceGrace = false;

            if (!maligaroInit)
            {
                maligaroInit = true;
                nextClones = now + 7.5;
                nextTeleSlam = now + 5.0;
                nextCascade = now + 3.5;
            }

            // Orbiting Blade Vortex pulses following MaligaroCascade[8481].
            if (now < vortexUntil && now >= nextVortexPulse)
            {
                nextVortexPulse = now + 0.65;
                vortexOrigin = boss.Walk.Position(now);
                var bvAtk = BuildAttack(runner, boss, MaligaroEffects, MaligaroCascade, DamageType.Physical, 0.75, 16, forceSpell: true);
                await HitCircle(runner, bossId, boss, bvAtk, vortexOrigin, 16, () => !stopped && Alive(boss));
            }

            // Phase threshold clone summons at 66% / 33%.
            if (phases.Completed < 2 && phases.Begin(boss.Life, boss.MaxLife))
            {
                int idx = phases.Completed;
                await SummonMaligaroClones(runner, bossId, boss, pos, now);
                MarkPhaseCompleted(runner.Instance, "MaligaroSoul", idx);
                phases.Finish();
            }

            if (now < boss.BusyUntil || now < boss.FlinchUntil || MonsterActionFactor(boss, now) == 0) return;

            var aim = target.Walk.Position(now);
            double dist = Distance(pos, aim);

            if (now >= nextClones)
            {
                nextClones = now + 15.0;
                int living = maligaroClones.Count(cid => runner.Area.Monsters.TryGetValue(cid, out var m) && Alive(m));
                if (living < 3)
                {
                    await SummonMaligaroClones(runner, bossId, boss, pos, now);
                    return;
                }
            }

            // MaligaroTeleportSlam[6412]: blink behind target + heavy Phys/Chaos slam.
            if (now >= nextTeleSlam)
            {
                nextTeleSlam = now + 9.0;
                if (await CastAction(runner, bossId, boss, MaligaroEffects, MaligaroTeleSlam, aim, now, DamageType.Chaos, 1.5, 15, "MaligaroSoul", forceSpell: false) is { } slam)
                {
                    await SpawnTimedEffect(runner, effects, pos, MaligaroTrailArt, now + 1.2, scale: 1.15f);
                    var dest = RingSpot(runner, aim, Math.Atan2(aim.Y - pos.Y, aim.X - pos.X), 5);
                    await TeleportBoss(runner, bossId, boss, dest, now);
                    hits.Add(new(now + 1.0, bossId, boss, slam.Attack, aim, 15, MaligaroBladePoofArt));
                    return;
                }
            }

            // UnholyTrioTriggerCascade2Maligaro[8481]: Bladefall cascade + Blade Vortex.
            if (now >= nextCascade)
            {
                nextCascade = now + 7.0;
                if (await CastAction(runner, bossId, boss, MaligaroEffects, MaligaroCascade, aim, now, DamageType.Physical, 1.2, 13, "MaligaroSoul", forceSpell: true) is { } cas)
                {
                    double facing = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                    for (int i = 1; i <= 4; i++)
                    {
                        var spot = WalkableNear(runner, (
                            (int)Math.Round(pos.X + Math.Cos(facing) * (i * 9)),
                            (int)Math.Round(pos.Y + Math.Sin(facing) * (i * 9))));
                        await SpawnTimedEffect(runner, effects, spot, MaligaroBladePoofArt, now + 0.4 + i * 0.25 + 1.0, scale: 1.1f);
                        hits.Add(new(now + 0.45 + i * 0.25, bossId, boss, cas.Attack, spot, 10, null));
                    }
                    vortexOrigin = pos;
                    vortexUntil = now + 4.5;
                    nextVortexPulse = now + 0.8;
                    await SpawnTimedEffect(runner, effects, pos, MaligaroVortexArt, vortexUntil, scale: 1.2f);
                    return;
                }
            }

            if (dist <= 16)
            {
                if (await CastAction(runner, bossId, boss, MaligaroEffects, MaligaroCombo, aim, now, DamageType.Physical, 1.15, 14, "MaligaroSoul", forceSpell: false) is { } combo)
                    hits.Add(new(now + 0.6, bossId, boss, combo.Attack, aim, 13, null));
            }
            else
            {
                await Chase(runner, bossId, boss, aim, now, "MaligaroSoul");
            }
        }

        async Task SummonMaligaroClones(Player runner, uint bossId, SpawnedMonster boss, (int X, int Y) center, double now)
        {
            await CastAction(runner, bossId, boss, MaligaroEffects, MaligaroClonesSkill, center, now, DamageType.Chaos, 0.8, 12, "MaligaroSoul");
            int living = maligaroClones.Count(cid => runner.Area.Monsters.TryGetValue(cid, out var m) && Alive(m));
            for (int i = 0; i < MaligaroClones.Length && living + i < 3; i++)
            {
                var spot = RingSpot(runner, center, i * (Math.Tau / 3.0), 15);
                await SpawnTimedEffect(runner, effects, spot, MaligaroTrailArt, now + 1.5, scale: 1.0f);
                await SpawnAdd(runner, MaligaroClones[i], boss.Level, spot, bossId, maligaroClones,
                    ownedCurrent: () => !stopped && !maligaroCleaned);
            }
        }

        // ---- 3. Shavronne, Unbound ----

        async Task StepShavronne(Player runner, double now)
        {
            var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Variety?.Id == ShavronneSoul);
            if (entry.Value is not { } boss) return;
            uint bossId = entry.Key;

            if (!Alive(boss))
            {
                if (!shavCleaned)
                {
                    shavCleaned = true;
                    tornadoActive = false;
                }
                return;
            }

            await EnsureNativeSkills(runner, bossId, boss, ShavronneEffects, boundBosses);
            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 115, awakeId: bossId);
            if (target is null) return;

            await EnsureBossBar(runner, bossId, boss);
            boss.Target = target;
            boss.Aggro = true;
            boss.EntranceGrace = false;

            if (!shavInit)
            {
                shavInit = true;
                tornadoActive = true;
                tornadoX = pos.X;
                tornadoY = pos.Y;
                nextShavTeleport = now + 8.5;
                nextBrutusSlam = now + 6.5;
                nextStormBurst = now + 3.0;
                nextBallLightning = now + 10.5;
                nextTornadoTick = now + 1.0;
            }

            // Roaming ShavronneTornadoDaemon lightning cyclone tracking players.
            if (tornadoActive && now >= nextTornadoTick)
            {
                nextTornadoTick = now + 0.5;
                var goal = target.Walk.Position(now);
                double dx = goal.X - tornadoX, dy = goal.Y - tornadoY;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d > 2.0)
                {
                    double stepDist = Math.Min(d, 4.5);
                    tornadoX += dx / d * stepDist;
                    tornadoY += dy / d * stepDist;
                }
                var tSpot = WalkableNear(runner, ((int)Math.Round(tornadoX), (int)Math.Round(tornadoY)));
                if (now >= nextTornadoVisual)
                {
                    nextTornadoVisual = now + 1.3;
                    await SpawnTimedEffect(runner, effects, tSpot, ShavTornadoArt, now + 1.6, scale: 1.2f);
                }
                var tAtk = ElementalSpellAttack(boss, DamageType.Lightning, 0.7, radius: 10);
                await HitCircle(runner, bossId, boss, tAtk, tSpot, 10, () => !stopped && Alive(boss));
            }

            if (now < boss.BusyUntil || now < boss.FlinchUntil || MonsterActionFactor(boss, now) == 0) return;

            var aim = target.Walk.Position(now);

            // ShavronneGhostBrutusSlam[8514]: telegraphed spectral Brutus slam at target.
            if (now >= nextBrutusSlam)
            {
                nextBrutusSlam = now + 10.5;
                if (await CastAction(runner, bossId, boss, ShavronneEffects, ShavBrutusSlam, aim, now, DamageType.Lightning, 1.55, 16, "ShavronneSoul", forceSpell: true) is { } brutus)
                {
                    await SpawnTimedEffect(runner, effects, aim, ShavBrutusPrepArt, now + 1.55, scale: 1.3f);
                    hits.Add(new(now + 1.45, bossId, boss, brutus.Attack, aim, 16, ShavBrutusBigArt));
                    return;
                }
            }

            // ShavronneTeleportUnholyTrio[5068]: blink to an arena TeleportMarker.
            if (now >= nextShavTeleport)
            {
                nextShavTeleport = now + 9.5;
                var rng = AreaRng(runner);
                var dest = shavTeleportSpots.Count > 0
                    ? shavTeleportSpots[rng.Next(shavTeleportSpots.Count)]
                    : RingSpot(runner, aim, rng.NextDouble() * Math.Tau, 24);
                await CastAction(runner, bossId, boss, ShavronneEffects, ShavTeleport, dest, now, DamageType.Lightning, 0.9, 12, "ShavronneSoul", forceSpell: true);
                await TeleportBoss(runner, bossId, boss, dest, now);
                return;
            }

            // ShavronneBallLightning[8515]: charged lightning ring burst.
            if (now >= nextBallLightning)
            {
                nextBallLightning = now + 11.5;
                if (await CastAction(runner, bossId, boss, ShavronneEffects, ShavBallLightning, aim, now, DamageType.Lightning, 1.35, 14, "ShavronneSoul", forceSpell: true) is { } ball)
                {
                    await SpawnTimedEffect(runner, effects, pos, ShavBallChargeArt, now + 1.6, scale: 1.25f);
                    double facing = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                    for (int i = 1; i <= 3; i++)
                    {
                        var orbAt = RingSpot(runner, pos, facing, i * 12);
                        hits.Add(new(now + 1.0 + i * 0.45, bossId, boss, ball.Attack, orbAt, 11, ShavLightningBoltArt));
                    }
                    return;
                }
            }

            // ShavronneStormBurst[6583]: delayed orb explosions toward target.
            if (now >= nextStormBurst)
            {
                nextStormBurst = now + 4.2;
                if (await CastAction(runner, bossId, boss, ShavronneEffects, ShavStormBurst, aim, now, DamageType.Lightning, 1.05, 11, "ShavronneSoul", forceSpell: true) is { } burst)
                {
                    var rng = AreaRng(runner);
                    for (int i = 0; i < 3; i++)
                    {
                        var orb = i == 0 ? aim : RingSpot(runner, aim, rng.NextDouble() * Math.Tau, 10);
                        hits.Add(new(now + 0.7 + i * 0.3, bossId, boss, burst.Attack, orb, 10, ShavStormBurstArt));
                    }
                    return;
                }
            }

            if (Distance(pos, aim) > 42)
                await Chase(runner, bossId, boss, aim, now, "ShavronneSoul");
        }

        async Task StepHits(Player runner, double now)
        {
            for (int i = hits.Count - 1; i >= 0; i--)
            {
                var h = hits[i];
                if (now < h.At) continue;
                hits.RemoveAt(i);
                if (!Alive(h.Source)) continue;
                if (h.BurstAo is { } ao)
                    await SpawnTimedEffect(runner, effects, h.Spot, ao, now + 1.3, scale: 1.2f);
                await HitCircle(runner, h.SourceId, h.Source, h.Attack, h.Spot, h.Radius, () => !stopped && Alive(h.Source));
            }
        }
    }
}
