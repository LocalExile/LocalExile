using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class ChargeRulesTests
{
    [Fact]
    public async Task CoreChargesGainRefreshExpireAndTriggerAtTheirLimits()
    {
        // Tulfall (+1 maximum, a Frenzy Charge and then the loss of every Power Charge on reaching the maximum), Voll's
        // Devotion (an Endurance Charge per Power Charge lost), Kingsguard (Life per Endurance Charge lost) and a 100%
        // Power Charge on kill; Frenzy Charges cannot be gained.
        var stats = new Dictionary<uint, int>
        {
            [Stat.MaxPowerCharges] = 1, [Stat.AddPowerChargeOnKillPctChance] = 100,
            [Stat.GainFrenzyChargeOnReachingMaximumPowerCharges] = 1,
            [Stat.LoseAllPowerChargesOnReachingMaximumPowerCharges] = 1,
            [Stat.GainEnduranceChargeOnPowerChargeExpiry] = 1, [Stat.GainXLifeWhenEnduranceChargeExpiresOrConsumed] = 50,
            [Stat.CannotGainFrenzyCharges] = 1,
        };
        int Get(uint name) => stats.GetValueOrDefault(name);
        var session = new ChargeSession();
        session.Observe(Get);
        var rng = new Random(1);
        void Kill(double at) => session.Admit(at, [new ChargeEvent.Kill(MonsterRarity.Normal)], Get, () => default, alive: true, rng);

        // Each kill gains one and starts the duration of all of them again: 10 s from the last gain.
        Kill(0); Kill(5); Kill(8);
        Assert.Equal(3, session.Effective(ChargeType.Power));
        Assert.Equal(18, session.State.ExpiresAt(ChargeType.Power), 6);
        // Reaching the maximum of 4: no Frenzy Charge (cannot gain), every Power Charge lost, one Endurance Charge each.
        Kill(9);
        Assert.Equal(0, session.Effective(ChargeType.Power));
        Assert.Equal(0, session.Effective(ChargeType.Frenzy));
        Assert.Equal(3, session.Effective(ChargeType.Endurance));   // the maximum of 3 caps the four lost
        Assert.Equal(9, session.LastLost(ChargeType.Power));
        // The Endurance Charges expire all at once 10 s later; Kingsguard's Life is 50 per charge lost.
        session.Step(19.5, Get, () => default, alive: true, moving: false, inTown: false, rng);
        Assert.Equal(0, session.Effective(ChargeType.Endurance));
        Assert.Equal(150, session.TakeLifeGained());
        // A lower maximum (Eternal Damnation) removes what is held above it; death is not a loss.
        stats.Remove(Stat.CannotGainFrenzyCharges);
        session.Observe(Get);
        session.Admit(20, [new ChargeEvent.Grant(ChargeType.Frenzy, 2, "test")], Get, () => default, alive: true, rng);
        Assert.Equal(2, session.Effective(ChargeType.Frenzy));
        stats[Stat.MaximumEnduranceFrenzyPowerChargesIs0] = 1;
        session.Observe(Get);
        Assert.Equal(0, ChargeMaxima.Of(ChargeType.Power, Get));
        session.Step(20.1, Get, () => default, alive: true, moving: false, inTown: false, rng);
        Assert.Equal(0, session.Effective(ChargeType.Frenzy));
        Assert.Equal(20.1, session.LastLost(ChargeType.Frenzy), 6);
        await SublimeVisionAuraRaisesItsChargeLimit();
    }

    static async Task SublimeVisionAuraRaisesItsChargeLimit()
    {
        var catalog = new UniqueCatalog(Program.Items);
        var definition = catalog.Find("Sublime Vision");
        Assert.NotNull(definition);
        var results = new List<((int Maximum, int Held, int AfterToggle) Want, (int Maximum, int Held, int AfterToggle) Got)>();
        foreach (var (auraName, type, stat) in new[]
        {
            ("Determination", ChargeType.Endurance, Stat.MaximumEnduranceChargesPlusWhileAffectedByDetermination),
            ("Discipline", ChargeType.Power, Stat.MaximumPowerChargesPlusWhileAffectedByDiscipline),
            ("Grace", ChargeType.Frenzy, Stat.MaximumFrenzyChargesPlusWhileAffectedByGrace),
        })
        {
            using var fixture = new TriggeredSummonTests.Fixture();
            var player = fixture.Player;
            var mod = Program.Items.Mods.ById($"SublimeVision{auraName}");
            Assert.NotNull(mod);
            var jewel = Assert.IsType<Item>(Enumerable.Range(1, 512).Select(seed => catalog.Create(definition, new Random(seed)))
                .FirstOrDefault(candidate => candidate?.Mods.Any(roll => roll.Hash == mod.Hash) == true));
            ushort skill = Assert.Single(fixture.Equip((auraName, 1)));
            var aura = Assert.Single(Game.GemSkillEffects(player));
            var charges = Game.ChargesOf(player);
            var random = new Random(17);
            var helmet = Assert.IsType<Item>(catalog.Create("Ancient Skull", new Random(17)));
            Game.ItemsOf(player).Add(PlayerInventories.Helmet, 0, 0, helmet);
            var minion = new GolemState.Golem(0xB0A1, null!, Game.GolemData.Find("SummonFlameGolem")!, 1);
            int Crit(IReadOnlyDictionary<uint, int> stats) => Game.GolemModifiers(player, minion, stats)
                .Effects[Stat.CriticalStrikeChancePlusPct];
            async Task Toggle() => Assert.True(await Game.ToggleAura(player, skill, aura.Effect, aura.Name,
                Game.SupportedReservationCost(player, skill)!.Value, aura.Level, aura.Quality));
            if (type == ChargeType.Power)
            {
                await Toggle();
                var noJewel = Game.CharacterPlayerStats(player);
                Assert.Equal(50, noJewel.GetValueOrDefault(Stat.MinionCriticalStrikeChancePlusPctPerMaximumPowerCharge));
                Assert.Equal(3, ChargeMaxima.Of(ChargeType.Power, noJewel.GetValueOrDefault));
                Assert.Equal(0, charges.State.Held(ChargeType.Power));
                Assert.Equal(150, Crit(noJewel));
                await Toggle();
            }

            const byte slot = 1;
            player.Character.Passives.Add(Game.PassivesFor(player).JewelNodeForSlot(slot)!.Value);
            Assert.Null(Game.PassiveJewelRefusal(player, slot, 0, jewel));
            Game.ItemsOf(player).Add(PlayerInventories.PassiveJewels, slot, 0, jewel);
            Assert.Equal(0, Game.CharacterPlayerStats(player).GetValueOrDefault(stat));
            Assert.Equal(3, ChargeMaxima.Of(type, Game.CharacterPlayerStats(player).GetValueOrDefault));

            await Toggle();
            var active = Game.CharacterPlayerStats(player);
            Assert.Equal(1, active.GetValueOrDefault(stat));
            foreach (var other in Charges.Types.Where(other => other != type))
                Assert.Equal(3, ChargeMaxima.Of(other, active.GetValueOrDefault));
            int maximum = ChargeMaxima.Of(type, active.GetValueOrDefault);
            Assert.Equal(0, charges.State.Held(ChargeType.Power));
            int minionCritical = Crit(active);
            charges.Observe(active.GetValueOrDefault);
            double now = Game.AreaTime(player);
            charges.Admit(now, [new ChargeEvent.Grant(type, 8, "test aura charge cap")],
                active.GetValueOrDefault, () => default, alive: true, random);
            int held = charges.State.Held(type);

            await Toggle();
            var off = Game.CharacterPlayerStats(player);
            Assert.Equal(0, off.GetValueOrDefault(stat));
            Assert.Equal(3, ChargeMaxima.Of(type, off.GetValueOrDefault));
            charges.Observe(off.GetValueOrDefault);
            charges.Step(Math.Max(now + .1, Game.AreaTime(player)), off.GetValueOrDefault, () => default, alive: true, moving: false, inTown: false, random);
            int after = charges.State.Held(type);
            int offCritical = Crit(off);
            Console.WriteLine($"Sublime Vision {auraName}: max {maximum}, held {held}, after turning off {after}; Ancient Skull crit {minionCritical}% -> {offCritical}%");
            Assert.Equal(type == ChargeType.Power ? 200 : 150, minionCritical);
            Assert.Equal(150, offCritical);
            results.Add(((4, 4, 3), (maximum, held, after)));
        }
        Assert.All(results, result => Assert.Equal(result.Want, result.Got));
    }

    [Fact]
    public void PerChargeStatsScaleWithTheChargesHeldOrTheMaximum()
    {
        // 5% reduced extra damage taken from critical strikes per Endurance Charge and Deathmarked's 10% Mark effect per
        // maximum Power Charge.
        var raw = new Dictionary<uint, int>
        {
            [Stat.SelfCriticalStrikeMultiplierMinusPctPerEnduranceCharge] = 5,
            [Stat.MarkSkillsCurseEffectPlusPctPerMaximumPowerCharge] = 10,
        };
        // power: the Endurance and Power Charges held.
        Dictionary<uint, int> Expand(int power, int maximum) => ChargeEffects.Expand(name => raw.GetValueOrDefault(name),
            t => t == ChargeType.Frenzy ? 0 : power, null, ChargeScaling.Rules,
            t => t == ChargeType.Power ? maximum : 3);
        var held = Expand(power: 3, maximum: 4);
        Assert.Equal(15, held[Stat.BaseSelfCriticalStrikeMultiplierMinusPct]);
        Assert.Equal(40, held[Stat.MarkSkillsCurseEffectPlusPct]);
        var none = Expand(power: 0, maximum: 4);
        Assert.Equal(40, none[Stat.MarkSkillsCurseEffectPlusPct]);
        Assert.False(none.ContainsKey(Stat.BaseSelfCriticalStrikeMultiplierMinusPct));
        // Badge of the Brotherhood: the Frenzy maximum is the Power one; with Masterful Form the Endurance one follows.
        var badge = new Dictionary<uint, int>
        {
            [Stat.MaxPowerCharges] = 2, [Stat.MaximumFrenzyChargesIsEqualToMaximumPowerCharges] = 1,
            [ChargePanelStats.EnduranceEqualsFrenzy] = 1,
        };
        Assert.Equal(new[] { 5, 5, 5 }, ChargeMaxima.Compute(name => badge.GetValueOrDefault(name)).Select(r => r.Value));
        // Ralakesh's Impatience counts as the maximum; Endurance Charge on Melee Stun's flag is the stunning skill's own.
        Assert.True(ChargeScaling.CountsAsMaximum(ChargeType.Power, n => n == Stat.CountAsHavingMaxPowerCharges ? 1 : 0));
        Func<uint, int> support = n => n == Stat.GainEnduranceChargeOnMeleeStun ? 1 : 0;
        Assert.Equal(ChargeType.Endurance, Assert.Single(ChargeGains.Roll(new ChargeEvent.Stun(Melee: true, support), _ => 0,
            default, new Random(1))).Type);
        Assert.Empty(ChargeGains.Roll(new ChargeEvent.Stun(Melee: false, support), _ => 0, default, new Random(1)));
    }
}
