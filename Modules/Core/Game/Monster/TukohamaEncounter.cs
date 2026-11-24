using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Act6To8Driver;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Act 6 — The Karui Fortress (<c>2_6_5</c>) Tukohama, Karui God of War encounter controller:
/// <list type="bullet">
/// <item>Perimeter Scorching Ray (<c>TukohamaPuiTotemBeam[5789]</c>) and fire-mortar totems
/// (<c>PuiTotemTukohamaMinion[4255]</c>, <c>NoRewards = true</c>) placed at cached <c>BeamTotemSpawner</c> markers.</item>
/// <item><b>50% HP Invulnerability Phase</b>: <c>CampaignBossHelpers.SetInvulnerable</c> + <c>tukohama_shield</c> (848),
/// summoning tethered <c>TukohamaShieldTotem[5793]</c> (<i>Tukohama's Protection</i>) at <c>ShieldingTotemSpawner</c>
/// markers that must be destroyed to drop Tukohama's shield and resume damage (persisted via <c>SyncSavedPhases</c>).</item>
/// <item><c>TukohamaSunderTotem[5792]</c> (<i>Tukohama's Servant</i>) &amp; <c>TukohamaSlamTotem[5795]</c>
/// (<i>Tukohama's Frenzy</i>) plus telegraphed <c>TukohamaSunder[6664]</c> and <c>TukohamaSlam[6665]</c>.</item>
/// </list>
/// </summary>
public static class TukohamaEncounter
{
    public const string Area = "2_6_5";
    public const string Boss = "Metadata/Monsters/Tukohama/Tukohama";                         // MV 5783
    public const string RageTotem = "Metadata/Monsters/Totems/PuiTotemTukohamaMinion";        // MV 4255
    public const string BeamTotem = "Metadata/Monsters/Totems/TukohamaPuiTotemBeam";          // MV 5789
    public const string SunderTotem = "Metadata/Monsters/Tukohama/TukohamaSunderTotem";       // MV 5792
    public const string ShieldTotem = "Metadata/Monsters/Tukohama/TukohamaShieldTotem";       // MV 5793
    public const string SlamTotem = "Metadata/Monsters/Tukohama/TukohamaSlamTotem";           // MV 5795

    const string PhaseKey = "Tukohama";
    static readonly int[] PhaseThresholds = [50];
    static readonly int[] BossEffects = [406, 5373, 5302, 6664, 6665];

    const ushort ShieldBuffRow = 848; // tukohama_shield

    const string ShieldArt = "Metadata/Effects/Spells/monsters_effects/tukohama_e/shieldingtotem/shield.ao";
    const string ShieldBeamArt = "Metadata/Effects/Spells/monsters_effects/tukohama_e/shieldingtotem/shieldingbeam.ao";
    const string SunderSurgeArt = "Metadata/Effects/Spells/monsters_effects/tukohama_e/sunder_surge.ao";
    const string FireQuakeArt = "Metadata/Effects/Spells/monsters_effects/tukohama_e/totemsunder/firequake_01_slam.ao";
    const string SlamMarkerArt = "Metadata/Effects/Spells/monsters_effects/tukohama_e/totemslam/marker.ao";
    const string TotemSlamArt = "Metadata/Effects/Spells/monsters_effects/tukohama_e/totemslam/tukohama_totemslam.ao";
    const string CircleSlamArt = "Metadata/Effects/Spells/monsters_effects/tukohama_e/circleslam_surge.ao";

    static readonly RuleTable<InstanceState, Fight> Fights = new();

    public static async Task Tick(Player player)
    {
        if (player.Layout.Id != Area || !CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        if (!Fights.TryGetValue(player.Instance, out var fight))
        {
            if (!player.Area.Monsters.Values.Any(m => m.Hostile && m.Variety?.Id == Boss))
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

    sealed class Fight
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly GameArea area;
        readonly List<(int X, int Y)> beamSpots;
        readonly List<(int X, int Y)> shieldSpots;
        readonly (int X, int Y)? cachedMiddle;

        readonly List<TimedEffect> effects = [];
        readonly List<ScheduledHit> hits = [];
        readonly HashSet<uint> perimeterTotems = [];
        readonly HashSet<uint> combatTotems = [];
        readonly HashSet<uint> shieldTotems = [];

        uint bossId;
        (int X, int Y) center;
        bool initialized, cleaned, stopped, shieldPhaseActive;
        double nextChaseAt, nextSunderAt, nextSlamAt, nextTotemAt, nextPerimeterPulseAt, nextCombatTotemPulseAt;
        int perimeterSweepStep;

        public Fight(Player owner)
        {
            area = owner.Area;
            var placed = AreaPlacements(owner);
            cachedMiddle = placed.Of("Metadata/MiscellaneousObjects/ArenaMiddle").FirstOrDefault() is { } m ? (m.X, m.Y) : null;
            beamSpots = [.. placed.Of("Metadata/Monsters/Tukohama/BeamTotemSpawner").Select(p => (p.X, p.Y))];
            shieldSpots = [.. placed.Of("Metadata/Monsters/Tukohama/ShieldingTotemSpawner").Select(p => (p.X, p.Y))];
        }

        public void StopAndClear()
        {
            stopped = true;
            if (bossId != 0 && area.Monsters.TryGetValue(bossId, out var b))
                ClearInvulnerableOnUnload(area, bossId, b, ShieldBuffRow);
        }

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            await ResolveHits(runner, hits, effects, now, () => !stopped);

            var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == Boss);
            if (entry.Value is not { } boss)
                return;
            bossId = entry.Key;

            if (!Alive(boss))
            {
                if (!cleaned) await Cleanup(runner, boss);
                return;
            }

            SyncSavedPhases(runner.Instance, boss, PhaseKey, PhaseThresholds);
            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 125, awakeId: bossId);
            if (target is null) return;

            if (!initialized)
            {
                initialized = true;
                center = cachedMiddle ?? pos;
                boss.Aggro = true;
                boss.Target = target;
                MarkStarted(runner.Instance, PhaseKey);
                await BindEffects(runner, bossId, boss, BossEffects);
                await EnsureBossBar(runner, bossId, boss);
                nextSunderAt = now + 4.5;
                nextSlamAt = now + 8.0;
                nextTotemAt = now + 6.5;
                nextPerimeterPulseAt = now + 3.0;
                nextCombatTotemPulseAt = now + 4.0;
                await SpawnPerimeterTotems(runner, boss);
                Console.WriteLine($"[gateway] Tukohama controller active for 0x{bossId:x} (phases done {boss.LifePhases!.Completed})");
            }
            else
            {
                boss.Target = target;
                await EnsureBossBar(runner, bossId, boss);
            }

            await StepPerimeterTotems(runner, boss, target, now);
            await StepCombatTotems(runner, boss, target, now);

            var phases = boss.LifePhases!;
            if (shieldPhaseActive)
            {
                bool anyShieldAlive = shieldTotems.Any(id => runner.Area.Monsters.TryGetValue(id, out var st) && Alive(st));
                if (!anyShieldAlive)
                {
                    shieldPhaseActive = false;
                    shieldTotems.Clear();
                    SetInvulnerable(runner, bossId, boss, false, ShieldBuffRow);
                    MarkPhaseCompleted(runner.Instance, PhaseKey, phases.Completed);
                    phases.Finish();
                    nextSunderAt = now + 2.5;
                    nextSlamAt = now + 5.5;
                    Console.WriteLine($"[gateway] Tukohama 0x{bossId:x}: Tukohama's Protection destroyed; shield dropped");
                }
                return;
            }

            // Guarantee vulnerability outside shield phase.
            if (boss.Effects[Stat.BaseCannotBeDamaged] != 0)
                SetInvulnerable(runner, bossId, boss, false, ShieldBuffRow);

            // 50% HP shield phase: summon tethered TukohamaShieldTotem(s)
            if (!phases.Active && phases.Completed < 1 && phases.Begin(boss.Life, boss.MaxLife))
            {
                shieldPhaseActive = true;
                SetInvulnerable(runner, bossId, boss, true, ShieldBuffRow);
                boss.Walk.Teleport(center.X, center.Y, now);
                await SendWorldRecord(runner, bossId, InstanceProtocol.Teleport(bossId, center.X, center.Y), null);
                await SpawnTimedEffect(runner, effects, center, ShieldArt, now + 3.0, scale: 1.3f);
                await SpawnShieldTotems(runner, boss, now);
                Console.WriteLine($"[gateway] Tukohama 0x{bossId:x}: 50% HP invulnerability phase -> {shieldTotems.Count} Protection totem(s)");
                return;
            }

            if (!CanAct(boss, now)) return;

            var aim = target.Walk.Position(now);
            double dist = Distance(pos, aim);

            // Summon TukohamaSunderTotem / TukohamaSlamTotem periodically
            if (now >= nextTotemAt)
            {
                nextTotemAt = now + (phases.Completed > 0 ? 11.0 : 14.0);
                int living = combatTotems.Count(id => runner.Area.Monsters.TryGetValue(id, out var t) && Alive(t));
                if (living < 3)
                {
                    var rng = AreaRng(runner);
                    var spot = RingSpot(runner, aim, rng.NextDouble() * Math.Tau, 14);
                    string variety = (living & 1) == 0 ? SunderTotem : SlamTotem;
                    uint tid = await SpawnAdd(runner, variety, boss.Level, spot, bossId, combatTotems,
                        ownedCurrent: () => !stopped && !cleaned && Alive(boss));
                    if (tid != 0)
                        await SpawnTimedEffect(runner, effects, spot, SlamMarkerArt, now + 1.4, scale: 1.0f);
                }
            }

            // Telegraphed TukohamaSlam [6665]
            if (now >= nextSlamAt && dist <= 34)
            {
                nextSlamAt = now + (phases.Completed > 0 ? 7.5 : 9.5);
                if (await CastAction(runner, bossId, boss, 6665, aim, 1.5, now, "[Tukohama] Slam") is { } gen)
                {
                    var impact = WalkableNear(runner, aim);
                    await SpawnTimedEffect(runner, effects, impact, SlamMarkerArt, now + 1.15, scale: 1.35f);
                    hits.Add(new(now + 1.1, bossId, boss, impact, 17,
                        HybridAttack(boss, DamageType.Fire, 1.0, 0.8, 17), gen, CircleSlamArt, 1.35f));
                    return;
                }
            }

            // Telegraphed TukohamaSunder [6664] — shockwave line of fire quakes
            if (now >= nextSunderAt && dist <= 55)
            {
                nextSunderAt = now + (phases.Completed > 0 ? 5.5 : 7.0);
                if (await CastAction(runner, bossId, boss, 6664, aim, 1.25, now, "[Tukohama] Sunder") is { } gen)
                {
                    double angle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                    var atk = HybridAttack(boss, DamageType.Fire, 0.8, 0.75, 11);
                    for (int s = 1; s <= 4; s++)
                    {
                        var spot = WalkableNear(runner, (
                            (int)Math.Round(pos.X + Math.Cos(angle) * (s * 12)),
                            (int)Math.Round(pos.Y + Math.Sin(angle) * (s * 12))));
                        await SpawnTimedEffect(runner, effects, spot, SunderSurgeArt, now + 0.55 + s * 0.2, scale: 1.1f);
                        hits.Add(new(now + 0.5 + s * 0.2, bossId, boss, spot, 10.5, atk, gen, FireQuakeArt, 1.1f));
                    }
                    return;
                }
            }

            nextChaseAt = await StepChaseOrMelee(runner, bossId, boss, target, now, nextChaseAt, hits, meleeScale: 1.1);
        }

        async Task SpawnPerimeterTotems(Player runner, SpawnedMonster boss)
        {
            List<(int X, int Y)> spots = beamSpots.Count > 0
                ? beamSpots
                : [.. Enumerable.Range(0, 4).Select(i => RingSpot(runner, center, i * (Math.PI / 2.0) + Math.PI / 4.0, 32))];
            for (int i = 0; i < spots.Count && i < 6; i++)
            {
                string variety = (i & 1) == 0 ? RageTotem : BeamTotem;
                uint tid = await SpawnAdd(runner, variety, boss.Level, spots[i], bossId, perimeterTotems,
                    ownedCurrent: () => !stopped && !cleaned && Alive(boss));
                if (tid != 0 && runner.Area.Monsters.TryGetValue(tid, out var tm))
                    SetInvulnerable(runner, tid, tm, true, CannotBeDamagedBuff);
            }
        }

        async Task SpawnShieldTotems(Player runner, SpawnedMonster boss, double now)
        {
            List<(int X, int Y)> spots = shieldSpots.Count > 0
                ? shieldSpots
                : [RingSpot(runner, center, 0, 22), RingSpot(runner, center, Math.PI, 22)];
            for (int i = 0; i < spots.Count && i < 3; i++)
            {
                uint tid = await SpawnAdd(runner, ShieldTotem, boss.Level, spots[i], bossId, shieldTotems,
                    rarity: MonsterRarity.Unique, ownedCurrent: () => !stopped && !cleaned && Alive(boss));
                if (tid != 0)
                    await SpawnTimedEffect(runner, effects, spots[i], ShieldBeamArt, now + 4.5, scale: 1.15f);
            }
            if (shieldTotems.Count == 0)
            {
                // Safety fallback: never strand Tukohama invulnerable if spawn failed.
                shieldPhaseActive = false;
                SetInvulnerable(runner, bossId, boss, false, ShieldBuffRow);
            }
        }

        async Task StepPerimeterTotems(Player runner, SpawnedMonster boss, Player target, double now)
        {
            if (now < nextPerimeterPulseAt || perimeterTotems.Count == 0) return;
            nextPerimeterPulseAt = now + (shieldPhaseActive ? 2.6 : 3.6);
            var aim = target.Walk.Position(now);
            int idx = perimeterSweepStep++;
            foreach (uint tid in perimeterTotems.ToArray())
            {
                if (!runner.Area.Monsters.TryGetValue(tid, out var tm) || !Alive(tm)) continue;
                var tpos = tm.Walk.Position(now);
                if (tm.Variety?.Id == BeamTotem)
                {
                    // Sweeping Scorching Ray fire beam across the arena
                    double angle = Math.Atan2(center.Y - tpos.Y, center.X - tpos.X) + Math.Sin(idx * 0.7) * 0.45;
                    await SpawnTimedEffect(runner, effects, tpos, SunderSurgeArt, now + 0.9, scale: 0.95f);
                    await HitBeamLine(runner, tid, tm, tpos, angle, 55, 5.0,
                        ElementalSpellAttack(tm, DamageType.Fire, 0.65, 55), () => !stopped && Alive(boss));
                }
                else
                {
                    // Fire mortar volley targeting player vicinity
                    var rng = AreaRng(runner);
                    var drop = RingSpot(runner, aim, rng.NextDouble() * Math.Tau, 4 + rng.NextDouble() * 10);
                    await SpawnTimedEffect(runner, effects, drop, SlamMarkerArt, now + 1.25, scale: 0.95f);
                    hits.Add(new(now + 1.2, tid, tm, drop, 9,
                        ElementalSpellAttack(tm, DamageType.Fire, 0.75, 9), null, FireQuakeArt, 0.95f));
                }
            }
        }

        async Task StepCombatTotems(Player runner, SpawnedMonster boss, Player target, double now)
        {
            if (now < nextCombatTotemPulseAt || combatTotems.Count == 0) return;
            nextCombatTotemPulseAt = now + 3.2;
            var aim = target.Walk.Position(now);
            foreach (uint tid in combatTotems.ToArray())
            {
                if (!runner.Area.Monsters.TryGetValue(tid, out var tm) || !Alive(tm))
                {
                    combatTotems.Remove(tid);
                    continue;
                }
                var tpos = tm.Walk.Position(now);
                if (Distance(tpos, aim) > 40) continue;
                if (tm.Variety?.Id == SunderTotem)
                {
                    await SpawnTimedEffect(runner, effects, tpos, SunderSurgeArt, now + 0.95, scale: 1.0f);
                    hits.Add(new(now + 0.85, tid, tm, aim, 11,
                        HybridAttack(tm, DamageType.Fire, 0.6, 0.6, 11), null, FireQuakeArt, 1.0f));
                }
                else
                {
                    await SpawnTimedEffect(runner, effects, tpos, TotemSlamArt, now + 1.0, scale: 1.05f);
                    hits.Add(new(now + 0.85, tid, tm, tpos, 14,
                        PhysicalAttack(tm, 1.05, 14), null, CircleSlamArt, 1.0f));
                }
            }
        }

        async Task Cleanup(Player runner, SpawnedMonster boss)
        {
            cleaned = true;
            shieldPhaseActive = false;
            SetInvulnerable(runner, bossId, boss, false, ShieldBuffRow);
            hits.Clear();
            await RetireMonsters(runner, perimeterTotems);
            perimeterTotems.Clear();
            await RetireMonsters(runner, combatTotems);
            combatTotems.Clear();
            await RetireMonsters(runner, shieldTotems);
            shieldTotems.Clear();
            await ClearAllEffects(runner, effects);
            Console.WriteLine("[gateway] Tukohama defeated; totems and hazards cleared");
        }
    }
}
