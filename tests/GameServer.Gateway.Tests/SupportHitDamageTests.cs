using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class SupportHitDamageTests
{
    static Dictionary<uint, int> With(params (uint Name, int Value)[] stats) =>
        stats.ToDictionary(s => s.Name, s => s.Value);

    static Func<uint, int> Get(params (uint Name, int Value)[] stats) =>
        name => stats.FirstOrDefault(s => s.Name == name).Value;

    static Func<uint, int> Flags(params string[] flags) => name => flags.Contains(Program.Stats.Name(name)) ? 1 : 0;

    // Hypothermia, Behead, Damage on Full Life, Awakened Increased Area of Effect, double damage, the six supports' more,
    // Point Blank, the melee proximity supports and Ruthless: each multiplies a hit only when its condition holds.
    [Fact]
    public void Supports_and_keystones_multiply_hits_when_their_conditions_hold()
    {
        // Phantasm adds projectile-spell minions to a melee summon without changing its melee damage types.
        var supports = new GemSupports();
        var skills = new SkillCatalog();
        int phantasm = skills.EffectRow("SupportSummonPhantasm");
        int multiple = skills.EffectRow("SupportGreaterMultipleProjectiles");
        int faster = skills.EffectRow("SupportFasterProjectiles");
        foreach (string active in new[] { "RaiseZombie", "SummonSkeletons" })
        {
            int effect = skills.EffectRow(active);
            GemSupports.Candidate[] projectileSupports = [new(multiple, 20, 0, 1), new(faster, 20, 0, 2)];
            Assert.Empty(supports.Resolve(effect, projectileSupports, skills));
            foreach (bool phantasmFirst in new[] { false, true })
            {
                GemSupports.Candidate[] candidates = phantasmFirst
                    ? [new(phantasm, 20, 0, 1), new(multiple, 20, 0, 2), new(faster, 20, 0, 3)]
                    : [new(multiple, 20, 0, 1), new(faster, 20, 0, 2), new(phantasm, 20, 0, 3)];
                var applied = supports.Resolve(effect, candidates, skills);
                Assert.Equal(3, applied.Count);
                var gmp = Assert.Single(applied, s => s.Gem.Effect == multiple);
                Assert.Contains(applied, s => s.Gem.Effect == faster);
                Assert.Equal(4, gmp.Stats.Single(s => s.Key == Stat.NumberOfAdditionalProjectiles).Value);
                var stats = applied.SelectMany(s => s.Stats).Select(s => (s.Key, s.Value)).ToArray();
                Assert.Equal(1d, GemSupportDamage.Multipliers(DamageTypeHistory.Physical, stats,
                    new HashSet<int> { 0, 19 }).GeneralOrPhysical);
                Assert.Equal(.74, GemSupportDamage.Multipliers(DamageTypeHistory.Physical, stats,
                    new HashSet<int> { 1, 2 }).GeneralOrPhysical, 9);
            }
            // Resolution must not retain the added minion types when Phantasm leaves the link group.
            Assert.Empty(supports.Resolve(effect, projectileSupports, skills));
        }

        // Native final-speed rounding precedes duration: Burning Arrow's -30% inherent speed.
        foreach (var (increased, final, rate) in new[] { (15, -20, 112), (30, -9, 127), (45, 2, 143) })
        {
            Assert.Equal(final, SkillAttackTiming.FinalSpeedPercent(increased, .7));
            Assert.Equal(rate, (int)Math.Round(100 / SkillAttackTiming.Seconds(714, increased, -30)));
        }
        Assert.Equal(-99, SkillAttackTiming.FinalSpeedPercent(0, 0));
        // Each support condition multiplies only when it holds.
        {
            var skill = With((Stat.SupportHypothermiaDamagePlusPctVsChilledEnemiesFinal, 29),
                (Stat.SupportExecutionerDamageVsEnemiesOnLowLifePlusPctFinal, 49),
                (Stat.SupportDamageWhileOnFullLifePlusPctFinal, 34));
            Assert.Equal(1, Game.ConditionalSupportMore(skill, attack: true, chilled: false, lowLife: false, fullLife: false, lifetap: false));
            Assert.Equal(1.29, Game.ConditionalSupportMore(skill, attack: true, chilled: true, lowLife: false, fullLife: false, lifetap: false), 9);
            Assert.Equal(1.49, Game.ConditionalSupportMore(skill, attack: false, chilled: false, lowLife: true, fullLife: false, lifetap: false), 9);
            Assert.Equal(1.34, Game.ConditionalSupportMore(skill, attack: true, chilled: false, lowLife: false, fullLife: true, lifetap: false), 9);
            Assert.Equal(1, Game.ConditionalSupportMore(skill, attack: false, chilled: false, lowLife: false, fullLife: true, lifetap: false));
            Assert.Equal(1.29 * 1.49 * 1.34, Game.ConditionalSupportMore(skill, true, true, true, true, false), 9);
            // Awakened Increased Area of Effect: more damage for area skills only.
            var awakened = new[] { (Stat.IncAoePlusMoreAreaDamagePlusPctFinal, 24) };
            Assert.Equal(1.24, GemSupportDamage.Multipliers(DamageTypeHistory.Physical, awakened, new HashSet<int> { 0, 7 }).GeneralOrPhysical, 9);
            Assert.Equal(1, GemSupportDamage.Multipliers(DamageTypeHistory.Physical, awakened, new HashSet<int> { 0 }).GeneralOrPhysical);
        }

        // A hit on a low-life enemy gets Behead's more damage.
        {
            var player = new Player(new(1, "behead", 300, 300, 100, 100, null, []), 1, 1)
            {
                Pools = new(1, 1000, 100, 2, 0), PlayerLife = 900,
            };
            var skill = With((Stat.SupportExecutionerDamageVsEnemiesOnLowLifePlusPctFinal, 30),
                (Stat.SupportDamageWhileOnFullLifePlusPctFinal, 20));
            var hit = DamagePacket.Of(DamageType.Physical, 100, 100);
            var low = new SpawnedMonster("low", new(100, 100, 0), 0, 0, 1000) { Life = 500 };
            var healthy = new SpawnedMonster("healthy", new(100, 100, 0), 0, 0, 1000) { Life = 501 };
            Assert.Equal(130, Game.WithConditionalSupportDamageLocked(player, skill, true, low, hit, 0).MinTotal, 9);
            Assert.Equal(900u, player.PlayerLife);
            Assert.Equal(1000u, player.PlayerMaxLife);
            Assert.Equal(100, Game.WithConditionalSupportDamageLocked(player, skill, true, healthy, hit, 0).MinTotal, 9);
            player.PlayerLife = 1000;   // full Life: Damage on Full Life joins in
            Assert.Equal(120, Game.WithConditionalSupportDamageLocked(player, skill, true, healthy, hit, 0).MinTotal, 9);
        }

        // Double damage: the player's chance on every hit, a spell's chance on spells, the skill's own (Heavy Strike);
        // triple damage overrides it, so double only rolls when triple misses.
        {
            var random = new Random(1);
            var hit = DamagePacket.Of(DamageType.Physical, 100, 100);
            double Dealt(HitRollModifiers hitRolls) => hitRolls.RollOutgoingHit(hit, random).MinTotal;

            Assert.Equal(100, Dealt(HitRollModifiers.From(Get())));
            Assert.Equal(200, Dealt(HitRollModifiers.From(Get((Stat.ChanceToDealDoubleDamagePct, 100)))));
            Assert.Equal(300, Dealt(HitRollModifiers.From(
                Get((Stat.ChanceToDealDoubleDamagePct, 100), (Stat.BaseChanceToDealTripleDamagePct, 100)))));
            var spells = HitRollModifiers.From(Get((Stat.SpellChanceToDealDoubleDamagePct, 100)));
            Assert.Equal(100, Dealt(spells));
            Assert.Equal(100, Dealt(spells.ForSkill(Get(), spell: false)));
            Assert.Equal(200, Dealt(spells.ForSkill(Get(), spell: true)));
            Assert.Equal(200, Dealt(HitRollModifiers.From(Get()).ForSkill(Get((Stat.ChanceToDealDoubleDamagePct, 100)), spell: false)));

            // Half triple and half double: a quarter of the hits are doubled, not half.
            var both = HitRollModifiers.From(Get((Stat.ChanceToDealDoubleDamagePct, 50), (Stat.BaseChanceToDealTripleDamagePct, 50)));
            var counts = Enumerable.Range(0, 20000).Select(_ => Dealt(both)).GroupBy(d => d).ToDictionary(g => g.Key, g => g.Count());
            Assert.InRange(counts[300], 9600, 10400);
            Assert.InRange(counts[200], 4700, 5300);
            Assert.InRange(counts[100], 4700, 5300);
        }

        // Overcharge, Bonechill, Deadly Ailments, Ancestral Call, Infused Channelling and Bloodthirst: each support's more
        // multiplies a hit once, and Bonechill's counts only for a hit with cold damage, in the elemental group.
        {
            (uint, int)[] stats = [(Stat.SupportPureShockDamagePlusPctFinal, -25), (Stat.SupportBonechillColdDamagePlusPctFinal, 22),
                (Stat.SupportBetterAilmentsHitDamagePlusPctFinal, -80), (Stat.SupportSpiritStrikeDamagePlusPctFinal, 20),
                (Stat.SupportStormBarrierDamagePlusPctFinal, 37), (Stat.SupportBloodThirstDamagePlusPctFinal, 17)];
            HashSet<int> spell = [1];
            var (general, typed) = GemSupportDamage.Multipliers(DamageTypeHistory.Cold, stats, spell);
            Assert.Equal(.75 * .2 * 1.2 * 1.37 * 1.17, general, 9);
            Assert.Equal(1.22, typed, 9);
            Assert.Equal(1, GemSupportDamage.Multipliers(DamageTypeHistory.Physical, stats, spell).ElementalOrChaos);
        }

        // Green Effluent affects every damage type and composes with the existing support multiplier.
        foreach (var history in new[] { DamageTypeHistory.Physical, DamageTypeHistory.Fire, DamageTypeHistory.Cold,
            DamageTypeHistory.Lightning, DamageTypeHistory.Chaos })
        {
            HashSet<int> types = [1];
            var baseline = GemSupportDamage.Multipliers(history, [(Stat.SupportEchoDamagePlusPctFinal, -10)], types);
            var affected = GemSupportDamage.Multipliers(history,
                [(Stat.SupportEchoDamagePlusPctFinal, -10), (Stat.DoedreAuraDamagePlusPctFinal, -70)], types);
            Assert.Equal(baseline.GeneralOrPhysical * .3, affected.GeneralOrPhysical, 8);
            Assert.Equal(baseline.ElementalOrChaos, affected.ElementalOrChaos);
            Assert.Equal(baseline, GemSupportDamage.Multipliers(history,
                [(Stat.SupportEchoDamagePlusPctFinal, -10), (Stat.DoedreAuraDamagePlusPctFinal, 0)], types));
        }

        // Point Blank ramps from 30% more near to 30% less far.
        Assert.Equal(1.3, WardBreakEffects.PointBlank(0));
        Assert.Equal(1.3, WardBreakEffects.PointBlank(10));
        Assert.Equal(1.15, WardBreakEffects.PointBlank(22.5), 9);
        Assert.Equal(1, WardBreakEffects.PointBlank(35), 9);
        Assert.Equal(.85, WardBreakEffects.PointBlank(77.5), 9);
        Assert.Equal(.7, WardBreakEffects.PointBlank(120));
        Assert.Equal(.7, WardBreakEffects.PointBlank(150));
        // With Far Shot too, the two multiply: 1.3 × 0.8 at the muzzle, 0.7 × 1.6 at full range.
        Assert.Equal(1.04, WardBreakEffects.PointBlank(5) * WardBreakEffects.FarShot(5), 9);
        Assert.Equal(1.12, WardBreakEffects.PointBlank(150) * WardBreakEffects.FarShot(150), 9);

        // Impact, Close Combat and Shield Crush: "up to X% more Melee Damage, based on proximity", whole up to 15 units,
        // linear to none at 40 (PoB MeleeProximity), each its own multiplier.
        {
            static int Impact(uint name) => name == Stat.SlayerDamagePlusPctFinalFromDistance ? 15 : 0;
            Assert.Equal(1.15, Game.MeleeProximityMore(Impact, 0), 9);
            Assert.Equal(1.15, Game.MeleeProximityMore(Impact, 15), 9);
            Assert.Equal(1.075, Game.MeleeProximityMore(Impact, 27.5), 9);
            Assert.Equal(1, Game.MeleeProximityMore(Impact, 40));
            Assert.Equal(1, Game.MeleeProximityMore(Impact, 60));
            static int Both(uint name) => name switch
            {
                Stat.SlayerDamagePlusPctFinalFromDistance => 15,
                Stat.SupportSlashingDamagePlusPctFinalFromDistance => 39,
                _ => 0,
            };
            Assert.Equal(1.15 * 1.39, Game.MeleeProximityMore(Both, 10), 9);
            Assert.Equal(1.03 * 1.078, Game.MeleeProximityMore(Both, 35), 9);
        }

        // Ruthless: every third use of a supported skill is a Ruthless Blow with 98% more melee damage at level 20; each
        // skill keeps its own count, and a use without the support does not count.
        {
            static int Ruthless(uint name) => name switch
            {
                Stat.SupportRuthlessBigHitMaxCount => 3,
                Stat.SupportRuthlessBigHitDamagePlusPctFinal => 98,
                _ => 0,
            };
            static int Plain(uint _) => 0;
            var counts = new Dictionary<ushort, int>();
            double Use(ushort skill, Func<uint, int> stats) => Game.RuthlessBlowMore(counts, skill, stats);

            Assert.Equal(1, Use(1, Ruthless));
            Assert.Equal(1, Use(1, Ruthless));
            Assert.Equal(1, Use(2, Ruthless));
            Assert.Equal(1, Use(2, Plain));
            Assert.Equal(1.98, Use(1, Ruthless), 9);
            Assert.Equal(1, Use(1, Ruthless));
            Assert.Equal(1, Use(2, Ruthless));
            Assert.Equal(1.98, Use(2, Ruthless), 9);
            Assert.Equal(1, Use(3, Plain));
            Assert.False(counts.ContainsKey(3));
        }
        // Trap and mine throwing: 0.5 s and 0.3 s over the summed throwing speed and each support's more.
        Assert.Equal(.5, Game.PlacedThrowSeconds(Get(), mine: false), 9);
        Assert.Equal(.5 / 1.2, Game.PlacedThrowSeconds(Get((Stat.TrapThrowingSpeedPlusPct, 19), (Stat.TrapAndMineThrowingSpeedPlusPct, 1)), mine: false), 9);
        Assert.Equal(.5 / (1.2 * .9), Game.PlacedThrowSeconds(Get((Stat.TrapThrowingSpeedPlusPct, 20),
            (Stat.SupportTrapAndMineDamageTrapThrowingSpeedPlusPctFinal, -10)), mine: false), 9);
        Assert.Equal(.3 / (1.5 * .45), Game.PlacedThrowSeconds(Get((Stat.MineLayingSpeedPlusPct, 50),
            (Stat.SupportMinefieldMineThrowingSpeedPlusPctFinal, -55)), mine: true), 9);
        Assert.Equal(15, Game.TrapTriggerRadius(Get((Stat.TrapTriggerRadiusPlusPct, 50))), 9);
        Assert.Equal(.5 / 1.5, Game.PlacedThrowSeconds(Get((Stat.TrapThrowingSpeedPlusPct, 20),
            (Stat.TrapThrowingSpeedPlusPctPerFrenzyCharge, 10)), mine: false, frenzyCharges: 3), 9);
        // Swift Assembly's three chances are rolled separately and their extra throws add up.
        static uint Swift(int extra) => extra switch
        {
            1 => Stat.SupportAdditionalTrapMinePctChanceFor1AdditionalTrapMine,
            2 => Stat.SupportAdditionalTrapMinePctChanceFor2AdditionalTrapMine,
            _ => Stat.SupportAdditionalTrapMinePctChanceFor3AdditionalTrapMine,
        };
        Assert.Equal(0, Game.SwiftAssemblyExtra(Get(), new Random(1)));
        Assert.Equal(6, Game.SwiftAssemblyExtra(Get((Swift(1), 100), (Swift(2), 100), (Swift(3), 100)), new Random(1)));
        Assert.Equal(4, Game.SwiftAssemblyExtra(Get((Swift(1), 100), (Swift(2), 0), (Swift(3), 100)), new Random(1)));
        Assert.Equal(2, Game.SwiftAssemblyExtra(Get((Swift(2), 100)), new Random(1)));
        // Several traps or mines from one use scatter around the aim: within the support's circle, or a radius that
        // grows with their number.
        Assert.Equal((100, 100), Game.PlacedThrowPoint((100, 100), 1, 20, new Random(1)));
        Assert.Equal(new[] { 0d, 20, 10, 14 }, new[] { Game.PlacedScatterRadius(1, 0), Game.PlacedScatterRadius(3, 20),
            Game.PlacedScatterRadius(3, 0), Game.PlacedScatterRadius(5, 0) });
        Assert.All(Enumerable.Range(1, 40), i => Assert.True(
            Game.Dist(Game.PlacedThrowPoint((100, 100), 3, 0, new Random(i)), (100, 100)) <= 11));
        Assert.Contains(Enumerable.Range(1, 40), i => Game.PlacedThrowPoint((100, 100), 3, 0, new Random(i)) != (100, 100));
        // They cluster near the aim with a few outliers: most of 200 land within half the radius, some beyond it.
        var scatter = new Random(1);
        var landed = Enumerable.Range(0, 200).Select(_ => Game.Dist(Game.PlacedThrowPoint((100, 100), 3, 20, scatter), (100, 100))).ToArray();
        Assert.InRange(landed.Count(d => d <= 10.5), 95, 175);
        Assert.Contains(landed, d => d > 10.5);
        // A detonation sequence speeds up: 250 ms, then 10% more detonation speed for each mine already gone off.
        Assert.Equal(new[] { .25, .25 / 1.1, .25 / 1.5, .1 }, new[] { Game.MineSequenceGap(250, 0),
            Game.MineSequenceGap(0, 1), Game.MineSequenceGap(250, 5), Game.MineSequenceGap(200, 10) });
        Assert.Equal(0, Game.SwiftAssemblyExtra(Get(), new Random(1)));
        Assert.Equal(3, Game.SwiftAssemblyExtra(Get((Stat.SupportAdditionalTrapMinePctChanceFor3AdditionalTrapMine, 100)), new Random(1)));
        Assert.Equal(1, Game.SwiftAssemblyExtra(Get((Stat.SupportAdditionalTrapMinePctChanceFor1AdditionalTrapMine, 100)), new Random(1)));
        // A trap or mine fires at the nearest enemy that is not on it, else straight away from the player.
        Assert.Equal((110, 100), Game.PlacedProjectileAim((100, 100), (90, 100), [(100, 100), (130, 100), (110, 100), (300, 100)]));
        Assert.Equal((120, 100), Game.PlacedProjectileAim((100, 100), (90, 100), [(300, 100)]));
        Assert.Equal((100, 80), Game.PlacedProjectileAim((100, 100), (100, 130), []));
        Assert.Equal((120, 100), Game.PlacedProjectileAim((100, 100), (100, 100), []));
        // With a random source and no enemy it fires any way, 20 cells out.
        Assert.InRange(Game.Dist(Game.PlacedProjectileAim((100, 100), (90, 100), [(300, 100)], new Random(3)), (100, 100)), 19, 21);
        // The trap, mine and totem supports' own more/less damage multiplies the supported hit (level 1 values).
        foreach (var (stat, value) in new[] { (Stat.SupportTrapDamagePlusPctFinal, -20),
            (Stat.SupportTrapAndMineDamagePlusPctFinal, 30), (Stat.SupportClustertrapDamagePlusPctFinal, -61),
            (Stat.SupportMultithrowDamagePlusPctFinal, -58), (Stat.SupportGemMineDamagePlusPctFinal, -60),
            (Stat.SupportRemoteMine2DamagePlusPctFinal, -60), (Stat.SupportLocusMineDamagePlusPctFinal, -30),
            (Stat.SupportAdditionalTotemDamagePlusPctFinal, -40), (Stat.SupportFocusedBallistaTotemDamagePlusPctFinal, 9) })
        {
            var (general, typed) = GemSupportDamage.Multipliers(DamageTypeHistory.Fire, [(stat, value)], new HashSet<int> { 1 });
            Assert.Equal(1 + value / 100.0, general, 9);
            Assert.Equal(1, typed);
        }
    }

    // Elemental Focus and the never-ailment stats stop the ailments they name; Overcharge's Shock as though dealing 690%
    // more Damage and "Inflict non-Damaging Ailments as though dealing 100% more Damage" raise the damage the ailments
    // read but not the chance.
    [Fact]
    public void Prohibitions_and_as_though_stats_change_the_ailments_a_hit_inflicts()
    {
        // Each prohibition stops its own ailments.
        string[] all = ["ignite", "chill", "freeze", "shock"];
        Assert.All(all, a => Assert.True(MonsterElementalAilments.CannotInflict(a, Flags("cannot_inflict_status_ailments"))));
        Assert.All(all, a => Assert.True(MonsterElementalAilments.CannotInflict(a, Flags("never_ignite_chill_freeze_shock"))));
        Assert.All(all, a => Assert.False(MonsterElementalAilments.CannotInflict(a, Flags())));
        var neverCold = Flags("never_freeze_or_chill");
        Assert.Equal([false, true, true, false], all.Select(a => MonsterElementalAilments.CannotInflict(a, neverCold)));
        Assert.Equal([false, false, true, false], all.Select(a => MonsterElementalAilments.CannotInflict(a, Flags("never_freeze"))));
        Assert.Equal([false, false, false, true], all.Select(a => MonsterElementalAilments.CannotInflict(a, Flags("never_shock"))));
        Assert.Equal(MonsterIgnites.Settings.None,
            MonsterIgnites.Settings.From([], "main_hand", Flags("cannot_inflict_status_ailments", "base_chance_to_ignite_%")));

        // Elemental Focus stops a critical hit's chill, freeze and shock.
        {
            static List<MonsterElementalAilments.Effect> Hit(Func<uint, int> attacker) =>
                new MonsterElementalAilments().Inflict(DamagePacket.Of(DamageType.Cold, 500, 500).Add(DamageType.Lightning,
                    new DamageRange(500, 500)), critical: true, threshold: 1000, now: 0, attacker, _ => 0, new Random(1));
            Assert.Equal(3, Hit(Flags()).Count);
            Assert.Empty(Hit(Flags("cannot_inflict_status_ailments")));
            Assert.Equal([MonsterElementalAilments.Kind.Shock], Hit(Flags("never_freeze_or_chill")).Select(e => e.Kind));
        }

        // A lightning crit of 1% of the threshold shocks for 18% instead of 7%; a cold crit of 3% chills for 16% instead of
        // 12% and freezes for 0.36 s instead of being discarded under 0.3 s; the chance to shock is unchanged.
        {
            static int None(uint _) => 0;
            static List<MonsterElementalAilments.Effect> Hit(double cold, double lightning, bool critical, Func<uint, int> attacker) =>
                new MonsterElementalAilments().Inflict(DamagePacket.Of(DamageType.Cold, cold, cold).Add(DamageType.Lightning,
                    new DamageRange(lightning, lightning)), critical, 100, 0, attacker, None, new Random(1));
            Func<uint, int> overcharge = s => s == Stat.SupportPureShockShockAsThoughDamagePlusPctFinal ? 690 : 0;
            Func<uint, int> ailments = s => s == Stat.NonDamagingAilmentsAsThoughDamagePlusPctFinal ? 100 : 0;

            Assert.Equal(7, Assert.Single(Hit(0, 1, true, None)).Magnitude);
            Assert.Equal(18, Assert.Single(Hit(0, 1, true, overcharge)).Magnitude);
            Assert.Empty(Hit(0, 1, false, overcharge));
            Assert.Equal(12, Assert.Single(Hit(3, 0, true, None)).Magnitude);
            var cold = Hit(3, 0, true, ailments);
            Assert.Equal(16, cold.Single(e => e.Kind == MonsterElementalAilments.Kind.Chill).Magnitude);
            Assert.Equal(.36, cold.Single(e => e.Kind == MonsterElementalAilments.Kind.Freeze).Until, 9);
        }
    }
}
