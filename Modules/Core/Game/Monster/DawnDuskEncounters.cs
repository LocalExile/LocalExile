using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Act6To8Driver;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Act 8 — Solaris Temple Level 2 (<c>2_8_12_2</c>, <b>Dawn, Harbinger of Solaris</b> <c>GuardianOfSolaris[5681]</c>)
/// and Lunaris Temple Level 2 (<c>2_8_7_2</c>, <b>Dusk, Harbinger of Lunaris</b> <c>GuardianOfLunaris[5657]</c>):
/// <list type="bullet">
/// <item><b>Dawn</b>: All 4 <c>MoveDaemonIonCannon{Parallel,Cross,Square,Slice}[8571/8578/8587/8595]</c> sweeping solar
/// beam patterns, channeled <c>DawnFireBeamDivine[6064]</c> sweeping laser, <c>DawnBladefall[5315]</c>, and
/// <c>DawnThrownShieldProjectile[6280]</c>.</item>
/// <item><b>Dusk</b>: <c>ColdFlameDashDusk[4978]</c> frost-dash leaving chilled ground, curved-arc glaives
/// (<c>DuskCurveProjectile1..6[6487..6492]</c>), rectangular carpet-bomb mortar (<c>CarpetBombProjectileDusk[8524]</c> /
/// <c>MortarInRectangleDusk[8526]</c>), <c>DuskBarrage[3168]</c>, and charged <c>MonsterSnipeDuskBoss[5152]</c>.</item>
/// </list>
/// </summary>
public static class DawnDuskEncounters
{
    public const string SolarisArea = "2_8_12_2";
    public const string LunarisArea = "2_8_7_2";
    public const string Dawn = "Metadata/Monsters/GuardianOfSolaris/GuardianOfSolaris"; // MV 5681
    public const string Dusk = "Metadata/Monsters/GuardianOfLunaris/GuardianOfLunaris"; // MV 5657

    static readonly int[] DawnEffects = [406, 5315, 6280, 6064, 8571, 8578, 8587, 8595];
    static readonly int[] DuskEffects = [406, 5152, 4978, 6487, 6488, 6489, 3168, 6490, 6491, 6492, 8524, 8526];

    const string SolarBeamArt = "Metadata/Effects/Spells/monsters_effects/solaris_and_lunaris/solaris_e/solarbeam_beam.ao";
    const string SolarImpactArt = "Metadata/Effects/Spells/monsters_effects/solaris_and_lunaris/solaris_e/solarbeam_impact.ao";
    const string FireBeamArt = "Metadata/Effects/Spells/firebeam/firebeam_beam.ao";
    const string ShieldTossArt = "Metadata/Effects/Spells/monsters_effects/act8/dawn/shieldtoss.ao";
    const string ShieldSurgeArt = "Metadata/Effects/Spells/monsters_effects/act8/dawn/shieldtoss_surge.ao";

    const string GlaiveProjArt = "Metadata/Effects/Spells/monsters_effects/solaris_and_lunaris/lunaris_e/glaive_projectile.ao";
    const string GlaiveImpactArt = "Metadata/Effects/Spells/monsters_effects/solaris_and_lunaris/lunaris_e/glaive_impact.ao";
    const string CarpetFirstArt = "Metadata/Effects/Spells/monsters_effects/act8/dusk/carpetbomb/projectilefirst.ao";
    const string CarpetImpactArt = "Metadata/Effects/Spells/monsters_effects/act8/dusk/carpetbomb/impact.ao";
    const string SnipeBuildupArt = "Metadata/Effects/Spells/monsters_effects/act8/dusk/snipe/buildup.ao";
    const string SnipeShotArt = "Metadata/Effects/Spells/monsters_effects/act8/dusk/snipe/powershot.ao";
    const string ColdDashArt = "Metadata/Effects/Spells/flame_dash/flame_dash.ao";

    static readonly RuleTable<InstanceState, DawnFight> DawnFights = new();
    static readonly RuleTable<InstanceState, DuskFight> DuskFights = new();
    static readonly RuleTable<Player, ArenaHazards.Exposure> ColdGroundExposures = new();

    public static async Task Tick(Player player)
    {
        if (!CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        if (player.Layout.Id == SolarisArea) await TickDawn(player);
        else if (player.Layout.Id == LunarisArea) await TickDusk(player);
    }

    static async Task TickDawn(Player player)
    {
        if (!DawnFights.TryGetValue(player.Instance, out var fight))
        {
            if (!player.Area.Monsters.Values.Any(m => m.Hostile && m.Variety?.Id == Dawn))
                return;
            fight = DawnFights.GetValue(player.Instance, _ => new DawnFight());
        }
        await fight.Gate.WaitAsync();
        try
        {
            if (IsMonsterRunner(player))
                await fight.Step(player, AreaTime(player));
        }
        finally { fight.Gate.Release(); }
    }

    static async Task TickDusk(Player player)
    {
        if (!DuskFights.TryGetValue(player.Instance, out var fight))
        {
            if (!player.Area.Monsters.Values.Any(m => m.Hostile && m.Variety?.Id == Dusk))
                return;
            fight = DuskFights.GetValue(player.Instance, _ => new DuskFight());
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
        if (ColdGroundExposures.TryGetValue(player, out var exp)) exp.Last = double.NaN;
        return Task.CompletedTask;
    }

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var p in DawnFights) p.Value.Stop();
            foreach (var p in DuskFights) p.Value.Stop();
            foreach (var p in ColdGroundExposures) p.Value.Last = double.NaN;
        }
    }

    // ---- Dawn, Harbinger of Solaris (2_8_12_2) ----

    sealed class IonBeam(uint fxId, (int X, int Y) from, (int X, int Y) to, double started, double ends)
    {
        public readonly uint FxId = fxId;
        public readonly (int X, int Y) From = from, To = to;
        public readonly double Started = started, Ends = ends;
        public double NextPulse = started;
        public (int X, int Y) Position(double now)
        {
            double t = Math.Clamp((now - Started) / Math.Max(0.1, Ends - Started), 0, 1);
            return ((int)Math.Round(From.X + (To.X - From.X) * t), (int)Math.Round(From.Y + (To.Y - From.Y) * t));
        }
    }

    sealed class SweepingDivineBeam((int X, int Y) origin, double startAngle, double sweepRad, double started, double ends)
    {
        public readonly (int X, int Y) Origin = origin;
        public readonly double StartAngle = startAngle, SweepRad = sweepRad, Started = started, Ends = ends;
        public double NextPulse = started;
    }

    sealed class DawnFight
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly List<TimedEffect> effects = [];
        readonly List<ScheduledHit> hits = [];
        readonly List<IonBeam> ionBeams = [];

        uint bossId;
        bool initialized, cleaned, stopped;
        int ionPattern;
        double nextChaseAt, nextIonAt, nextDivineBeamAt, nextBladefallAt, nextShieldAt;
        SweepingDivineBeam? divineBeam;

        public void Stop() => stopped = true;

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            await ResolveHits(runner, hits, effects, now, () => !stopped);

            var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == Dawn);
            if (entry.Value is not { } boss) return;
            bossId = entry.Key;

            if (!Alive(boss))
            {
                if (!cleaned) await Cleanup(runner);
                return;
            }

            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 130, awakeId: bossId);
            if (target is null) return;

            if (!initialized)
            {
                initialized = true;
                boss.Aggro = true;
                await BindEffects(runner, bossId, boss, DawnEffects);
                await EnsureBossBar(runner, bossId, boss);
                nextIonAt = now + 4.5;
                nextDivineBeamAt = now + 8.5;
                nextBladefallAt = now + 3.0;
                nextShieldAt = now + 6.0;
                Console.WriteLine($"[gateway] Dawn controller active for 0x{bossId:x}");
            }
            else
            {
                boss.Target = target;
                await EnsureBossBar(runner, bossId, boss);
            }

            await StepIonBeams(runner, boss, now);
            await StepDivineBeam(runner, boss, now);

            if (!CanAct(boss, now)) return;

            var aim = target.Walk.Position(now);
            double dist = Distance(pos, aim);

            // 4 MoveDaemonIonCannon patterns: Parallel[8571], Cross[8578], Square[8587], Slice[8595]
            if (now >= nextIonAt)
            {
                nextIonAt = now + 10.5;
                int pattern = ionPattern++ % 4;
                int effect = pattern switch { 0 => 8571, 1 => 8578, 2 => 8587, _ => 8595 };
                if (await CastAction(runner, bossId, boss, effect, aim, 1.35, now, $"[Dawn] Ion Cannon {pattern}") is not null)
                {
                    await SpawnIonPattern(runner, pos, aim, pattern, now);
                    return;
                }
            }

            // Channeled DawnFireBeamDivine[6064]
            if (divineBeam is null && now >= nextDivineBeamAt && dist <= 65)
            {
                nextDivineBeamAt = now + 12.0;
                if (await CastAction(runner, bossId, boss, 6064, aim, 2.8, now, "[Dawn] Divine Fire Beam") is not null)
                {
                    double startAngle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X) - Math.PI / 5.0;
                    await SpawnTimedEffect(runner, effects, pos, FireBeamArt, now + 2.8, scale: 1.25f);
                    divineBeam = new(pos, startAngle, Math.PI * 0.4, now + 0.6, now + 2.7);
                    return;
                }
            }

            // DawnBladefall[5315]
            if (now >= nextBladefallAt && dist <= 62)
            {
                nextBladefallAt = now + 6.5;
                if (await CastAction(runner, bossId, boss, 5315, aim, 1.2, now, "[Dawn] Bladefall") is { } gen)
                {
                    double angle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                    var atk = HybridAttack(boss, DamageType.Fire, 0.65, 0.55, 11, spell: true);
                    for (int s = 0; s < 3; s++)
                    {
                        var spot = WalkableNear(runner, (
                            (int)Math.Round(aim.X + Math.Cos(angle) * (s * 8 - 8)),
                            (int)Math.Round(aim.Y + Math.Sin(angle) * (s * 8 - 8))));
                        hits.Add(new(now + 0.55 + s * 0.25, bossId, boss, spot, 10.5, atk, gen, SolarImpactArt, 1.05f));
                    }
                    return;
                }
            }

            // DawnThrownShieldProjectile[6280]
            if (now >= nextShieldAt && dist <= 58)
            {
                nextShieldAt = now + 5.5;
                if (await CastAction(runner, bossId, boss, 6280, aim, 1.0, now, "[Dawn] Thrown Shield") is { } gen)
                {
                    await SpawnTimedEffect(runner, effects, aim, ShieldTossArt, now + 0.9, scale: 1.1f);
                    hits.Add(new(now + 0.55, bossId, boss, aim, 11,
                        HybridAttack(boss, DamageType.Fire, 0.8, 0.6, 11), gen, ShieldSurgeArt, 1.1f));
                    return;
                }
            }

            nextChaseAt = await StepChaseOrMelee(runner, bossId, boss, target, now, nextChaseAt, hits, meleeScale: 1.05);
        }

        async Task SpawnIonPattern(Player runner, (int X, int Y) center, (int X, int Y) aim, int pattern, double now)
        {
            double duration = 3.6;
            List<((int X, int Y) From, (int X, int Y) To)> lanes = [];
            const int span = 34;
            switch (pattern)
            {
                case 0: // Parallel sweeping bars
                    lanes.Add(((aim.X - span, aim.Y - 14), (aim.X + span, aim.Y - 14)));
                    lanes.Add(((aim.X + span, aim.Y + 14), (aim.X - span, aim.Y + 14)));
                    break;
                case 1: // Cross intersecting beams
                    lanes.Add(((aim.X - span, aim.Y), (aim.X + span, aim.Y)));
                    lanes.Add(((aim.X, aim.Y - span), (aim.X, aim.Y + span)));
                    break;
                case 2: // Closing Square around target
                    lanes.Add(((aim.X - span, aim.Y - span), (aim.X + span, aim.Y - span)));
                    lanes.Add(((aim.X + span, aim.Y - span), (aim.X + span, aim.Y + span)));
                    lanes.Add(((aim.X + span, aim.Y + span), (aim.X - span, aim.Y + span)));
                    lanes.Add(((aim.X - span, aim.Y + span), (aim.X - span, aim.Y - span)));
                    break;
                default: // Diagonal Slice through arena center
                    lanes.Add(((center.X - span, center.Y - span), (center.X + span, center.Y + span)));
                    lanes.Add(((center.X - span, center.Y + span), (center.X + span, center.Y - span)));
                    break;
            }
            foreach (var (from, to) in lanes)
            {
                uint fx = await SpawnTimedEffect(runner, effects, from, SolarBeamArt, now + duration, scale: 1.15f);
                await SendWorldRecord(runner, fx, InstanceProtocol.MoveTo(fx, to.X, to.Y, (float)duration, InstanceProtocol.FollowTerrain), null);
                ionBeams.Add(new(fx, from, to, now, now + duration));
            }
        }

        async Task StepIonBeams(Player runner, SpawnedMonster boss, double now)
        {
            if (ionBeams.Count == 0) return;
            var players = ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)).ToArray();
            var atk = ElementalSpellAttack(boss, DamageType.Fire, 0.8, 9);
            for (int i = ionBeams.Count - 1; i >= 0; i--)
            {
                var b = ionBeams[i];
                if (now >= b.Ends) { ionBeams.RemoveAt(i); continue; }
                if (now < b.NextPulse) continue;
                b.NextPulse = now + 0.4;
                var cur = b.Position(now);
                foreach (var p in players)
                    await ArenaHazards.Hit(p, bossId, boss, atk, at => Distance(at, cur) <= 8.5, () => !stopped && Alive(boss));
            }
        }

        async Task StepDivineBeam(Player runner, SpawnedMonster boss, double now)
        {
            if (divineBeam is not { } beam) return;
            if (now >= beam.Ends) { divineBeam = null; return; }
            if (now < beam.NextPulse) return;
            beam.NextPulse = now + 0.35;
            double t = Math.Clamp((now - beam.Started) / Math.Max(0.1, beam.Ends - beam.Started), 0, 1);
            double angle = beam.StartAngle + beam.SweepRad * t;
            await HitBeamLine(runner, bossId, boss, beam.Origin, angle, 64, 6.0,
                ElementalSpellAttack(boss, DamageType.Fire, 0.85, 64), () => !stopped && Alive(boss));
        }

        async Task Cleanup(Player runner)
        {
            cleaned = true;
            divineBeam = null;
            ionBeams.Clear();
            hits.Clear();
            await ClearAllEffects(runner, effects);
            Console.WriteLine("[gateway] Dawn defeated; solar beams cleared");
        }
    }

    // ---- Dusk, Harbinger of Lunaris (2_8_7_2) ----

    sealed record ColdPatch((int X, int Y) At, double Ends, double Radius);
    sealed class CurvedGlaive(uint fxId, (int X, int Y) origin, double radius, double startAngle, double sweepRad, double started, double ends)
    {
        public readonly uint FxId = fxId;
        public readonly (int X, int Y) Origin = origin;
        public readonly double Radius = radius, StartAngle = startAngle, SweepRad = sweepRad, Started = started, Ends = ends;
        public readonly HashSet<Player> Hit = [];
        public (int X, int Y) Position(double now)
        {
            double t = Math.Clamp((now - Started) / Math.Max(0.1, Ends - Started), 0, 1);
            double a = StartAngle + SweepRad * t;
            return ((int)Math.Round(Origin.X + Math.Cos(a) * Radius), (int)Math.Round(Origin.Y + Math.Sin(a) * Radius));
        }
    }

    sealed class DuskFight
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly List<TimedEffect> effects = [];
        readonly List<ScheduledHit> hits = [];
        readonly List<ColdPatch> coldGround = [];
        readonly List<CurvedGlaive> glaives = [];

        uint bossId;
        bool initialized, cleaned, stopped;
        int glaiveCycle;
        double nextChaseAt, nextDashAt, nextGlaiveAt, nextCarpetAt, nextSnipeAt;
        double snipeFiresAt;
        (int X, int Y) snipeOrigin;
        double snipeAngle;

        public void Stop() => stopped = true;

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            await ResolveHits(runner, hits, effects, now, () => !stopped);
            coldGround.RemoveAll(c => now >= c.Ends);

            var entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == Dusk);
            if (entry.Value is not { } boss) return;
            bossId = entry.Key;

            if (!Alive(boss))
            {
                if (!cleaned) await Cleanup(runner);
                return;
            }

            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 135, awakeId: bossId);
            if (target is null)
            {
                ResetColdExposures(runner);
                return;
            }

            if (!initialized)
            {
                initialized = true;
                boss.Aggro = true;
                await BindEffects(runner, bossId, boss, DuskEffects);
                await EnsureBossBar(runner, bossId, boss);
                nextDashAt = now + 5.0;
                nextGlaiveAt = now + 3.5;
                nextCarpetAt = now + 7.5;
                nextSnipeAt = now + 10.0;
                Console.WriteLine($"[gateway] Dusk controller active for 0x{bossId:x}");
            }
            else
            {
                boss.Target = target;
                await EnsureBossBar(runner, bossId, boss);
            }

            // Resolve charged MonsterSnipeDuskBoss[5152] piercing beam
            if (snipeFiresAt > 0 && now >= snipeFiresAt)
            {
                snipeFiresAt = 0;
                var endPt = WalkableNear(runner, (
                    (int)Math.Round(snipeOrigin.X + Math.Cos(snipeAngle) * 58),
                    (int)Math.Round(snipeOrigin.Y + Math.Sin(snipeAngle) * 58)));
                await SpawnTimedEffect(runner, effects, endPt, SnipeShotArt, now + 1.1, scale: 1.25f);
                await HitBeamLine(runner, bossId, boss, snipeOrigin, snipeAngle, 72, 6.5,
                    HybridAttack(boss, DamageType.Cold, 0.9, 1.1, 72), () => !stopped && Alive(boss));
            }

            await StepGlaives(runner, boss, now);
            await StepColdGround(runner, boss, now);

            if (!CanAct(boss, now)) return;

            var aim = target.Walk.Position(now);
            double dist = Distance(pos, aim);

            // Charged MonsterSnipeDuskBoss[5152]
            if (now >= nextSnipeAt && dist <= 72)
            {
                nextSnipeAt = now + 11.5;
                if (await CastAction(runner, bossId, boss, 5152, aim, 1.9, now, "[Dusk] Charged Snipe") is not null)
                {
                    snipeOrigin = pos;
                    snipeAngle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                    snipeFiresAt = now + 1.45;
                    await SpawnTimedEffect(runner, effects, pos, SnipeBuildupArt, now + 1.5, scale: 1.2f);
                    return;
                }
            }

            // Rectangular carpet-bomb mortar (8524/8526)
            if (now >= nextCarpetAt && dist <= 68)
            {
                nextCarpetAt = now + 9.0;
                int eff = AreaRng(runner).Next(2) == 0 ? 8524 : 8526;
                if (await CastAction(runner, bossId, boss, eff, aim, 1.45, now, "[Dusk] Carpet Bomb Mortar") is { } gen)
                {
                    double angle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                    double cos = Math.Cos(angle), sin = Math.Sin(angle);
                    double px = -sin, py = cos;
                    var atk = ElementalSpellAttack(boss, DamageType.Cold, 0.95, 9.5);
                    for (int row = 0; row < 4; row++)
                    {
                        for (int col = -1; col <= 1; col += 2)
                        {
                            var drop = WalkableNear(runner, (
                                (int)Math.Round(pos.X + cos * (12 + row * 10) + px * (col * 8)),
                                (int)Math.Round(pos.Y + sin * (12 + row * 10) + py * (col * 8))));
                            await SpawnTimedEffect(runner, effects, drop, CarpetFirstArt, now + 0.8 + row * 0.22, scale: 1.0f);
                            hits.Add(new(now + 0.75 + row * 0.22, bossId, boss, drop, 9.0, atk, gen, CarpetImpactArt, 1.05f));
                        }
                    }
                    return;
                }
            }

            // Curved-arc glaives (DuskCurveProjectile1..6 [6487..6492])
            if (now >= nextGlaiveAt && dist <= 68)
            {
                nextGlaiveAt = now + 5.8;
                int eff = 6487 + (glaiveCycle++ % 6);
                if (await CastAction(runner, bossId, boss, eff, aim, 1.1, now, $"[Dusk] Curved Glaives {eff}") is not null)
                {
                    double aimAngle = Math.Atan2(aim.Y - pos.Y, aim.X - pos.X);
                    double arcRadius = Math.Clamp(dist, 16, 44);
                    for (int side = -1; side <= 1; side += 2)
                    {
                        double startA = aimAngle + side * 1.1;
                        double sweep = -side * 2.2;
                        var startPt = RingSpot(runner, pos, startA, arcRadius);
                        var endPt = RingSpot(runner, pos, startA + sweep, arcRadius);
                        uint fx = await SpawnTimedEffect(runner, effects, startPt, GlaiveProjArt, now + 2.0, scale: 1.1f);
                        await SendWorldRecord(runner, fx, InstanceProtocol.MoveTo(fx, endPt.X, endPt.Y, 2.0f, InstanceProtocol.FollowTerrain), null);
                        glaives.Add(new(fx, pos, arcRadius, startA, sweep, now, now + 2.0));
                    }
                    return;
                }
            }

            // ColdFlameDashDusk[4978] reposition + chilled ground trail
            if (now >= nextDashAt)
            {
                nextDashAt = now + 8.0;
                double escapeAngle = Math.Atan2(pos.Y - aim.Y, pos.X - aim.X) + (AreaRng(runner).NextDouble() - 0.5) * 1.4;
                var dest = RingSpot(runner, aim, escapeAngle, 28);
                if (await CastAction(runner, bossId, boss, 4978, dest, 0.85, now, "[Dusk] Cold Flame Dash") is not null)
                {
                    for (int s = 0; s <= 2; s++)
                    {
                        double t = s / 2.0;
                        var pt = WalkableNear(runner, (
                            (int)Math.Round(pos.X + (dest.X - pos.X) * t),
                            (int)Math.Round(pos.Y + (dest.Y - pos.Y) * t)));
                        await SpawnTimedEffect(runner, effects, pt, ColdDashArt, now + 6.0, scale: 1.0f);
                        coldGround.Add(new(pt, now + 6.0, 8.5));
                    }
                    boss.Walk.Teleport(dest.X, dest.Y, now);
                    await SendWorldRecord(runner, bossId, InstanceProtocol.Teleport(bossId, dest.X, dest.Y), null);
                    return;
                }
            }

            // DuskBarrage[3168] ranged default
            if (dist <= 64)
            {
                if (await CastAction(runner, bossId, boss, 3168, aim, 1.0, now, "[Dusk] Barrage") is { } gen)
                {
                    hits.Add(new(now + 0.5, bossId, boss, aim, 9.0,
                        HybridAttack(boss, DamageType.Cold, 0.45, 0.65, 9.0), gen, GlaiveImpactArt, 0.95f));
                    return;
                }
            }

            nextChaseAt = await StepChaseOrMelee(runner, bossId, boss, target, now, nextChaseAt, hits, meleeScale: 0.95);
        }

        async Task StepGlaives(Player runner, SpawnedMonster boss, double now)
        {
            if (glaives.Count == 0) return;
            var players = ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)).ToArray();
            var atk = HybridAttack(boss, DamageType.Cold, 0.55, 0.75, 8.5);
            for (int i = glaives.Count - 1; i >= 0; i--)
            {
                var g = glaives[i];
                if (now >= g.Ends) { glaives.RemoveAt(i); continue; }
                var cur = g.Position(now);
                foreach (var p in players)
                {
                    if (g.Hit.Contains(p) || Distance(p.Walk.Position(now), cur) > 8.0) continue;
                    g.Hit.Add(p);
                    await SpawnTimedEffect(runner, effects, cur, GlaiveImpactArt, now + 0.8, scale: 1.0f);
                    await ArenaHazards.Hit(p, bossId, boss, atk, at => Distance(at, cur) <= 9.0, () => !stopped && Alive(boss));
                }
            }
        }

        async Task StepColdGround(Player runner, SpawnedMonster boss, double now)
        {
            if (coldGround.Count == 0)
            {
                ResetColdExposures(runner);
                return;
            }
            var (min, max) = Monsters.BaseDamage(boss.Variety!, boss.Level) ?? (28, 38);
            double dps = (min + max) * 0.4;
            foreach (var p in ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)))
            {
                var exp = ColdGroundExposures.GetValue(p, _ => new());
                await ArenaHazards.Ground(p, exp, now, (at, _) =>
                    coldGround.Any(c => Distance(at, c.At) <= c.Radius)
                        ? DamagePacket.Of(DamageType.Cold, dps, dps)
                        : DamagePacket.Empty,
                    () => !stopped && Alive(boss));
            }
        }

        static void ResetColdExposures(Player runner)
        {
            foreach (var p in ActivePlayers(runner))
                if (ColdGroundExposures.TryGetValue(p, out var exp)) exp.Last = double.NaN;
        }

        async Task Cleanup(Player runner)
        {
            cleaned = true;
            glaives.Clear();
            coldGround.Clear();
            hits.Clear();
            ResetColdExposures(runner);
            await ClearAllEffects(runner, effects);
            Console.WriteLine("[gateway] Dusk defeated; glaives and hazards cleared");
        }
    }
}
