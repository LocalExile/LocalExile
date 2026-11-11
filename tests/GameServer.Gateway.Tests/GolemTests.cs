using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class GolemTests
{
    [Fact]
    public void AllPlayableGolemsHaveNativeSkillsAndScaleAtTheirActorLevel()
    {
        var catalog = new GolemCatalog(); var skills = new SkillCatalog(); var monsters = new MonsterCatalog();
        Assert.Equal(17, catalog.All.Count);
        Assert.Null(catalog.Find("SummonLightningGolemAltY"));
        var conn = new ClientState(new(1, "golem-catalog", 1000, 1000, 100, 100, null, []), 1, 1);
        foreach (var definition in catalog.All)
        {
            var variety = monsters.Find(definition.Variety);
            Assert.NotNull(variety); Assert.True(variety.Supported);
            var own = skills.EffectStats(definition.Effect, 20, 20).ToDictionary(p => p.Item1, p => p.Item2);
            int Get(string name) => own.GetValueOrDefault(Program.Stats.Key(name)!.Value);
            Assert.Equal(70, Get("base_display_minion_actor_level"));
            Assert.Equal(definition.Hordes ? 3 : 1, Get("base_number_of_golems_allowed"));
            Assert.Equal(definition.Hordes || definition.Special && definition.Kind != "Stone", definition.Buff == 0);
            var golem = new GolemState.Golem(1, null!, definition, 1) { Own = own };
            var abilities = Game.GolemAbilities(conn, variety, 70, golem, new Dictionary<uint, int>());
            var native = Game.GolemNativeSkills(conn, 70, abilities);
            Assert.Equal(variety.GrantedEffects!.Count, abilities.Count);
            Assert.Equal(abilities.Count, native.Count);
            Assert.Equal(abilities.Count, abilities.Select(a => a.Skill).Distinct().Count());
            Assert.Contains(abilities, a => a.Attack.Damage.MaxTotal > 0);
            Assert.All(abilities, a => Assert.True(a.Attack.Seconds > 0));
            foreach (var (source, target) in definition.BuffStats)
            { Assert.NotNull(Program.Stats.Key(source)); Assert.NotNull(Program.Stats.Key(target)); }
        }
        // One-row monster spells must use actor level, not leave the row's level-3 values in a level-70 golem.
        var low = catalog.Skill(skills, 5025, 34); var high = catalog.Skill(skills, 5025, 70);
        uint cold = Program.Stats.Key("spell_minimum_base_cold_damage")!.Value;
        Assert.True(high.Stats[cold] > low.Stats[cold] * 3);
        var cone = catalog.Skill(skills, 5056, 34);
        Assert.Equal(1, cone.Level);
        Assert.Equal(133, cone.Stats[Program.Stats.Key("spell_minimum_base_fire_damage")!.Value]);
        Assert.Equal(20, catalog.Skill(skills, 5056, 70).Level);
        Assert.Equal(522, catalog.Skill(skills, 4336, 3).Stats[Program.Stats.Key("base_chaos_damage_to_deal_per_minute")!.Value]);
        var flame = catalog.Find("SummonFlameGolem")!;
        var minion = new GolemState.Golem(1, null!, flame, 1);
        var varietyFlame = monsters.Find(flame.Variety)!;
        var baseline = Game.GolemAbilities(conn, varietyFlame, 70, minion, new Dictionary<uint, int>());
        foreach (string kind in new[] { "global", "attack", "spell" })
            foreach (string bound in new[] { "minimum", "maximum" })
                minion.Own[Program.Stats.Key($"{kind}_{bound}_added_fire_damage")!.Value] = 100;
        var added = Game.GolemAbilities(conn, varietyFlame, 70, minion, new Dictionary<uint, int>());
        Assert.All(added, a => Assert.Equal(baseline.Single(b => b.Effect == a.Effect).Attack.Damage[DamageType.Fire].Min + 100,
            a.Attack.Damage[DamageType.Fire].Min));
        Assert.Equal(3, GolemCatalog.Limit(1, n => n == "number_of_golems_allowed" ? 2 : 0));
        Assert.Equal(4, GolemCatalog.Limit(1, n => n == "base_number_of_golems_allowed" ? 3 : 0));
        Assert.Equal(8, GolemCatalog.Limit(3, n => n switch { "number_of_golems_allowed" => 1, "double_golem_limit" => 1, _ => 0 }));
        var stone = catalog.Find("SummonStoneGolem")!;
        Assert.Equal(70, GolemCatalog.BuffEffect(stone, n => n == "golem_buff_effect_+%" ? 50 : 0,
            n => n == "golem_buff_effect_+%" ? 20 : 0, 1));
        // Recovery buffs have conditional fields absent from ordinary golem buff packets.
        var safeguarding = new GolemState.Golem(0x4000000, null!, catalog.Find("SummonStoneGolemAltY")!, 1);
        safeguarding.OwnerBuff[Program.Stats.Key("melee_damage_removed_from_stone_golem_before_life_or_es_%")!.Value] = 18;
        byte[] guard = Game.GolemOwnerBuffPacket(7, 592, safeguarding);
        Assert.Equal(33, guard.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 18, 0, 0, 0, 0 }, guard[23..]);
        byte[] after = InstanceProtocol.LifeUpdate(0x1000, 100);
        Assert.Equal(after, ((byte[])[.. guard, .. after])[33..]);
    }

    [Fact]
    public async Task GolemsReplaceWithoutStackingBuffsTakeDamageReviveAndLoseTheirSource()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-golems-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull; var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        using var wire = new Wire();
        Saves.Open(path); Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "golem-runtime", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "GolemRuntime", Instance = new(), PlayerLife = 10000, PlayerMana = 10000, PlayerSent = true,
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("golem-test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        conn.Golems.Restored = true;
        foreach (string stat in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
            conn.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;

        async Task PrimordialMightOwnerWindow()
        {
            const string condition = "_if_golem_summoned_in_past_8_seconds";
            const string ownerModId = "IncreasedDamageIfGolemSummonedRecently__1";
            string character = "PrimordialOwner" + Guid.NewGuid().ToString("N");
            var schedule = new AreaClockSchedule();
            var updates = new PlayerUpdatesState(lifetime, schedule);
            var previousAreaUpdateContext = global::Game.AreaUpdateContext.Value;
            global::Game.AreaUpdateContext.Value = updates;
            ClientState? owner = null;
            try
            {
                owner = new ClientState(new(1, "primordial-owner", 1000, 1000, 100, 100, null, []), 3, 1)
                {
                    Character = character, Instance = new(), PlayerLife = 10000, PlayerMana = 10000, PlayerSent = true,
                    Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = updates,
                    Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    LeagueModule = conn.LeagueModule,
                };
                owner.Golems.Restored = true;
                foreach (string stat in new[] { "virtual_strength_for_requirements",
                             "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                    owner.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;

                var might = new UniqueCatalog(Program.Items).Create("Primordial Might", new Random(504))!;
                var ownerRoll = might.Mods.Single(mod => Program.Items.Mods.ByHash(mod.Hash)?.Id == ownerModId);
                var golemRoll = might.Mods.Single(mod =>
                    Program.Items.Mods.ByHash(mod.Hash)?.Id == "IncreasedGolemDamageIfGolemSummonedRecently__1_");
                int coefficient = ownerRoll.Values[0];
                int golemCoefficient = golemRoll.Values[0];
                Assert.InRange(coefficient, 25, 30);
                Assert.InRange(golemCoefficient, 35, 45);
                Assert.Contains(Program.Items.Mods.ByHash(ownerRoll.Hash)!.Stats,
                    stat => stat.Id == "damage_+%" + condition);

                var inventory = global::Game.ItemsOf(owner);
                var jewelNode = Program.Passives.JewelNodeForSlot(0)!.Value;
                owner.Passives.Add(jewelNode);
                var wornMight = inventory.Add(PlayerInventories.PassiveJewels, 0, 0, might);
                var golemGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSummonRockGolem")!);
                var fireballGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemFireball")!);
                var weapon = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with
                {
                    Sockets = [new(SocketColour.White, golemGem), new(SocketColour.White, fireballGem)],
                    Links = [2],
                };
                inventory.Add(PlayerInventories.Weapon1, 0, 0, weapon);
                void Recalculate()
                {
                    global::Game.EquipmentStatSources(owner, inventory.EquipmentSnapshot());
                    owner.Pools = global::Game.PoolsFor(owner, 100);
                    owner.PlayerLife = owner.Pools.Life;
                    owner.PlayerMana = owner.Pools.Mana;
                }
                Recalculate();

                var golemSource = Assert.Single(global::Game.GemSkillEffects(owner),
                    grant => global::Game.SkillsFor(owner).EffectId(grant.Effect) == "SummonStoneGolem");
                var golemDefinition = global::Game.GolemData.Find("SummonStoneGolem")!;
                Assert.Equal("SummonStoneGolem", golemDefinition.Id);
                var fireball = Assert.Single(global::Game.GemSkillEffects(owner),
                    grant => global::Game.SkillsFor(owner).EffectId(grant.Effect) == "Fireball");
                int Stat(IReadOnlyDictionary<uint, int> stats, string name) =>
                    stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
                int OwnerDamage() => Stat(global::Game.CharacterPlayerStats(owner), "damage_+%");
                double Hit() => global::Game.SupportedHit(owner, fireball.Id).MinTotal;
                bool Window() => global::Game.ItemConditionFlags(owner, owner.Clock.Elapsed.TotalSeconds)[condition];
                GolemState.Golem CurrentGolem()
                {
                    lock (owner.Monsters)
                    {
                        var current = Assert.Single(owner.Golems.All).Value;
                        Assert.False(current.Monster.Dead);
                        Assert.True(current.Monster.Life > 0);
                        return current;
                    }
                }
                static double GolemDamage(GolemState.Golem golem, int effect) =>
                    golem.Abilities.Single(ability => ability.Effect == effect).Attack.Damage.MinTotal;
                int NativeDamage(GolemState.Golem golem, int effect, string bound)
                {
                    var ability = golem.Abilities.Single(candidate => candidate.Effect == effect);
                    var component = Assert.Single(ability.Attack.Damage.Components, part => part.Range.Max > 0);
                    string kind = ability.Attack.Spell ? "spell" : "attack";
                    string slug = component.Type.ToString().ToLowerInvariant();
                    uint key;
                    if (ability.Attack.Spell)
                        key = Program.Stats.Key($"{kind}_{bound}_base_{slug}_damage")!.Value;
                    else
                    {
                        Assert.Equal(DamageType.Physical, component.Type);
                        key = bound == "minimum" ? StatKeys.MainHandMinPhysical : StatKeys.MainHandMaxPhysical;
                    }
                    var skill = golem.Skills.Single(candidate => candidate.Id == ability.Skill);
                    return Assert.Single(skill.Stats!, stat => stat.Key == key).Value;
                }

                schedule.At = 6;
                using var statsScope = global::Game.PlayerStatsScope(owner);
                int beforeOwner = OwnerDamage();
                double beforeHit = Hit();
                Assert.False(Window());
                Assert.Equal(double.NegativeInfinity, owner.Golems.LastSummoned);

                uint failed = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    golemSource.Id, golemDefinition, (100, 100), current: () => false);
                Assert.Equal(0u, failed);
                Assert.Equal(double.NegativeInfinity, owner.Golems.LastSummoned);
                Assert.False(Window());

                uint summoned = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    golemSource.Id, golemDefinition, (100, 100));
                Assert.NotEqual(0u, summoned);
                Assert.Equal(6, owner.Golems.LastSummoned);
                Assert.True(Window());
                int activeOwner = OwnerDamage();
                double activeHit = Hit();
                Assert.Equal(coefficient, activeOwner - beforeOwner);
                Assert.True(activeHit > beforeHit);
                var activeGolem = CurrentGolem();
                var recentAbility = Assert.Single(activeGolem.Abilities, ability => ability.Effect == 136);
                double activeGolemDamage = recentAbility.Attack.Damage.MinTotal;
                Assert.True(activeGolemDamage > 0);
                Assert.Equal(activeOwner, OwnerDamage());
                Assert.Equal(activeHit, Hit());
                Assert.Equal(6, owner.Golems.LastSummoned);

                var sourcesWithMight = owner.StatSourcesCache;
                Assert.NotNull(sourcesWithMight);
                uint damageKey = Program.Stats.Key("damage_+%")!.Value;
                Assert.Equal(coefficient, sourcesWithMight.Sources.GetValueOrDefault(damageKey));
                inventory.Remove(PlayerInventories.PassiveJewels, wornMight.Id);
                global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, owner, inventory).GetAwaiter().GetResult();
                Assert.True(Window());
                Assert.DoesNotContain(inventory.Items(PlayerInventories.PassiveJewels), item => item.Id == wornMight.Id);
                var sourcesWithoutMight = owner.StatSourcesCache;
                Assert.NotNull(sourcesWithoutMight);
                Assert.NotSame(sourcesWithMight, sourcesWithoutMight);
                Assert.Equal(0, sourcesWithoutMight.Sources.GetValueOrDefault(damageKey));
                int sourceOffOwner = OwnerDamage();
                double sourceOffHit = Hit();
                Assert.Equal(coefficient, activeOwner - sourceOffOwner);
                Assert.Equal(beforeOwner, sourceOffOwner);
                Assert.Equal(beforeHit, sourceOffHit);
                Assert.True(activeHit > sourceOffHit);
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(activeGolem, CurrentGolem());
                double sourceOffGolemDamage = GolemDamage(activeGolem, 136);
                Assert.True(activeGolemDamage > sourceOffGolemDamage);

                inventory.Add(PlayerInventories.PassiveJewels, 0, 0, might);
                global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, owner, inventory).GetAwaiter().GetResult();
                Assert.True(Window());
                Assert.Equal(activeOwner, OwnerDamage());
                Assert.Equal(activeHit, Hit());
                schedule.At = 6.5;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(activeGolem, CurrentGolem());
                Assert.Equal(activeGolemDamage, GolemDamage(activeGolem, 136));
                Assert.Equal(6, owner.Golems.LastSummoned);

                lock (owner.Monsters)
                {
                    var golem = Assert.Single(owner.Golems.All).Value;
                    golem.Monster.Dead = true;
                    golem.Monster.Life = 0;
                    owner.Golems.All.Remove(golem.Id);
                    owner.Minions.Remove(golem.Id);
                    owner.Monsters.Remove(golem.Id);
                    owner.MinionOrigins.TryRemove(golem.Id, out _);
                }
                Assert.True(Window());

                schedule.At = 13.999;
                Assert.True(Window());
                Assert.Equal(activeOwner, OwnerDamage());
                Assert.Equal(activeHit, Hit());
                schedule.At = 14;
                Assert.False(Window());
                Assert.Equal(sourceOffOwner, OwnerDamage());
                Assert.Equal(sourceOffHit, Hit());
                Assert.Equal(beforeOwner, OwnerDamage());
                Assert.Equal(beforeHit, Hit());

                uint restored = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    golemSource.Id, golemDefinition, (100, 100), arrival: true);
                Assert.NotEqual(0u, restored);
                Assert.Equal(6, owner.Golems.LastSummoned);
                Assert.False(Window());
                Assert.Equal(sourceOffOwner, OwnerDamage());
                Assert.Equal(sourceOffHit, Hit());
                var expiredArrival = CurrentGolem();
                Assert.Equal(6, expiredArrival.SummonedAt);
                Assert.Equal(14, expiredArrival.LastStep);
                Assert.Equal(sourceOffGolemDamage, GolemDamage(expiredArrival, 136));

                uint partlyAged = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    golemSource.Id, golemDefinition, (100, 100), arrival: true, summonedSecondsRemaining: 4);
                Assert.NotEqual(0u, partlyAged);
                var agedArrival = CurrentGolem();
                Assert.Equal(10, agedArrival.SummonedAt);
                Assert.Equal(14, agedArrival.LastStep);
                Assert.Equal(golemCoefficient, global::Game.GolemRecentDamageIncrease(agedArrival, schedule.At));
                Assert.True(GolemDamage(agedArrival, 136) > GolemDamage(expiredArrival, 136));
                Assert.True(NativeDamage(agedArrival, 136, "minimum") > NativeDamage(expiredArrival, 136, "minimum"));
                Assert.True(NativeDamage(agedArrival, 136, "maximum") > NativeDamage(expiredArrival, 136, "maximum"));

                foreach (double invalidRemaining in new[] { double.NaN, double.PositiveInfinity })
                {
                    schedule.At = 15;
                    uint invalidArrival = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                        golemSource.Id, golemDefinition, (100, 100), arrival: true,
                        summonedSecondsRemaining: invalidRemaining);
                    Assert.NotEqual(0u, invalidArrival);
                    var invalid = CurrentGolem();
                    Assert.Equal(7, invalid.SummonedAt);
                    Assert.Equal(15, invalid.LastStep);
                    Assert.False(global::Game.GolemSummonedInPastEightSeconds(invalid, schedule.At));
                    Assert.Equal(sourceOffGolemDamage, GolemDamage(invalid, 136));
                }

                schedule.At = 20;
                uint refreshed = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    golemSource.Id, golemDefinition, (100, 100));
                Assert.NotEqual(0u, refreshed);
                Assert.Equal(20, owner.Golems.LastSummoned);
                Assert.True(Window());
                Assert.Equal(coefficient, OwnerDamage() - sourceOffOwner);
                Assert.True(Hit() > sourceOffHit);
                var refreshedGolem = CurrentGolem();
                double refreshedGolemDamage = GolemDamage(refreshedGolem, 136);
                Assert.True(refreshedGolemDamage > sourceOffGolemDamage);
                schedule.At = 27.999;
                Assert.True(Window());
                Assert.Equal(coefficient, OwnerDamage() - sourceOffOwner);
                Assert.True(Hit() > sourceOffHit);
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(refreshedGolem, CurrentGolem());
                Assert.Equal(refreshedGolemDamage, GolemDamage(refreshedGolem, 136));
                schedule.At = 28;
                Assert.False(Window());
                Assert.Equal(sourceOffOwner, OwnerDamage());
                Assert.Equal(sourceOffHit, Hit());
                Assert.Equal(beforeOwner, OwnerDamage());
                Assert.Equal(beforeHit, Hit());
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(refreshedGolem, CurrentGolem());
                Assert.Equal(sourceOffGolemDamage, GolemDamage(refreshedGolem, 136));
            }
            finally
            {
                global::Game.AreaUpdateContext.Value = previousAreaUpdateContext;
                if (owner is not null)
                {
                    owner.Closed = true;
                    PlayerInventories.Forget(character);
                    global::Game.InactiveItems.TryRemove(character, out _);
                }
            }
        }

        async Task PrimordialGolemLifetimeMatrix()
        {
            const string character = "PrimordialGolemLifetime";
            var schedule = new AreaClockSchedule();
            var updates = new PlayerUpdatesState(lifetime, schedule);
            var previousAreaUpdateContext = global::Game.AreaUpdateContext.Value;
            global::Game.AreaUpdateContext.Value = updates;
            ClientState? owner = null;
            ClientState? restored = null;
            ClientState? legacy = null;
            try
            {
                owner = new ClientState(new(1, "primordial-golem-lifetime", 1000, 1000, 100, 100, null, []), 4, 1)
                {
                    Character = character, Instance = new(), PlayerLife = 10000, PlayerMana = 10000, PlayerSent = true,
                    Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = updates,
                    Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    LeagueModule = conn.LeagueModule,
                };
                owner.Golems.Restored = true;
                foreach (string stat in new[] { "virtual_strength_for_requirements",
                             "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                    owner.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
                owner.PlayerStats[Program.Stats.Key("minion_damage_+%")!.Value] = 50;
                owner.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 1;

                var inventory = global::Game.ItemsOf(owner);
                var jewelNode = Program.Passives.JewelNodeForSlot(0)!.Value;
                owner.Passives.Add(jewelNode);
                var normalMight = new UniqueCatalog(Program.Items).Create("Primordial Might", new Random(504))!;
                var replicaMight = new UniqueCatalog(Program.Items).Create("Replica Primordial Might", new Random(504))!;
                var chain = new UniqueCatalog(Program.Items).Create("The Primordial Chain", new Random(504))!;
                string ItemModId(ItemMod mod) => Program.Items.Mods.ByHash(mod.Hash)?.Id ?? "";
                var normalConditional = normalMight.Mods.Single(mod =>
                    ItemModId(mod) == "IncreasedGolemDamageIfGolemSummonedRecently__1_");
                var replicaConditional = replicaMight.Mods.Single(mod =>
                    ItemModId(mod) == "IncreasedGolemDamageIfGolemSummonedRecentlyUnique__1");
                int normalCoefficient = normalConditional.Values[0];
                int replicaCoefficient = replicaConditional.Values[0];
                Assert.InRange(normalCoefficient, 35, 45);
                Assert.InRange(replicaCoefficient, 100, 125);
                Assert.Contains(replicaMight.Mods, mod => ItemModId(mod) == "MaximumGolemsUnique__4_"
                    && mod.Values[0] == -1);
                Assert.Contains(replicaMight.Mods, mod => ItemModId(mod) == "GolemLifeUnique__1");
                Assert.Contains(chain.Mods, mod => ItemModId(mod) == "MaximumGolemsUnique__3"
                    && mod.Values[0] == 3);

                var normalWorn = inventory.Add(PlayerInventories.PassiveJewels, 0, 0, normalMight);
                var stoneGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSummonRockGolem")!);
                var flameGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSummonFlameGolem")!);
                var weapon = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with
                {
                    Sockets = [new(SocketColour.White, stoneGem), new(SocketColour.White, flameGem)],
                    Links = [2],
                };
                inventory.Add(PlayerInventories.Weapon1, 0, 0, weapon);
                void Recalculate()
                {
                    global::Game.EquipmentStatSources(owner, inventory.EquipmentSnapshot());
                    owner.Pools = global::Game.PoolsFor(owner, 100);
                    owner.PlayerLife = owner.Pools.Life;
                    owner.PlayerMana = owner.Pools.Mana;
                }
                Recalculate();
                int Stat(IReadOnlyDictionary<uint, int> stats, string name) =>
                    stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
                GolemState.Ability Ability(GolemState.Golem golem, int effect) =>
                    golem.Abilities.Single(ability => ability.Effect == effect);
                GolemState.Ability ExpiredTwin(GolemState.Golem golem, IReadOnlyDictionary<uint, int> player,
                    double at, int effect)
                {
                    var twin = new GolemState.Golem(0, null!, golem.Definition, golem.Source)
                    {
                        Own = golem.Own, Player = new(player), SummonedAt = at - 8,
                    };
                    return global::Game.GolemAbilities(owner, global::Game.Monsters.Find(golem.Definition.Variety)!,
                        golem.Monster.Level, twin, player, at).Single(ability => ability.Effect == effect);
                }
                int ExpiredIncrease(GolemState.Golem golem, IReadOnlyDictionary<uint, int> player, double at)
                {
                    var twin = new GolemState.Golem(0, null!, golem.Definition, golem.Source)
                    {
                        Own = golem.Own, Player = new(player), SummonedAt = at - 8,
                    };
                    return global::Game.GolemModifiers(owner, twin, player, at).Effects["damage_+%"];
                }

                var stoneSource = Assert.Single(global::Game.GemSkillEffects(owner),
                    grant => global::Game.SkillsFor(owner).EffectId(grant.Effect) == "SummonStoneGolem");
                var flameSource = Assert.Single(global::Game.GemSkillEffects(owner),
                    grant => global::Game.SkillsFor(owner).EffectId(grant.Effect) == "SummonFlameGolem");
                var stoneDefinition = global::Game.GolemData.Find("SummonStoneGolem")!;
                var flameDefinition = global::Game.GolemData.Find("SummonFlameGolem")!;

                schedule.At = 6;
                var playerWithNormal = global::Game.CharacterPlayerStats(owner);
                Assert.True(Stat(playerWithNormal, "minion_damage_+%") >= 50);
                uint firstId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    stoneSource.Id, stoneDefinition, (100, 100));
                Assert.NotEqual(0u, firstId);
                var first = owner.Golems.All[firstId];
                Assert.Equal(6, first.SummonedAt);
                Assert.Equal(6, first.LastStep);
                var stoneSlam = Assert.Single(first.Abilities, ability =>
                    ability.Effect == 136 && ability.Attack.Damage.MaxTotal > 0);
                int stoneEffect = stoneSlam.Effect;
                Assert.Equal(normalCoefficient, global::Game.GolemRecentDamageIncrease(first, schedule.At));
                var normalAbility = Ability(first, stoneEffect);
                var normalExpiredAbility = ExpiredTwin(first, playerWithNormal, schedule.At, stoneEffect);
                int normalExpiredIncrease = ExpiredIncrease(first, playerWithNormal, schedule.At);
                double normalExpected = normalExpiredAbility.Attack.Damage.MinTotal
                    * (100 + normalExpiredIncrease + normalCoefficient) / (100.0 + normalExpiredIncrease);
                Assert.Equal(normalExpected, normalAbility.Attack.Damage.MinTotal, 3);

                var normalActiveAbility = Ability(first, stoneEffect);
                inventory.Remove(PlayerInventories.PassiveJewels, normalWorn.Id);
                Recalculate();
                schedule.At = 6.25;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(first, owner.Golems.All[firstId]);
                Assert.Equal(0, global::Game.GolemRecentDamageIncrease(first, schedule.At));
                var sourceOffAbility = Ability(first, stoneEffect);
                Assert.True(normalActiveAbility.Attack.Damage.MinTotal > sourceOffAbility.Attack.Damage.MinTotal);
                Assert.True(normalActiveAbility.Attack.Damage.MaxTotal > sourceOffAbility.Attack.Damage.MaxTotal);
                inventory.Add(PlayerInventories.PassiveJewels, 0, 0, normalMight);
                Recalculate();
                schedule.At = 7;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(first, owner.Golems.All[firstId]);
                Assert.Equal(normalCoefficient, global::Game.GolemRecentDamageIncrease(first, schedule.At));
                var sourceRewornAbility = Ability(first, stoneEffect);
                Assert.Equal(normalActiveAbility.Attack.Damage.MinTotal, sourceRewornAbility.Attack.Damage.MinTotal);
                Assert.Equal(normalActiveAbility.Attack.Damage.MaxTotal, sourceRewornAbility.Attack.Damage.MaxTotal);

                schedule.At = 10;
                uint secondId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    stoneSource.Id, stoneDefinition, (100, 100));
                Assert.NotEqual(0u, secondId);
                var second = owner.Golems.All[secondId];
                Assert.Equal(2, owner.Golems.All.Count);

                var replacedNormal = inventory.Items(PlayerInventories.PassiveJewels).Single();
                inventory.Remove(PlayerInventories.PassiveJewels, replacedNormal.Id);
                inventory.Add(PlayerInventories.PassiveJewels, 0, 0, replicaMight);
                inventory.Add(5, 0, 0, chain);
                Recalculate();
                schedule.At = 10.25;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(first, owner.Golems.All[firstId]);
                Assert.Equal(replicaCoefficient, global::Game.GolemRecentDamageIncrease(first, schedule.At));
                var playerWithReplica = global::Game.CharacterPlayerStats(owner);
                var replicaAbility = Ability(first, stoneEffect);
                var replicaExpiredAbility = ExpiredTwin(first, playerWithReplica, schedule.At, stoneEffect);
                int replicaExpiredIncrease = ExpiredIncrease(first, playerWithReplica, schedule.At);
                double replicaExpected = replicaExpiredAbility.Attack.Damage.MinTotal
                    * (100 + replicaExpiredIncrease + replicaCoefficient) / (100.0 + replicaExpiredIncrease);
                Assert.Equal(replicaExpected, replicaAbility.Attack.Damage.MinTotal, 3);

                schedule.At = 10.5;
                uint flameId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    flameSource.Id, flameDefinition, (100, 100));
                Assert.NotEqual(0u, flameId);
                var flame = owner.Golems.All[flameId];
                Assert.Equal(3, owner.Golems.All.Count);
                Assert.Equal(replicaCoefficient, global::Game.GolemRecentDamageIncrease(flame, schedule.At));
                var flameSpell = flame.Abilities.First(ability => ability.Attack.Spell && ability.Attack.Damage.MaxTotal > 0);
                Assert.True(flameSpell.Attack.Damage.MinTotal > 0);

                schedule.At = 13.999;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(first, owner.Golems.All[firstId]);
                Assert.True(global::Game.GolemSummonedInPastEightSeconds(first, schedule.At));
                Assert.True(global::Game.GolemSummonedInPastEightSeconds(second, schedule.At));
                Assert.True(global::Game.GolemSummonedInPastEightSeconds(owner, schedule.At));
                schedule.At = 14;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(first, owner.Golems.All[firstId]);
                Assert.Same(second, owner.Golems.All[secondId]);
                Assert.False(global::Game.GolemSummonedInPastEightSeconds(first, schedule.At));
                Assert.True(global::Game.GolemSummonedInPastEightSeconds(second, schedule.At));
                Assert.True(global::Game.GolemSummonedInPastEightSeconds(owner, schedule.At));
                Assert.Equal(14, first.LastStep);

                // Rebuild at a level change must preserve each actor's birth, and its initial native packet after restore.
                int firstBirth = (int)first.SummonedAt;
                int secondBirth = (int)second.SummonedAt;
                double flameBirth = flame.SummonedAt;
                schedule.At = 14.5;
                uint worn = inventory.Items(PlayerInventories.Weapon1).Single().Id;
                int levelTwenty = Program.Items.Gem(Program.Items.ByHash(stoneGem.TypeHash)!)!.ExperienceFor(20);
                inventory.Change(PlayerInventories.Weapon1, worn, item => item with
                {
                    Sockets =
                    [
                        new(SocketColour.White, stoneGem with { GemExperience = levelTwenty }),
                        new(SocketColour.White, flameGem),
                    ],
                });
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Equal(3, owner.Golems.All.Count);
                var rebuiltFirst = owner.Golems.All.Values.Single(golem =>
                    golem.Definition.Kind == "Stone" && (int)golem.SummonedAt == firstBirth);
                var rebuiltSecond = owner.Golems.All.Values.Single(golem =>
                    golem.Definition.Kind == "Stone" && (int)golem.SummonedAt == secondBirth);
                var rebuiltFlame = owner.Golems.All.Values.Single(golem =>
                    golem.Definition.Kind == "Flame" && golem.SummonedAt == flameBirth);
                Assert.Equal(70, rebuiltFirst.Monster.Level);
                Assert.Equal(14.5, rebuiltSecond.LastStep);
                Assert.Equal(replicaCoefficient, global::Game.GolemRecentDamageIncrease(rebuiltSecond, schedule.At));
                Assert.Equal(replicaCoefficient, global::Game.GolemRecentDamageIncrease(rebuiltFlame, schedule.At));
                Assert.Contains(rebuiltSecond.Skills, skill => skill.Stats?.Any() == true);

                // Save the partly aged actors, restore them, and prove the restored first step is not aged from birth.
                rebuiltSecond.Monster.Life = Math.Max(1, rebuiltSecond.Monster.MaxLife / 2);
                global::Game.SaveGolems(owner);
                var saved = JsonSerializer.Deserialize<GolemState.Saved[]>(
                    CharacterSaves.ModuleData(character, "Game.Golems")!)!;
                Assert.All(saved.Where(entry => entry.ReviveSeconds == 0),
                    entry => Assert.True(double.IsFinite(entry.SummonedSecondsRemaining)));
                Assert.Contains(saved, entry => entry.SummonedSecondsRemaining is > 3 and < 5);
                double restoreAt = schedule.At;

                restored = new ClientState(new(1, "primordial-golem-restored", 1000, 1000, 100, 100, null, []), 5, 1)
                {
                    Character = character, Instance = new(), PlayerLife = 10000, PlayerMana = 10000, PlayerSent = true,
                    Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = updates,
                    Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    LeagueModule = conn.LeagueModule,
                };
                restored.Passives.Add(jewelNode);
                foreach (string stat in new[] { "virtual_strength_for_requirements",
                             "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                    restored.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
                restored.PlayerStats[Program.Stats.Key("minion_damage_+%")!.Value] = 50;
                restored.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 1;
                global::Game.EquipmentStatSources(restored, inventory.EquipmentSnapshot());
                restored.Pools = global::Game.PoolsFor(restored, 100);
                restored.PlayerLife = restored.Pools.Life; restored.PlayerMana = restored.Pools.Mana;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, restored, restoreAt);
                Assert.Equal(3, restored.Golems.All.Count);
                var restoredSecond = restored.Golems.All.Values.Single(golem =>
                    golem.Definition.Kind == "Stone" && (int)golem.SummonedAt == secondBirth);
                var restoredFlame = restored.Golems.All.Values.Single(golem =>
                    golem.Definition.Kind == "Flame" && golem.SummonedAt == flameBirth);
                Assert.Equal(restoreAt, restoredSecond.LastStep);
                Assert.True(restoredSecond.SummonedAt < restoredSecond.LastStep);
                Assert.Equal(replicaCoefficient,
                    global::Game.GolemRecentDamageIncrease(restoredSecond, restoreAt));
                Assert.Equal(rebuiltSecond.Monster.Life / (double)rebuiltSecond.Monster.MaxLife,
                    restoredSecond.Monster.Life / (double)restoredSecond.Monster.MaxLife, 2);
                Assert.NotEmpty(restoredSecond.Skills);
                uint restoredLife = restoredSecond.Monster.Life;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, restored, restoreAt);
                Assert.Equal(restoredLife, restoredSecond.Monster.Life);
                Assert.Equal(restoreAt, restoredSecond.LastStep);

                schedule.At = 18;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(rebuiltFirst, owner.Golems.All[rebuiltFirst.Id]);
                Assert.Same(rebuiltSecond, owner.Golems.All[rebuiltSecond.Id]);
                Assert.False(global::Game.GolemSummonedInPastEightSeconds(rebuiltSecond, schedule.At));
                Assert.True(global::Game.GolemSummonedInPastEightSeconds(rebuiltFlame, schedule.At));
                Assert.True(global::Game.GolemSummonedInPastEightSeconds(owner, schedule.At));

                schedule.At = 18.499;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                var staleFlameAbility = rebuiltFlame.Abilities.Single(ability => ability.Effect == flameSpell.Effect);
                Assert.True(global::Game.GolemRecentDamageIncrease(rebuiltFlame, schedule.At) > 0);
                schedule.At = 18.5;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                var currentFlameAbility = rebuiltFlame.Abilities.Single(ability => ability.Effect == staleFlameAbility.Effect);
                Assert.Equal(0, global::Game.GolemRecentDamageIncrease(rebuiltFlame, schedule.At));
                Assert.True(staleFlameAbility.Attack.Damage.MinTotal > currentFlameAbility.Attack.Damage.MinTotal);
                Assert.True(staleFlameAbility.Attack.Damage.MaxTotal > currentFlameAbility.Attack.Damage.MaxTotal);

                const uint targetId = 0x08000003;
                var from = rebuiltFlame.Monster.Walk.Position(schedule.At);
                var target = new SpawnedMonster("primordial golem target",
                    new(from.X + 2, from.Y, 0), 100, 8, 1_000_000_000);
                owner.Monsters[targetId] = target;
                int outputStart = owner.Minions.OutputQueue.Count;
                uint targetLife = target.Life;
                try
                {
                    Assert.True(global::Game.GolemHitLocked("test", wire.Stream, wire.Ciphers, owner, rebuiltFlame,
                        staleFlameAbility, targetId, target, schedule.At));
                    Assert.True(target.Life < targetLife);
                    int lost = (int)(targetLife - target.Life);
                    var hitOutput = owner.Minions.OutputQueue.Skip(outputStart).Single(output =>
                        output.Packet.Length >= 2
                        && BinaryPrimitives.ReadUInt16BigEndian(output.Packet) == InstanceProtocol.ActionHitId);
                    ushort hitFlags = BinaryPrimitives.ReadUInt16BigEndian(hitOutput.Packet.AsSpan(20));
                    bool criticalHit = (hitFlags & 0x0020) != 0;
                    double criticalFactor = 1;
                    if (criticalHit && !target.Effects.NoExtraCriticalDamage)
                    {
                        var effects = rebuiltFlame.Monster.Mods.Effects;
                        criticalFactor = 1.3 + (effects["base_critical_strike_multiplier_+"]
                            + effects["critical_strike_multiplier_+"]
                            + target.Curses["enemy_additional_critical_strike_multiplier_against_self"]) / 100.0;
                    }
                    var possibleDamage = currentFlameAbility.Attack.Damage.Scale(criticalFactor);
                    Assert.InRange(lost, (int)Math.Floor(possibleDamage.MinTotal),
                        (int)Math.Ceiling(possibleDamage.MaxTotal));
                    Assert.Equal(global::Game.HitCode(currentFlameAbility.Attack.Damage), hitOutput.Packet[23]);

                    var currentAbilities = rebuiltFlame.Abilities;
                    uint targetLifeBeforeMissing = target.Life;
                    int missingOutputStart = owner.Minions.OutputQueue.Count;
                    rebuiltFlame.Abilities = currentAbilities
                        .Where(ability => ability.Effect != staleFlameAbility.Effect).ToArray();
                    Assert.True(owner.Monsters.TryGetValue(targetId, out var registeredTarget));
                    Assert.Same(target, registeredTarget);
                    Assert.False(target.Dead);
                    Assert.True(target.Life > 0);
                    Assert.False(target.Invulnerable);
                    Assert.True(target.Hostile);
                    Assert.False(global::Game.GolemHitLocked("test", wire.Stream, wire.Ciphers, owner, rebuiltFlame,
                        staleFlameAbility, targetId, target, schedule.At + .001));
                    Assert.Equal(targetLifeBeforeMissing, target.Life);
                    Assert.DoesNotContain(owner.Minions.OutputQueue.Skip(missingOutputStart), output =>
                        output.Packet.Length >= 2
                        && BinaryPrimitives.ReadUInt16BigEndian(output.Packet) == InstanceProtocol.ActionHitId);

                    rebuiltFlame.Abilities = currentAbilities;
                    uint targetLifeBeforeRestored = target.Life;
                    int restoredOutputStart = owner.Minions.OutputQueue.Count;
                    Assert.True(global::Game.GolemHitLocked("test", wire.Stream, wire.Ciphers, owner, rebuiltFlame,
                        staleFlameAbility, targetId, target, schedule.At + .001));
                    Assert.True(target.Life < targetLifeBeforeRestored);
                    var restoredHit = owner.Minions.OutputQueue.Skip(restoredOutputStart).Single(output =>
                        output.Packet.Length >= 2
                        && BinaryPrimitives.ReadUInt16BigEndian(output.Packet) == InstanceProtocol.ActionHitId);
                    Assert.Equal(global::Game.HitCode(currentFlameAbility.Attack.Damage), restoredHit.Packet[23]);
                }
                finally
                {
                    owner.Monsters.Remove(targetId);
                }

                // The generic-fighter definition is the only switch that grants the golem-only conditional.
                uint golemRecentDamage = Program.Stats.Key("golem_damage_+%_if_summoned_in_past_8_seconds")!.Value;
                var playerWithRecentDamage = new Dictionary<uint, int>(rebuiltFlame.Player);
                Assert.True(playerWithRecentDamage.GetValueOrDefault(golemRecentDamage) > 0);
                var playerWithoutRecentDamage = new Dictionary<uint, int>(playerWithRecentDamage);
                playerWithoutRecentDamage.Remove(golemRecentDamage);
                var generic = new GolemState.Golem(0, null!, rebuiltFlame.Definition with { IsGolem = false },
                    rebuiltFlame.Source)
                {
                    Own = rebuiltFlame.Own, Player = new(playerWithRecentDamage),
                    SummonedAt = 18.25,
                };
                Assert.Equal(0, global::Game.GolemRecentDamageIncrease(generic, 18.5));
                double ProjectedDamage(GolemState.Golem golem, IReadOnlyDictionary<uint, int> player)
                {
                    var ability = global::Game.GolemAbilities(owner,
                        global::Game.Monsters.Find(golem.Definition.Variety)!, golem.Monster?.Level ?? 70,
                        golem, player, 18.25).Single(candidate => candidate.Effect == staleFlameAbility.Effect);
                    Assert.True(ability.Attack.Damage.MaxTotal > 0);
                    return ability.Attack.Damage.MinTotal;
                }
                Assert.Equal(ProjectedDamage(generic, playerWithoutRecentDamage),
                    ProjectedDamage(generic, playerWithRecentDamage));
                Assert.True(ProjectedDamage(rebuiltFlame, playerWithRecentDamage)
                    > ProjectedDamage(rebuiltFlame, playerWithoutRecentDamage));

                // An old save without the optional field is expired at its first raw native and ability build.
                var legacyEntries = saved.Select(entry => new
                {
                    entry.Effect, entry.Skill, entry.Life, entry.ReviveSeconds,
                }).ToArray();
                CharacterSaves.ChangeModuleData(character, "Game.Golems", _ => JsonSerializer.Serialize(legacyEntries));
                schedule.At = 19;
                legacy = new ClientState(new(1, "primordial-golem-legacy", 1000, 1000, 100, 100, null, []), 6, 1)
                {
                    Character = character, Instance = new(), PlayerLife = 10000, PlayerMana = 10000, PlayerSent = true,
                    Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = updates,
                    Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    LeagueModule = conn.LeagueModule,
                };
                legacy.Passives.Add(jewelNode);
                foreach (string stat in new[] { "virtual_strength_for_requirements",
                             "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                    legacy.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
                legacy.PlayerStats[Program.Stats.Key("minion_damage_+%")!.Value] = 50;
                legacy.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 1;
                global::Game.EquipmentStatSources(legacy, inventory.EquipmentSnapshot());
                legacy.Pools = global::Game.PoolsFor(legacy, 100);
                legacy.PlayerLife = legacy.Pools.Life; legacy.PlayerMana = legacy.Pools.Mana;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, legacy, schedule.At);
                Assert.Equal(3, legacy.Golems.All.Count);
                Assert.All(legacy.Golems.All.Values, golem =>
                {
                    Assert.Equal(11, golem.SummonedAt);
                    Assert.Equal(19, golem.LastStep);
                    Assert.Equal(0, global::Game.GolemRecentDamageIncrease(golem, schedule.At));
                });
                Assert.All(legacy.Golems.All.Values, golem => Assert.NotEmpty(golem.Skills));

                foreach (double invalidRemaining in new[] { double.NaN, double.PositiveInfinity })
                {
                    schedule.At++;
                    uint invalidId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, legacy,
                        stoneSource.Id, stoneDefinition, (100, 100), arrival: true,
                        summonedSecondsRemaining: invalidRemaining);
                    Assert.NotEqual(0u, invalidId);
                    var invalid = legacy.Golems.All[invalidId];
                    Assert.Equal(schedule.At - 8, invalid.SummonedAt);
                    Assert.Equal(schedule.At, invalid.LastStep);
                    Assert.Equal(0, global::Game.GolemRecentDamageIncrease(invalid, schedule.At));
                }
            }
            finally
            {
                global::Game.AreaUpdateContext.Value = previousAreaUpdateContext;
                foreach (var client in new[] { legacy, restored, owner })
                    if (client is not null) client.Closed = true;
                PlayerInventories.Forget(character);
                global::Game.InactiveItems.TryRemove(character, out _);
            }
        }

        async Task PrimordialGroundHazardWindow()
        {
            const string character = "PrimordialGroundHazard";
            const string condition = "golem_damage_+%_if_summoned_in_past_8_seconds";
            const string minionDamage = "minion_damage_+%";
            const string numberAllowed = "number_of_golems_allowed";
            var schedule = new AreaClockSchedule();
            var updates = new PlayerUpdatesState(lifetime, schedule);
            var previousAreaUpdateContext = global::Game.AreaUpdateContext.Value;
            global::Game.AreaUpdateContext.Value = updates;
            ClientState? owner = null;
            try
            {
                owner = new ClientState(new(1, "primordial-ground-hazard", 1000, 1000, 100, 100, null, []), 7, 1)
                {
                    Character = character, Instance = new(), PlayerLife = 10000, PlayerMana = 10000, PlayerSent = true,
                    Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = updates,
                    Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    LeagueModule = conn.LeagueModule,
                };
                owner.Golems.Restored = true;
                foreach (string stat in new[] { "virtual_strength_for_requirements",
                             "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                    owner.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
                owner.PlayerStats[Program.Stats.Key(minionDamage)!.Value] = 50;

                var inventory = global::Game.ItemsOf(owner);
                var jewelNode = Program.Passives.JewelNodeForSlot(0)!.Value;
                owner.Passives.Add(jewelNode);
                var normalMight = new UniqueCatalog(Program.Items).Create("Primordial Might", new Random(504))!;
                var replicaMight = new UniqueCatalog(Program.Items).Create("Replica Primordial Might", new Random(504))!;
                string ItemModId(ItemMod mod) => Program.Items.Mods.ByHash(mod.Hash)?.Id ?? "";
                int normalCoefficient = normalMight.Mods.Single(mod =>
                    ItemModId(mod) == "IncreasedGolemDamageIfGolemSummonedRecently__1_").Values[0];
                int replicaCoefficient = replicaMight.Mods.Single(mod =>
                    ItemModId(mod) == "IncreasedGolemDamageIfGolemSummonedRecentlyUnique__1").Values[0];
                Assert.InRange(normalCoefficient, 35, 45);
                Assert.InRange(replicaCoefficient, 100, 125);
                Assert.Contains(replicaMight.Mods, mod => ItemModId(mod) == "MaximumGolemsUnique__4_"
                    && mod.Values[0] == -1);

                var chaosGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSummonChaosGolem")!);
                var baseFlameType = Program.Items.Find("Metadata/Items/Gems/SkillGemSummonFlameGolem")!;
                var flameDefinition = global::Game.GolemData.Find("SummonFlameGolemAltY")!;
                Assert.Equal(4765, flameDefinition.Effect);
                var meteorVariant = Program.Items.Gem(baseFlameType)!.Variants
                    .Single(variant => variant.Effect == flameDefinition.Effect);
                var meteorGem = ItemCatalog.Create(baseFlameType) with { GemVariant = meteorVariant.Index };
                var weapon = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with
                {
                    Sockets = [new(SocketColour.White, chaosGem), new(SocketColour.White, meteorGem)],
                    Links = [2],
                };
                inventory.Add(PlayerInventories.Weapon1, 0, 0, weapon);
                void Recalculate()
                {
                    global::Game.EquipmentStatSources(owner, inventory.EquipmentSnapshot());
                    owner.Pools = global::Game.PoolsFor(owner, 100);
                    owner.PlayerLife = owner.Pools.Life;
                    owner.PlayerMana = owner.Pools.Mana;
                }
                Recalculate();

                var chaosSource = Assert.Single(global::Game.GemSkillEffects(owner),
                    grant => global::Game.SkillsFor(owner).EffectId(grant.Effect) == "SummonChaosGolem");
                var chaosDefinition = global::Game.GolemData.Find("SummonChaosGolem")!;
                schedule.At = 6;
                uint chaosId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    chaosSource.Id, chaosDefinition, (100, 100));
                Assert.NotEqual(0u, chaosId);
                GolemState.Golem CurrentGolem() => Assert.Single(owner!.Golems.All).Value;
                var chaos = CurrentGolem();
                Assert.Equal(6, chaos.SummonedAt);
                Assert.False(chaos.Monster.Dead);
                var aura = Assert.Single(chaos.Abilities, ability => ability.Effect == 4336);
                int Stat(IReadOnlyDictionary<uint, int> stats, string name) =>
                    Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
                double MoreFactor(int percent) => Math.Max(0, 1 + percent / 100.0);
                double UnitDamage(IReadOnlyDictionary<uint, int> stats, GolemState.Golem golem, string perMinute)
                {
                    Assert.Equal(0, Stat(golem.Own, "support_minion_damage_+%_final"));
                    Assert.Equal(0, Stat(golem.Player, "golem_damage_+%_final_from_unique_primordial_tether"));
                    Assert.Equal(0, Stat(golem.Player, "unique_primordial_tether_golem_damage_+%_final"));
                    return Stat(stats, perMinute) / 60.0
                        * MoreFactor(Stat(golem.Own, "active_skill_minion_damage_+%_final"));
                }
                double ChaosUnitDamage(GolemState.Golem golem, GolemState.Ability ability) =>
                    UnitDamage(ability.Stats, golem, "base_chaos_damage_to_deal_per_minute");
                double FireUnitDamage(GolemState.Golem golem, GolemState.Ability ability) =>
                    UnitDamage(ability.Stats, golem, "base_fire_damage_to_deal_per_minute");
                var chaosUnitDps = ChaosUnitDamage(chaos, aura);
                Assert.True(chaosUnitDps > 0);

                int NextTargetId = 0x08000100;
                SpawnedMonster AddTarget(string name, GolemState.Golem golem, double at, out uint targetId)
                {
                    targetId = (uint)NextTargetId++;
                    var position = golem.Monster.Walk.Position(at);
                    var target = new SpawnedMonster(name, new(position.X, position.Y, 0), 100, 8, 1_000_000_000)
                    { Hostile = true };
                    owner!.Monsters[targetId] = target;
                    return target;
                }
                void ConsumeRaw(GolemState.Golem golem, GolemState.Hazard activeHazard, double at, double expectedRaw,
                    SpawnedMonster target, uint targetId, string stage)
                {
                    Assert.Equal(0, global::Game.MonsterHitResistance(target, activeHazard.Type, default).Value);
                    Assert.Equal(1.0, global::Game.MonsterDamageTaken(target, spell: true, now: at,
                        chaos: activeHazard.Type == DamageType.Chaos), 12);
                    double total = expectedRaw + activeHazard.DamageCarry.GetValueOrDefault(targetId);
                    int expectedDamage = (int)Math.Floor(total);
                    double expectedCarry = total - expectedDamage;
                    uint before = target.Life;
                    global::Game.GolemHazardsLocked("test", wire.Stream, wire.Ciphers, owner!, golem, at);
                    uint lost = before - target.Life;
                    Assert.True(lost == expectedDamage,
                        $"{stage}: expected {expectedDamage} life loss, actual {lost}; raw={expectedRaw:R}");
                    Assert.Equal(expectedCarry, activeHazard.DamageCarry.GetValueOrDefault(targetId), 12);
                    Assert.Equal(Math.Min(at, activeHazard.Until), activeHazard.LastAt, 12);
                    owner!.Monsters.Remove(targetId);
                }
                void ConsumeKnown50(GolemState.Golem golem, GolemState.Hazard activeHazard, double at, double unitDps,
                    double elapsed, double activeSeconds, int coefficient, string stage)
                {
                    Assert.True(coefficient >= 0);
                    Assert.InRange(activeSeconds, 0, elapsed);
                    double expectedRaw = unitDps * (1.5 * elapsed + coefficient / 100.0 * activeSeconds);
                    var target = AddTarget(stage, golem, at, out uint targetId);
                    ConsumeRaw(golem, activeHazard, at, expectedRaw, target, targetId, stage);
                }
                void ConsumeZeroBaseline(GolemState.Golem golem, GolemState.Hazard activeHazard, double at, double unitDps,
                    double elapsed, double activeSeconds, int coefficient, string stage)
                {
                    Assert.Equal(0, activeHazard.DamagePerSecond);
                    Assert.InRange(activeSeconds, 0, elapsed);
                    double expectedRaw = unitDps * Math.Max(0, 1 + (-100 + coefficient) / 100.0) * activeSeconds;
                    var target = AddTarget(stage, golem, at, out uint targetId);
                    ConsumeRaw(golem, activeHazard, at, expectedRaw, target, targetId, stage);
                }

                void Contact(GolemState.Golem golem, GolemState.Ability ability, double at)
                {
                    var action = new GolemState.Action(ability, 0, null, golem.Monster.Walk.Position(at), at,
                        golem.Monster.ActionGeneration);
                    global::Game.GolemAbilityContactLocked("test", wire.Stream, wire.Ciphers, owner!,
                        golem, action, at);
                }

                schedule.At = 12;
                Contact(chaos, aura, schedule.At);
                var hazard = Assert.Single(chaos.Hazards, candidate => candidate.Effect == 4336);
                Assert.True(hazard.Until > 15);
                Assert.Equal(12, hazard.LastAt);
                Assert.Equal(50, hazard.NormalDamageIncrease);
                Assert.Equal(chaosUnitDps, hazard.UnitDamagePerSecond, 12);
                Assert.Equal(chaosUnitDps * 1.5, hazard.DamagePerSecond, 12);

                var normalWorn = inventory.Add(PlayerInventories.PassiveJewels, 0, 0, normalMight);
                Recalculate();
                schedule.At = 12;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(chaos, CurrentGolem());
                Assert.Equal(normalCoefficient, chaos.Player.GetValueOrDefault(Program.Stats.Key(condition)!.Value));
                Assert.DoesNotContain(owner.Monsters.Values, monster => monster.Hostile);

                schedule.At = 13;
                ConsumeKnown50(chaos, hazard, schedule.At, chaosUnitDps, elapsed: 1, activeSeconds: 1,
                    normalCoefficient, "before eight seconds");

                ConsumeKnown50(chaos, hazard, schedule.At, chaosUnitDps, elapsed: 0, activeSeconds: 0,
                    normalCoefficient, "repeated same clock");

                schedule.At = 15;
                ConsumeKnown50(chaos, hazard, schedule.At, chaosUnitDps, elapsed: 2, activeSeconds: 1,
                    normalCoefficient, "delayed crossing eight seconds");

                double afterBoundarySeconds = hazard.Until - 15;
                schedule.At = hazard.Until;
                ConsumeKnown50(chaos, hazard, schedule.At, chaosUnitDps, afterBoundarySeconds, activeSeconds: 0,
                    normalCoefficient, "after eight seconds to hazard cap");
                Assert.DoesNotContain(chaos.Hazards, candidate => ReferenceEquals(candidate, hazard));
                schedule.At = hazard.Until + 1;
                global::Game.GolemHazardsLocked("test", wire.Stream, wire.Ciphers, owner, chaos, schedule.At);
                Assert.Empty(chaos.Hazards);

                inventory.Remove(PlayerInventories.PassiveJewels, normalWorn.Id);
                Recalculate();
                schedule.At = 30;
                uint replacementId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    chaosSource.Id, chaosDefinition, (100, 100));
                Assert.NotEqual(0u, replacementId);
                chaos = owner.Golems.All[replacementId];
                Assert.Equal(30, chaos.SummonedAt);
                aura = Assert.Single(chaos.Abilities, ability => ability.Effect == 4336);
                chaosUnitDps = ChaosUnitDamage(chaos, aura);
                Contact(chaos, aura, schedule.At);
                hazard = Assert.Single(chaos.Hazards, candidate => candidate.Effect == 4336);
                Assert.Equal(0, chaos.Player.GetValueOrDefault(Program.Stats.Key(condition)!.Value));
                Assert.Equal(chaosUnitDps * 1.5, hazard.DamagePerSecond, 12);

                normalWorn = inventory.Add(PlayerInventories.PassiveJewels, 0, 0, normalMight);
                Recalculate();
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(chaos, owner.Golems.All[replacementId]);
                Assert.Equal(normalCoefficient, chaos.Player.GetValueOrDefault(Program.Stats.Key(condition)!.Value));
                schedule.At = 30.5;
                ConsumeKnown50(chaos, hazard, schedule.At, chaosUnitDps, elapsed: .5, activeSeconds: .5,
                    normalCoefficient, "normal source added to existing hazard");

                inventory.Remove(PlayerInventories.PassiveJewels, normalWorn.Id);
                owner.PlayerStats[Program.Stats.Key(numberAllowed)!.Value] = 1;
                var replicaWorn = inventory.Add(PlayerInventories.PassiveJewels, 0, 0, replicaMight);
                Recalculate();
                schedule.At = 31;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(chaos, owner.Golems.All[replacementId]);
                Assert.Equal(replicaCoefficient, chaos.Player.GetValueOrDefault(Program.Stats.Key(condition)!.Value));
                Assert.True(chaos.Limit > 0);
                schedule.At = 31.5;
                ConsumeKnown50(chaos, hazard, schedule.At, chaosUnitDps, elapsed: .5, activeSeconds: .5,
                    replicaCoefficient, "actual replica source on same young golem and hazard");
                var replicaPlayer = new Dictionary<uint, int>(chaos.Player);

                inventory.Remove(PlayerInventories.PassiveJewels, replicaWorn.Id);
                Recalculate();
                schedule.At = 32;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(chaos, owner.Golems.All[replacementId]);
                Assert.Equal(0, chaos.Player.GetValueOrDefault(Program.Stats.Key(condition)!.Value));
                schedule.At = 33;
                ConsumeKnown50(chaos, hazard, schedule.At, chaosUnitDps, elapsed: 1, activeSeconds: 0,
                    coefficient: 0, "source removed returns to baseline");

                var generic = new GolemState.Golem(chaos.Id, chaos.Monster,
                    chaos.Definition with { IsGolem = false }, chaos.Source)
                {
                    Player = replicaPlayer,
                    SummonedAt = chaos.SummonedAt,
                };
                generic.Hazards.Add(new(4336, generic.Monster.Walk.Position(33.5), 34, 10, chaosUnitDps * 1.5,
                    DamageType.Chaos, UnitDamagePerSecond: chaosUnitDps, NormalDamageIncrease: 50) { LastAt = 33.5 });
                var genericHazard = Assert.Single(generic.Hazards);
                schedule.At = 34;
                var genericTarget = AddTarget("generic baseline target", generic, schedule.At, out uint genericTargetId);
                ConsumeRaw(generic, genericHazard, schedule.At,
                    chaosUnitDps * 1.5 * .5, genericTarget, genericTargetId,
                    "generic definition ignores conditional source");

                var legacy = new GolemState.Golem(chaos.Id, chaos.Monster, chaos.Definition, chaos.Source)
                {
                    Player = replicaPlayer,
                    SummonedAt = chaos.SummonedAt,
                };
                legacy.Hazards.Add(new(4336, legacy.Monster.Walk.Position(34.5), 35, 10, chaosUnitDps * 1.5,
                    DamageType.Chaos) { LastAt = 34.5 });
                var legacyHazard = Assert.Single(legacy.Hazards);
                schedule.At = 35;
                var legacyTarget = AddTarget("legacy baseline target", legacy, schedule.At, out uint legacyTargetId);
                ConsumeRaw(legacy, legacyHazard, schedule.At,
                    chaosUnitDps * 1.5 * .5, legacyTarget, legacyTargetId,
                    "default hazard metadata keeps its positive baseline");

                var meteorSource = Assert.Single(global::Game.GemSkillEffects(owner),
                    grant => global::Game.SkillsFor(owner).EffectId(grant.Effect) == "SummonFlameGolemAltY");
                schedule.At = 40;
                uint meteorId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    meteorSource.Id, flameDefinition, (100, 100));
                Assert.NotEqual(0u, meteorId);
                var meteorGolem = owner.Golems.All[meteorId];
                Assert.Equal(flameDefinition.Id, meteorGolem.Definition.Id);
                Assert.True(meteorGolem.Definition.Special);
                Assert.NotNull(meteorGolem.Pending);
                Assert.Equal(5268, meteorGolem.Pending.Ability.Effect);
                normalWorn = inventory.Add(PlayerInventories.PassiveJewels, 0, 0, normalMight);
                Recalculate();
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(meteorGolem, owner.Golems.All[meteorId]);
                Assert.Equal(normalCoefficient,
                    meteorGolem.Player.GetValueOrDefault(Program.Stats.Key(condition)!.Value));
                schedule.At = 41;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(meteorGolem, owner.Golems.All[meteorId]);
                var meteorHazard = Assert.Single(meteorGolem.Hazards, candidate => candidate.Effect == 5268);
                var meteorAbility = Assert.Single(meteorGolem.Abilities, ability => ability.Effect == 5268);
                double meteorUnitDps = FireUnitDamage(meteorGolem, meteorAbility);
                Assert.Equal(DamageType.Fire, meteorHazard.Type);
                Assert.True(meteorUnitDps > 0);
                Assert.Equal(meteorUnitDps, meteorHazard.UnitDamagePerSecond, 12);
                Assert.Equal(meteorUnitDps * 1.5, meteorHazard.DamagePerSecond, 12);
                Assert.Equal(50, meteorHazard.NormalDamageIncrease);
                Assert.Equal(41, meteorHazard.LastAt);
                Assert.Equal(46, meteorHazard.Until);
                schedule.At = 41.5;
                ConsumeKnown50(meteorGolem, meteorHazard, schedule.At, meteorUnitDps, elapsed: .5, activeSeconds: .5,
                    normalCoefficient, "actual special meteor hazard");

                inventory.Remove(PlayerInventories.PassiveJewels, normalWorn.Id);
                Recalculate();
                schedule.At = 42;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Equal(0, meteorGolem.Player.GetValueOrDefault(Program.Stats.Key(condition)!.Value));
                schedule.At = 42.5;
                Assert.Single(meteorGolem.Hazards);
                ConsumeKnown50(meteorGolem, meteorHazard, schedule.At, meteorUnitDps, elapsed: .5, activeSeconds: 0,
                    coefficient: 0, "meteor source removed returns to baseline");

                double meteorAfterBoundary = meteorHazard.Until - 42.5;
                schedule.At = meteorHazard.Until;
                ConsumeKnown50(meteorGolem, meteorHazard, schedule.At, meteorUnitDps, meteorAfterBoundary,
                    activeSeconds: 0, coefficient: 0, "meteor hazard cap");
                Assert.DoesNotContain(meteorGolem.Hazards, candidate => ReferenceEquals(candidate, meteorHazard));

                // A current actor can retain its last young snapshot. Contact without a corrective GolemStep must
                // synchronize the modifier before it captures the expired hazard's numeric metadata.
                schedule.At = 50;
                uint expiredId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    meteorSource.Id, flameDefinition, (100, 100));
                Assert.NotEqual(0u, expiredId);
                var expiredMeteor = owner.Golems.All[expiredId];
                normalWorn = inventory.Add(PlayerInventories.PassiveJewels, 0, 0, normalMight);
                Recalculate();
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Equal(normalCoefficient, expiredMeteor.Player.GetValueOrDefault(Program.Stats.Key(condition)!.Value));
                Assert.Equal(50 + normalCoefficient, expiredMeteor.Monster.Mods.Effects["damage_+%"]);
                var expiredAbility = Assert.Single(expiredMeteor.Abilities, ability => ability.Effect == 5268);
                schedule.At = 59;
                Contact(expiredMeteor, expiredAbility, schedule.At);
                var expiredHazard = Assert.Single(expiredMeteor.Hazards, candidate => candidate.Effect == 5268);
                double expiredUnitDps = FireUnitDamage(expiredMeteor, expiredAbility);
                Assert.Equal(0, global::Game.GolemRecentDamageIncrease(expiredMeteor, schedule.At));
                Assert.Equal(50, expiredHazard.NormalDamageIncrease);
                Assert.Equal(expiredUnitDps, expiredHazard.UnitDamagePerSecond, 12);
                Assert.Equal(expiredUnitDps * 1.5, expiredHazard.DamagePerSecond, 12);
                schedule.At = 59.5;
                ConsumeKnown50(expiredMeteor, expiredHazard, schedule.At, expiredUnitDps, elapsed: .5, activeSeconds: 0,
                    normalCoefficient, "expired creation excludes stale conditional source");
                inventory.Remove(PlayerInventories.PassiveJewels, normalWorn.Id);
                Recalculate();

                // A true golem's unconditional unit rate keeps a zero-baseline meteor alive. Equipping the real
                // Replica source after creation gives that same young hazard positive conditional damage.
                owner.PlayerStats[Program.Stats.Key(minionDamage)!.Value] = -100;
                Recalculate();
                schedule.At = 70;
                uint zeroId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    meteorSource.Id, flameDefinition, (100, 100));
                Assert.NotEqual(0u, zeroId);
                var zeroMeteor = owner.Golems.All[zeroId];
                var zeroAbility = Assert.Single(zeroMeteor.Abilities, ability => ability.Effect == 5268);
                Contact(zeroMeteor, zeroAbility, schedule.At);
                var zeroHazard = Assert.Single(zeroMeteor.Hazards, candidate => candidate.Effect == 5268);
                double zeroUnitDps = FireUnitDamage(zeroMeteor, zeroAbility);
                Assert.True(zeroUnitDps > 0);
                Assert.Equal(-100, zeroHazard.NormalDamageIncrease);
                Assert.Equal(zeroUnitDps, zeroHazard.UnitDamagePerSecond, 12);
                Assert.Equal(0, zeroHazard.DamagePerSecond);

                replicaWorn = inventory.Add(PlayerInventories.PassiveJewels, 0, 0, replicaMight);
                Recalculate();
                schedule.At = 70.5;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Equal(replicaCoefficient, zeroMeteor.Player.GetValueOrDefault(Program.Stats.Key(condition)!.Value));
                schedule.At = 71;
                ConsumeZeroBaseline(zeroMeteor, zeroHazard, schedule.At, zeroUnitDps, elapsed: .5, activeSeconds: .5,
                    replicaCoefficient, "zero-baseline meteor gains real conditional source");
            }
            finally
            {
                global::Game.AreaUpdateContext.Value = previousAreaUpdateContext;
                if (owner is not null)
                {
                    owner.Closed = true;
                    PlayerInventories.Forget(character);
                    global::Game.InactiveItems.TryRemove(character, out _);
                }
            }
        }

        try
        {
            await PrimordialMightOwnerWindow();
            await PrimordialGolemLifetimeMatrix();
            await PrimordialGroundHazardWindow();
            var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSummonFlameGolem")!);
            var weapon = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with
            { Sockets = [new(SocketColour.White, gem)] };
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, weapon);
            var source = Game.GemSkillEffects(conn).Single(g => g.Name == "Summon Flame Golem");
            var definition = Game.GolemData.Find("SummonFlameGolem")!;
            uint first = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, conn, source.Id, definition, (100, 100));
            Assert.NotEqual(0u, first);
            uint next = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, conn, source.Id, definition, (100, 100));
            Assert.NotEqual(first, next); Assert.Single(conn.Golems.All); Assert.False(conn.Monsters.ContainsKey(first));
            var golem = conn.Golems.All[next];
            var baselineAttacks = golem.Abilities.Where(a => !a.Attack.Spell).ToDictionary(a => a.Effect, a => a.Attack.Seconds);
            var baselineSpells = golem.Abilities.Where(a => a.Attack.Spell).ToDictionary(a => a.Effect, a => a.Attack.Seconds);
            var baselineMovement = golem.Monster.Mods.Effects.MovementPct;
            uint maximumEnergyShield = Program.Stats.Key("maximum_energy_shield")!.Value;
            Assert.Equal(249u, maximumEnergyShield);
            var plainMonster = new MonsterObject(100, 100, 1234, MonsterObject.BaseComponents);
            byte[] PlainPool(uint current)
            {
                var pool = new byte[18];
                BinaryPrimitives.WriteUInt32LittleEndian(pool, current);
                return pool;
            }
            Assert.True(plainMonster.Blob().AsSpan().IndexOf(
                [.. PlainPool(1234), .. PlainPool(0), .. PlainPool(0)]) >= 0);
            Assert.Null(plainMonster.Stats);
            Assert.Equal(0u, conn.MinionOrigins[next].Object.EnergyShield);
            Assert.DoesNotContain(conn.MinionOrigins[next].Object.Stats ?? [], stat => stat.Key == maximumEnergyShield);

            // The real unique's two coefficient rows use the owner's final Dexterity in complete groups of 50.
            // Keep the raw base deliberately different from the final value to catch a base_dexterity consumer.
            var unique = new UniqueCatalog(Program.Items).Create("Speaker's Wreath", new Random(504))!;
            string ItemModId(ItemMod mod) => Program.Items.Mods.ByHash(mod.Hash)?.Id ?? "";
            Assert.Contains("DexterityUnique__20__", unique.Mods.Select(ItemModId));
            Assert.Contains("MinionAttackSpeedPerXDexUnique__1", unique.Mods.Select(ItemModId));
            Assert.Contains("MinionMovementSpeedPerXDexUnique__1", unique.Mods.Select(ItemModId));
            int rolledDexterity = unique.Mods.Where(mod => ItemModId(mod) == "DexterityUnique__20__").Sum(mod => mod.Values[0]);
            var inventory = Game.ItemsOf(conn);
            Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot());
            conn.Pools = Game.PoolsFor(conn, conn.Pools.Level);
            int beforeSpeakerDexterity = conn.Pools.Dexterity;
            foreach (var old in inventory.Items(PlayerInventories.Helmet)) inventory.Remove(PlayerInventories.Helmet, old.Id);
            inventory.Add(PlayerInventories.Helmet, 0, 0, unique);
            Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot());
            conn.Pools = Game.PoolsFor(conn, conn.Pools.Level);
            Assert.Equal(beforeSpeakerDexterity + rolledDexterity, conn.Pools.Dexterity);

            // The same shared attribute resolver includes typed/all-attribute percentages before this consumer.
            int withoutPercentDexterity = conn.Pools.Dexterity;
            conn.PlayerStats[Program.Stats.Key("dexterity_+%")!.Value] = 100;
            conn.Pools = Game.PoolsFor(conn, conn.Pools.Level);
            Assert.Equal(withoutPercentDexterity * 2, conn.Pools.Dexterity);
            conn.PlayerStats.Remove(Program.Stats.Key("dexterity_+%")!.Value);
            conn.Pools = Game.PoolsFor(conn, conn.Pools.Level);

            var finalDexterity = Program.Stats.Key("dexterity")!.Value;
            var baseDexterity = Program.Stats.Key("base_dexterity")!.Value;
            conn.PlayerStats[baseDexterity] = 1000;
            void SetFinalDexterity(int value) => conn.PlayerStats[finalDexterity] = value;
            async Task RefreshGolemAt(double at, int dexterity, int expectedBonus)
            {
                SetFinalDexterity(dexterity);
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, at);
                Assert.Equal(baselineMovement + expectedBonus, golem.Monster.Mods.Effects.MovementPct);
                foreach (var ability in golem.Abilities.Where(a => !a.Attack.Spell))
                    Assert.Equal(baselineAttacks[ability.Effect] / (1 + expectedBonus / 100.0), ability.Attack.Seconds, 6);
                foreach (var ability in golem.Abilities.Where(a => a.Attack.Spell))
                    Assert.Equal(baselineSpells[ability.Effect], ability.Attack.Seconds, 6);
            }

            await RefreshGolemAt(1, 49, 0);
            await RefreshGolemAt(1.6, 50, 2);
            await RefreshGolemAt(2.2, 99, 2);
            var sent = new List<byte[]>();
            var previousOutgoing = Program.Outgoing;
            Program.Outgoing = (ciphers, bytes) =>
            {
                if (ReferenceEquals(ciphers, wire.Ciphers)) lock (sent) sent.Add(bytes.ToArray());
                previousOutgoing?.Invoke(ciphers, bytes);
            };
            try { await RefreshGolemAt(2.8, 100, 4); }
            finally { Program.Outgoing = previousOutgoing; }
            Assert.Contains(sent, packet => packet.SequenceEqual(InstanceEntities.StatsUpdate(next,
                [(StatKeys.MovementVelocityPct, baselineMovement + 4)])));

            // A second refresh at the same concrete inputs rebuilds the buckets instead of adding the coefficient again.
            SetFinalDexterity(100);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 3.4);
            Assert.Equal(baselineMovement + 4, golem.Monster.Mods.Effects.MovementPct);
            foreach (var ability in golem.Abilities.Where(a => !a.Attack.Spell))
                Assert.Equal(baselineAttacks[ability.Effect] / 1.04, ability.Attack.Seconds, 6);

            // Summons use the same modifier projection and refresh path: a real Raise Zombie receives both rows.
            var zombieGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemRaiseZombie")!);
            var gemHost = inventory.Items(PlayerInventories.Weapon1).Single();
            inventory.Change(PlayerInventories.Weapon1, gemHost.Id, item => item with
            {
                Sockets = [.. item.Sockets, new ItemSocket(SocketColour.White, zombieGem)],
            });
            ushort zombieSource = Game.GemSkillEffects(conn)
                .Single(g => Game.SkillsFor(conn).EffectId(g.Effect) == "RaiseZombie").Id;
            var corpseVariety = Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie")!;
            const uint corpseId = 0x1000c0de;
            var corpse = new SpawnedMonster("zombie corpse", new(100, 100, 0), 70, 8, 0)
            {
                Hostile = true, Dead = true, Life = 0, Variety = corpseVariety, Level = 70,
            };
            conn.Monsters[corpseId] = corpse;
            SetFinalDexterity(100);
            uint zombieId = await Game.SummonFighter("test", wire.Stream, wire.Ciphers, conn, zombieSource,
                Game.SummonData.Find("RaiseZombie")!, (100, 100), corpseId);
            Assert.NotEqual(0u, zombieId);
            var zombie = conn.Summons.Fighters[zombieId];
            Assert.Equal(4, zombie.Monster.Mods.Effects.MovementPct);
            var zombieWithSpeaker = zombie.Abilities.Where(a => !a.Attack.Spell).ToDictionary(a => a.Effect, a => a.Attack.Seconds);
            SetFinalDexterity(49);
            await Game.SummonsTick("test", wire.Stream, wire.Ciphers, conn, 4);
            Assert.Equal(0, zombie.Monster.Mods.Effects.MovementPct);
            foreach (var ability in zombie.Abilities.Where(a => !a.Attack.Spell))
            {
                Assert.True(ability.Attack.Seconds > zombieWithSpeaker[ability.Effect]);
                Assert.Equal(zombieWithSpeaker[ability.Effect] * 1.04, ability.Attack.Seconds, 6);
            }
            zombie.Monster.Dead = true; zombie.Monster.Life = 0;
            await Game.SummonsTick("test", wire.Stream, wire.Ciphers, conn, 4.6);
            Assert.DoesNotContain(zombieId, conn.Summons.Fighters.Keys);

            // Removing the worn source removes the coefficient without changing the owner's final Dexterity.
            var wornSpeaker = inventory.Items(PlayerInventories.Helmet).Single();
            inventory.Remove(PlayerInventories.Helmet, wornSpeaker.Id);
            Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot());
            conn.Pools = Game.PoolsFor(conn, conn.Pools.Level);
            SetFinalDexterity(100);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 5.2);
            Assert.Equal(baselineMovement, golem.Monster.Mods.Effects.MovementPct);
            foreach (var ability in golem.Abilities.Where(a => !a.Attack.Spell))
                Assert.Equal(baselineAttacks[ability.Effect], ability.Attack.Seconds, 6);
            conn.PlayerStats.Remove(finalDexterity);
            conn.PlayerStats.Remove(baseDexterity);

            uint increased = Program.Stats.Key("damage_+%")!.Value;
            Assert.Equal(20, conn.Golems.OwnerStats[increased]); Assert.Single(conn.Golems.Buffs);
            Game.SaveGolems(conn);
            var saved = JsonSerializer.Deserialize<GolemState.Saved[]>(CharacterSaves.ModuleData(conn.Character, "Game.Golems")!);
            Assert.Single(saved!); Assert.Equal(definition.Id, saved![0].Effect);

            var attacker = new SpawnedMonster("enemy", new(100, 100, 1), 100, 8, 1000);
            var spell = new MonsterAttack(0x4000, 1, 100, DamagePacket.Of(DamageType.Physical, 1000000, 1000000)) { Spell = true };
            Assert.True(Game.GolemTakeHitLocked(conn, golem, 0x08000000, attacker, spell, 3));
            Assert.True(golem.Monster.Dead); Assert.Equal(0u, golem.Monster.Life); Assert.Equal(5, golem.ReviveAt);
            await Game.RefreshGolemBuffs("test", wire.Stream, wire.Ciphers, conn);
            Assert.False(conn.Golems.OwnerStats.ContainsKey(increased)); Assert.Empty(conn.Golems.Buffs);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 4.9);
            Assert.True(golem.Monster.Dead);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 5);
            Assert.Single(conn.Golems.All); Assert.False(conn.Golems.All.Values.Single().Monster.Dead);
            Assert.Equal(20, conn.Golems.OwnerStats[increased]);

            // The revive replaced the golem object above; every Ancient Skull assertion must use the current live actor.
            golem = conn.Golems.All.Values.Single();
            void AssertCurrentGolem()
            {
                Assert.Single(conn.Golems.All);
                Assert.Same(golem, conn.Golems.All.Values.Single());
                Assert.False(golem.Monster.Dead);
                Assert.True(golem.Monster.Life > 0);
            }
            AssertCurrentGolem();

            // Ancient Skull's real coefficient uses the owner's maximum Power Charges, including Character.ot's base three.
            // Change only max_power_charges (the additive amount) so the refresh hash has to see the derived maximum.
            const int AncientSkullCriticalChancePerMaximumPowerCharge = 50;
            const string AncientSkullCriticalChanceMod = "MinionCriticalStrikeChanceMaximumPowerChargeUnique__1";
            var ancientSkull = new UniqueCatalog(Program.Items).Create("Ancient Skull", new Random(504))!;
            var ancientSkullCoefficient = ancientSkull.Mods.Single(mod => ItemModId(mod) == AncientSkullCriticalChanceMod);
            Assert.Equal(AncientSkullCriticalChancePerMaximumPowerCharge, ancientSkullCoefficient.Values[0]);
            var rawAncientSkullStats = ItemModStats.Sum([ancientSkull], Program.Items.Mods);
            Assert.True(rawAncientSkullStats.TryGetValue("minion_critical_strike_chance_+%_per_maximum_power_charge",
                out var rawAncientSkullChance), string.Join("|", rawAncientSkullStats.Select(p => $"{p.Key}={p.Value}")));
            Assert.Equal(AncientSkullCriticalChancePerMaximumPowerCharge, rawAncientSkullChance);
            int criticalWithoutAncientSkull = golem.Monster.Mods.Effects["critical_strike_chance_+%"];
            var actualCriticalAbility = golem.Abilities.First(a => a.Crit > 0);
            int baselineFinalCriticalChance = Game.GolemCriticalChance(actualCriticalAbility, golem.Monster.Mods);
            inventory.Add(PlayerInventories.Helmet, 0, 0, ancientSkull);
            Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot());
            conn.Pools = Game.PoolsFor(conn, conn.Pools.Level);

            uint maximumPowerCharges = Program.Stats.Key("max_power_charges")!.Value;
            var completedStats = Game.CharacterPlayerStats(conn);
            Assert.Equal(3, Charges.LimitsOf(ChargeType.Power, name =>
                completedStats.GetValueOrDefault(Program.Stats.Key(name) ?? 0)).Maximum);
            async Task<int> RefreshAncientSkullAt(double at, int additiveMaximum, int expectedMaximum, int expectedBonus)
            {
                conn.PlayerStats[maximumPowerCharges] = additiveMaximum;
                AssertCurrentGolem();
                var ownerStats = Game.CharacterPlayerStats(conn);
                int OwnerStat(string name) => Program.Stats.Key(name) is uint key
                    ? ownerStats.GetValueOrDefault(key) : 0;
                Assert.Equal(expectedMaximum, Charges.LimitsOf(ChargeType.Power, OwnerStat).Maximum);
                var session = Game.ChargesOf(conn);
                Assert.Equal(expectedMaximum, session.Limits(ChargeType.Power).Maximum);
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, at);
                AssertCurrentGolem();
                Assert.Equal(expectedMaximum, session.Limits(ChargeType.Power).Maximum);
                Assert.Equal(criticalWithoutAncientSkull + expectedBonus,
                    golem.Monster.Mods.Effects["critical_strike_chance_+%"]);
                int finalChance = (int)Math.Clamp(actualCriticalAbility.Crit
                    * (100L + criticalWithoutAncientSkull + expectedBonus) / 100.0, 0, 10_000);
                Assert.Equal(finalChance, Game.GolemCriticalChance(actualCriticalAbility, golem.Monster.Mods));
                return finalChance;
            }

            _ = await RefreshAncientSkullAt(5.5, -3, 0, 0);
            _ = await RefreshAncientSkullAt(6, 0, 3, 3 * AncientSkullCriticalChancePerMaximumPowerCharge);
            int maximumFiveFinalCriticalChance = await RefreshAncientSkullAt(6.5, 2, 5,
                5 * AncientSkullCriticalChancePerMaximumPowerCharge);

            async Task SetPowerChargesAt(double at, int count, int expectedHeld)
            {
                AssertCurrentGolem();
                var ownerStats = Game.CharacterPlayerStats(conn);
                int OwnerStat(string name) => Program.Stats.Key(name) is uint key
                    ? ownerStats.GetValueOrDefault(key) : 0;
                Assert.Equal(5, Charges.LimitsOf(ChargeType.Power, OwnerStat).Maximum);
                var session = Game.ChargesOf(conn);
                lock (conn.Monsters)
                {
                    Assert.Equal(5, session.Limits(ChargeType.Power).Maximum);
                    session.Admit(at, [new ChargeEvent.Set(ChargeType.Power, count)], OwnerStat,
                        () => default, alive: true, new Random(1));
                    Assert.Equal(expectedHeld, session.Effective(ChargeType.Power));
                    Assert.Equal(expectedHeld, session.State.Held(ChargeType.Power));
                    Assert.Equal(5, session.Limits(ChargeType.Power).Maximum);
                }
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, at);
                AssertCurrentGolem();
                Assert.Equal(5, session.Limits(ChargeType.Power).Maximum);
                Assert.Equal(expectedHeld, session.Effective(ChargeType.Power));
                Assert.Equal(criticalWithoutAncientSkull + 5 * AncientSkullCriticalChancePerMaximumPowerCharge,
                    golem.Monster.Mods.Effects["critical_strike_chance_+%"]);
                Assert.Equal(maximumFiveFinalCriticalChance,
                    Game.GolemCriticalChance(actualCriticalAbility, golem.Monster.Mods));
            }

            await SetPowerChargesAt(7, 0, 0);
            await SetPowerChargesAt(7.5, 3, 3);

            var equippedAncientSkull = inventory.Items(PlayerInventories.Helmet).Single();
            inventory.Remove(PlayerInventories.Helmet, equippedAncientSkull.Id);
            Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot());
            conn.Pools = Game.PoolsFor(conn, conn.Pools.Level);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 8);
            AssertCurrentGolem();
            Assert.Equal(criticalWithoutAncientSkull, golem.Monster.Mods.Effects["critical_strike_chance_+%"]);
            Assert.Equal(baselineFinalCriticalChance,
                Game.GolemCriticalChance(actualCriticalAbility, golem.Monster.Mods));

            inventory.Add(PlayerInventories.Helmet, 0, 0, ancientSkull);
            Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot());
            conn.Pools = Game.PoolsFor(conn, conn.Pools.Level);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 8.5);
            AssertCurrentGolem();
            Assert.Equal(criticalWithoutAncientSkull + 5 * AncientSkullCriticalChancePerMaximumPowerCharge,
                golem.Monster.Mods.Effects["critical_strike_chance_+%"]);
            Assert.Equal(maximumFiveFinalCriticalChance,
                Game.GolemCriticalChance(actualCriticalAbility, golem.Monster.Mods));

            int actualFinalCriticalChance = Game.GolemCriticalChance(actualCriticalAbility, golem.Monster.Mods);
            Assert.InRange(actualFinalCriticalChance, 1, 9_999);
            Assert.True(Game.GolemCriticalStrike(actualCriticalAbility, golem.Monster.Mods, actualFinalCriticalChance - 1));
            Assert.False(Game.GolemCriticalStrike(actualCriticalAbility, golem.Monster.Mods, actualFinalCriticalChance));

            // Start a separate feedback connection so changing the maximum before summon cannot race the 9.0 refresh.
            const string feedbackCharacter = "AncientFeedback";
            ClientState? feedback = null;
            try
            {
                feedback = new ClientState(new(1, "ancient-feedback", 1000, 1000, 100, 100, null, []), 2, 1)
                {
                    Character = feedbackCharacter, Instance = new(), PlayerLife = 10000, PlayerMana = 10000, PlayerSent = true,
                    Experience = CharacterStats.ExperienceFor(100),
                    Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    LeagueModule = conn.LeagueModule,
                };
                foreach (string stat in new[] { "virtual_strength_for_requirements",
                             "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                    feedback.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
                feedback.Golems.Restored = true;

                var feedbackInventory = Game.ItemsOf(feedback);
                var feedbackSkull = new UniqueCatalog(Program.Items).Create("Ancient Skull", new Random(504))!;
                var feedbackSkullLife = feedbackSkull.Mods.Single(mod =>
                    Program.Items.Mods.ByHash(mod.Hash)?.Id == "MinionLifeUnique__5_");
                int actualSkullLifeRoll = feedbackSkullLife.Values.Single();
                Assert.InRange(actualSkullLifeRoll, -40, -20);
                feedbackInventory.Add(PlayerInventories.Helmet, 0, 0, feedbackSkull);
                var feedbackGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSummonFlameGolem")!);
                var extraEnergyShield = Program.Items.Mods.ById(
                    "WeaponTreeMinionLifeAsExtraEnergyShieldReducedElementalResistance1")!;
                var extraEnergyShieldRoll = extraEnergyShield.Roll(ModSection.Explicit, new Random(504));
                Assert.Equal(15, extraEnergyShieldRoll.Values[0]);
                Assert.Equal(-6, extraEnergyShieldRoll.Values[1]);
                var feedbackWeapon = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with
                {
                    Sockets = [new(SocketColour.White, feedbackGem)], Mods = [.. ItemCatalog.Create(Program.Items.Find(
                        "Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!).Mods, extraEnergyShieldRoll],
                };
                feedbackInventory.Add(PlayerInventories.Weapon1, 0, 0, feedbackWeapon);
                var feedbackHost = feedbackInventory.Items(PlayerInventories.Weapon1).Single();
                Game.EquipmentStatSources(feedback, feedbackInventory.EquipmentSnapshot());
                feedback.Pools = Game.PoolsFor(feedback, feedback.Pools.Level);

                uint feedbackMaximumPowerCharges = Program.Stats.Key("max_power_charges")!.Value;
                feedback.PlayerStats[feedbackMaximumPowerCharges] = 61;
                var feedbackOwnerStats = Game.CharacterPlayerStats(feedback);
                Assert.Equal(64, Charges.LimitsOf(ChargeType.Power, name =>
                    feedbackOwnerStats.GetValueOrDefault(Program.Stats.Key(name) ?? 0)).Maximum);

                var feedbackDefinition = Game.GolemData.Find("SummonFlameGolem")!;
                var feedbackSource = Assert.Single(Game.GemSkillEffects(feedback),
                    g => Game.SkillsFor(feedback).EffectId(g.Effect) == "SummonFlameGolem");
                var energyShieldPackets = new List<byte[]>();
                var beforeEnergyShieldOutgoing = Program.Outgoing;
                Program.Outgoing = (ciphers, bytes) =>
                {
                    if (ReferenceEquals(ciphers, wire.Ciphers)) lock (energyShieldPackets) energyShieldPackets.Add(bytes.ToArray());
                    beforeEnergyShieldOutgoing?.Invoke(ciphers, bytes);
                };
                uint feedbackGolemId;
                try
                {
                    feedbackGolemId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, feedback,
                        feedbackSource.Id, feedbackDefinition, (100, 100));
                }
                finally { Program.Outgoing = beforeEnergyShieldOutgoing; }
                Assert.NotEqual(0u, feedbackGolemId);
                var feedbackGolem = feedback.Golems.All[feedbackGolemId];
                Assert.Same(feedbackGolem.Monster, feedback.Monsters[feedbackGolemId]);
                Assert.False(feedbackGolem.Monster.Dead);
                Assert.True(feedbackGolem.Monster.Life > 0);
                var feedbackAbility = feedbackGolem.Abilities.First(a => a.Crit > 0);
                Assert.True(feedbackAbility.Attack.Spell);
                Assert.Equal(64 * AncientSkullCriticalChancePerMaximumPowerCharge,
                    feedbackGolem.Monster.Mods.Effects["critical_strike_chance_+%"]);
                Assert.Equal(10_000, Game.GolemCriticalChance(feedbackAbility, feedbackGolem.Monster.Mods));
                int feedbackOwnLifeMore = feedbackGolem.Own.GetValueOrDefault(
                    Program.Stats.Key("active_skill_minion_life_+%_final")!.Value);
                Assert.Equal(0, feedbackOwnLifeMore);
                Assert.Equal(0, feedbackGolem.Own.GetValueOrDefault(
                    Program.Stats.Key("support_minion_maximum_life_+%_final")!.Value));
                Assert.Equal(0, feedbackGolem.Own.GetValueOrDefault(
                    Program.Stats.Key("support_minion_damage_minion_life_+%_final")!.Value));
                Assert.Equal(0, feedbackGolem.Own.GetValueOrDefault(
                    Program.Stats.Key("minion_maximum_life_+%")!.Value));
                Assert.Equal(actualSkullLifeRoll, feedbackGolem.Player.GetValueOrDefault(
                    Program.Stats.Key("minion_maximum_life_+%")!.Value));
                Assert.Equal(0, feedbackGolem.Player.GetValueOrDefault(
                    Program.Stats.Key("golem_maximum_life_+%")!.Value));
                Assert.Equal(0, feedbackGolem.Player.GetValueOrDefault(
                    Program.Stats.Key("active_skill_non_reaper_minion_maximum_life_+%_final")!.Value));
                Assert.Equal(0, feedbackGolem.Player.GetValueOrDefault(
                    Program.Stats.Key("elemental_golems_maximum_life_is_doubled")!.Value));
                Assert.Equal(0, feedbackGolem.Gear.GetValueOrDefault("base_maximum_life"));
                Assert.Equal(0, feedbackGolem.Gear.GetValueOrDefault("maximum_life_+%"));
                Assert.Equal(15, feedbackGolem.Monster.Mods.Effects[
                    "maximum_life_%_to_add_as_maximum_energy_shield"]);
                Assert.Equal(0, feedbackGolem.Monster.Mods.Effects["maximum_energy_shield_+%"]);
                Assert.Equal(0, feedbackGolem.Monster.Mods.Effects["global_defences_+%"]);
                Assert.Equal(0, feedbackGolem.Monster.Mods.Effects["energy_shield_+%_final_for_minion"]);
                Assert.Equal(0, feedbackGolem.Own.GetValueOrDefault(
                    Program.Stats.Key("active_skill_minion_energy_shield_+%_final")!.Value));
                Assert.Equal(0, feedbackGolem.Auras.GetValueOrDefault("base_maximum_energy_shield"));
                Assert.Equal(0, feedbackGolem.Gear.GetValueOrDefault("base_maximum_energy_shield"));
                var actualVariety = feedbackGolem.Monster.Variety!;
                byte actualLevel = feedbackGolem.Monster.Level;
                double expectedServerShield = .15 * (double)Game.Monsters.BaseLife(actualVariety, actualLevel)!.Value
                    * (1 + actualSkullLifeRoll / 100d);
                uint expectedEnergyShield = (uint)Math.Floor(expectedServerShield);
                var feedbackPools = Game.GolemPools.Calculate(Game.Monsters, actualVariety, actualLevel,
                    feedbackGolem, feedbackGolem.Player, feedbackGolem.Monster.Mods.Effects);
                Assert.Equal(expectedServerShield, feedbackPools.MaximumEnergyShield, 10);
                Assert.Equal(expectedEnergyShield, feedbackGolem.MaximumShield);
                Assert.Equal(feedbackGolem.MaximumShield, feedbackGolem.Shield);
                var energyShieldOrigin = feedback.MinionOrigins[feedbackGolemId];
                Assert.Equal(expectedEnergyShield, energyShieldOrigin.Object.EnergyShield);
                Assert.Equal(expectedEnergyShield, (uint)energyShieldOrigin.Object.Stats!.Single(
                    stat => stat.Key == maximumEnergyShield).Value);
                var emittedCreate = Assert.Single(energyShieldPackets, bytes => bytes.Length >= 18
                    && BinaryPrimitives.ReadUInt16BigEndian(bytes) == InstanceEntities.CreateObjectId
                    && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2)) == feedbackGolemId);
                int blobLength = BinaryPrimitives.ReadUInt16BigEndian(emittedCreate.AsSpan(16));
                var emittedBlob = emittedCreate.AsSpan(18, blobLength).ToArray();
                int cursor = 2 + 18;
                uint Compact()
                {
                    byte first = emittedBlob[cursor++];
                    int following = first < 0x80 ? 0 : first < 0xc0 ? 1 : first < 0xe0 ? 2 : first < 0xf0 ? 3 : 4;
                    uint value = following == 4 ? 0u : (uint)(first & (0x7f >> following));
                    for (int i = 0; i < following; i++) value = (value << 8) | emittedBlob[cursor++];
                    return value;
                }
                var emittedStats = new Dictionary<uint, uint>();
                for (uint count = Compact(), i = 0; i < count; i++) emittedStats[Compact()] = Compact();
                Assert.Equal(expectedEnergyShield, emittedStats[maximumEnergyShield]);
                byte[] Pool(uint current)
                {
                    var pool = new byte[18];
                    BinaryPrimitives.WriteUInt32LittleEndian(pool, current);
                    return pool;
                }
                Assert.True(emittedBlob.IndexOf([.. Pool(feedbackGolem.Monster.Life), .. Pool(0), .. Pool(expectedEnergyShield)]) >= 0);

                const uint feedbackTargetId = 0x08000002;
                const double feedbackHitAt = 1;
                var feedbackFrom = feedbackGolem.Monster.Walk.Position(feedbackHitAt);
                var feedbackTarget = new SpawnedMonster("ancient skull feedback target",
                    new(feedbackFrom.X + 17, feedbackFrom.Y - 9, 0), 100, 8, 1_000_000);
                feedback.Monsters[feedbackTargetId] = feedbackTarget;
                int feedbackOutputStart = feedback.Minions.OutputQueue.Count;
                uint lifeBeforeFeedbackHit = feedbackTarget.Life;
                try
                {
                    Assert.True(Game.GolemHitLocked("test", wire.Stream, wire.Ciphers, feedback, feedbackGolem,
                        feedbackAbility, feedbackTargetId, feedbackTarget, feedbackHitAt));
                }
                finally
                {
                    feedback.Monsters.Remove(feedbackTargetId);
                }
                Assert.True(feedbackTarget.Life < lifeBeforeFeedbackHit);

                var feedbackOutputs = feedback.Minions.OutputQueue.Skip(feedbackOutputStart).ToArray();
                var hitOutput = feedbackOutputs.Single(output => output.Packet.Length >= 2
                    && BinaryPrimitives.ReadUInt16BigEndian(output.Packet) == InstanceProtocol.ActionHitId);
                var hitPacket = hitOutput.Packet;
                Assert.Equal(25, hitPacket.Length);
                Assert.Equal(feedbackGolem.Id, BinaryPrimitives.ReadUInt32BigEndian(hitPacket.AsSpan(2)));
                Assert.Equal((ushort)0xffff, BinaryPrimitives.ReadUInt16BigEndian(hitPacket.AsSpan(12)));
                Assert.Equal(feedbackTargetId, BinaryPrimitives.ReadUInt32BigEndian(hitPacket.AsSpan(16)));
                Assert.Equal((ushort)0x4221, BinaryPrimitives.ReadUInt16BigEndian(hitPacket.AsSpan(20)));
                Assert.Equal(0x03, hitPacket[22]);
                Assert.Equal(Game.HitCode(feedbackAbility.Attack.Damage), hitPacket[23]);
                var feedbackTo = feedbackTarget.Walk.Position(feedbackHitAt);
                Assert.Equal(Game.HitAngle((int)feedbackTo.X - feedbackFrom.X, (int)feedbackTo.Y - feedbackFrom.Y), hitPacket[24]);
                var lifeOutput = feedbackOutputs.Single(output => output.Kind == PlayerMinions.OutputKind.Life);
                Assert.Equal(feedbackTargetId, lifeOutput.TargetId);
                Assert.Same(feedbackTarget, lifeOutput.Target);
                Assert.Equal(feedbackTarget.Life, lifeOutput.Target!.Life);

                async Task<List<byte[]>> Capture(ConnectionCiphers ciphers, Func<Task> send)
                {
                    var packets = new List<byte[]>();
                    var previous = Program.Outgoing;
                    Program.Outgoing = (seen, bytes) =>
                    {
                        if (ReferenceEquals(seen, ciphers)) lock (packets) packets.Add(bytes.ToArray());
                        previous?.Invoke(seen, bytes);
                    };
                    try { await send(); }
                    finally { Program.Outgoing = previous; }
                    return packets;
                }
                void AssertEnergyShieldEmission(IReadOnlyList<byte[]> packets, uint id, uint maximum, uint current)
                {
                    var maximumPacket = InstanceEntities.StatsUpdate(id, [(maximumEnergyShield, (int)maximum)]);
                    var currentPacket = InstanceProtocol.LifeUpdate(id, current, pool: 2);
                    Assert.Contains(packets, packet =>
                    {
                        var bytes = packet.AsSpan();
                        int atMaximum = bytes.IndexOf(maximumPacket), atCurrent = bytes.IndexOf(currentPacket);
                        return atMaximum >= 0 && atCurrent > atMaximum;
                    });
                }
                uint MaximumFrom() => (uint)feedback.MinionOrigins[feedbackGolemId].Object.Stats!
                    .Single(stat => stat.Key == maximumEnergyShield).Value;

                Assert.Equal((uint)int.MaxValue, Game.NativeEnergyShield(double.PositiveInfinity));
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, feedback, 2.05);
                var stableStats = feedback.MinionOrigins[feedbackGolemId].Object.Stats;
                var stableAbilities = feedbackGolem.Abilities;
                feedbackGolem.LastStep = 2;
                var esAttacker = new SpawnedMonster("energy shield attacker", new(100, 100, 1), 100, 8, 1_000_000);
                var esHit = new MonsterAttack(0x4000, 1, 100,
                    DamagePacket.Of(DamageType.Fire, Math.Max(1, (int)expectedEnergyShield / 4),
                        Math.Max(1, (int)expectedEnergyShield / 4))) { Spell = true };
                uint esLifeBefore = feedbackGolem.Monster.Life;
                double esBefore = feedbackGolem.Shield;
                Assert.True(Game.GolemTakeHitLocked(feedback, feedbackGolem, 0x08000000, esAttacker, esHit, 2.1));
                Assert.Equal(esLifeBefore, feedbackGolem.Monster.Life);
                Assert.True(feedbackGolem.Shield < esBefore);
                Assert.Same(stableStats, feedback.MinionOrigins[feedbackGolemId].Object.Stats);
                Assert.Same(stableAbilities, feedbackGolem.Abilities);
                uint currentAfterAbsorption = Game.NativeEnergyShield(feedbackGolem.Shield);
                var absorptionPackets = await Capture(wire.Ciphers,
                    () => Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, feedback));
                AssertEnergyShieldEmission(absorptionPackets, feedbackGolemId, MaximumFrom(), currentAfterAbsorption);

                var chaosHit = esHit with { Damage = DamagePacket.Of(DamageType.Chaos, 1, 1) };
                double shieldBeforeChaos = feedbackGolem.Shield; uint lifeBeforeChaos = feedbackGolem.Monster.Life;
                Assert.True(Game.GolemTakeHitLocked(feedback, feedbackGolem, 0x08000000, esAttacker, chaosHit, 2.15));
                Assert.Equal(shieldBeforeChaos, feedbackGolem.Shield);
                Assert.Equal(lifeBeforeChaos - 1, feedbackGolem.Monster.Life);

                // Both hits happen before the real drain: the one deduplicated output must project current zero,
                // never the positive value captured by the first hit. The physical remainder spills to life.
                int spill = Math.Max(1, (int)Math.Floor((double)feedbackGolem.Shield) / 2);
                var spillHit = esHit with { Damage = DamagePacket.Of(DamageType.Physical, spill, spill) };
                uint lifeBeforeSpill = feedbackGolem.Monster.Life;
                Assert.True(Game.GolemTakeHitLocked(feedback, feedbackGolem, 0x08000000, esAttacker, spillHit, 2.2));
                Assert.True(feedbackGolem.Shield > 0);
                int remainder = (int)Math.Floor((double)feedbackGolem.Shield) + 7;
                Assert.True(Game.GolemTakeHitLocked(feedback, feedbackGolem, 0x08000000, esAttacker,
                    spillHit with { Damage = DamagePacket.Of(DamageType.Physical, remainder, remainder) }, 2.25));
                Assert.Equal(0u, feedbackGolem.Shield);
                Assert.Equal(lifeBeforeSpill - 7, feedbackGolem.Monster.Life);
                var deferredPackets = await Capture(wire.Ciphers,
                    () => Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, feedback));
                AssertEnergyShieldEmission(deferredPackets, feedbackGolemId, MaximumFrom(), 0);
                Assert.Contains(deferredPackets, packet => packet.AsSpan().IndexOf(
                    InstanceProtocol.LifeUpdate(feedbackGolemId, feedbackGolem.Monster.Life)) >= 0);

                feedbackGolem.LastStep = 2.5;
                var rechargePackets = await Capture(wire.Ciphers,
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, feedback, 5));
                uint recharged = Game.NativeEnergyShield(feedbackGolem.Shield);
                Assert.InRange(recharged, 1u, MaximumFrom() - 1);
                AssertEnergyShieldEmission(rechargePackets, feedbackGolemId, MaximumFrom(), recharged);
                var rechargedStats = feedback.MinionOrigins[feedbackGolemId].Object.Stats;
                var rechargedAbilities = feedbackGolem.Abilities;
                var repeatedPackets = await Capture(wire.Ciphers,
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, feedback, 5));
                Assert.DoesNotContain(repeatedPackets, packet => packet.AsSpan().IndexOf(
                    InstanceProtocol.LifeUpdate(feedbackGolemId, recharged, pool: 2)) >= 0);
                Assert.Same(rechargedStats, feedback.MinionOrigins[feedbackGolemId].Object.Stats);
                Assert.Same(rechargedAbilities, feedbackGolem.Abilities);

                // Removing only the real tree modifier keeps the gem host, item id and sockets intact. The maximum
                // stat is explicitly zeroed and pool 2 is zero in the same live-source output.
                var baseBow = ItemCatalog.Create(Program.Items.Find(
                    "Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!);
                feedbackInventory.Change(PlayerInventories.Weapon1, feedbackHost.Id,
                    item => item with { Mods = baseBow.Mods });
                Game.EquipmentStatSources(feedback, feedbackInventory.EquipmentSnapshot());
                feedback.Pools = Game.PoolsFor(feedback, feedback.Pools.Level);
                var sourceOffPackets = await Capture(wire.Ciphers,
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, feedback, 5.6));
                Assert.Equal(0u, feedbackGolem.MaximumShield);
                Assert.Equal(0u, feedbackGolem.Shield);
                AssertEnergyShieldEmission(sourceOffPackets, feedbackGolemId, 0, 0);
                Assert.Equal(0, Assert.Single(feedback.MinionOrigins[feedbackGolemId].Object.Stats!,
                    stat => stat.Key == maximumEnergyShield).Value);

                // A peer created only after the real provider retires receives an explicit zero maximum and zero pool.
                using var zeroPeerWire = new Wire();
                var zeroPeer = new ClientState(new(1, "energy-shield-zero-peer", 1000, 1000, 100, 100, null, []), 1, 1)
                {
                    Character = "EnergyShieldZeroPeer", Instance = feedback.Instance, PlayerSent = true,
                    PlayerLife = 10000, PlayerMana = 10000,
                };
                feedback.Instance.PublishPlayer(new(feedback.PeerId, 2, new("Owner", 1, 100, 100, 100, 100),
                    100, 100, 100, feedback));
                feedback.Instance.PublishPlayer(new(zeroPeer.PeerId, 2, new("ZeroPeer", 1, 100, 100, 100, 100),
                    100, 100, 100, zeroPeer));
                Game.PublishAwakeMonsters(feedback);
                var zeroPeerPackets = await Capture(zeroPeerWire.Ciphers,
                    () => Game.SyncPeerMinions("zero-peer", zeroPeerWire.Stream, zeroPeerWire.Ciphers, zeroPeer));
                uint zeroMirroredId = zeroPeer.MirroredObjects[(feedback.PeerId, feedbackGolemId)];
                var zeroPublished = Assert.Single(feedback.PublishedMinions, minion => minion.Id == feedbackGolemId);
                var zeroOrigin = feedback.MinionOrigins[feedbackGolemId];
                Assert.Equal(0u, zeroOrigin.Object.EnergyShield);
                Assert.Equal(0, Assert.Single(zeroOrigin.Object.Stats!, stat => stat.Key == maximumEnergyShield).Value);
                var zeroCreate = InstanceEntities.CreateObject(zeroMirroredId, zeroOrigin.Type, (zeroOrigin.Object with
                {
                    GridX = zeroPublished.X, GridY = zeroPublished.Y, Life = zeroPublished.Life, EnergyShield = 0,
                }).Blob());
                Assert.Contains(zeroPeerPackets, packet => packet.AsSpan().IndexOf(zeroCreate) >= 0);
                feedback.Instance.RemovePlayer(zeroPeer.PeerId);

                feedbackInventory.Change(PlayerInventories.Weapon1, feedbackHost.Id,
                    item => item with { Mods = [.. baseBow.Mods, extraEnergyShieldRoll] });
                Game.EquipmentStatSources(feedback, feedbackInventory.EquipmentSnapshot());
                feedback.Pools = Game.PoolsFor(feedback, feedback.Pools.Level);
                var sourceOnPackets = await Capture(wire.Ciphers,
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, feedback, 6.2));
                uint partialAfterReturn = Game.NativeEnergyShield(feedbackGolem.Shield);
                Assert.InRange(partialAfterReturn, 1u, MaximumFrom() - 1);
                AssertEnergyShieldEmission(sourceOnPackets, feedbackGolemId, MaximumFrom(), partialAfterReturn);

                // A late peer receives the current absorbed/recharged value, not a full-shield birth snapshot.
                using var peerWire = new Wire();
                var peer = new ClientState(new(1, "energy-shield-peer", 1000, 1000, 100, 100, null, []), 1, 1)
                {
                    Character = "EnergyShieldPeer", Instance = feedback.Instance, PlayerSent = true,
                    PlayerLife = 10000, PlayerMana = 10000,
                };
                feedback.Instance.PublishPlayer(new(feedback.PeerId, 2, new("Owner", 1, 100, 100, 100, 100),
                    100, 100, 100, feedback));
                feedback.Instance.PublishPlayer(new(peer.PeerId, 2, new("Peer", 1, 100, 100, 100, 100),
                    100, 100, 100, peer));
                Game.PublishAwakeMonsters(feedback);
                var lateCreatePackets = await Capture(peerWire.Ciphers,
                    () => Game.SyncPeerMinions("peer", peerWire.Stream, peerWire.Ciphers, peer));
                uint mirroredId = peer.MirroredObjects[(feedback.PeerId, feedbackGolemId)];
                var published = Assert.Single(feedback.PublishedMinions, minion => minion.Id == feedbackGolemId);
                var mirroredOrigin = feedback.MinionOrigins[feedbackGolemId];
                var lateCreate = InstanceEntities.CreateObject(mirroredId, mirroredOrigin.Type, (mirroredOrigin.Object with
                {
                    GridX = published.X, GridY = published.Y, Life = published.Life, EnergyShield = partialAfterReturn,
                }).Blob());
                Assert.Contains(lateCreatePackets, packet => packet.AsSpan().IndexOf(lateCreate) >= 0);
                Assert.NotEqual(MaximumFrom(), partialAfterReturn);

                // The peer's retirement change contains maximum first, current second, including both explicit zeroes.
                feedbackInventory.Change(PlayerInventories.Weapon1, feedbackHost.Id,
                    item => item with { Mods = baseBow.Mods });
                Game.EquipmentStatSources(feedback, feedbackInventory.EquipmentSnapshot());
                feedback.Pools = Game.PoolsFor(feedback, feedback.Pools.Level);
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, feedback, 7);
                Game.PublishAwakeMonsters(feedback);
                var peerRetirementPackets = await Capture(peerWire.Ciphers,
                    () => Game.SyncPeerMinions("peer", peerWire.Stream, peerWire.Ciphers, peer));
                AssertEnergyShieldEmission(peerRetirementPackets, mirroredId, 0, 0);

                feedbackInventory.Change(PlayerInventories.Weapon1, feedbackHost.Id,
                    item => item with { Mods = [.. baseBow.Mods, extraEnergyShieldRoll] });
                Game.EquipmentStatSources(feedback, feedbackInventory.EquipmentSnapshot());
                feedback.Pools = Game.PoolsFor(feedback, feedback.Pools.Level);
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, feedback, 7.6);
                Assert.InRange(Game.NativeEnergyShield(feedbackGolem.Shield), 1u, MaximumFrom() - 1);
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, feedback, 10.7);
                Assert.Equal(MaximumFrom(), Game.NativeEnergyShield(feedbackGolem.Shield));

                // Queue a real positive ES update while retirement is still ineligible, then remove the real host.
                int retirementOutputStart = feedback.Minions.OutputQueue.Count;
                var retirementHit = esHit with { Damage = DamagePacket.Of(DamageType.Fire,
                    Math.Max(1, (int)MaximumFrom() / 4), Math.Max(1, (int)MaximumFrom() / 4)) };
                uint retirementLife = feedbackGolem.Monster.Life;
                Assert.True(Game.GolemTakeHitLocked(feedback, feedbackGolem, 0x08000000, esAttacker, retirementHit, 10.8));
                Assert.Equal(retirementLife, feedbackGolem.Monster.Life);
                uint queuedPositive = Game.NativeEnergyShield(feedbackGolem.Shield);
                Assert.InRange(queuedPositive, 1u, MaximumFrom() - 1);
                Assert.Single(feedback.Minions.OutputQueue.Skip(retirementOutputStart),
                    output => output.Kind == PlayerMinions.OutputKind.EnergyShield
                        && output.TargetId == feedbackGolemId);

                feedbackInventory.Remove(PlayerInventories.Weapon1, feedbackHost.Id);
                Game.EquipmentStatSources(feedback, feedbackInventory.EquipmentSnapshot());
                feedback.Pools = Game.PoolsFor(feedback, feedback.Pools.Level);
                var retiredPackets = await Capture(wire.Ciphers,
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, feedback, 11.3));
                Assert.False(feedback.MinionOrigins.ContainsKey(feedbackGolemId));
                Assert.DoesNotContain(retiredPackets, packet => packet.AsSpan().IndexOf(
                    InstanceProtocol.LifeUpdate(feedbackGolemId, 0, pool: 2)) >= 0);
                Assert.DoesNotContain(retiredPackets, packet => packet.AsSpan().IndexOf(
                    InstanceProtocol.LifeUpdate(feedbackGolemId, queuedPositive, pool: 2)) >= 0);

                // A positive native ES actor crossing the fatal conversion in the in-step refresh emits an explicit
                // zero maximum/current and performs no recovery or action after the refresh marked it dead.
                feedbackInventory.Add(PlayerInventories.Weapon1, 0, 0, feedbackWeapon);
                Game.EquipmentStatSources(feedback, feedbackInventory.EquipmentSnapshot());
                feedback.Pools = Game.PoolsFor(feedback, feedback.Pools.Level);
                var fatalSource = Assert.Single(Game.GemSkillEffects(feedback),
                    g => Game.SkillsFor(feedback).EffectId(g.Effect) == "SummonFlameGolem");
                uint fatalId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, feedback,
                    fatalSource.Id, feedbackDefinition, (100, 100));
                var fatalGolem = feedback.Golems.All[fatalId];
                uint fatalBornMaximum = Game.NativeEnergyShield(fatalGolem.MaximumShield);
                var fatalBornOrigin = feedback.MinionOrigins[fatalId];
                Assert.True(fatalBornMaximum > 0);
                Assert.Equal(fatalBornMaximum, Game.NativeEnergyShield(fatalGolem.Shield));
                Assert.True(fatalBornOrigin.Object.EnergyShield > 0);
                Assert.Equal((int)fatalBornMaximum,
                    fatalBornOrigin.Object.Stats!.Single(stat => stat.Key == maximumEnergyShield).Value);
                feedback.Golems.RefreshAt = double.PositiveInfinity;
                fatalGolem.Player[Program.Stats.Key(
                    "minion_maximum_life_%_to_convert_to_maximum_energy_shield_per_1%_chaos_resistance")!.Value] = 2;
                fatalGolem.Player[Program.Stats.Key("minion_chaos_resistance_%")!.Value] = 40;
                double fatalLastStep = fatalGolem.LastStep;
                var fatalPackets = await Capture(wire.Ciphers,
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, feedback, 11.2));
                Assert.True(fatalGolem.Monster.Dead);
                Assert.Equal(0u, fatalGolem.Monster.Life);
                Assert.Equal(1u, fatalGolem.Monster.MaxLife);
                Assert.Equal(0u, fatalGolem.MaximumShield);
                Assert.Equal(0u, fatalGolem.Shield);
                Assert.Null(fatalGolem.Pending);
                Assert.Equal(fatalLastStep, fatalGolem.LastStep);
                var fatalOrigin = feedback.MinionOrigins[fatalId];
                Assert.Equal(0u, fatalOrigin.Object.EnergyShield);
                Assert.Empty(fatalOrigin.Object.Skills ?? []);
                Assert.Equal(0, Assert.Single(fatalOrigin.Object.Stats!,
                    stat => stat.Key == maximumEnergyShield).Value);
                var fatalMaximumPacket = InstanceEntities.StatsUpdate(fatalId, [(maximumEnergyShield, 0)]);
                var fatalCurrentPacket = InstanceProtocol.LifeUpdate(fatalId, 0, pool: 2);
                Assert.Contains(fatalPackets, packet =>
                {
                    var bytes = packet.AsSpan();
                    int maximumAt = bytes.IndexOf(fatalMaximumPacket), currentAt = bytes.IndexOf(fatalCurrentPacket);
                    return maximumAt >= 0 && currentAt > maximumAt;
                });
            }
            finally
            {
                if (feedback is not null)
                {
                    feedback.Closed = true;
                    foreach (var player in feedback.Instance.Presence.PlayersExcept(feedback.PeerId))
                        feedback.Instance.RemovePlayer(player.Id);
                    PlayerInventories.Forget(feedbackCharacter);
                    Game.InactiveItems.TryRemove(feedbackCharacter, out _);
                }
            }

            const string fleshCharacter = "FleshPools";
            var fleshSchedule = new AreaClockSchedule();
            var fleshUpdates = new PlayerUpdatesState(lifetime, fleshSchedule);
            var previousFleshContext = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = fleshUpdates;
            ClientState? flesh = null;
            try
            {
                fleshSchedule.At = 6;
                flesh = new ClientState(new(1, "flesh-pools", 1000, 1000, 100, 100, null, []), 4, 1)
                {
                    Character = fleshCharacter, Instance = new(), PlayerLife = 10000, PlayerMana = 10000,
                    PlayerSent = true, Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = fleshUpdates,
                    Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    LeagueModule = conn.LeagueModule,
                };
                flesh.Golems.Restored = true;
                foreach (string stat in new[] { "virtual_strength_for_requirements",
                             "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                    flesh.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
                flesh.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 1;
                // Diagnostic-only fixture control: cancel the golem controller's ordinary 1% life/second regeneration.
                flesh.PlayerStats[Program.Stats.Key("minion_life_regeneration_rate_per_minute_%")!.Value] = -60;

                var fleshInventory = Game.ItemsOf(flesh);
                var flameGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSummonFlameGolem")!);
                var chaosGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSummonChaosGolem")!);
                fleshInventory.Add(PlayerInventories.Weapon1, 0, 0,
                    ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with
                    { Sockets = [new(SocketColour.White, flameGem), new(SocketColour.White, chaosGem)], Links = [2] });
                void RecalculateFlesh()
                {
                    Game.EquipmentStatSources(flesh, fleshInventory.EquipmentSnapshot());
                    flesh.Pools = Game.PoolsFor(flesh, flesh.Pools.Level);
                    flesh.PlayerLife = flesh.Pools.Life;
                    flesh.PlayerMana = flesh.Pools.Mana;
                }
                RecalculateFlesh();
                var flameSource = Assert.Single(Game.GemSkillEffects(flesh),
                    grant => Game.SkillsFor(flesh).EffectId(grant.Effect) == "SummonFlameGolem");
                var chaosSource = Assert.Single(Game.GemSkillEffects(flesh),
                    grant => Game.SkillsFor(flesh).EffectId(grant.Effect) == "SummonChaosGolem");
                var flameDefinition = Game.GolemData.Find("SummonFlameGolem")!;
                var chaosDefinition = Game.GolemData.Find("SummonChaosGolem")!;
                var flameVariety = Game.Monsters.Find(flameDefinition.Variety)!;
                var chaosVariety = Game.Monsters.Find(chaosDefinition.Variety)!;
                Assert.Equal(20, HitMitigation.Resistance(Game.Monsters, flameVariety, 70, DamageType.Chaos).Value);
                Assert.Equal(60, HitMitigation.Resistance(Game.Monsters, chaosVariety, 70, DamageType.Chaos).Value);

                uint flameId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, flesh,
                    flameSource.Id, flameDefinition, (100, 100));
                uint chaosId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, flesh,
                    chaosSource.Id, chaosDefinition, (100, 100));
                Assert.NotEqual(0u, flameId);
                Assert.NotEqual(0u, chaosId);
                var flame = flesh.Golems.All[flameId];
                var chaos = flesh.Golems.All[chaosId];
                Assert.False(flame.Monster.Dead);
                Assert.False(chaos.Monster.Dead);

                var fleshArmour = new UniqueCatalog(Program.Items).Create("Fleshcrafter", new Random(504))!;
                var conversionMod = Program.Items.Mods.ById("MinionLifeConvertedToEnergyShieldUnique__1")!;
                Assert.Equal(2, conversionMod.Stats[0].Min);
                Assert.Equal("minion_maximum_life_%_to_convert_to_maximum_energy_shield_per_1%_chaos_resistance",
                    conversionMod.Stats[0].Id);
                string[] fleshRows =
                [
                    "LocalIncreasedEnergyShieldUniqueBodyInt1", "MinionLifeConvertedToEnergyShieldUnique__1",
                    "MinionChaosDamageDoesNotBypassESUnique__1", "MinionEnergyShieldRechargeDelayUnique__1",
                    "MinionHitsIgnoreResistanceWithESUnique__1_",
                ];
                foreach (string row in fleshRows)
                    Assert.Contains(fleshArmour.Mods, mod => Program.Items.Mods.ByHash(mod.Hash)?.Id == row);
                Assert.Equal(5, fleshArmour.Mods.Count(mod =>
                    fleshRows.Contains(Program.Items.Mods.ByHash(mod.Hash)?.Id)));
                Assert.Equal(2, fleshArmour.Mods.Single(mod => mod.Hash == conversionMod.Hash).Values[0]);
                var fleshArmourSlot = fleshInventory.Add(PlayerInventories.BodyArmour, 0, 0, fleshArmour);
                RecalculateFlesh();

                async Task<List<byte[]>> CaptureFlesh(Func<Task> send)
                {
                    var packets = new List<byte[]>();
                    var previous = Program.Outgoing;
                    Program.Outgoing = (seen, bytes) =>
                    {
                        if (ReferenceEquals(seen, wire.Ciphers)) lock (packets) packets.Add(bytes.ToArray());
                        previous?.Invoke(seen, bytes);
                    };
                    try { await send(); }
                    finally { Program.Outgoing = previous; }
                    return packets;
                }

                fleshSchedule.At = 6.5;
                var refreshPackets = await CaptureFlesh(
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, flesh, fleshSchedule.At));
                var flameConversion = Game.GolemPools.Calculate(Game.Monsters, flame.Monster.Variety!,
                    flame.Monster.Level, flame, flame.Player, flame.Monster.Mods.Effects);
                Assert.Equal(40, flameConversion.ConversionPercent);
                Assert.InRange(flameConversion.MaximumLife, 1u, (uint)int.MaxValue);
                Assert.Equal(flameConversion.MaximumLife, flame.Monster.MaxLife);
                Assert.True(chaos.Monster.Dead);
                Assert.Equal(0u, chaos.Monster.Life);
                Assert.Equal(1u, chaos.Monster.MaxLife);
                Assert.Empty(chaos.Abilities);
                Assert.Empty(chaos.Skills);
                Assert.Empty(chaos.OwnerBuff);
                Assert.Empty(chaos.Hazards);
                Assert.Contains(refreshPackets, packet => packet.AsSpan().IndexOf(
                    InstanceProtocol.LifeUpdate(chaosId, 0)) >= 0);

                // The second automatic Chaos Golem is admitted through the ordinary create shape, then becomes a real
                // dead roster actor in the same create/wake gate. Its transport maximum stays positive.
                fleshSchedule.At = 8.5;
                var revivePackets = await CaptureFlesh(
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, flesh, fleshSchedule.At));
                var revivedChaos = Assert.Single(flesh.Golems.All.Values,
                    golem => golem.Definition.Kind == "Chaos" && golem.Id != chaosId);
                Assert.NotEqual(chaosId, revivedChaos.Id);
                Assert.True(revivedChaos.Monster.Dead);
                Assert.Equal(0u, revivedChaos.Monster.Life);
                Assert.Equal(1u, revivedChaos.Monster.MaxLife);
                var chaosOrigin = flesh.MinionOrigins[revivedChaos.Id];
                var createPacket = Assert.Single(revivePackets, packet => packet.Length >= 18
                    && BinaryPrimitives.ReadUInt16BigEndian(packet) == InstanceEntities.CreateObjectId
                    && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(2)) == revivedChaos.Id);
                var deathPacket = InstanceProtocol.LifeUpdate(revivedChaos.Id, 0);
                Assert.Contains(revivePackets, packet =>
                {
                    var bytes = packet.AsSpan();
                    int createAt = bytes.IndexOf(createPacket), deathAt = bytes.IndexOf(deathPacket);
                    return createAt >= 0 && deathAt > createAt;
                });
                Assert.Equal(1u, chaosOrigin.Object.Life);
                Assert.Equal(1, chaosOrigin.Object.Stats!.Single(stat => stat.Key == StatKeys.MaximumLife).Value);

                Game.SaveGolems(flesh);
                var savedFlesh = JsonSerializer.Deserialize<GolemState.Saved[]>(
                    CharacterSaves.ModuleData(flesh.Character, "Game.Golems")!)!;
                var savedChaos = Assert.Single(savedFlesh, saved => saved.Effect == chaosDefinition.Id);
                Assert.Equal(0, savedChaos.Life);
                Assert.True(double.IsFinite(savedChaos.ReviveSeconds));
                Assert.InRange(savedChaos.ReviveSeconds, .001, 2);

                // The actual Bonemeld coefficient inherits the owner's final Chaos resistance into the prepared minion
                // effects before the same capped Chaos resistance and conversion calculation runs.
                var bonemeld = new UniqueCatalog(Program.Items).Create("Bonemeld", new Random(504))!;
                var inherit = bonemeld.Mods.Single(mod =>
                    Program.Items.Mods.ByHash(mod.Hash)?.Id == "MinionsGainPercentOfYourResistancesUnique_1");
                Assert.Equal(50, inherit.Values[0]);
                var amethystRing = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Rings/Ring10")!);
                Assert.Contains(amethystRing.Mods, mod => Program.Items.Mods.ByHash(mod.Hash)?.Stats.Any(
                    stat => stat.Id == "base_chaos_damage_resistance_%") == true);
                var bonemeldSlot = fleshInventory.Add(5, 0, 0, bonemeld);
                var amethystRingSlot = fleshInventory.Add(6, 0, 0, amethystRing);
                RecalculateFlesh();
                Assert.DoesNotContain(bonemeldSlot.Id, flesh.StatSourcesCache!.Inactive);
                int ownerChaos = Game.Resistance(flesh, DamageType.Chaos);
                Assert.InRange(ownerChaos, 1, GameServer.Core.World.DamageModel.MaxResistCap);
                fleshSchedule.At = 9;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, flesh, fleshSchedule.At);
                var inherited = Game.GolemPools.Calculate(Game.Monsters, flame.Monster.Variety!, flame.Monster.Level,
                    flame, flame.Player, flame.Monster.Mods.Effects);
                int inheritedChaos = (int)Math.Floor(ownerChaos * inherit.Values[0] / 100d);
                int expectedMinionChaos = 20 + inheritedChaos;
                int conversionCoefficient = flame.Monster.Mods.Effects[
                    "maximum_life_%_to_convert_to_maximum_energy_shield_per_1%_chaos_resistance"];
                int expectedConversion = Math.Clamp(conversionCoefficient * Math.Max(0, expectedMinionChaos), 0, 100);
                Assert.Equal(expectedMinionChaos, inherited.ChaosResistance);
                Assert.Equal(expectedConversion, inherited.ConversionPercent);
                Assert.True(inherited.ConversionPercent < 100);
                fleshInventory.Remove(5, bonemeldSlot.Id);
                fleshInventory.Remove(6, amethystRingSlot.Id);
                RecalculateFlesh();
                fleshSchedule.At = 9.5;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, flesh, fleshSchedule.At);
                var restoredFlame = Game.GolemPools.Calculate(Game.Monsters, flame.Monster.Variety!, flame.Monster.Level,
                    flame, flame.Player, flame.Monster.Mods.Effects);
                Assert.Equal(flameConversion.ChaosResistance, restoredFlame.ChaosResistance);
                Assert.Equal(flameConversion.ConversionPercent, restoredFlame.ConversionPercent);

                // Source off/on uses absolute current pools on the same actor: an increased maximum neither heals nor
                // refills, and a decreased maximum only clamps down.
                uint damagedLife = flame.Monster.MaxLife - 7;
                flame.Monster.Life = damagedLife;
                fleshInventory.Remove(PlayerInventories.BodyArmour, fleshArmourSlot.Id);
                RecalculateFlesh();
                fleshSchedule.At = 10;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, flesh, fleshSchedule.At);
                Assert.Same(flame, flesh.Golems.All[flameId]);
                Assert.True(flame.Monster.MaxLife > flameConversion.MaximumLife);
                Assert.True(damagedLife == flame.Monster.Life,
                    $"damagedLife={damagedLife}, currentLife={flame.Monster.Life}, maxLife={flame.Monster.MaxLife}, " +
                    $"maximumShield={flame.MaximumShield}, shield={flame.Shield}, ownerLife={flesh.PlayerLife}");
                Assert.Equal(0u, flame.MaximumShield);
                Assert.Equal(0u, flame.Shield);

                // Removing the source before the dead actor's next deadline permits a healthy revive while the gem is
                // still valid. Re-wearing the source clamps the now-full ordinary pool without granting a full ES pool.
                flame.Monster.Life = flame.Monster.MaxLife;
                fleshSchedule.At = 10.5;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, flesh, fleshSchedule.At);
                var healthyChaos = Assert.Single(flesh.Golems.All.Values,
                    golem => golem.Definition.Kind == "Chaos" && golem.Id != chaosId && golem.Id != revivedChaos.Id);
                Assert.False(healthyChaos.Monster.Dead);
                Assert.True(healthyChaos.Monster.Life > 0);
                fleshInventory.Add(PlayerInventories.BodyArmour, 0, 0, fleshArmour);
                RecalculateFlesh();
                fleshSchedule.At = 11;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, flesh, fleshSchedule.At);
                Assert.Same(flame, flesh.Golems.All[flameId]);
                Assert.Equal(flameConversion.MaximumLife, flame.Monster.MaxLife);
                Assert.Equal(flameConversion.MaximumLife, flame.Monster.Life);
                Assert.True(flame.MaximumShield > 0);
                Assert.Equal(flame.MaximumShield / 6, flame.Shield);
                Assert.True(flame.Shield < flame.MaximumShield);

                // Frozen-stat audit and independent arithmetic controls: B=1000, LifeInc=50, ESInc=100 and 40%
                // converted currently give Life=900 and ES=1000, not convertedES=1200 from a rounded final Life.
                uint minionES = Program.Stats.Key("minion_maximum_energy_shield_+%")!.Value;
                uint minionESFinal = Program.Stats.Key("energy_shield_+%_final_for_minion")!.Value;
                uint noArmourOrES = Program.Stats.Key("minions_have_no_armour_or_energy_shield")!.Value;
                uint activeESFinal = Program.Stats.Key("active_skill_minion_energy_shield_+%_final")!.Value;
                var mappedPlayer = new Dictionary<uint, int>(flame.Player)
                {
                    [minionES] = 100, [minionESFinal] = 100, [noArmourOrES] = 1,
                };
                var mappedEffects = Game.GolemModifiers(flesh, flame, mappedPlayer).Effects;
                Assert.Equal(100, mappedEffects["maximum_energy_shield_+%"]);
                Assert.Equal(100, mappedEffects["energy_shield_+%_final_for_minion"]);
                Assert.Equal(1, mappedEffects["minions_have_no_armour_or_energy_shield"]);

                int baseLife = Game.Monsters.BaseLife(flameVariety, 70) ?? 100;
                var control = new GolemState.Golem(0, null!, flameDefinition with { IsGolem = false }, 0)
                {
                    Gear = new Dictionary<string, int> { ["base_maximum_life"] = 1000 - baseLife },
                    Own = new Dictionary<uint, int> { [activeESFinal] = 0 },
                };
                var controlPlayer = new Dictionary<uint, int>
                    { [Program.Stats.Key("minion_maximum_life_+%")!.Value] = 50 };
                Game.GolemPools.Result Control(IReadOnlyDictionary<string, int> effects,
                    Dictionary<uint, int> own, Dictionary<string, int>? auras = null)
                {
                    control.Own = new(own);
                    control.Auras = auras ?? [];
                    return Game.GolemPools.Calculate(Game.Monsters, flameVariety, 70, control, controlPlayer,
                        new MonsterModEffects(effects));
                }
                control.Own = new()
                {
                    [minionESFinal] = 10, [noArmourOrES] = 1, [activeESFinal] = 25,
                };
                var additiveMinionEffects = Game.GolemModifiers(flesh, control, mappedPlayer).Effects;
                Assert.Equal(110, additiveMinionEffects["energy_shield_+%_final_for_minion"]);
                Assert.Equal(2, additiveMinionEffects["minions_have_no_armour_or_energy_shield"]);
                Assert.Equal(25, additiveMinionEffects["active_skill_minion_energy_shield_+%_final"]);
                var mappedNoShield = Game.GolemPools.Calculate(Game.Monsters, flameVariety, 70, control,
                    controlPlayer, additiveMinionEffects);
                Assert.Equal(0, mappedNoShield.MaximumEnergyShield);
                var converted = Control(new Dictionary<string, int>
                {
                    ["maximum_life_%_to_convert_to_maximum_energy_shield_per_1%_chaos_resistance"] = 2,
                    ["maximum_energy_shield_+%"] = 100,
                }, []);
                Assert.Equal(1000, converted.RawBaseLife);
                Assert.Equal(40, converted.ConversionPercent);
                Assert.Equal(900u, converted.MaximumLife);
                Assert.Equal(1000, converted.MaximumEnergyShield);
                var ordinaryGain = Control(new Dictionary<string, int>
                    { ["maximum_life_%_to_add_as_maximum_energy_shield"] = 15 }, []);
                Assert.Equal(225, ordinaryGain.MaximumEnergyShield);
                var gainAndConvert = Control(new Dictionary<string, int>
                {
                    ["maximum_life_%_to_add_as_maximum_energy_shield"] = 15,
                    ["maximum_life_%_to_convert_to_maximum_energy_shield_per_1%_chaos_resistance"] = 2,
                }, []);
                Assert.Equal(825, gainAndConvert.MaximumEnergyShield);
                var more = Control(new Dictionary<string, int>
                {
                    ["maximum_life_%_to_add_as_maximum_energy_shield"] = 15,
                    ["maximum_life_%_to_convert_to_maximum_energy_shield_per_1%_chaos_resistance"] = 2,
                    ["energy_shield_+%_final_for_minion"] = 100,
                }, new() { [Program.Stats.Key("active_skill_minion_life_+%_final")!.Value] = 100,
                    [activeESFinal] = 100 });
                Assert.Equal(6600, more.MaximumEnergyShield);
                var zeroLifeMore = Control(new Dictionary<string, int>(), new()
                {
                    [Program.Stats.Key("active_skill_minion_life_+%_final")!.Value] = -100,
                });
                Assert.Equal(0, zeroLifeMore.LifeMore);
                Assert.Equal(1u, zeroLifeMore.MaximumLife);
                var flat = Control(new Dictionary<string, int> { ["maximum_energy_shield_+%"] = 100 },
                    [], new() { ["base_maximum_energy_shield"] = 100 });
                Assert.Equal(200, flat.MaximumEnergyShield);
                control.Gear = new Dictionary<string, int> { ["base_maximum_life"] = int.MaxValue };
                var wideFlatLife = Control(new Dictionary<string, int>(), []);
                Assert.Equal((double)baseLife + int.MaxValue, wideFlatLife.RawBaseLife);
                Assert.Equal((uint)int.MaxValue, wideFlatLife.MaximumLife);
                Assert.True(double.IsFinite(wideFlatLife.MaximumEnergyShield));
                control.Gear = new Dictionary<string, int> { ["base_maximum_life"] = 1000 - baseLife };
                var noShield = Control(new Dictionary<string, int>
                {
                    ["maximum_life_%_to_convert_to_maximum_energy_shield_per_1%_chaos_resistance"] = 2,
                    ["minions_have_no_armour_or_energy_shield"] = 1,
                }, []);
                Assert.Equal(0, noShield.MaximumEnergyShield);
                var negativeChaos = Control(new Dictionary<string, int>
                {
                    ["maximum_life_%_to_convert_to_maximum_energy_shield_per_1%_chaos_resistance"] = 2,
                    ["base_chaos_damage_resistance_%"] = -100,
                }, []);
                Assert.True(negativeChaos.ChaosResistance < 0);
                Assert.Equal(0, negativeChaos.ConversionPercent);
                Assert.Equal(1500u, negativeChaos.MaximumLife);
                var cappedChaos = Control(new Dictionary<string, int>
                {
                    ["maximum_life_%_to_convert_to_maximum_energy_shield_per_1%_chaos_resistance"] = 2,
                    ["base_chaos_damage_resistance_%"] = int.MaxValue,
                    ["base_maximum_chaos_damage_resistance_%"] = int.MaxValue,
                }, []);
                Assert.Equal(90, cappedChaos.ChaosResistance);
                Assert.True(cappedChaos.Fatal);
                Assert.Equal(0u, cappedChaos.MaximumLife);
            }
            finally
            {
                Game.AreaUpdateContext.Value = previousFleshContext;
                if (flesh is not null)
                {
                    flesh.Closed = true;
                    PlayerInventories.Forget(fleshCharacter);
                    Game.InactiveItems.TryRemove(fleshCharacter, out _);
                }
            }

            // Revision 5 must replace the old seeded Fleshcrafter row but leave an administrator's later edit alone.
            var catalogPath = Path.Combine(Path.GetTempPath(), $"localexile-catalog-{Guid.NewGuid():N}.db");
            try
            {
                var store = new GameServer.Storage.GameDataStore(catalogPath);
                static string UniqueKey(UniqueCatalog.Definition entry) => $"{entry.Name}|{entry.BaseTypeId}";
                var oldSeed = UniqueCatalog.Seed.Select(entry => entry.Name == "Fleshcrafter"
                    ? entry with { Mods = entry.Mods.Where(id => id != "MinionLifeConvertedToEnergyShieldUnique__1").ToArray() }
                    : entry).ToArray();
                GameDataCatalog.RefreshSeed(store, "unique_item", oldSeed, UniqueKey, "4");
                GameDataCatalog.Seed(store, "unique_item", oldSeed, UniqueKey);
                GameDataCatalog.Reload(store);
                var correctedFlesh = GameDataCatalog.Uniques.Single(entry => entry.Name == "Fleshcrafter");
                Assert.Contains("MinionLifeConvertedToEnergyShieldUnique__1", correctedFlesh.Mods);

                string fleshKey = UniqueKey(correctedFlesh);
                var customFlesh = correctedFlesh with { ItemLevel = 84 };
                store.PutCatalogRow("unique_item", fleshKey, JsonSerializer.Serialize(customFlesh));
                GameDataCatalog.Reload(store);
                Assert.Equal(84, GameDataCatalog.Uniques.Single(entry => UniqueKey(entry) == fleshKey).ItemLevel);
            }
            finally
            {
                GameDataCatalog.ResetToSeeds();
            }

            var criticalMods = MonsterModSet.None with
            {
                Effects = new(new Dictionary<string, int>
                {
                    ["critical_strike_chance_+%"] = 100,
                    ["spell_critical_strike_chance_+%"] = 100,
                }),
            };
            var criticalAttack = actualCriticalAbility with { Crit = 500, Attack = actualCriticalAbility.Attack with { Spell = false } };
            var criticalSpell = actualCriticalAbility with { Crit = 500, Attack = actualCriticalAbility.Attack with { Spell = true } };
            Assert.Equal(1_000, Game.GolemCriticalChance(criticalAttack, criticalMods));
            Assert.Equal(1_500, Game.GolemCriticalChance(criticalSpell, criticalMods));
            Assert.Equal(10_000, Game.GolemCriticalChance(criticalAttack, MonsterModSet.None with
            {
                Effects = new(new Dictionary<string, int> { ["critical_strike_chance_+%"] = int.MaxValue }),
            }));
            Assert.False(Game.GolemCriticalStrike(criticalAttack, MonsterModSet.None with
            {
                Effects = new(new Dictionary<string, int> { ["never_crit"] = 1, ["always_crit"] = 1 }),
            }, 0));
            Assert.True(Game.GolemCriticalStrike(criticalAttack, MonsterModSet.None with
            {
                Effects = new(new Dictionary<string, int> { ["always_crit"] = 1 }),
            }, 9_999));

            inventory.Remove(PlayerInventories.Helmet, inventory.Items(PlayerInventories.Helmet).Single().Id);
            conn.PlayerStats.Remove(maximumPowerCharges);
            Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot());
            conn.Pools = Game.PoolsFor(conn, conn.Pools.Level);

            uint worn = Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).Single().Id;
            int experience = Program.Items.Gem(Program.Items.ByHash(gem.TypeHash)!)!.ExperienceFor(20);
            Game.ItemsOf(conn).Change(PlayerInventories.Weapon1, worn, item => item with
            { Sockets = [new(SocketColour.White, gem with { GemExperience = experience })] });
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 9);
            Assert.Equal(70, conn.Golems.All.Values.Single().Monster.Level);
            Assert.Equal(29, conn.Golems.OwnerStats[increased]);
            var auraFighter = conn.Golems.All.Values.Single();
            var auraAt = auraFighter.Monster.Walk.Position(0);
            var allyHaste = new AuraCatalog.Aura(1, Program.Skills.EffectRow("Haste"), "Haste", "Haste", 1, 152, 0, 5000);
            var ally = new InstancePlayer(0x2000abcd, 0, new("Ally", 0, auraAt.X, auraAt.Y, 100, 100), auraAt.X, auraAt.Y, 100)
                { Auras = [new(allyHaste, 1000, 100)] };
            conn.Instance.PublishPlayer(ally);
            await Game.SendOwnAuraChanges("test", wire.Stream, wire.Ciphers, conn);
            Assert.Single(conn.ReceivedAuras);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 9.6);
            Assert.Equal(20, auraFighter.Auras["base_movement_velocity_+%"]);
            Assert.Equal(30, auraFighter.Auras["attack_speed_+%"]);
            conn.Instance.RemovePlayer(ally.Id);
            await Game.SendOwnAuraChanges("test", wire.Stream, wire.Ciphers, conn);
            Assert.Empty(conn.ReceivedAuras);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 10.2);
            Assert.Empty(auraFighter.Auras); // Leaving the aura's instance retires all its cached minion bonuses.
            var living = conn.Golems.All.Values.Single();
            var position = living.Monster.Walk.Position(0);
            var target = new SpawnedMonster("golem target", new(position.X + 2, position.Y, 0), 100, 8, 1000000);
            conn.Monsters[0x08000001] = target;
            var schedule = new AreaClockSchedule();
            var updates = new PlayerUpdatesState(lifetime, schedule);
            var previousAreaUpdateContext = global::Game.AreaUpdateContext.Value;
            global::Game.AreaUpdateContext.Value = updates;
            try
            {
                schedule.At = 10.3;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 10.3);
                schedule.At = 11.3;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 11.3);
            }
            finally
            {
                global::Game.AreaUpdateContext.Value = previousAreaUpdateContext;
            }
            Assert.True(target.Life < target.MaxLife);
            var smallHit = spell with { Damage = DamagePacket.Of(DamageType.Physical, 100, 100) };
            uint before = living.Monster.Life;
            Assert.True(Game.GolemTakeHitLocked(conn, living, 0x08000000, attacker, smallHit, 12));
            Assert.True(living.Monster.Life < before);

            // A death reported by another damage executor still schedules the same two-second return.
            attacker.BusyUntil = double.PositiveInfinity; attacker.ActionGeneration = 1;
            conn.Monsters[0x08000000] = attacker;
            conn.Golems.IncomingHits.Add(new(0x08000000, attacker, living, spell, 1));
            living.Monster.Dead = true; living.Monster.Life = 0;
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 12);
            Assert.Equal(14, living.ReviveAt);
            Assert.Equal(12, attacker.BusyUntil); Assert.Empty(conn.Golems.IncomingHits);

            Game.ItemsOf(conn).Remove(PlayerInventories.Weapon1, Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).Single().Id);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 13);
            Assert.Empty(conn.Golems.All); Assert.Empty(conn.Golems.Buffs);
            Assert.False(conn.Golems.OwnerStats.ContainsKey(increased));
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 15);
            Assert.Empty(conn.Golems.All);

            // Reservation minions use the same native abilities/contact pipeline, without acquiring golem limits,
            // owner buffs or the local golem auto-resummon rule. Every permanent gem must release its reservation.
            foreach (string effectId in new[] { "Haste", "PurityOfElements", "PurityOfFire", "PurityOfIce", "PurityOfLightning",
                "Clarity", "Vitality", "Precision", "Determination", "Grace", "Discipline", "Anger", "Hatred", "Wrath",
                "Zealotry", "Malevolence", "BloodAndSand", "FleshAndStone", "Pride", "ArcticArmour", "TempestShield",
                "HeraldOfAsh", "HeraldOfIce", "HeraldOfThunder", "HeraldOfPurity", "HeraldOfAgony",
                "SummonSkitterbots", "TemporalRift" })
            {
                var type = Program.Items.All.FirstOrDefault(t => Program.Items.Gem(t) is { IsSupport: false } data
                    && Program.Skills.EffectId(data.Variant(0).Effect) == effectId);
                Assert.True(type is not null, "Missing reservation gem: " + effectId);
                var reservationGem = ItemCatalog.Create(type!);
                var wornWeapon = weapon with { Sockets = [new(SocketColour.White, reservationGem)] };
                Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, wornWeapon);
                var active = Game.GemSkillEffects(conn).Single(g => Program.Skills.EffectId(g.Effect) == effectId);
                var reservation = Game.SupportedReservationCost(conn, active.Id);
                Assert.NotNull(reservation);
                if (StanceCatalog.IsSource(effectId))
                {
                    Assert.True(Game.TryBeginSkillCooldownUse(conn, active.Id, active.Effect,
                        Game.CooldownFor(conn, active.Effect, active.Level, active.Quality), instant: true,
                        () => { }, out var stanceUse));
                    Assert.True(await Game.CastGemSkill("test", wire.Stream, wire.Ciphers, conn, active.Id,
                        (0, 0), (0, 0), cooldownUse: stanceUse), effectId);
                }
                else
                    Assert.True(await Game.ToggleAura("test", wire.Stream, wire.Ciphers, conn, active.Id, active.Effect,
                        active.Name, reservation!.Value, active.Level, active.Quality), effectId);
                Assert.Single(conn.Auras); Assert.True(Game.ReservedMana(conn) > 0, effectId);
                var aura = conn.Auras[active.Id];
                if (effectId == "TempestShield")
                    Assert.Equal(Convert.FromHexString("000000020000000000000000"),
                        AuraCatalog.StartPackets(aura, 0x1000, null).Last()[^12..]);
                if (effectId == "TemporalRift")
                    Assert.Equal(Convert.FromHexString("000000020000002A00000064"),
                        AuraCatalog.StartPackets(aura, 0x1000, null).Last()[^12..]);
                if (effectId is "HeraldOfIce" or "HeraldOfThunder" or "TempestShield")
                    Assert.True(Game.ReservationHitPacket(conn, aura).MaxTotal > 0, effectId);
                if (effectId == "HeraldOfPurity")
                {
                    Game.ReservationKill(new(conn, target, "test", position));
                    await Game.ReservationSkillsTick("test", wire.Stream, wire.Ciphers, conn, conn.Clock.Elapsed.TotalSeconds);
                    Assert.Contains(conn.Reservations.Fighters.Values, f => f.Definition.Kind == "Purity");
                }
                if (effectId == "SummonSkitterbots")
                {
                    await Game.ReservationSkillsTick("test", wire.Stream, wire.Ciphers, conn, conn.Clock.Elapsed.TotalSeconds);
                    Assert.Equal(2, conn.Reservations.Fighters.Count);
                }
                await Game.DisableReservations("test", wire.Stream, wire.Ciphers, conn, effectId);
                Assert.Empty(conn.Auras); Assert.Equal(0, Game.ReservedMana(conn));
                Assert.Empty(conn.Reservations.Fighters);
                Game.ItemsOf(conn).Remove(PlayerInventories.Weapon1, Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).Single().Id);
            }

            // Independent integer-recovery controls use the existing host kernel directly. Ordinary recharge is one
            // third of maximum per second after its delay; fractional progress is retained across split steps.
            var ordinaryKernel = new EnergyShield();
            ordinaryKernel.SetMaximum(300, refill: true);
            Assert.Equal(0, ordinaryKernel.Damage(100, bypass: false, now: 1));
            Assert.Equal(200u, ordinaryKernel.Current);
            ordinaryKernel.Advance(2.9, 3.1, _ => 0, alive: true);
            Assert.Equal(210u, ordinaryKernel.Current);
            Assert.Equal(4 / 3d, EnergyShield.Delay(name => name == "energy_shield_delay_-%" ? 50 : 0), 12);
            Assert.Equal(1d, EnergyShield.Delay(name => name == "energy_shield_delay_-%" ? 100 : 0), 12);

            var boundaryKernel = new EnergyShield();
            boundaryKernel.SetMaximum(300, refill: true);
            boundaryKernel.Damage(100, bypass: false, now: 1);
            boundaryKernel.Advance(1, 3, _ => 0, alive: true);
            Assert.Equal(200u, boundaryKernel.Current);
            boundaryKernel.Advance(3, 3.1, _ => 0, alive: true);
            Assert.Equal(210u, boundaryKernel.Current);

            var splitKernel = new EnergyShield();
            splitKernel.SetMaximum(3, refill: true);
            splitKernel.Damage(2, bypass: false, now: 0);
            splitKernel.Advance(0, 2.5, _ => 0, alive: true);
            splitKernel.Advance(2.5, 3, _ => 0, alive: true);
            var combinedKernel = new EnergyShield();
            combinedKernel.SetMaximum(3, refill: true);
            combinedKernel.Damage(2, bypass: false, now: 0);
            combinedKernel.Advance(0, 3, _ => 0, alive: true);
            Assert.Equal(combinedKernel.Current, splitKernel.Current);
            Assert.Equal(2u, splitKernel.Current);

            var interruptedKernel = new EnergyShield();
            interruptedKernel.SetMaximum(300, refill: true);
            interruptedKernel.Damage(100, bypass: false, now: 1);
            interruptedKernel.Advance(1, 3.1, _ => 0, alive: true);
            Assert.Equal(210u, interruptedKernel.Current);
            interruptedKernel.Damage(10, bypass: false, now: 3.1);
            interruptedKernel.Advance(3.1, 5, _ => 0, alive: true);
            Assert.Equal(200u, interruptedKernel.Current);

            // A later ordinary hit owns a newer interruption than a DoT endpoint that expired before it.
            var monotonicInterrupt = new EnergyShield();
            monotonicInterrupt.SetMaximum(300, refill: true);
            monotonicInterrupt.Damage(100, bypass: false, now: 1.5);
            monotonicInterrupt.Interrupt(1.4);
            monotonicInterrupt.Interrupt(double.NaN);
            monotonicInterrupt.Interrupt(double.PositiveInfinity);
            monotonicInterrupt.Advance(1.5, 3.4, _ => 0, alive: true);
            Assert.Equal(200u, monotonicInterrupt.Current);
            monotonicInterrupt.Advance(3.4, 3.5, _ => 0, alive: true);
            Assert.Equal(200u, monotonicInterrupt.Current);
            monotonicInterrupt.Advance(3.5, 3.6, _ => 0, alive: true);
            Assert.Equal(210u, monotonicInterrupt.Current);

            var snapshotKernel = new EnergyShield();
            snapshotKernel.SetMaximum(300, refill: true);
            snapshotKernel.Damage(100, bypass: false, now: 100);
            snapshotKernel.Gain(.5);
            var kernelSnapshot = snapshotKernel.Capture(100.5);
            Assert.Equal(200u, kernelSnapshot.Current);
            Assert.Equal(.5, kernelSnapshot.Fraction, 12);
            Assert.Equal(.5, kernelSnapshot.InterruptedSecondsAgo!.Value, 12);

            var splitRestore = new EnergyShield();
            splitRestore.SetMaximum(300);
            splitRestore.Restore(kernelSnapshot, 500);
            splitRestore.Advance(500, 500, _ => 0, alive: true);
            Assert.Equal(200u, splitRestore.Current);
            splitRestore.Advance(500, 501.5, _ => 0, alive: true);
            Assert.Equal(200u, splitRestore.Current);
            Assert.Equal(.5, splitRestore.Capture(501.5).Fraction, 12);
            splitRestore.Advance(501.5, 501.515625, _ => 0, alive: true);
            var combinedRestore = new EnergyShield();
            combinedRestore.SetMaximum(300);
            combinedRestore.Restore(kernelSnapshot, 500);
            combinedRestore.Advance(500, 501.515625, _ => 0, alive: true);
            Assert.Equal(202u, splitRestore.Current);
            Assert.Equal(combinedRestore.Current, splitRestore.Current);
            var repeatedSnapshot = splitRestore.Capture(501.515625);
            var repeatedRestore = new EnergyShield();
            repeatedRestore.SetMaximum(300);
            repeatedRestore.Restore(repeatedSnapshot, 900);
            Assert.Equal(repeatedSnapshot.Current, repeatedRestore.Current);
            Assert.Equal(.0625, repeatedSnapshot.Fraction, 12);
            Assert.Equal(2.015625, repeatedSnapshot.InterruptedSecondsAgo!.Value, 12);

            var futureSkew = new EnergyShield();
            futureSkew.SetMaximum(300);
            futureSkew.Restore(new(100, .25, -1), 10);
            var futureSnapshot = futureSkew.Capture(10);
            Assert.Equal(.25, futureSnapshot.Fraction, 12);
            Assert.Equal(-1, futureSnapshot.InterruptedSecondsAgo);

            var invalidSnapshot = new EnergyShield();
            invalidSnapshot.SetMaximum(300);
            invalidSnapshot.Restore(new(100, double.NaN, double.NaN), 20);
            var invalidCapture = invalidSnapshot.Capture(20);
            Assert.Equal(0, invalidCapture.Fraction);
            Assert.Equal(0d, invalidCapture.InterruptedSecondsAgo!.Value);
            invalidSnapshot.Advance(20, 21.999, _ => 0, alive: true);
            Assert.Equal(100u, invalidSnapshot.Current);

            var fullSnapshot = new EnergyShield();
            fullSnapshot.SetMaximum(300);
            fullSnapshot.Restore(new(999, .75, null), 30);
            Assert.Equal(300u, fullSnapshot.Current);
            Assert.Equal(0, fullSnapshot.Capture(30).Fraction);
            var zeroCapSnapshot = new EnergyShield();
            zeroCapSnapshot.SetMaximum(0);
            zeroCapSnapshot.Restore(new(100, .5, 0), 40);
            Assert.Equal(0u, zeroCapSnapshot.Current);
            Assert.Equal(2, EnergyShield.Delay(_ => 0));

            var fractionalIgnite = new MonsterIgnites();
            Assert.True(fractionalIgnite.Add(0, .1, .5));
            Assert.Equal(0, fractionalIgnite.Advance(.5, 1, 0, out double? exactIgniteEnd));
            Assert.Equal(.5, exactIgniteEnd);

            var fractionalPoison = new MonsterPoisons();
            Assert.True(fractionalPoison.Add(0, .1, .5));
            Assert.Equal(0, fractionalPoison.Advance(.5, 1, out double? exactPoisonEnd));
            Assert.Equal(.5, exactPoisonEnd);

            var immuneIgnite = new MonsterIgnites();
            Assert.True(immuneIgnite.Add(0, 100, 2));
            Assert.Equal(0, immuneIgnite.Advance(1, 0, 0, out double? immuneEnd));
            Assert.Null(immuneEnd);
            var immunePoison = new MonsterPoisons();
            Assert.True(immunePoison.Add(0, 100, 2));
            Assert.Equal(0, immunePoison.Advance(1, 0, out double? immunePoisonEnd));
            Assert.Null(immunePoisonEnd);

            var straddlingIgnite = new MonsterIgnites();
            Assert.True(straddlingIgnite.Add(0, 2, .5));
            Assert.Equal(1, straddlingIgnite.Advance(1, 1, 0, out double? expiryEnd));
            Assert.Equal(.5, expiryEnd);

            var splitIgnite = new MonsterIgnites();
            Assert.True(splitIgnite.Add(0, 1.2, 1));
            Assert.Equal(0, splitIgnite.Advance(.4, 1));
            Assert.Equal(1, splitIgnite.Advance(1, 1));
            var combinedIgnite = new MonsterIgnites();
            Assert.True(combinedIgnite.Add(0, 1.2, 1));
            Assert.Equal(1, combinedIgnite.Advance(1, 1));

            const string resourceCharacter = "IntegerResourceAuthority";
            var resourceSchedule = new AreaClockSchedule();
            var resourceUpdates = new PlayerUpdatesState(lifetime, resourceSchedule);
            var previousResourceContext = global::Game.AreaUpdateContext.Value;
            global::Game.AreaUpdateContext.Value = resourceUpdates;
            ClientState? resource = null;
            var extraResourceClients = new List<ClientState>();
            try
            {
                resourceSchedule.At = 20;
                resource = new ClientState(new(1, "integer-resource-authority", 1000, 1000, 100, 100, null, []), 6, 1)
                {
                    Character = resourceCharacter, Instance = new(), PlayerLife = 10000, PlayerMana = 10000,
                    PlayerSent = true, Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = resourceUpdates,
                    Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    LeagueModule = conn.LeagueModule,
                };
                resource.Golems.Restored = true;
                foreach (string stat in new[] { "virtual_strength_for_requirements",
                             "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                    resource.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
                resource.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 1;
                resource.PlayerStats[Program.Stats.Key("minion_life_regeneration_rate_per_minute_%")!.Value] = -60;
                resource.PlayerStats[Program.Stats.Key("minion_additional_spell_block_%")!.Value] = 0;
                resource.PlayerStats[Program.Stats.Key("minion_block_%")!.Value] = 0;

                var resourceInventory = Game.ItemsOf(resource);
                var resourceGem = ItemCatalog.Create(
                    Program.Items.Find("Metadata/Items/Gems/SkillGemSummonFlameGolem")!);
                var treeProvider = Program.Items.Mods.ById(
                    "WeaponTreeMinionLifeAsExtraEnergyShieldReducedElementalResistance1")!;
                var treeRoll = treeProvider.Roll(ModSection.Explicit, new Random(504));
                Assert.Equal(15, treeRoll.Values[0]);
                var resourceWeapon = ItemCatalog.Create(
                    Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with
                {
                    Sockets = [new(SocketColour.White, resourceGem)],
                    Mods = [.. ItemCatalog.Create(Program.Items.Find(
                        "Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!).Mods, treeRoll],
                };
                var weaponSlot = resourceInventory.Add(PlayerInventories.Weapon1, 0, 0, resourceWeapon);

                var resourceArmour = new UniqueCatalog(Program.Items).Create("Fleshcrafter", new Random(504))!;
                bool IsRow(ItemMod mod, string id) => Program.Items.Mods.ByHash(mod.Hash)?.Id == id;
                const string conversionRow = "MinionLifeConvertedToEnergyShieldUnique__1";
                const string noBypassRow = "MinionChaosDamageDoesNotBypassESUnique__1";
                const string delayRow = "MinionEnergyShieldRechargeDelayUnique__1";
                var conversion = resourceArmour.Mods.Single(mod => IsRow(mod, conversionRow));
                var noBypass = resourceArmour.Mods.Single(mod => IsRow(mod, noBypassRow));
                var delay = resourceArmour.Mods.Single(mod => IsRow(mod, delayRow));
                Assert.Equal(2, conversion.Values[0]);
                Assert.Equal(1, noBypass.Values[0]);
                Assert.InRange(delay.Values[0], 50, 100);
                var originalArmourMods = resourceArmour.Mods.ToArray();
                var wornArmour = resourceArmour with
                {
                    Mods = originalArmourMods.Where(mod => !IsRow(mod, noBypassRow)).ToArray(),
                };
                var armourSlot = resourceInventory.Add(PlayerInventories.BodyArmour, 0, 0, wornArmour);

                void RecalculateResource()
                {
                    Game.EquipmentStatSources(resource, resourceInventory.EquipmentSnapshot());
                    resource.Pools = Game.PoolsFor(resource, resource.Pools.Level);
                    resource.PlayerLife = resource.Pools.Life;
                    resource.PlayerMana = resource.Pools.Mana;
                }
                void SetDelay(int value)
                {
                    resourceInventory.Change(PlayerInventories.BodyArmour, armourSlot.Id, item => item with
                    {
                        Mods = item.Mods.Select(mod => mod.Hash == delay.Hash
                            ? mod with { Values = [value] } : mod).ToArray(),
                    });
                    RecalculateResource();
                }
                RecalculateResource();
                var resourceSource = Assert.Single(Game.GemSkillEffects(resource),
                    grant => Game.SkillsFor(resource).EffectId(grant.Effect) == "SummonFlameGolem");
                var resourceDefinition = Game.GolemData.Find("SummonFlameGolem")!;
                uint ExpectedMaximum(GolemState.Golem golem)
                {
                    var pools = Game.GolemPools.Calculate(Game.Monsters, golem.Monster.Variety!, golem.Monster.Level,
                        golem, golem.Player, golem.Monster.Mods.Effects);
                    return Game.NativeEnergyShield(pools.MaximumEnergyShield);
                }
                void AssertIntegerPool(GolemState.Golem golem)
                {
                    Assert.InRange(golem.MaximumShield, 0u, (uint)int.MaxValue);
                    Assert.InRange(golem.Shield, 0u, golem.MaximumShield);
                    Assert.Equal(ExpectedMaximum(golem), golem.MaximumShield);
                }
                void AssertConservation(GolemState.Golem golem, uint lifeBefore, uint shieldBefore,
                    uint lifeAfter, uint shieldAfter, int mitigated)
                {
                    long absorbed = (long)shieldBefore - shieldAfter;
                    long lifeLost = (long)lifeBefore - lifeAfter;
                    Assert.Equal((long)mitigated, absorbed + lifeLost);
                }
                async Task<List<byte[]>> CaptureResource(Func<Task> send)
                {
                    var packets = new List<byte[]>();
                    var previous = Program.Outgoing;
                    Program.Outgoing = (seen, bytes) =>
                    {
                        if (ReferenceEquals(seen, wire.Ciphers)) lock (packets) packets.Add(bytes.ToArray());
                        previous?.Invoke(seen, bytes);
                    };
                    try { await send(); }
                    finally { Program.Outgoing = previous; }
                    return packets;
                }
                ClientState NewResourceClient(string name, double at)
                {
                    resourceSchedule.At = at;
                    var client = new ClientState(new(1, name, 1000, 1000, 100, 100, null, []), 8, 1)
                    {
                        Character = resourceCharacter, Instance = new(), PlayerLife = 10000, PlayerMana = 10000,
                        PlayerSent = true, Experience = CharacterStats.ExperienceFor(100),
                        PlayerUpdatesState = resourceUpdates,
                        Pools = new(resource.Pools.Level, 10000, 10000, 2, 0,
                            Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    };
                    foreach (var stat in resource.PlayerStats) client.PlayerStats[stat.Key] = stat.Value;
                    Game.EquipmentStatSources(client, resourceInventory.EquipmentSnapshot());
                    client.Pools = Game.PoolsFor(client, client.Pools.Level);
                    client.PlayerLife = client.Pools.Life; client.PlayerMana = client.Pools.Mana;
                    extraResourceClients.Add(client);
                    return client;
                }
                void AssertCreateResources(IReadOnlyList<byte[]> packets, uint id, uint currentLife,
                    uint currentShield, uint maximumLife, uint maximumShield)
                {
                    var create = Assert.Single(packets, bytes => bytes.Length >= 18
                        && BinaryPrimitives.ReadUInt16BigEndian(bytes) == InstanceEntities.CreateObjectId
                        && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2)) == id);
                    int blobLength = BinaryPrimitives.ReadUInt16BigEndian(create.AsSpan(16));
                    var blob = create.AsSpan(18, blobLength).ToArray();
                    int cursor = 20;
                    uint Compact()
                    {
                        byte first = blob[cursor++];
                        int following = first < 0x80 ? 0 : first < 0xc0 ? 1 : first < 0xe0 ? 2 : first < 0xf0 ? 3 : 4;
                        uint value = following == 4 ? 0u : (uint)(first & (0x7f >> following));
                        for (int i = 0; i < following; i++) value = (value << 8) | blob[cursor++];
                        return value;
                    }
                    var stats = new Dictionary<uint, uint>();
                    for (uint count = Compact(), i = 0; i < count; i++) stats[Compact()] = Compact();
                    Assert.Equal(maximumLife, stats[StatKeys.MaximumLife]);
                    Assert.Equal(maximumShield, stats[maximumEnergyShield]);
                    byte[] Pool(uint current)
                    {
                        var pool = new byte[18];
                        BinaryPrimitives.WriteUInt32LittleEndian(pool, current);
                        return pool;
                    }
                    Assert.True(blob.AsSpan().IndexOf([.. Pool(currentLife), .. Pool(0), .. Pool(currentShield)]) >= 0);
                }

                // Labelled neutral diagnostics: moving preserves the absolute instance and fractional integration state.
                // The remaining second is integral, while expiry discards the last unbanked fraction.
                var neutralIgnite = new MonsterIgnites();
                var movedIgnite = new MonsterIgnites();
                Assert.True(neutralIgnite.Add(10, .75, 3));
                Assert.Equal(0, neutralIgnite.Advance(10.5, 1));
                neutralIgnite.MoveTo(movedIgnite);
                Assert.True(neutralIgnite.Idle);
                Assert.False(movedIgnite.Idle);
                Assert.Equal(13, movedIgnite.Until);
                Assert.Equal(1, movedIgnite.Advance(11.5, 1, 0, out double? movedIgniteActivity));
                Assert.Equal(11.5, movedIgniteActivity);
                Assert.False(movedIgnite.Idle);
                Assert.Equal(1, movedIgnite.Advance(13, 1, 0, out movedIgniteActivity));
                Assert.Equal(13, movedIgniteActivity);
                Assert.True(movedIgnite.Idle);
                Assert.Equal(0, neutralIgnite.Advance(13, 1, 0, out double? sourceIgniteActivity));
                Assert.Null(sourceIgniteActivity);
                var untouchedIgnite = new MonsterIgnites();
                Assert.True(untouchedIgnite.Add(10, .75, 3));
                Assert.Equal(2, untouchedIgnite.Advance(13, 1));
                Assert.True(untouchedIgnite.Idle);

                var neutralPoison = new MonsterPoisons();
                var movedPoison = new MonsterPoisons();
                Assert.True(neutralPoison.Add(10, .75, 3));
                Assert.Equal(0, neutralPoison.Advance(10.5, 1));
                neutralPoison.MoveTo(movedPoison);
                Assert.True(neutralPoison.Idle);
                Assert.Equal(13, movedPoison.Until);
                Assert.Equal(1, movedPoison.Advance(11.5, 1, out double? movedPoisonActivity));
                Assert.Equal(11.5, movedPoisonActivity);
                Assert.False(movedPoison.Idle);
                Assert.Equal(1, movedPoison.Advance(13, 1, out movedPoisonActivity));
                Assert.Equal(13, movedPoisonActivity);
                Assert.True(movedPoison.Idle);
                Assert.Equal(0, neutralPoison.Advance(13, 1, out double? sourcePoisonActivity));
                Assert.Null(sourcePoisonActivity);
                var untouchedPoison = new MonsterPoisons();
                Assert.True(untouchedPoison.Add(10, .75, 3));
                Assert.Equal(2, untouchedPoison.Advance(13, 1));
                Assert.True(untouchedPoison.Idle);

                var neutralElemental = new MonsterElementalAilments();
                Assert.NotNull(neutralElemental.WithoutDamage(MonsterElementalAilments.Kind.Chill, 3, 10,
                    _ => 0, _ => 0, area: 0));
                Assert.NotNull(neutralElemental.Freeze(2, 10, _ => 0, _ => 0));
                Assert.NotNull(neutralElemental.WithoutDamage(MonsterElementalAilments.Kind.Shock, 4, 10,
                    _ => 0, _ => 0, area: 0));
                Assert.Equal(3, neutralElemental.Present(10).Count);
                var movedElemental = new MonsterElementalAilments();
                neutralElemental.MoveTo(movedElemental);
                Assert.True(neutralElemental.Idle);
                Assert.Equal(3, movedElemental.Present(10).Count);
                Assert.Equal(0, movedElemental.ActionFactor(10));
                Assert.Equal(2, movedElemental.Remaining(MonsterElementalAilments.Kind.Freeze, 10));

                resourceSchedule.At = 20.25;
                var freshResourcePackets = await CaptureResource(() => Game.SummonGolem("test", wire.Stream,
                    wire.Ciphers, resource, resourceSource.Id, resourceDefinition, (100, 100)));
                Assert.NotEmpty(freshResourcePackets);
                var freshResource = Assert.Single(resource.Golems.All).Value;
                Assert.Equal(freshResource.Monster.MaxLife, freshResource.Monster.Life);
                Assert.Equal(freshResource.MaximumShield, freshResource.Shield);
                Assert.True(freshResource.MaximumShield > 0);
                AssertCreateResources(freshResourcePackets, freshResource.Id, freshResource.Monster.Life,
                    freshResource.Shield, freshResource.Monster.MaxLife, freshResource.MaximumShield);

                // Labelled model diagnostic: unconditional hit ignore is separate from critical ignore. It zeroes
                // elemental resistance (including negative resistance) from any hit, removes penetration, and the
                // real suppressor wins. Physical, chaos and OverTime retain their ordinary resistance.
                static int ModelDamage(DamageType type, DamageModel.ResistanceValue target,
                    HitResistances.Mods mods, bool overTime = false)
                {
                    var strike = new HitResistances.Strike(Critical: false, Attack: true, OverTime: overTime);
                    var mitigated = DamageModel.Mitigate(DamagePacket.Of(type, 400, 400),
                        damage => HitResistances.Effective(damage, target, strike, mods));
                    return (int)Math.Round(mitigated[type].Min);
                }
                var unconditional = new HitResistances.Mods(0, 0, 0, 0, UnconditionalIgnoreElemental: true);
                var suppressed = new HitResistances.Mods(0, 0, 0, 1, true);
                Assert.Equal(340, ModelDamage(DamageType.Fire, new(25, 75, 10), default));
                Assert.Equal(400, ModelDamage(DamageType.Fire, new(25, 75, 10), unconditional));
                Assert.Equal(440, ModelDamage(DamageType.Fire, new(0, 75, 10), default));
                Assert.Equal(400, ModelDamage(DamageType.Fire, new(0, 75, 10), unconditional));
                Assert.Equal(540, ModelDamage(DamageType.Fire, new(-25, 75, 10), default));
                Assert.Equal(400, ModelDamage(DamageType.Fire, new(-25, 75, 10), unconditional));
                Assert.Equal(500, ModelDamage(DamageType.Fire, new(-25, 75, 10), suppressed));
                Assert.Equal(300, ModelDamage(DamageType.Physical, new(25), unconditional));
                Assert.Equal(300, ModelDamage(DamageType.Chaos, new(25), unconditional));
                Assert.Equal(300, ModelDamage(DamageType.Fire, new(25), unconditional, overTime: true));

                // Actual catalogue Fleshcrafter + tree Energy Shield + real summoned Flame Golem. The fixed current
                // ability is a labelled diagnostic after the real source/ability refresh. Target resistance is supplied
                // through the target's ordinary mod effects; no artificial ignore field or target mutation is used.
                var preservedArmourItem = resourceInventory.Items(PlayerInventories.BodyArmour).Single().Item;
                var preservedWeaponItem = resourceInventory.Items(PlayerInventories.Weapon1).Single().Item;
                var preservedPlayerStats = new Dictionary<uint, int>(resource.PlayerStats);
                var preservedIncomingRandom = resource.IncomingHitRandom;
                var preservedAreaTime = resourceSchedule.At;
                var preservedGolemWalk = freshResource.Monster.Walk;
                var preservedGolemTarget = freshResource.Target;
                var preservedAggro = freshResource.Monster.Aggro;
                var preservedLastTarget = freshResource.Monster.LastTarget;
                var preservedLife = freshResource.Monster.Life;
                var preservedMaximumLife = freshResource.Monster.MaxLife;
                var preservedShield = freshResource.ShieldPool.Capture(preservedAreaTime);
                var preservedMaximumShield = freshResource.MaximumShield;
                var preservedRegeneration = freshResource.Regeneration;
                var preservedLastStep = freshResource.LastStep;
                var preservedBusyUntil = freshResource.Monster.BusyUntil;
                var preservedFlinchUntil = freshResource.Monster.FlinchUntil;
                var preservedStunImmuneUntil = freshResource.Monster.StunImmuneUntil;
                var preservedActionGeneration = freshResource.Monster.ActionGeneration;
                var preservedSwingGeneration = freshResource.Monster.SwingGeneration;
                var preservedSwingOrderedAt = freshResource.Monster.SwingOrderedAt;
                var preservedReceivedActionFactor = freshResource.Monster.ReceivedActionFactor;
                var preservedReceivedActionUntil = freshResource.Monster.ReceivedActionUntil;
                var preservedReceivedActionFloor = freshResource.Monster.ReceivedActionFloor;
                var preservedRecipientDebuffRate = freshResource.Monster.RecipientDebuffRate;
                var preservedPending = freshResource.Pending;
                var preservedAbilities = freshResource.Abilities;
                var preservedMods = freshResource.Monster.Mods;
                var preservedGolemPlayer = new Dictionary<uint, int>(freshResource.Player);
                var preservedNextMove = freshResource.NextMove;
                var preservedNextOrb = freshResource.NextOrb;
                var preservedCombo = freshResource.Combo;
                var preservedInputHash = freshResource.InputHash;
                var preservedProjectileRuntime = resource.ProjectileRuntime;
                var preservedCharges = freshResource.Charges.ToDictionary(pair => pair.Key,
                    pair => new Queue<double>(pair.Value));
                // Labelled diagnostic: force the existing minion critical stat through the public raw player-stat path.
                // The real source refresh must preserve the zero chance.
                var minionCriticalChance = Program.Stats.Key("minion_critical_strike_chance_+%")!.Value;
                resource.PlayerStats[minionCriticalChance] = -100;
                RecalculateResource();
                resourceSchedule.At = 20.26;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                var previousOutgoingAbilities = freshResource.Abilities;
                var previousOutgoingMods = freshResource.Monster.Mods;
                var projectileAbility = Assert.Single(previousOutgoingAbilities,
                    ability => ability.Projectile is { Lobbed: false } && ability.Attack.Damage.MaxTotal > 0);
                Assert.Equal(0, Game.GolemCriticalChance(projectileAbility, previousOutgoingMods));
                var outgoingTargetId = 0x0A22D3D0u;
                var outgoingAt = freshResource.Monster.Walk.Position(20.26);
                var outgoingTarget = new SpawnedMonster("Fleshcrafter impact target",
                    new(outgoingAt.X + 15, outgoingAt.Y, 0), 100, 8, 1_000_000)
                {
                    Hostile = true,
                    Mods = MonsterModSet.None with
                    {
                        Effects = new(new Dictionary<string, int>
                        {
                            ["base_fire_damage_resistance_%"] = 25,
                            ["base_chaos_damage_resistance_%"] = 25,
                            ["base_additional_physical_damage_reduction_%"] = 25,
                        }),
                    },
                };
                Assert.DoesNotContain(outgoingTargetId, resource.Monsters.Keys);
                resource.Monsters[outgoingTargetId] = outgoingTarget;
                var farTargetId = 0x0A22D3D2u;
                var farTarget = new SpawnedMonster("Fleshcrafter far impact target",
                    new(outgoingAt.X + 140, outgoingAt.Y, 0), 100, 8, 1_000_000)
                {
                    Hostile = true,
                    Mods = outgoingTarget.Mods,
                };
                Assert.DoesNotContain(farTargetId, resource.Monsters.Keys);
                resource.Monsters[farTargetId] = farTarget;
                Game.ProjectileSession? impactSession = null;
                try
                {
                    GolemState.Ability GolemAbility(DamageType type) => projectileAbility with
                    {
                        Attack = projectileAbility.Attack with
                        {
                            Spell = true,
                            Damage = DamagePacket.Of(type, 400, 400),
                        },
                    };
                    GolemState.Ability fixedAbility = GolemAbility(DamageType.Fire);
                    freshResource.Abilities = [fixedAbility];
                    uint LifeLoss(GolemState.Ability ability)
                    {
                        freshResource.Abilities = [ability];
                        outgoingTarget.Life = outgoingTarget.MaxLife;
                        uint before = outgoingTarget.Life;
                        Assert.True(Game.GolemHitLocked("test", wire.Stream, wire.Ciphers, resource,
                            freshResource, ability, outgoingTargetId, outgoingTarget, 20.27));
                        return before - outgoingTarget.Life;
                    }

                    Assert.True(freshResource.MaximumShield > 1);
                    freshResource.ShieldPool.SetMaximum(freshResource.MaximumShield, refill: true);
                    freshResource.ShieldPool.Damage((int)freshResource.MaximumShield - 1, bypass: false, now: 20.26);
                    Assert.Equal(1u, freshResource.Shield);
                    Assert.Equal(400u, LifeLoss(fixedAbility));
                    freshResource.ShieldPool.Damage(1, bypass: false, now: 20.27);
                    Assert.Equal(0u, freshResource.Shield);
                    Assert.Equal(300u, LifeLoss(fixedAbility));
                    freshResource.ShieldPool.SetMaximum(freshResource.MaximumShield, refill: true);
                    freshResource.ShieldPool.Damage((int)freshResource.MaximumShield - 1, bypass: false, now: 20.28);
                    Assert.Equal(300u, LifeLoss(GolemAbility(DamageType.Physical)));
                    Assert.Equal(1u, freshResource.Shield);
                    Assert.Equal(300u, LifeLoss(GolemAbility(DamageType.Chaos)));

                    // The equipment source changes without waiting for the .5 s roster refresh. GolemHitLocked must
                    // sample a fresh owner and the actual current source at contact, not the cached Fleshcrafter effect.
                    var ignoreKey = Program.Stats.Key(
                        "minion_hits_ignore_enemy_elemental_resistances_while_has_energy_shield")!.Value;
                    Assert.NotEqual(0, freshResource.Player.GetValueOrDefault(ignoreKey));
                    freshResource.Abilities = [projectileAbility];
                    freshResource.Monster.Mods = previousOutgoingMods;
                    freshResource.ShieldPool.SetMaximum(freshResource.MaximumShield, refill: true);
                    Assert.NotEqual(0u, freshResource.Shield);
                    resourceInventory.Remove(PlayerInventories.BodyArmour, armourSlot.Id);
                    RecalculateResource();
                    Assert.NotEqual(0, freshResource.Player.GetValueOrDefault(ignoreKey));
                    outgoingTarget.Life = outgoingTarget.MaxLife;
                    uint normalBefore = outgoingTarget.Life;
                    Assert.True(Game.GolemHitLocked("test", wire.Stream, wire.Ciphers, resource, freshResource,
                        projectileAbility, outgoingTargetId, outgoingTarget, 20.29));
                    int normalLost = checked((int)(normalBefore - outgoingTarget.Life));
                    Assert.Equal(0, freshResource.Player.GetValueOrDefault(ignoreKey));
                    var normalAbility = Assert.Single(freshResource.Abilities,
                        ability => ability.Effect == projectileAbility.Effect);
                    Assert.Equal(0, Game.GolemCriticalChance(normalAbility, freshResource.Monster.Mods));
                    int NormalComponent(DamageType type, double value) => Game.MonsterTakesMitigated(outgoingTarget,
                        DamageModel.Mitigate(DamagePacket.Of(type, value, value),
                            damage => Game.MonsterHitResistance(outgoingTarget, damage, default)), spell: true);
                    Assert.InRange(normalLost,
                        NormalComponent(DamageType.Fire, normalAbility.Attack.Damage[DamageType.Fire].Min),
                        NormalComponent(DamageType.Fire, normalAbility.Attack.Damage[DamageType.Fire].Max));

                    resourceInventory.Add(PlayerInventories.BodyArmour, 0, 0, preservedArmourItem);
                    armourSlot = resourceInventory.Items(PlayerInventories.BodyArmour).Single();
                    RecalculateResource();
                    Assert.Equal(0, freshResource.Player.GetValueOrDefault(ignoreKey));
                    uint shieldBeforeSourceOnHit = freshResource.Shield;
                    outgoingTarget.Life = outgoingTarget.MaxLife;
                    uint ignoredBefore = outgoingTarget.Life;
                    Assert.True(Game.GolemHitLocked("test", wire.Stream, wire.Ciphers, resource, freshResource,
                        projectileAbility, outgoingTargetId, outgoingTarget, 20.3));
                    Assert.NotEqual(0, freshResource.Player.GetValueOrDefault(ignoreKey));
                    Assert.True(freshResource.MaximumShield > 0);
                    Assert.Equal(shieldBeforeSourceOnHit, freshResource.Shield);
                    int ignoredLost = checked((int)(ignoredBefore - outgoingTarget.Life));
                    var ignoredAbility = Assert.Single(freshResource.Abilities,
                        ability => ability.Effect == projectileAbility.Effect);
                    Assert.Equal(0, Game.GolemCriticalChance(ignoredAbility, freshResource.Monster.Mods));
                    Assert.InRange(ignoredLost,
                        (int)Math.Round(ignoredAbility.Attack.Damage[DamageType.Fire].Min),
                        (int)Math.Round(ignoredAbility.Attack.Damage[DamageType.Fire].Max));

                    // A vanished gem source is not kept alive by the roster. Dead and replaced actors likewise cannot
                    // deliver a new hit.
                    resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id, item => item with { Sockets = [] });
                    RecalculateResource();
                    uint missingSourceLife = outgoingTarget.Life;
                    Assert.False(Game.GolemHitLocked("test", wire.Stream, wire.Ciphers, resource, freshResource,
                        projectileAbility, outgoingTargetId, outgoingTarget, 20.31));
                    Assert.Equal(missingSourceLife, outgoingTarget.Life);
                    resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id,
                        item => item with { Sockets = preservedWeaponItem.Sockets });
                    RecalculateResource();
                    resourceSource = Assert.Single(Game.GemSkillEffects(resource),
                        grant => Game.SkillsFor(resource).EffectId(grant.Effect) == "SummonFlameGolem");
                    freshResource.Source = resourceSource.Id;

                    freshResource.Monster.Dead = true;
                    Assert.False(Game.GolemHitLocked("test", wire.Stream, wire.Ciphers, resource, freshResource,
                        projectileAbility, outgoingTargetId, outgoingTarget, 20.32));
                    freshResource.Monster.Dead = false;
                    freshResource.Monster.Life = freshResource.Monster.MaxLife;
                    var originalRoster = resource.Golems.All[freshResource.Id];
                    var replacementRoster = new GolemState.Golem(freshResource.Id, freshResource.Monster,
                        freshResource.Definition, freshResource.Source);
                    resource.Golems.All[freshResource.Id] = replacementRoster;
                    Assert.False(Game.GolemHitLocked("test", wire.Stream, wire.Ciphers, resource, freshResource,
                        projectileAbility, outgoingTargetId, outgoingTarget, 20.33));
                    resource.Golems.All[freshResource.Id] = originalRoster;

                    // Actual projectile route. Release with ES positive, deplete that ES through real incoming hits,
                    // then contact through ObjectProjectileOffense. A second flight releases at ES zero and recovers
                    // through the production shield kernel before contact. No direct GolemHitLocked contact is used here.
                    var fixedProjectile = projectileAbility with
                    {
                        Attack = projectileAbility.Attack with { Spell = true,
                            Damage = DamagePacket.Of(DamageType.Fire, 400, 400) },
                        Projectile = projectileAbility.Projectile! with { Count = 1, SpreadRadians = 0 },
                    };
                    impactSession = new Game.ProjectileSession("test", wire.Stream, wire.Ciphers, resource);
                    resource.ProjectileRuntime = impactSession;
                    freshResource.Abilities = [fixedProjectile];
                    freshResource.Monster.BusyUntil = 21;
                    freshResource.Charges.Clear();
                    freshResource.ShieldPool.SetMaximum(freshResource.MaximumShield, refill: true);
                    resource.IncomingHitRandom = new LowestRandom();
                    resourceSchedule.At = 21;
                    string Number(double value) => double.IsPositiveInfinity(value) ? "+inf"
                        : double.IsNegativeInfinity(value) ? "-inf"
                        : double.IsNaN(value) ? "nan" : value.ToString("0.#####");
                    string Position((int X, int Y) value) => $"({value.X},{value.Y})";
                    double Distance((int X, int Y) a, (int X, int Y) b) =>
                        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
                    double ProjectileDistance(GameServer.Core.Projectiles.ProjectileVector a, (int X, int Y) b) =>
                        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
                    double ContactReach(GameServer.Core.Projectiles.ProjectileEntity projectile,
                        SpawnedMonster target) => projectile.Profile.ContactCells is int cells
                        ? cells + LivingCollision.Size(target) + .5
                        // Public Actor creation uses Radius=0 and exposes Size separately; circle contact adds no radius.
                        : projectile.Profile.Radius;
                    string DamageText(DamagePacket damage) => string.Join("+", damage.Components.Select(component =>
                        $"{component.Type}:{Number(component.Range.Min)}-{Number(component.Range.Max)}"));
                    string AbilityText(GolemState.Golem golem, GolemState.Ability ability)
                    {
                        return $"{ability.Id}[effect={ability.Effect},skill={ability.Skill}," +
                            $"range={Number(ability.Attack.Range)},damage={DamageText(ability.Attack.Damage)}," +
                            $"contact={Number(ability.Contact)}," +
                            $"projectile={ability.Projectile?.Metadata ?? "none"}]";
                    }
                    string PendingText(GolemState.Golem golem, GolemState.Action? pending) => pending is null
                        ? "null" : $"{AbilityText(golem, pending.Ability)},target=0x{pending.Target:x8}," +
                            $"aim={Position(pending.Aim)},contact={Number(pending.ContactAt)},at={Number(pending.At)}," +
                            $"generation={pending.Generation},started={pending.Started}";
                    string TargetText(string role, uint id, SpawnedMonster target, double now,
                        (int X, int Y) golemAt, (int X, int Y) ownerAt)
                    {
                        var at = target.Walk.Position(now);
                        string effects = string.Join(",", target.Mods.Effects.Stats.OrderBy(pair => pair.Key,
                            StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
                        return $"{role}=0x{id:x8}[label={target.Label},pos21={Position(at)}," +
                            $"Hostile={target.Hostile},Dead={target.Dead},Life={target.Life},MaxLife={target.MaxLife}," +
                            $"Asleep={target.Asleep},connAsleep={resource.AsleepMonsters.Contains(id)}," +
                            $"Invulnerable={target.Invulnerable},BusyUntil={Number(target.BusyUntil)}," +
                            $"distFromG={Number(Distance(at, golemAt))},distFromOwner={Number(Distance(at, ownerAt))}," +
                            $"receivedActionFactor={Number(target.ReceivedActionFactor)}," +
                            $"receivedActionUntil={Number(target.ReceivedActionUntil)}," +
                            $"receivedActionFloor={target.ReceivedActionFloor}," +
                            $"ailmentActionFactor21={Number(target.Ailments.ActionFactor(now))}," +
                            $"monsterActionFactor21={Number(Game.MonsterActionFactor(target, now))}," +
                            $"effects=[{effects}]]";
                    }
                    string PublicSnapshot(string phase, GolemState.Golem golem, double now)
                    {
                        var golemAt = golem.Monster.Walk.Position(now);
                        var ownerAt = resource.Walk.Position(now);
                        var supported = Game.SupportedSkill(resource, golem.Source);
                        bool rosterSame = ReferenceEquals(resource.Golems.All.GetValueOrDefault(golem.Id), golem);
                        bool monstersSame = ReferenceEquals(resource.Monsters.GetValueOrDefault(golem.Id), golem.Monster);
                        string abilities = string.Join(" | ", golem.Abilities.Select(ability => AbilityText(golem, ability)));
                        return $"{phase} at={Number(now)},schedule.At={Number(resourceSchedule.At)}\n" +
                            $"record=0x{golem.Id:x8},definition=0x{golem.Definition.Effect}:{golem.Definition.Id}," +
                            $"kind={golem.Definition.Kind},variety={golem.Definition.Variety},source=0x{golem.Source:x8}," +
                            $"rosterSame={rosterSame},monstersSame={monstersSame}," +
                            $"minionsOwns={resource.Minions.Owns(golem.Id, golem.Monster)},dead={golem.Monster.Dead}," +
                            $"life={golem.Monster.Life}/{golem.Monster.MaxLife},shield={golem.Shield}/{golem.MaximumShield}," +
                            $"gemLevel={golem.GemLevel},quality={golem.Quality}," +
                            $"supportedGem=(effect={supported.Effect},level={supported.Level},quality={supported.Quality}," +
                            $"requirements={supported.RequirementsMet},supports={supported.Supports.Count})," +
                            $"closed={resource.Closed},leavingArea={resource.LeavingArea},paused={resource.Paused}," +
                            $"currentGameWork={Game.CurrentGameWork},inTown={Game.InTown(resource)}," +
                            $"clockElapsed={Number(resource.Clock.Elapsed.TotalSeconds)},areaTime={Number(Game.AreaTime(resource))}," +
                            $"connWalkPosition21={Position(ownerAt)},gWalkPosition21={Position(golemAt)}," +
                            $"gAggroRange={golem.Monster.AggroRange}," +
                            $"busyUntil={Number(golem.Monster.BusyUntil)},flinchUntil={Number(golem.Monster.FlinchUntil)}," +
                            $"actionGeneration={golem.Monster.ActionGeneration}," +
                            $"monsterActionFactor21={Number(Game.MonsterActionFactor(golem.Monster, now))}," +
                            $"receivedActionFactor={Number(golem.Monster.ReceivedActionFactor)}," +
                            $"receivedActionUntil={Number(golem.Monster.ReceivedActionUntil)}," +
                            $"receivedActionFloor={golem.Monster.ReceivedActionFloor}," +
                            $"ailmentActionFactor21={Number(golem.Monster.Ailments.ActionFactor(now))}," +
                            $"target=0x{golem.Target:x8}," +
                            $"pending={PendingText(golem, golem.Pending)}\n" +
                            $"targets=[{TargetText("first", outgoingTargetId, outgoingTarget, now, golemAt, ownerAt)}; " +
                            $"{TargetText("far", farTargetId, farTarget, now, golemAt, ownerAt)}]\n" +
                            $"abilities=[{abilities}]";
                    }
                    var golemAt21 = freshResource.Monster.Walk.Position(resourceSchedule.At);
                    var ownerAt21 = resource.Walk.Position(resourceSchedule.At);
                    outgoingTarget.Walk = new PlayerWalk(golemAt21.X + 15, golemAt21.Y, 0);
                    farTarget.Walk = new PlayerWalk(golemAt21.X + 140, golemAt21.Y, 0);
                    outgoingTarget.Life = outgoingTarget.MaxLife;
                    outgoingTarget.Burn = 0;
                    outgoingTarget.Regen = 0;
                    outgoingTarget.Ignites.Clear();
                    outgoingTarget.Bleeds.Clear();
                    outgoingTarget.Poisons.Clear();
                    double firstTargetDistance = Distance(outgoingTarget.Walk.Position(resourceSchedule.At), golemAt21);
                    Assert.True(firstTargetDistance <= fixedProjectile.Attack.Range + LivingCollision.Size(outgoingTarget),
                        $"first target {Position(outgoingTarget.Walk.Position(resourceSchedule.At))} is " +
                        $"{Number(firstTargetDistance)} from golem {Position(golemAt21)}; ability range is " +
                        $"{Number(fixedProjectile.Attack.Range)}, target body size {LivingCollision.Size(outgoingTarget)}");
                    double firstTargetOwnerDistance = Distance(outgoingTarget.Walk.Position(resourceSchedule.At), ownerAt21);
                    Assert.True(firstTargetOwnerDistance <= 100,
                        $"first target owner distance {Number(firstTargetOwnerDistance)} from owner {Position(ownerAt21)}");
                    var before21 = PublicSnapshot("BEFORE", freshResource, resourceSchedule.At);
                    await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                    var after21 = PublicSnapshot("AFTER", freshResource, resourceSchedule.At);
                    Assert.True(freshResource.Pending is not null,
                        $"first Pending is null after GolemsTick at 21\n{before21}\n{after21}");
                    Assert.Equal(outgoingTargetId, freshResource.Target);
                    var firstPending = freshResource.Pending!;
                    Assert.Equal(fixedProjectile.Effect, firstPending.Ability.Effect);
                    Assert.True(firstPending.Started);
                    double firstContact = firstPending.At;
                    resourceSchedule.At = firstContact;
                    await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                    Assert.Equal(outgoingTarget.MaxLife, outgoingTarget.Life);
                    double firstDepletionAt = firstContact + .101;
                    impactSession.AdvanceTo(GameServer.Core.Projectiles.ProjectileRuntime.Time(firstDepletionAt));
                    var firstProjectile = Assert.Single(impactSession.Runtime.Projectiles);
                    Assert.True(freshResource.Shield > 0);
                    Assert.Equal(outgoingTarget.MaxLife, outgoingTarget.Life);
                    var incomingSource = new SpawnedMonster("actual ES depletion", new(100, 100, 1),
                        100, 8, 1_000_000);
                    Assert.Empty(incomingSource.Effects.Stats);
                    Assert.Equal(0, incomingSource.Effects.CriticalChance);
                    resourceSchedule.At = firstDepletionAt;
                    var firstPhysicalResistance = Game.MonsterHitResistance(
                        freshResource.Monster, DamageType.Physical, default);
                    int firstPlayerArmour = Program.Stats.Key("minion_physical_damage_reduction_rating")
                        is uint firstDepletionPlayerArmourKey
                        ? freshResource.Player.GetValueOrDefault(firstDepletionPlayerArmourKey) : 0;
                    int firstGolemArmour = freshResource.Definition.IsGolem
                        && Program.Stats.Key("golem_physical_damage_reduction_rating")
                            is uint firstDepletionGolemArmourKey
                        ? freshResource.Player.GetValueOrDefault(firstDepletionGolemArmourKey) : 0;
                    int firstEffectArmour = freshResource.Monster.Effects["base_physical_damage_reduction_rating"];
                    int firstArmour = Math.Max(0, firstPlayerArmour + firstGolemArmour + firstEffectArmour);
                    int firstPhysicalReduction = freshResource.Monster.Effects.PhysicalReduction;
                    double firstDamageTaken = freshResource.Monster.Effects.DamageTaken(spell: true);
                    string firstPhysicalState = $"at={Number(firstDepletionAt)},shield={freshResource.Shield}," +
                        $"resistance={firstPhysicalResistance.Value}/{firstPhysicalResistance.Maximum}," +
                        $"penetration={firstPhysicalResistance.Penetration},armour={firstArmour}" +
                        $"(player={firstPlayerArmour},golem={firstGolemArmour},effects={firstEffectArmour})," +
                        $"physicalReduction={firstPhysicalReduction},damageTaken={Number(firstDamageTaken)}," +
                        $"alwaysCrit={incomingSource.Effects["always_crit"]}," +
                        $"criticalChance={incomingSource.Effects.CriticalChance}," +
                        $"areaDamageTaken={freshResource.Monster.Effects["damage_taken_+%_from_area_hits"]}";
                    Assert.True(firstPhysicalResistance.Value == 0 && firstPhysicalResistance.Penetration == 0
                        && firstArmour == 0 && firstPhysicalReduction == 0 && firstDamageTaken == 1,
                        $"first physical depletion preconditions are not orthogonal: {firstPhysicalState}");
                    uint firstRawDamage = freshResource.Shield;
                    uint firstLifeBeforeHit = freshResource.Monster.Life;
                    var firstPhysicalHit = new MonsterAttack(0x4000, 1, 100,
                        DamagePacket.Of(DamageType.Physical, checked((int)firstRawDamage),
                            checked((int)firstRawDamage)))
                    { Spell = true };
                    Assert.True(Game.GolemTakeHitLocked(resource, freshResource, 0x0A22D3D1,
                        incomingSource, firstPhysicalHit, firstDepletionAt), firstPhysicalState);
                    Assert.Equal(0u, freshResource.Shield);
                    Assert.Equal(firstLifeBeforeHit, freshResource.Monster.Life);
                    Assert.Same(firstProjectile, Assert.Single(impactSession.Runtime.Projectiles));
                    Assert.Equal(outgoingTarget.MaxLife, outgoingTarget.Life);
                    uint normalProjectileBefore = outgoingTarget.Life;
                    double firstRuntimeAt = impactSession.Runtime.Now /
                        (double)GameServer.Core.Projectiles.ProjectileRuntime.QuantaPerSecond;
                    double firstRemaining = ProjectileDistance(firstProjectile.Position,
                        outgoingTarget.Walk.Position(firstRuntimeAt));
                    double firstStop = firstRuntimeAt + Math.Max(0,
                        firstRemaining - ContactReach(firstProjectile, outgoingTarget)) / firstProjectile.Speed + .001;
                    Assert.True(firstStop >= resourceSchedule.At,
                        $"first runtime stop {Number(firstStop)} precedes area time {Number(resourceSchedule.At)}");
                    resourceSchedule.At = firstStop;
                    impactSession.AdvanceTo(GameServer.Core.Projectiles.ProjectileRuntime.Time(firstStop));
                    uint firstProjectileLoss = normalProjectileBefore - outgoingTarget.Life;
                    double firstRuntimeNow = impactSession.Runtime.Now /
                        (double)GameServer.Core.Projectiles.ProjectileRuntime.QuantaPerSecond;
                    string firstConnTarget = resource.Monsters.GetValueOrDefault(outgoingTargetId)?.Label ?? "none";
                    Assert.True(firstProjectileLoss == 300u,
                        $"first actual projectile loss={firstProjectileLoss}, expected=300, " +
                        $"firstContact={Number(firstContact)}, stop={Number(firstStop)}, " +
                        $"runtimeNow={Number(firstRuntimeNow)}, " +
                        $"projectiles={impactSession.Runtime.Projectiles.Count},shield={freshResource.Shield}, " +
                        $"targetLife={outgoingTarget.Life}/{outgoingTarget.MaxLife},targetDead={outgoingTarget.Dead}," +
                        $"hostile={outgoingTarget.Hostile},invulnerable={outgoingTarget.Invulnerable}," +
                        $"golemDead={freshResource.Monster.Dead},golemLife={freshResource.Monster.Life}," +
                        $"flinch={Number(freshResource.Monster.FlinchUntil)}," +
                        $"actionGeneration={freshResource.Monster.ActionGeneration}," +
                        $"pending={PendingText(freshResource, freshResource.Pending)}, " +
                        $"source={freshResource.Source},connTarget={firstConnTarget}");

                    impactSession.Invalidate();
                    await impactSession.OutputCompleted;
                    impactSession = null;
                    resource.ProjectileRuntime = null;

                    // The far diagnostic action contacts after enough real flight time for the production shield
                    // kernel to recharge. First recover, then deplete through one real Physical spell hit immediately
                    // before the prepared release, so release has ES zero and contact has ES positive.
                    double recoveryStart = firstContact + 20;
                    double secondDepletionAt = firstContact + 21.1;
                    resourceSchedule.At = recoveryStart;
                    freshResource.ShieldPool.Advance(recoveryStart, secondDepletionAt,
                        name => freshResource.Monster.Mods.Effects[name], alive: true);
                    Assert.True(freshResource.Shield > 0);
                    resourceSchedule.At = secondDepletionAt;
                    var secondPhysicalResistance = Game.MonsterHitResistance(
                        freshResource.Monster, DamageType.Physical, default);
                    int secondPlayerArmour = Program.Stats.Key("minion_physical_damage_reduction_rating")
                        is uint secondDepletionPlayerArmourKey
                        ? freshResource.Player.GetValueOrDefault(secondDepletionPlayerArmourKey) : 0;
                    int secondGolemArmour = freshResource.Definition.IsGolem
                        && Program.Stats.Key("golem_physical_damage_reduction_rating")
                            is uint secondDepletionGolemArmourKey
                        ? freshResource.Player.GetValueOrDefault(secondDepletionGolemArmourKey) : 0;
                    int secondEffectArmour = freshResource.Monster.Effects["base_physical_damage_reduction_rating"];
                    int secondArmour = Math.Max(0, secondPlayerArmour + secondGolemArmour + secondEffectArmour);
                    int secondPhysicalReduction = freshResource.Monster.Effects.PhysicalReduction;
                    double secondDamageTaken = freshResource.Monster.Effects.DamageTaken(spell: true);
                    string secondPhysicalState = $"at={Number(secondDepletionAt)},shield={freshResource.Shield}," +
                        $"resistance={secondPhysicalResistance.Value}/{secondPhysicalResistance.Maximum}," +
                        $"penetration={secondPhysicalResistance.Penetration},armour={secondArmour}" +
                        $"(player={secondPlayerArmour},golem={secondGolemArmour},effects={secondEffectArmour})," +
                        $"physicalReduction={secondPhysicalReduction},damageTaken={Number(secondDamageTaken)}," +
                        $"alwaysCrit={incomingSource.Effects["always_crit"]}," +
                        $"criticalChance={incomingSource.Effects.CriticalChance}," +
                        $"areaDamageTaken={freshResource.Monster.Effects["damage_taken_+%_from_area_hits"]}";
                    Assert.True(secondPhysicalResistance.Value == 0 && secondPhysicalResistance.Penetration == 0
                        && secondArmour == 0 && secondPhysicalReduction == 0 && secondDamageTaken == 1,
                        $"second physical depletion preconditions are not orthogonal: {secondPhysicalState}");
                    uint secondRawDamage = freshResource.Shield;
                    uint secondLifeBeforeHit = freshResource.Monster.Life;
                    var secondPhysicalHit = new MonsterAttack(0x4000, 1, 100,
                        DamagePacket.Of(DamageType.Physical, checked((int)secondRawDamage),
                            checked((int)secondRawDamage)))
                    { Spell = true };
                    Assert.True(Game.GolemTakeHitLocked(resource, freshResource, 0x0A22D3D3,
                        incomingSource, secondPhysicalHit, secondDepletionAt), secondPhysicalState);
                    Assert.Equal(0u, freshResource.Shield);
                    Assert.Equal(secondLifeBeforeHit, freshResource.Monster.Life);
                    freshResource.Pending = null;
                    freshResource.Abilities = [fixedProjectile];
                    freshResource.Charges.Clear();
                    impactSession = new Game.ProjectileSession("test", wire.Stream, wire.Ciphers, resource);
                    resource.ProjectileRuntime = impactSession;
                    double secondStart = firstContact + 21.2;
                    resourceSchedule.At = secondStart;
                    var releasePosition = freshResource.Monster.Walk.Position(secondStart);
                    farTarget = new SpawnedMonster("Fleshcrafter far impact target",
                        new PlayerWalk(releasePosition.X + 140, releasePosition.Y, 0), 100, 8, 1_000_000)
                    {
                        Hostile = true,
                        Mods = outgoingTarget.Mods,
                    };
                    resource.Monsters[farTargetId] = farTarget;
                    var secondAction = new GolemState.Action(fixedProjectile, farTargetId, farTarget,
                        farTarget.Walk.Position(secondStart), secondStart, freshResource.Monster.ActionGeneration);
                    Assert.Equal(releasePosition.X + 140, farTarget.Walk.Position(secondStart).X);
                    Assert.Equal(releasePosition.Y, farTarget.Walk.Position(secondStart).Y);
                    Game.GolemAbilityContactLocked("test", wire.Stream, wire.Ciphers, resource,
                        freshResource, secondAction, secondStart);
                    double secondRelease = secondStart + .1;
                    impactSession.AdvanceTo(GameServer.Core.Projectiles.ProjectileRuntime.Time(secondRelease));
                    Assert.Equal(0u, freshResource.Shield);
                    var secondProjectile = Assert.Single(impactSession.Runtime.Projectiles);
                    uint ignoredProjectileBefore = farTarget.Life;
                    Assert.Equal(farTarget.MaxLife, ignoredProjectileBefore);
                    double naturalSpeed = ProjectileSpeed.CellsPerSecond(fixedProjectile.Projectile!.Speed,
                        _ => 0, _ => 0);
                    Assert.Equal(69d, naturalSpeed);
                    double releaseCenterDistance = Distance(farTarget.Walk.Position(secondRelease), releasePosition);
                    double naturalFlight = releaseCenterDistance / naturalSpeed;
                    double rechargeDelay = EnergyShield.Delay(
                        name => freshResource.Monster.Mods.Effects[name]);
                    double recoveryAt = secondRelease + rechargeDelay + .1;
                    Assert.True(recoveryAt < secondRelease + naturalFlight,
                        $"recovery {recoveryAt:0.###} must precede natural contact {secondRelease + naturalFlight:0.###}");
                    double secondRemaining = ProjectileDistance(secondProjectile.Position,
                        farTarget.Walk.Position(secondRelease));
                    double secondReach = ContactReach(secondProjectile, farTarget);
                    double earliestContact = secondRelease + Math.Max(0, secondRemaining - secondReach) /
                        secondProjectile.Speed;
                    Assert.True(recoveryAt < earliestContact,
                        $"recovery {recoveryAt:0.###} must precede earliest contact {earliestContact:0.###}; " +
                        $"release {Position(releasePosition)}, projectile {secondProjectile.Position}, " +
                        $"target {Position(farTarget.Walk.Position(secondRelease))}, remaining {secondRemaining:0.###}, " +
                        $"reach {secondReach:0.###}, speed {secondProjectile.Speed:0.###}");
                    resourceSchedule.At = recoveryAt;
                    impactSession.AdvanceTo(GameServer.Core.Projectiles.ProjectileRuntime.Time(recoveryAt));
                    Assert.Same(secondProjectile, Assert.Single(impactSession.Runtime.Projectiles));
                    Assert.Equal(farTarget.MaxLife, farTarget.Life);
                    freshResource.ShieldPool.Advance(secondRelease, recoveryAt,
                        name => freshResource.Monster.Mods.Effects[name], alive: true);
                    Assert.True(freshResource.Shield > 0);
                    Assert.InRange(freshResource.Shield, 1u, freshResource.MaximumShield);
                    double contactStop = earliestContact + .001;
                    Assert.True(contactStop >= resourceSchedule.At,
                        $"contact stop {contactStop:0.###} precedes area time {resourceSchedule.At:0.###}");
                    resourceSchedule.At = contactStop;
                    impactSession.AdvanceTo(GameServer.Core.Projectiles.ProjectileRuntime.Time(contactStop));
                    Assert.Equal(400u, ignoredProjectileBefore - farTarget.Life);
                }
                finally
                {
                    if (impactSession is not null)
                    {
                        impactSession.Invalidate();
                        await impactSession.OutputCompleted;
                    }
                    resource.ProjectileRuntime = preservedProjectileRuntime;
                    resource.Monsters.Remove(outgoingTargetId);
                    resource.Monsters.Remove(farTargetId);
                    void RestoreEquipment(byte row, Item item)
                    {
                        var current = resourceInventory.Items(row).ToArray();
                        if (current.Length == 1 && current[0].Item == item) return;
                        foreach (var existing in current) resourceInventory.Remove(row, existing.Id);
                        resourceInventory.Add(row, 0, 0, item);
                    }
                    RestoreEquipment(PlayerInventories.BodyArmour, preservedArmourItem);
                    RestoreEquipment(PlayerInventories.Weapon1, preservedWeaponItem);
                    resource.PlayerStats.Clear();
                    foreach (var (key, value) in preservedPlayerStats) resource.PlayerStats[key] = value;
                    RecalculateResource();
                    freshResource.Abilities = preservedAbilities;
                    freshResource.Monster.Mods = preservedMods;
                    freshResource.Player = preservedGolemPlayer;
                    freshResource.Monster.Walk = preservedGolemWalk;
                    freshResource.Target = preservedGolemTarget;
                    freshResource.Monster.Aggro = preservedAggro;
                    freshResource.Monster.LastTarget = preservedLastTarget;
                    freshResource.Monster.Dead = false;
                    freshResource.Monster.MaxLife = preservedMaximumLife;
                    freshResource.Monster.Life = preservedLife;
                    freshResource.ShieldPool.SetMaximum(preservedMaximumShield);
                    freshResource.ShieldPool.Restore(preservedShield, preservedAreaTime);
                    freshResource.Regeneration = preservedRegeneration;
                    freshResource.LastStep = preservedLastStep;
                    freshResource.Monster.BusyUntil = preservedBusyUntil;
                    freshResource.Monster.FlinchUntil = preservedFlinchUntil;
                    freshResource.Monster.StunImmuneUntil = preservedStunImmuneUntil;
                    freshResource.Monster.ActionGeneration = preservedActionGeneration;
                    freshResource.Monster.SwingGeneration = preservedSwingGeneration;
                    freshResource.Monster.SwingOrderedAt = preservedSwingOrderedAt;
                    freshResource.Monster.ReceivedActionFactor = preservedReceivedActionFactor;
                    freshResource.Monster.ReceivedActionUntil = preservedReceivedActionUntil;
                    freshResource.Monster.ReceivedActionFloor = preservedReceivedActionFloor;
                    freshResource.Monster.RecipientDebuffRate = preservedRecipientDebuffRate;
                    freshResource.Pending = preservedPending;
                    freshResource.NextMove = preservedNextMove;
                    freshResource.NextOrb = preservedNextOrb;
                    freshResource.Combo = preservedCombo;
                    freshResource.InputHash = preservedInputHash;
                    freshResource.Charges.Clear();
                    foreach (var (effect, charges) in preservedCharges) freshResource.Charges[effect] = charges;
                    resource.IncomingHitRandom = preservedIncomingRandom;
                    resourceSchedule.At = preservedAreaTime;
                }

                resource.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 2;
                RecalculateResource();
                resourceSchedule.At = 20.3;
                uint capPeerId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                    resourceSource.Id, resourceDefinition, (100, 100));
                var capPeer = resource.Golems.All[capPeerId];
                resourceSchedule.At = 20.4;
                uint exactNewerId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                    resourceSource.Id, resourceDefinition, (100, 100), arrival: true, replacingOld: capPeer);
                Assert.NotEqual(0u, exactNewerId);
                Assert.Equal(2, resource.Golems.All.Count);
                Assert.Same(freshResource, resource.Golems.All[freshResource.Id]);
                Assert.DoesNotContain(capPeer, resource.Golems.All.Values);
                Assert.Same(resource.Golems.All[exactNewerId], resource.Golems.All.Values.Single(g => g.Id == exactNewerId));
                var exactNewer = resource.Golems.All[exactNewerId];
                resource.Monsters.Remove(exactNewerId);
                resource.Minions.Remove(exactNewerId);
                resource.MinionOrigins.TryRemove(exactNewerId, out _);
                resource.Golems.All.Remove(exactNewerId);
                resource.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 1;
                RecalculateResource();
                resource.Golems.RefreshAt = 0;

                resourceSchedule.At = 20.25;
                freshResource.Monster.Life = freshResource.Monster.MaxLife - 137;
                freshResource.ShieldPool.Damage(Math.Max(1, (int)freshResource.Shield / 3), bypass: false, now: 20.25);
                freshResource.Regeneration = .375;
                uint savedLife = freshResource.Monster.Life;
                uint savedShield = freshResource.Shield;
                uint savedMaximumLife = freshResource.Monster.MaxLife;
                uint savedMaximumShield = freshResource.MaximumShield;
                Assert.InRange(savedLife, 1u, savedMaximumLife - 1);
                Assert.InRange(savedShield, 1u, savedMaximumShield - 1);
                Game.SaveGolems(resource);
                string currentResourceJson = CharacterSaves.ModuleData(resourceCharacter, "Game.Golems")!;
                var currentResourceEntries = JsonSerializer.Deserialize<GolemState.Saved[]>(currentResourceJson)!;
                var currentSaved = Assert.Single(currentResourceEntries,
                    entry => entry.Effect == resourceDefinition.Id && entry.ReviveSeconds == 0);
                Assert.NotNull(currentSaved.Resources);
                Assert.Equal(savedLife, currentSaved.Resources!.Life);
                Assert.Equal(.375, currentSaved.Resources.LifeRegeneration);
                Assert.Equal(savedShield, currentSaved.Resources.EnergyShield.Current);
                Assert.Equal(0d, currentSaved.Resources.EnergyShield.InterruptedSecondsAgo!.Value);

                resourceSchedule.At = 1000;
                var restoredResource = NewResourceClient("integer-resource-restored", 1000);
                var restoredResourcePackets = await CaptureResource(
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, restoredResource, 1000));
                var restoredResourceActor = Assert.Single(restoredResource.Golems.All).Value;
                uint restoredMaximumLife = restoredResourceActor.Monster.MaxLife;
                uint restoredMaximumShield = restoredResourceActor.MaximumShield;
                Assert.Equal(Math.Min(savedLife, restoredMaximumLife), restoredResourceActor.Monster.Life);
                Assert.Equal(Math.Min(savedShield, restoredMaximumShield), restoredResourceActor.Shield);
                Assert.Equal(.375, restoredResourceActor.Regeneration);
                Assert.Equal(0d, restoredResourceActor.ShieldPool.Capture(1000).InterruptedSecondsAgo!.Value);
                AssertCreateResources(restoredResourcePackets, restoredResourceActor.Id,
                    restoredResourceActor.Monster.Life, restoredResourceActor.Shield,
                    restoredMaximumLife, restoredMaximumShield);

                uint noHealLife = restoredResourceActor.Monster.Life;
                uint noHealShield = restoredResourceActor.Shield;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, restoredResource, 1000);
                Assert.Equal(noHealLife, restoredResourceActor.Monster.Life);
                Assert.Equal(noHealShield, restoredResourceActor.Shield);
                Game.SaveGolems(restoredResource);
                var repeatedEntries = JsonSerializer.Deserialize<GolemState.Saved[]>(
                    CharacterSaves.ModuleData(resourceCharacter, "Game.Golems")!)!;
                var repeatedSaved = Assert.Single(repeatedEntries, entry => entry.Effect == resourceDefinition.Id);
                Assert.NotNull(repeatedSaved.Resources);
                Assert.Equal(noHealLife, repeatedSaved.Resources!.Life);
                Assert.Equal(noHealShield, repeatedSaved.Resources.EnergyShield.Current);
                Assert.Equal(.375, repeatedSaved.Resources.LifeRegeneration);

                resourceSchedule.At = 20.5;
                resource.Golems.RefreshAt = 0;
                var rebuildLife = freshResource.Monster.Life;
                var rebuildShield = freshResource.Shield;
                var rebuildBirth = freshResource.SummonedAt;
                Task<uint> preparedGolem;
                int levelTwenty = Program.Items.Gem(Program.Items.ByHash(resourceGem.TypeHash)!)!.ExperienceFor(20);
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id, item => item with
                {
                    Sockets = [new(SocketColour.White, resourceGem with { GemExperience = levelTwenty })],
                });
                var raceAttacker = new SpawnedMonster("replacement race attacker", new(100, 100, 1), 100, 8, 1_000_000);
                var raceHit = new MonsterAttack(0x4000, 1, 100, DamagePacket.Of(DamageType.Physical, 1, 1)) { Spell = true };
                var golemRaceGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(golemRaceGate.Wait(0));
                double rejectedIgniteUntil = default;
                try
                {
                    preparedGolem = Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                        resourceSource.Id, resourceDefinition, (100, 100), arrival: true, replacingOld: freshResource);
                    Assert.False(preparedGolem.IsCompleted, "the actual golem replacement did not reach the held create gate");
                    Assert.True(freshResource.Monster.Ignites.Add(20.5, 1, 2));
                    rejectedIgniteUntil = freshResource.Monster.Ignites.Until;
                    Assert.Equal(0, freshResource.Monster.Ignites.Advance(20.75, 1));
                    resourceSchedule.At = 20.75;
                    uint golemLifeBeforeHit = freshResource.Monster.Life;
                    uint golemShieldBeforeHit = freshResource.Shield;
                    resource.IncomingHitRandom = new LowestRandom();
                    Assert.True(Game.GolemTakeHitLocked(resource, freshResource, 0x09000031, raceAttacker, raceHit, 20.75));
                    Assert.True(freshResource.Monster.Life < golemLifeBeforeHit || freshResource.Shield < golemShieldBeforeHit);
                    rebuildLife = freshResource.Monster.Life;
                    rebuildShield = freshResource.Shield;
                }
                finally { golemRaceGate.Release(); }
                Assert.Equal(0u, await preparedGolem);
                Assert.Same(freshResource, resource.Golems.All[freshResource.Id]);
                Assert.Equal(rebuildLife, freshResource.Monster.Life);
                Assert.Equal(rebuildShield, freshResource.Shield);
                Assert.Equal(rejectedIgniteUntil, freshResource.Monster.Ignites.Until);
                Assert.Equal(1, freshResource.Monster.Ignites.Advance(21.75, 1));
                freshResource.Monster.Ignites.Clear();
                freshResource.Monster.Ignites.Buff = null;
                var rebuildSnapshot = freshResource.ShieldPool.Capture(resourceSchedule.At);
                var rebuildPackets = await CaptureResource(
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At));
                var rebuiltResource = Assert.Single(resource.Golems.All).Value;
                Assert.True(rebuiltResource.Monster.MaxLife >= savedMaximumLife);
                Assert.Equal(rebuildBirth, rebuiltResource.SummonedAt);
                Assert.Equal(Math.Min(rebuildLife, rebuiltResource.Monster.MaxLife), rebuiltResource.Monster.Life);
                Assert.Equal(Math.Min(rebuildShield, rebuiltResource.MaximumShield), rebuiltResource.Shield);
                Assert.Equal(.375, rebuiltResource.Regeneration);
                Assert.Equal(rebuildSnapshot.InterruptedSecondsAgo,
                    rebuiltResource.ShieldPool.Capture(resourceSchedule.At).InterruptedSecondsAgo);
                AssertCreateResources(rebuildPackets, rebuiltResource.Id, rebuiltResource.Monster.Life,
                    rebuiltResource.Shield, rebuiltResource.Monster.MaxLife, rebuiltResource.MaximumShield);

                resourceSchedule.At = 20.75;
                rebuiltResource.Monster.Life = 1;
                rebuiltResource.ShieldPool.Empty();
                Dictionary<uint, int> sourceStatsBefore;
                using (new Game.PlayerStatScope(resource, fresh: true))
                    sourceStatsBefore = new(Game.CharacterPlayerStats(resource));
                Task<uint> preparedWithSource = null!;
                Task sourcePickup = null!;
                var sourceRaceGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(sourceRaceGate.Wait(0));
                try
                {
                    preparedWithSource = Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                        resourceSource.Id, resourceDefinition, (100, 100), arrival: true, replacingOld: rebuiltResource);
                    Assert.False(preparedWithSource.IsCompleted);
                    Assert.True(rebuiltResource.Monster.Ignites.Add(20.75, 1, 2));
                    Assert.Equal(0, rebuiltResource.Monster.Ignites.Advance(21, 1));
                    sourcePickup = Game.PickUpItem("test", wire.Stream, wire.Ciphers, resource,
                        new InstanceItems.PickUpItem(PlayerInventories.InventoryId(PlayerInventories.BodyArmour),
                            armourSlot.Id, 3, 0, 0));
                    Assert.False(sourcePickup.IsCompleted, "public equipment pickup did not suspend behind the held create gate");
                    Assert.Empty(resourceInventory.Items(PlayerInventories.BodyArmour));
                    Assert.Equal(armourSlot.Id, Assert.Single(resourceInventory.Items(PlayerInventories.Cursor)).Id);
                    Dictionary<uint, int> sourceStatsAfter;
                    using (new Game.PlayerStatScope(resource, fresh: true))
                        sourceStatsAfter = new(Game.CharacterPlayerStats(resource));
                    Assert.Contains(sourceStatsBefore, pair => !sourceStatsAfter.TryGetValue(pair.Key, out int value)
                        || value != pair.Value);
                }
                finally { sourceRaceGate.Release(); }
                Assert.Equal(0u, await preparedWithSource);
                await sourcePickup;
                Assert.Same(rebuiltResource, resource.Golems.All[rebuiltResource.Id]);
                Assert.Equal(1u, rebuiltResource.Monster.Life);
                Assert.Equal(0u, rebuiltResource.Shield);
                Assert.Equal(1, rebuiltResource.Monster.Ignites.Advance(21.75, 1));
                rebuiltResource.Monster.Ignites.Clear();
                rebuiltResource.Monster.Ignites.Buff = null;

                await Game.PlaceItem("test", wire.Stream, wire.Ciphers, resource,
                    new InstanceItems.PlaceItem(PlayerInventories.InventoryId(PlayerInventories.BodyArmour),
                        0, 0, 0, 3, 0, 0));
                Assert.Equal(armourSlot.Id, Assert.Single(resourceInventory.Items(PlayerInventories.BodyArmour)).Id);
                resourceSchedule.At = 20.8;
                uint sourceRetryId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                    resourceSource.Id, resourceDefinition, (100, 100), arrival: true, replacingOld: rebuiltResource);
                Assert.NotEqual(0u, sourceRetryId);
                GolemState.Golem sourceRetry = resource.Golems.All[sourceRetryId];
                Assert.Same(sourceRetry, resource.Golems.All.Values.Single());
                Assert.Equal(1u, sourceRetry.Monster.Life);
                Assert.Equal(0u, sourceRetry.Shield);
                AssertIntegerPool(sourceRetry);

                // Restore the real socketed source to its intrinsic level-1 gem before proving that overriding raw
                // global gem stats, rather than the stored experience, drives the effective level and quality.
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id,
                    item => item with { Sockets = [new(SocketColour.White, resourceGem)] });
                RecalculateResource();
                resource.Golems.RefreshAt = 0;
                resourceSchedule.At = 20.9;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                sourceRetry = Assert.Single(resource.Golems.All).Value;
                Assert.Equal(1, sourceRetry.GemLevel);

                // Labelled nativePlayerStats diagnostic: the intrinsic socketed gem is level 1 / quality 0.
                // Raw global gem stats make the same source level 2 with positive quality, and the actual replacement
                // must admit the current actor while preserving its exact points.
                using (new Game.PlayerStatScope(resource, fresh: true))
                {
                    var intrinsic = Game.SupportedSkill(resource, resourceSource.Id);
                    Assert.Equal(1, intrinsic.Level);
                    Assert.Equal(0, intrinsic.Quality);
                }
                uint allGemLevel = Program.Stats.Key("all_skill_gem_level_+")!.Value;
                uint allGemQuality = Program.Stats.Key("all_skill_gem_quality_+")!.Value;
                resource.PlayerStats[allGemLevel] = 1;
                resource.PlayerStats[allGemQuality] = 10;
                var effective = Game.SupportedSkill(resource, resourceSource.Id);
                Assert.Equal(2, effective.Level);
                Assert.Equal(10, effective.Quality);
                sourceRetry.Monster.Life = 1;
                sourceRetry.ShieldPool.Empty();
                resourceSchedule.At = 21;
                var resourcePointLife = sourceRetry.Monster.Life;
                var resourcePointShield = sourceRetry.Shield;
                var stateCommitGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(stateCommitGate.Wait(0));
                Task<uint> preparedStateCommit = null!;
                EnergyShield.Snapshot stateCommitSnapshot = default;
                try
                {
                    preparedStateCommit = Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                        resourceSource.Id, resourceDefinition, (100, 100), arrival: true, replacingOld: sourceRetry);
                    Assert.False(preparedStateCommit.IsCompleted,
                        "the actual current-state replacement did not reach the held create gate");
                    // This is a neutral state control, not a source row: only the current live model is changed.
                    Assert.True(sourceRetry.Monster.Ignites.Add(21, 1, 2));
                    Assert.Equal(0, sourceRetry.Monster.Ignites.Advance(21.5, 1));
                    stateCommitSnapshot = sourceRetry.ShieldPool.Capture(21.5);
                    resourceSchedule.At = 21.5;
                }
                finally { stateCommitGate.Release(); }
                uint stateCommitId = await preparedStateCommit;
                Assert.NotEqual(0u, stateCommitId);
                sourceRetry = resource.Golems.All[stateCommitId];
                Assert.Single(resource.Golems.All);
                Assert.Equal(2, sourceRetry.GemLevel);
                Assert.Equal(10, sourceRetry.Quality);
                Assert.Equal(resourcePointLife, sourceRetry.Monster.Life);
                Assert.Equal(resourcePointShield, sourceRetry.Shield);
                Assert.Equal(stateCommitSnapshot.InterruptedSecondsAgo,
                    sourceRetry.ShieldPool.Capture(21.5).InterruptedSecondsAgo);
                Assert.True(sourceRetry.Monster.Ignites.Burning);
                Assert.Equal(1, sourceRetry.Monster.Ignites.Advance(22.5, 1));
                sourceRetry.Monster.Ignites.Clear();
                sourceRetry.Monster.Ignites.Buff = null;
                resource.PlayerStats.Remove(allGemLevel);
                resource.PlayerStats.Remove(allGemQuality);
                RecalculateResource();

                // A newer golem keeps the owner-wide flag true, but the old actor's own eight-second window is rechecked
                // when the held create commits. The newer actor is a real second SummonGolem, not a clock assignment.
                uint golemLimitKey = Program.Stats.Key("number_of_golems_allowed")!.Value;
                int previousRawGolemLimit = resource.PlayerStats.GetValueOrDefault(golemLimitKey);
                int skillGolemLimit = Game.SupportedSkill(resource, resourceSource.Id).Stats
                    .GetValueOrDefault(Program.Stats.Key("base_number_of_golems_allowed")!.Value);
                int LimitForRaw(int raw)
                {
                    var stats = new Dictionary<uint, int>(resource.PlayerStats) { [golemLimitKey] = raw };
                    return GolemCatalog.Limit(skillGolemLimit,
                        name => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0);
                }
                int capTwoRaw = Enumerable.Range(0, 16).First(raw => LimitForRaw(raw) == 2);
                resource.PlayerStats[golemLimitKey] = capTwoRaw;
                RecalculateResource();
                resourceSchedule.At = 28.1;
                uint youngerId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                    resourceSource.Id, resourceDefinition, (100, 100));
                Assert.NotEqual(0u, youngerId);
                var younger = resource.Golems.All[youngerId];
                Assert.NotSame(sourceRetry, younger);
                Assert.Equal(20.25, sourceRetry.SummonedAt);
                Assert.Equal(28.1, younger.SummonedAt);
                Assert.Equal(2, resource.Golems.All.Count);
                Assert.Equal(2, younger.Limit);
                Assert.True(resource.Minions.Owns(sourceRetry.Id, sourceRetry.Monster));
                Assert.True(resource.Minions.Owns(younger.Id, younger.Monster));
                resourceSchedule.At = 28.2;
                Assert.True(Game.GolemSummonedInPastEightSeconds(sourceRetry, resourceSchedule.At));
                Assert.True(Game.GolemSummonedInPastEightSeconds(younger, resourceSchedule.At));
                Assert.True(Game.GolemSummonedInPastEightSeconds(resource, resourceSchedule.At));
                var youngWindowGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(youngWindowGate.Wait(0));
                Task<uint> staleYoungWindowReplacement = null!;
                try
                {
                    staleYoungWindowReplacement = Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                        resourceSource.Id, resourceDefinition, (100, 100), arrival: true, replacingOld: sourceRetry);
                    Assert.False(staleYoungWindowReplacement.IsCompleted);
                }
                finally
                {
                    resourceSchedule.At = 28.25;
                    youngWindowGate.Release();
                }
                Assert.Equal(0u, await staleYoungWindowReplacement);
                Assert.Same(sourceRetry, resource.Golems.All[sourceRetry.Id]);
                Assert.Same(younger, resource.Golems.All[younger.Id]);
                Assert.True(resource.Minions.Owns(sourceRetry.Id, sourceRetry.Monster));
                Assert.True(resource.Minions.Owns(younger.Id, younger.Monster));
                Assert.True(Game.GolemSummonedInPastEightSeconds(resource, 28.25));
                Assert.False(Game.GolemSummonedInPastEightSeconds(sourceRetry, 28.25));

                // The actual live entrypoint admits the same replacement after expiry, preserving the original birth.
                uint expiredSourceRetryId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                    resourceSource.Id, resourceDefinition, (100, 100), arrival: true, replacingOld: sourceRetry);
                Assert.NotEqual(0u, expiredSourceRetryId);
                sourceRetry = resource.Golems.All[expiredSourceRetryId];
                Assert.NotSame(sourceRetry, younger);
                Assert.Equal(20.25, sourceRetry.SummonedAt);
                Assert.Equal(0, Game.GolemRecentDamageIncrease(sourceRetry, 28.25));

                // Fixture cleanup only: remove the extra real younger actor and restore the original raw override for
                // the independent lethal current-state control below. This does not claim an actual cap of one.
                resource.Monsters.Remove(younger.Id);
                resource.Minions.Remove(younger.Id);
                resource.MinionOrigins.TryRemove(younger.Id, out _);
                resource.Golems.All.Remove(younger.Id);
                resource.PlayerStats[golemLimitKey] = previousRawGolemLimit;
                RecalculateResource();
                Assert.Single(resource.Golems.All);

                resourceSchedule.At = 20.9;
                Task<uint> preparedLethal = null!;
                var lethalRaceGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(lethalRaceGate.Wait(0));
                try
                {
                    preparedLethal = Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                        resourceSource.Id, resourceDefinition, (100, 100), arrival: true, replacingOld: sourceRetry);
                    Assert.False(preparedLethal.IsCompleted);
                    Assert.True(Game.GolemTakeHitLocked(resource, sourceRetry, 0x09000032, raceAttacker,
                        raceHit with { Damage = DamagePacket.Of(DamageType.Physical, 1_000_000, 1_000_000) }, 20.9));
                    Assert.True(sourceRetry.Monster.Dead);
                }
                finally { lethalRaceGate.Release(); }
                Assert.Equal(0u, await preparedLethal);
                Assert.Same(sourceRetry, resource.Golems.All[sourceRetry.Id]);
                Assert.True(sourceRetry.Monster.Dead);

                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id,
                    item => item with { Sockets = [new(SocketColour.White, resourceGem)] });
                foreach (var removedGolem in resource.Golems.All.Values.ToArray())
                {
                    resource.Monsters.Remove(removedGolem.Id);
                    resource.Minions.Remove(removedGolem.Id);
                    resource.MinionOrigins.TryRemove(removedGolem.Id, out _);
                }
                resource.Golems.All.Clear();
                resource.Golems.RefreshAt = 0;

                string legacyGolemJson = JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        currentSaved.Effect, currentSaved.Skill, Life = .42, ReviveSeconds = 0d,
                        currentSaved.SummonedSecondsRemaining,
                    },
                });
                Assert.DoesNotContain("\"Resources\"", legacyGolemJson);
                CharacterSaves.ChangeModuleData(resourceCharacter, "Game.Golems", _ => legacyGolemJson);
                var legacyResource = NewResourceClient("integer-resource-legacy", 1100);
                var legacyPackets = await CaptureResource(
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, legacyResource, 1100));
                var legacyResourceActor = Assert.Single(legacyResource.Golems.All).Value;
                Assert.Equal((uint)(legacyResourceActor.Monster.MaxLife * .42), legacyResourceActor.Monster.Life);
                Assert.True(legacyResourceActor.MaximumShield > 0);
                Assert.Equal(0u, legacyResourceActor.Shield);
                Assert.Equal(0d, legacyResourceActor.ShieldPool.Capture(1100).InterruptedSecondsAgo!.Value);
                AssertCreateResources(legacyPackets, legacyResourceActor.Id, legacyResourceActor.Monster.Life, 0,
                    legacyResourceActor.Monster.MaxLife, legacyResourceActor.MaximumShield);
                double legacyDelay = EnergyShield.Delay(name => legacyResourceActor.Monster.Mods.Effects[name]);
                resourceSchedule.At = 1100 + legacyDelay - .01;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, legacyResource, resourceSchedule.At);
                Assert.Equal(0u, legacyResourceActor.Shield);
                resourceSchedule.At = 1100 + legacyDelay + .1;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, legacyResource, resourceSchedule.At);
                Assert.InRange(legacyResourceActor.Shield, 1u, legacyResourceActor.MaximumShield - 1);

                var zeroResources = new GolemState.MinionResourceSnapshot(0, 0, new(0, 0, 0));
                var zeroEntry = currentSaved with { Life = 0, ReviveSeconds = 0, Resources = zeroResources };
                CharacterSaves.ChangeModuleData(resourceCharacter, "Game.Golems",
                    _ => JsonSerializer.Serialize(new[] { zeroEntry }));
                var zeroResource = NewResourceClient("integer-resource-zero", 1200);
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, zeroResource, 1200);
                var zeroActor = Assert.Single(zeroResource.Golems.All).Value;
                Assert.True(zeroActor.Monster.Dead);
                Assert.Equal(0u, zeroActor.Monster.Life);
                Assert.Equal(0u, zeroActor.Shield);
                Assert.Equal(1202, zeroActor.ReviveAt);

                var waitingEntry = currentSaved with
                {
                    Life = 0, ReviveSeconds = .5, Resources = zeroResources,
                };
                CharacterSaves.ChangeModuleData(resourceCharacter, "Game.Golems",
                    _ => JsonSerializer.Serialize(new[] { waitingEntry }));
                var waitingResource = NewResourceClient("integer-resource-waiting", 1300);
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, waitingResource, 1300);
                Assert.Empty(waitingResource.Golems.All);
                Assert.Single(waitingResource.Golems.Waiting);
                resourceSchedule.At = 1300.5;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, waitingResource, resourceSchedule.At);
                var revivedResource = Assert.Single(waitingResource.Golems.All).Value;
                Assert.False(revivedResource.Monster.Dead);
                Assert.Equal(revivedResource.Monster.MaxLife, revivedResource.Monster.Life);
                Assert.Equal(revivedResource.MaximumShield, revivedResource.Shield);

                var neverCrit = new SpawnedMonster("integer resource attacker", new(100, 100, 1), 100, 8, 1_000_000)
                {
                    Mods = MonsterModSet.None with
                    {
                        Effects = new(new Dictionary<string, int>
                        {
                            ["critical_strike_chance_+%"] = -10_000,
                            ["accuracy_rating_+%"] = -100,
                        }),
                    },
                };
                Assert.Equal(0, neverCrit.Effects.CriticalChance);
                Assert.Equal(5, DamageModel.AccuracyHitChance(0, 0));
                MonsterAttack FixedHit(DamageType type, int amount) =>
                    new(0x4000, 1, 100, DamagePacket.Of(type, amount, amount)) { Spell = true };

                resourceSchedule.At = 20;
                uint offId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource, resourceSource.Id,
                    resourceDefinition, (100, 100));
                var offActor = resource.Golems.All[offId];
                Assert.Equal(0, offActor.Monster.Mods.Effects["chaos_damage_does_not_bypass_energy_shield"]);
                Assert.Equal(20, Game.GolemPools.Calculate(Game.Monsters, offActor.Monster.Variety!,
                    offActor.Monster.Level, offActor, offActor.Player, offActor.Monster.Mods.Effects).ChaosResistance);
                AssertIntegerPool(offActor);
                offActor.Monster.Life = 600;
                uint offLife = offActor.Monster.Life, offShield = offActor.Shield;
                Assert.True(Game.GolemTakeHitLocked(resource, offActor, 0x08000010, neverCrit,
                    FixedHit(DamageType.Chaos, 400), 20.1));
                Assert.Equal(0, offActor.Monster.Mods.Effects["chaos_damage_does_not_bypass_energy_shield"]);
                Assert.Equal(offShield, offActor.Shield);
                Assert.Equal(offLife - 320, offActor.Monster.Life);
                AssertConservation(offActor, offLife, offShield, offActor.Monster.Life, offActor.Shield, 320);

                resourceInventory.Change(PlayerInventories.BodyArmour, armourSlot.Id,
                    item => item with { Mods = originalArmourMods });
                RecalculateResource();
                uint onId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource, resourceSource.Id,
                    resourceDefinition, (100, 100));
                Assert.NotEqual(offId, onId);
                var onActor = resource.Golems.All[onId];
                Assert.True(onActor.Monster.MaxLife > 600);
                onActor.Monster.Life = 600;
                uint onLife = onActor.Monster.Life, onShield = onActor.Shield;
                Assert.True(onShield > 320);
                Assert.Equal(onShield, onActor.MaximumShield);
                Assert.True(Game.GolemTakeHitLocked(resource, onActor, 0x08000010, neverCrit,
                    FixedHit(DamageType.Chaos, 400), 20.2));
                Assert.Equal(1, onActor.Monster.Mods.Effects["chaos_damage_does_not_bypass_energy_shield"]);
                AssertIntegerPool(onActor);
                Assert.Equal(onLife, onActor.Monster.Life);
                Assert.Equal(onShield - 320, onActor.Shield);
                AssertConservation(onActor, onLife, onShield, onActor.Monster.Life, onActor.Shield, 320);

                uint physicalLife = onActor.Monster.Life, physicalShield = onActor.Shield;
                Assert.True(Game.GolemTakeHitLocked(resource, onActor, 0x08000010, neverCrit,
                    FixedHit(DamageType.Physical, 50), 20.3));
                Assert.Equal(physicalShield - 50, onActor.Shield);
                Assert.Equal(physicalLife, onActor.Monster.Life);
                AssertConservation(onActor, physicalLife, physicalShield, onActor.Monster.Life, onActor.Shield, 50);

                uint spillLife = onActor.Monster.Life, spillShield = onActor.Shield;
                Assert.True(spillShield < 320);
                Assert.True(Game.GolemTakeHitLocked(resource, onActor, 0x08000010, neverCrit,
                    FixedHit(DamageType.Chaos, 400), 20.4));
                Assert.Equal(spillLife - (320 - spillShield), onActor.Monster.Life);
                Assert.Equal(0u, onActor.Shield);
                AssertConservation(onActor, spillLife, spillShield, onActor.Monster.Life, onActor.Shield, 320);

                resourceSchedule.At = 30;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                uint retirementId = onActor.Id;
                uint retirementLife = onActor.Monster.Life;
                uint completeMaximumShield = onActor.MaximumShield;
                Assert.Same(onActor, resource.Golems.All[retirementId]);
                Assert.Equal(517u, retirementLife);
                Assert.Equal(completeMaximumShield, onActor.Shield);

                uint removedPlacementId = armourSlot.Id;
                Assert.NotNull(resourceInventory.Remove(PlayerInventories.BodyArmour, removedPlacementId));
                RecalculateResource();
                Assert.True(Game.GolemTakeHitLocked(resource, onActor, 0x08000010, neverCrit,
                    FixedHit(DamageType.Chaos, 400), 30.1));
                Assert.Same(onActor, resource.Golems.All[retirementId]);
                Assert.False(onActor.Monster.Dead);
                Assert.Equal(0, onActor.Monster.Mods.Effects["chaos_damage_does_not_bypass_energy_shield"]);
                uint bareMaximumShield = (uint)Math.Floor(onActor.Monster.MaxLife * .15d);
                Assert.Equal(bareMaximumShield, onActor.MaximumShield);
                Assert.Equal(Math.Min(completeMaximumShield, bareMaximumShield), onActor.Shield);
                Assert.Equal(retirementLife - 320, onActor.Monster.Life);
                Assert.Equal(197u, onActor.Monster.Life);

                armourSlot = resourceInventory.Add(PlayerInventories.BodyArmour, 0, 0,
                    resourceArmour with { Mods = originalArmourMods });
                RecalculateResource();
                uint relocatedPlacementId = armourSlot.Id;
                uint bareShield = onActor.Shield, bareLife = onActor.Monster.Life;
                Assert.Equal(bareMaximumShield, bareShield);
                Assert.True(Game.GolemTakeHitLocked(resource, onActor, 0x08000010, neverCrit,
                    FixedHit(DamageType.Chaos, 100), 30.2));
                Assert.Same(onActor, resource.Golems.All[retirementId]);
                Assert.Equal(1, onActor.Monster.Mods.Effects["chaos_damage_does_not_bypass_energy_shield"]);
                Assert.Equal(completeMaximumShield, onActor.MaximumShield);
                Assert.Equal(bareShield - 80, onActor.Shield);
                Assert.Equal(85u, onActor.Shield);
                Assert.Equal(bareLife, onActor.Monster.Life);
                Assert.Equal(197u, onActor.Monster.Life);
                Assert.Equal(relocatedPlacementId, armourSlot.Id);

                SetDelay(50);
                resourceSchedule.At = 40;
                uint fastId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource, resourceSource.Id,
                    resourceDefinition, (100, 100));
                var fast = resource.Golems.All[fastId];
                Assert.Equal(50, fast.Monster.Mods.Effects["energy_shield_delay_-%"]);
                uint fastFull = fast.Shield;
                Assert.True(Game.GolemTakeHitLocked(resource, fast, 0x08000010, neverCrit,
                    FixedHit(DamageType.Physical, 100), 40.5));
                uint fastDamaged = fast.Shield;
                Assert.True(fastDamaged < fastFull);
                double fastDelay = EnergyShield.Delay(name => fast.Monster.Mods.Effects[name]);
                Assert.Equal(4 / 3d, fastDelay, 12);
                resourceSchedule.At = 40.5 + fastDelay;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(fastDamaged, fast.Shield);
                resourceSchedule.At = 40.7;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(fastDamaged, fast.Shield);
                resourceSchedule.At = 40.5 + fastDelay + .2;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(fastDamaged + (uint)Math.Floor(fast.MaximumShield / 3d * .2), fast.Shield);

                SetDelay(100);
                resourceSchedule.At = 50;
                uint slowId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource, resourceSource.Id,
                    resourceDefinition, (100, 100));
                var slow = resource.Golems.All[slowId];
                Assert.Equal(100, slow.Monster.Mods.Effects["energy_shield_delay_-%"]);
                Assert.True(Game.GolemTakeHitLocked(resource, slow, 0x08000010, neverCrit,
                    FixedHit(DamageType.Physical, 100), 50.5));
                uint slowDamaged = slow.Shield;
                Assert.True(slowDamaged < slow.MaximumShield);
                Assert.True(Game.GolemTakeHitLocked(resource, slow, 0x08000010, neverCrit,
                    FixedHit(DamageType.Physical, 0), 50.6));
                resource.PlayerStats[Program.Stats.Key("elemental_golem_immunity_to_elemental_damage")!.Value] = 1;
                Assert.True(Game.GolemTakeHitLocked(resource, slow, 0x08000010, neverCrit,
                    FixedHit(DamageType.Fire, 100), 50.7));
                resource.PlayerStats[Program.Stats.Key("elemental_golem_immunity_to_elemental_damage")!.Value] = 0;
                Assert.Equal(slowDamaged, slow.Shield);
                resourceSchedule.At = 51.5;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(slowDamaged, slow.Shield);
                resourceSchedule.At = 51.7;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(slowDamaged + (uint)Math.Floor(slow.MaximumShield / 3d * .2), slow.Shield);

                resource.IncomingHitRandom = new HighestRandom();
                var missed = new MonsterAttack(0x4000, 1, 100, DamagePacket.Of(DamageType.Physical, 100, 100));
                uint beforeMiss = slow.Shield;
                Assert.False(Game.GolemTakeHitLocked(resource, slow, 0x08000010, neverCrit, missed, 51.8));
                Assert.Equal(beforeMiss, slow.Shield);
                resourceSchedule.At = 52;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(slow.MaximumShield, slow.Shield);

                Assert.True(Game.GolemTakeHitLocked(resource, slow, 0x08000010, neverCrit,
                    FixedHit(DamageType.Physical, 100), 53));
                uint partialShield = slow.Shield;
                Assert.Equal(slow.MaximumShield - 100, partialShield);
                resource.PlayerStats[Program.Stats.Key("minion_additional_spell_block_%")!.Value] = 100;
                resource.IncomingHitRandom = new LowestRandom();
                Assert.False(Game.GolemTakeHitLocked(resource, slow, 0x08000010, neverCrit,
                    FixedHit(DamageType.Fire, 100), 53.5));
                Assert.Equal(partialShield, slow.Shield);
                resource.PlayerStats[Program.Stats.Key("minion_additional_spell_block_%")!.Value] = 0;
                resourceSchedule.At = 54;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(partialShield, slow.Shield);
                resourceSchedule.At = 54.2;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(partialShield + (uint)Math.Floor(slow.MaximumShield / 3d * .2), slow.Shield);

                SetDelay(100);
                resourceSchedule.At = 60;
                uint dotId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource, resourceSource.Id,
                    resourceDefinition, (100, 100));
                var dotGolem = resource.Golems.All[dotId];
                var fireSource = new SpawnedMonster("deterministic igniter", new(100, 100, 1), 100, 8, 1_000_000)
                {
                    Mods = MonsterModSet.None with
                    {
                        Effects = new(new Dictionary<string, int>
                        {
                            ["critical_strike_chance_+%"] = -10_000,
                            ["base_chance_to_ignite_%"] = 100,
                        }),
                    },
                };
                var fireAttack = new MonsterAttack(0x4000, 1, 100, DamagePacket.Of(DamageType.Fire, 20, 20))
                {
                    Spell = true,
                };
                fireSource.Attack = fireAttack;
                resource.IncomingHitRandom = new LowestRandom();
                uint dotLife = dotGolem.Monster.Life;
                Assert.True(Game.GolemTakeHitLocked(resource, dotGolem, 0x08000020, fireSource, fireAttack, 60.1));
                dotLife = dotGolem.Monster.Life;
                Assert.True(dotGolem.Monster.Ignites.Burning);
                Assert.Equal(18, dotGolem.Monster.Ignites.PerSecond(60.1), 12);

                uint fireShield = dotGolem.Shield;
                double shortFireFactor = Game.MonsterDotTaken(dotGolem.Monster, DamageType.Fire, 60.6);
                int shortFireDamage = (int)Math.Floor(18 * shortFireFactor * .5);
                int dotOutputStart = resource.Minions.OutputQueue.Count;
                resourceSchedule.At = 60.6;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(fireShield - (uint)shortFireDamage, dotGolem.Shield);
                Assert.Equal(dotLife, dotGolem.Monster.Life);
                var shortTickOutputs = resource.Minions.OutputQueue.Skip(dotOutputStart).ToArray();
                Assert.DoesNotContain(shortTickOutputs, output => output.Kind == PlayerMinions.OutputKind.Flinch);
                Assert.DoesNotContain(shortTickOutputs, output => output.Log is "golem takes damage" or "golem died");

                resourceSchedule.At = 64.05;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.False(dotGolem.ShieldPool.Recharging);
                uint throughBurn = dotGolem.Shield;
                resourceSchedule.At = 65.1;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(throughBurn, dotGolem.Shield);
                resourceSchedule.At = 65.2;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(throughBurn + (uint)Math.Floor(dotGolem.MaximumShield / 3d * .1), dotGolem.Shield);

                // A real positive fire hit depopulates ES; the following no-hit burn tick then spills exactly once to Life.
                var fireResistance = Game.MonsterHitResistance(dotGolem.Monster, DamageType.Fire, default);
                int cappedFireMaximum = Math.Clamp(fireResistance.Maximum
                    + dotGolem.Monster.Mods.Effects["base_maximum_fire_damage_resistance_%"], 0, 90);
                int IncomingFire(int amount) => (int)Math.Round((int)Math.Round(DamageModel.Mitigate(
                    DamagePacket.Of(DamageType.Fire, amount, amount),
                    _ => fireResistance with { Maximum = cappedFireMaximum })[DamageType.Fire].Min)
                    * dotGolem.Monster.Mods.Effects.DamageTaken(spell: true));
                int low = 1, high = int.MaxValue;
                while (low < high)
                {
                    int middle = low + (high - low) / 2;
                    if ((uint)IncomingFire(middle) > dotGolem.Shield) high = middle;
                    else low = middle + 1;
                }
                fireAttack = fireAttack with
                {
                    Damage = DamagePacket.Of(DamageType.Fire, low, low),
                };
                fireSource.Attack = fireAttack;
                resourceSchedule.At = 66;
                Assert.True(Game.GolemTakeHitLocked(resource, dotGolem, 0x08000020, fireSource, fireAttack, 66));
                Assert.Equal(0u, dotGolem.Shield);
                dotLife = dotGolem.Monster.Life;
                Assert.True(dotLife > 0);
                double spillFactor = Game.MonsterDotTaken(dotGolem.Monster, DamageType.Fire, 66.1);
                int expectedSpill = (int)Math.Floor(dotGolem.Monster.Ignites.PerSecond(66.1) * spillFactor * .1);
                resourceSchedule.At = 66.1;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(dotLife - (uint)expectedSpill, dotGolem.Monster.Life);
                Assert.Equal(0u, dotGolem.Shield);

                // A later real hit keeps its later recharge interruption when a coarse tick processes an earlier burn expiry.
                dotGolem.Monster.Ignites.Clear();
                dotGolem.Monster.Ignites.Buff = null;
                resourceSchedule.At = 68.5;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                dotGolem.ShieldPool.SetMaximum(dotGolem.MaximumShield, refill: true);
                var crossingHit = fireAttack with { Damage = DamagePacket.Of(DamageType.Physical, 100, 100) };
                Assert.True(Game.GolemTakeHitLocked(resource, dotGolem, 0x08000020, fireSource, crossingHit, 68.5));
                Assert.True(dotGolem.Monster.Ignites.Add(68.5, 10, .5));
                Assert.True(Game.GolemTakeHitLocked(resource, dotGolem, 0x08000020, fireSource, crossingHit, 69.25));
                resourceSchedule.At = 69.5;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.False(dotGolem.Monster.Ignites.Burning);
                Assert.False(dotGolem.ShieldPool.Recharging);
                uint crossingDamage = dotGolem.Shield;
                resourceSchedule.At = 70.25;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(crossingDamage, dotGolem.Shield);
                resourceSchedule.At = 70.5;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(crossingDamage + (uint)Math.Floor(dotGolem.MaximumShield / 3d * .25), dotGolem.Shield);

                // Real catalogue rows supply hostile bleed/poison; the owned monster owns their strongest/stacking sets.
                var rhoa = Game.Monsters.Find("Metadata/Monsters/Rhoas/Rhoa")!;
                var poisonMod = Program.Items.Mods.ById("MonsterModApplyPoison")!;
                var bleedMod = Program.Items.Mods.ById("MonsterModApplyBleed")!;
                Assert.Equal(MonsterModCatalog.Domain, poisonMod.Domain);
                Assert.Equal(MonsterModCatalog.Domain, bleedMod.Domain);
                Assert.Contains(poisonMod.GenerationType, new[] { MonsterModCatalog.Prefix, MonsterModCatalog.Suffix });
                Assert.Contains(bleedMod.GenerationType, new[] { MonsterModCatalog.Prefix, MonsterModCatalog.Suffix });
                var hostileMods = Game.MonsterModRolls.Value.Roll(rhoa, 80, MonsterRarity.Magic,
                    new Random(504), alone: true, count: 2, forced: [poisonMod, bleedMod]);
                Assert.Equal(1, hostileMods.Effects["global_poison_on_hit"]);
                Assert.Equal(1, hostileMods.Effects["global_bleed_on_hit"]);

                resourceSchedule.At = 80;
                uint hostileId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource, resourceSource.Id,
                    resourceDefinition, (100, 100));
                var hostileTarget = resource.Golems.All[hostileId];
                var victimMods = hostileTarget.Monster.Mods;
                var hostileSource = new SpawnedMonster("Rhoa ailment source", new(100, 100, 1), 100, 8, 1_000_000)
                {
                    Mods = hostileMods,
                };
                var hostileAttack = new MonsterAttack(0x4000, 1, 100,
                    DamagePacket.Of(DamageType.Physical, 200, 200)) { Spell = true };
                hostileSource.Attack = hostileAttack;
                var incomingBefore = resource.IncomingAilments.Snapshot(80);
                uint playerLifeBefore = resource.PlayerLife;
                resource.IncomingHitRandom = new LowestRandom();
                Assert.True(Game.GolemTakeHitLocked(resource, hostileTarget, 0x08000021, hostileSource,
                    hostileAttack, 80));
                Assert.True(hostileTarget.Monster.Bleeds.Burning);
                Assert.True(hostileTarget.Monster.Poisons.Poisoned);
                Assert.Equal(incomingBefore, resource.IncomingAilments.Snapshot(80));
                Assert.Equal(playerLifeBefore, resource.PlayerLife);
                Assert.Equal(20, Game.GolemPools.Calculate(Game.Monsters, hostileTarget.Monster.Variety!,
                    hostileTarget.Monster.Level, hostileTarget, hostileTarget.Player, hostileTarget.Monster.Mods.Effects)
                    .ChaosResistance);
                Assert.Equal(.8, Game.MonsterDotTaken(hostileTarget.Monster, DamageType.Chaos, 80), 12);

                hostileTarget.Monster.Bleeds.Clear();
                hostileTarget.Monster.Bleeds.Buff = null;
                double poisonDps = hostileTarget.Monster.Poisons.TotalDps(80);
                Assert.Equal(60, poisonDps, 12);
                uint poisonedShield = hostileTarget.Shield;
                uint poisonedLife = hostileTarget.Monster.Life;
                resourceSchedule.At = 80.2;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(1, hostileTarget.Monster.Mods.Effects["chaos_damage_does_not_bypass_energy_shield"]);
                Assert.Equal(poisonedLife, hostileTarget.Monster.Life);
                Assert.Equal(poisonedShield - 9, hostileTarget.Shield);

                resourceInventory.Change(PlayerInventories.BodyArmour, armourSlot.Id,
                    item => item with { Mods = item.Mods.Where(mod => mod.Hash != noBypass.Hash).ToArray() });
                RecalculateResource();
                poisonedShield = hostileTarget.Shield;
                poisonedLife = hostileTarget.Monster.Life;
                resourceSchedule.At = 80.4;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(0, hostileTarget.Monster.Mods.Effects["chaos_damage_does_not_bypass_energy_shield"]);
                Assert.Equal(poisonedShield, hostileTarget.Shield);
                Assert.Equal(poisonedLife - 10, hostileTarget.Monster.Life);

                resourceInventory.Change(PlayerInventories.BodyArmour, armourSlot.Id,
                    item => item with { Mods = originalArmourMods });
                RecalculateResource();
                poisonedShield = hostileTarget.Shield;
                poisonedLife = hostileTarget.Monster.Life;
                resourceSchedule.At = 80.6;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(1, hostileTarget.Monster.Mods.Effects["chaos_damage_does_not_bypass_energy_shield"]);
                Assert.Equal(poisonedLife, hostileTarget.Monster.Life);
                Assert.Equal(poisonedShield - 9, hostileTarget.Shield);

                hostileTarget.Monster.Poisons.Clear();
                hostileTarget.Monster.Poisons.Buff = null;
                Assert.True(hostileTarget.Monster.Poisons.Add(81, 200, 1));
                Assert.True(hostileTarget.Monster.Poisons.Add(81, 301, 1));
                Assert.Equal(501, hostileTarget.Monster.Poisons.TotalDps(81), 12);
                poisonedShield = hostileTarget.Shield;
                poisonedLife = hostileTarget.Monster.Life;
                resourceSchedule.At = 81.25;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(poisonedLife, hostileTarget.Monster.Life);
                Assert.Equal(poisonedShield - 100, hostileTarget.Shield);

                hostileTarget.Monster.Poisons.Clear();
                hostileTarget.Monster.Poisons.Buff = null;
                uint armourKey = Program.Stats.Key("minion_physical_damage_reduction_rating")!.Value;
                resource.PlayerStats[armourKey] = 1_000_000;
                RecalculateResource();
                hostileTarget.Monster.Walk.Order(hostileTarget.Monster.Walk.Position(81.5).X,
                    hostileTarget.Monster.Walk.Position(81.5).Y, 81.5);
                Assert.True(hostileTarget.Monster.Bleeds.Add(81.5, 100, 1));
                Assert.True(hostileTarget.Monster.Bleeds.Add(81.5, 201, 1));
                Assert.False(hostileTarget.Monster.Walk.Moving(81.75));
                Assert.Equal(1, Game.BleedTaken(hostileTarget.Monster, 81.75), 12);
                uint bleedShield = hostileTarget.Shield;
                uint bleedLife = hostileTarget.Monster.Life;
                resourceSchedule.At = 81.75;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.True(hostileTarget.Monster.Mods.Effects["physical_damage_reduction_rating"] >= 1_000_000);
                Assert.Equal(bleedLife, hostileTarget.Monster.Life);
                Assert.Equal(bleedShield - 50, hostileTarget.Shield);
                resource.PlayerStats.Remove(armourKey);
                RecalculateResource();

                // Bleed uses the shared self-duration contract, not ignite's reduction; poison applies its base-self
                // reduction and the shared contract once. The custom rows are labelled diagnostics.
                Assert.Equal(1, IncomingDamagingAilments.SelfDuration(null, bleed: true));
                Assert.Equal(2, IncomingDamagingAilments.SelfDuration(
                    name => name == "combined_bleeding_duration_on_self_+%" ? 100 : 0, bleed: true));
                var durationSource = hostileMods with
                {
                    Effects = hostileMods.Effects.Plus(new MonsterModEffects(new Dictionary<string, int>
                    {
                        ["base_bleed_duration_+%"] = 25,
                        ["base_poison_duration_+%"] = 25,
                    })),
                };
                hostileSource.Mods = durationSource;
                hostileTarget.Monster.Ignites.Clear();
                hostileTarget.Monster.Bleeds.Clear();
                hostileTarget.Monster.Poisons.Clear();
                hostileTarget.Monster.Mods = victimMods with
                {
                    Effects = victimMods.Effects.Plus(new MonsterModEffects(new Dictionary<string, int>
                    {
                        ["base_self_ignite_duration_-%"] = 100,
                        ["base_self_poison_duration_-%"] = 50,
                    })),
                };
                hostileTarget.Monster.Life = hostileTarget.Monster.MaxLife;
                hostileTarget.ShieldPool.SetMaximum(hostileTarget.MaximumShield, refill: true);
                Game.GolemHostileAilmentsLocked(resource, hostileTarget, 0x08000021, hostileSource,
                    DamagePacket.Of(DamageType.Physical, 200, 200), critical: false, attack: false, 81.875);
                Assert.Equal(6.25, hostileTarget.Monster.Bleeds.Until - 81.875, 12);
                Assert.Equal(1.25, hostileTarget.Monster.Poisons.Until - 81.875, 12);

                hostileTarget.Monster.Bleeds.Clear();
                hostileTarget.Monster.Poisons.Clear();
                hostileTarget.Monster.Mods = victimMods with
                {
                    Effects = victimMods.Effects.Plus(new MonsterModEffects(new Dictionary<string, int>
                    {
                        ["base_self_poison_duration_-%"] = 50,
                        ["combined_bleeding_duration_on_self_+%"] = 100,
                        ["self_bleed_duration_+%_final"] = 50,
                        ["combined_poison_duration_on_self_+%"] = 100,
                        ["self_poison_duration_+%_final"] = 50,
                    })),
                };
                hostileTarget.Monster.Life = hostileTarget.Monster.MaxLife;
                hostileTarget.ShieldPool.SetMaximum(hostileTarget.MaximumShield, refill: true);
                Game.GolemHostileAilmentsLocked(resource, hostileTarget, 0x08000021, hostileSource,
                    DamagePacket.Of(DamageType.Physical, 200, 200), critical: false, attack: false, 82);
                Assert.Equal(18.75, hostileTarget.Monster.Bleeds.Until - 82, 12);
                Assert.Equal(3.75, hostileTarget.Monster.Poisons.Until - 82, 12);
                hostileTarget.Monster.Bleeds.Clear();
                hostileTarget.Monster.Poisons.Clear();
                hostileSource.Mods = hostileMods;

                // Prepared victim cannot/immune/avoid rows reject both real hostile ailment sources.
                hostileTarget.Monster.Ignites.Clear();
                hostileTarget.Monster.Bleeds.Clear();
                hostileTarget.Monster.Poisons.Clear();
                hostileTarget.Monster.Ignites.Buff = null;
                hostileTarget.Monster.Bleeds.Buff = null;
                hostileTarget.Monster.Poisons.Buff = null;
                hostileTarget.LastStep = 82.1;
                resourceSchedule.At = 82.1;
                Game.GolemStepLocked("test", wire.Stream, wire.Ciphers, resource, hostileTarget, 82.1);
                hostileTarget.Monster.Mods = victimMods with
                {
                    Effects = victimMods.Effects.Plus(new MonsterModEffects(new Dictionary<string, int>
                    {
                        ["cannot_be_poisoned"] = 1,
                        ["immune_to_bleeding"] = 1,
                    })),
                };
                hostileTarget.Monster.Life = hostileTarget.Monster.MaxLife;
                hostileTarget.ShieldPool.SetMaximum(hostileTarget.MaximumShield, refill: true);
                resourceSchedule.At = 82.125;
                Game.GolemHostileAilmentsLocked(resource, hostileTarget, 0x08000021, hostileSource,
                    DamagePacket.Of(DamageType.Physical, 200, 200), critical: false, attack: true, 82.125);
                Assert.False(hostileTarget.Monster.Bleeds.Burning);
                Assert.False(hostileTarget.Monster.Poisons.Poisoned);
                Assert.Equal(1, hostileTarget.Monster.Mods.Effects["cannot_be_poisoned"]);
                Assert.Equal(1, hostileTarget.Monster.Mods.Effects["immune_to_bleeding"]);

                hostileTarget.Monster.Mods = victimMods with
                {
                    Effects = victimMods.Effects.Plus(new MonsterModEffects(new Dictionary<string, int>
                    {
                        ["avoid_poison_%"] = 100,
                        ["avoid_bleed_%"] = 100,
                    })),
                };
                resource.IncomingHitRandom = new LowestRandom();
                resourceSchedule.At = 82.25;
                Game.GolemHostileAilmentsLocked(resource, hostileTarget, 0x08000021, hostileSource,
                    DamagePacket.Of(DamageType.Physical, 200, 200), critical: false, attack: true, 82.25);
                Assert.False(hostileTarget.Monster.Bleeds.Burning);
                Assert.False(hostileTarget.Monster.Poisons.Poisoned);
                hostileTarget.Monster.Mods = victimMods;

                // Attack-only source rows follow the current contact, not a different cached source ability.
                hostileSource.Mods = MonsterModSet.None with
                {
                    Effects = new(new Dictionary<string, int>
                    {
                        ["bleed_on_hit_with_attacks_%"] = 100,
                        ["base_attacks_poison"] = 1,
                    }),
                };
                hostileSource.Attack = hostileAttack with { Spell = false };
                hostileTarget.Monster.Ignites.Clear();
                hostileTarget.Monster.Bleeds.Clear();
                hostileTarget.Monster.Poisons.Clear();
                hostileTarget.Monster.Life = hostileTarget.Monster.MaxLife;
                hostileTarget.ShieldPool.SetMaximum(hostileTarget.MaximumShield, refill: true);
                resource.IncomingHitRandom = new LowestRandom();
                Assert.True(Game.GolemTakeHitLocked(resource, hostileTarget, 0x08000021, hostileSource,
                    hostileAttack with { Spell = true }, 82.375));
                Assert.False(hostileTarget.Monster.Bleeds.Burning);
                Assert.False(hostileTarget.Monster.Poisons.Poisoned);

                hostileSource.Attack = hostileAttack with { Spell = true };
                hostileTarget.Monster.Life = hostileTarget.Monster.MaxLife;
                hostileTarget.ShieldPool.SetMaximum(hostileTarget.MaximumShield, refill: true);
                Assert.True(Game.GolemTakeHitLocked(resource, hostileTarget, 0x08000021, hostileSource,
                    hostileAttack with { Spell = false }, 82.5));
                Assert.True(hostileTarget.Monster.Bleeds.Burning);
                Assert.True(hostileTarget.Monster.Poisons.Poisoned);
                hostileTarget.Monster.Bleeds.Clear();
                hostileTarget.Monster.Poisons.Clear();
                hostileSource.Mods = hostileMods;

                // Actual accepted hit routes admit both status paths. The deterministic igniter is the labelled
                // diagnostic source; the hostile physical contact uses the real catalogue poison/bleed rows.
                var transferFireAttack = fireAttack with
                {
                    Damage = DamagePacket.Of(DamageType.Fire, 20, 20), Spell = true,
                };
                var transferHostileAttack = hostileAttack with
                {
                    Damage = DamagePacket.Of(DamageType.Physical, 20, 20), Spell = true,
                };
                fireSource.Attack = transferFireAttack;
                hostileSource.Mods = hostileMods;
                hostileSource.Attack = transferHostileAttack;
                resource.IncomingHitRandom = new LowestRandom();
                Assert.True(Game.GolemTakeHitLocked(resource, hostileTarget, 0x08000021, fireSource,
                    transferFireAttack, 82.75));
                Assert.True(Game.GolemTakeHitLocked(resource, hostileTarget, 0x08000021, hostileSource,
                    transferHostileAttack, 82.75));
                var alwaysCrit = Program.Items.Mods.ById("MonsterModAlwaysCrit")!;
                Assert.Equal(MonsterModCatalog.Domain, alwaysCrit.Domain);
                var elementalMods = Game.MonsterModRolls.Value.Roll(rhoa, 80, MonsterRarity.Magic,
                    new Random(504), alone: true, count: 1, forced: [alwaysCrit]);
                Assert.Equal(1, elementalMods.Effects["always_crit"]);
                var elementalSource = new SpawnedMonster("catalogue always-critical elemental source",
                    new(100, 100, 1), 100, 8, 1_000_000) { Mods = elementalMods };
                int elementalAmount = (int)Math.Clamp(
                    ((long)hostileTarget.Monster.Life + hostileTarget.Shield) / 10, 1, 100);
                var elementalAttack = new MonsterAttack(0x4000, 1, 100,
                    DamagePacket.Of(DamageType.Cold, elementalAmount, elementalAmount)
                        .Add(DamagePacket.Of(DamageType.Lightning, elementalAmount, elementalAmount)))
                { Spell = true };
                elementalSource.Attack = elementalAttack;
                Assert.True(Game.GolemTakeHitLocked(resource, hostileTarget, 0x08000022, elementalSource,
                    elementalAttack, 82.75));
                Assert.True(hostileTarget.Monster.Life > 0);
                Assert.True(hostileTarget.Monster.Ailments.Magnitude(
                    MonsterElementalAilments.Kind.Chill, 82.75) > 0);
                Assert.True(hostileTarget.Monster.Ailments.Magnitude(
                    MonsterElementalAilments.Kind.Freeze, 82.75) > 0);
                Assert.True(hostileTarget.Monster.Ailments.Magnitude(
                    MonsterElementalAilments.Kind.Shock, 82.75) > 0);
                double chillRemaining = hostileTarget.Monster.Ailments.Remaining(
                    MonsterElementalAilments.Kind.Chill, 82.75);
                double freezeRemaining = hostileTarget.Monster.Ailments.Remaining(
                    MonsterElementalAilments.Kind.Freeze, 82.75);
                double shockRemaining = hostileTarget.Monster.Ailments.Remaining(
                    MonsterElementalAilments.Kind.Shock, 82.75);
                double chillMagnitude = hostileTarget.Monster.Ailments.Magnitude(
                    MonsterElementalAilments.Kind.Chill, 82.75);
                double freezeMagnitude = hostileTarget.Monster.Ailments.Magnitude(
                    MonsterElementalAilments.Kind.Freeze, 82.75);
                double shockMagnitude = hostileTarget.Monster.Ailments.Magnitude(
                    MonsterElementalAilments.Kind.Shock, 82.75);
                Assert.True(hostileTarget.Monster.Ignites.Burning);
                Assert.True(hostileTarget.Monster.Bleeds.Burning);
                Assert.True(hostileTarget.Monster.Poisons.Poisoned);
                uint movedLife = hostileTarget.Monster.Life;
                uint movedShield = hostileTarget.Shield;
                var movedPool = hostileTarget.ShieldPool.Capture(82.75);
                uint oldGolemId = hostileTarget.Id;
                double igniteUntil = hostileTarget.Monster.Ignites.Until;
                double bleedUntil = hostileTarget.Monster.Bleeds.Until;
                double poisonUntil = hostileTarget.Monster.Poisons.Until;
                double movedIgniteDps = hostileTarget.Monster.Ignites.PerSecond(82.75);
                double movedBleedDps = hostileTarget.Monster.Bleeds.PerSecond(82.75);
                double movedPoisonDps = hostileTarget.Monster.Poisons.TotalDps(82.75);
                int movedPoisonStacks = hostileTarget.Monster.Poisons.Stacks(82.75);
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id, item => item with
                {
                    Sockets = item.Sockets.Select(socket => socket.Gem?.TypeHash == resourceGem.TypeHash
                        ? socket with { Gem = resourceGem with { GemExperience = levelTwenty } } : socket).ToArray(),
                });
                RecalculateResource();
                resource.Golems.RefreshAt = 0;
                resourceSchedule.At = 82.75;
                int rosterBeforeRescale = resource.Golems.All.Count;
                var rescaledGolemPackets = await CaptureResource(
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, 82.75));
                int rosterAfterRescale = resource.Golems.All.Count;
                Assert.Equal(rosterBeforeRescale, rosterAfterRescale);
                var rescaledGolem = Assert.Single(resource.Golems.All.Values, golem =>
                    golem.Id != oldGolemId
                    && golem.Monster.Ignites.Burning && golem.Monster.Ignites.BuffSource == 0x08000021
                    && golem.Monster.Ignites.Until == igniteUntil
                    && golem.Monster.Bleeds.Burning && golem.Monster.Bleeds.BuffSource == 0x08000021
                    && golem.Monster.Bleeds.Until == bleedUntil
                    && golem.Monster.Poisons.Poisoned && golem.Monster.Poisons.BuffSource == 0x08000021
                    && golem.Monster.Poisons.Until == poisonUntil
                    && golem.Monster.Ailments.Remaining(MonsterElementalAilments.Kind.Chill, 82.75) == chillRemaining
                    && golem.Monster.Ailments.Remaining(MonsterElementalAilments.Kind.Freeze, 82.75) == freezeRemaining
                    && golem.Monster.Ailments.Remaining(MonsterElementalAilments.Kind.Shock, 82.75) == shockRemaining);
                Assert.NotSame(hostileTarget, rescaledGolem);
                Assert.Same(rescaledGolem, resource.Golems.All[rescaledGolem.Id]);
                Assert.DoesNotContain(oldGolemId, resource.Golems.All.Keys);
                Assert.True(rescaledGolem.GemLevel > 1);
                Assert.Equal(movedLife, rescaledGolem.Monster.Life);
                Assert.Equal(movedShield, rescaledGolem.Shield);
                Assert.Equal(movedPool.InterruptedSecondsAgo,
                    rescaledGolem.ShieldPool.Capture(82.75).InterruptedSecondsAgo);
                Assert.Equal(chillRemaining, rescaledGolem.Monster.Ailments.Remaining(
                    MonsterElementalAilments.Kind.Chill, 82.75));
                Assert.Equal(freezeRemaining, rescaledGolem.Monster.Ailments.Remaining(
                    MonsterElementalAilments.Kind.Freeze, 82.75));
                Assert.Equal(shockRemaining, rescaledGolem.Monster.Ailments.Remaining(
                    MonsterElementalAilments.Kind.Shock, 82.75));
                Assert.Equal(chillMagnitude, rescaledGolem.Monster.Ailments.Magnitude(
                    MonsterElementalAilments.Kind.Chill, 82.75));
                Assert.Equal(freezeMagnitude, rescaledGolem.Monster.Ailments.Magnitude(
                    MonsterElementalAilments.Kind.Freeze, 82.75));
                Assert.Equal(shockMagnitude, rescaledGolem.Monster.Ailments.Magnitude(
                    MonsterElementalAilments.Kind.Shock, 82.75));
                Assert.Equal(0, rescaledGolem.Monster.Ailments.ActionFactor(82.75));
                Assert.Equal(igniteUntil, rescaledGolem.Monster.Ignites.Until);
                Assert.Equal(bleedUntil, rescaledGolem.Monster.Bleeds.Until);
                Assert.Equal(poisonUntil, rescaledGolem.Monster.Poisons.Until);
                Assert.Equal(movedIgniteDps, rescaledGolem.Monster.Ignites.PerSecond(82.75), 12);
                Assert.Equal(movedBleedDps, rescaledGolem.Monster.Bleeds.PerSecond(82.75), 12);
                Assert.Equal(movedPoisonDps, rescaledGolem.Monster.Poisons.TotalDps(82.75), 12);
                Assert.Equal(movedPoisonStacks, rescaledGolem.Monster.Poisons.Stacks(82.75));
                Assert.Equal(0x08000021u, rescaledGolem.Monster.Ignites.BuffSource);
                Assert.Equal(0x08000021u, rescaledGolem.Monster.Bleeds.BuffSource);
                Assert.Equal(0x08000021u, rescaledGolem.Monster.Poisons.BuffSource);
                byte[] currentIgniteBuff = InstanceEntities.AddBuff(rescaledGolem.Id,
                    rescaledGolem.Monster.Ignites.Buff!.Value, Game.IgnitedBuffRow, 0x08000021,
                    stacks: 1, u68: BitConverter.SingleToUInt32Bits((float)(igniteUntil - 82.75)));
                byte[] currentBleedBuff = InstanceEntities.AddBuff(rescaledGolem.Id,
                    rescaledGolem.Monster.Bleeds.Buff!.Value, LabyrinthTrials.BleedingBuffRow!.Value,
                    0x08000021, stacks: 1, u68: BitConverter.SingleToUInt32Bits((float)(bleedUntil - 82.75)));
                byte[] currentPoisonBuff = InstanceEntities.AddBuff(rescaledGolem.Id,
                    rescaledGolem.Monster.Poisons.Buff!.Value, Game.PoisonBuffRow, 0x08000021,
                    stacks: (ushort)movedPoisonStacks,
                    u68: BitConverter.SingleToUInt32Bits((float)(poisonUntil - 82.75)));
                Assert.Contains(rescaledGolemPackets, packet => packet.SequenceEqual(currentIgniteBuff));
                Assert.Contains(rescaledGolemPackets, packet => packet.SequenceEqual(currentBleedBuff));
                Assert.Contains(rescaledGolemPackets, packet => packet.SequenceEqual(currentPoisonBuff));
                Assert.DoesNotContain(rescaledGolemPackets, packet => packet.Length >= 6
                    && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(2)) == oldGolemId
                    && (packet[0], packet[1]) is (0x01, 0xc1));
                var elementalPresentation = Game.MonsterAilmentChangesLocked(resource, 82.75);
                foreach (string kind in new[] { "Chill", "Freeze", "Shock" })
                    Assert.Contains(elementalPresentation, entry => entry.Packet.Length >= 6
                        && BinaryPrimitives.ReadUInt32BigEndian(entry.Packet.AsSpan(2)) == rescaledGolem.Id
                        && entry.Log.Contains(kind, StringComparison.Ordinal));
                hostileTarget = rescaledGolem;
                var taggedB2Actor = rescaledGolem;
                void AssertTaggedB2Current(string boundary)
                {
                    string GolemKind(GolemState.Golem golem) =>
                        $"{golem.Definition.Kind}:0x{golem.Id:x8}";
                    string roster = string.Join(",", resource.Golems.All.Values
                        .OrderBy(GolemKind, StringComparer.Ordinal).Select(GolemKind));
                    string sources = string.Join(",", Game.GemSkillEffects(resource)
                        .Select(grant => $"{Game.SkillsFor(resource).EffectId(grant.Effect)}:0x{grant.Id:x8}"));
                    Assert.True(resource.Golems.All.TryGetValue(taggedB2Actor.Id, out var found),
                        $"{boundary}: tagged B2 actor 0x{taggedB2Actor.Id:x8} is missing; " +
                        $"roster [{roster}], gem sources [{sources}]");
                    var current = found!;
                    Assert.True(ReferenceEquals(taggedB2Actor, current),
                        $"{boundary}: tagged B2 actor 0x{taggedB2Actor.Id:x8} was replaced; " +
                        $"roster [{roster}], gem sources [{sources}]");
                    Assert.True(resourceSource.Id == current.Source,
                        $"{boundary}: tagged B2 actor source changed; roster [{roster}], gem sources [{sources}]");
                    Assert.True(current.GemLevel == 20,
                        $"{boundary}: tagged B2 actor gem level changed; roster [{roster}], gem sources [{sources}]");
                    Assert.True(ReferenceEquals(current.Monster, resource.Monsters[current.Id]),
                        $"{boundary}: tagged B2 actor lost its registered monster; roster [{roster}]");
                    Assert.True(resource.Minions.Owns(current.Id, current.Monster),
                        $"{boundary}: tagged B2 actor lost ownership; roster [{roster}], gem sources [{sources}]");
                    Assert.Contains(Game.GemSkillEffects(resource), grant => grant.Id == resourceSource.Id);
                    Assert.Equal(20, Game.SupportedSkill(resource, resourceSource.Id).Level);
                }
                AssertTaggedB2Current("immediately after actual B2 replacement");
                hostileTarget.Monster.Ignites.Clear();
                hostileTarget.Monster.Ignites.Buff = null;
                hostileTarget.Monster.Bleeds.Clear();
                hostileTarget.Monster.Bleeds.Buff = null;
                hostileTarget.Monster.Poisons.Clear();
                hostileTarget.Monster.Poisons.Buff = null;
                hostileTarget.Monster.Ailments.MoveTo(new MonsterElementalAilments());

                resource.IncomingHitRandom = new HighestRandom();
                hostileAttack = hostileAttack with { Spell = false };
                hostileSource.Attack = hostileAttack;
                resourceSchedule.At = 82.625;
                Assert.False(Game.GolemTakeHitLocked(resource, hostileTarget, 0x08000021, hostileSource,
                    hostileAttack, 82.625));
                Assert.False(hostileTarget.Monster.Bleeds.Burning);
                Assert.False(hostileTarget.Monster.Poisons.Poisoned);
                hostileAttack = hostileAttack with { Spell = true };
                hostileSource.Attack = hostileAttack;
                uint spellBlock = Program.Stats.Key("minion_additional_spell_block_%")!.Value;
                resource.PlayerStats[spellBlock] = 100;
                resource.IncomingHitRandom = new LowestRandom();
                resourceSchedule.At = 82.75;
                Assert.False(Game.GolemTakeHitLocked(resource, hostileTarget, 0x08000021, hostileSource,
                    hostileAttack, 82.75));
                Assert.False(hostileTarget.Monster.Bleeds.Burning);
                Assert.False(hostileTarget.Monster.Poisons.Poisoned);
                resource.PlayerStats.Remove(spellBlock);

                // Prepared family-identity diagnostics: these exercise the owned DoT owner's family guard, not a
                // native reservation lifecycle or an all-family admission proof.
                foreach (var (kind, index) in new[]
                         {
                             ("Agony", 0), ("SkitterCold", 1), ("SkitterLightning", 2),
                         })
                {
                    uint familyId = 0x09000100u + (uint)index;
                    Assert.DoesNotContain(familyId, resource.Golems.All.Keys);
                    Assert.DoesNotContain(familyId, resource.Reservations.Fighters.Keys);
                    Assert.DoesNotContain(familyId, resource.Summons.Fighters.Keys);
                    Assert.DoesNotContain(familyId, resource.Monsters.Keys);
                    var familyMonster = new SpawnedMonster($"prepared {kind} DoT immunity",
                        new(100, 100, 1), 80, 8, 10_000);
                    var family = new GolemState.Golem(familyId, familyMonster,
                        new GolemCatalog.Definition(0, $"diagnostic-{kind}", kind, "", 0, [], false, false, false), 0)
                    {
                        Player = new(Game.CharacterPlayerStats(resource)),
                        LastStep = 82.875,
                    };
                    resource.Monsters[familyId] = familyMonster;
                    resource.Reservations.Fighters[familyId] = family;
                    family.ShieldPool.SetMaximum(500, refill: true);
                    family.ShieldPool.Damage(100, bypass: false, now: 82.875);
                    uint familyShield = family.Shield;
                    uint familyLife = familyMonster.Life;
                    Assert.True(familyMonster.Ignites.Add(82.875, 1_000, .125));
                    Assert.True(familyMonster.Bleeds.Add(82.875, 1_000, .125));
                    Assert.True(familyMonster.Poisons.Add(82.875, 1_000, .125));
                    Game.GolemDotsLocked(resource, family, 82.875, 83);
                    Assert.Equal(familyShield, family.Shield);
                    Assert.Equal(familyLife, familyMonster.Life);
                    Assert.False(family.ShieldPool.Recharging);
                    Assert.True(familyMonster.Ignites.Idle);
                    Assert.True(familyMonster.Bleeds.Idle);
                    Assert.True(familyMonster.Poisons.Idle);
                    resource.Monsters.Remove(familyId);
                    resource.Reservations.Fighters.Remove(familyId);
                }

                AssertTaggedB2Current("before independent Zombie setup");

                // Real persistent Raise Zombie: fresh full cast, JSON round-trip, far-origin restore, source cap changes,
                // legacy zero-ES arrival, dead records, and steady gem-level rescale all cross the production paths.
                var zombieResourceGem = ItemCatalog.Create(
                    Program.Items.Find("Metadata/Items/Gems/SkillGemRaiseZombie")!);
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id,
                    item => item with { Sockets = [.. item.Sockets, new(SocketColour.White, zombieResourceGem)] });
                RecalculateResource();
                var zombieResourceSource = Assert.Single(Game.GemSkillEffects(resource),
                    grant => Game.SkillsFor(resource).EffectId(grant.Effect) == "RaiseZombie");
                Assert.Equal(resourceSource.Id, Assert.Single(Game.GemSkillEffects(resource),
                    grant => Game.SkillsFor(resource).EffectId(grant.Effect) == "SummonFlameGolem").Id);
                Assert.Equal(20, Game.SupportedSkill(resource, resourceSource.Id).Level);
                Assert.Equal(1, Game.SupportedSkill(resource, zombieResourceSource.Id).Level);
                AssertTaggedB2Current("after original Zombie socket setup");
                var zombieResourceDefinition = Game.SummonData.Find("RaiseZombie")!;
                const uint zombieCorpseId = 0x2200c0de;
                resource.Monsters[zombieCorpseId] = new SpawnedMonster("real zombie corpse", new(100, 100, 0), 70, 8, 0)
                {
                    Hostile = true, Dead = true, Life = 0, Variety = Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie"),
                    Level = 70,
                };
                resource.Summons.Restored = true;
                resourceSchedule.At = 200;
                var freshZombiePackets = await CaptureResource(() => Game.SummonFighter("test", wire.Stream,
                    wire.Ciphers, resource, zombieResourceSource.Id, zombieResourceDefinition, (100, 100), zombieCorpseId));
                var freshZombie = Assert.Single(resource.Summons.Fighters).Value;
                Assert.NotEqual(0u, freshZombie.Id);
                Assert.Equal(freshZombie.Monster.MaxLife, freshZombie.Monster.Life);
                Assert.Equal(freshZombie.MaximumShield, freshZombie.Shield);
                Assert.True(freshZombie.MaximumShield > 0);
                AssertCreateResources(freshZombiePackets, freshZombie.Id, freshZombie.Monster.Life,
                    freshZombie.Shield, freshZombie.Monster.MaxLife, freshZombie.MaximumShield);

                freshZombie.Monster.Life = freshZombie.Monster.MaxLife - 7;
                freshZombie.ShieldPool.Damage(3, bypass: false, now: 200.5);
                freshZombie.Regeneration = .375;
                resourceSchedule.At = 200.75;
                Game.SaveSummons(resource);
                var summonJson = CharacterSaves.ModuleData(resourceCharacter, "Game.SummonedMinions")!;
                var summonSaved = Assert.Single(JsonSerializer.Deserialize<SummonedMinions.Saved[]>(summonJson)!,
                    entry => entry.Effect == "RaiseZombie");
                Assert.NotNull(summonSaved.Resources);
                Assert.Equal(freshZombie.Monster.Life, summonSaved.Resources!.Life);
                Assert.Equal(.375, summonSaved.Resources.LifeRegeneration);
                Assert.Equal(freshZombie.Shield, summonSaved.Resources.EnergyShield.Current);
                Assert.Equal(.25, summonSaved.Resources.EnergyShield.InterruptedSecondsAgo);
                uint steadyLife = summonSaved.Resources.Life;
                uint steadyShield = summonSaved.Resources.EnergyShield.Current;
                string steadyVariety = freshZombie.Monster.Variety!.Id;
                int steadyLevel = freshZombie.Monster.Level;

                int zombieLevelTwenty = Program.Items.Gem(Program.Items.ByHash(zombieResourceGem.TypeHash)!)!.ExperienceFor(20);
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id, item => item with
                {
                    Sockets = item.Sockets.Select(socket => socket.Gem?.TypeHash == zombieResourceGem.TypeHash
                        ? socket with { Gem = zombieResourceGem with { GemExperience = zombieLevelTwenty } } : socket).ToArray(),
                });
                Assert.Equal(20, Game.SupportedSkill(resource, resourceSource.Id).Level);
                Assert.Equal(20, Game.SupportedSkill(resource, zombieResourceSource.Id).Level);
                AssertTaggedB2Current("after Zombie source reached level 20");
                resourceSchedule.At = 201;
                Task<uint> preparedZombie = null!;
                var zombieRaceGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(zombieRaceGate.Wait(0));
                EnergyShield.Snapshot zombieRaceSnapshot = default;
                try
                {
                    preparedZombie = Game.SummonFighter("test", wire.Stream, wire.Ciphers, resource,
                        zombieResourceSource.Id, zombieResourceDefinition, (100, 100), restoredVariety: steadyVariety,
                        replacingOld: freshZombie);
                    Assert.False(preparedZombie.IsCompleted, "the actual zombie replacement did not reach the held create gate");
                    uint zombieShieldBeforeHit = freshZombie.Shield;
                    uint zombieLifeBeforeHit = freshZombie.Monster.Life;
                    Assert.True(Game.GolemTakeHitLocked(resource, freshZombie, 0x09000033, neverCrit,
                        FixedHit(DamageType.Physical, 1), 201));
                    Assert.True(freshZombie.Monster.Life < zombieLifeBeforeHit || freshZombie.Shield < zombieShieldBeforeHit);
                    zombieRaceSnapshot = freshZombie.ShieldPool.Capture(201);
                }
                finally { zombieRaceGate.Release(); }
                Assert.Equal(0u, await preparedZombie);
                Assert.Same(freshZombie, resource.Summons.Fighters[freshZombie.Id]);
                steadyLife = freshZombie.Monster.Life;
                steadyShield = freshZombie.Shield;
                var steadyPackets = await CaptureResource(
                    () => Game.SummonsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At));
                var rescaledZombie = Assert.Single(resource.Summons.Fighters).Value;
                Assert.NotEqual(freshZombie.Id, rescaledZombie.Id);
                Assert.True(rescaledZombie.Monster.Level > steadyLevel);
                Assert.True(rescaledZombie.GemLevel > 1);
                Assert.Equal(steadyVariety, rescaledZombie.Monster.Variety!.Id);
                Assert.Equal(double.PositiveInfinity, resource.Summons.Until[rescaledZombie.Id]);
                Assert.Equal(Math.Min(steadyLife, rescaledZombie.Monster.MaxLife), rescaledZombie.Monster.Life);
                Assert.Equal(Math.Min(steadyShield, rescaledZombie.MaximumShield), rescaledZombie.Shield);
                Assert.Equal(.375, rescaledZombie.Regeneration);
                Assert.Equal(zombieRaceSnapshot.InterruptedSecondsAgo,
                    rescaledZombie.ShieldPool.Capture(201).InterruptedSecondsAgo);
                AssertCreateResources(steadyPackets, rescaledZombie.Id, rescaledZombie.Monster.Life,
                    rescaledZombie.Shield, rescaledZombie.Monster.MaxLife, rescaledZombie.MaximumShield);

                // The same current-state move is exercised through the real SummonsTick rescale path, after actual
                // accepted hit routes admit both the diagnostic ignite and the catalogue bleed/poison rows.
                fireSource.Attack = transferFireAttack;
                hostileSource.Mods = hostileMods;
                hostileSource.Attack = transferHostileAttack;
                resource.IncomingHitRandom = new LowestRandom();
                Assert.True(Game.GolemTakeHitLocked(resource, rescaledZombie, 0x08000021, fireSource,
                    transferFireAttack, 201));
                Assert.True(Game.GolemTakeHitLocked(resource, rescaledZombie, 0x08000021, hostileSource,
                    transferHostileAttack, 201));
                Assert.True(rescaledZombie.Monster.Ignites.Burning);
                Assert.True(rescaledZombie.Monster.Bleeds.Burning);
                Assert.True(rescaledZombie.Monster.Poisons.Poisoned);
                double summonIgniteUntil = rescaledZombie.Monster.Ignites.Until;
                double summonBleedUntil = rescaledZombie.Monster.Bleeds.Until;
                double summonPoisonUntil = rescaledZombie.Monster.Poisons.Until;
                int summonPoisonStacks = rescaledZombie.Monster.Poisons.Stacks(201);
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id,
                    item => item with
                    {
                        Sockets = item.Sockets.Select(socket => socket.Gem?.TypeHash == zombieResourceGem.TypeHash
                            ? socket with { Gem = zombieResourceGem } : socket).ToArray(),
                    });
                resource.Summons.RefreshAt = 0;
                resourceSchedule.At = 201.125;
                var summonRescalePackets = await CaptureResource(
                    () => Game.SummonsTick("test", wire.Stream, wire.Ciphers, resource, 201.125));
                Assert.Equal(resourceSource.Id, Assert.Single(Game.GemSkillEffects(resource),
                    grant => Game.SkillsFor(resource).EffectId(grant.Effect) == "SummonFlameGolem").Id);
                Assert.Equal(20, Game.SupportedSkill(resource, resourceSource.Id).Level);
                Assert.Equal(1, Game.SupportedSkill(resource, zombieResourceSource.Id).Level);
                AssertTaggedB2Current("after Zombie transfer");
                var transferredZombie = Assert.Single(resource.Summons.Fighters).Value;
                Assert.NotSame(rescaledZombie, transferredZombie);
                Assert.Same(transferredZombie, resource.Summons.Fighters[transferredZombie.Id]);
                Assert.True(transferredZombie.GemLevel > 0);
                uint transferredLife = transferredZombie.Monster.Life;
                uint transferredShield = transferredZombie.Shield;
                Assert.Equal(summonIgniteUntil, transferredZombie.Monster.Ignites.Until);
                Assert.Equal(summonBleedUntil, transferredZombie.Monster.Bleeds.Until);
                Assert.Equal(summonPoisonUntil, transferredZombie.Monster.Poisons.Until);
                Assert.Equal(summonPoisonStacks, transferredZombie.Monster.Poisons.Stacks(201.125));
                Assert.Equal(0x08000021u, transferredZombie.Monster.Ignites.BuffSource);
                Assert.Equal(0x08000021u, transferredZombie.Monster.Bleeds.BuffSource);
                Assert.Equal(0x08000021u, transferredZombie.Monster.Poisons.BuffSource);
                var summonBuffPackets = await CaptureResource(
                    () => Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, resource));
                Assert.Contains(summonBuffPackets, packet => packet.SequenceEqual(InstanceEntities.AddBuff(
                    transferredZombie.Id, transferredZombie.Monster.Ignites.Buff!.Value, Game.IgnitedBuffRow,
                    0x08000021, stacks: 1,
                    u68: BitConverter.SingleToUInt32Bits((float)(summonIgniteUntil - 201.125)))));
                Assert.Contains(summonBuffPackets, packet => packet.SequenceEqual(InstanceEntities.AddBuff(
                    transferredZombie.Id, transferredZombie.Monster.Bleeds.Buff!.Value,
                    LabyrinthTrials.BleedingBuffRow!.Value, 0x08000021,
                    stacks: 1,
                    u68: BitConverter.SingleToUInt32Bits((float)(summonBleedUntil - 201.125)))));
                Assert.Contains(summonBuffPackets, packet => packet.SequenceEqual(InstanceEntities.AddBuff(
                    transferredZombie.Id, transferredZombie.Monster.Poisons.Buff!.Value, Game.PoisonBuffRow,
                    0x08000021, stacks: (ushort)summonPoisonStacks,
                    u68: BitConverter.SingleToUInt32Bits((float)(summonPoisonUntil - 201.125)))));
                Assert.DoesNotContain(summonBuffPackets, packet => packet.Length >= 6
                    && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(2)) == rescaledZombie.Id
                    && (packet[0], packet[1]) is (0x01, 0xc1));
                AssertCreateResources(summonRescalePackets, transferredZombie.Id, transferredZombie.Monster.Life,
                    transferredZombie.Shield, transferredZombie.Monster.MaxLife, transferredZombie.MaximumShield);
                rescaledZombie = transferredZombie;

                // Clear the moved-status fixture, then restore the actual socketed gem to level 20 and cross the
                // real rescale path so the existing high/low cap controls still start from a genuine high actor.
                rescaledZombie.Monster.Ignites.Clear();
                rescaledZombie.Monster.Ignites.Buff = null;
                rescaledZombie.Monster.Bleeds.Clear();
                rescaledZombie.Monster.Bleeds.Buff = null;
                rescaledZombie.Monster.Poisons.Clear();
                rescaledZombie.Monster.Poisons.Buff = null;
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id, item => item with
                {
                    Sockets = item.Sockets.Select(socket => socket.Gem?.TypeHash == zombieResourceGem.TypeHash
                        ? socket with { Gem = zombieResourceGem with { GemExperience = zombieLevelTwenty } } : socket).ToArray(),
                });
                resource.Summons.RefreshAt = 0;
                resourceSchedule.At = 201.25;
                var restoredHighPackets = await CaptureResource(
                    () => Game.SummonsTick("test", wire.Stream, wire.Ciphers, resource, 201.25));
                var restoredHighZombie = Assert.Single(resource.Summons.Fighters).Value;
                Assert.NotSame(transferredZombie, restoredHighZombie);
                Assert.True(restoredHighZombie.Monster.Level > transferredZombie.Monster.Level);
                Assert.True(restoredHighZombie.GemLevel > transferredZombie.GemLevel);
                Assert.Equal(transferredLife, restoredHighZombie.Monster.Life);
                Assert.Equal(transferredShield, restoredHighZombie.Shield);
                AssertCreateResources(restoredHighPackets, restoredHighZombie.Id, restoredHighZombie.Monster.Life,
                    restoredHighZombie.Shield, restoredHighZombie.Monster.MaxLife, restoredHighZombie.MaximumShield);
                rescaledZombie = restoredHighZombie;
                Assert.Equal(20, Game.SupportedSkill(resource, zombieResourceSource.Id).Level);
                AssertTaggedB2Current("before spirit control");

                rescaledZombie.Monster.Life = rescaledZombie.Monster.MaxLife;
                rescaledZombie.ShieldPool.SetMaximum(rescaledZombie.MaximumShield, refill: true);
                resourceSchedule.At = 201.5;
                Game.SaveSummons(resource);
                uint highLife = rescaledZombie.Monster.Life;
                uint highShield = rescaledZombie.Shield;
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id, item => item with
                {
                    Sockets = item.Sockets.Select(socket => socket.Gem?.TypeHash == zombieResourceGem.TypeHash
                        ? socket with { Gem = zombieResourceGem } : socket).ToArray(),
                });
                var lowerClient = NewResourceClient("integer-resource-zombie-lower", 7000);
                var lowerPackets = await CaptureResource(
                    () => Game.SummonsTick("test", wire.Stream, wire.Ciphers, lowerClient, 7000));
                var lowerZombie = Assert.Single(lowerClient.Summons.Fighters).Value;
                Assert.True(highLife > lowerZombie.Monster.MaxLife);
                Assert.True(highShield > lowerZombie.MaximumShield);
                Assert.Equal(lowerZombie.Monster.MaxLife, lowerZombie.Monster.Life);
                Assert.Equal(lowerZombie.MaximumShield, lowerZombie.Shield);
                AssertCreateResources(lowerPackets, lowerZombie.Id, lowerZombie.Monster.Life,
                    lowerZombie.Shield, lowerZombie.Monster.MaxLife, lowerZombie.MaximumShield);

                lowerZombie.Monster.Life = lowerZombie.Monster.MaxLife - 5;
                lowerZombie.ShieldPool.Damage(2, bypass: false, now: 7000.5);
                resourceSchedule.At = 7000.75;
                Game.SaveSummons(lowerClient);
                uint increasedLife = lowerZombie.Monster.Life;
                uint increasedShield = lowerZombie.Shield;
                var increasedJson = CharacterSaves.ModuleData(resourceCharacter, "Game.SummonedMinions")!;
                var increasedSaved = Assert.Single(JsonSerializer.Deserialize<SummonedMinions.Saved[]>(increasedJson)!,
                    entry => entry.Effect == "RaiseZombie");
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id, item => item with
                {
                    Sockets = item.Sockets.Select(socket => socket.Gem?.TypeHash == zombieResourceGem.TypeHash
                        ? socket with { Gem = zombieResourceGem with { GemExperience = zombieLevelTwenty } } : socket).ToArray(),
                });
                var higherClient = NewResourceClient("integer-resource-zombie-higher", 9000);
                var higherPackets = await CaptureResource(
                    () => Game.SummonsTick("test", wire.Stream, wire.Ciphers, higherClient, 9000));
                var higherZombie = Assert.Single(higherClient.Summons.Fighters).Value;
                Assert.True(higherZombie.Monster.MaxLife > lowerZombie.Monster.MaxLife);
                Assert.True(higherZombie.MaximumShield > lowerZombie.MaximumShield);
                Assert.Equal(increasedLife, higherZombie.Monster.Life);
                Assert.Equal(increasedShield, higherZombie.Shield);
                Assert.Equal(.25, higherZombie.ShieldPool.Capture(9000).InterruptedSecondsAgo);
                AssertCreateResources(higherPackets, higherZombie.Id, higherZombie.Monster.Life,
                    higherZombie.Shield, higherZombie.Monster.MaxLife, higherZombie.MaximumShield);
                await Game.SummonsTick("test", wire.Stream, wire.Ciphers, higherClient, 9000);
                Assert.Equal(increasedLife, higherZombie.Monster.Life);
                Assert.Equal(increasedShield, higherZombie.Shield);
                Game.SaveSummons(higherClient);
                var repeatedSummon = Assert.Single(JsonSerializer.Deserialize<SummonedMinions.Saved[]>(
                    CharacterSaves.ModuleData(resourceCharacter, "Game.SummonedMinions")!)!, entry => entry.Effect == "RaiseZombie");
                Assert.Equal(increasedSaved.Resources!.Life, repeatedSummon.Resources!.Life);
                Assert.Equal(increasedSaved.Resources.EnergyShield.Current, repeatedSummon.Resources.EnergyShield.Current);
                Assert.Equal(increasedSaved.Resources.EnergyShield.Fraction, repeatedSummon.Resources.EnergyShield.Fraction);

                string legacySummonJson = JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        Effect = "RaiseZombie", Source = zombieResourceSource.Id, Variety = steadyVariety, Life = .42,
                    },
                });
                Assert.DoesNotContain("\"Resources\"", legacySummonJson);
                CharacterSaves.ChangeModuleData(resourceCharacter, "Game.SummonedMinions", _ => legacySummonJson);
                resourceSchedule.At = 10_000;
                var legacySummon = NewResourceClient("integer-resource-zombie-legacy", resourceSchedule.At);
                var legacySummonPackets = await CaptureResource(
                    () => Game.SummonsTick("test", wire.Stream, wire.Ciphers, legacySummon, resourceSchedule.At));
                var legacyZombie = Assert.Single(legacySummon.Summons.Fighters).Value;
                Assert.Equal((uint)(legacyZombie.Monster.MaxLife * .42), legacyZombie.Monster.Life);
                Assert.Equal(0u, legacyZombie.Shield);
                Assert.Equal(0d, legacyZombie.ShieldPool.Capture(resourceSchedule.At).InterruptedSecondsAgo);
                AssertCreateResources(legacySummonPackets, legacyZombie.Id, legacyZombie.Monster.Life, 0,
                    legacyZombie.Monster.MaxLife, legacyZombie.MaximumShield);
                double legacySummonDelay = EnergyShield.Delay(name => legacyZombie.Monster.Mods.Effects[name]);
                resourceSchedule.At = 10_000 + legacySummonDelay - .01;
                await Game.SummonsTick("test", wire.Stream, wire.Ciphers, legacySummon, resourceSchedule.At);
                Assert.Equal(0u, legacyZombie.Shield);
                resourceSchedule.At = 10_000 + legacySummonDelay + .1;
                await Game.SummonsTick("test", wire.Stream, wire.Ciphers, legacySummon, resourceSchedule.At);
                Assert.InRange(legacyZombie.Shield, 1u, legacyZombie.MaximumShield - 1);

                var spiritGem = ItemCatalog.Create(
                    Program.Items.Find("Metadata/Items/Gems/SkillGemSummonRagingSpirit")!);
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id,
                    item => item with { Sockets = [.. item.Sockets, new ItemSocket(SocketColour.White, spiritGem)] });
                RecalculateResource();
                var spiritSource = Assert.Single(Game.GemSkillEffects(resource),
                    grant => Game.SkillsFor(resource).EffectId(grant.Effect) == "SummonRagingSpirit");
                var spiritDefinition = Game.SummonData.Find("SummonRagingSpirit")!;
                resourceSchedule.At = 11_000;
                uint spiritId = await Game.SummonFighter("test", wire.Stream, wire.Ciphers, resource,
                    spiritSource.Id, spiritDefinition, (100, 100));
                var spirit = resource.Summons.Fighters[spiritId];
                double spiritUntil = resource.Summons.Until[spiritId];
                Assert.True(double.IsFinite(spiritUntil));
                AssertTaggedB2Current("before spirit expiry");
                resourceSchedule.At = spiritUntil - .25;
                var spiritGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(spiritGate.Wait(0));
                var preparedSpirit = Game.SummonFighter("test", wire.Stream, wire.Ciphers, resource,
                    spiritSource.Id, spiritDefinition, (100, 100), replacingOld: spirit);
                Assert.False(preparedSpirit.IsCompleted);
                resourceSchedule.At = spiritUntil + .25;
                spiritGate.Release();
                Assert.Equal(0u, await preparedSpirit);
                Assert.Same(spirit, resource.Summons.Fighters[spiritId]);
                Assert.Equal(spiritUntil, resource.Summons.Until[spiritId]);
                await Game.SummonsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.DoesNotContain(spiritId, resource.Summons.Fighters.Keys);
                AssertTaggedB2Current("after spirit expiry");
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id,
                    item => item with
                    {
                        Sockets = item.Sockets.Where(socket => socket.Gem?.TypeHash != spiritGem.TypeHash).ToArray(),
                    });
                RecalculateResource();

                CharacterSaves.ChangeModuleData(resourceCharacter, "Game.SummonedMinions", _ => JsonSerializer.Serialize(new[]
                {
                    new SummonedMinions.Saved("RaiseZombie", zombieResourceSource.Id, steadyVariety, 0),
                    new SummonedMinions.Saved("RaiseZombie", zombieResourceSource.Id, steadyVariety, 0,
                        Resources: new GolemState.MinionResourceSnapshot(0, 0, new(0, 0, 0))),
                }));
                resourceSchedule.At = 12_000;
                var deadSummon = NewResourceClient("integer-resource-zombie-dead", resourceSchedule.At);
                await Game.SummonsTick("test", wire.Stream, wire.Ciphers, deadSummon, resourceSchedule.At);
                Assert.Empty(deadSummon.Summons.Fighters);
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id, item => item with
                {
                    Sockets = [new(SocketColour.White, resourceGem with { GemExperience = levelTwenty })],
                });
                RecalculateResource();
                AssertTaggedB2Current("before generic 83 and tail 84 controls");
                var highTailSource = Assert.Single(Game.GemSkillEffects(resource),
                    grant => Game.SkillsFor(resource).EffectId(grant.Effect) == "SummonFlameGolem");
                Assert.Equal(resourceSource.Id, highTailSource.Id);
                Assert.Equal(20, Game.SupportedSkill(resource, resourceSource.Id).Level);
                var highTailActor = Assert.Single(resource.Golems.All.Values,
                    golem => golem.Monster.Poisons.BuffSource == 0x08000021);
                Assert.Equal(resourceDefinition.Effect, highTailActor.Definition.Effect);
                Assert.Equal(resourceSource.Id, highTailActor.Source);
                Assert.Equal(20, highTailActor.GemLevel);
                hostileTarget = highTailActor;
                hostileId = highTailActor.Id;
                Assert.Same(highTailActor.Monster, resource.Monsters[hostileId]);
                Assert.True(resource.Minions.Owns(hostileId, highTailActor.Monster));
                resourceSchedule.At = 83;

                // Generic hostile loops skip the exact owned actor and cannot consume its sets or manufacture rewards.
                // The production projectile session supplies the same explicit binary clock to those loops.
                var genericProjectiles = new global::Game.ProjectileSession("test", wire.Stream, wire.Ciphers, resource);
                resource.ProjectileRuntime = genericProjectiles;
                try
                {
                    genericProjectiles.AdvanceTo(global::GameServer.Core.Projectiles.ProjectileRuntime.Time(83));
                    hostileTarget.Monster.Ignites.Clear();
                    hostileTarget.Monster.Bleeds.Clear();
                    hostileTarget.Monster.Poisons.Clear();
                    hostileTarget.Monster.Ignites.Buff = null;
                    hostileTarget.Monster.Bleeds.Buff = null;
                    hostileTarget.Monster.Poisons.Buff = null;
                    hostileTarget.Monster.Ignites.BuffSource = 0;
                    hostileTarget.Monster.Bleeds.BuffSource = 0;
                    hostileTarget.Monster.Poisons.BuffSource = 0;
                    hostileTarget.LastStep = 83;
                    Assert.True(hostileTarget.Monster.Ignites.Add(83, 1000, 2));
                    Assert.True(hostileTarget.Monster.Bleeds.Add(83, 1000, 2));
                    Assert.True(hostileTarget.Monster.Poisons.Add(83, 1000, 2));
                    uint ownedShield = hostileTarget.Shield;
                    uint ownedLife = hostileTarget.Monster.Life;
                    genericProjectiles.AdvanceTo(global::GameServer.Core.Projectiles.ProjectileRuntime.Time(83.125));
                    resourceSchedule.At = 83.125;
                    await Game.BurnIgnited("test", wire.Stream, wire.Ciphers, resource);
                    await Game.BleedMonsters("test", wire.Stream, wire.Ciphers, resource);
                    await Game.TickPoisons("test", wire.Stream, wire.Ciphers, resource);
                    Assert.Equal(ownedShield, hostileTarget.Shield);
                    Assert.Equal(ownedLife, hostileTarget.Monster.Life);
                    genericProjectiles.AdvanceTo(global::GameServer.Core.Projectiles.ProjectileRuntime.Time(83.25));
                    resourceSchedule.At = 83.25;
                    await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                    Assert.True(hostileTarget.Shield < ownedShield || hostileTarget.Monster.Life < ownedLife);

                    uint nonOwnedId = 0x09000022;
                    Assert.DoesNotContain(nonOwnedId, resource.Golems.All.Keys);
                    Assert.DoesNotContain(nonOwnedId, resource.Reservations.Fighters.Keys);
                    Assert.DoesNotContain(nonOwnedId, resource.Summons.Fighters.Keys);
                    Assert.DoesNotContain(nonOwnedId, resource.Monsters.Keys);
                    var nonOwned = new SpawnedMonster("nonowned dot baseline", new(100, 100, 0), 100, 8, 1_000_000)
                    {
                        Variety = rhoa,
                    };
                    resource.Monsters[nonOwnedId] = nonOwned;
                    Assert.True(nonOwned.Ignites.Add(83.25, 1000, 1));
                    Assert.True(nonOwned.Bleeds.Add(83.25, 1000, 1));
                    Assert.True(nonOwned.Poisons.Add(83.25, 1000, 1));
                    uint nonOwnedLife = nonOwned.Life;
                    genericProjectiles.AdvanceTo(global::GameServer.Core.Projectiles.ProjectileRuntime.Time(83.5));
                    resourceSchedule.At = 83.5;
                    await Game.BurnIgnited("test", wire.Stream, wire.Ciphers, resource);
                    await Game.BleedMonsters("test", wire.Stream, wire.Ciphers, resource);
                    await Game.TickPoisons("test", wire.Stream, wire.Ciphers, resource);
                    Assert.True(nonOwned.Life < nonOwnedLife);
                    resource.Monsters.Remove(nonOwnedId);
                }
                finally
                {
                    genericProjectiles.Invalidate();
                    await genericProjectiles.OutputCompleted;
                    resource.ProjectileRuntime = null;
                }

                // A held send gate cannot emit an old actor's queued buff after that exact actor is replaced. The same
                // packet waits normally while the current actor still owns its id.
                resourceSchedule.At = 83.75;
                await Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, resource);
                hostileTarget.Monster.Dead = false;
                hostileTarget.Monster.Life = hostileTarget.Monster.MaxLife;
                hostileTarget.ShieldPool.SetMaximum(hostileTarget.MaximumShield, refill: true);
                hostileTarget.Monster.Poisons.Clear();
                hostileTarget.Monster.Poisons.Buff = null;
                hostileTarget.Monster.Poisons.BuffSource = 0;
                Assert.True(hostileTarget.Monster.Poisons.Add(84, 10, 2));
                Game.GolemDotsLocked(resource, hostileTarget, 84, 84.125);
                Assert.True(hostileTarget.Monster.Poisons.Buff.HasValue);
                ushort queuedPoisonBuff = hostileTarget.Monster.Poisons.Buff.Value;
                byte[] queuedPoisonPacket = InstanceEntities.AddBuff(hostileId, queuedPoisonBuff, Game.PoisonBuffRow,
                    Program.PlayerId, stacks: 1,
                    u68: BitConverter.SingleToUInt32Bits((float)(hostileTarget.Monster.Poisons.Until - 84.125)));
                var queuedPoison = Assert.Single(resource.Minions.OutputQueue,
                    output => output.TargetId == hostileId && ReferenceEquals(output.Target, hostileTarget.Monster)
                        && output.Packet.SequenceEqual(queuedPoisonPacket));
                Assert.NotNull(queuedPoison);

                var replacementMonster = new SpawnedMonster("replacement at the held gate", new(100, 100, 1),
                    100, 8, 1_000_000);
                var staleBuffPackets = await CaptureResource(async () =>
                {
                    var sendGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(sendGate.Wait(0));
                    resource.Monsters[hostileId] = replacementMonster;
                    var publishing = Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, resource);
                    Assert.False(publishing.IsCompleted);
                    sendGate.Release();
                    await publishing;
                    resource.Monsters[hostileId] = hostileTarget.Monster;
                });
                Assert.DoesNotContain(staleBuffPackets, packet => packet.SequenceEqual(queuedPoisonPacket));

                hostileTarget.Monster.Poisons.Clear();
                hostileTarget.Monster.Poisons.BuffSource = 0;
                Game.GolemDotsLocked(resource, hostileTarget, 84.125, 84.25);
                byte[] validRemoval = InstanceEntities.RemoveBuff(hostileId, queuedPoisonBuff);
                Assert.Single(resource.Minions.OutputQueue,
                    output => output.TargetId == hostileId && ReferenceEquals(output.Target, hostileTarget.Monster)
                        && output.Packet.SequenceEqual(validRemoval));
                var validBuffPackets = await CaptureResource(
                    () => Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, resource));
                Assert.Contains(validBuffPackets, packet => packet.SequenceEqual(validRemoval));

                // Existing Life regeneration is applied before owned DoT. A low-Life golem with real Flame regeneration
                // survives this frame, while the positive stationary bleed still prevents ES recharge.
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id,
                    item => item with { Sockets = [new(SocketColour.White, resourceGem)] });
                RecalculateResource();
                var regenerationKey = Program.Stats.Key("minion_life_regeneration_rate_per_minute_%")!.Value;
                resource.PlayerStats.Remove(regenerationKey);
                resource.PlayerStats[armourKey] = 0;
                RecalculateResource();
                resourceSchedule.At = 84.25;
                uint lowRegenerationId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                    resourceSource.Id, resourceDefinition, (100, 100));
                var lowRegeneration = resource.Golems.All[lowRegenerationId];
                Assert.Equal(663u, lowRegeneration.Monster.MaxLife);
                uint golemArmourKey = Program.Stats.Key("golem_physical_damage_reduction_rating")!.Value;
                int ownerArmour = lowRegeneration.Player.GetValueOrDefault(armourKey);
                int innateGolemArmour = lowRegeneration.Player.GetValueOrDefault(golemArmourKey);
                Assert.Equal(0, ownerArmour);
                Assert.Equal(0, innateGolemArmour);
                double lowRegenerationArmour = Math.Max(0, ownerArmour + innateGolemArmour
                    + lowRegeneration.Monster.Mods.Effects["base_physical_damage_reduction_rating"])
                    * Math.Max(0,
                        1 + lowRegeneration.Monster.Mods.Effects["determination_aura_armour_+%_final"] / 100.0);
                Assert.Equal(0, lowRegenerationArmour, 12);
                Assert.Equal(0, lowRegeneration.Monster.Mods.Effects.PhysicalReduction);
                Assert.Equal(0, Game.MonsterHitResistance(lowRegeneration.Monster, DamageType.Physical, default).Value);
                uint lowRegenerationLife = lowRegeneration.Monster.Life;
                uint lowRegenerationShield = lowRegeneration.Shield;
                int lowLifeHit = checked((int)(lowRegenerationLife + lowRegenerationShield - 1));
                Assert.True(Game.GolemTakeHitLocked(resource, lowRegeneration, 0x08000023, neverCrit,
                    FixedHit(DamageType.Physical, lowLifeHit), 84.25));
                Assert.Equal(1u, lowRegeneration.Monster.Life);
                Assert.Equal(0u, lowRegeneration.Shield);

                lowRegeneration.Monster.Walk.Order(lowRegeneration.Monster.Walk.Position(84.25).X,
                    lowRegeneration.Monster.Walk.Position(84.25).Y, 84.25);
                // Labelled diagnostic: a stationary 4 DPS bleed for one second isolates registration order after
                // the real low-Life hit, without a moving multiplier or an ordinary attack's on-hit effects.
                Assert.True(lowRegeneration.Monster.Bleeds.Add(84.25, 4, 1));
                resourceSchedule.At = 84.75;
                Assert.False(lowRegeneration.Monster.Walk.Moving(resourceSchedule.At));
                Assert.Equal(1, Game.BleedTaken(lowRegeneration.Monster, resourceSchedule.At), 12);
                uint expectedRegeneration = (uint)Math.Floor(lowRegeneration.Monster.MaxLife * .01 * .5);
                Assert.Equal(3u, expectedRegeneration);
                int bleedOutputStart = resource.Minions.OutputQueue.Count;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                var lowRegenerationOutputs = resource.Minions.OutputQueue.Skip(bleedOutputStart).ToArray();
                Assert.False(lowRegeneration.Monster.Dead);
                Assert.Equal(1u + expectedRegeneration - 2u, lowRegeneration.Monster.Life);
                Assert.Equal(2u, lowRegeneration.Monster.Life);
                Assert.Equal(0u, lowRegeneration.Shield);
                Assert.False(lowRegeneration.ShieldPool.Recharging);
                Assert.DoesNotContain(lowRegenerationOutputs, output => output.Kind == PlayerMinions.OutputKind.Flinch);
                Assert.DoesNotContain(lowRegenerationOutputs,
                    output => output.Log is "golem takes damage" or "golem died");

                resource.PlayerStats[regenerationKey] = -60;
                resource.PlayerStats.Remove(armourKey);
                RecalculateResource();

                // Fatal stat conversion removes every native owned ailment buff, empties the pools and cannot resurrect.
                resource.PlayerStats.Remove(spellBlock);
                resourceSchedule.At = 85;
                hostileId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                    resourceSource.Id, resourceDefinition, (100, 100));
                hostileTarget = resource.Golems.All[hostileId];
                resource.IncomingHitRandom = new LowestRandom();
                Game.GolemHostileAilmentsLocked(resource, hostileTarget, 0x08000021, fireSource,
                    DamagePacket.Of(DamageType.Fire, 1, 1), critical: false, attack: true, 85);
                Assert.True(hostileTarget.Monster.Ignites.Burning);
                Assert.True(hostileTarget.Monster.Ignites.Buff.HasValue);
                Game.GolemHostileAilmentsLocked(resource, hostileTarget, 0x08000021, hostileSource,
                    DamagePacket.Of(DamageType.Physical, 1, 1), critical: false, attack: true, 85);
                Assert.True(hostileTarget.Monster.Bleeds.Burning);
                Assert.True(hostileTarget.Monster.Poisons.Poisoned);
                Assert.True(hostileTarget.Monster.Bleeds.Buff.HasValue);
                Assert.True(hostileTarget.Monster.Poisons.Buff.HasValue);
                ushort[] dyingBuffs = [hostileTarget.Monster.Ignites.Buff.Value,
                    hostileTarget.Monster.Bleeds.Buff.Value, hostileTarget.Monster.Poisons.Buff.Value];
                Assert.Equal(3, dyingBuffs.Distinct().Count());
                uint fatalChaosResistance = Program.Stats.Key("minion_chaos_resistance_%")!.Value;
                bool hadFatalChaosResistance = resource.PlayerStats.Remove(fatalChaosResistance,
                    out int previousFatalChaosResistance);
                if (hadFatalChaosResistance) _ = Game.CharacterPlayerStats(resource);
                resource.PlayerStats[fatalChaosResistance] = 30;
                var fatalPlayer = Game.CharacterPlayerStats(resource);
                var fatalMods = Game.GolemModifiers(resource, hostileTarget, fatalPlayer);
                Assert.Equal(30, fatalMods.Effects.Resistance(DamageType.Chaos));
                var fatalPools = Game.GolemPools.Calculate(Game.Monsters, hostileTarget.Monster.Variety!,
                    hostileTarget.Monster.Level, hostileTarget, fatalPlayer,
                    fatalMods.Effects);
                Assert.Equal(50, fatalPools.ChaosResistance);
                Assert.Equal(100, fatalPools.ConversionPercent);
                Assert.True(fatalPools.Fatal);
                Assert.Equal(0, fatalPools.MaximumEnergyShield);
                Assert.Equal(0u, fatalPools.MaximumLife);
                resourceSchedule.At = 85.125;
                var deathPackets = await CaptureResource(
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At));
                Assert.True(hostileTarget.Monster.Dead);
                Assert.Equal(0u, hostileTarget.Monster.Life);
                Assert.Equal(0u, hostileTarget.Shield);
                Assert.Equal(0u, hostileTarget.MaximumShield);
                Assert.Equal(1u, hostileTarget.Monster.MaxLife);
                Assert.True(hostileTarget.Monster.Ignites.Idle);
                Assert.True(hostileTarget.Monster.Bleeds.Idle);
                Assert.True(hostileTarget.Monster.Poisons.Idle);
                Assert.Null(hostileTarget.Monster.Ignites.Buff);
                Assert.Null(hostileTarget.Monster.Bleeds.Buff);
                Assert.Null(hostileTarget.Monster.Poisons.Buff);
                Assert.Equal(87.125, hostileTarget.ReviveAt);
                byte[] deathPacket = InstanceProtocol.LifeUpdate(hostileTarget.Id, 0);
                Assert.Single(deathPackets, packet => packet.AsSpan().IndexOf(deathPacket) >= 0);
                foreach (ushort buff in dyingBuffs)
                {
                    byte[] removed = InstanceEntities.RemoveBuff(hostileTarget.Id, buff);
                    Assert.Single(deathPackets, packet => packet.SequenceEqual(removed));
                }

                resourceSchedule.At = 85.25;
                var repeatedDeathPackets = await CaptureResource(
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At));
                Assert.Equal(87.125, hostileTarget.ReviveAt);
                Assert.Equal(0u, hostileTarget.Monster.Life);
                Assert.DoesNotContain(repeatedDeathPackets, packet => packet.AsSpan().IndexOf(deathPacket) >= 0);
                Assert.DoesNotContain(repeatedDeathPackets, packet => dyingBuffs.Any(buff =>
                    packet.SequenceEqual(InstanceEntities.RemoveBuff(hostileTarget.Id, buff))));

                var currentWeapon = resourceInventory.Items(PlayerInventories.Weapon1).Single();
                Assert.Equal(weaponSlot.Id, currentWeapon.Id);
                Assert.Equal(resourceGem, currentWeapon.Item.Sockets.Single().Gem);
                Assert.Equal(armourSlot.Id, resourceInventory.Items(PlayerInventories.BodyArmour).Single().Id);
                resourceInventory.Remove(PlayerInventories.Weapon1, weaponSlot.Id);
                RecalculateResource();
                resource.Golems.All.Remove(hostileId);
                resource.Minions.Remove(hostileId);
                resource.Monsters.Remove(hostileId);
                Assert.DoesNotContain(hostileId, resource.Golems.All.Keys);
                Assert.True(hostileTarget.Monster.Ignites.Add(85.625, 10, 2));
                uint staleShield = hostileTarget.Shield;
                uint staleLife = hostileTarget.Monster.Life;
                Game.GolemStepLocked("test", wire.Stream, wire.Ciphers, resource, hostileTarget, 85.75);
                Assert.Equal(staleShield, hostileTarget.Shield);
                Assert.Equal(staleLife, hostileTarget.Monster.Life);
                if (hadFatalChaosResistance) resource.PlayerStats[fatalChaosResistance] = previousFatalChaosResistance;
                else resource.PlayerStats.Remove(fatalChaosResistance);

                resource.IncomingHitRandom = Random.Shared;
            }
            finally
            {
                global::Game.AreaUpdateContext.Value = previousResourceContext;
                foreach (var client in extraResourceClients) client.Closed = true;
                if (resource is not null)
                {
                    resource.Closed = true;
                    PlayerInventories.Forget(resourceCharacter);
                    Game.InactiveItems.TryRemove(resourceCharacter, out _);
                }
            }
        }
        finally
        {
            conn.Closed = true; PlayerInventories.Forget(conn.Character!);
            lifetime.Stop(ex => throw ex); Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!); TestEnvironment.ReleaseSaves(path);
        }
    }

    sealed class HighestRandom : Random
    {
        public override int Next(int maxValue) => maxValue > 0 ? maxValue - 1 : 0;
        public override int Next(int minValue, int maxValue) => maxValue > minValue ? maxValue - 1 : minValue;
    }

    sealed class LowestRandom : Random
    {
        public override int Next(int maxValue) => 0;
        public override int Next(int minValue, int maxValue) => minValue;
    }

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new(); readonly TcpClient reader;
        readonly Task drain;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();
        public Wire()
        {
            listener.Start(); var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
            drain = Task.Run(async () =>
            {
                try { var buffer = new byte[65536]; while (await reader.GetStream().ReadAsync(buffer) > 0) { } }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            });
        }
        public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Stop(); drain.GetAwaiter().GetResult(); }
    }

    sealed class AreaClockSchedule : IPlayerUpdateSchedule
    {
        public double At;
        public double Now(ClientState conn) => At;
        public Task Wait(int milliseconds, CancellationToken stopping) => Task.CompletedTask;
    }
}
