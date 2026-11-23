using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class WeaponPassiveDamageTests
{
    [Fact]
    public void Weapon_and_bow_passives_scale_hits_spider_profiles_wand_gains_and_ground_dots()
    {
        double ReadHit(string weapon, bool passive)
        {
            using var fixture = new TriggeredSummonTests.Fixture();
            var player = fixture.Player;
            var items = Game.ItemsOf(player);
            uint host = Assert.Single(items.Items(PlayerInventories.Weapon1)).Id;
            items.Change(PlayerInventories.Weapon1, host, item => ItemCatalog.Create(Program.Items.Find(weapon)!)
                with { Sockets = item.Sockets, Links = item.Links });
            using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (!ReferenceEquals(owner, player)) return values;
                values[Stat.AttackMinimumAddedLightningDamage] = values[Stat.AttackMaximumAddedLightningDamage] = 700;
                if (passive) values[Stat.MaceElementalDamagePlusPct] = 25;
                return values;
            });
            ushort skill = Assert.Single(fixture.Equip(("Heavy Strike", 1)));
            return Game.SupportedHitFor(player, skill, suppress: false)[DamageType.Lightning].Max;
        }

        double mace = ReadHit("Driftwood Club", passive: false);
        Assert.True(mace > 0);
        Assert.Equal(mace * 1.25, ReadHit("Driftwood Club", passive: true), 7);
        Assert.Equal(ReadHit("Rusted Sword", passive: false), ReadHit("Rusted Sword", passive: true), 7);

        // SpiderDamageProfile retains WeaponTypedIncreases for attacks and SpellStaffDamagePlusPct for staff spells.
        using (var fixture = new TriggeredSummonTests.Fixture())
        {
            var player = fixture.Player;
            var items = Game.ItemsOf(player);
            uint host = Assert.Single(items.Items(PlayerInventories.Weapon1)).Id;
            items.Change(PlayerInventories.Weapon1, host, item => ItemCatalog.Create(Program.Items.Find("Driftwood Club")!)
                with { Sockets = item.Sockets, Links = item.Links });
            using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (!ReferenceEquals(owner, player)) return values;
                values[Stat.AttackMinimumAddedLightningDamage] = values[Stat.AttackMaximumAddedLightningDamage] = 400;
                values[Stat.MaceElementalDamagePlusPct] = 40;
                values[Stat.HitAndAilmentDamagePlusPctVsEnemiesAffectedByAtLeast3SpidersWebs] = 15;
                return values;
            });
            ushort skill = Assert.Single(fixture.Equip(("Heavy Strike", 1)));
            var inputs = Assert.IsType<Game.SupportedHitInputs>(Game.SupportedHitInputsFor(player, skill));
            var direct = Game.SupportedHitFrom(inputs, suppress: false)[DamageType.Lightning].Max;
            var spider = Assert.IsType<SpiderDamageSource>(Game.CaptureSpiderDamage(player, inputs, 1));
            var profiled = Game.SpiderDamageProfile(spider, webs: 0, blinded: false).Hit[DamageType.Lightning].Max;
            Assert.Equal(direct, profiled, 7);
        }

        using (var fixture = new TriggeredSummonTests.Fixture())
        {
            var player = fixture.Player;
            var items = Game.ItemsOf(player);
            uint host = Assert.Single(items.Items(PlayerInventories.Weapon1)).Id;
            items.Change(PlayerInventories.Weapon1, host, item => ItemCatalog.Create(Program.Items.Find("Gnarled Branch")!)
                with { Sockets = item.Sockets, Links = item.Links });
            using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (!ReferenceEquals(owner, player)) return values;
                values[Stat.SpellStaffDamagePlusPct] = 50;
                values[Stat.HitAndAilmentDamagePlusPctVsEnemiesAffectedByAtLeast3SpidersWebs] = 15;
                return values;
            });
            ushort spell = Assert.Single(fixture.Equip(("Ice Nova", 1)));
            var inputs = Assert.IsType<Game.SupportedHitInputs>(Game.SupportedHitInputsFor(player, spell));
            var direct = Game.SupportedHitFrom(inputs, suppress: false)[DamageType.Cold].Max;
            var spider = Assert.IsType<SpiderDamageSource>(Game.CaptureSpiderDamage(player, inputs, 1));
            var profiled = Game.SpiderDamageProfile(spider, webs: 0, blinded: false).Hit[DamageType.Cold].Max;
            Assert.Equal(direct, profiled, 7);
        }

        // AddWandGains applies through Skills.WeaponStats and StatSnapshotCache.
        using (var fixture = new TriggeredSummonTests.Fixture())
        {
            var player = fixture.Player;
            var items = Game.ItemsOf(player);
            uint host = Assert.Single(items.Items(PlayerInventories.Weapon1)).Id;
            items.Change(PlayerInventories.Weapon1, host, item => ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!)
                with { Sockets = item.Sockets, Links = item.Links });
            using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (!ReferenceEquals(owner, player)) return values;
                values[Stat.WandPhysicalDamagePctToAddAsFire] = 50;
                return values;
            });
            var cached = Game.CharacterPlayerStats(player);
            Assert.True(cached.GetValueOrDefault(Stat.MainHandMaximumFireDamage) > 0);
            var weaponStats = Game.WeaponStats(player, Game.WeaponFor(player), cached).ToDictionary(s => s.Item1, s => s.Item2);
            Assert.True(weaponStats.GetValueOrDefault(Stat.MainHandMaximumFireDamage) > 0);
        }

        // DotMultiplierPlusWithBowSkills scales CausticGroundDps.
        double plainDot = Game.CausticGroundDps(k => k == Stat.BaseChaosDamageToDealPerMinute ? 6000 : 0, _ => 0);
        double bowDot = Game.CausticGroundDps(k => k switch
        {
            Stat.BaseChaosDamageToDealPerMinute => 6000,
            Stat.DotMultiplierPlusWithBowSkills => 25,
            _ => 0,
        }, _ => 0);
        Assert.Equal(plainDot * 1.25, bowDot, 7);
    }

    [Fact]
    public void Attack_ignite_chance_and_elemental_attack_ailments_apply_across_attack_paths()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (!ReferenceEquals(owner, player)) return values;
            values[Stat.AttackIgniteChancePct] = 37;
            values[Stat.AttackMinimumAddedFireDamage] = values[Stat.AttackMaximumAddedFireDamage] = 700;
            return values;
        });
        ushort attack = Assert.Single(f.Equip(("Heavy Strike", 1)));
        var character = Game.CharacterPlayerStats(player);
        Assert.Equal(37, Game.SupportedIgnite(player, attack, spell: false, character).Chance);

        ushort spell = Assert.Single(f.Equip(("Ice Nova", 1)));
        Assert.Equal(0, Game.SupportedIgnite(player, spell, spell: true, character).Chance);

        // Projectile attack path and PanelStats both include AttackIgniteChancePct for weapon hands only.
        var items = Game.ItemsOf(player);
        uint host = Assert.Single(items.Items(PlayerInventories.Weapon1)).Id;
        items.Change(PlayerInventories.Weapon1, host, item => ItemCatalog.Create(Program.Items.Find("Crude Bow")!)
            with { Sockets = item.Sockets, Links = item.Links });
        var rangedUse = Game.CaptureOrdinaryRangedProjectile(player, Game.ProjectilesFor(player), (20, 0),
            OrdinaryRangedAttack.Arrow, 1, new(0), release: null);
        var rangedShot = Assert.IsType<ProjectileOffense>(rangedUse.Payload);
        Assert.Equal(37, rangedShot.Ignite.Chance);

        var panel = PanelStats.Compute(k => k == Stat.AttackIgniteChancePct ? 37 : 0).ToDictionary(s => s.Stat, s => s.Value);
        Assert.Equal(37, panel.GetValueOrDefault(Stat.VirtualTotalMainHandChanceToIgniteOnHitPct));
        Assert.Equal(37, panel.GetValueOrDefault(Stat.VirtualTotalOffHandChanceToIgniteOnHitPct));
        Assert.Equal(0, panel.GetValueOrDefault(Stat.VirtualTotalSpellChanceToIgniteOnHitPct));

        // FireDamageWithAttackSkillsPlusPct scales attack hits and attack Ignite, not spell hits or spell Ignite.
        var fireRaw = DamagePacket.Of(DamageType.Fire, 100, 100);
        int AttackFire(uint k) => k == Stat.FireDamageWithAttackSkillsPlusPct ? 50 : 0;
        Assert.Equal(150, PlayerHitDamage.Resolve(fireRaw, AttackFire, new(), spell: false)[DamageType.Fire].Max, 7);
        Assert.Equal(100, PlayerHitDamage.Resolve(fireRaw, AttackFire, new(), spell: true)[DamageType.Fire].Max, 7);

        int IgnitePerMin(string hand, bool isSpell, Func<uint, int> get) => AilmentPanelStats
            .Compute(new(hand, Spell: isSpell), fireRaw, new(), get)
            .First(r => r.Stat == Stat.Of($"maximum_ignite_damage_to_deal_per_minute_from_non_critical_{hand}_damage")).Value;
        int baseAttackIgnite = IgnitePerMin("main_hand", isSpell: false, _ => 0);
        Assert.Equal((int)Math.Round(baseAttackIgnite * 1.5), IgnitePerMin("main_hand", isSpell: false, AttackFire));
        Assert.Equal(IgnitePerMin("spell", isSpell: true, _ => 0), IgnitePerMin("spell", isSpell: true, AttackFire));
    }
}
