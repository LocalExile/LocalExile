using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Act6To8Driver;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Act 6 — Shavronne's Tower (<c>2_6_7_2</c>) Shavronne the Returned &amp; Reassembled Brutus 2-phase encounter:
/// <list type="bullet">
/// <item><b>Phase 1</b>: Shavronne (<c>ShavronneTower[5488]</c>) fights alone while Reassembled Brutus
/// (<c>BruteTower[5482]</c>) remains dormant/invulnerable at <c>BruteCradlePart2</c>. Shavronne teleports
/// (<c>ShavronneTeleportTower[5066]</c>), summons <c>ShavronneTowerLightningBook[5492]</c> (<i>Tome of Thunder</i>,
/// <c>NoRewards = true</c>), and casts <c>ShavronneLightningPulse[6671]</c> and <c>ShavronneProjectileBarrage[6715]</c>.</item>
/// <item><b>Phase 2</b> (triggered at 50% Shavronne HP or if Shavronne falls): Reassembled Brutus awakens while
/// Shavronne levitates and channels <c>ShavronneEmpowerBeam[6691]</c>, empowering Brutus's
/// <c>BrutusLightningMultiSlam[6711]</c>, <c>BrutusWallHookEmpowered[6368]</c> (chain pull + lightning impact), and
/// <c>BrutusLevitationSlam[6369]</c>. Survives portal re-entry via <c>SyncSavedPhases</c> and handles either boss
/// dying first cleanly.</item>
/// </list>
/// </summary>
public static class ShavronneBrutusEncounter
{
    public const string Area = "2_6_7_2";
    public const string Shavronne = "Metadata/Monsters/Shavronne/ShavronneTower";             // MV 5488
    public const string Brutus = "Metadata/Monsters/Brute/BruteTower";                        // MV 5482
    public const string LightningBook = "Metadata/Monsters/Totems/ShavronneTowerLightningBook"; // MV 5492

    const string PhaseKey = "ShavronneBrutus";
    static readonly int[] PhaseThresholds = [50];
    static readonly int[] ShavronneEffects = [3804, 6671, 5066, 6673, 6674, 6691, 6675, 6715];
    static readonly int[] BrutusEffects = [492, 493, 6709, 6711, 6367, 6368, 6369];

    const string EmpowerBeamArt = "Metadata/Effects/Spells/monsters_effects/atlasofworldsbosses/vilainther/act/empower_beam_01.ao";
    const string LightningBoltsArt = "Metadata/Effects/Spells/monsters_effects/act4/shavronne/lightning_touch/lightningtouchbolts_shav.ao";
    const string LightningGlowArt = "Metadata/Effects/Spells/monsters_effects/act4/shavronne/lightning_touch/lightningtouchglow_shav.ao";
    const string ProjectileExplodeArt = "Metadata/Effects/Spells/monsters_effects/act4/shavronne/projectile_explode.ao";
    const string BrutusBigSlamArt = "Metadata/Effects/Spells/monsters_effects/act4/shavronne/bruteslam_big.ao";
    const string BrutusSmallSlamArt = "Metadata/Effects/Spells/monsters_effects/act4/shavronne/bruteslam_smol.ao";

    static readonly RuleTable<InstanceState, Fight> Fights = new();

    public static async Task Tick(Player player)
    {
        if (player.Layout.Id != Area || !CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        if (!Fights.TryGetValue(player.Instance, out var fight))
        {
            if (!player.Area.Monsters.Values.Any(m => m.Hostile && m.Variety?.Id is Shavronne or Brutus))
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

    public static Task Leaving(Player player) => Task.CompletedTask;

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights) pair.Value.StopAndClear();
        }
    }

    sealed record PendingHook(double At, uint TargetId, (int X, int Y) BossPos, (int X, int Y) AimSpot,
        bool Empowered, int Generation);

    sealed class Fight
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly GameArea area;
        readonly (int X, int Y)? cachedMiddle;
        readonly (int X, int Y)? cachedCradle;

        readonly List<TimedEffect> effects = [];
        readonly List<ScheduledHit> hits = [];
        readonly List<PendingHook> pendingHooks = [];
        readonly HashSet<uint> books = [];

        uint shavId, brutusId;
        (int X, int Y) arenaCenter;
        bool initialized, phase2Active, cleaned, stopped;
        double nextShavChaseAt, nextTeleportAt, nextPulseAt, nextBarrageAt, nextBookAt, nextBookPulseAt, nextEmpowerAt;
        double nextBrutusChaseAt, nextMultiSlamAt, nextHookAt, nextLevSlamAt;

        public Fight(Player owner)
        {
            area = owner.Area;
            var placed = AreaPlacements(owner);
            cachedMiddle = placed.Of("Metadata/Monsters/Shavronne/ShavronneArenaMiddle").FirstOrDefault() is { } m ? (m.X, m.Y) : null;
            cachedCradle = placed.Of("Metadata/Monsters/Brute/BruteCradlePart2").FirstOrDefault() is { } c ? (c.X, c.Y) : null;
        }

        public void StopAndClear()
        {
            stopped = true;
            if (brutusId != 0 && area.Monsters.TryGetValue(brutusId, out var br))
                ClearInvulnerableOnUnload(area, brutusId, br, CannotBeDamagedBuff);
            if (shavId != 0 && area.Monsters.TryGetValue(shavId, out var sh))
                ClearInvulnerableOnUnload(area, shavId, sh, CannotBeDamagedBuff);
        }

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            await ResolveHits(runner, hits, effects, now, () => !stopped);

            var shavEntry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == Shavronne);
            var brutusEntry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == Brutus);
            shavId = shavEntry.Key;
            brutusId = brutusEntry.Key;
            var shav = shavEntry.Value;
            var brutus = brutusEntry.Value;

            bool shavAlive = Alive(shav);
            bool brutusAlive = Alive(brutus);
            if (!shavAlive && !brutusAlive)
            {
                if (!cleaned) await Cleanup(runner);
                return;
            }

            if (shav is not null)
                SyncSavedPhases(runner.Instance, shav, PhaseKey, PhaseThresholds);

            // Hold Brutus dormant and invulnerable before Phase 2 awakens (unless Shavronne already died/crossed 50%).
            bool savedPhase2 = (shav?.LifePhases?.Completed ?? 0) > 0 || !shavAlive || IsPhase2Flagged(runner.Instance);
            if (savedPhase2 && !phase2Active)
                phase2Active = true;

            if (!phase2Active && brutusAlive && brutus is not null)
            {
                if (brutus.Effects[Stat.BaseCannotBeDamaged] == 0)
                    SetInvulnerable(runner, brutusId, brutus, true, CannotBeDamagedBuff);
                brutus.EntranceGrace = true;
            }

            var anchorPos = shavAlive ? shav!.Walk.Position(now) : brutus!.Walk.Position(now);
            var target = NearestLivingTarget(runner, anchorPos, now, maxRange: 130, awakeId: shavAlive ? shavId : brutusId);
            if (target is null) return;

            if (!initialized)
            {
                initialized = true;
                arenaCenter = cachedMiddle ?? anchorPos;
                MarkStarted(runner.Instance, PhaseKey);
                if (shav is not null)
                {
                    shav.Aggro = true;
                    await BindEffects(runner, shavId, shav, ShavronneEffects);
                    await EnsureBossBar(runner, shavId, shav);
                }
                if (brutus is not null)
                    await BindEffects(runner, brutusId, brutus, BrutusEffects);
                nextTeleportAt = now + 7.5;
                nextPulseAt = now + 4.5;
                nextBarrageAt = now + 6.0;
                nextBookAt = now + 5.5;
                nextBookPulseAt = now + 3.5;
                nextEmpowerAt = now + 2.5;
                nextMultiSlamAt = now + 4.5;
                nextHookAt = now + 7.5;
                nextLevSlamAt = now + 10.0;
                Console.WriteLine($"[gateway] Shavronne & Brutus controller active (phase2={phase2Active})");
            }

            // Transition to Phase 2 at 50% Shavronne HP or if Shavronne falls
            if (!phase2Active && (!shavAlive || (shav!.LifePhases is { } ph && ph.Begin(shav.Life, shav.MaxLife))))
            {
                phase2Active = true;
                MarkPhaseCompleted(runner.Instance, PhaseKey, 0);
                if (shav?.LifePhases is { Active: true } ph2) ph2.Finish();
                if (brutusAlive && brutus is not null)
                {
                    brutus.EntranceGrace = false;
                    brutus.Aggro = true;
                    brutus.Target = target;
                    SetInvulnerable(runner, brutusId, brutus, false, CannotBeDamagedBuff);
                    await EnsureBossBar(runner, brutusId, brutus);
                    var bPos = brutus.Walk.Position(now);
                    await SpawnTimedEffect(runner, effects, bPos, LightningGlowArt, now + 2.2, scale: 1.4f);
                }
                Console.WriteLine("[gateway] Shavronne & Brutus: Phase 2 — Reassembled Brutus awakened!");
            }

            // Ensure Brutus is unlocked in Phase 2 even after portal re-entry
            if (phase2Active && brutusAlive && brutus is not null)
            {
                if (brutus.Effects[Stat.BaseCannotBeDamaged] != 0)
                    SetInvulnerable(runner, brutusId, brutus, false, CannotBeDamagedBuff);
                brutus.EntranceGrace = false;
                brutus.Aggro = true;
                brutus.Target = target;
                await EnsureBossBar(runner, brutusId, brutus);
            }

            await StepBooks(runner, target, now);

            if (shavAlive && shav is not null)
                await StepShavronne(runner, shav, brutusAlive ? brutus : null, target, now);

            if (phase2Active && brutusAlive && brutus is not null)
            {
                await StepPendingHooks(runner, brutus, now);
                await StepBrutus(runner, brutus, empowered: shavAlive, target, now);
            }
        }

        static bool IsPhase2Flagged(InstanceState instance) => instance.HasState(PhaseFlag(PhaseKey, 0));

        async Task StepShavronne(Player runner, SpawnedMonster shav, SpawnedMonster? brutus, Player target, double now)
        {
            shav.Target = target;
            await EnsureBossBar(runner, shavId, shav);
            if (!CanAct(shav, now)) return;

            var pos = shav.Walk.Position(now);
            var aim = target.Walk.Position(now);

            // In Phase 2 while Brutus lives, Shavronne periodically channels ShavronneEmpowerBeam[6691] onto Brutus
            if (phase2Active && brutus is not null && Alive(brutus) && now >= nextEmpowerAt)
            {
                nextEmpowerAt = now + 9.0;
                var bPos = brutus.Walk.Position(now);
                if (await CastAction(runner, shavId, shav, 6691, bPos, 2.0, now, "[Shavronne] Empower Beam") is not null)
                {
                    await SpawnTimedEffect(runner, effects, pos, EmpowerBeamArt, now + 2.2, scale: 1.2f);
                    await SpawnTimedEffect(runner, effects, bPos, LightningGlowArt, now + 2.2, scale: 1.2f);
                    return;
                }
            }

            // Summon Tome of Thunder (ShavronneTowerLightningBook[5492], NoRewards = true)
            if (now >= nextBookAt)
            {
                nextBookAt = now + (phase2Active ? 13.0 : 10.5);
                int living = books.Count(id => runner.Area.Monsters.TryGetValue(id, out var b) && Alive(b));
                if (living < 3)
                {
                    var rng = AreaRng(runner);
                    var spot = RingSpot(runner, aim, rng.NextDouble() * Math.Tau, 15);
                    if (await CastAction(runner, shavId, shav, 6674, spot, 1.0, now, "[Shavronne] Summon Tome") is not null)
                    {
                        await SpawnAdd(runner, LightningBook, shav.Level, spot, shavId, books,
                            ownedCurrent: () => !stopped && !cleaned && Alive(shav));
                        return;
                    }
                }
            }

            // ShavronneTeleportTower[5066] reposition
            if (now >= nextTeleportAt)
            {
                nextTeleportAt = now + 10.5;
                var dest = RingSpot(runner, arenaCenter, AreaRng(runner).NextDouble() * Math.Tau, 24);
                if (await CastAction(runner, shavId, shav, 5066, dest, 0.9, now, "[Shavronne] Teleport") is not null)
                {
                    await SpawnTimedEffect(runner, effects, pos, LightningGlowArt, now + 1.0, scale: 1.0f);
                    shav.Walk.Teleport(dest.X, dest.Y, now);
                    await SendWorldRecord(runner, shavId, InstanceProtocol.Teleport(shavId, dest.X, dest.Y), null);
                    await SpawnTimedEffect(runner, effects, dest, LightningBoltsArt, now + 1.1, scale: 1.0f);
                    return;
                }
            }

            // ShavronneLightningPulse[6671] nova
            if (now >= nextPulseAt && Distance(pos, aim) <= 36)
            {
                nextPulseAt = now + 6.5;
                if (await CastAction(runner, shavId, shav, 6671, aim, 1.15, now, "[Shavronne] Lightning Pulse") is { } gen)
                {
                    await SpawnTimedEffect(runner, effects, pos, LightningBoltsArt, now + 1.2, scale: 1.3f);
                    hits.Add(new(now + 0.75, shavId, shav, pos, 22,
                        ElementalSpellAttack(shav, DamageType.Lightning, 1.15, 22), gen, ProjectileExplodeArt, 1.2f));
                    return;
                }
            }

            // ShavronneProjectileBarrage[6715] / ShavronneLevitatingProjectile[6673]
            if (now >= nextBarrageAt && Distance(pos, aim) <= 70)
            {
                nextBarrageAt = now + 5.0;
                int effect = phase2Active ? 6673 : 6715;
                if (await CastAction(runner, shavId, shav, effect, aim, 1.1, now, "[Shavronne] Barrage") is { } gen)
                {
                    double baseAngle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                    var atk = ElementalSpellAttack(shav, DamageType.Lightning, 0.9, 10);
                    for (int i = -1; i <= 1; i++)
                    {
                        double a = baseAngle + i * 0.24;
                        var drop = WalkableNear(runner, (
                            (int)Math.Round(pos.X + Math.Cos(a) * Math.Min(42, Distance(pos, aim) + 6)),
                            (int)Math.Round(pos.Y + Math.Sin(a) * Math.Min(42, Distance(pos, aim) + 6))));
                        hits.Add(new(now + 0.65 + Math.Abs(i) * 0.12, shavId, shav, drop, 9.5, atk, gen, ProjectileExplodeArt, 1.0f));
                    }
                    return;
                }
            }

            nextShavChaseAt = await StepChaseOrMelee(runner, shavId, shav, target, now, nextShavChaseAt, hits, meleeScale: 0.9, meleeEffect: 3804);
        }

        async Task StepBrutus(Player runner, SpawnedMonster brutus, bool empowered, Player target, double now)
        {
            if (!CanAct(brutus, now)) return;
            var pos = brutus.Walk.Position(now);
            var aim = target.Walk.Position(now);
            double dist = Distance(pos, aim);

            // BrutusLevitationSlam[6369] — empowered flying slam onto target
            if (empowered && now >= nextLevSlamAt && dist <= 52)
            {
                nextLevSlamAt = now + 11.0;
                var landing = WalkableNear(runner, aim);
                if (await CastAction(runner, brutusId, brutus, 6369, landing, 1.7, now, "[Brutus] Levitation Slam") is { } gen)
                {
                    await SpawnTimedEffect(runner, effects, landing, LightningGlowArt, now + 1.35, scale: 1.4f);
                    brutus.Walk.Teleport(landing.X, landing.Y, now);
                    await SendWorldRecord(runner, brutusId, InstanceProtocol.Teleport(brutusId, landing.X, landing.Y), null);
                    hits.Add(new(now + 1.25, brutusId, brutus, landing, 18,
                        HybridAttack(brutus, DamageType.Lightning, 1.0, 0.95, 18), gen, BrutusBigSlamArt, 1.35f));
                    return;
                }
            }

            // BrutusWallHookEmpowered[6368] (or BrutusWallHook[6367]) — telegraphed chain throw with 0.4s dodge window
            if (now >= nextHookAt && dist is >= 16 and <= 58)
            {
                nextHookAt = now + 9.5;
                int effect = empowered ? 6368 : 6367;
                if (await CastAction(runner, brutusId, brutus, effect, aim, 1.15, now, "[Brutus] Wall Hook") is { } gen)
                {
                    await SpawnTimedEffect(runner, effects, aim, empowered ? LightningGlowArt : BrutusSmallSlamArt, now + 0.55, scale: 0.95f);
                    pendingHooks.Add(new(now + 0.4, target.Id, pos, aim, empowered, gen));
                    return;
                }
            }

            // BrutusLightningMultiSlam[6711] (or BrutusMultiSlam[6709]) — 3-hit cascading slam
            if (now >= nextMultiSlamAt && dist <= 32)
            {
                nextMultiSlamAt = now + 6.5;
                int effect = empowered ? 6711 : 6709;
                if (await CastAction(runner, brutusId, brutus, effect, aim, 1.65, now, "[Brutus] Multi Slam") is { } gen)
                {
                    double angle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                    var atk = empowered
                        ? HybridAttack(brutus, DamageType.Lightning, 0.85, 0.85, 14)
                        : PhysicalAttack(brutus, 1.25, 14);
                    for (int s = 1; s <= 3; s++)
                    {
                        var slamSpot = WalkableNear(runner, (
                            (int)Math.Round(pos.X + Math.Cos(angle) * (s * 10)),
                            (int)Math.Round(pos.Y + Math.Sin(angle) * (s * 10))));
                        hits.Add(new(now + 0.45 + s * 0.35, brutusId, brutus, slamSpot, 13, atk, gen,
                            s == 3 ? BrutusBigSlamArt : BrutusSmallSlamArt, s == 3 ? 1.25f : 1.0f));
                    }
                    return;
                }
            }

            nextBrutusChaseAt = await StepChaseOrMelee(runner, brutusId, brutus, target, now, nextBrutusChaseAt, hits,
                meleeScale: empowered ? 1.2 : 1.05, meleeEffect: empowered ? 493 : 492);
        }

        async Task StepBooks(Player runner, Player target, double now)
        {
            if (now < nextBookPulseAt || books.Count == 0) return;
            nextBookPulseAt = now + 3.0;
            var aim = target.Walk.Position(now);
            foreach (uint bid in books.ToArray())
            {
                if (!runner.Area.Monsters.TryGetValue(bid, out var bm) || !Alive(bm))
                {
                    books.Remove(bid);
                    continue;
                }
                if (Distance(bm.Walk.Position(now), aim) > 55) continue;
                await SpawnTimedEffect(runner, effects, aim, LightningBoltsArt, now + 0.9, scale: 0.85f);
                hits.Add(new(now + 0.5, bid, bm, aim, 8.5,
                    ElementalSpellAttack(bm, DamageType.Lightning, 0.7, 8.5), null));
            }
        }

        async Task StepPendingHooks(Player runner, SpawnedMonster brutus, double now)
        {
            if (pendingHooks.Count == 0) return;
            for (int i = pendingHooks.Count - 1; i >= 0; i--)
            {
                var h = pendingHooks[i];
                if (now < h.At) continue;
                pendingHooks.RemoveAt(i);
                if (!Alive(brutus) || brutus.ActionGeneration != h.Generation || MonsterActionFactor(brutus, now) == 0)
                    continue;
                var target = ActivePlayers(runner).FirstOrDefault(p => p.Id == h.TargetId
                    && p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p));
                if (target is null) continue;
                // Only connect if the player did not dodge out of the chain hook line/impact zone.
                var curAt = target.Walk.Position(now);
                double hookAngle = Math.Atan2(h.AimSpot.Y - h.BossPos.Y, h.AimSpot.X - h.BossPos.X);
                double hookLen = Distance(h.BossPos, h.AimSpot) + 6;
                double dx = curAt.X - h.BossPos.X, dy = curAt.Y - h.BossPos.Y;
                double along = dx * Math.Cos(hookAngle) + dy * Math.Sin(hookAngle);
                double perp = Math.Abs(-dx * Math.Sin(hookAngle) + dy * Math.Cos(hookAngle));
                bool caught = Distance(curAt, h.AimSpot) <= 9.0 || (along >= 6 && along <= hookLen && perp <= 6.5);
                if (!caught) continue;

                var bPos = brutus.Walk.Position(now);
                var pullSpot = RingSpot(runner, bPos, Math.Atan2(curAt.Y - bPos.Y, curAt.X - bPos.X), 7);
                target.Walk.Teleport(pullSpot.X, pullSpot.Y, now);
                await SendWorldRecord(target, target.Id, InstanceProtocol.Teleport(target.Id, pullSpot.X, pullSpot.Y),
                    "[Brutus] chain hook pull");
                var atk = h.Empowered
                    ? HybridAttack(brutus, DamageType.Lightning, 0.7, 0.65, 12)
                    : PhysicalAttack(brutus, 1.05, 12);
                await SpawnTimedEffect(runner, effects, pullSpot, h.Empowered ? LightningBoltsArt : BrutusSmallSlamArt, now + 1.0, scale: 1.1f);
                await ArenaHazards.Hit(target, brutusId, brutus, atk, _ => true, () => !stopped && Alive(brutus));
            }
        }

        async Task Cleanup(Player runner)
        {
            cleaned = true;
            hits.Clear();
            pendingHooks.Clear();
            await RetireMonsters(runner, books);
            books.Clear();
            await ClearAllEffects(runner, effects);
            Console.WriteLine("[gateway] Shavronne & Brutus defeated; tomes and hazards cleared");
        }
    }
}
