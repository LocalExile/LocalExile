using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class SupportHitDamageTests
{
    static Dictionary<uint, int> With(params (string Name, int Value)[] stats) =>
        stats.ToDictionary(s => Program.Stats.Key(s.Name)!.Value, s => s.Value);

    static Func<string, int> Get(params (string Name, int Value)[] stats) =>
        name => stats.FirstOrDefault(s => s.Name == name).Value;

    static Func<string, int> Flags(params string[] flags) => name => flags.Contains(name) ? 1 : 0;

    // Hypothermia, Behead, Damage on Full Life, Awakened Increased Area of Effect, double damage, the six supports' more,
    // Point Blank, the melee proximity supports and Ruthless: each multiplies a hit only when its condition holds.
    [Fact]
    public void Supports_and_keystones_multiply_hits_when_their_conditions_hold()
    {
        // Each support condition multiplies only when it holds.
        {
            var skill = With(("support_hypothermia_damage_+%_vs_chilled_enemies_final", 29),
                ("support_executioner_damage_vs_enemies_on_low_life_+%_final", 49),
                ("support_damage_while_on_full_life_+%_final", 34));
            Assert.Equal(1, Game.ConditionalSupportMore(skill, attack: true, chilled: false, lowLife: false, fullLife: false));
            Assert.Equal(1.29, Game.ConditionalSupportMore(skill, attack: true, chilled: true, lowLife: false, fullLife: false), 9);
            Assert.Equal(1.49, Game.ConditionalSupportMore(skill, attack: false, chilled: false, lowLife: true, fullLife: false), 9);
            Assert.Equal(1.34, Game.ConditionalSupportMore(skill, attack: true, chilled: false, lowLife: false, fullLife: true), 9);
            Assert.Equal(1, Game.ConditionalSupportMore(skill, attack: false, chilled: false, lowLife: false, fullLife: true));
            Assert.Equal(1.29 * 1.49 * 1.34, Game.ConditionalSupportMore(skill, true, true, true, true), 9);
            // Awakened Increased Area of Effect: more damage for area skills only.
            var awakened = new[] { ("inc_aoe_plus_more_area_damage_+%_final", 24) };
            Assert.Equal(1.24, GemSupportDamage.Multipliers(DamageTypeHistory.Physical, awakened, new HashSet<int> { 0, 7 }).GeneralOrPhysical, 9);
            Assert.Equal(1, GemSupportDamage.Multipliers(DamageTypeHistory.Physical, awakened, new HashSet<int> { 0 }).GeneralOrPhysical);
        }

        // A hit on a low-life enemy gets Behead's more damage.
        {
            var conn = new ClientState(new(1, "behead", 300, 300, 100, 100, null, []), 1, 1)
            {
                Pools = new(1, 1000, 100, 2, 0), PlayerLife = 900,
            };
            var skill = With(("support_executioner_damage_vs_enemies_on_low_life_+%_final", 30),
                ("support_damage_while_on_full_life_+%_final", 20));
            var hit = DamagePacket.Of(DamageType.Physical, 100, 100);
            var low = new SpawnedMonster("low", new(100, 100, 0), 0, 0, 1000) { Life = 500 };
            var healthy = new SpawnedMonster("healthy", new(100, 100, 0), 0, 0, 1000) { Life = 501 };
            Assert.Equal(130, Game.WithConditionalSupportDamageLocked(conn, skill, true, low, hit, 0).MinTotal, 9);
            Assert.Equal(100, Game.WithConditionalSupportDamageLocked(conn, skill, true, healthy, hit, 0).MinTotal, 9);
            conn.PlayerLife = 1000;   // full Life: Damage on Full Life joins in
            Assert.Equal(120, Game.WithConditionalSupportDamageLocked(conn, skill, true, healthy, hit, 0).MinTotal, 9);
        }

        // Double damage: the player's chance on every hit, a spell's chance on spells, the skill's own (Heavy Strike);
        // triple damage overrides it, so double only rolls when triple misses.
        {
            var random = new Random(1);
            var hit = DamagePacket.Of(DamageType.Physical, 100, 100);
            double Dealt(GamblerModifiers gambler) => gambler.RollOutgoingHit(hit, random).MinTotal;

            Assert.Equal(100, Dealt(GamblerModifiers.From(Get())));
            Assert.Equal(200, Dealt(GamblerModifiers.From(Get(("chance_to_deal_double_damage_%", 100)))));
            Assert.Equal(300, Dealt(GamblerModifiers.From(
                Get(("chance_to_deal_double_damage_%", 100), ("base_chance_to_deal_triple_damage_%", 100)))));
            var spells = GamblerModifiers.From(Get(("spell_chance_to_deal_double_damage_%", 100)));
            Assert.Equal(100, Dealt(spells));
            Assert.Equal(100, Dealt(spells.ForSkill(Get(), spell: false)));
            Assert.Equal(200, Dealt(spells.ForSkill(Get(), spell: true)));
            Assert.Equal(200, Dealt(GamblerModifiers.From(Get()).ForSkill(Get(("chance_to_deal_double_damage_%", 100)), spell: false)));

            // Half triple and half double: a quarter of the hits are doubled, not half.
            var both = GamblerModifiers.From(Get(("chance_to_deal_double_damage_%", 50), ("base_chance_to_deal_triple_damage_%", 50)));
            var counts = Enumerable.Range(0, 20000).Select(_ => Dealt(both)).GroupBy(d => d).ToDictionary(g => g.Key, g => g.Count());
            Assert.InRange(counts[300], 9600, 10400);
            Assert.InRange(counts[200], 4700, 5300);
            Assert.InRange(counts[100], 4700, 5300);
        }

        // Overcharge, Bonechill, Deadly Ailments, Ancestral Call, Infused Channelling and Bloodthirst: each support's more
        // multiplies a hit once, and Bonechill's counts only for a hit with cold damage, in the elemental group.
        {
            (string, int)[] stats = [("support_pure_shock_damage_+%_final", -25), ("support_bonechill_cold_damage_+%_final", 22),
                ("support_better_ailments_hit_damage_+%_final", -80), ("support_spirit_strike_damage_+%_final", 20),
                ("support_storm_barrier_damage_+%_final", 37), ("support_blood_thirst_damage_+%_final", 17)];
            HashSet<int> spell = [1];
            var (general, typed) = GemSupportDamage.Multipliers(DamageTypeHistory.Cold, stats, spell);
            Assert.Equal(.75 * .2 * 1.2 * 1.37 * 1.17, general, 9);
            Assert.Equal(1.22, typed, 9);
            Assert.Equal(1, GemSupportDamage.Multipliers(DamageTypeHistory.Physical, stats, spell).ElementalOrChaos);
        }

        // Point Blank ramps from 30% more near to 30% less far.
        Assert.Equal(1.3, AntiquarianEffects.PointBlank(0));
        Assert.Equal(1.3, AntiquarianEffects.PointBlank(10));
        Assert.Equal(1.15, AntiquarianEffects.PointBlank(22.5), 9);
        Assert.Equal(1, AntiquarianEffects.PointBlank(35), 9);
        Assert.Equal(.85, AntiquarianEffects.PointBlank(77.5), 9);
        Assert.Equal(.7, AntiquarianEffects.PointBlank(120));
        Assert.Equal(.7, AntiquarianEffects.PointBlank(150));
        // With Far Shot too, the two multiply: 1.3 × 0.8 at the muzzle, 0.7 × 1.6 at full range.
        Assert.Equal(1.04, AntiquarianEffects.PointBlank(5) * AntiquarianEffects.FarShot(5), 9);
        Assert.Equal(1.12, AntiquarianEffects.PointBlank(150) * AntiquarianEffects.FarShot(150), 9);

        // Impact, Close Combat and Shield Crush: "up to X% more Melee Damage, based on proximity", whole up to 15 units,
        // linear to none at 40 (PoB MeleeProximity), each its own multiplier.
        {
            static int Impact(string name) => name == "slayer_damage_+%_final_from_distance" ? 15 : 0;
            Assert.Equal(1.15, Game.MeleeProximityMore(Impact, 0), 9);
            Assert.Equal(1.15, Game.MeleeProximityMore(Impact, 15), 9);
            Assert.Equal(1.075, Game.MeleeProximityMore(Impact, 27.5), 9);
            Assert.Equal(1, Game.MeleeProximityMore(Impact, 40));
            Assert.Equal(1, Game.MeleeProximityMore(Impact, 60));
            static int Both(string name) => name switch
            {
                "slayer_damage_+%_final_from_distance" => 15,
                "support_slashing_damage_+%_final_from_distance" => 39,
                _ => 0,
            };
            Assert.Equal(1.15 * 1.39, Game.MeleeProximityMore(Both, 10), 9);
            Assert.Equal(1.03 * 1.078, Game.MeleeProximityMore(Both, 35), 9);
        }

        // Ruthless: every third use of a supported skill is a Ruthless Blow with 98% more melee damage at level 20; each
        // skill keeps its own count, and a use without the support does not count.
        {
            static int Ruthless(string name) => name switch
            {
                "support_ruthless_big_hit_max_count" => 3,
                "support_ruthless_big_hit_damage_+%_final" => 98,
                _ => 0,
            };
            static int Plain(string _) => 0;
            var counts = new Dictionary<ushort, int>();
            double Use(ushort skill, Func<string, int> stats) => Game.RuthlessBlowMore(counts, skill, stats);

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
            static List<MonsterElementalAilments.Effect> Hit(Func<string, int> attacker) =>
                new MonsterElementalAilments().Inflict(500, 500, critical: true, threshold: 1000, now: 0, attacker, _ => 0, new Random(1));
            Assert.Equal(3, Hit(Flags()).Count);
            Assert.Empty(Hit(Flags("cannot_inflict_status_ailments")));
            Assert.Equal([MonsterElementalAilments.Kind.Shock], Hit(Flags("never_freeze_or_chill")).Select(e => e.Kind));
        }

        // A lightning crit of 1% of the threshold shocks for 18% instead of 7%; a cold crit of 3% chills for 16% instead of
        // 12% and freezes for 0.36 s instead of being discarded under 0.3 s; the chance to shock is unchanged.
        {
            static int None(string _) => 0;
            static List<MonsterElementalAilments.Effect> Hit(double cold, double lightning, bool critical, Func<string, int> attacker) =>
                new MonsterElementalAilments().Inflict(cold, lightning, critical, 100, 0, attacker, None, new Random(1));
            Func<string, int> overcharge = s => s == "support_pure_shock_shock_as_though_damage_+%_final" ? 690 : 0;
            Func<string, int> ailments = s => s == "non_damaging_ailments_as_though_damage_+%_final" ? 100 : 0;

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
