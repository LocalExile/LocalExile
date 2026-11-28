using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class AutomationTests
{
    sealed class ManualClock(double start) : IPlayerUpdateSchedule
    {
        readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public double At = start;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping) => release.Task.WaitAsync(stopping);
        public void Release() => release.TrySetResult();
    }

    static async Task Drain(Game.ProjectileSession session)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Enqueue(() => { done.SetResult(); return Task.CompletedTask; });
        await done.Task;
    }

    static async Task<bool> ManualCastInstant(Player player, ushort skillId, (int X, int Y) at)
    {
        var gem = Game.GemSkillEffects(player).Single(g => g.Id == skillId);
        if (!Game.TryBeginSkillCooldownUse(player, skillId, gem.Effect, Game.CooldownForSkill(player, skillId),
                instant: true, () => { }, out var ticket))
            return false;
        return await Game.CastGemSkill(player, skillId, at, at, cooldownUse: ticket);
    }

    [Fact]
    public async Task Standalone_instant_spells_work_manually_with_retail_mechanics()
    {
        var clock = new ManualClock(10);
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        var origin = (X: 100, Y: 100);
        player.Walk.Teleport(origin.X, origin.Y, clock.At);
        player.ProjectileRuntimeState = fixture.Session;
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            // 1. Arcane Cloak: consumes % of current mana, creates 75% hit-absorption guard buff (1484 with List=[remaining]),
            // grants added global lightning damage = 10% of mana spent, shares GuardCooldownGroup 10, refuses at 0 mana.
            fixture.Equip(("Arcane Cloak", 1), ("Steelskin", 1));
            var cloak = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "ArcaneCloak");
            var steelskin = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "QuickGuard");
            player.PlayerMana = 0;
            Assert.False(await ManualCastInstant(player, cloak.Id, origin));
            player.PlayerMana = 1000;
            Assert.True(await ManualCastInstant(player, cloak.Id, origin));
            // L1 Arcane Cloak consumes 45% of current mana => 450 spent, 450 shield, 45 flat added lightning damage.
            Assert.Equal(550u, player.PlayerMana);
            var cloakBuff = player.Area.Buffs.Get(player.Id, 1484, player.Id);
            Assert.NotNull(cloakBuff);
            Assert.Equal([450u], cloakBuff!.List);
            var cloakStats = Game.CharacterPlayerStats(player);
            Assert.Equal(1, cloakStats.GetValueOrDefault(Stat.AffectedByGuardSkill));
            Assert.True(cloakStats.GetValueOrDefault(Stat.GlobalMinimumAddedLightningDamage) >= 45);
            Assert.True(cloakStats.GetValueOrDefault(Stat.GlobalMaximumAddedLightningDamage) >= 45);
            // Shares guard cooldown group 10: Steelskin refused while Arcane Cloak runs.
            Assert.False(await ManualCastInstant(player, steelskin.Id, origin));
            // Hit absorption: 75% of 400 = 300 absorbed, leaving 100 damage and 150 remaining shield.
            var (phys, _, _) = Game.TriggeredGuardHit(player, 400, 0, 0, clock.At);
            Assert.Equal(100, phys);
            Assert.Equal([150u], player.Area.Buffs.Get(player.Id, 1484, player.Id)!.List);
            // Depleting remaining shield (200 * 75% = 150) ends Arcane Cloak and clears buff/lightning bonus.
            var (phys2, _, _) = Game.TriggeredGuardHit(player, 200, 0, 0, clock.At);
            Assert.Equal(50, phys2);
            Assert.Null(player.Area.Buffs.Get(player.Id, 1484, player.Id));
            clock.At += 5;

            // 2. Withering Step: grants slither (1360) + Elusive (1374), inflicts Withered stacks on nearby enemies once per use,
            // refuses while Elusive, and ends + removes Elusive when using another skill.
            fixture.Equip(("Withering Step", 1), ("Blood Rage", 1));
            var step = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "WitheringStep");
            var bloodRage = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "BloodRage");
            var witherTarget = new SpawnedMonster("wither dummy", new(110, 100, 0), 0, 0, 100_000);
            player.Area.Monsters[801] = witherTarget;
            player.PlayerMana = 1000;
            Assert.True(await ManualCastInstant(player, step.Id, origin));
            Assert.NotNull(player.Area.Buffs.Get(player.Id, Game.WitheringStepBuffRow, player.Id));
            Assert.NotNull(player.Area.Buffs.Get(player.Id, Game.ElusiveBuffRow, player.Id));
            Assert.Equal(4, witherTarget.Withered.Count);
            // Cannot cast Withering Step while already Elusive.
            Assert.False(await ManualCastInstant(player, step.Id, origin));
            // Casting another skill ends Withering Step and removes Elusive.
            Assert.True(await ManualCastInstant(player, bloodRage.Id, origin));
            Assert.Null(player.Area.Buffs.Get(player.Id, Game.WitheringStepBuffRow, player.Id));
            Assert.Null(player.Area.Buffs.Get(player.Id, Game.ElusiveBuffRow, player.Id));
            Assert.Equal(0, Game.ElusiveOf(player).Effect(clock.At));
            clock.At += 5;
            // Letting Withering Step's Elusive decay naturally clears both slither (1360) and elusive (1374) on tick.
            Assert.True(await ManualCastInstant(player, step.Id, origin));
            Assert.NotNull(player.Area.Buffs.Get(player.Id, Game.ElusiveBuffRow, player.Id));
            clock.At += 6;
            await Game.TickInstantBuffSkills(player, clock.At);
            Assert.Null(player.Area.Buffs.Get(player.Id, Game.WitheringStepBuffRow, player.Id));
            Assert.Null(player.Area.Buffs.Get(player.Id, Game.ElusiveBuffRow, player.Id));
            clock.At += 5;

            // 3. Berserk: requires >= 5 Rage, grants berserk buff (1310) + RageEffectPlusPct, drains Rage at accelerating rate until 0.
            fixture.Equip(("Berserk", 1));
            var berserk = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "Berserk");
            player.Rage.Clear();
            player.PlayerMana = 1000;
            Assert.False(await ManualCastInstant(player, berserk.Id, origin)); // 0 Rage < 5 minimum
            player.Rage.Gain(20, clock.At, new RageState.Inputs());
            Assert.True(await ManualCastInstant(player, berserk.Id, origin));
            Assert.NotNull(player.Area.Buffs.Get(player.Id, Game.BerserkBuffRow, player.Id));
            var berserkStats = Game.CharacterPlayerStats(player);
            Assert.Equal(1, berserkStats.GetValueOrDefault(Stat.AffectedByBerserk));
            Assert.True(berserkStats.GetValueOrDefault(Stat.RageEffectPlusPct) >= 40);
            // Cannot cast again while active.
            Assert.False(await ManualCastInstant(player, berserk.Id, origin));
            // Advance clock in steps: Rage drains at accelerating rate until 0, ending Berserk.
            for (int i = 0; i < 6; i++)
            {
                clock.At += 1;
                await Game.TickInstantBuffSkills(player, clock.At);
            }
            Assert.Null(player.Area.Buffs.Get(player.Id, Game.BerserkBuffRow, player.Id));
            Assert.Equal(0, player.Rage.Read(clock.At, new RageState.Inputs()).Current);
            clock.At += 6;

            // 4. Corrupting Fever: pays Life cost, grants blood_surge (1797), inflicts stacking Corrupted Blood (238) on hit
            // dealing physical DoT, and refreshes duration when spending life threshold.
            fixture.Equip(("Corrupting Fever", 1), ("Blood Rage", 1));
            var fever = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "CorruptingFever");
            var lifeSpender = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "BloodRage");
            player.Pools = player.Pools with { Life = 2000 };
            player.PlayerLife = 2000;
            uint lifeBefore = player.PlayerLife;
            Assert.True(await ManualCastInstant(player, fever.Id, origin));
            Assert.True(player.PlayerLife < lifeBefore);
            Assert.NotNull(player.Area.Buffs.Get(player.Id, Game.CorruptingFeverBuffRow, player.Id));
            // Hit a monster -> inflicts Corrupted Blood (row 238) and ticks physical DoT.
            var cbTarget = new SpawnedMonster("cb dummy", new(112, 100, 0), 0, 0, 10_000);
            player.Area.Monsters[802] = cbTarget;
            var hitStats = Game.CharacterPlayerStats(player);
            for (int hit = 0; hit < 12; hit++)
                Game.CorruptingFeverOnHitLocked(player, 802, cbTarget, hitStats, clock.At, new Random(1));
            Assert.Equal(10, Game.MonsterCorruptedBloodStacks(cbTarget, clock.At));
            Assert.Equal((ushort)10, player.Area.Buffs.Get(802, Game.MonsterCorruptedBloodBuffRow, player.Id)!.Stacks);
            uint cbLifeBefore = cbTarget.Life;
            clock.At += 1;
            await Game.TickMonsterCorruptedBlood(player);
            Assert.True(cbTarget.Life < cbLifeBefore);
            // Spending life threshold refreshes Corrupting Fever duration past its original 6s expiry.
            clock.At += 4;
            for (int i = 0; i < 3; i++) Game.CorruptingFeverOnLifeSpentLocked(player, 15, lifeSpender.Id, clock.At);
            clock.At += 3; // 8s after initial cast (would have expired at 6s without refresh), still active!
            await Game.TickInstantBuffSkills(player, clock.At);
            Assert.True(Game.CorruptingFeverActiveLocked(player, clock.At));
            clock.At += 10;
            await Game.TickInstantBuffSkills(player, clock.At);
            Assert.False(Game.CorruptingFeverActiveLocked(player, clock.At));

            // 5. Plague Bearer: Off -> Incubating (-20% poison damage while storing), stores 40% of poison damage up to cap,
            // toggling switches to Infecting which drains stored plague to deal chaos DoT and returns to Incubating at 0.
            fixture.Equip(("Plague Bearer", 1));
            var plague = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "PlagueBearer");
            player.PlayerMana = 1000;
            Assert.True(await ManualCastInstant(player, plague.Id, origin));
            Assert.Equal(Game.PlagueBearerMode.Incubating, Game.PlagueBearerStatus(player).Mode);
            Assert.Equal(-20, Game.CharacterPlayerStats(player).GetValueOrDefault(Stat.CorrosiveShroudPoisonDamagePlusPctFinalWhileAccumulatingPoison));
            // Cannot infect at 0 stored plague.
            clock.At += 1;
            Assert.False(await ManualCastInstant(player, plague.Id, origin));
            // Store poison damage up to level 1 max (4000).
            Game.PlagueBearerOnPoisonInflictedLocked(player, perSecond: 5000, seconds: 2.5, count: 1);
            var pbFull = Game.PlagueBearerStatus(player);
            Assert.Equal(pbFull.MaxStored, pbFull.Stored);
            // At max stored plague, -20% poison penalty is lifted!
            Assert.Equal(0, Game.CharacterPlayerStats(player).GetValueOrDefault(Stat.CorrosiveShroudPoisonDamagePlusPctFinalWhileAccumulatingPoison));
            // Toggle to Infecting -> deals chaos DoT to nearby monster and drains back to Incubating.
            var pbTarget = new SpawnedMonster("plague dummy", new(108, 100, 0), 0, 0, 50_000);
            player.Area.Monsters[803] = pbTarget;
            Assert.True(await ManualCastInstant(player, plague.Id, origin));
            Assert.Equal(Game.PlagueBearerMode.Infecting, Game.PlagueBearerStatus(player).Mode);
            uint pbLifeBefore = pbTarget.Life;
            for (int i = 0; i < 10; i++)
            {
                clock.At += 1;
                await Game.TickInstantBuffSkills(player, clock.At);
            }
            Assert.True(pbTarget.Life < pbLifeBefore);
            Assert.Equal(Game.PlagueBearerMode.Incubating, Game.PlagueBearerStatus(player).Mode);
            Assert.Equal(0, Game.PlagueBearerStatus(player).Stored);
            await Drain(fixture.Session);
        }
        finally
        {
            player.Closed = true;
            clock.Release();
            Game.AreaUpdateContext.Value = previous;
        }
    }

    [Fact]
    public async Task Automation_links_and_triggers_all_eligible_instant_spells_end_to_end()
    {
        var clock = new ManualClock(100);
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        var origin = (X: 100, Y: 100);
        player.Walk.Teleport(origin.X, origin.Y, clock.At);
        player.ProjectileRuntimeState = fixture.Session;
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            async Task ActivateAutomation(int expectedLinked)
            {
                Game.ClearCooldownTriggersLocked(player);
                var auto = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "Automation");
                var record = Game.PlayerSkills(player).Single(s => s.Id == auto.Id);
                Assert.Equal(expectedLinked, record.Stats!.First(s => s.Key == Stat.MetaSkillLinkedSkillCount).Value);
                Assert.True(Game.TryBeginSkillCooldownUse(player, auto.Id, auto.Effect,
                    Game.CooldownForSkill(player, auto.Id), instant: true, () => { }, out var ticket));
                Assert.True(await Game.CastGemSkill(player, auto.Id, origin, origin, cooldownUse: ticket));
                Assert.NotNull(player.Area.Buffs.Get(player.Id, 2437, player.Id, tag: auto.Id));
            }

            // Group 1: Arcane Cloak, Withering Step, Berserk, Corrupting Fever, Plague Bearer linked with Automation.
            fixture.Equip(("Automation", 1), ("Arcane Cloak", 1), ("Withering Step", 1),
                ("Berserk", 1), ("Corrupting Fever", 1), ("Plague Bearer", 1));
            // Verify 150% cost multiplier and cooldown recovery penalty from SupportAutomation on linked spell (Berserk).
            var autoBerserk = Game.GemSkillEffects(player).Single(g => Program.Skills.EffectId(g.Effect) == "Berserk");
            var autoBerserkStats = Game.SupportedStats(player, autoBerserk.Id);
            Assert.Equal(15, autoBerserkStats[Stat.ManaCost]); // Base 10 * 150% = 15
            Assert.True(Game.CooldownForSkill(player, autoBerserk.Id)!.Value.RecoveryPercent < 0); // -24% at L1
            await ActivateAutomation(expectedLinked: 5);

            var enemy = new SpawnedMonster("auto target", new(110, 100, 0), 0, 0, 100_000);
            player.Area.Monsters[901] = enemy;
            player.PlayerMana = 2000;
            player.Pools = player.Pools with { Life = 2000 };
            player.PlayerLife = 2000;
            player.Rage.Gain(15, clock.At, new RageState.Inputs());

            await Game.TickCooldownTriggers(player, clock.At);
            await Drain(fixture.Session);
            Assert.NotNull(player.Area.Buffs.Get(player.Id, 1484, player.Id)); // Arcane Cloak triggered
            Assert.NotNull(player.Area.Buffs.Get(player.Id, Game.WitheringStepBuffRow, player.Id)); // Withering Step triggered
            Assert.Equal(4, enemy.Withered.Count);
            Assert.NotNull(player.Area.Buffs.Get(player.Id, Game.BerserkBuffRow, player.Id)); // Berserk triggered
            Assert.NotNull(player.Area.Buffs.Get(player.Id, Game.CorruptingFeverBuffRow, player.Id)); // Corrupting Fever triggered
            Assert.Equal(Game.PlagueBearerMode.Incubating, Game.PlagueBearerStatus(player).Mode); // Plague Bearer activated

            // Fill Plague Bearer to cap -> next Automation tick switches it to Infecting and does not prematurely cancel it!
            Game.PlagueBearerOnPoisonInflictedLocked(player, perSecond: 5000, seconds: 2.5, count: 1);
            clock.At += 2;
            await Game.TickCooldownTriggers(player, clock.At);
            await Drain(fixture.Session);
            Assert.Equal(Game.PlagueBearerMode.Infecting, Game.PlagueBearerStatus(player).Mode);
            clock.At += 2;
            await Game.TickCooldownTriggers(player, clock.At);
            await Drain(fixture.Session);
            Assert.Equal(Game.PlagueBearerMode.Infecting, Game.PlagueBearerStatus(player).Mode);

            // Clean up active buffs before testing group 2.
            Game.ClearInstantBuffSkillsLocked(player);
            clock.At += 10;

            // Group 2: Frostblink, War Banner, Predator Support (Signal Prey), Bladefall of Trarthus.
            fixture.Equip(("Automation", 1), ("Frostblink", 1), ("War Banner", 1),
                ("Predator Support", 1), ("Bladefall of Trarthus", 1));
            await ActivateAutomation(expectedLinked: 4);

            // Without Valour, War Banner does not place; grant Valour via melee kill so Automation places it cleanly.
            var valourDummy = new SpawnedMonster("valour dummy", new(105, 100, 0), 0, 0, 1000);
            Game.GainValourFromMeleeLocked(player, valourDummy, killed: true, clock.At, Game.CharacterPlayerStats(player));
            uint enemyLifeBefore = enemy.Life;
            player.PlayerMana = 2000;
            await Game.TickCooldownTriggers(player, clock.At);
            await Drain(fixture.Session);

            Assert.True(enemy.Life < enemyLifeBefore); // Frostblink area cold hit landed
            Assert.NotNull(player.Area.Buffs.Get(player.Id, 2482, player.Id)); // War Banner placed
            Assert.NotNull(player.Summons.SignalledPrey); // Signal Prey marked nearest enemy
            Assert.Equal(901u, player.Summons.SignalledPrey!.Id);
            Assert.NotNull(player.BladefallTrarthus); // Bladefall of Trarthus activated

            // Next tick after cooldown: Bladefall of Trarthus stays ON and placed War Banner is NOT toggled off!
            clock.At += 5;
            await Game.TickCooldownTriggers(player, clock.At);
            await Drain(fixture.Session);
            Assert.NotNull(player.Area.Buffs.Get(player.Id, 2482, player.Id));
            Assert.NotNull(player.BladefallTrarthus);
            await Game.EndBladefallTrarthus(player, "test cleanup");

            // Group 3: Dread Banner + Defiance Banner + existing Steelskin/Blood Rage/Phase Run/Righteous Fire/Convocation/Brand Recall/Detonate Mines all link cleanly.
            fixture.Equip(("Automation", 1), ("Dread Banner", 1), ("Defiance Banner", 1), ("Steelskin", 1), ("Blood Rage", 1), ("Righteous Fire", 1));
            await ActivateAutomation(expectedLinked: 5);
            // Area transfer carry & restore preserves active Automation controller cleanly.
            Game.CarryCooldownTriggers(player, player.Ticket);
            Assert.True(Game.ClearCooldownTriggersLocked(player));
            Game.RestoreCooldownTriggers(player);
            clock.At += 10;
            player.PlayerMana = 2000;
            player.PlayerLife = 2000;
            await Game.TickCooldownTriggers(player, clock.At);
            await Drain(fixture.Session);
            Assert.NotNull(player.Area.Buffs.Get(player.Id, 1302, player.Id)); // Steelskin triggered after restore
            Assert.NotNull(player.RighteousFire); // Righteous Fire triggered after restore
            await Game.EndRighteousFire(player, "test cleanup", player.RighteousFire);
        }
        finally
        {
            player.Closed = true;
            clock.Release();
            Game.AreaUpdateContext.Value = previous;
        }
    }
}
