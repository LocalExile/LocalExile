using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Act6To8Driver;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Act 6 &amp; Act 8 side-boss polish encounters (augmenting ordinary <c>BossKits</c> AI cleanly):
/// <list type="bullet">
/// <item><b>Hector Titucius (<c>2_8_5</c> Bath House)</b>: Projectile-blocking shield while charging
/// (<c>SetEncounterProximityShield</c> + <c>frontal_proximity_shield</c> buff 1885 during
/// <c>TriggerCharge1..3[7845..7847]</c>) releasing <c>TriggeredFireOrbs[7848]</c> fire-orb bursts.</item>
/// <item><b>Toxic Conduits (<c>2_8_2_1</c>)</b>: Spawns <b>Towering Aberration</b> (<c>ZombieGiantBoss[1175]</c>)
/// when all 5 <c>ZombieGiantSummoningPackMember[1176]</c> (<i>Lingering Aberration</i>) die.</item>
/// <item><b>Defiled Proclamation (<c>2_6_9</c> Western Forest)</b>: Cascading <c>RockGolemSlamTriggered1..6[138..143]</c>
/// multi-slams.</item>
/// <item><b>Brinecrack (<c>2_6_13</c> Cavern of Anger)</b>: <c>NessaCrabWhirlingBlades[354]</c> dash-slash +
/// <c>NessaCrabTriggeredEarthquake[5494]</c> delayed aftershock.</item>
/// <item><b>Nightbringer Lucius (<c>2_8_6</c> Lunaris Concourse)</b>: Sweeping ion-cannon beams +
/// <c>AxisShieldCharge[234]</c> &amp; <c>AxisBossIceCrash[4814]</c>.</item>
/// <item><b>Regulus, Sun's Herald (<c>2_8_11</c> Solaris Concourse)</b>: <c>AxisBossSummonMagmaDaemon[4218]</c>
/// magma orbs + burning ground + <c>AxisLeapSlam[814]</c>.</item>
/// </list>
/// </summary>
public static class Act6To8SideBossPolish
{
    public const string WesternForest = "2_6_9";
    public const string CavernOfAnger = "2_6_13";
    public const string ToxicConduits = "2_8_2_1";
    public const string BathHouse = "2_8_5";
    public const string LunarisConcourse = "2_8_6";
    public const string SolarisConcourse = "2_8_11";

    public static readonly string[] Areas =
        [WesternForest, CavernOfAnger, ToxicConduits, BathHouse, LunarisConcourse, SolarisConcourse];

    public const string DefiledProclamation = "Metadata/Monsters/RockGolem/RockGolemProclamationBoss";      // MV 4754
    public const string Brinecrack = "Metadata/Monsters/NessaCrab/NessaCrab";                               // MV 6004
    public const string LingeringAberration = "Metadata/Monsters/Zombies/ZombieGiantSummoningPackMember";   // MV 1176
    public const string ToweringAberration = "Metadata/Monsters/Zombies/ZombieGiantBoss";                   // MV 1175
    public const string HectorTitucius = "Metadata/Monsters/Exiles/Titucius";                               // MV 6092
    public const string NightbringerLucius = "Metadata/Monsters/Axis/AxisEliteLunarisBoss_";                // MV 1410
    public const string Regulus = "Metadata/Monsters/Axis/AxisEliteSolarisBoss";                            // MV 1405

    const ushort FrontalShieldBuffRow = 1885; // frontal_proximity_shield

    const string TituciusChargeAoeArt = "Metadata/Effects/Spells/monsters_effects/act8/titucius/shield_charge/chargeaoe.ao";
    const string TituciusImpactArt = "Metadata/Effects/Spells/monsters_effects/act8/titucius/shield_charge/impact.ao";
    const string TituciusSlamArt = "Metadata/Effects/Spells/monsters_effects/act8/titucius/slam/slam.ao";
    const string FireOrbArt = "Metadata/Effects/Spells/monsters_effects/act6/abberath/combofireballbig.ao";
    const string CrabLeapArt = "Metadata/Effects/Spells/monsters_effects/act6/nessacrab/leap.ao";
    const string IonCannonArt = "Metadata/Effects/Spells/monsters_effects/blackguard_cleric/ioncannon/ioncannonbc.ao";
    const string MagmaImpactArt = "Metadata/Effects/Spells/monsters_effects/act4/kaom/volcano/volcanicimpact.ao";
    const string MagmaPoolArt = "Metadata/Effects/Spells/monsters_effects/act4/kaom/volcano/loop_lavapool.ao";

    static readonly RuleTable<InstanceState, AreaPolish> States = new();
    static readonly RuleTable<Player, ArenaHazards.Exposure> MagmaExposures = new();

    public static async Task Tick(Player player)
    {
        if (!CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        string area = player.Layout.Id;
        if (area is not (WesternForest or CavernOfAnger or ToxicConduits or BathHouse or LunarisConcourse or SolarisConcourse))
            return;
        var state = States.GetValue(player.Instance, _ => new AreaPolish(player.Area, area));
        await state.Gate.WaitAsync();
        try
        {
            if (IsMonsterRunner(player))
                await state.Step(player, AreaTime(player));
        }
        finally { state.Gate.Release(); }
    }

    public static async Task OnMonsterKilled(MonsterKill kill)
    {
        var player = kill.Player;
        if (player.Layout.Id != ToxicConduits || kill.Monster.VarietyId != LingeringAberration) return;
        var state = States.GetValue(player.Instance, _ => new AreaPolish(player.Area, ToxicConduits));
        await state.Gate.WaitAsync();
        try
        {
            await state.CheckToxicConduitsGiant(player, (kill.Monster.X, kill.Monster.Y), (byte)Math.Clamp(kill.Monster.Level, 1, 100));
        }
        finally { state.Gate.Release(); }
    }

    public static Task Leaving(Player player)
    {
        if (MagmaExposures.TryGetValue(player, out var exp)) exp.Last = double.NaN;
        return Task.CompletedTask;
    }

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var p in States) p.Value.StopAndClear();
            foreach (var p in MagmaExposures) p.Value.Last = double.NaN;
        }
    }

    sealed record MagmaPatch((int X, int Y) At, double Ends, double Radius);

    sealed class AreaPolish(GameArea gameArea, string area)
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly List<TimedEffect> effects = [];
        readonly List<ScheduledHit> hits = [];
        readonly List<MagmaPatch> magma = [];

        uint tituciusId;
        bool tituciusShielded, giantSpawned, cleaned, stopped;
        double tituciusShieldEndsAt, nextSpecialAt, nextSecondaryAt;
        int chargeTier;

        public void StopAndClear()
        {
            stopped = true;
            if (tituciusId != 0 && gameArea.Monsters.TryGetValue(tituciusId, out var t))
            {
                SetEncounterProximityShield(gameArea, t, 0);
                gameArea.Buffs.Clear(tituciusId, FrontalShieldBuffRow, tituciusId);
            }
        }

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            await ResolveHits(runner, hits, effects, now, () => !stopped);
            magma.RemoveAll(m => now >= m.Ends);

            switch (area)
            {
                case BathHouse: await StepTitucius(runner, now); break;
                case ToxicConduits: await StepToxicConduits(runner, now); break;
                case WesternForest: await StepProclamation(runner, now); break;
                case CavernOfAnger: await StepBrinecrack(runner, now); break;
                case LunarisConcourse: await StepLucius(runner, now); break;
                case SolarisConcourse: await StepRegulus(runner, now); break;
            }
        }

        // ---- Hector Titucius (2_8_5) ----

        async Task StepTitucius(Player runner, double now)
        {
            var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == HectorTitucius);
            if (entry.Value is not { } boss) return;
            tituciusId = entry.Key;

            if (!Alive(boss))
            {
                if (!cleaned)
                {
                    cleaned = true;
                    SetEncounterProximityShield(runner.Area, boss, 0);
                    runner.Area.Buffs.Clear(tituciusId, FrontalShieldBuffRow, tituciusId);
                    hits.Clear();
                    await ClearAllEffects(runner, effects);
                }
                return;
            }

            if (tituciusShielded && now >= tituciusShieldEndsAt)
            {
                tituciusShielded = false;
                SetEncounterProximityShield(runner.Area, boss, 0);
                runner.Area.Buffs.Clear(tituciusId, FrontalShieldBuffRow, tituciusId);
            }

            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 105, awakeId: tituciusId);
            if (target is null) return;
            if (nextSpecialAt == 0) { nextSpecialAt = now + 4.5; nextSecondaryAt = now + 8.0; }

            var aim = target.Walk.Position(now);

            // TriggerCharge1..3[7845..7847] with projectile-blocking shield + TriggeredFireOrbs[7848] release
            if (now >= nextSpecialAt && CanAct(boss, now))
            {
                nextSpecialAt = now + 7.5;
                int tier = (chargeTier++ % 3) + 1;
                tituciusShielded = true;
                tituciusShieldEndsAt = now + 2.2;
                SetEncounterProximityShield(runner.Area, boss, 16);
                runner.Area.Buffs.Set(tituciusId, new(FrontalShieldBuffRow, tituciusId, U68: Buff.Seconds(2.2)));

                var dest = WalkableNear(runner, aim);
                boss.BusyUntil = now + 1.4;
                await SpawnTimedEffect(runner, effects, pos, TituciusChargeAoeArt, now + 1.5, scale: 1.15f);
                boss.Walk.Teleport(dest.X, dest.Y, now);
                await SendWorldRecord(runner, tituciusId, InstanceProtocol.Teleport(tituciusId, dest.X, dest.Y), "[Titucius] Shield Charge");
                hits.Add(new(now + 0.65, tituciusId, boss, dest, 13, PhysicalAttack(boss, 1.0 + tier * 0.15, 13), null, TituciusImpactArt, 1.15f));

                // TriggeredFireOrbs[7848] radial burst at end of charge
                int orbCount = 2 + tier;
                var orbAtk = ElementalSpellAttack(boss, DamageType.Fire, 0.9, 9.5);
                for (int i = 0; i < orbCount; i++)
                {
                    var drop = RingSpot(runner, dest, i * (Math.Tau / orbCount), 12);
                    hits.Add(new(now + 1.0 + i * 0.12, tituciusId, boss, drop, 9.5, orbAtk, null, FireOrbArt, 0.95f));
                }
            }
            else if (now >= nextSecondaryAt && Distance(pos, aim) <= 28)
            {
                nextSecondaryAt = now + 9.0;
                hits.Add(new(now + 0.7, tituciusId, boss, aim, 14, PhysicalAttack(boss, 1.2, 14), null, TituciusSlamArt, 1.2f));
            }
        }

        // ---- Toxic Conduits Giant Zombie (2_8_2_1) ----

        public async Task CheckToxicConduitsGiant(Player runner, (int X, int Y) lastDeathPos, byte level)
        {
            if (giantSpawned || runner.Instance.HasState(Quests.InstanceFlag("ToxicConduits-GiantSpawned")))
                return;
            bool anyRemaining = runner.Area.Monsters.Values.Any(m => m.Variety?.Id == LingeringAberration && Alive(m));
            if (anyRemaining) return;
            if (runner.Area.Monsters.Values.Any(m => m.Variety?.Id == ToweringAberration))
            {
                giantSpawned = true;
                return;
            }
            giantSpawned = true;
            runner.Instance.SetState(Quests.InstanceFlag("ToxicConduits-GiantSpawned"));
            if (Monsters.Find(ToweringAberration) is { Supported: true, Id: ToweringAberration } giantVar)
            {
                var spot = WalkableNear(runner, lastDeathPos);
                uint gid = await SpawnMonster(runner, giantVar, level, MonsterRarity.Unique, spot.X, spot.Y,
                    instanceOwned: true, owned: (_, m) => { m.Aggro = true; m.NoRewards = false; },
                    ownedCurrent: () => !stopped);
                await SpawnTimedEffect(runner, effects, spot, TituciusSlamArt, AreaTime(runner) + 1.4, scale: 1.35f);
                Console.WriteLine($"[gateway] Toxic Conduits: all 5 Lingering Aberrations defeated -> Towering Aberration 0x{gid:x} spawned");
            }
        }

        async Task StepToxicConduits(Player runner, double now)
        {
            if (!giantSpawned)
            {
                var aberrations = runner.Area.Monsters.Values.Where(m => m.Variety?.Id == LingeringAberration).ToArray();
                if (aberrations.Length > 0 && aberrations.All(m => !Alive(m)))
                {
                    var last = aberrations[0];
                    await CheckToxicConduitsGiant(runner, last.Walk.Position(now), last.Level);
                }
                return;
            }

            // QuakeSlamSewerZombieBoss[5485] slam cadence for Towering Aberration
            var gEntry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == ToweringAberration);
            if (gEntry.Value is not { } giant || !Alive(giant)) return;
            var pos = giant.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 95, awakeId: gEntry.Key);
            if (target is null) return;
            if (nextSpecialAt == 0) nextSpecialAt = now + 4.5;
            if (now >= nextSpecialAt && Distance(pos, target.Walk.Position(now)) <= 30)
            {
                nextSpecialAt = now + 7.5;
                var aim = target.Walk.Position(now);
                hits.Add(new(now + 0.7, gEntry.Key, giant, aim, 15, PhysicalAttack(giant, 1.35, 15), null, TituciusSlamArt, 1.3f));
                hits.Add(new(now + 1.7, gEntry.Key, giant, aim, 19, PhysicalAttack(giant, 1.25, 19), null, TituciusImpactArt, 1.35f));
            }
        }

        // ---- Defiled Proclamation (2_6_9) ----

        async Task StepProclamation(Player runner, double now)
        {
            var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == DefiledProclamation);
            if (entry.Value is not { } boss || !Alive(boss)) return;
            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 100, awakeId: entry.Key);
            if (target is null) return;
            if (nextSpecialAt == 0) nextSpecialAt = now + 4.0;

            // Triggered multi-slams (RockGolemSlamTriggered1..6 [138..143])
            if (now >= nextSpecialAt && Distance(pos, target.Walk.Position(now)) <= 45)
            {
                nextSpecialAt = now + 7.0;
                var aim = target.Walk.Position(now);
                double angle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                var atk = PhysicalAttack(boss, 1.15, 12);
                for (int s = 1; s <= 4; s++)
                {
                    var spot = WalkableNear(runner, (
                        (int)Math.Round(pos.X + Math.Cos(angle) * (s * 10)),
                        (int)Math.Round(pos.Y + Math.Sin(angle) * (s * 10))));
                    hits.Add(new(now + 0.45 + s * 0.25, entry.Key, boss, spot, 11.5, atk, null, TituciusSlamArt, 1.15f));
                }
            }
        }

        // ---- Brinecrack (2_6_13) ----

        async Task StepBrinecrack(Player runner, double now)
        {
            var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == Brinecrack);
            if (entry.Value is not { } boss || !Alive(boss)) return;
            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 100, awakeId: entry.Key);
            if (target is null) return;
            if (nextSpecialAt == 0) { nextSpecialAt = now + 4.5; nextSecondaryAt = now + 7.5; }
            var aim = target.Walk.Position(now);

            // NessaCrabWhirlingBlades[354]
            if (now >= nextSpecialAt && Distance(pos, aim) is >= 10 and <= 48)
            {
                nextSpecialAt = now + 7.0;
                var dest = WalkableNear(runner, aim);
                boss.Walk.Teleport(dest.X, dest.Y, now);
                await SendWorldRecord(runner, entry.Key, InstanceProtocol.Teleport(entry.Key, dest.X, dest.Y), "[Brinecrack] Whirling Blades");
                await SpawnTimedEffect(runner, effects, dest, CrabLeapArt, now + 1.0, scale: 1.1f);
                hits.Add(new(now + 0.45, entry.Key, boss, dest, 12,
                    HybridAttack(boss, DamageType.Cold, 0.85, 0.55, 12), null));
            }
            // NessaCrabTriggeredEarthquake[5494]
            else if (now >= nextSecondaryAt && Distance(pos, aim) <= 32)
            {
                nextSecondaryAt = now + 8.5;
                hits.Add(new(now + 0.55, entry.Key, boss, aim, 13, PhysicalAttack(boss, 1.05, 13), null, TituciusSlamArt, 1.1f));
                hits.Add(new(now + 1.65, entry.Key, boss, aim, 18,
                    HybridAttack(boss, DamageType.Cold, 0.85, 0.75, 18), null, TituciusImpactArt, 1.25f));
            }
        }

        // ---- Nightbringer Lucius (2_8_6) ----

        async Task StepLucius(Player runner, double now)
        {
            var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == NightbringerLucius);
            if (entry.Value is not { } boss || !Alive(boss)) return;
            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 110, awakeId: entry.Key);
            if (target is null) return;
            if (nextSpecialAt == 0) { nextSpecialAt = now + 4.5; nextSecondaryAt = now + 8.0; }
            var aim = target.Walk.Position(now);

            // Sweeping Ion Cannon beam
            if (now >= nextSpecialAt && Distance(pos, aim) <= 65)
            {
                nextSpecialAt = now + 8.0;
                double angle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                await SpawnTimedEffect(runner, effects, aim, IonCannonArt, now + 1.5, scale: 1.2f);
                await HitBeamLine(runner, entry.Key, boss, pos, angle, 56, 6.0,
                    HybridAttack(boss, DamageType.Cold, 0.35, 0.9, 56, spell: true), () => !stopped && Alive(boss));
            }
            // AxisShieldCharge[234] + Ice Crash burst
            else if (now >= nextSecondaryAt && Distance(pos, aim) is >= 12 and <= 52)
            {
                nextSecondaryAt = now + 9.0;
                var dest = WalkableNear(runner, aim);
                boss.Walk.Teleport(dest.X, dest.Y, now);
                await SendWorldRecord(runner, entry.Key, InstanceProtocol.Teleport(entry.Key, dest.X, dest.Y), "[Lucius] Shield Charge");
                hits.Add(new(now + 0.55, entry.Key, boss, dest, 14,
                    HybridAttack(boss, DamageType.Cold, 0.75, 0.8, 14), null, IonCannonArt, 1.15f));
            }
        }

        // ---- Regulus, Sun's Herald (2_8_11) ----

        async Task StepRegulus(Player runner, double now)
        {
            var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == Regulus);
            if (entry.Value is not { } boss || !Alive(boss))
            {
                if (magma.Count > 0)
                {
                    magma.Clear();
                    foreach (var p in ActivePlayers(runner))
                        if (MagmaExposures.TryGetValue(p, out var e)) e.Last = double.NaN;
                }
                return;
            }
            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 110, awakeId: entry.Key);
            if (target is null)
            {
                foreach (var p in ActivePlayers(runner))
                    if (MagmaExposures.TryGetValue(p, out var e)) e.Last = double.NaN;
                return;
            }
            if (nextSpecialAt == 0) { nextSpecialAt = now + 4.5; nextSecondaryAt = now + 8.0; }
            var aim = target.Walk.Position(now);

            // AxisBossSummonMagmaDaemon[4218] magma bursts + lava pools
            if (now >= nextSpecialAt)
            {
                nextSpecialAt = now + 8.5;
                var rng = AreaRng(runner);
                var atk = ElementalSpellAttack(boss, DamageType.Fire, 1.1, 10.5);
                for (int i = 0; i < 3; i++)
                {
                    var drop = RingSpot(runner, aim, rng.NextDouble() * Math.Tau, i == 0 ? 0 : 7 + i * 3);
                    hits.Add(new(now + 1.0 + i * 0.25, entry.Key, boss, drop, 10.5, atk, null, MagmaImpactArt, 1.1f));
                    await SpawnTimedEffect(runner, effects, drop, MagmaPoolArt, now + 7.0, scale: 1.0f);
                    magma.Add(new(drop, now + 7.0, 9.0));
                }
            }
            // AxisLeapSlam[814]
            else if (now >= nextSecondaryAt && Distance(pos, aim) is >= 12 and <= 50)
            {
                nextSecondaryAt = now + 9.0;
                var dest = WalkableNear(runner, aim);
                boss.Walk.Teleport(dest.X, dest.Y, now);
                await SendWorldRecord(runner, entry.Key, InstanceProtocol.Teleport(entry.Key, dest.X, dest.Y), "[Regulus] Leap Slam");
                hits.Add(new(now + 0.6, entry.Key, boss, dest, 14,
                    HybridAttack(boss, DamageType.Fire, 0.85, 0.75, 14), null, MagmaImpactArt, 1.2f));
            }

            if (magma.Count > 0)
            {
                var (min, max) = Monsters.BaseDamage(boss.Variety!, boss.Level) ?? (28, 38);
                double burnDps = (min + max) * 0.42;
                foreach (var p in ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)))
                {
                    var exp = MagmaExposures.GetValue(p, _ => new());
                    await ArenaHazards.Ground(p, exp, now, (at, _) =>
                        magma.Any(m => Distance(at, m.At) <= m.Radius)
                            ? DamagePacket.Of(DamageType.Fire, burnDps, burnDps)
                            : DamagePacket.Empty,
                        () => !stopped && Alive(boss));
                }
            }
        }
    }
}
