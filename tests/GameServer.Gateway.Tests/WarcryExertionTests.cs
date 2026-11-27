using GameServer.Core;
using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class WarcryExertionTests
{
    [Fact]
    public void Battlemages_Cry_only_links_eligible_spells_and_applies_the_gems_damage_penalty()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        fixture.Equip(("Battlemage's Cry", 1), ("Fireball", 1), ("Grace", 1), ("Essence Drain", 1));
        var fireball = Game.GemSkillEffects(player).Single(g => g.Name == "Fireball");
        var grace = Game.GemSkillEffects(player).Single(g => g.Name == "Grace");
        var drain = Game.GemSkillEffects(player).Single(g => g.Name == "Essence Drain");
        var linked = Game.SupportedSkill(player, fireball.Id);
        Assert.Equal(1, linked.Stats[Stat.TriggeredByDivineCry]);
        Assert.Equal(-44, linked.Stats[Stat.SupportDivineCryDamagePlusPctFinal]);
        Assert.NotNull(Game.GemSkillInstanceDisabled(player, fireball.Effect, fireball.Id));
        Assert.Null(Game.GemSkillInstanceDisabled(player, fireball.Effect, fireball.Id, triggered: true));
        Assert.Equal(0, Game.SupportedSkill(player, grace.Id).Stats.GetValueOrDefault(Stat.TriggeredByDivineCry));
        var reduced = Game.SupportedHitFor(player, fireball.Id, suppress: false, projectile: true)[DamageType.Fire];
        double reducedDot = Assert.IsType<MonsterEssenceDrain.Cast>(Game.EssenceDrainCast(player, drain.Id, drain.Effect)).DamagePerSecond;

        var items = Game.ItemsOf(player);
        var weapon = Assert.Single(items.Items(PlayerInventories.Weapon1));
        items.Change(PlayerInventories.Weapon1, weapon.Id, item => item with { Links = [1, 3] });
        var plain = Game.SupportedSkill(player, fireball.Id);
        Assert.Equal(0, plain.Stats.GetValueOrDefault(Stat.TriggeredByDivineCry));
        Assert.Null(Game.GemSkillInstanceDisabled(player, fireball.Effect, fireball.Id));
        var full = Game.SupportedHitFor(player, fireball.Id, suppress: false, projectile: true)[DamageType.Fire];
        Assert.True(full.Min > 0);
        Assert.Equal(full.Min * .56, reduced.Min, precision: 5);
        Assert.Equal(full.Max * .56, reduced.Max, precision: 5);
        double fullDot = Assert.IsType<MonsterEssenceDrain.Cast>(Game.EssenceDrainCast(player, drain.Id, drain.Effect)).DamagePerSecond;
        Assert.True(fullDot > 0);
        Assert.Equal(fullDot * .56, reducedDot, precision: 5);
    }

    [Fact]
    public async Task Battlemages_Cry_pays_for_one_spell_per_attack_and_spends_the_unaffordable_exertion()
    {
        var clock = new ViperStrikeMambaTests.AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        var origin = (X: 100, Y: 100);
        player.Walk.Teleport(origin.X, origin.Y, clock.At);
        player.ProjectileRuntimeState = fixture.Session;
        fixture.Equip(("Battlemage's Cry", 1), ("Fireball", 1), ("Ground Slam", 1));
        var cry = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "BattlemagesCry");
        var slam = Game.GemSkillEffects(player).Single(g => g.Name == "Ground Slam");
        var spell = Game.GemSkillEffects(player).Single(g => g.Name == "Fireball");
        var own = Game.SupportedSkill(player, cry.Id).Stats;
        Assert.Equal(5, own[Stat.SkillEmpowersNextXMeleeAttacks]);
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (ReferenceEquals(owner, player)) values[Stat.GlobalAlwaysHit] = 1;
            return values;
        });
        player.Area.Monsters.Add(911, new("Battlemage first target", new(110, 100, 0), 0, 0, 1_000_000));
        player.Area.Monsters.Add(912, new("Battlemage second target", new(114, 101, 0), 0, 0, 1_000_000));
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            int attackCost = Game.SupportedStats(player, slam.Id).GetValueOrDefault(Stat.ManaCost);
            int spellCost = Game.SupportedStats(player, spell.Id).GetValueOrDefault(Stat.ManaCost);
            Assert.True(attackCost > 0 && spellCost > 0);

            async Task<int> Use(int mana)
            {
                player.PlayerMana = (uint)mana;
                player.NextAttackAt = 0;
                Assert.True(await Game.CastGemSkill(player, slam.Id, origin, (120, 100)));
                clock.At += 1;
                fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                return mana - (int)player.PlayerMana;
            }

            Assert.Equal(attackCost, await Use(500));
            Game.GrantBattlemagesCryExertsLocked(player, cry.Id, own, new Dictionary<uint, int>());
            Assert.Equal(attackCost, await Use(attackCost));
            for (int i = 0; i < 4; i++) Assert.Equal(attackCost + spellCost, await Use(500));
            Assert.Equal(attackCost, await Use(500));
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Theory]
    [InlineData(2.49, 80)]
    [InlineData(2.50, 0)]
    public void Infernal_Cry_uses_the_debuff_at_death_even_when_the_caster_did_not_kill(
        double timeUntilDeath, int damage)
    {
        var clock = new ViperStrikeMambaTests.AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.ProjectileRuntimeState = fixture.Session;
        player.Walk.Teleport(100, 100, clock.At);
        fixture.Equip(("Infernal Cry", 1));
        var cry = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "InfernalCry");
        var own = Game.SupportedSkill(player, cry.Id).Stats;
        Assert.Equal(2500, own[Stat.BaseSecondarySkillEffectDuration]);

        const uint Victim = 901, Close = 902, Far = 903;
        var corpse = new SpawnedMonster("Infernal Cry corpse", new(110, 100, 0), 0, 0, 1000);
        var close = new SpawnedMonster("within the explosion", new(127, 100, 0), 0, 0, 10000);
        var far = new SpawnedMonster("outside the explosion", new(136, 100, 0), 0, 0, 10000);
        player.Area.Monsters.Add(Victim, corpse);
        player.Area.Monsters.Add(Close, close);
        player.Area.Monsters.Add(Far, far);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            Game.ApplyInfernalCryDebuffLocked(player, cry.Id, cry.Effect, own, (100, 100), 60, clock.At);
            Assert.True(player.Area.Buffs.Has(Victim, 1539, player.Id));
            clock.At += timeUntilDeath;
            Assert.Equal(0u, Game.ApplyMonsterHitLocked(player, corpse, 1000)?.Life);
            long at = ProjectileRuntime.Time(clock.At);
            Game.CommitProjectileKill(fixture.Session, Victim, corpse, at, Random.Shared, playerKill: false);

            // The output can arrive after the debuff's deadline. The actual time of death decides.
            clock.At += .1;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            Assert.Equal((uint)damage, close.MaxLife - close.Life);
            Assert.Equal(far.MaxLife, far.Life);
            Assert.Equal(damage != 0, corpse.CorpseConsumed);
            Assert.False(player.Area.Buffs.Has(Victim, 1539, player.Id));
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public async Task Infernal_Cry_releases_Combust_once_per_exerted_attack_that_hits()
    {
        var clock = new ViperStrikeMambaTests.AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        var origin = (X: 100, Y: 100);
        var aim = (X: 120, Y: 100);
        player.Walk.Teleport(origin.X, origin.Y, clock.At);
        player.ProjectileRuntimeState = fixture.Session;
        var items = Game.ItemsOf(player);
        var weapon = Assert.Single(items.Items(PlayerInventories.Weapon1));
        items.Change(PlayerInventories.Weapon1, weapon.Id, _ => ItemCatalog.Create(Program.Items.Find("Driftwood Club")!));
        fixture.Equip(("Infernal Cry", 1), ("Ground Slam", 1));
        var cry = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "InfernalCry");
        var combust = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "Combust");
        var slam = Game.GemSkillEffects(player).Single(g => g.Name == "Ground Slam");
        var own = Game.SupportedSkill(player, cry.Id).Stats;
        Assert.Equal(6, own[Stat.SkillEmpowersNextXMeleeAttacks]);
        Assert.NotNull(Game.GemSkillInstanceDisabled(player, combust.Effect, combust.Id));
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (ReferenceEquals(owner, player))
            {
                values[Stat.GlobalAlwaysHit] = 1;
                values[Stat.GlobalCannotCrit] = 1;
            }
            return values;
        });
        int fireHits = 0;
        using var damage = GameEvents.OutgoingDamage.Add("Game", (owner, _, _, rolled) =>
        {
            if (ReferenceEquals(owner, player) && rolled[DamageType.Fire].Max > 0) fireHits++;
            return rolled;
        });
        var foes = new[] { (Id: 901u, X: 109, Y: 100), (Id: 902u, X: 113, Y: 101) };
        foreach (var foe in foes)
            player.Area.Monsters.Add(foe.Id, new("Combust witness", new(foe.X, foe.Y, 0), 0, 0, 1_000_000));
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            async Task Slam((int X, int Y) toward, int expectedFireHits)
            {
                foreach (var foe in foes) player.Area.Monsters[foe.Id].Walk.Teleport(foe.X, foe.Y, clock.At);
                fireHits = 0;
                player.NextAttackAt = 0;
                Assert.True(await Game.CastGemSkill(player, slam.Id, origin, toward));
                clock.At += 1;
                fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                Assert.Equal(expectedFireHits, fireHits);
            }

            await Slam(aim, 0);
            Game.GrantInfernalCryExertsLocked(player, cry.Id, own, new Dictionary<uint, int>());
            await Slam((80, 100), 0); // one exertion was spent on a swing which did not hit
            for (int i = 0; i < 5; i++) await Slam(aim, 2);
            await Slam(aim, 0);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public async Task Seismic_Cry_extends_six_slam_waves_after_its_armour_buff_expires()
    {
        var clock = new ViperStrikeMambaTests.AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        var origin = (X: 100, Y: 100);
        var aim = (X: 168, Y: 100);
        player.Walk.Teleport(origin.X, origin.Y, clock.At);
        player.ProjectileRuntimeState = fixture.Session;
        var items = Game.ItemsOf(player);
        var weapon = Assert.Single(items.Items(PlayerInventories.Weapon1));
        items.Change(PlayerInventories.Weapon1, weapon.Id, _ => ItemCatalog.Create(Program.Items.Find("Driftwood Club")!));
        fixture.Equip(("Seismic Cry", 1), ("Ground Slam", 1), ("Heavy Strike", 1));
        var cry = Game.GemSkillEffects(player).Single(g => g.Name == "Seismic Cry");
        var slam = Game.GemSkillEffects(player).Single(g => g.Name == "Ground Slam");
        var strike = Game.GemSkillEffects(player).Single(g => g.Name == "Heavy Strike");
        var own = Game.SupportedSkill(player, cry.Id).Stats;
        Assert.Equal(6, own[Stat.SkillEmpowersNextXMeleeAttacks]);
        Assert.Equal(50, own[Stat.SeismicCryBaseSlamSkillAreaPlusPctFinal]);
        using var accuracy = GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
        {
            if (ReferenceEquals(owner, player)) stats[Stat.GlobalAlwaysHit] = 1;
            return stats;
        });
        var near = new SpawnedMonster("inside either wave", new(115, 100, 0), 0, 0, 1_000_000);
        var far = new SpawnedMonster("outside the regular wave", new(167, 100, 0), 0, 0, 1_000_000);
        player.Area.Monsters.Add(901, near);
        player.Area.Monsters.Add(902, far);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            async Task Slam(bool reachesFar)
            {
                uint nearLife = near.Life, farLife = far.Life;
                player.NextAttackAt = 0;
                Assert.True(await Game.CastGemSkill(player, slam.Id, origin, aim));
                Assert.True(near.Life < nearLife);
                Assert.Equal(reachesFar, far.Life < farLife);
            }

            await Slam(false);
            var playerStats = new Dictionary<uint, int>();
            Game.ApplyWarcryBuffLocked(player, "SeismicCry", own, playerStats, origin, 60, clock.At, 2.5, 1);
            Game.GrantSeismicCryExertsLocked(player, own, playerStats);
            Assert.True(player.Area.Buffs.Has(player.Id, 1544, player.Id));
            clock.At += 10;
            Game.ExpireWarcryBuffs(player);
            Assert.False(player.Area.Buffs.Has(player.Id, 1544, player.Id));

            player.NextAttackAt = 0;
            uint previousLife = near.Life;
            Assert.True(await Game.CastGemSkill(player, strike.Id, origin, (115, 100), 901));
            Assert.True(near.Life < previousLife);
            for (int i = 0; i < 6; i++) await Slam(true);
            await Slam(false);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public async Task Ancestral_Cry_exerts_eight_strikes_and_targets_two_additional_enemies()
    {
        var clock = new ViperStrikeMambaTests.AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        var origin = (X: 100, Y: 100);
        var aim = (X: 110, Y: 100);
        player.Walk.Teleport(origin.X, origin.Y, clock.At);
        player.ProjectileRuntimeState = fixture.Session;
        var items = Game.ItemsOf(player);
        var weapon = Assert.Single(items.Items(PlayerInventories.Weapon1));
        items.Change(PlayerInventories.Weapon1, weapon.Id, _ => ItemCatalog.Create(Program.Items.Find("Driftwood Club")!));
        fixture.Equip(("Ancestral Cry", 1), ("Ground Slam", 1), ("Heavy Strike", 1));
        var cry = Game.GemSkillEffects(player).Single(g => g.Name == "Ancestral Cry");
        var slam = Game.GemSkillEffects(player).Single(g => g.Name == "Ground Slam");
        var strike = Game.GemSkillEffects(player).Single(g => g.Name == "Heavy Strike");
        var own = Game.SupportedSkill(player, cry.Id).Stats;
        Assert.Equal(8, own[Stat.SkillEmpowersNextXMeleeAttacks]);
        Assert.Equal(2, own[Stat.AncestralCryEmpoweredAttacksStrikeXAdditionalEnemies]);
        using var accuracy = GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
        {
            if (ReferenceEquals(owner, player)) stats[Stat.GlobalAlwaysHit] = 1;
            return stats;
        });
        (int X, int Y)[] cells = [aim, (100, 114), (100, 86)];
        var monsters = cells.Select(cell => new SpawnedMonster("Ancestral Cry witness", new(cell.X, cell.Y, 0), 0, 0, 1_000_000)).ToArray();
        for (int i = 0; i < monsters.Length; i++) player.Area.Monsters.Add((uint)(901 + i), monsters[i]);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            async Task Strike(bool hitsExtras, ushort sequence)
            {
                for (int i = 0; i < monsters.Length; i++) monsters[i].Walk.Teleport(cells[i].X, cells[i].Y, clock.At);
                uint[] life = monsters.Select(m => m.Life).ToArray();
                player.NextAttackAt = 0;
                Assert.True(await Game.CastGemSkill(player, strike.Id, origin, aim, 901, sequence: sequence));
                Assert.True(monsters[0].Life < life[0]);
                for (int i = 1; i < monsters.Length; i++) Assert.Equal(hitsExtras, monsters[i].Life < life[i]);
            }

            await Strike(false, 0x8100);
            Game.GrantAncestralCryExertsLocked(player, own, new Dictionary<uint, int>());
            player.NextAttackAt = 0;
            Assert.True(await Game.CastGemSkill(player, slam.Id, origin, aim));
            for (int i = 0; i < 8; i++) await Strike(true, (ushort)(0x8101 + i));
            await Strike(false, 0x8109);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Theory]
    [InlineData(0, 1177)]
    [InlineData(1, 1236)]
    [InlineData(6, 1471)]
    public async Task Rallying_Cry_keeps_five_exerts_at_the_release_time_ally_count(int expectedAllies, int expectedDamage)
    {
        var clock = new ViperStrikeMambaTests.AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        var origin = (X: 100, Y: 100);
        var aim = (X: 109, Y: 100);
        player.Walk.Teleport(origin.X, origin.Y, clock.At);
        player.ProjectileRuntimeState = fixture.Session;
        var items = Game.ItemsOf(player);
        var weapon = Assert.Single(items.Items(PlayerInventories.Weapon1));
        items.Change(PlayerInventories.Weapon1, weapon.Id, _ => ItemCatalog.Create(Program.Items.Find("Driftwood Club")!));
        fixture.Equip(("Rallying Cry", 1), ("Heavy Strike", 1), ("Overexertion Support", 1));
        var cry = Game.GemSkillEffects(player).Single(g => g.Name == "Rallying Cry");
        var strike = Game.GemSkillEffects(player).Single(g => g.Name == "Heavy Strike");
        var own = Game.SupportedSkill(player, cry.Id).Stats;
        Assert.Equal(5, own[Stat.SkillEmpowersNextXMeleeAttacks]);
        Assert.Equal(5, own[Stat.RallyingCryDamagePlusPctFinalFromOsmPerNearbyAlly]);
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (ReferenceEquals(owner, player))
            {
                values[Stat.GlobalAlwaysHit] = 1;
                values[Stat.GlobalCannotCrit] = 1;
            }
            return values;
        });
        using var damage = GameEvents.OutgoingDamage.Add("Game", (owner, _, _, rolled) => ReferenceEquals(owner, player)
            ? DamagePacket.Of(DamageType.Chaos, 1000, 1000) : rolled);
        var foe = new SpawnedMonster("Rallying Cry witness", new(aim.X, aim.Y, 0), 0, 0, 1_000_000);
        player.Area.Monsters.Add(901, foe);
        player.Area.Monsters.Add(902, new("out of range", new(190, 100, 0), 0, 0, 100) { Hostile = false });
        player.Area.Monsters.Add(903, new("non-blocking ally", new(100, 110, 0), 0, 0, 100)
            { Hostile = false, NonBlocking = true });
        for (int i = 1; i < expectedAllies; i++)
            player.Area.Monsters.Add((uint)(910 + i), new("nearby ally", new(100, 110 + i, 0), 0, 0, 100) { Hostile = false });
        if (expectedAllies > 0) player.Totems.Place(new(909, 0, (100, 112), 100, clock.At + 3, null), 1, clock.At);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            async Task<int> Strike()
            {
                foe.Walk.Teleport(aim.X, aim.Y, clock.At);
                uint life = foe.Life;
                player.NextAttackAt = 0;
                Assert.True(await Game.CastGemSkill(player, strike.Id, origin, aim, 901));
                return (int)(life - foe.Life);
            }

            Assert.Equal(1000, await Strike());
            var playerStats = new Dictionary<uint, int>();
            int affected = Game.GrantWarcryBuffLocked(player, "RallyingCry", own, playerStats, origin, 60, clock.At, 2.5, 1);
            Assert.Equal(expectedAllies, affected);
            Assert.False(player.Area.Buffs.Has(player.Id, Game.RallyingCryBuffRow, player.Id));
            Assert.False(player.Area.Buffs.Has(903, Game.RallyingCryBuffRow, player.Id));
            Game.GrantRallyingCryExertsLocked(player, own, playerStats, affected);

            clock.At += 10;
            Game.ExpireWarcryBuffs(player);
            player.Totems.Clear();
            for (int i = 1; i < expectedAllies; i++) player.Area.Monsters.Remove((uint)(910 + i));
            for (int i = 0; i < 5; i++) Assert.InRange(await Strike(), expectedDamage - 1, expectedDamage + 1);
            Assert.Equal(1000, await Strike());
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public void War_bringer_doubles_the_native_count_and_each_eligible_attack_spends_one()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        var native = IntimidatingCryStats();
        Game.ApplyIntimidatingCryLocked(player, native, new Dictionary<uint, int>(), 1);
        Assert.Equal(2, Game.IntimidatingCryExerts(player));

        Game.ApplyIntimidatingCryLocked(player, native, new Dictionary<uint, int> { [Stat.WarcriesExertTwiceAsManyAttacks] = 1 }, 1);
        Assert.Equal(4, Game.IntimidatingCryExerts(player));
        Assert.False(Game.ConsumeIntimidatingCryExertLocked(player, eligible: false));
        for (int remaining = 3; remaining >= 0; remaining--)
        {
            Assert.True(Game.ConsumeIntimidatingCryExertLocked(player, eligible: true));
            Assert.Equal(remaining, Game.IntimidatingCryExerts(player));
        }
        Assert.False(Game.ConsumeIntimidatingCryExertLocked(player, eligible: true));
    }

    [Theory]
    [InlineData("Molten Strike", false)]
    [InlineData("Shield Charge", false)]
    [InlineData("Shield Charge", true)]
    public async Task Paid_attacks_apply_both_multipliers_only_when_eligible(string attack, bool blockTravelExertion)
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        var previous = Game.AreaUpdateContext.Value;
        var clock = new AttackClock();
        player.ProjectileRuntimeState = f.Session;
        player.Walk.Teleport(100, 100, clock.At);
        Game.AreaUpdateContext.Value = new(f.Lifetime, clock);
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (ReferenceEquals(owner, player))
            {
                values[Stat.GlobalAlwaysHit] = 1;
                values[Stat.GlobalCannotCrit] = 1;
                if (blockTravelExertion) values[Stat.TravelSkillsCannotBeExerted] = 1;
            }
            return values;
        });
        using var damage = GameEvents.OutgoingDamage.Add("Game", (owner, _, _, rolled) => ReferenceEquals(owner, player)
            ? DamagePacket.Of(DamageType.Physical, 1000, 1000) : rolled);
        try
        {
            if (attack == "Shield Charge")
            {
                var items = Game.ItemsOf(player);
                items.Change(PlayerInventories.Weapon1, items.Items(PlayerInventories.Weapon1).Single().Id,
                    _ => ItemCatalog.Create(Program.Items.Find("Driftwood Club")!));
                items.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find("Splintered Tower Shield")!));
            }
            ushort skill = attack == "Molten Strike"
                ? Assert.Single(f.Equip((attack, 1), ("Overexertion Support", 1), ("Momentum Support", 1)))
                : Assert.Single(f.Equip((attack, 1), ("Overexertion Support", 1)));
            var native = Game.SupportedSkill(player, skill).Stats;
            Assert.Equal(7, native[Stat.SupportOverexertionDamagePlusPctFinalPerWarcryExertingAction]);
            Assert.Equal(10, native[Stat.SupportOverexertionDamagePlusPctFinalIfExerted]);
            uint normal = await Hit(901);
            Assert.Equal(1000u, normal);

            Game.ApplyIntimidatingCryLocked(player, IntimidatingCryStats(), new Dictionary<uint, int>(), clock.At);
            Assert.Equal(2, Game.IntimidatingCryExerts(player));
            Assert.Equal(blockTravelExertion ? normal : 1177u, await Hit(902));
            Assert.Equal(blockTravelExertion ? 2 : 1, Game.IntimidatingCryExerts(player));
            if (attack == "Molten Strike")
                Assert.Equal(native.GetValueOrDefault(Stat.AttackSpeedPlusPct) + 30,
                    Game.SupportedSkill(player, skill).Stats.GetValueOrDefault(Stat.AttackSpeedPlusPct));

            async Task<uint> Hit(uint id)
            {
                var target = new SpawnedMonster("exerted attack target", new(104, 100, 0), 0, 0, 1_000_000);
                player.Area.Monsters.Add(id, target);
                player.NextAttackAt = 0;
                // At zero travel distance Shield Charge lands one hit and has no distance bonus.
                var aim = attack == "Shield Charge" ? (100, 100) : (104, 100);
                Assert.True(await Game.CastGemSkill(player, skill, (100, 100), aim, id));
                while (target.Life == target.MaxLife && clock.At < 10)
                {
                    clock.At += .05;
                    f.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                }
                Assert.True(target.Life < target.MaxLife);
                player.Area.Monsters.Remove(id);
                clock.At += 2;
                f.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                return target.MaxLife - target.Life;
            }
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public async Task Overexertion_scales_damaging_ailments_on_exerted_attacks()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        var previous = Game.AreaUpdateContext.Value;
        var clock = new AttackClock();
        player.ProjectileRuntimeState = f.Session;
        player.Walk.Teleport(100, 100, clock.At);
        Game.AreaUpdateContext.Value = new(f.Lifetime, clock);
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (ReferenceEquals(owner, player))
            {
                values[Stat.GlobalAlwaysHit] = 1;
                values[Stat.GlobalCannotCrit] = 1;
                values[Stat.BleedOnHitWithAttacksPct] = 100;
                values[Stat.AlwaysIgnite] = 1;
                values[Stat.BaseChanceToPoisonOnHitPct] = 100;
                values[Stat.AttackMinimumAddedPhysicalDamage] = 10000;
                values[Stat.AttackMaximumAddedPhysicalDamage] = 10000;
                values[Stat.AttackMinimumAddedFireDamage] = 10000;
                values[Stat.AttackMaximumAddedFireDamage] = 10000;
                values[Stat.NumberOfAdditionalProjectiles] = 1;
            }
            return values;
        });
        try
        {
            async Task<(double Bleed, double Ignite, double Poison)> Measure(ushort skill, (int X, int Y) targetAt, uint id)
            {
                var target = new SpawnedMonster("exerted ailment target", new(targetAt.X, targetAt.Y, 0), 0, 0, 10_000_000);
                player.Area.Monsters.Add(id, target);
                player.NextAttackAt = 0;
                Assert.True(await Game.CastGemSkill(player, skill, (100, 100), targetAt, id));
                while (target.Life == target.MaxLife && clock.At < 40)
                {
                    clock.At += .05;
                    f.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                }
                Assert.True(target.Life < target.MaxLife);
                double bleed = target.Bleeds.PerSecond(clock.At);
                double ignite = target.Ignites.PerSecond(clock.At);
                double poison = target.Poisons.TotalDps(clock.At);
                player.Area.Monsters.Remove(id);
                clock.At += 2;
                f.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                Assert.True(bleed > 0 && ignite > 0 && poison > 0);
                return (bleed, ignite, poison);
            }

            static void AssertScaled((double Bleed, double Ignite, double Poison) normal, (double Bleed, double Ignite, double Poison) exerted)
            {
                Assert.InRange(exerted.Bleed / normal.Bleed, 1.17, 1.184);
                Assert.InRange(exerted.Ignite / normal.Ignite, 1.17, 1.184);
                Assert.InRange(exerted.Poison / normal.Poison, 1.17, 1.184);
            }

            // Molten Strike melee (MoltenStrike.cs) + Lightning Strike melee (Combat.cs) and projectile (Projectiles.cs).
            ushort molten = Assert.Single(f.Equip(("Molten Strike", 1), ("Overexertion Support", 1)));
            var moltenNormal = await Measure(molten, (104, 100), 951);
            Game.ApplyIntimidatingCryLocked(player, IntimidatingCryStats(), new Dictionary<uint, int>(), clock.At);
            var moltenExerted = await Measure(molten, (104, 100), 952);
            AssertScaled(moltenNormal, moltenExerted);

            ushort lightning = Assert.Single(f.Equip(("Lightning Strike", 1), ("Overexertion Support", 1)));
            Game.ConsumeIntimidatingCryExertLocked(player, eligible: true); // spend remaining exert
            var areaNormal = await Measure(lightning, (104, 100), 953);
            var projNormal = await Measure(lightning, (118, 100), 954);
            Game.ApplyIntimidatingCryLocked(player, IntimidatingCryStats(), new Dictionary<uint, int>(), clock.At);
            var areaExerted = await Measure(lightning, (104, 100), 955);
            var projExerted = await Measure(lightning, (118, 100), 956);
            AssertScaled(areaNormal, areaExerted);
            AssertScaled(projNormal, projExerted);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public async Task Automation_and_Autoexertion_publish_linked_counts_active_buffs_and_exerted_warcry_triggers()
    {
        var clock = new AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        var origin = (X: 100, Y: 100);
        player.Walk.Teleport(origin.X, origin.Y, clock.At);
        player.ProjectileRuntimeState = fixture.Session;
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            // Unlinked Automation publishes meta_skill_linked_skill_count = 0 and refuses activation.
            fixture.Equip(("Automation", 1));
            var soloAuto = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "Automation");
            var soloSkill = Game.PlayerSkills(player).Single(s => s.Id == soloAuto.Id);
            Assert.Equal(0, soloSkill.Stats!.First(s => s.Key == Stat.MetaSkillLinkedSkillCount).Value);
            Assert.True(Game.TryBeginSkillCooldownUse(player, soloAuto.Id, soloAuto.Effect,
                Game.CooldownForSkill(player, soloAuto.Id), instant: true, () => { }, out var soloTicket));
            Assert.False(await Game.CastGemSkill(player, soloAuto.Id, origin, origin, cooldownUse: soloTicket));
            clock.At += 2;

            // Linked Automation (Steelskin + Brand Recall) publishes linked count = 2 and activates with ActiveSkill on buff 2437.
            fixture.Equip(("Automation", 1), ("Steelskin", 1), ("Brand Recall", 1));
            var auto = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "Automation");
            var autoRecord = Game.PlayerSkills(player).Single(s => s.Id == auto.Id);
            Assert.Equal(2, autoRecord.Stats!.First(s => s.Key == Stat.MetaSkillLinkedSkillCount).Value);
            Assert.True(Game.TryBeginSkillCooldownUse(player, auto.Id, auto.Effect,
                Game.CooldownForSkill(player, auto.Id), instant: true, () => { }, out var autoTicket));
            Assert.True(await Game.CastGemSkill(player, auto.Id, origin, origin, cooldownUse: autoTicket));
            var autoBuff = player.Area.Buffs.Get(player.Id, 2437, player.Id, tag: auto.Id);
            Assert.NotNull(autoBuff);
            Assert.Equal((auto.Id, Program.Skills.ActionHash(auto.Effect)!.Value), autoBuff!.ActiveSkill);
            Assert.Equal(0, Game.ReservedMana(player));

            // Linked Autoexertion (Rallying Cry + Seismic Cry) publishes linked count = 2, reserves 40% mana, and keeps buff 2438 ActiveSkill.
            fixture.Equip(("Autoexertion", 1), ("Rallying Cry", 1), ("Seismic Cry", 1));
            var autoexert = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "Autoexertion");
            var rallying = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "RallyingCry");
            var autoexertRecord = Game.PlayerSkills(player).Single(s => s.Id == autoexert.Id);
            Assert.Equal(2, autoexertRecord.Stats!.First(s => s.Key == Stat.MetaSkillLinkedSkillCount).Value);
            Assert.True(Game.TryBeginSkillCooldownUse(player, autoexert.Id, autoexert.Effect,
                Game.CooldownForSkill(player, autoexert.Id), instant: true, () => { }, out var exertTicket));
            Assert.True(await Game.CastGemSkill(player, autoexert.Id, origin, origin, cooldownUse: exertTicket));
            var exertBuff = player.Area.Buffs.Get(player.Id, 2438, player.Id, tag: autoexert.Id);
            Assert.NotNull(exertBuff);
            Assert.Equal((autoexert.Id, Program.Skills.ActionHash(autoexert.Effect)!.Value), exertBuff!.ActiveSkill);
            int expectedReserved = 2 * PetrifiedBlood.Required(player.Pools.Mana, player.Pools.ManaReservationEfficiencyPct, 2000, 0);
            Assert.Equal(expectedReserved, Game.ReservedMana(player));

            // Autoexertion-supported Rallying Cry suppresses the Rallying Cry buff (1538) while still counting nearby allies for exertion.
            var ally = new SpawnedMonster("Rallying Cry ally", new(110, 100, 0), 0, 0, 1000) { Hostile = false, OwnerId = player.Id };
            player.Area.Monsters.Add(981, ally);
            var rallyingGem = Game.SupportedSkill(player, rallying.Id);
            Assert.Equal(1, rallyingGem.Stats.GetValueOrDefault(Stat.WarcriesDoNotApplyBuffsToSelfOrAllies));
            Assert.Equal(1, Game.RallyingCryNearbyAlliesLocked(player, origin, Game.WarcryRadius, clock.At));
            Game.GrantRallyingCryExertsLocked(player, rallyingGem.Stats, new Dictionary<uint, int>(), 1, rallying.Id);
            Assert.False(player.Area.Buffs.Has(player.Id, Game.RallyingCryBuffRow, player.Id));
            var hudBuff = player.Area.Buffs.Get(player.Id, Game.EmpoweredAttacksBuffRow, player.Id, tag: 4);
            Assert.NotNull(hudBuff);
            Assert.Equal((rallying.Id, Program.Skills.ActionHash(rallying.Effect)!.Value), hudBuff!.ActiveSkill);
            Assert.Equal((ushort)rallyingGem.Stats[Stat.SkillEmpowersNextXMeleeAttacks], hudBuff.Stacks);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public async Task Warcry_exertion_publishes_HUD_counts_and_applies_passive_exerted_attack_modifiers()
    {
        var clock = new AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        var origin = (X: 100, Y: 100);
        var aim = (X: 112, Y: 100);
        player.Walk.Teleport(origin.X, origin.Y, clock.At);
        player.ProjectileRuntimeState = fixture.Session;
        var items = Game.ItemsOf(player);
        var weapon = Assert.Single(items.Items(PlayerInventories.Weapon1));
        items.Change(PlayerInventories.Weapon1, weapon.Id, _ => ItemCatalog.Create(Program.Items.Find("Driftwood Club")!));
        fixture.Equip(("Seismic Cry", 1), ("Ground Slam", 1));
        var cry = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "SeismicCry");
        var slam = Game.GemSkillEffects(player).Single(g => g.Name == "Ground Slam");
        var cryOwn = Game.SupportedSkill(player, cry.Id).Stats;
        Assert.Equal(6, cryOwn[Stat.SkillEmpowersNextXMeleeAttacks]);

        bool spiderConditional = false;
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (ReferenceEquals(owner, player))
            {
                values[Stat.GlobalAlwaysHit] = 1;
                values[Stat.GlobalCannotCrit] = 1;
                values[Stat.AttackMinimumAddedPhysicalDamage] = 1000;
                values[Stat.AttackMaximumAddedPhysicalDamage] = 1000;
                values[Stat.WarcryEmpowersNextXMeleeAttacks] = 1; // Measured Fury (+1 exerted attack)
                values[Stat.EmpoweredAttackDamagePlusPct] = 100; // +100% increased exerted attack damage
                values[Stat.EmpoweredAttackDamagePlusPctFinal] = 50; // +50% more exerted attack damage
                if (spiderConditional) values[Stat.HitAndAilmentDamagePlusPctVsEnemiesAffectedByAtLeast3SpidersWebs] = 25;
            }
            return values;
        });

        var target = new SpawnedMonster("exerted slam target", new(110, 100, 0), 0, 0, 10_000_000);
        player.Area.Monsters.Add(971, target);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            async Task<uint> SlamDamage()
            {
                uint before = target.Life;
                player.NextAttackAt = 0;
                Assert.True(await Game.CastGemSkill(player, slam.Id, origin, aim));
                return before - target.Life;
            }

            uint unexerted = await SlamDamage();
            Assert.True(unexerted > 0);
            Assert.Null(player.Area.Buffs.Get(player.Id, Game.EmpoweredAttacksBuffRow, player.Id, tag: 2));

            // Grant Seismic Cry with Measured Fury (+1 => 7 exerts) and verify HUD counter buff 1579.
            var playerStats = Game.CharacterPlayerStats(player);
            Game.GrantSeismicCryExertsLocked(player, cryOwn, playerStats, cry.Id);
            var hud = player.Area.Buffs.Get(player.Id, Game.EmpoweredAttacksBuffRow, player.Id, tag: 2);
            Assert.NotNull(hud);
            Assert.Equal((cry.Id, Program.Skills.ActionHash(cry.Effect)!.Value), hud!.ActiveSkill);
            Assert.Equal((ushort)7, hud.Stacks);

            // Exerted slam gains +100% increased and +50% more damage and decrements HUD stacks to 6.
            uint exerted = await SlamDamage();
            Assert.True(exerted > unexerted * 2.5, $"Expected exerted damage ({exerted}) to reflect +100% increased and +50% more over {unexerted}.");
            Assert.Equal((ushort)6, player.Area.Buffs.Get(player.Id, Game.EmpoweredAttacksBuffRow, player.Id, tag: 2)!.Stacks);

            // Spend the remaining 6 exerts (including with conditional SpiderDamage active) and confirm HUD buff clears at 0.
            spiderConditional = true;
            uint exertedWithSpider = await SlamDamage();
            Assert.True(exertedWithSpider > unexerted * 2.5,
                $"Expected exerted damage with conditional SpiderDamage ({exertedWithSpider}) to keep +100% increased and +50% more over {unexerted}.");
            spiderConditional = false;
            for (int i = 0; i < 5; i++) Assert.True(await SlamDamage() > unexerted * 2.5);
            Assert.Null(player.Area.Buffs.Get(player.Id, Game.EmpoweredAttacksBuffRow, player.Id, tag: 2));
            Assert.Equal(unexerted, await SlamDamage());

            // Verify additive accuracy increase bucket and base-crit scaling (before % increased crit).
            int baseRating = Game.SupportedAccuracy(player, slam.Id, AttackHand.Main, extraIncreased: 0).Rating;
            int plus100 = Game.SupportedAccuracy(player, slam.Id, AttackHand.Main, extraIncreased: 100).Rating;
            int plus200 = Game.SupportedAccuracy(player, slam.Id, AttackHand.Main, extraIncreased: 200).Rating;
            Assert.True(plus100 > baseRating && plus200 - plus100 <= plus100 - baseRating + 1,
                $"Expected additive accuracy increase: base={baseRating}, +100%={plus100}, +200%={plus200}.");

            int baseCrit = Game.SupportedCriticalChanceFor(player, slam.Id, release: true, extraIncrease: 100, weaponCritical: 500, extraBaseCrit: 0);
            int boostedCrit = Game.SupportedCriticalChanceFor(player, slam.Id, release: true, extraIncrease: 100, weaponCritical: 500, extraBaseCrit: 200);
            Assert.Equal(baseCrit + 400, boostedCrit); // +2% base crit scaled by +100% increased = +4% final crit

            // Verify Infernal Cry + Overexertion on Lightning Strike projectiles counts Infernal Cry once (not twice).
            fixture.Equip(("Infernal Cry", 1), ("Lightning Strike", 1), ("Overexertion Support", 1));
            var infernal = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "InfernalCry");
            var ls = Game.GemSkillEffects(player).Single(g => g.Name == "Lightning Strike");
            Game.GrantInfernalCryExertsLocked(player, infernal.Id, Game.SupportedSkill(player, infernal.Id).Stats, new Dictionary<uint, int>());
            var lsRoll = Game.CaptureSkillCriticalRoll(player, ls.Id);
            var lsExert = Game.ConsumeMeleeWarcryExertsLocked(player, ls.Id, ls.Effect, lsRoll, eligible: true);
            var lsProj = Game.CaptureGemProjectile(player, fixture.Session, ls.Id, ls, (120, 100), lsRoll, weaponHand: null,
                cryExerted: lsExert.Intimidating, otherWarcryExertions: lsExert.AncestralTargets > 0 ? 1 : 0,
                rallyingDamage: lsExert.RallyingDamage, ancestralExerted: lsExert.AncestralTargets > 0);
            Assert.Equal(1, ((ProjectileOffense)lsProj.Payload!).OtherWarcryExertions);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    static Dictionary<uint, int> IntimidatingCryStats()
    {
        int effect = Program.Items.FindGem("Intimidating Cry")!.Value.Variant.Effect;
        return Program.Skills.EffectStats(effect, 1, 0).ToDictionary(stat => stat.Item1, stat => stat.Item2);
    }

    sealed class AttackClock : IPlayerUpdateSchedule
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
}
