using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class HitDebuffTests
{
    [Fact]
    public async Task Existing_bleed_gates_blind_and_life_gain()
    {
        await Existing_bleed_enables_the_correct_attack_and_global_blind_sources();
        await A_paid_hit_recovers_life_only_if_its_target_was_already_bleeding();
    }

    static async Task Existing_bleed_enables_the_correct_attack_and_global_blind_sources()
    {
        const string attack = "AnimalCharmBlindOnHitVsBleedingEnemies1";
        Assert.False((await ConditionalHit(null, 0, preBled: true)).Blind);
        Assert.True((await ConditionalHit(attack, 15, preBled: true)).Blind);
        Assert.False((await ConditionalHit(attack, 15, skill: "Ice Nova", preBled: true)).Blind);
        var freshBleed = await ConditionalHit(attack, 15, inflictBleed: true);
        Assert.True(freshBleed.Bleeding);
        Assert.False(freshBleed.Blind);
        const string notable = "AfflictionNotableDisorientingWounds";
        Assert.False((await ConditionalHit(notable, 1, skill: "Ice Nova", cluster: true)).Blind);
        Assert.True((await ConditionalHit(notable, 1, skill: "Ice Nova", preBled: true, cluster: true)).Blind);
    }

    static async Task<(bool Blind, bool Bleeding, uint Life)> ConditionalHit(string? modId, int value,
        string skill = "Heavy Strike", bool preBled = false, bool inflictBleed = false,
        bool cluster = false, bool cannotRecover = false)
    {
        var clock = new ContactClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.ProjectileRuntimeState = fixture.Session;
        player.Walk.Teleport(100, 100, clock.At);
        var areaRoll = new QualifyingRoll();
        var randomProperty = typeof(AreaFrames).GetProperty(nameof(AreaFrames.Rng))!;
        var items = Game.ItemsOf(player);
        uint main = Assert.Single(items.Items(PlayerInventories.Weapon1)).Id;
        items.Change(PlayerInventories.Weapon1, main, _ => ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
        ushort gem = Assert.Single(fixture.Equip((skill, 18)));
        if (modId is not null)
        {
            var jewel = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create(cluster ? "Megalomaniac" : "That Which Was Taken", new Random(17)));
            var mod = Program.Items.Mods.ById(modId)!;
            var native = new ItemMod(ModSection.Explicit, mod.Hash, [value]);
            var equipped = jewel with { Mods = [native] };
            byte slot = cluster ? (byte)20 : (byte)1;
            player.Character.Passives.Add(Game.PassivesFor(player).JewelNodeForSlot(slot)!.Value);
            player.Character.Passives.Add(31703);
            items.Add(PlayerInventories.PassiveJewels, slot, 0, equipped);
            if (cluster)
            {
                var layout = ClusterJewelLayout.For(slot, equipped);
                Assert.NotNull(layout);
                var chosen = layout.NotableOrders.Single(row => row.Value == 0);
                player.Character.ClusterPassives.Add((ushort)(layout.Base + chosen.Key));
                Assert.True(Game.CharacterPlayerStats(player).GetValueOrDefault(Stat.GlobalChanceToBlindOnHitPctVsBleedingEnemies) > 0);
            }
            else
            {
                uint row = Assert.Single(mod.Stats).Id;
                Assert.Equal(value, Game.CharacterPlayerStats(player).GetValueOrDefault(row));
            }
        }
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (!ReferenceEquals(owner, player)) return values;
            values[Stat.GlobalAlwaysHit] = values[Stat.GlobalCannotCrit] = 1;
            if (inflictBleed) values[Stat.BleedOnHitWithAttacksPct] = 100;
            if (cannotRecover) values[Stat.CannotRecoverLife] = 1;
            return values;
        });
        const uint id = 900;
        var target = new SpawnedMonster("conditional hit witness", new(104, 100, 0), 0, 0, 10000)
            { ObjectId = id, Hostile = true };
        player.Area.Monsters[id] = target;
        player.Area.Grid.Set(new(id), (104, 100));
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            Game.RecomputePoolsAndSpeed(player);
            Game.ConfigureLeech(player, Game.CharacterPlayerStats(player).GetValueOrDefault, onLowLife: true);
            randomProperty.SetValue(player.Frames, areaRoll);
            using var rolls = GameEvents.OutgoingDamage.Add("Game", (owner, _, enemy, packet) =>
            {
                if (ReferenceEquals(owner, player) && ReferenceEquals(enemy, target))
                    randomProperty.SetValue(player.Frames, areaRoll);
                return packet;
            });
            if (preBled) Assert.True(target.Bleeds.Add(clock.At, 1, 10));
            uint life = player.PlayerLife = player.PlayerMaxLife / 2;
            uint mana = player.PlayerMana;
            Assert.True(await Game.CastGemSkill(player, gem, (100, 100), (104, 100), targetId: id));
            Assert.True(player.PlayerMana < mana);
            Assert.InRange(target.Life, 1u, target.MaxLife - 1);
            Assert.Same(areaRoll, Game.AreaRng(player));
            return (Game.Blinded(player, id, clock.At), target.Bleeds.Until > clock.At,
                player.PlayerLife - life);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    sealed class QualifyingRoll : Random
    {
        public override int Next(int maxValue) => 0;
        public override int Next(int minValue, int maxValue) => minValue;
        public override double NextDouble() => 0;
    }

    sealed class ContactClock : IPlayerUpdateSchedule
    {
        public double At = 1;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }

    static async Task A_paid_hit_recovers_life_only_if_its_target_was_already_bleeding()
    {
        const string life = "AnimalCharmLifeOnHitVsBleedingEnemies1";
        Assert.Equal(0u, (await ConditionalHit(null, 0, preBled: true)).Life);
        Assert.Equal(6u, (await ConditionalHit(life, 6, preBled: true)).Life);
        Assert.Equal(6u, (await ConditionalHit(life, 6, skill: "Ice Nova", preBled: true)).Life);
        var fresh = await ConditionalHit(life, 6, inflictBleed: true);
        Assert.True(fresh.Bleeding);
        Assert.Equal(0u, fresh.Life);
        Assert.Equal(0u, (await ConditionalHit(life, 6, preBled: true, cannotRecover: true)).Life);
    }

    static Func<uint, int> Get(params (uint Name, int Value)[] stats) =>
        name => stats.FirstOrDefault(s => s.Name == name).Value;

    static Func<uint, int> Chances(params string[] stats) => name => stats.Contains(Program.Stats.Name(name)) ? 100 : 0;

    // Intimidate, Unnerve, Blind and Withered from the player's hits: each rolls its own chances and lasts its own time
    // from the latest hit, and Intimidate, Unnerve and Withered raise the damage the monster takes.
    [Fact]
    public void Hits_intimidate_unnerve_blind_and_wither_by_their_chances()
    {
        // Bite's conditional stat is an additive increased-damage bucket, never an automatic neutral-resolver bonus and
        // never a final-packet more multiplier. These are explicit arithmetic controls independent of the resolver.
        {
            const string conditional = "hit_and_ailment_damage_+%_vs_blinded_enemies";
            var raw = DamagePacket.Of(DamageType.Physical, 100, 100);
            var conditionalStats = Get((Stat.DamagePlusPct, 100), (Stat.Of(conditional), 50));
            var neutralAttack = PlayerHitDamage.Resolve(raw, conditionalStats, new DamageConversion(),
                spell: false);
            var explicitAttack = PlayerHitDamage.Resolve(raw, conditionalStats, new DamageConversion(),
                spell: false, additionalIncreased: 50);
            var neutralSpell = PlayerHitDamage.Resolve(raw, conditionalStats, new DamageConversion(),
                spell: true);
            var explicitSpell = PlayerHitDamage.Resolve(raw, conditionalStats, new DamageConversion(),
                spell: true, additionalIncreased: 50);
            Assert.Equal(200, neutralAttack[DamageType.Physical].Min);
            Assert.Equal(200, neutralAttack[DamageType.Physical].Max);
            Assert.Equal(250, explicitAttack[DamageType.Physical].Min);
            Assert.Equal(250, explicitAttack[DamageType.Physical].Max);
            Assert.Equal(200, neutralSpell[DamageType.Physical].Min);
            Assert.Equal(200, neutralSpell[DamageType.Physical].Max);
            Assert.Equal(250, explicitSpell[DamageType.Physical].Min);
            Assert.Equal(250, explicitSpell[DamageType.Physical].Max);

            // Physical ancestry reaches only the converted physical portion. Raw fire receives generic damage but not
            // the physical increase, while explicit Bite increases both portions once: 500 baseline and 600, not 750.
            var mixedRaw = DamagePacket.Of(DamageType.Physical, 100, 100)
                .Add(DamagePacket.Of(DamageType.Fire, 100, 100));
            var conversion = new DamageConversion().Add(DamageType.Physical, DamageType.Fire, 100);
            var mixedStats = Get((Stat.DamagePlusPct, 100), (Stat.PhysicalDamagePlusPct, 100), (Stat.Of(conditional), 50));
            var mixedPaths = conversion.Trace(mixedRaw);
            Assert.Collection(mixedPaths,
                path =>
                {
                    Assert.Equal(DamageType.Fire, path.Type);
                    Assert.Equal(DamageTypeHistory.Fire, path.History);
                    Assert.Equal(100, path.Range.Min);
                    Assert.Equal(100, path.Range.Max);
                },
                path =>
                {
                    Assert.Equal(DamageType.Fire, path.Type);
                    Assert.Equal(DamageTypeHistory.Physical | DamageTypeHistory.Fire, path.History);
                    Assert.Equal(100, path.Range.Min);
                    Assert.Equal(100, path.Range.Max);
                });
            var mixedHit = PlayerHitDamage.Resolve(mixedRaw, mixedStats, conversion, spell: false);
            var mixedExtraHit = PlayerHitDamage.Resolve(mixedRaw, mixedStats, conversion, spell: false,
                additionalIncreased: 50);
            Assert.Equal(500, mixedHit.MinTotal);
            Assert.Equal(500, mixedHit.MaxTotal);
            Assert.Equal(600, mixedExtraHit.MinTotal);
            Assert.Equal(600, mixedExtraHit.MaxTotal);

            // Hit-only attack, spell, area, and projectile increases do not leak into Ignite. The 500 fire baseline
            // burns at 450 DPS; the explicit additive 50 gives 540 DPS (27,000 and 32,400 per minute).
            var ailmentStats = Get((Stat.DamagePlusPct, 100), (Stat.PhysicalDamagePlusPct, 100),
                (Stat.AttackDamagePlusPct, 1_000), (Stat.SpellDamagePlusPct, 1_000),
                (Stat.AreaDamagePlusPct, 1_000), (Stat.ProjectileDamagePlusPct, 1_000), (Stat.Of(conditional), 50));
            uint igniteRow = Stat.Of(
                "minimum_ignite_damage_to_deal_per_minute_from_non_critical_spell_damage");
            var ignite = AilmentPanelStats.Compute(new("spell", Spell: true), mixedRaw, conversion, ailmentStats);
            var effectiveIgnite = AilmentPanelStats.Compute(new("spell", Spell: true), mixedRaw, conversion,
                ailmentStats, additionalIncreased: 50);
            Assert.Equal(27_000, ignite.Single(row => row.Stat == igniteRow).Value);
            Assert.Equal(32_400, effectiveIgnite.Single(row => row.Stat == igniteRow).Value);
        }

        // Intimidate and Unnerve: the attack-only and spell-only chances, Grattus Signet's crits, 4 s from the latest hit,
        // and 10% increased damage taken from attacks (Intimidate) or spells (Unnerve).
        {
            var random = new Random(1);
            Assert.Equal((true, false), Game.HitDebuffRolls(Chances("attacks_intimidate_on_hit_%"), spell: false, critical: false, random));
            Assert.Equal((false, false), Game.HitDebuffRolls(Chances("attacks_intimidate_on_hit_%"), spell: true, critical: false, random));
            Assert.Equal((false, true), Game.HitDebuffRolls(Chances("chance_to_unnerve_on_hit_with_spells_%"), true, false, random));
            Assert.Equal((false, false), Game.HitDebuffRolls(Chances("chance_to_unnerve_on_hit_with_spells_%"), false, false, random));
            Assert.Equal((false, true), Game.HitDebuffRolls(Chances("attacks_inflict_unnerve_on_crit"), false, true, random));
            Assert.Equal((false, false), Game.HitDebuffRolls(Chances("attacks_inflict_unnerve_on_crit"), false, false, random));
            Assert.Equal((true, false), Game.HitDebuffRolls(Chances("spells_inflict_intimidate_on_crit"), true, true, random));
            Assert.Equal((true, true), Game.HitDebuffRolls(Chances("chance_to_intimidate_on_hit_%", "chance_to_unnerve_on_hit_%"), true, false, random));

            var m = new SpawnedMonster("intimidated", new(105, 100, 0), 0, 0, 100);
            Game.HitDebuffsOnHitLocked(m, Chances("chance_to_intimidate_on_hit_%"), spell: false, critical: false, now: 10, random: new Random(1));
            Game.HitDebuffsOnHitLocked(m, Chances("chance_to_intimidate_on_hit_%"), spell: false, critical: false, now: 12, random: new Random(1));
            Assert.Equal(16, m.IntimidatedUntil);
            Assert.Equal(10, Game.HitDebuffTaken(m, spell: false, 15.9));
            Assert.Equal(0, Game.HitDebuffTaken(m, spell: true, 15.9));
            Assert.Equal(0, Game.HitDebuffTaken(m, spell: false, 16));
        }

        // Blind: the general chance, the attack-only one and the critical one sum only after the accepted hit selects
        // attack/spell and critical. The owned timed state keeps each source and lets the strongest effect win.
        {
            var input = BlindHitInput.Capture(Get((Stat.GlobalChanceToBlindOnHitPct, 40),
                (Stat.AttacksChanceToBlindOnHitPct, 30), (Stat.PctChanceToBlindOnCriticalStrike, 30),
                (Stat.BlindDurationPlusPct, 50)));
            Assert.Equal(70, input.Chance(spell: false, critical: false));
            Assert.Equal(40, input.Chance(spell: true, critical: false));
            Assert.Equal(70, input.Chance(spell: true, critical: true));
            Assert.Equal(100, input.Chance(spell: false, critical: true));
            Assert.Equal(6, input.Seconds, 9);
            Assert.Equal(0, input.Effect);
            var wide = BlindHitInput.Capture(name => name switch
            {
                Stat.GlobalChanceToBlindOnHitPct => 2_000_000_000L,
                Stat.AttacksChanceToBlindOnHitPct => 2_000_000_000L,
                Stat.PctChanceToBlindOnCriticalStrike => 2_000_000_000L,
                _ => 0L,
            });
            Assert.Equal(100, wide.Chance(spell: false, critical: true));

            var blind = new TimedBlind();
            Assert.True(blind.Inflict(10, input.Seconds, input.Effect, immune: false));
            Assert.Equal((20, 16d), blind.Read(11));
            Assert.True(blind.Inflict(11, 1, effectPercent: 50, immune: false));
            Assert.Equal((30, 12d), blind.Read(11.5));
            Assert.Equal((20, 16d), blind.Read(12));
            Assert.False(blind.Inflict(12, 4, effectPercent: 0, immune: true));
            Assert.Equal((0, 0d), blind.Read(16));
        }

        // Withered: 2 s or Withering Touch's duration, expiring slower by wither_expire_speed_+%; 6% increased chaos damage
        // taken per stack (with withered_effect_+%), up to 15 stacks with their own ends, on chaos damage only.
        {
            var random = new Random(1);
            var wither = Get((Stat.WitheredOnHitFor2SecondsPctChance, 100));
            Assert.Equal(0, Game.WitherRoll(Get(), random));
            Assert.Equal(2, Game.WitherRoll(wither, random), 9);
            Assert.Equal(2.5, Game.WitherRoll(Get((Stat.WitheredOnHitChancePct, 100), (Stat.SupportWitheredBaseDurationMs, 2000),
                (Stat.WitherExpireSpeedPlusPct, -20)), random), 9);

            var m = new SpawnedMonster("withered", new(105, 100, 0), 0, 0, 1000);
            for (int i = 0; i < 16; i++) Game.WitherOnHitLocked(m, wither, now: 10 + i * 0.1, random);
            Assert.Equal(15, m.Withered.Count);
            Assert.Equal(12.1, m.Withered.Min(s => s.Until), 9);
            Assert.Equal(90, Game.WitheredTaken(m, 11.6), 9);
            Assert.Equal(18, Game.WitheredTaken(m, 13.25), 9);
            Assert.Equal(190, Game.MonsterTakesMitigated(m, DamagePacket.Of(DamageType.Chaos, 100, 100), spell: false, now: 11.6));
            Assert.Equal(100, Game.MonsterTakesMitigated(m, DamagePacket.Of(DamageType.Fire, 100, 100), spell: false, now: 11.6));

            var stronger = new SpawnedMonster("withered more", new(105, 100, 0), 0, 0, 1000);
            Game.WitherOnHitLocked(stronger, Get((Stat.WitheredOnHitFor2SecondsPctChance, 100), (Stat.WitheredEffectPlusPct, 50)), 10, random);
            Assert.Equal(9, Game.WitheredTaken(stronger, 10.5), 9);
        }
    }

    // Exposure on hit: -10% by default, a magnitude stat's own value, the extra and minimum stats, 4 s by element and
    // magnitude, the highest running one lowering the monster's resistance to hits and burns. Elemental Equilibrium: an
    // elemental hit removes Exposure of its own elements and inflicts -25% of the others, scaled by
    // elemental_equilibrium_effect_+%; a hit without elemental damage does neither.
    [Fact]
    public void Hits_expose_by_element_and_elemental_equilibrium_exposes_the_other_elements()
    {
        {
            var random = new Random(1);
            Assert.Equal(-10, Game.ExposureRoll(Get((Stat.BaseInflictFireExposureOnHitPctChance, 100)), DamageType.Fire, false, random));
            Assert.Equal(0, Game.ExposureRoll(Get((Stat.BaseInflictFireExposureOnHitPctChance, 100)), DamageType.Cold, false, random));
            var withCold = Get((Stat.ChanceToInflictColdExposureOnHitWithColdDamagePct, 100));
            Assert.Equal(0, Game.ExposureRoll(withCold, DamageType.Cold, withType: false, random));
            Assert.Equal(-10, Game.ExposureRoll(withCold, DamageType.Cold, withType: true, random));
            Assert.Equal(-14, Game.ExposureRoll(Get((Stat.LightningExposureOnHitMagnitude, -14)), DamageType.Lightning, false, random));
            Assert.Equal(-22, Game.ExposureRoll(Get((Stat.InflictAllExposureOnHit, 1),
                (Stat.ColdExposureYouInflictAppliesExtraColdResistancePlusPct, -12)), DamageType.Cold, false, random));
            Assert.Equal(-25, Game.ExposureRoll(Get((Stat.InflictAllExposureOnHit, 1),
                (Stat.ExposureYouInflictHasMinimumResistancePct, -25)), DamageType.Fire, false, random));
            Assert.Equal(0, Game.ExposureRoll(Get((Stat.InflictAllExposureOnHit, 1), (Stat.CannotInflictExposure, 1)),
                DamageType.Fire, false, random));

            var m = new SpawnedMonster("exposed", new(105, 100, 0), 0, 0, 100);
            var fire = DamagePacket.Of(DamageType.Fire, 10, 10);
            Game.ExposureOnHitLocked(m, Get((Stat.BaseInflictFireExposureOnHitPctChance, 100)), fire, now: 10, random);
            Game.ExposureOnHitLocked(m, Get((Stat.FireExposureOnHitMagnitude, -14)), fire, now: 8, random);
            int unexposed = Game.MonsterHitResistance(m, DamageType.Fire, default).Value;
            Assert.Equal(unexposed - 14, Game.MonsterHitResistance(m, DamageType.Fire, default, now: 11).Value);
            Assert.Equal(unexposed - 10, Game.MonsterHitResistance(m, DamageType.Fire, default, now: 12.5).Value);
            Assert.Equal(unexposed, Game.MonsterHitResistance(m, DamageType.Fire, default, now: 14).Value);
            Assert.Equal(unexposed, Game.MonsterHitResistance(m, DamageType.Cold, default, now: 11).Value);
            Assert.Equal(14, m.ExposureUntil[0]);
        }

        {
            var random = new Random(1);
            var m = new SpawnedMonster("balanced", new(105, 100, 0), 0, 0, 100);
            var keystone = Get((Stat.KeystoneElementalEquilibrium, 1));
            var physical = DamagePacket.Of(DamageType.Physical, 10, 10);
            Game.ExposureOnHitLocked(m, Get((Stat.BaseInflictFireExposureOnHitPctChance, 100)), physical, now: 10, random);
            Game.ExposureOnHitLocked(m, keystone, physical, now: 10.5, random);
            Assert.Equal(-10, Game.MonsterExposure(m, DamageType.Fire, 11));
            Assert.Equal(0, Game.MonsterExposure(m, DamageType.Cold, 11));

            Game.ExposureOnHitLocked(m, keystone, DamagePacket.Of(DamageType.Cold, 10, 10), now: 11, random);
            Assert.Equal(-25, Game.MonsterExposure(m, DamageType.Fire, 12));
            Assert.Equal(-25, Game.MonsterExposure(m, DamageType.Lightning, 12));
            Assert.Equal(0, Game.MonsterExposure(m, DamageType.Cold, 12));

            Game.ExposureOnHitLocked(m, Get((Stat.KeystoneElementalEquilibrium, 1), (Stat.ElementalEquilibriumEffectPlusPct, 20)),
                DamagePacket.Of(DamageType.Fire, 10, 10), now: 12, random);
            Assert.Equal(0, Game.MonsterExposure(m, DamageType.Fire, 12.5));
            Assert.Equal(double.NegativeInfinity, m.ExposureUntil[0]);
            Assert.Equal(-30, Game.MonsterExposure(m, DamageType.Cold, 12.5));
            Assert.Equal(-30, Game.MonsterExposure(m, DamageType.Lightning, 12.5));
        }
    }
}
