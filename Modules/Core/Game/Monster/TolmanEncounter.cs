using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Act6To8Driver;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Act 8 — The Quay (<c>2_8_8</c>) Tolman resurrection encounter (<c>a8q6</c> "Love is Dead"):
/// <list type="bullet">
/// <item>Triggered when <c>a8q6-DeliveredItem</c> is set on Clarissa Ankh hand-in (or approaching Clarissa with
/// <c>a8q6-DeliveredItem</c> before <c>a8q6-KilledBoss</c>). Any pre-placed <c>ZombieBossTolman_[1226]</c> stays
/// hidden &amp; invulnerable until the undead waves finish.</item>
/// <item><b>3 escalating undead waves</b> (<c>NoRewards = true</c>): Wave 1 <i>Sleepy Dead</i>
/// (<c>ZombieMinionTolman[1231]</c> / <c>ZombieMinionTolmanFast[1233]</c>), Wave 2 <i>Noble Dead</i>
/// (<c>OriathanCitizenZombieMale1Tolman[5544]</c> / <c>ZombieMinionTolmanSoulMortar[1232]</c>), Wave 3
/// <i>Blackguard</i> (<c>AxisFootSoldierGuardTolman[1395]</c>, <c>AxisEliteSoldierGuardTolman[1373]</c>,
/// <c>AxisCasterGuardTolman[1342]</c>).</item>
/// <item><b>Tolman awakens</b> with <c>TolmanDelayedBlastDaemon</c> telegraphed blasts,
/// <c>TolmanDesecrateDaemon</c> caustic ground, and <c>TolmanDetonateDeadDaemon</c> (<i>Creeping Fire</i>
/// detonations). Grants <c>a8q6-KilledBoss</c> to all active players when Tolman dies.</item>
/// </list>
/// </summary>
public static class TolmanEncounter
{
    public const string Area = "2_8_8";
    public const string Boss = "Metadata/Monsters/Zombies/ZombieBossTolman_";                            // MV 1226

    const string SleepyDead = "Metadata/Monsters/Zombies/ZombieMinionTolman";                            // MV 1231
    const string SleepyDeadFast = "Metadata/Monsters/Zombies/ZombieMinionTolmanFast";                    // MV 1233
    const string SleepyDeadMortar = "Metadata/Monsters/Zombies/ZombieMinionTolmanSoulMortar";            // MV 1232
    const string NobleDead = "Metadata/Monsters/OriathanCitizen/OriathanCitizenZombieMale1Tolman";       // MV 5544
    const string BlackguardSoldier = "Metadata/Monsters/Axis/AxisFootSoldierGuardTolman";                // MV 1395
    const string BlackguardElite = "Metadata/Monsters/Axis/AxisEliteSoldierGuardTolman";                 // MV 1373
    const string BlackguardMage = "Metadata/Monsters/Axis/AxisCasterGuardTolman";                        // MV 1342

    const string DeliveredFlag = "a8q6-DeliveredItem";
    const string KilledFlag = "a8q6-KilledBoss";
    const string PhaseKey = "TolmanResurrection";

    const string DesecrateArt = "Metadata/Effects/Spells/desecrate/center.ao";
    const string CorpseBlastArt = "Metadata/Effects/Spells/detonate_dead_v2/blast_scalable.ao";
    const string ChaosBlastArt = "Metadata/Effects/Spells/detonate_dead_v2/chaos/blast_scalable.ao";
    const string FlameDashArt = "Metadata/Effects/Spells/flame_dash/flame_dash.ao";

    static readonly RuleTable<InstanceState, Fight> Fights = new();
    static readonly RuleTable<Player, ArenaHazards.Exposure> GroundExposures = new();

    public static async Task Tick(Player player)
    {
        if (player.Layout.Id != Area || !CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        var fight = Fights.GetValue(player.Instance, _ => new Fight(player));
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
        if (GroundExposures.TryGetValue(player, out var exp)) exp.Last = double.NaN;
        return Task.CompletedTask;
    }

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights) pair.Value.StopAndClear();
            foreach (var pair in GroundExposures) pair.Value.Last = double.NaN;
        }
    }

    sealed record HazardZone((int X, int Y) At, double Ends, double Radius, DamageType Element);

    sealed class Fight
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly GameArea area;
        readonly (int X, int Y)? clarissaSpot;
        readonly (int X, int Y)? bossSpawnerSpot;
        readonly List<(int X, int Y)> waveSpawners;
        readonly List<(int X, int Y)> desecrateSpawners;

        readonly List<TimedEffect> effects = [];
        readonly List<ScheduledHit> hits = [];
        readonly List<HazardZone> zones = [];
        readonly HashSet<uint> waveAdds = [];

        uint tolmanId;
        int stage; // 0 = waiting for Ankh hand-in, 1..3 = waves, 4 = Tolman active, 5 = complete
        bool tolmanSpawned, cleaned, stopped;
        double nextChaseAt, nextBlastAt, nextDesecrateAt, nextCreepingFireAt, waveSafetyAt;

        public Fight(Player owner)
        {
            area = owner.Area;
            var placed = AreaPlacements(owner);
            clarissaSpot = placed.Of("Metadata/NPC/Act8/ClarissaQuay").FirstOrDefault() is { } c ? (c.X, c.Y) : null;
            bossSpawnerSpot = placed.Of("Metadata/Monsters/Zombies/Tolman/TolmanBossSpawner").FirstOrDefault() is { } b ? (b.X, b.Y) : null;
            waveSpawners = [.. placed.Of("Metadata/Monsters/Zombies/Tolman/TolmanNormalSpawner")
                .Concat(placed.Of("Metadata/Monsters/Zombies/Tolman/TolmanMagicSpawner"))
                .Select(p => (p.X, p.Y))];
            desecrateSpawners = [.. placed.Of("Metadata/Monsters/Zombies/Tolman/TolmanDesecrateSpawner")
                .Concat(placed.Of("Metadata/Monsters/Zombies/Tolman/TolmanDetonateDeadSpawner"))
                .Select(p => (p.X, p.Y))];
            if (owner.Instance.HasState(PhaseFlag(PhaseKey, 4))) stage = 5;
            else if (owner.Instance.HasState(PhaseFlag(PhaseKey, 3))) stage = 4;
        }

        public void StopAndClear()
        {
            stopped = true;
            if (tolmanId != 0 && area.Monsters.TryGetValue(tolmanId, out var t))
            {
                ClearInvulnerableOnUnload(area, tolmanId, t, CannotBeDamagedBuff);
                t.WorldHidden = false;
            }
        }

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            await ResolveHits(runner, hits, effects, now, () => !stopped);
            zones.RemoveAll(z => now >= z.Ends);

            var tolmanEntry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Variety?.Id == Boss);
            if (tolmanEntry.Key != 0) tolmanId = tolmanEntry.Key;
            var tolman = tolmanEntry.Value;

            // Hold any pre-placed Tolman dormant/hidden until all 3 undead waves finish
            if (stage < 4 && tolman is not null && Alive(tolman))
            {
                if (tolman.Effects[Stat.BaseCannotBeDamaged] == 0)
                    SetInvulnerable(runner, tolmanId, tolman, true, CannotBeDamagedBuff);
                if (!tolman.WorldHidden)
                    await SetWorldHidden(runner, tolmanId, tolman, true);
                tolman.EntranceGrace = true;
            }

            if (stage == 5)
            {
                ResetExposures(runner);
                return;
            }

            var site = bossSpawnerSpot ?? clarissaSpot ?? (tolman is not null ? tolman.Walk.Position(now) : (0, 0));
            if (site == (0, 0)) return;

            var players = ActivePlayers(runner);
            byte areaLevel = (byte)Math.Clamp(Packs.For(Area)?.AreaLevel ?? 57, 1, 100);

            if (stage == 0)
            {
                // Start when any player near Clarissa/site has a8q6-DeliveredItem and not yet a8q6-KilledBoss
                bool ready = players.Any(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)
                    && ClientHasQuestFlag(p, DeliveredFlag) && !ClientHasQuestFlag(p, KilledFlag)
                    && Distance(p.Walk.Position(now), site) <= 95);
                if (!ready) return;

                stage = 1;
                MarkStarted(runner.Instance, PhaseKey);
                await SpawnWave(runner, 1, areaLevel, site, now);
                Console.WriteLine("[gateway] Tolman resurrection event started: Wave 1 (Sleepy Dead)");
                return;
            }

            if (stage is >= 1 and <= 3)
            {
                bool anyAlive = waveAdds.Any(id => runner.Area.Monsters.TryGetValue(id, out var m) && Alive(m));
                if (anyAlive && now < waveSafetyAt) return;

                // Clean up stale wave adds on safety expiry before advancing
                if (anyAlive)
                    await RetireMonsters(runner, waveAdds);
                waveAdds.Clear();

                if (stage < 3)
                {
                    stage++;
                    await SpawnWave(runner, stage, areaLevel, site, now);
                    Console.WriteLine($"[gateway] Tolman resurrection event: Wave {stage}/3 spawned");
                    return;
                }

                // Wave 3 cleared -> Awaken Tolman!
                stage = 4;
                MarkPhaseCompleted(runner.Instance, PhaseKey, 3);
                await AwakenTolman(runner, tolman, areaLevel, site, now);
                return;
            }

            // Stage 4: Tolman active combat
            if (tolman is null && !tolmanSpawned)
            {
                await AwakenTolman(runner, null, areaLevel, site, now);
                return;
            }

            if (tolman is null || !Alive(tolman))
            {
                await CompleteEncounter(runner);
                return;
            }

            // Ensure Tolman is unlocked and visible
            if (tolman.Effects[Stat.BaseCannotBeDamaged] != 0)
                SetInvulnerable(runner, tolmanId, tolman, false, CannotBeDamagedBuff);
            if (tolman.WorldHidden)
                await SetWorldHidden(runner, tolmanId, tolman, false);
            tolman.EntranceGrace = false;

            var pos = tolman.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 130, awakeId: tolmanId);
            if (target is null)
            {
                ResetExposures(runner);
                return;
            }
            tolman.Target = target;
            await StepHazardGround(runner, tolman, now);

            var aim = target.Walk.Position(now);

            // TolmanDelayedBlastDaemon — telegraphed soul blasts
            if (now >= nextBlastAt)
            {
                nextBlastAt = now + 5.5;
                var rng = AreaRng(runner);
                var atk = HybridAttack(tolman, DamageType.Fire, 0.45, 0.95, 11, spell: true);
                for (int i = 0; i < 3; i++)
                {
                    var drop = RingSpot(runner, aim, rng.NextDouble() * Math.Tau, i == 0 ? 0 : 7 + i * 3);
                    await SpawnTimedEffect(runner, effects, drop, DesecrateArt, now + 1.35 + i * 0.2, scale: 1.05f);
                    hits.Add(new(now + 1.3 + i * 0.2, tolmanId, tolman, drop, 10.5, atk, null, CorpseBlastArt, 1.15f));
                }
            }

            // TolmanDesecrateDaemon — caustic desecrate patches
            if (now >= nextDesecrateAt)
            {
                nextDesecrateAt = now + 7.5;
                var spot = desecrateSpawners.Count > 0 && AreaRng(runner).Next(2) == 0
                    ? desecrateSpawners[AreaRng(runner).Next(desecrateSpawners.Count)]
                    : WalkableNear(runner, aim);
                await SpawnTimedEffect(runner, effects, spot, DesecrateArt, now + 8.5, scale: 1.25f);
                zones.Add(new(spot, now + 8.5, 11.0, DamageType.Chaos));
            }

            // TolmanDetonateDeadDaemon — Creeping Fire crawling ground fire & corpse explosion
            if (now >= nextCreepingFireAt)
            {
                nextCreepingFireAt = now + 6.5;
                double angle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                var atk = ElementalSpellAttack(tolman, DamageType.Fire, 1.1, 10.5);
                for (int s = 1; s <= 3; s++)
                {
                    var spot = WalkableNear(runner, (
                        (int)Math.Round(pos.X + Math.Cos(angle) * (s * 10)),
                        (int)Math.Round(pos.Y + Math.Sin(angle) * (s * 10))));
                    await SpawnTimedEffect(runner, effects, spot, FlameDashArt, now + 5.5, scale: 1.0f);
                    zones.Add(new(spot, now + 5.5, 8.0, DamageType.Fire));
                    hits.Add(new(now + 0.7 + s * 0.35, tolmanId, tolman, spot, 10, atk, null, ChaosBlastArt, 1.05f));
                }
            }

            if (CanAct(tolman, now))
                nextChaseAt = await StepChaseOrMelee(runner, tolmanId, tolman, target, now, nextChaseAt, hits, meleeScale: 1.15);
        }

        async Task SpawnWave(Player runner, int wave, byte level, (int X, int Y) center, double now)
        {
            waveSafetyAt = now + 40.0;
            var rng = AreaRng(runner);
            string[] types = wave switch
            {
                1 => [SleepyDead, SleepyDeadFast],
                2 => [NobleDead, SleepyDeadMortar, SleepyDeadFast],
                _ => [BlackguardSoldier, BlackguardElite, BlackguardMage],
            };
            int count = wave == 1 ? 5 : 6;
            for (int i = 0; i < count; i++)
            {
                var spot = waveSpawners.Count > 0
                    ? waveSpawners[rng.Next(waveSpawners.Count)]
                    : RingSpot(runner, center, i * (Math.Tau / count), 18);
                await SpawnAdd(runner, types[i % types.Length], level, spot, 0, waveAdds,
                    rarity: wave == 3 && i == 1 ? MonsterRarity.Magic : MonsterRarity.Normal,
                    ownedCurrent: () => !stopped && !cleaned);
            }
        }

        async Task AwakenTolman(Player runner, SpawnedMonster? existing, byte level, (int X, int Y) site, double now)
        {
            tolmanSpawned = true;
            if (existing is not null && Alive(existing))
            {
                existing.EntranceGrace = false;
                existing.Aggro = true;
                SetInvulnerable(runner, tolmanId, existing, false, CannotBeDamagedBuff);
                await SetWorldHidden(runner, tolmanId, existing, false);
                await SpawnTimedEffect(runner, effects, existing.Walk.Position(now), ChaosBlastArt, now + 1.5, scale: 1.3f);
            }
            else if (Monsters.Find(Boss) is { Supported: true, Id: Boss } bossVar)
            {
                var spot = WalkableNear(runner, site);
                tolmanId = await SpawnMonster(runner, bossVar, level, MonsterRarity.Unique, spot.X, spot.Y,
                    instanceOwned: true, owned: (_, m) => { m.Aggro = true; m.EntranceGrace = false; },
                    ownedCurrent: () => !stopped && !cleaned);
                await SpawnTimedEffect(runner, effects, spot, ChaosBlastArt, now + 1.5, scale: 1.3f);
            }
            nextBlastAt = now + 2.5;
            nextDesecrateAt = now + 4.0;
            nextCreepingFireAt = now + 5.5;
            Console.WriteLine($"[gateway] Tolman 0x{tolmanId:x} awakened at The Quay!");
        }

        async Task StepHazardGround(Player runner, SpawnedMonster tolman, double now)
        {
            if (zones.Count == 0)
            {
                ResetExposures(runner);
                return;
            }
            var (min, max) = Monsters.BaseDamage(tolman.Variety!, tolman.Level) ?? (28, 38);
            double dps = (min + max) * 0.42;
            foreach (var p in ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)))
            {
                var exp = GroundExposures.GetValue(p, _ => new());
                await ArenaHazards.Ground(p, exp, now, (at, _) =>
                {
                    var pkt = DamagePacket.Empty;
                    bool hasChaos = false, hasFire = false;
                    foreach (var z in zones)
                    {
                        if (Distance(at, z.At) > z.Radius) continue;
                        if (z.Element == DamageType.Chaos) hasChaos = true;
                        else hasFire = true;
                    }
                    if (hasChaos) pkt = pkt.Add(DamageType.Chaos, new(dps, dps));
                    if (hasFire) pkt = pkt.Add(DamageType.Fire, new(dps, dps));
                    return pkt;
                }, () => !stopped && Alive(tolman));
            }
        }

        static void ResetExposures(Player runner)
        {
            foreach (var p in ActivePlayers(runner))
                if (GroundExposures.TryGetValue(p, out var exp)) exp.Last = double.NaN;
        }

        async Task CompleteEncounter(Player runner)
        {
            if (cleaned) return;
            cleaned = true;
            stage = 5;
            MarkPhaseCompleted(runner.Instance, PhaseKey, 4);
            hits.Clear();
            zones.Clear();
            ResetExposures(runner);
            await RetireMonsters(runner, waveAdds);
            waveAdds.Clear();
            await ClearAllEffects(runner, effects);

            // Grant a8q6-KilledBoss to every active player in the instance
            foreach (var p in ActivePlayers(runner))
            {
                if (!ClientHasQuestFlag(p, KilledFlag))
                {
                    await SetQuestFlags(p, [KilledFlag]);
                    await UpdateQuestObjectStates(p, [KilledFlag]);
                }
            }
            Console.WriteLine("[gateway] Tolman defeated; a8q6-KilledBoss granted to active players");
        }
    }
}
