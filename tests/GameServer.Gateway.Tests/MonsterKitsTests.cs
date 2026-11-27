using GameServer.Core;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>
/// S4 uncapped ordinary multi-skill kits, data-driven <see cref="MonsterKitSkill.MinRange"/> /
/// <see cref="MonsterKitSkill.CooldownGroup"/>, zero-alloc <c>BossKits.Choose</c>, cached <c>GrantedSkill</c> reuse,
/// and S6 <see cref="MonsterKitHooks"/> registration &amp; hot-reload disposal.
/// </summary>
public sealed class MonsterKitsTests
{
    [Fact]
    public void Ordinary_uncapped_kits_min_range_cooldown_group_cached_skills_and_aggro_gated_choose()
    {
        using var f = new MinionUtilityTests.Fixture();
        var player = f.Player;
        var catalog = Game.SkillsFor(player);

        // 1. Cap lift (>5 skills on ordinary non-boss varieties) + H4 cached BaseSkills reuse for unmodified Normal spawns.
        Assert.True(MonsterKits.MaxOrdinaryKitSkills >= 16);
        MonsterCatalog.Variety? uncapped = null;
        MonsterKits.CachedKit? uncappedTemplate = null;
        foreach (var v in Game.Monsters.All)
        {
            if (!v.Supported || v.BossHealthBar || BossKits.IsCampaignBoss(v) || Game.IsBossControllerVariety(v)) continue;
            if (v.GrantedEffects is not { Count: > 5 }) continue;
            var tpl = MonsterKits.TemplateFor(player, v, 70);
            if (tpl.BaseKit.Count > 5)
            {
                uncapped = v;
                uncappedTemplate = tpl;
                break;
            }
        }
        Assert.NotNull(uncapped);
        Assert.NotNull(uncappedTemplate);
        Assert.InRange(uncappedTemplate!.BaseKit.Count, 6, MonsterKits.MaxOrdinaryKitSkills);
        Assert.NotNull(uncappedTemplate.BaseSkills);
        Assert.Equal(uncappedTemplate.BaseKit.Count, uncappedTemplate.BaseSkills!.Count);

        var (ordKit, _, _) = MonsterKits.ForOrdinary(player, uncapped!, 70, -1, MonsterModEffects.None, applyAuraStats: false);
        Assert.Same(uncappedTemplate.BaseKit, ordKit);
        var reusedSkills = MonsterKits.SkillsForOrdinary(player, uncapped!, 70, ordKit);
        Assert.Same(uncappedTemplate.BaseSkills, reusedSkills);
        var (dmgModKit, _, _) = MonsterKits.ForOrdinary(player, uncapped!, 70, -1, MonsterModEffects.None, mapBossDamage: 2.0, applyAuraStats: false);
        if (uncappedTemplate.BaseKit.Any(k => !k.Attack.Spell && !k.Attack.Damage.IsZero))
            Assert.NotSame(uncappedTemplate.BaseSkills, MonsterKits.SkillsForOrdinary(player, uncapped!, 70, dmgModKit));

        // 2. Animation == NULL primary skills (e.g. Melee / MonsterFireball) are NOT dropped by S4 exclusion rules.
        var solarisCaster = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Axis/AxisCasterSolarisChampion"));
        int fireball3 = catalog.EffectRow("MonsterFireball3Parallel");
        Assert.Null(SkillCatalog.EffectAnimationName(fireball3));
        var casterTpl = MonsterKits.TemplateFor(player, solarisCaster, 60);
        Assert.Contains(casterTpl.BaseKit, k => k.Effect == fireball3);

        // 3. MinRange + CooldownGroup + zero-alloc Choose(player, id, m, now, d) parity & rotation.
        var walk = new PlayerWalk(100, 100, 15, (_, _) => true);
        var atkMelee = new MonsterAttack(0x4000, 1.0, 12, DamagePacket.Of(DamageType.Physical, 5, 10));
        var atkLong = new MonsterAttack(0x4001, 1.0, 45, DamagePacket.Of(DamageType.Fire, 15, 25)) { Ranged = true, Spell = true };
        var atkSharedA = new MonsterAttack(0x4002, 1.0, 40, DamagePacket.Of(DamageType.Cold, 20, 30)) { Ranged = true, Spell = true };
        var atkSharedB = new MonsterAttack(0x4003, 1.0, 40, DamagePacket.Of(DamageType.Lightning, 20, 30)) { Ranged = true, Spell = true };
        var kit = new MonsterKitSkill[]
        {
            new(1001, atkMelee, 0),
            new(1002, atkLong, 5.0) { MinRange = 20 },
            new(1003, atkSharedA, 6.0) { CooldownGroup = 7 },
            new(1004, atkSharedB, 6.0) { CooldownGroup = 7 },
        };
        var monster = new SpawnedMonster("test-kit", walk, aggroRange: 50, stopRange: 6, maxLife: 100)
        {
            Kit = kit,
            Attack = atkMelee,
        };
        player.Area.Monsters.Add(0x5001, monster);

        // At distance 10 (< MinRange 20), atkLong is gated out; atkSharedA (special, tier 1) wins.
        var pick1 = BossKits.Choose(player, 0x5001, monster, now: 10.0, distance: 10.0, cachedAreaLists: false);
        Assert.Equal(atkSharedA.SkillId, pick1?.SkillId);

        // Consuming atkSharedA puts CooldownGroup 7 on cooldown -> atkSharedB is also blocked until 16.0!
        BossKits.Consume(monster, atkSharedA, now: 10.0);
        Assert.False(BossKits.Ready(monster, atkSharedB, now: 12.0));
        Assert.True(BossKits.Ready(monster, atkSharedB, now: 16.0));

        // At distance 10 while group 7 is on cooldown and atkLong is inside MinRange, melee (plain) wins.
        var pick2 = BossKits.Choose(player, 0x5001, monster, now: 11.0, distance: 10.0, cachedAreaLists: false);
        Assert.Equal(atkMelee.SkillId, pick2?.SkillId);
        // Zero-alloc overload matches Func<> overload.
        var pick2Legacy = BossKits.Choose(monster, now: 11.0, distance: 10.0, a => MonsterSkills.Usable(player, 0x5001, monster, a, 11.0));
        Assert.Equal(pick2?.SkillId, pick2Legacy?.SkillId);

        // After plain melee at t=11, at distance 30 (>= MinRange 20) atkLong becomes eligible and wins!
        BossKits.Consume(monster, atkMelee, now: 11.0);
        var pick3 = BossKits.Choose(player, 0x5001, monster, now: 12.0, distance: 30.0, cachedAreaLists: false);
        Assert.Equal(atkLong.SkillId, pick3?.SkillId);

        // 4. H1 aggro-gate: an idle monster outside notice range keeps its initial Attack in PlanMonsterAi.
        var idleWalk = new PlayerWalk(player.Walk.Position(12.0).X + 90, player.Walk.Position(12.0).Y, 15, (_, _) => true);
        var idleMonster = new SpawnedMonster("idle-kit", idleWalk, aggroRange: 30, stopRange: 6, maxLife: 100)
        {
            Kit = [new(1001, atkMelee, 0), new(1003, atkSharedA with { Range = 120 }, 6.0)],
            Attack = atkMelee,
            Aggro = false,
        };
        player.Area.Monsters.Add(0x5002, idleMonster);
        var cadence = new MonsterAiCadence();
        Game.PlanMonsterAi(player, cadence, 12.5);
        Assert.False(idleMonster.Aggro);
        Assert.Equal(atkMelee.SkillId, idleMonster.Attack?.SkillId);
    }

    [Fact]
    public void MonsterKitHooks_filter_combo_binding_state_and_observer_detach_cleanly_on_dispose()
    {
        using var f = new MinionUtilityTests.Fixture();
        var player = f.Player;
        var catalog = Game.SkillsFor(player);

        var solarisCaster = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Axis/AxisCasterSolarisChampion"));
        int fireball3 = catalog.EffectRow("MonsterFireball3Parallel");
        var daresso = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/Daresso/DaressoBoss"));
        var daressoEffectsBefore = BossKits.UniqueEffects(catalog, daresso);
        int suppressedBossEffect = daressoEffectsBefore.First(e => !SkillCatalog.IsEmptyAction(e));

        // 1. EffectFilter suppresses effects in BOTH MonsterKits and BossKits, invalidating cache on add & dispose.
        Assert.Contains(MonsterKits.TemplateFor(player, solarisCaster, 60).BaseKit, k => k.Effect == fireball3);
        Assert.Contains(suppressedBossEffect, daressoEffectsBefore);

        using (var filterToken = MonsterKitHooks.AddEffectFilter((v, eff, _) =>
            !(v.Row == solarisCaster.Row && eff == fireball3) && !(v.Row == daresso.Row && eff == suppressedBossEffect)))
        {
            Assert.True(MonsterKitHooks.HasEffectFilters);
            Assert.DoesNotContain(MonsterKits.TemplateFor(player, solarisCaster, 60).BaseKit, k => k.Effect == fireball3);
            Assert.DoesNotContain(suppressedBossEffect, BossKits.UniqueEffects(catalog, daresso));
        }

        Assert.False(MonsterKitHooks.HasEffectFilters);
        Assert.Contains(MonsterKits.TemplateFor(player, solarisCaster, 60).BaseKit, k => k.Effect == fireball3);
        Assert.Contains(suppressedBossEffect, BossKits.UniqueEffects(catalog, daresso));

        // 2. ComboBindingProvider overrides an EmptyAction* parent binding and cleans up on Dispose.
        int reaveParent = catalog.EffectRow("EmptyActionAttackDaressoReave");
        int customChild = catalog.EffectRow("DaressoReaveGeometryAttack4");
        using (var comboToken = MonsterKitHooks.AddComboBindings((v, parentEff) =>
            v.Row == daresso.Row && parentEff == reaveParent
                ? [new MonsterKitHooks.ComboEventBinding("reave", customChild, MonsterComboDispatch.TriggeredAction)]
                : null))
        {
            Assert.True(MonsterKitHooks.HasComboProviders);
            var overridden = MonsterCombos.For(catalog, daresso);
            Assert.True(overridden.Parents.TryGetValue(reaveParent, out var parent));
            Assert.All(parent!.Events, e => Assert.Equal(customChild, e.ChildEffect));
        }
        Assert.False(MonsterKitHooks.HasComboProviders);
        // Restored back to Tier-2 ordinal 1..4 series!
        var restored = MonsterCombos.For(catalog, daresso);
        Assert.Equal(
            ["DaressoReaveGeometryAttack1", "DaressoReaveGeometryAttack2", "DaressoReaveGeometryAttack3", "DaressoReaveGeometryAttack4"],
            restored.Parents[reaveParent].Events.Select(e => catalog.EffectId(e.ChildEffect)));

        // 3. ClipEventObserver + StateTransitionProvider fire and detach cleanly.
        var observed = new List<(int Effect, string Event)>();
        using (var obsToken = MonsterKitHooks.AddClipEventObserver((_, _, _, eff, ev, _) => observed.Add((eff, ev))))
        using (var stateToken = MonsterKitHooks.AddStateTransitions((_, cur) => cur))
        {
            Assert.True(MonsterKitHooks.HasClipObservers);
            Assert.True(MonsterKitHooks.HasStateProviders);
            var dummy = new SpawnedMonster("obs", new PlayerWalk(0, 0, 10, (_, _) => true), 30, 5, 10);
            MonsterKitHooks.NotifyClipEvent(player, 1u, dummy, reaveParent, "reave", 1.0);
            Assert.Single(observed);
            Assert.Equal((reaveParent, "reave"), observed[0]);
            Assert.Equal(daresso.StateMachine, MonsterKitHooks.ApplyStateTransitions(daresso, daresso.StateMachine));
        }
        Assert.False(MonsterKitHooks.HasClipObservers);
        Assert.False(MonsterKitHooks.HasStateProviders);
    }
}
