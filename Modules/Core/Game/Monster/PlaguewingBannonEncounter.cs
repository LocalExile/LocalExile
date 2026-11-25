using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Act9To10Encounters;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Act 10 — The Cathedral Rooftop (<c>2_10_1</c>) Bannon rescue &amp; Plaguewing encounter controller:
/// <list type="bullet">
/// <item><b>Bannon Rooftop Defense</b>: Triggered on approaching <c>BannonRooftop</c> before
/// <c>a10q1-RescuedBannon</c>: spawns 2 staged waves of <c>KitavaCultist*Event</c> + <c>KitavaDemonBannon</c>
/// ("Kitava's Herald", <c>NoRewards = true</c>) while holding any pre-spawned Plaguewing dormant until waves clear.</item>
/// <item><b>Plaguewing (<c>KitavaDemonPlaguewing[5521]</c>) Finale</b>: Full combat with
/// <c>MonsterLeapSlamCausticCloudPlaguewing[839]</c> (leap slam leaving caustic pools),
/// <c>PlaguewingWhirlingBlades[352]</c> (fast sweeping dash slash), and
/// <c>CausticMortarElementalPlaguewing[5213]</c> (3-orb caustic mortar barrage). Sets <c>a10q1-RescuedBannon</c>
/// on Plaguewing's death so <c>BannonRooftop</c> transitions to standing state 2.</item>
/// </list>
/// </summary>
public static class PlaguewingBannonEncounter
{
    public const string Area = "2_10_1";
    // Verified exact MonsterVarieties.Id in client.db (singular "Monster", not "Monsters"):
    public const string Plaguewing = "Metadata/Monster/KitavaDemon/KitavaDemonPlaguewing";     // [5521]
    public const string KitavasHerald = "Metadata/Monster/KitavaDemon/KitavaDemonBannon";      // [5520]
    public static readonly string[] EventCultists =
    [
        "Metadata/Monsters/KitavaCultist/KitavaCultistDaggersEvent",     // [5975]
        "Metadata/Monsters/KitavaCultist/KitavaCultistSpearEvent",       // [5976]
        "Metadata/Monsters/KitavaCultist/KitavaCultistKnifestickEvent",  // [5977]
        "Metadata/Monsters/KitavaCultist/KitavaCultistSpearCandleEvent", // [5979]
    ];

    public const string BannonRooftopType = "Metadata/NPC/Act10/BannonRooftop";
    public const string HeraldSpawnerType = "Metadata/Monsters/KitavaDemon/KitavaDemonSpawnerBannon";
    public const string PlaguewingSpawnerType = "Metadata/Monsters/KitavaDemon/KitavaDemonSpawnerPlaguewing";
    public const string RescuedBannonFlag = "a10q1-RescuedBannon";

    // Verified GrantedEffects rows on KitavaDemonPlaguewing[5521] in client.db:
    const int MeleeEffect = 406;
    const int LeapSlamCausticEffect = 839;   // MonsterLeapSlamCausticCloudPlaguewing
    const int CausticMortarEffect = 5213;    // CausticMortarElementalPlaguewing
    const int WhirlingBladesEffect = 352;    // PlaguewingWhirlingBlades
    static readonly int[] PlaguewingEffects = [MeleeEffect, LeapSlamCausticEffect, CausticMortarEffect, WhirlingBladesEffect];

    // Verified BuffDefinitions rows in client.db:
    const ushort CannotBeDamagedBuff = 350;  // cannot_be_damaged
    const ushort CausticCloudBuff = 484;     // caustic_cloud

    // Verified .ao visual paths in game-files.zip:
    const string MortarBuildupArt = "Metadata/Effects/Spells/monsters_effects/kitava_demon/mortar_buildup_plaguewing.ao";
    const string SlamImpactArt = "Metadata/Effects/Spells/monsters_effects/kitava_demon/slam_impact.ao";
    const string XExplosionArt = "Metadata/Effects/Spells/monsters_effects/kitava_demon/x_explosion.ao";
    const string CausticGroundArt = "Metadata/Effects/Spells/ground_effects/caustic/caustic_01.ao";

    static readonly RuleTable<InstanceState, Fight> Fights = new();

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

    public static async Task OnMonsterKilled(MonsterKill kill)
    {
        if (kill.Player.Layout.Id != Area || kill.Monster.VarietyId != Plaguewing) return;
        await GrantRescuedBannon(kill.Player);
    }

    public static async Task Leaving(Player player)
    {
        if (player.Layout.Id != Area) return;
        if (Fights.TryGetValue(player.Instance, out var fight))
            await fight.ReleasePlayerDebuffs(player);
    }

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights) pair.Value.StopAndClear();
        }
    }

    static async Task GrantRescuedBannon(Player runner)
    {
        foreach (var p in ActivePlayers(runner))
        {
            if (!ClientHasQuestFlag(p, RescuedBannonFlag))
            {
                await SetQuestFlags(p, [RescuedBannonFlag]);
                await UpdateQuestObjectStates(p, [RescuedBannonFlag]);
            }
        }
    }

    sealed record CausticPool((int X, int Y) At, double Radius, double Until);
    sealed record DelayedAttack(double At, uint SourceId, SpawnedMonster Source, MonsterAttack Attack,
        (int X, int Y) From, (int X, int Y) To, double Radius, bool IsDash, bool LeavesPool, string? BurstAo);

    sealed class Fight
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly GameArea area;
        readonly List<TimedEffect> effects = [];
        readonly List<CausticPool> pools = [];
        readonly List<DelayedAttack> attacks = [];
        readonly HashSet<uint> boundBosses = [];
        readonly HashSet<uint> waveAdds = [];
        readonly Dictionary<Player, ArenaHazards.Exposure> causticExposures = [];
        readonly object causticHolder = new();

        readonly (int X, int Y)? bannonSpot;
        readonly (int X, int Y)? heraldSpot;
        readonly (int X, int Y)? plaguewingSpot;

        uint plaguewingId;
        int waveStage; // 0 = idle, 1 = cultist wave 1, 2 = cultist + Herald wave 2, 3 = Plaguewing finale
        bool plaguewingHeld, bossInitialized, cleaned, stopped;
        double waveTimeoutAt, nextSpecial, nextPoolTick;
        int rotation;

        public Fight(Player owner)
        {
            area = owner.Area;
            var placed = AreaPlacements(owner);
            bannonSpot = placed.Of(BannonRooftopType).FirstOrDefault() is { } b ? (b.X, b.Y) : null;
            heraldSpot = placed.Of(HeraldSpawnerType).FirstOrDefault() is { } h ? (h.X, h.Y) : null;
            plaguewingSpot = placed.Of(PlaguewingSpawnerType).FirstOrDefault() is { } p ? (p.X, p.Y) : null;
        }

        public void StopAndClear()
        {
            stopped = true;
            if (plaguewingId != 0 && area.Monsters.TryGetValue(plaguewingId, out var boss))
            {
                ClearInvulnerableOnUnload(area, plaguewingId, boss, CannotBeDamagedBuff);
                boss.WorldHidden = false;
            }
            foreach (var p in causticExposures.Keys.ToArray())
                _ = ReleasePlayerDebuff(p, causticHolder);
            causticExposures.Clear();
        }

        public async Task ReleasePlayerDebuffs(Player p)
        {
            causticExposures.Remove(p);
            await ReleasePlayerDebuff(p, causticHolder);
        }

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            pools.RemoveAll(p => now >= p.Until);
            await StepDelayedAttacks(runner, now);

            var bossEntry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Variety?.Id == Plaguewing);
            if (bossEntry.Value is { } existingBoss)
                plaguewingId = bossEntry.Key;

            // If Plaguewing is dead, clean up and ensure Bannon is rescued.
            if (bossEntry.Value is { } deadCheck && !Alive(deadCheck))
            {
                if (!cleaned) await Cleanup(runner);
                return;
            }

            if (cleaned) return;

            var anchor = bannonSpot ?? plaguewingSpot ?? bossEntry.Value?.Walk.Position(now);
            if (anchor is not { } arenaAt) return;

            var nearPlayer = NearestLivingTarget(runner, arenaAt, now, maxRange: 95);
            if (waveStage == 0)
            {
                if (nearPlayer is null) return;
                // Skip ambush waves if all active players already rescued Bannon.
                if (ActivePlayers(runner).All(p => ClientHasQuestFlag(p, RescuedBannonFlag)))
                {
                    if (bossEntry.Value is null) return;
                    waveStage = 3;
                }
                else
                {
                    await StartWave1(runner, arenaAt, now);
                }
            }

            // Hold any Act10.cs On.SpawnsNear-created Plaguewing hidden/invulnerable until Wave 2 clears.
            if (waveStage is 1 or 2)
            {
                if (bossEntry.Value is { } held && Alive(held) && !plaguewingHeld)
                {
                    plaguewingHeld = true;
                    SetInvulnerable(runner, bossEntry.Key, held, true, CannotBeDamagedBuff);
                    await SetWorldHidden(runner, bossEntry.Key, held, true);
                }

                bool waveAlive = waveAdds.Any(id => runner.Area.Monsters.TryGetValue(id, out var m) && Alive(m));
                if (waveAlive && now < waveTimeoutAt) return;

                if (waveStage == 1)
                {
                    await StartWave2(runner, arenaAt, now);
                    return;
                }

                await BeginPlaguewingFinale(runner, arenaAt, bossEntry.Key, bossEntry.Value, now);
                return;
            }

            // Stage 3: Active Plaguewing finale.
            if (bossEntry.Value is not { } boss)
            {
                await BeginPlaguewingFinale(runner, arenaAt, 0, null, now);
                return;
            }

            await StepPlaguewing(runner, bossEntry.Key, boss, now);
            await StepCausticPools(runner, bossEntry.Key, boss, now);
        }

        async Task StartWave1(Player runner, (int X, int Y) center, double now)
        {
            waveStage = 1;
            waveTimeoutAt = now + 28.0;
            byte level = (byte)Math.Clamp(ServerLevels.AreaLevel(runner.Layout.Id), 1, 100);
            for (int i = 0; i < 4; i++)
            {
                var spot = RingSpot(runner, center, i * (Math.Tau / 4.0), 16);
                await SpawnAdd(runner, EventCultists[i % EventCultists.Length], level, spot, 0, waveAdds,
                    ownedCurrent: () => !stopped && !cleaned);
            }
            Console.WriteLine("[gateway] Cathedral Rooftop: Bannon defense Wave 1 (Kitava Cultists) started");
        }

        async Task StartWave2(Player runner, (int X, int Y) center, double now)
        {
            waveStage = 2;
            waveTimeoutAt = now + 35.0;
            byte level = (byte)Math.Clamp(ServerLevels.AreaLevel(runner.Layout.Id), 1, 100);
            var hSpot = heraldSpot ?? RingSpot(runner, center, 0.5, 18);
            await SpawnTimedEffect(runner, effects, hSpot, SlamImpactArt, now + 1.8, scale: 1.25f);
            await SpawnAdd(runner, KitavasHerald, level, hSpot, 0, waveAdds,
                rarity: MonsterRarity.Unique, ownedCurrent: () => !stopped && !cleaned);
            for (int i = 0; i < 3; i++)
            {
                var spot = RingSpot(runner, center, i * (Math.Tau / 3.0), 18);
                await SpawnAdd(runner, EventCultists[i % EventCultists.Length], level, spot, 0, waveAdds,
                    ownedCurrent: () => !stopped && !cleaned);
            }
            Console.WriteLine("[gateway] Cathedral Rooftop: Bannon defense Wave 2 (Kitava's Herald + Cultists) started");
        }

        async Task BeginPlaguewingFinale(Player runner, (int X, int Y) center, uint existingId, SpawnedMonster? existingBoss, double now)
        {
            waveStage = 3;
            var spawnAt = plaguewingSpot ?? RingSpot(runner, center, 1.0, 16);
            if (existingBoss is not null && Alive(existingBoss))
            {
                plaguewingId = existingId;
                plaguewingHeld = false;
                await TeleportBoss(runner, existingId, existingBoss, spawnAt, now);
                await SetWorldHidden(runner, existingId, existingBoss, false);
                SetInvulnerable(runner, existingId, existingBoss, false, CannotBeDamagedBuff);
            }
            else if (Monsters.Find(Plaguewing) is { Supported: true } v)
            {
                byte level = (byte)Math.Clamp(ServerLevels.AreaLevel(runner.Layout.Id), 1, 100);
                var safe = WalkableNear(runner, spawnAt);
                plaguewingId = await SpawnMonster(runner, v, level, MonsterRarity.Unique, safe.X, safe.Y,
                    instanceOwned: true,
                    owned: (_, m) => { m.Aggro = true; m.EntranceGrace = false; },
                    ownedCurrent: () => !stopped && !cleaned);
            }
            await SpawnTimedEffect(runner, effects, spawnAt, SlamImpactArt, now + 2.0, scale: 1.35f);
            Console.WriteLine($"[gateway] Cathedral Rooftop: Plaguewing 0x{plaguewingId:x} descended for Bannon finale");
        }

        async Task StepPlaguewing(Player runner, uint bossId, SpawnedMonster boss, double now)
        {
            await EnsureNativeSkills(runner, bossId, boss, PlaguewingEffects, boundBosses);
            if (boss.Effects[Stat.BaseCannotBeDamaged] != 0)
                SetInvulnerable(runner, bossId, boss, false, CannotBeDamagedBuff);
            if (boss.WorldHidden)
                await SetWorldHidden(runner, bossId, boss, false);

            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 130, awakeId: bossId);
            if (target is null) return;

            await EnsureBossBar(runner, bossId, boss);
            boss.Target = target;
            boss.Aggro = true;
            boss.EntranceGrace = false;

            if (!bossInitialized)
            {
                bossInitialized = true;
                nextSpecial = now + 2.2;
            }

            if (now < boss.BusyUntil || now < boss.FlinchUntil || MonsterActionFactor(boss, now) == 0) return;

            var aim = target.Walk.Position(now);
            double dist = Distance(pos, aim);

            if (now >= nextSpecial)
            {
                int step = rotation++;
                nextSpecial = now + 4.2;
                if (step % 3 == 0)
                {
                    // MonsterLeapSlamCausticCloudPlaguewing[839]: leap slam + caustic pool.
                    if (await CastAction(runner, bossId, boss, PlaguewingEffects, LeapSlamCausticEffect, aim, now, DamageType.Chaos, 1.4, 15, "Plaguewing", forceSpell: false) is { } leap)
                    {
                        await TeleportBoss(runner, bossId, boss, aim, now);
                        attacks.Add(new(now + 0.85, bossId, boss, leap.Attack, pos, aim, 15, IsDash: false, LeavesPool: true, BurstAo: SlamImpactArt));
                        return;
                    }
                }
                else if (step % 3 == 1)
                {
                    // PlaguewingWhirlingBlades[352]: rapid dash slash through target.
                    double angle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                    var dashEnd = WalkableNear(runner, (
                        (int)Math.Round(aim.X + Math.Cos(angle) * 10),
                        (int)Math.Round(aim.Y + Math.Sin(angle) * 10)));
                    if (await CastAction(runner, bossId, boss, PlaguewingEffects, WhirlingBladesEffect, dashEnd, now, DamageType.Physical, 1.25, 11, "Plaguewing", forceSpell: false) is { } dash)
                    {
                        attacks.Add(new(now + 0.65, bossId, boss, dash.Attack, pos, dashEnd, 9, IsDash: true, LeavesPool: false, BurstAo: XExplosionArt));
                        return;
                    }
                }
                else
                {
                    // CausticMortarElementalPlaguewing[5213]: 3-orb caustic mortar barrage.
                    if (await CastAction(runner, bossId, boss, PlaguewingEffects, CausticMortarEffect, aim, now, DamageType.Chaos, 1.2, 12, "Plaguewing", forceSpell: true) is { } mortar)
                    {
                        await SpawnTimedEffect(runner, effects, pos, MortarBuildupArt, now + 1.4, scale: 1.2f);
                        var rng = AreaRng(runner);
                        for (int i = 0; i < 3; i++)
                        {
                            var drop = i == 0 ? aim : RingSpot(runner, aim, rng.NextDouble() * Math.Tau, 11);
                            attacks.Add(new(now + 0.9 + i * 0.25, bossId, boss, mortar.Attack, pos, drop, 11, IsDash: false, LeavesPool: true, BurstAo: XExplosionArt));
                        }
                        return;
                    }
                }
            }

            if (dist <= 16)
            {
                if (await CastAction(runner, bossId, boss, PlaguewingEffects, MeleeEffect, aim, now, DamageType.Physical, 1.05, 14, "Plaguewing", forceSpell: false) is { } melee)
                    attacks.Add(new(now + melee.Attack.Seconds * 0.45, bossId, boss, melee.Attack, pos, aim, 13, IsDash: false, LeavesPool: false, BurstAo: null));
            }
            else
            {
                await Chase(runner, bossId, boss, aim, now, "Plaguewing");
            }
        }

        async Task StepDelayedAttacks(Player runner, double now)
        {
            for (int i = attacks.Count - 1; i >= 0; i--)
            {
                var a = attacks[i];
                if (now < a.At) continue;
                attacks.RemoveAt(i);
                if (!Alive(a.Source)) continue;
                if (a.IsDash)
                {
                    await TeleportBoss(runner, a.SourceId, a.Source, a.To, now);
                    await HitSegment(runner, a.SourceId, a.Source, a.Attack, a.From, a.To, a.Radius, () => !stopped && Alive(a.Source));
                }
                else
                {
                    await HitCircle(runner, a.SourceId, a.Source, a.Attack, a.To, a.Radius, () => !stopped && Alive(a.Source));
                }
                if (a.BurstAo is { } ao)
                    await SpawnTimedEffect(runner, effects, a.To, ao, now + 1.3, scale: 1.2f);
                if (a.LeavesPool && pools.Count < 10)
                {
                    pools.Add(new(a.To, 10, now + 9.0));
                    await SpawnTimedEffect(runner, effects, a.To, CausticGroundArt, now + 9.0, scale: 1.2f);
                }
            }
        }

        async Task StepCausticPools(Player runner, uint bossId, SpawnedMonster boss, double now)
        {
            if (now < nextPoolTick) return;
            nextPoolTick = now + 0.25;
            var (minDmg, maxDmg) = Monsters.BaseDamage(boss.Variety!, boss.Level) ?? (30, 46);
            double chaosDps = (minDmg + maxDmg) * 0.55;

            foreach (var p in ActivePlayers(runner))
            {
                var at = p.Walk.Position(now);
                bool inPool = p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p) && pools.Any(pool => Distance(at, pool.At) <= pool.Radius);
                if (inPool)
                    await HoldPlayerDebuff(p, causticHolder, CausticCloudBuff, bossId, EmptyStats);
                else
                    await ReleasePlayerDebuff(p, causticHolder);

                if (!causticExposures.TryGetValue(p, out var exp))
                    causticExposures[p] = exp = new();
                await ArenaHazards.Ground(p, exp, now, (pt, _) =>
                    pools.Any(pool => Distance(pt, pool.At) <= pool.Radius)
                        ? DamagePacket.Of(DamageType.Chaos, chaosDps, chaosDps)
                        : DamagePacket.Empty,
                    () => !stopped && !cleaned);
            }
        }

        async Task Cleanup(Player runner)
        {
            cleaned = true;
            attacks.Clear();
            pools.Clear();
            await GrantRescuedBannon(runner);
            await RetireMonsters(runner, waveAdds);
            waveAdds.Clear();
            foreach (var p in ActivePlayers(runner))
                await ReleasePlayerDebuffs(p);
            await ClearAllEffects(runner, effects);
            Console.WriteLine("[gateway] Plaguewing defeated; Bannon rescued (a10q1-RescuedBannon) and hazards cleared");
        }
    }
}
