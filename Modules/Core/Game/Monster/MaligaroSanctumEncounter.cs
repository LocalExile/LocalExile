using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Act6To8Driver;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Act 7 — Maligaro's Sanctum (<c>2_7_5_map</c>) 3-phase sub-boss encounter controller:
/// <list type="bullet">
/// <item><b>Phase 1 (100%→66%)</b>: <c>Maligaro2[5692]</c> fights alone with <c>MaligaroTeleportSlam[6412]</c>,
/// <c>MaligaroWhirlingBlades[6434]</c>, <c>MaligaroTelekineticBlades*[6418,6420,6422,6424]</c>, and
/// <c>MaligaroComboAttack[6415]</c>. Any pre-placed Black Death / Fidelitas remain hidden and invulnerable.</item>
/// <item><b>Intermission 1 (66%)</b>: Maligaro casts <c>MaligaroVanish[6426]</c> (<c>WorldHidden</c> +
/// <c>SetInvulnerable</c>) and reveals/spawns <b>Black Death</b> (<c>BlackDeathMaligaro[5698]</c> — keeps normal
/// rewards so she drops <c>BlackVenom</c>), who casts <c>MaligaroBlackDeathMortarBarrage[6335]</c> and caustic pools.</item>
/// <item><b>Intermission 2 (33%)</b>: Second <c>MaligaroVanish[6426]</c> reveals/spawns <b>Fidelitas</b>
/// (<c>FourLeggedDemonMaligaro[5700]</c>) with real server-side <c>MaligaroDemonProximityShield[2848]</c>
/// (<c>SetEncounterProximityShield</c>), <c>RapidSlam[6433]</c>, and <c>AttackVortex[7807]</c>.</item>
/// <item><b>Finale (33%→0%)</b>: Maligaro returns with accelerated teleport slams and telekinetic blade volleys.</item>
/// </list>
/// </summary>
public static class MaligaroSanctumEncounter
{
    public const string Area = "2_7_5_map";
    public const string Maligaro = "Metadata/Monsters/Maligaro/Maligaro2";                            // MV 5692
    public const string BlackDeath = "Metadata/Monsters/Spiders/BlackDeath/BlackDeathMaligaro";       // MV 5698
    public const string Fidelitas = "Metadata/Monsters/DemonFourLegged/FourLeggedDemonMaligaro";      // MV 5700

    const string PhaseKey = "MaligaroSanctum";
    static readonly int[] PhaseThresholds = [66, 33];
    static readonly int[] MaligaroEffects = [406, 6415, 6412, 6426, 6434, 6418, 5596, 6420, 6422, 6424];
    static readonly int[] BlackDeathEffects = [420, 6335, 6428, 6427];
    static readonly int[] FidelitasEffects = [420, 6433, 7807, 2848];

    const ushort ProximityShieldAuraBuff = 227; // proximity_shield_aura
    const int FidelitasShieldRadius = 32;

    const string ComboArt = "Metadata/Effects/Spells/monsters_effects/maligaro_act7_e/combo_attack/combo_attack.ao";
    const string BladeSummonArt = "Metadata/Effects/Spells/monsters_effects/maligaro_act7_e/telekinetic_blade/blade_summon.ao";
    const string BladeProjArt = "Metadata/Effects/Spells/monsters_effects/maligaro_act7_e/telekinetic_blade/maligaro_projectile.ao";
    const string DesecrateArt = "Metadata/Effects/Spells/monsters_effects/act4/maligaro/desecrate_ground/ground_01.ao";
    const string BlackDeathMortarArt = "Metadata/Effects/Spells/monsters_effects/maligaro_act7_e/blackdeath/purple_lightning_mortar/mortarimpact.ao";
    const string BlackDeathPoofArt = "Metadata/Effects/Spells/monsters_effects/maligaro_act7_e/blackdeath/purple_poison/poisonpoof.ao";
    const string FidelitasShieldArt = "Metadata/Effects/Spells/monsters_effects/maligaro_act7_e/fourleggeddemon/lightning_shield/lightning_shield.ao";
    const string FidelitasSlamArt = "Metadata/Effects/Spells/monsters_effects/maligaro_act7_e/fourleggeddemon/ground_slam/groundslam.ao";
    const string FidelitasStrikeArt = "Metadata/Effects/Spells/monsters_effects/maligaro_act7_e/fourleggeddemon/purple_lightning_strike/lightning_strike.ao";

    static readonly RuleTable<InstanceState, Fight> Fights = new();
    static readonly RuleTable<Player, ArenaHazards.Exposure> CausticExposures = new();

    public static async Task Tick(Player player)
    {
        if (player.Layout.Id != Area || !CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        if (!Fights.TryGetValue(player.Instance, out var fight))
        {
            if (!player.Area.Monsters.Values.Any(m => m.Hostile && m.Variety?.Id is Maligaro or BlackDeath or Fidelitas))
                return;
            fight = Fights.GetValue(player.Instance, _ => new Fight(player));
        }
        await fight.Gate.WaitAsync();
        try
        {
            if (IsMonsterRunner(player))
                await fight.Step(player, AreaTime(player));
        }
        finally { fight.Gate.Release(); }
    }

    public static Task Leaving(Player player)
    {
        if (CausticExposures.TryGetValue(player, out var exp)) exp.Last = double.NaN;
        return Task.CompletedTask;
    }

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights) pair.Value.StopAndClear();
            foreach (var pair in CausticExposures) pair.Value.Last = double.NaN;
        }
    }

    sealed record CausticPool((int X, int Y) At, double Ends, double Radius);

    sealed class Fight
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly GameArea area;
        readonly (int X, int Y)? cachedMiddle;

        readonly List<TimedEffect> effects = [];
        readonly List<ScheduledHit> hits = [];
        readonly List<CausticPool> pools = [];

        uint maligaroId, blackDeathId, fidelitasId;
        (int X, int Y) arenaCenter;
        bool initialized, vanished, cleaned, stopped;
        int activeSubBoss; // 0 = none, 1 = Black Death, 2 = Fidelitas
        double nextChaseAt, nextTeleportSlamAt, nextWhirlAt, nextBladesAt;
        double nextSubChaseAt, nextMortarAt, nextCausticNovaAt, nextRapidSlamAt, nextVortexAt;

        public Fight(Player owner)
        {
            area = owner.Area;
            cachedMiddle = AreaPlacements(owner).Of("Metadata/MiscellaneousObjects/ArenaMiddle").FirstOrDefault() is { } m
                ? (m.X, m.Y) : null;
        }

        public void StopAndClear()
        {
            stopped = true;
            if (maligaroId != 0 && area.Monsters.TryGetValue(maligaroId, out var m))
            {
                ClearInvulnerableOnUnload(area, maligaroId, m, CannotBeDamagedBuff);
                m.WorldHidden = false;
            }
            if (blackDeathId != 0 && area.Monsters.TryGetValue(blackDeathId, out var bd))
            {
                ClearInvulnerableOnUnload(area, blackDeathId, bd, CannotBeDamagedBuff);
                bd.WorldHidden = false;
            }
            if (fidelitasId != 0 && area.Monsters.TryGetValue(fidelitasId, out var fid))
            {
                ClearInvulnerableOnUnload(area, fidelitasId, fid, CannotBeDamagedBuff);
                SetEncounterProximityShield(area, fid, 0);
                area.Buffs.Clear(fidelitasId, ProximityShieldAuraBuff, fidelitasId);
                fid.WorldHidden = false;
            }
        }

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            await ResolveHits(runner, hits, effects, now, () => !stopped);
            pools.RemoveAll(p => now >= p.Ends);

            // Read the boss by its remembered id first; scan the area only when that lookup is gone.
            SpawnedMonster? maligaro = null;
            if (maligaroId != 0 && runner.Area.Monsters.TryGetValue(maligaroId, out var cached) && cached.Hostile && cached.Variety?.Id == Maligaro)
                maligaro = cached;
            else
            {
                var mEntry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == Maligaro);
                if (mEntry.Value is { } found)
                {
                    maligaroId = mEntry.Key;
                    maligaro = found;
                }
            }
            if (maligaro is null)
                return;

            if (!Alive(maligaro))
            {
                if (!cleaned) await Cleanup(runner, maligaro);
                return;
            }

            SyncSavedPhases(runner.Instance, maligaro, PhaseKey, PhaseThresholds);
            // Read each sub-boss by its remembered id first; scan the area only when that lookup is gone.
            SpawnedMonster? bdHold = null;
            if (blackDeathId != 0 && runner.Area.Monsters.TryGetValue(blackDeathId, out var cachedBd) && cachedBd.Hostile && cachedBd.Variety?.Id == BlackDeath)
                bdHold = cachedBd;
            else
            {
                var bdEntry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == BlackDeath);
                if (bdEntry.Value is { } foundBd)
                {
                    blackDeathId = bdEntry.Key;
                    bdHold = foundBd;
                }
            }
            SpawnedMonster? fidHold = null;
            if (fidelitasId != 0 && runner.Area.Monsters.TryGetValue(fidelitasId, out var cachedFid) && cachedFid.Hostile && cachedFid.Variety?.Id == Fidelitas)
                fidHold = cachedFid;
            else
            {
                var fidEntry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == Fidelitas);
                if (fidEntry.Value is { } foundFid)
                {
                    fidelitasId = fidEntry.Key;
                    fidHold = foundFid;
                }
            }
            if (activeSubBoss != 1 && bdHold is { } holdBd && Alive(holdBd))
                await HoldSubBoss(runner, blackDeathId, holdBd, hold: true);
            if (activeSubBoss != 2 && fidHold is { } holdFid && Alive(holdFid))
                await HoldSubBoss(runner, fidelitasId, holdFid, hold: true);

            var pos = maligaro.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 135, awakeId: vanished ? null : maligaroId);
            if (target is null)
            {
                ResetCausticExposures(runner);
                return;
            }

            if (!initialized)
            {
                initialized = true;
                arenaCenter = cachedMiddle ?? pos;
                maligaro.Aggro = true;
                MarkStarted(runner.Instance, PhaseKey);
                await BindEffects(runner, maligaroId, maligaro, MaligaroEffects);
                await EnsureBossBar(runner, maligaroId, maligaro);
                nextTeleportSlamAt = now + 5.5;
                nextWhirlAt = now + 3.5;
                nextBladesAt = now + 7.5;
                Console.WriteLine($"[gateway] Maligaro Sanctum controller active for 0x{maligaroId:x} (phases done {maligaro.LifePhases!.Completed})");
            }
            else
            {
                maligaro.Target = target;
                if (!vanished) await EnsureBossBar(runner, maligaroId, maligaro);
            }

            await StepCausticGround(runner, maligaro, now);

            var phases = maligaro.LifePhases!;
            if (vanished)
            {
                if (activeSubBoss == 1)
                {
                    if (blackDeathId != 0 && runner.Area.Monsters.TryGetValue(blackDeathId, out var bd) && Alive(bd))
                    {
                        await StepBlackDeath(runner, blackDeathId, bd, target, now);
                        return;
                    }
                    await ReturnFromVanish(runner, maligaro, phases, now, "Black Death defeated");
                    return;
                }
                if (activeSubBoss == 2)
                {
                    if (fidelitasId != 0 && runner.Area.Monsters.TryGetValue(fidelitasId, out var fid) && Alive(fid))
                    {
                        await StepFidelitas(runner, fidelitasId, fid, target, now);
                        return;
                    }
                    await ReturnFromVanish(runner, maligaro, phases, now, "Fidelitas defeated");
                    return;
                }
            }

            // Ensure Maligaro is vulnerable and visible outside intermissions
            if (maligaro.Effects[Stat.BaseCannotBeDamaged] != 0)
                SetInvulnerable(runner, maligaroId, maligaro, false, CannotBeDamagedBuff);
            if (maligaro.WorldHidden)
                await SetWorldHidden(runner, maligaroId, maligaro, false);

            // Trigger 66% (Black Death) and 33% (Fidelitas) Vanish intermissions
            if (!phases.Active && phases.Completed < 2 && phases.Begin(maligaro.Life, maligaro.MaxLife))
            {
                int sub = phases.Completed == 0 ? 1 : 2;
                await BeginVanish(runner, maligaro, sub, now);
                return;
            }

            if (!CanAct(maligaro, now)) return;

            var aim = target.Walk.Position(now);
            double dist = Distance(pos, aim);
            bool finale = phases.Completed >= 2;

            // MaligaroTeleportSlam[6412]
            if (now >= nextTeleportSlamAt && dist <= 65)
            {
                nextTeleportSlamAt = now + (finale ? 6.5 : 8.5);
                var landing = WalkableNear(runner, aim);
                if (await CastAction(runner, maligaroId, maligaro, 6412, landing, 1.35, now, "[Maligaro] Teleport Slam") is { } gen)
                {
                    maligaro.Walk.Teleport(landing.X, landing.Y, now);
                    await SendWorldRecord(runner, maligaroId, InstanceProtocol.Teleport(maligaroId, landing.X, landing.Y), null);
                    await SpawnTimedEffect(runner, effects, landing, ComboArt, now + 1.2, scale: 1.25f);
                    hits.Add(new(now + 0.85, maligaroId, maligaro, landing, 14,
                        HybridAttack(maligaro, DamageType.Chaos, 0.9, 0.7, 14), gen, DesecrateArt, 1.15f));
                    return;
                }
            }

            // MaligaroTelekineticBlades*[6418/6420/6422/6424]
            if (now >= nextBladesAt && dist <= 65)
            {
                nextBladesAt = now + (finale ? 5.5 : 7.5);
                int[] bladeEffects = [6418, 6420, 6422, 6424];
                int eff = bladeEffects[AreaRng(runner).Next(bladeEffects.Length)];
                if (await CastAction(runner, maligaroId, maligaro, eff, aim, 1.15, now, "[Maligaro] Telekinetic Blades") is { } gen)
                {
                    double baseAngle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                    var atk = HybridAttack(maligaro, DamageType.Chaos, 0.65, 0.65, 9, spell: true);
                    for (int i = -1; i <= 1; i++)
                    {
                        double a = baseAngle + i * 0.25;
                        var drop = WalkableNear(runner, (
                            (int)Math.Round(pos.X + Math.Cos(a) * Math.Min(45, dist + 6)),
                            (int)Math.Round(pos.Y + Math.Sin(a) * Math.Min(45, dist + 6))));
                        await SpawnTimedEffect(runner, effects, drop, BladeSummonArt, now + 0.9, scale: 1.0f);
                        hits.Add(new(now + 0.7 + Math.Abs(i) * 0.1, maligaroId, maligaro, drop, 9, atk, gen, BladeProjArt, 1.0f));
                    }
                    return;
                }
            }

            // MaligaroWhirlingBlades[6434]
            if (now >= nextWhirlAt && dist is >= 10 and <= 48)
            {
                nextWhirlAt = now + (finale ? 5.0 : 6.5);
                double angle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                var dest = WalkableNear(runner, (
                    (int)Math.Round(pos.X + Math.Cos(angle) * Math.Min(34, dist + 8)),
                    (int)Math.Round(pos.Y + Math.Sin(angle) * Math.Min(34, dist + 8))));
                if (await CastAction(runner, maligaroId, maligaro, 6434, dest, 1.0, now, "[Maligaro] Whirling Blades") is { } gen)
                {
                    var mid = ((pos.X + dest.X) / 2, (pos.Y + dest.Y) / 2);
                    maligaro.Walk.Teleport(dest.X, dest.Y, now);
                    await SendWorldRecord(runner, maligaroId, InstanceProtocol.Teleport(maligaroId, dest.X, dest.Y), null);
                    var atk = HybridAttack(maligaro, DamageType.Chaos, 0.8, 0.45, 11);
                    hits.Add(new(now + 0.35, maligaroId, maligaro, mid, 10, atk, gen));
                    hits.Add(new(now + 0.6, maligaroId, maligaro, dest, 11, atk, gen, ComboArt, 1.0f));
                    return;
                }
            }

            nextChaseAt = await StepChaseOrMelee(runner, maligaroId, maligaro, target, now, nextChaseAt, hits, meleeScale: 1.05, meleeEffect: 6415);
        }

        async Task BeginVanish(Player runner, SpawnedMonster maligaro, int sub, double now)
        {
            vanished = true;
            activeSubBoss = sub;
            var pos = maligaro.Walk.Position(now);
            // MaligaroVanish[6426] (ActionType 636, tgt=[4]): OnStart reads targetEntity->Positioned unchecked, so
            // it must carry Maligaro's own object id rather than 0.
            await CastAction(runner, maligaroId, maligaro, 6426, pos, 0.8, now, "[Maligaro] Vanish", targetObjectId: maligaroId);
            SetInvulnerable(runner, maligaroId, maligaro, true, CannotBeDamagedBuff);
            await SetWorldHidden(runner, maligaroId, maligaro, true);

            if (sub == 1)
            {
                // Reveal existing Black Death or spawn her (keep NoRewards = false so BlackVenom quest drop fires!)
                var existing = runner.Area.Monsters.FirstOrDefault(p => p.Value.Variety?.Id == BlackDeath && Alive(p.Value));
                if (existing.Value is { } bd)
                {
                    blackDeathId = existing.Key;
                    await HoldSubBoss(runner, blackDeathId, bd, hold: false);
                    bd.Walk.Teleport(arenaCenter.X, arenaCenter.Y, now);
                    await SendWorldRecord(runner, blackDeathId, InstanceProtocol.Teleport(blackDeathId, arenaCenter.X, arenaCenter.Y), null);
                }
                else if (Monsters.Find(BlackDeath) is { Supported: true, Id: BlackDeath } bdVar)
                {
                    var spot = WalkableNear(runner, arenaCenter);
                    blackDeathId = await SpawnMonster(runner, bdVar, maligaro.Level, MonsterRarity.Unique, spot.X, spot.Y,
                        instanceOwned: true, owned: (_, m) => { m.Aggro = true; m.NoRewards = false; },
                        ownedCurrent: () => !stopped && !cleaned);
                }
                if (blackDeathId != 0 && runner.Area.Monsters.TryGetValue(blackDeathId, out var bdActor))
                    await BindEffects(runner, blackDeathId, bdActor, BlackDeathEffects);
                nextMortarAt = now + 2.5;
                nextCausticNovaAt = now + 5.0;
                Console.WriteLine($"[gateway] Maligaro 0x{maligaroId:x}: Vanished -> Black Death 0x{blackDeathId:x} enters");
            }
            else
            {
                // Reveal existing Fidelitas or spawn him with MaligaroDemonProximityShield[2848]
                var existing = runner.Area.Monsters.FirstOrDefault(p => p.Value.Variety?.Id == Fidelitas && Alive(p.Value));
                if (existing.Value is { } fid)
                {
                    fidelitasId = existing.Key;
                    await HoldSubBoss(runner, fidelitasId, fid, hold: false);
                    fid.Walk.Teleport(arenaCenter.X, arenaCenter.Y, now);
                    await SendWorldRecord(runner, fidelitasId, InstanceProtocol.Teleport(fidelitasId, arenaCenter.X, arenaCenter.Y), null);
                }
                else if (Monsters.Find(Fidelitas) is { Supported: true, Id: Fidelitas } fidVar)
                {
                    var spot = WalkableNear(runner, arenaCenter);
                    fidelitasId = await SpawnMonster(runner, fidVar, maligaro.Level, MonsterRarity.Unique, spot.X, spot.Y,
                        instanceOwned: true, owned: (_, m) => { m.Aggro = true; m.NoRewards = true; },
                        ownedCurrent: () => !stopped && !cleaned);
                }
                if (fidelitasId != 0 && runner.Area.Monsters.TryGetValue(fidelitasId, out var fidActor))
                {
                    await BindEffects(runner, fidelitasId, fidActor, FidelitasEffects);
                    SetEncounterProximityShield(runner.Area, fidActor, FidelitasShieldRadius);
                    runner.Area.Buffs.Set(fidelitasId, new(ProximityShieldAuraBuff, fidelitasId));
                    await SpawnTimedEffect(runner, effects, fidActor.Walk.Position(now), FidelitasShieldArt, now + 4.0, scale: 1.25f);
                }
                nextRapidSlamAt = now + 3.0;
                nextVortexAt = now + 5.5;
                Console.WriteLine($"[gateway] Maligaro 0x{maligaroId:x}: Vanished -> Fidelitas 0x{fidelitasId:x} enters with Proximity Shield");
            }
        }

        async Task ReturnFromVanish(Player runner, SpawnedMonster maligaro, LifePhaseGate phases, double now, string reason)
        {
            vanished = false;
            activeSubBoss = 0;
            SetInvulnerable(runner, maligaroId, maligaro, false, CannotBeDamagedBuff);
            await SetWorldHidden(runner, maligaroId, maligaro, false);
            maligaro.Walk.Teleport(arenaCenter.X, arenaCenter.Y, now);
            await SendWorldRecord(runner, maligaroId, InstanceProtocol.Teleport(maligaroId, arenaCenter.X, arenaCenter.Y), null);
            MarkPhaseCompleted(runner.Instance, PhaseKey, phases.Completed);
            phases.Finish();
            nextTeleportSlamAt = now + 3.0;
            nextBladesAt = now + 4.5;
            Console.WriteLine($"[gateway] Maligaro 0x{maligaroId:x}: {reason}; Maligaro returns ({phases.Completed}/2 intermissions complete)");
        }

        async Task HoldSubBoss(Player runner, uint id, SpawnedMonster m, bool hold)
        {
            if (hold)
            {
                if (m.Effects[Stat.BaseCannotBeDamaged] == 0)
                    SetInvulnerable(runner, id, m, true, CannotBeDamagedBuff);
                if (!m.WorldHidden)
                    await SetWorldHidden(runner, id, m, true);
                m.EntranceGrace = true;
            }
            else
            {
                m.EntranceGrace = false;
                m.Aggro = true;
                SetInvulnerable(runner, id, m, false, CannotBeDamagedBuff);
                await SetWorldHidden(runner, id, m, false);
            }
        }

        async Task StepBlackDeath(Player runner, uint id, SpawnedMonster bd, Player target, double now)
        {
            bd.Target = target;
            if (!CanAct(bd, now)) return;
            var pos = bd.Walk.Position(now);
            var aim = target.Walk.Position(now);

            // MaligaroBlackDeathMortarBarrage[6335] — caustic mortar barrage
            if (now >= nextMortarAt && Distance(pos, aim) <= 68)
            {
                nextMortarAt = now + 5.5;
                if (await CastAction(runner, id, bd, 6335, aim, 1.35, now, "[Black Death] Caustic Mortar Barrage") is { } gen)
                {
                    var rng = AreaRng(runner);
                    var atk = ElementalSpellAttack(bd, DamageType.Chaos, 1.0, 10.5);
                    for (int i = 0; i < 4; i++)
                    {
                        var drop = RingSpot(runner, aim, rng.NextDouble() * Math.Tau, i == 0 ? 0 : 6 + i * 4);
                        await SpawnTimedEffect(runner, effects, drop, BlackDeathPoofArt, now + 0.8 + i * 0.2, scale: 1.05f);
                        hits.Add(new(now + 0.75 + i * 0.2, id, bd, drop, 10, atk, gen, BlackDeathMortarArt, 1.1f));
                        pools.Add(new(drop, now + 7.0, 8.5));
                    }
                    return;
                }
            }

            // NovaProjectileSpellBlackDeathCausticGround[6428]
            if (now >= nextCausticNovaAt && Distance(pos, aim) <= 40)
            {
                nextCausticNovaAt = now + 7.5;
                if (await CastAction(runner, id, bd, 6428, aim, 1.1, now, "[Black Death] Caustic Nova") is { } gen)
                {
                    await SpawnTimedEffect(runner, effects, pos, BlackDeathPoofArt, now + 1.3, scale: 1.3f);
                    pools.Add(new(pos, now + 7.5, 13.0));
                    hits.Add(new(now + 0.6, id, bd, pos, 18,
                        ElementalSpellAttack(bd, DamageType.Chaos, 1.05, 18), gen, BlackDeathMortarArt, 1.2f));
                    return;
                }
            }

            nextSubChaseAt = await StepChaseOrMelee(runner, id, bd, target, now, nextSubChaseAt, hits, meleeScale: 1.0, meleeEffect: 420);
        }

        async Task StepFidelitas(Player runner, uint id, SpawnedMonster fid, Player target, double now)
        {
            fid.Target = target;
            if (fid.Effects[Stat.CannotBeDamagedByThingsOutsideRadius] == 0)
            {
                SetEncounterProximityShield(runner.Area, fid, FidelitasShieldRadius);
                runner.Area.Buffs.Set(id, new(ProximityShieldAuraBuff, id));
            }
            if (!CanAct(fid, now)) return;
            var pos = fid.Walk.Position(now);
            var aim = target.Walk.Position(now);
            double dist = Distance(pos, aim);

            // RapidSlam[6433]
            if (now >= nextRapidSlamAt && dist <= 34)
            {
                nextRapidSlamAt = now + 5.5;
                if (await CastAction(runner, id, fid, 6433, aim, 1.4, now, "[Fidelitas] Rapid Slam") is { } gen)
                {
                    var atk = HybridAttack(fid, DamageType.Lightning, 0.8, 0.75, 13);
                    for (int s = 0; s < 3; s++)
                        hits.Add(new(now + 0.4 + s * 0.35, id, fid, aim, 13, atk, gen, FidelitasSlamArt, 1.15f));
                    return;
                }
            }

            // AttackVortex[7807] — sweeping lightning strike vortex
            if (now >= nextVortexAt && dist <= 30)
            {
                nextVortexAt = now + 7.0;
                if (await CastAction(runner, id, fid, 7807, aim, 1.25, now, "[Fidelitas] Attack Vortex") is { } gen)
                {
                    await SpawnTimedEffect(runner, effects, pos, FidelitasStrikeArt, now + 1.3, scale: 1.3f);
                    hits.Add(new(now + 0.75, id, fid, pos, 20,
                        HybridAttack(fid, DamageType.Lightning, 0.85, 0.85, 20), gen, FidelitasStrikeArt, 1.2f));
                    return;
                }
            }

            nextSubChaseAt = await StepChaseOrMelee(runner, id, fid, target, now, nextSubChaseAt, hits, meleeScale: 1.1, meleeEffect: 420);
        }

        async Task StepCausticGround(Player runner, SpawnedMonster source, double now)
        {
            if (pools.Count == 0)
            {
                ResetCausticExposures(runner);
                return;
            }
            var (min, max) = Monsters.BaseDamage(source.Variety!, source.Level) ?? (25, 35);
            double dps = (min + max) * 0.45;
            foreach (var p in ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)))
            {
                var exp = CausticExposures.GetValue(p, _ => new());
                await ArenaHazards.Ground(p, exp, now, (at, _) =>
                    pools.Any(z => Distance(at, z.At) <= z.Radius)
                        ? DamagePacket.Of(DamageType.Chaos, dps, dps)
                        : DamagePacket.Empty,
                    () => !stopped);
            }
        }

        static void ResetCausticExposures(Player runner)
        {
            foreach (var p in ActivePlayers(runner))
                if (CausticExposures.TryGetValue(p, out var exp)) exp.Last = double.NaN;
        }

        async Task Cleanup(Player runner, SpawnedMonster maligaro)
        {
            cleaned = true;
            vanished = false;
            SetInvulnerable(runner, maligaroId, maligaro, false, CannotBeDamagedBuff);
            if (fidelitasId != 0 && runner.Area.Monsters.TryGetValue(fidelitasId, out var fid))
            {
                SetEncounterProximityShield(runner.Area, fid, 0);
                runner.Area.Buffs.Clear(fidelitasId, ProximityShieldAuraBuff, fidelitasId);
            }
            hits.Clear();
            pools.Clear();
            ResetCausticExposures(runner);
            await ClearAllEffects(runner, effects);
            Console.WriteLine("[gateway] Maligaro defeated; Sanctum hazards cleared");
        }
    }
}
