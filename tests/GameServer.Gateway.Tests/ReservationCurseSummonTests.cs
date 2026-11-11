using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class ReservationCurseSummonTests
{
    [Fact]
    public void LifeReservationIsSeparateCappedAndCursesReplaceWithoutMutatingMods()
    {
        var pools = new CharacterStats.Pools(90, 1200, 800, 2, 0, LifeReservationEfficiencyPct: 100);
        var life = new AuraCatalog.Aura(1, 0, "Vitality", "Vitality", 1, 153, 200, 0, ReservesLife: true);
        var mana = life with { SkillId = 2, ReservesLife = false, ManaFlat = 0, ManaPercent = 5000 };
        Assert.Equal(100, AuraCatalog.RequiredLife(pools, [life, mana]));
        Assert.Equal(400, AuraCatalog.RequiredMana(pools, [life, mana]));
        Assert.Equal(67, AuraCatalog.RequiredLife(pools, [life with { ReservationEfficiencyPct = 100 }]));
        var conn = new ClientState(new(1, "reserve-test", 100, 100, 10, 10, null, []), 1, 1) { Pools = pools, PlayerLife = 1200 };
        conn.Auras[1] = life;
        Assert.Equal(1100u, conn.PlayerUnreservedLife);
        double carry = 0;
        Assert.Equal(1100u, PetrifiedBlood.RecoverLife(conn, 1000, 500, ref carry));
        Assert.Empty(AuraCatalog.Restore(["Vitality"], [life with { ManaFlat = 2400 }], pools));
        var curses = new MonsterCurses();
        var original = new MonsterModEffects(new Dictionary<string, int> { ["base_fire_damage_resistance_%"] = 20 });
        MonsterCurses.Curse Curse(uint caster, string effect, int value, double at) => new(caster, effect, 1, 71, (ushort)at, at, 20, false, false,
            new Dictionary<string, int> { ["base_fire_damage_resistance_%"] = value });
        curses.Put(Curse(1, "Flammability", -36, 1), 1);
        Assert.Equal(-16, curses.Effects(original).Resistance(DamageType.Fire));
        curses.Put(Curse(2, "Flammability", -20, 2), 1);
        Assert.Equal(-16, curses.Effects(original).Resistance(DamageType.Fire));
        curses.Put(Curse(1, "ElementalWeakness", -30, 3), 1);
        Assert.DoesNotContain((1u, "Flammability"), curses.All.Keys);
        Assert.Equal(-30, curses["base_fire_damage_resistance_%"]);
        Assert.Single(curses.All);
        curses.Put(Curse(2, "Flammability", -20, 4), 2);
        Assert.Equal(-50, curses["base_fire_damage_resistance_%"]);
        Assert.Equal(20, original.Resistance(DamageType.Fire));
        curses.Expire(20);
        Assert.Empty(curses.All); Assert.Same(original, curses.Effects(original));
        Assert.Equal(14, CurseCatalog.All.Count); Assert.False(CurseCatalog.Hex("AssassinsMark"));
        var skitter = new GolemState.Golem(1, null!, new(10825, "SummonSkitterbots", "SkitterCold", "", 0, [], false, false, false), 1)
            { Own = Program.Skills.EffectStats(10825, 20).ToDictionary(p => p.Item1, p => p.Item2) };
        var ailments = new MonsterElementalAilments();
        ailments.WithoutDamage(MonsterElementalAilments.Kind.Chill, 1, 0, n => Game.SkitterAilmentStat(skitter, n), _ => 0);
        ailments.WithoutDamage(MonsterElementalAilments.Kind.Shock, 1, 0, n => Game.SkitterAilmentStat(skitter, n), _ => 0);
        Assert.Equal(11, ailments.Magnitude(MonsterElementalAilments.Kind.Chill, 0));
        Assert.Equal(17, ailments.Magnitude(MonsterElementalAilments.Kind.Shock, 0));
    }

    [Fact]
    public async Task SkeletonsUseTheirOwnLimitExpireAndHaveNativeCombatSkills()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-summons-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull; var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        using var wire = new Wire();
        Saves.Open(path); Game.CombatLifetime = lifetime; Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "summon-runtime", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "SummonRuntime", Instance = new(), PlayerLife = 10000, PlayerMana = 10000, PlayerSent = true,
            Experience = CharacterStats.ExperienceFor(100), Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("summon-test", "Standard", false, false), new HashSet<string>(["Game"]))
        };
        conn.Summons.Restored = conn.Golems.Restored = true;
        foreach (string s in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" }) conn.PlayerStats[Program.Stats.Key(s)!.Value] = 1000;
        try
        {
            var catalog = new SummonCatalog(); var monsters = new MonsterCatalog();
            foreach (var definition in catalog.All.Where(d => d.Variety.Length > 0 && d.Family != "Clone"))
            {
                var variety = monsters.Find(definition.Variety);
                Assert.True(variety is { Supported: true }, definition.Id + " " + definition.Variety);
                var effect = Program.Skills.EffectRow(definition.Id);
                Assert.True(effect >= 0);
                var g = new GolemState.Golem(1, null!, new(effect, definition.Id, definition.Family, definition.Variety, 0, [], false, false, false), 1);
                var abilities = Game.GolemAbilities(conn, variety!, 70, g, new Dictionary<uint, int>());
                Assert.NotEmpty(Game.GolemNativeSkills(conn, 70, abilities));
            }
            var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSummonSkeletons")!);
            var weapon = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with { Sockets = [new(SocketColour.White, gem)] };
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, weapon);
            var source = Game.GemSkillEffects(conn).Single(g => Program.Skills.EffectId(g.Effect) == "SummonSkeletons");
            var skeleton = catalog.Find("SummonSkeletons")!;
            int limit = Game.SummonLimit(conn, skeleton, Game.SupportedSkill(conn, source.Id));
            Assert.Equal(5, limit);
            for (int i = 0; i < limit + 2; i++) Assert.NotEqual(0u, await Game.SummonFighter("test", wire.Stream, wire.Ciphers, conn, source.Id, skeleton, (100, 100)));
            Assert.Equal(limit, conn.Summons.Fighters.Count); Assert.Empty(conn.Golems.All);
            Assert.All(conn.Summons.Fighters.Values, g => Assert.Contains(g.Abilities, a => a.Attack.Damage.MaxTotal > 0));
            await Game.SummonsTick("test", wire.Stream, wire.Ciphers, conn, conn.Clock.Elapsed.TotalSeconds + 21);
            Assert.Empty(conn.Summons.Fighters);
            ushort Equip(string effect, params string[] supports)
            {
                Item Gem(string name)
                {
                    var type = Program.Items.FindGem(name)!.Value;
                    var item = ItemCatalog.Create(type.Type) with { GemVariant = type.Variant.Index };
                    return item with { GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(20) };
                }
                var sockets = new[] { effect }.Concat(supports).Select(n => new ItemSocket(SocketColour.White, Gem(n))).ToArray();
                uint host = Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).Single().Id;
                Game.ItemsOf(conn).Change(PlayerInventories.Weapon1, host, item => item with { Sockets = sockets, Links = [(byte)sockets.Length] });
                return Game.GemSkillEffects(conn).First().Id;
            }
            ushort curseSource = Equip("Flammability", "Blasphemy Support");
            int Published(ushort sourceId, string stat) => Game.PlayerSkills(conn).Single(s => s.Id == sourceId).Stats!
                .FirstOrDefault(p => p.Key == Program.Stats.Key(stat)).Value;
            Assert.Equal(1, Published(curseSource, "skill_is_instant"));
            Assert.Equal(42, Published(curseSource, "active_skill_area_of_effect_radius"));
            var victim = new SpawnedMonster("curse-target", new(100, 100, 1), 100, 8, 100000)
                { Hostile = true, Variety = monsters.Find("Metadata/Monsters/Zombies/Zombie"), Level = 70 };
            conn.Monsters[0x800abcd] = victim;
            Assert.True(Game.ApplyCurseLocked(conn, victim, curseSource, 1, true));
            Assert.Equal(-27, victim.Effects["base_fire_damage_resistance_%"]);
            Assert.Equal(0, victim.Mods.Effects["base_fire_damage_resistance_%"]);
            Assert.Single(victim.Curses.All);
            victim.Curses.Clear();

            foreach (var (name, stat) in new[] {
                ("Summon Reaper", "number_of_reapers_allowed"), ("Summon Holy Relic", "number_of_relics_allowed"),
                ("Summon Raging Spirit", "number_of_raging_spirits_allowed"), ("Summon Skeletons", "number_of_skeletons_allowed"),
                ("Raise Zombie", "number_of_zombies_allowed"), ("Raise Spectre", "number_of_spectres_allowed") })
            {
                ushort id = Equip(name);
                var supported = Game.SupportedSkill(conn, id);
                var definition = catalog.Find(Program.Skills.EffectId(supported.Effect))!;
                Assert.True(Published(id, stat) > 0, name);
                Assert.Equal(Game.SummonLimit(conn, definition, supported), Published(id, stat));
            }
            foreach (var (name, mineKind) in new[] { ("Stormblast Mine", true), ("Fire Trap", false) })
            {
                ushort id = Equip(name);
                Assert.Equal(15, Published(id, mineKind ? "number_of_remote_mines_allowed" : "number_of_traps_allowed"));
                Assert.Equal(1, Published(id, mineKind ? "number_of_mines_to_place" : "number_of_traps_to_throw"));
                Assert.Equal(1, Published(id, mineKind ? "skill_is_mined" : "skill_is_trapped"));
            }

            ushort zombieSource = Equip("Raise Zombie");
            Assert.Equal(0u, await Game.SummonFighter("test", wire.Stream, wire.Ciphers, conn, zombieSource, catalog.Find("RaiseZombie")!, (100, 100)));
            victim.Life = 0; victim.Dead = true;
            uint zombie = await Game.SummonFighter("test", wire.Stream, wire.Ciphers, conn, zombieSource, catalog.Find("RaiseZombie")!, (100, 100), 0x800abcd);
            Assert.NotEqual(0u, zombie); Assert.True(victim.CorpseConsumed);
            Assert.Equal(0u, await Game.SummonFighter("test", wire.Stream, wire.Ciphers, conn, zombieSource, catalog.Find("RaiseZombie")!, (100, 100), 0x800abcd));
            var fighter = conn.Summons.Fighters[zombie];
            Assert.True(Game.GolemTakeHitLocked(conn, fighter, 0x800abce, new("attacker", new(100, 100, 1), 100, 8, 1000),
                new(0x4000, 1, 100, DamagePacket.Of(DamageType.Physical, 1000000, 1000000)) { Spell = true }, 2));
            Assert.True(fighter.Monster.Dead); Assert.True(double.IsPositiveInfinity(fighter.ReviveAt));
            await Game.SummonsTick("test", wire.Stream, wire.Ciphers, conn, 3);
            Assert.Empty(conn.Summons.Fighters);

            ushort animated = Equip("Animate Weapon");
            var animatedSource = Assert.Single(Game.GemSkillEffects(conn),
                gem => Game.SkillsFor(conn).EffectId(gem.Effect) == "AnimateWeapon");
            Assert.Equal(animated, animatedSource.Id);
            Assert.Equal(0u, await Game.SummonFighter("test", wire.Stream, wire.Ciphers, conn, animated,
                catalog.Find("AnimateWeapon")!, (100, 100)));
            var dropped = new WorldItemObject(100, 100, ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1")!));
            uint ground = conn.Instance.Ground.Add(dropped);
            uint sword = await Game.SummonFighter("test", wire.Stream, wire.Ciphers, conn, animated, catalog.Find("AnimateWeapon")!, (100, 100), ground);
            Assert.NotEqual(0u, sword); Assert.Null(conn.Instance.Ground.Get(ground));
            var animatedActor = conn.Summons.Fighters[sword];
            Assert.Equal(animatedActor.Monster, conn.Monsters[sword]);
            Assert.True(conn.Minions.Owns(sword, animatedActor.Monster));
            Assert.NotNull(conn.Summons.Fighters[sword].Weapon);
            Assert.Single(conn.Summons.Fighters[sword].Equipment);

            var relicDefinition = catalog.Find("SummonHolyRelic")!;
            var relic = new GolemState.Golem(0x400ab00, new("relic", new(100, 100, 1), 100, 8, 1000),
                new(9793, relicDefinition.Id, relicDefinition.Family, relicDefinition.Variety, 0, [], false, false, false), 1)
                { Own = Program.Skills.EffectStats(9793, 20).ToDictionary(p => p.Item1, p => p.Item2) };
            var nova = new GolemState.Ability(9927, 0x4000, "RelicTriggeredNova", new(0x4000, 1, 30, DamagePacket.Empty),
                Program.Skills.EffectStats(9927, 20).ToDictionary(p => p.Item1, p => p.Item2), 30, .3, 1, .5);
            Game.HolyRelicRegenerationLocked(conn, relic, nova, 5);
            Assert.Equal(9, conn.Summons.HolyRegenerationUntil);
            Assert.Equal(9275, conn.Summons.HolyRegenerationPerMinute);
            var nativeRegen = conn.Minions.OutputQueue.Last(o => o.Log == "Holy Relic regeneration").Packet;
            // 0x1c1 fixed header + duration + empty list. This non-recovery row has no GrantedStats payload.
            Assert.Equal(31, nativeRegen.Length);
            Assert.Equal(new byte[4], nativeRegen[^4..]);

            var mine = new PlacedSkills.Placed(1, 1, 1, (100, 100), true, 0, 5, DamagePacket.Empty, new Dictionary<uint, int>(), 6, 0, false, 0);
            Assert.Equal(6, PlacedSkills.Reserved(pools: conn.Pools, [mine], life: false));
            mine.SkitterTriggered = true;
            conn.Placed.All[1] = mine;
            Assert.Equal(1, Game.CharacterPlayerStats(conn)[Program.Stats.Key("number_of_active_mines")!.Value]);
            var payload = new GrantedSkill(0x4000, 1, 2) { VaalResourcesEnabled = true };
            var unownedPacket = InstanceEntities.PlayerSkillsChange(0x1000, [payload], []);
            var ownedPacket = InstanceEntities.PlayerSkillsChange(0x1000, [payload with { Parent = (0x10203040, 0x1234) }], []);
            Assert.Equal(unownedPacket.Length + 6, ownedPacket.Length);
            Assert.Contains("03403020103412", Convert.ToHexString(ownedPacket));
            var enabled = InstanceEntities.PlayerSkillsChange(0x1000, [], [], canDetonateMines: true);
            Assert.Equal(0xa0, enabled[15]);
            var airborne = mine with { Id = 2, Armed = 4, LandAt = 3, Landed = false };
            var landing = new List<byte>();
            Game.LandPlacedLocked(conn, airborne, 2.9, landing);
            Assert.Empty(landing); Assert.False(airborne.Landed);
            Game.LandPlacedLocked(conn, airborne, 3, landing);
            Assert.True(airborne.Landed); Assert.NotEmpty(landing);
            int landedBytes = landing.Count;
            Game.LandPlacedLocked(conn, airborne, 3.1, landing);
            Assert.Equal(landedBytes, landing.Count);
            Game.DetonateMinesLocked(conn, 4);
            Assert.Equal(4, mine.DetonateAt);

            // An obsolete gone notification for a still-current physical gem is a positive valid-source control.
            await Game.EndSkillEffects("test", wire.Stream, wire.Ciphers, conn, [animated]);
            Assert.Contains(Game.GemSkillEffects(conn), gem => gem.Id == animatedSource.Id);
            Assert.Equal(animatedActor.Monster, conn.Summons.Fighters[sword].Monster);
            Assert.Equal(animatedActor.Monster, conn.Monsters[sword]);
            Assert.True(conn.Minions.Owns(sword, animatedActor.Monster));

            var animatedHost = Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).Single();
            var animatedSocket = new InstanceItems.SocketGem(PlayerInventories.InventoryId(PlayerInventories.Weapon1),
                animatedHost.Id, 0);
            await Game.UnsocketGem("test", wire.Stream, wire.Ciphers, conn, animatedSocket);
            Assert.DoesNotContain(Game.GemSkillEffects(conn),
                gem => Game.SkillsFor(conn).EffectId(gem.Effect) == "AnimateWeapon");
            Assert.DoesNotContain(sword, conn.Summons.Fighters.Keys);
            Assert.DoesNotContain(sword, conn.Monsters.Keys);
            Assert.False(conn.Minions.Owns(sword));
            Assert.Empty(conn.Summons.Fighters);

            var heldAnimated = Assert.Single(Game.ItemsOf(conn).Items(PlayerInventories.Cursor));
            Assert.Empty(Game.ItemsOf(conn).Items(PlayerInventories.MainInventory));
            await Game.PlaceItem("test", wire.Stream, wire.Ciphers, conn,
                new InstanceItems.PlaceItem(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                    0, 0, 0, 3, 0, 0));
            Assert.Empty(Game.ItemsOf(conn).Items(PlayerInventories.Cursor));
            Assert.Equal(heldAnimated.Id,
                Assert.Single(Game.ItemsOf(conn).Items(PlayerInventories.MainInventory)).Id);

            ushort claritySource = Equip("Clarity");
            CharacterSaves.SetAuraActive(conn.Character!, "Clarity", true);
            conn.ClientSkillIds = [claritySource];
            var heldHost = Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).Single();
            var socket = new InstanceItems.SocketGem(PlayerInventories.InventoryId(PlayerInventories.Weapon1), heldHost.Id, 0);
            await Game.UnsocketGem("test", wire.Stream, wire.Ciphers, conn, socket);
            Assert.DoesNotContain("Clarity", CharacterSaves.Load(conn.Character!)!.ActiveAuras ?? []);
            conn.Monsters.Clear();
            var groundTarget = new SpawnedMonster("burn-target", new(100, 100, 1), 100, 8, 10000) { Hostile = true };
            conn.Monsters[0x800abcf] = groundTarget;
            conn.Placed.Grounds.Add(new(10, 1, (100, 100), 20, 1000, 1.75, 0));
            conn.Placed.Grounds.Add(new(11, 1, (100, 100), 20, 500, 1.75, 0));
            Game.PlacedGroundsLocked("test", wire.Stream, wire.Ciphers, conn, 2, []);
            Assert.Equal(8250u, groundTarget.Life);
            Assert.Empty(conn.Placed.Grounds);
            await Game.SocketGem("test", wire.Stream, wire.Ciphers, conn, socket);
            Game.ReconcileOwnAuras(conn, validateSkills: true);
            Assert.DoesNotContain(conn.Auras.Values, a => a.EffectId == "Clarity");
            Assert.DoesNotContain("Clarity", CharacterSaves.Load(conn.Character!)!.ActiveAuras ?? []);

            async Task CatCoreFixture()
            {
                string character = "CatRuntime" + Guid.NewGuid().ToString("N");
                const byte witchRow = 1;
                PlayerInventories.SetClass(character, witchRow);
                CharacterSaves.Write(character, new(new Dictionary<byte, List<CharacterSaves.SavedPlacement>>(),
                    Experience: CharacterStats.ExperienceFor(100)));
                var schedule = new CatClockSchedule { At = 100 };
                var updates = new PlayerUpdatesState(lifetime, schedule);
                var previousContext = Game.AreaUpdateContext.Value;
                Game.AreaUpdateContext.Value = updates;
                using var catWire = new Wire();
                ClientState? catConnection = null;
                ClientState? restoredConnection = null;
                try
                {
                    catConnection = new ClientState(conn.Layout, 0xC470, 1)
                    {
                        Character = character, CharacterId = Guid.NewGuid(), Account = "cat-core-account",
                        League = "Standard", LeagueForm = "Standard", ClassRow = witchRow, Instance = new(),
                        PlayerSent = true, PlayerObjectCreated = true, PlayerUpdatesState = updates,
                        Experience = CharacterStats.ExperienceFor(100), LeagueModule = conn.LeagueModule,
                        Wire = ("cat-core", catWire.Stream, catWire.Ciphers), Pools = new(100, 1, 1, 0, 0),
                    };
                    var catConn = catConnection;
                    Assert.Empty(catConn.PlayerStats);

                    var inventory = Game.ItemsOf(catConn);
                    void Clear(byte row)
                    {
                        foreach (var old in inventory.Items(row)) inventory.Remove(row, old.Id);
                    }
                    Clear(PlayerInventories.Weapon1);
                    Clear(PlayerInventories.Helmet);
                    Clear(PlayerInventories.BodyArmour);
                    Clear(5);
                    Clear(6);
                    Clear(7);

                    Item Gem(string name, int level)
                    {
                        var type = Program.Items.FindGem(name)!.Value;
                        return ItemCatalog.Create(type.Type) with
                        {
                            GemVariant = type.Variant.Index,
                            GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(level),
                        };
                    }
                    var bow = ItemCatalog.Create(Program.Items.Find(
                        "Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with
                    {
                        Sockets = [new(SocketColour.White, Gem("Tornado Shot", 1)),
                            new(SocketColour.White, Gem("Fireball", 1))],
                        Links = [2],
                    };
                    inventory.Add(PlayerInventories.Weapon1, 0, 0, bow);
                    var astramentis = new UniqueCatalog(Program.Items).Create("Astramentis", new Random(504))!;
                    var amulet = inventory.Add(5, 0, 0, astramentis);
                    inventory.Add(6, 0, 0, new UniqueCatalog(Program.Items).Create("Le Heup of All", new Random(504))!);
                    inventory.Add(7, 0, 0, new UniqueCatalog(Program.Items).Create("Le Heup of All", new Random(505))!);
                    catConn.Pools = Game.PoolsFor(catConn, 100);
                    catConn.PlayerLife = catConn.Pools.Life;
                    catConn.PlayerMana = catConn.Pools.Mana;
                    Assert.Empty(catConn.PlayerStats);
                    Assert.Equal(100, catConn.Pools.Level);

                    var fur = new UniqueCatalog(Program.Items).Create("Farrul's Fur", new Random(504))!;
                    var furPlacement = inventory.Add(PlayerInventories.BodyArmour, 0, 0, fur);
                    Game.ReconcileOwnAuras(catConn, validateSkills: true);
                    Assert.DoesNotContain(Game.WornGrantedSkillsOf(catConn),
                        grant => grant.EffectId == "AspectOfTheCat");
                    Assert.False(Game.IsInactive(catConn, furPlacement.Id));
                    inventory.Remove(PlayerInventories.BodyArmour, furPlacement.Id);

                    var bite = new UniqueCatalog(Program.Items).Create("Farrul's Bite", new Random(504))!;
                    var bitePlacement = inventory.Add(PlayerInventories.Helmet, 0, 0, bite);
                    catConn.Pools = Game.PoolsFor(catConn, 100);
                    catConn.PlayerLife = catConn.Pools.Life;
                    catConn.PlayerMana = catConn.Pools.Mana;
                    Game.ReconcileOwnAuras(catConn, validateSkills: true);
                    var catGrant = Assert.Single(Game.WornGrantedSkillsOf(catConn),
                        grant => grant.EffectId == "AspectOfTheCat");
                    Assert.Equal(20, catGrant.Level);
                    var catSource = Assert.Single(Game.GemSkillEffects(catConn), gem =>
                        Game.SkillsFor(catConn).EffectId(gem.Effect) == "AspectOfTheCat");
                    Assert.True(Game.SupportedSkill(catConn, catSource.Id).RequirementsMet);
                    Assert.False(Program.Skills.IsAura(catSource.Effect));
                    Assert.True(Program.Skills.HasType(catSource.Effect, "HasReservation"));
                    Assert.Equal(1036, AuraCatalog.BuffRow("AspectOfTheCat"));
                    Assert.Equal(1037, Game.CatAspectPhaseRow(ReservationState.CatPhase.Stealth));
                    Assert.Equal(1038, Game.CatAspectPhaseRow(ReservationState.CatPhase.Agility));
                    using (var definitions = JsonDocument.Parse(File.ReadAllBytes("data/tables/BuffDefinitions.json")))
                    {
                        Assert.False(definitions.RootElement[1037].GetProperty("IsRecovery").GetBoolean());
                        Assert.False(definitions.RootElement[1038].GetProperty("IsRecovery").GetBoolean());
                        Assert.Equal(new[] { 556, 7521, 12561 },
                            definitions.RootElement[1037].GetProperty("StatsKeys").EnumerateArray()
                                .Select(v => v.GetInt32()).ToArray());
                        Assert.Equal(new[] { 70, 218 },
                            definitions.RootElement[1038].GetProperty("StatsKeys").EnumerateArray()
                                .Select(v => v.GetInt32()).ToArray());
                    }

                    ushort attackSource = Assert.Single(Game.GemSkillEffects(catConn), gem =>
                        Game.SkillsFor(catConn).EffectId(gem.Effect) == "TornadoShot").Id;
                    ushort fireball = Assert.Single(Game.GemSkillEffects(catConn), gem =>
                        Game.SkillsFor(catConn).EffectId(gem.Effect) == "Fireball").Id;
                    var tornado = Game.SupportedSkill(catConn, attackSource);
                    var fireballSkill = Game.SupportedSkill(catConn, fireball);
                    Assert.True(tornado.RequirementsMet);
                    Assert.True(fireballSkill.RequirementsMet);
                    Assert.Null(Game.GemSkillInstanceDisabled(catConn, tornado.Effect, attackSource));
                    Assert.Null(Game.GemSkillInstanceDisabled(catConn, fireballSkill.Effect, fireball));
                    uint biteCritKey = Program.Stats.Key("additional_base_critical_strike_chance")!.Value;
                    int attackChanceOff = Game.SupportedCriticalChance(catConn, attackSource);
                    int castMsOff = Game.CastMs(catConn, Game.SupportedSkill(catConn, fireball).Effect, fireball)!.Value;
                    var catCost = Game.SupportedReservationCost(catConn, catSource.Id)!.Value;
                    Assert.Equal(2500, catCost.ManaPercent);
                    Assert.Equal(0, catCost.LifePercent);

                    Game.CatAspectPhaseStats CurrentCat()
                    {
                        var sources = Game.CharacterStatSources(catConn).Stats;
                        return Game.CatAspectPhaseStatsLocked(catConn, catConn.Pools.AuraEffectPct, name =>
                            Program.Stats.Key(name) is uint key
                                ? catConn.PlayerStats.TryGetValue(key, out int replacement)
                                    ? replacement : sources.GetValueOrDefault(key) : 0);
                    }
                    // These phase values describe the UI stats and server calculations, not the non-recovery buff payload.
                    IReadOnlyList<uint> PhaseValues() => Game.CatAspectPhaseValues(catConn.Reservations.Cat, CurrentCat());
                    byte[] PhasePacket(ushort instance, double remaining) => InstanceEntities.AddBuff(Program.PlayerId,
                        instance, Game.CatAspectPhaseRow(catConn.Reservations.Cat), Program.PlayerId,
                        catConn.Reservations.CatOwner!.Value.Skill,
                        u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, remaining)));
                    static int Occurrences(IEnumerable<byte[]> packets, byte[] needle)
                    {
                        int count = 0;
                        foreach (var packet in packets)
                            for (int at = 0; at <= packet.Length - needle.Length;)
                                if (packet.AsSpan(at, needle.Length).SequenceEqual(needle)) { count++; at += needle.Length; }
                                else at++;
                        return count;
                    }
                    async Task<List<byte[]>> Capture(Func<Task> action)
                    {
                        var sent = new List<byte[]>();
                        var previous = Program.Outgoing;
                        Program.Outgoing = (ciphers, bytes) =>
                        {
                            if (ReferenceEquals(ciphers, catWire.Ciphers)) sent.Add(bytes.ToArray());
                            previous?.Invoke(ciphers, bytes);
                        };
                        try { await action(); }
                        finally { Program.Outgoing = previous; }
                        return sent;
                    }

                    var catState = catConn.Reservations;
                    var initial = await Capture(async () => Assert.True(await Game.ToggleAura("test", catWire.Stream,
                        catWire.Ciphers, catConn, catSource.Id, catSource.Effect, catSource.Name, catCost,
                        catGrant.Level, 0)));
                    var catAura = Assert.Single(catConn.Auras.Values, aura => aura.EffectId == "AspectOfTheCat");
                    Assert.Equal(1036, catAura.BuffRow);
                    Assert.Equal(catAura.Buff, catState.CatOwner!.Value.RootBuff);
                    Assert.Equal(ReservationState.CatPhase.Stealth, catState.Cat);
                    Assert.Equal(100, catState.CatPhaseStartedAt, 3);
                    Assert.Equal(104, catState.CatPhaseDeadline, 3);
                    Assert.Equal(3, PhaseValues().Count);
                    var initialPhasePacket = PhasePacket(catState.CatPhaseBuff,
                        catState.CatPhaseDeadline - schedule.At);
                    Assert.Equal(31, initialPhasePacket.Length);
                    Assert.Equal(new byte[4], initialPhasePacket[^4..]);
                    Assert.Equal(1, Occurrences(initial, initialPhasePacket));
                    Assert.Equal((int)Math.Ceiling(catConn.Pools.Mana * 0.25),
                        AuraCatalog.ReservedMana(catConn.Pools, catConn.Auras.Values));

                    var stealthStats = Game.CharacterPlayerStats(catConn);
                    int Stat(IReadOnlyDictionary<uint, int> stats, string name) =>
                        stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
                    Assert.Equal(1, Stat(stealthStats, "has_cat_aspect"));
                    Assert.Equal(1, Stat(stealthStats, "has_cats_stealth"));
                    Assert.Equal(0, Stat(stealthStats, "has_cats_agility"));
                    Assert.True(Stat(stealthStats, "critical_strike_chance_+%") >= 100);
                    Assert.Equal(15, PlayerDamageTaken.AvoidPct(DamageType.Physical,
                        name => Stat(stealthStats, name)));
                    Assert.True(Game.CharacterStatSources(catConn).Stats.GetValueOrDefault(biteCritKey) >= 100);
                    Assert.True(Game.SupportedCriticalChance(catConn, attackSource) > attackChanceOff);
                    var flags = Game.ItemConditionFlags(catConn, catConn.Clock.Elapsed.TotalSeconds);
                    Assert.True(flags["has_cat_aspect"]);
                    Assert.True(flags["has_cats_stealth"]);
                    Assert.False(flags["has_cats_agility"]);
                    Assert.Equal(StatConditions.State.Holds,
                        StatConditions.Of("movement_speed_+%_while_you_have_cats_stealth",
                            new StatConditions.Subject(Flags: flags)));
                    Assert.Equal(StatConditions.State.Holds,
                        StatConditions.Of("_while_affected_by_cats_stealth", new StatConditions.Subject(Flags: flags)));
                    Assert.Equal(StatConditions.State.Fails,
                        StatConditions.Of("_while_affected_by_cats_agility", new StatConditions.Subject(Flags: flags)));
                    int attackMsStealth = (int)Math.Round(Game.SupportedAttackSeconds(catConn, attackSource) * 1000);

                    var monster = new SpawnedMonster("cat-hit", new(100, 100, 1), 100, 8, 1_000_000);
                    var spell = new MonsterAttack(0x4000, 1, 100, DamagePacket.Of(DamageType.Physical, 100, 100))
                        { Spell = true };
                    Game.MonsterHitOutcome? Hit(int seed)
                    {
                        catConn.IncomingHitRandom = new Random(seed);
                        catConn.PlayerLife = catConn.Pools.Life;
                        catConn.PlayerMana = catConn.Pools.Mana;
                        lock (catConn.Monsters)
                            return Game.CommitMonsterHitLocked("cat-hit", catConn, 0xC470, monster, spell,
                                catConn.Clock.Elapsed.TotalSeconds,
                                (100, 100), (100, 100), false);
                    }
                    int avoidSeed = Enumerable.Range(0, 1000).First(seed => Hit(seed) is { RawDamage: 0 });
                    Assert.Equal(0, Hit(avoidSeed)!.RawDamage);

                    var preMagnitude = CurrentCat();
                    ushort oldPhaseInstance = catState.CatPhaseBuff;
                    double phaseStart = catState.CatPhaseStartedAt;
                    double phaseDeadline = catState.CatPhaseDeadline;
                    var amuletMod = Program.Items.Mods.ById("TalismanEnchantCatAspectBuffEffect")!;
                    inventory.Change(5, amulet.Id, item => item with
                    {
                        Mods = [.. item.Mods, new ItemMod(ModSection.Enchant, amuletMod.Hash, [100])],
                    });
                    catConn.Pools = Game.PoolsFor(catConn, catConn.Pools.Level);
                    var republished = await Capture(() => Game.SendOwnAuraChanges("cat-core", catWire.Stream,
                        catWire.Ciphers, catConn));
                    var postMagnitude = CurrentCat();
                    Assert.Equal(preMagnitude.CriticalStrikeChancePct * 2, postMagnitude.CriticalStrikeChancePct);
                    Assert.Equal(ReservationState.CatPhase.Stealth, catState.Cat);
                    Assert.Equal(phaseStart, catState.CatPhaseStartedAt);
                    Assert.Equal(phaseDeadline, catState.CatPhaseDeadline);
                    Assert.NotEqual(oldPhaseInstance, catState.CatPhaseBuff);
                    Assert.Equal(1, Occurrences(republished, InstanceEntities.RemoveBuff(Program.PlayerId, oldPhaseInstance)));
                    Assert.Equal(1, Occurrences(republished, PhasePacket(catState.CatPhaseBuff,
                        catState.CatPhaseDeadline - schedule.At)));

                    double stealthDeadline = catState.CatPhaseDeadline;
                    ushort stealthInstance = catState.CatPhaseBuff;
                    schedule.At = stealthDeadline - .001;
                    var beforeStealthBoundary = await Capture(() => Game.ReservationSkillsTick("test",
                        catWire.Stream, catWire.Ciphers, catConn, schedule.At));
                    Assert.Equal(ReservationState.CatPhase.Stealth, catState.Cat);
                    Assert.Equal(0, Occurrences(beforeStealthBoundary,
                        PhasePacket(catState.CatPhaseBuff, catState.CatPhaseDeadline - schedule.At)));
                    schedule.At = stealthDeadline;
                    var atStealthBoundary = await Capture(() => Game.ReservationSkillsTick("test",
                        catWire.Stream, catWire.Ciphers, catConn, schedule.At));
                    Assert.Equal(ReservationState.CatPhase.Agility, catState.Cat);
                    Assert.Equal(104, catState.CatPhaseStartedAt, 3);
                    Assert.Equal(110, catState.CatPhaseDeadline, 3);
                    Assert.Equal(2, PhaseValues().Count);
                    Assert.Equal(1, Occurrences(atStealthBoundary,
                        InstanceEntities.RemoveBuff(Program.PlayerId, stealthInstance)));
                    Assert.Equal(1, Occurrences(atStealthBoundary, PhasePacket(catState.CatPhaseBuff, 6)));
                    var agilityStats = Game.CharacterPlayerStats(catConn);
                    Assert.Equal(1, Stat(agilityStats, "has_cat_aspect"));
                    Assert.Equal(0, Stat(agilityStats, "has_cats_stealth"));
                    Assert.Equal(1, Stat(agilityStats, "has_cats_agility"));
                    Assert.True(Stat(agilityStats, "attack_speed_+%") >= 20);
                    Assert.True(Stat(agilityStats, "base_cast_speed_+%") >= 20);
                    Assert.True((int)Math.Round(Game.SupportedAttackSeconds(catConn, attackSource) * 1000) < attackMsStealth);
                    Assert.True(Game.CastMs(catConn, Game.SupportedSkill(catConn, fireball).Effect, fireball)!.Value < castMsOff);
                    Assert.Equal(0, PlayerDamageTaken.AvoidPct(DamageType.Physical,
                        name => Stat(agilityStats, name)));
                    Assert.True(Hit(avoidSeed)!.RawDamage > 0);

                    double agilityDeadline = catState.CatPhaseDeadline;
                    schedule.At = agilityDeadline - .001;
                    await Game.ReservationSkillsTick("test", catWire.Stream, catWire.Ciphers, catConn, schedule.At);
                    Assert.Equal(ReservationState.CatPhase.Agility, catState.Cat);
                    schedule.At = agilityDeadline;
                    var atAgilityBoundary = await Capture(() => Game.ReservationSkillsTick("test",
                        catWire.Stream, catWire.Ciphers, catConn, schedule.At));
                    Assert.Equal(ReservationState.CatPhase.Stealth, catState.Cat);
                    Assert.Equal(10, catState.CatPhaseStartedAt - 100, 3);
                    Assert.Equal(1, Occurrences(atAgilityBoundary, PhasePacket(catState.CatPhaseBuff, 4)));

                    schedule.At = 1101;
                    await Game.ReservationSkillsTick("test", catWire.Stream, catWire.Ciphers, catConn, schedule.At);
                    Assert.Equal(ReservationState.CatPhase.Stealth, catState.Cat);
                    Assert.Equal(1100, catState.CatPhaseStartedAt, 3);
                    Assert.Equal(1104, catState.CatPhaseDeadline, 3);

                    Assert.True(await Game.ToggleAura("test", catWire.Stream, catWire.Ciphers, catConn, catSource.Id,
                        catSource.Effect, catSource.Name, catCost, catGrant.Level, 0));
                    Assert.DoesNotContain(catConn.Auras.Values, aura => aura.EffectId == "AspectOfTheCat");
                    Assert.Equal(ReservationState.CatPhase.None, catState.Cat);
                    Assert.Equal(0, catState.PresentedCatPhaseBuff);
                    Assert.DoesNotContain("AspectOfTheCat", CharacterSaves.Load(character)!.ActiveAuras ?? []);
                    var offStats = Game.CharacterPlayerStats(catConn);
                    Assert.Equal(0, Stat(offStats, "has_cat_aspect"));
                    Assert.Equal(0, Stat(offStats, "has_cats_stealth"));
                    Assert.Equal(0, Stat(offStats, "has_cats_agility"));

                    furPlacement = inventory.Add(PlayerInventories.BodyArmour, 0, 0, fur);
                    Game.EnsureOwnAuraPools(catConn);
                    var freeCatCost = Game.SupportedReservationCost(catConn, catSource.Id)!.Value;
                    Assert.Equal(0, freeCatCost.ManaPercent);
                    Assert.Equal(0, freeCatCost.ManaFlat);
                    catConn.PlayerMana = 123;
                    Assert.True(await Game.ToggleAura("test", catWire.Stream, catWire.Ciphers, catConn, catSource.Id,
                        catSource.Effect, catSource.Name, freeCatCost, catGrant.Level, 0));
                    Assert.Equal(123u, catConn.PlayerMana);
                    Assert.Equal(ReservationState.CatPhase.Stealth, catState.Cat);
                    Assert.Equal(6, catState.CatPhaseDeadline - catState.CatPhaseStartedAt, 3);
                    Assert.Equal(1036, catConn.PresentedAuras[catSource.Id].BuffRow);

                    inventory.Remove(PlayerInventories.BodyArmour, furPlacement.Id);
                    Game.EnsureOwnAuraPools(catConn);
                    var repriced = catConn.Auras[catSource.Id];
                    Assert.Equal(2500, repriced.ManaPercent);
                    Assert.Equal(123u, catConn.PlayerMana);
                    Assert.Equal(0, repriced.LifePercent);

                    var savedCatIntent = CharacterSaves.Load(character)!.ActiveAuras!.ToArray();
                    Assert.True(await Game.ToggleAura("test", catWire.Stream, catWire.Ciphers, catConn,
                        catSource.Id, catSource.Effect, catSource.Name, catCost, catGrant.Level, 0));
                    Assert.Equal(ReservationState.CatPhase.None, catState.Cat);
                    CharacterSaves.SetAuraActive(character, "AspectOfTheCat", true);

                    using var restoredWire = new Wire();
                    restoredConnection = new ClientState(conn.Layout, 0xC471, 1)
                    {
                        Character = character, CharacterId = Guid.NewGuid(), Account = "cat-core-account",
                        League = "Standard", LeagueForm = "Standard", ClassRow = witchRow, Instance = new(),
                        PlayerSent = true, PlayerObjectCreated = true, PlayerUpdatesState = updates,
                        Experience = CharacterStats.ExperienceFor(100), LeagueModule = conn.LeagueModule,
                        Wire = ("cat-restored", restoredWire.Stream, restoredWire.Ciphers), Pools = new(100, 1, 1, 0, 0),
                    };
                    var restoredConn = restoredConnection;
                    restoredConn.Pools = Game.PoolsFor(restoredConn, 100);
                    restoredConn.PlayerLife = restoredConn.Pools.Life;
                    restoredConn.PlayerMana = restoredConn.Pools.Mana;
                    var restoredCat = Game.RestoreRememberedAuras(restoredConn, savedCatIntent);
                    Assert.Contains(restoredCat, aura => aura.EffectId == "AspectOfTheCat");
                    Game.EnsureOwnAuraPools(restoredConn);
                    Assert.True(Game.CatAspectRootActiveLocked(restoredConn));
                    Assert.Equal(ReservationState.CatPhase.Stealth, restoredConn.Reservations.Cat);
                    Assert.Contains("AspectOfTheCat", CharacterSaves.Load(character)!.ActiveAuras ?? []);

                    var town = new ClientState(AreaLayouts.For(AreaLayouts.LioneyesWatch, 1), 2, 1)
                    {
                        Character = character, ClassRow = witchRow, Experience = CharacterStats.ExperienceFor(100),
                        Instance = new(), PlayerLife = 10000, PlayerMana = 10000,
                        Pools = restoredConn.Pools, LeagueModule = conn.LeagueModule,
                    };
                    Assert.True(Game.InTown(town));
                    Assert.Empty(Game.RestoreRememberedAuras(town, savedCatIntent));

                    inventory.Remove(PlayerInventories.Helmet, bitePlacement.Id);
                    Game.ReconcileOwnAuras(restoredConn, validateSkills: true);
                    Assert.DoesNotContain(restoredConn.Auras.Values, aura => aura.EffectId == "AspectOfTheCat");
                    Assert.Equal(ReservationState.CatPhase.None, restoredConn.Reservations.Cat);
                    Assert.DoesNotContain("AspectOfTheCat", CharacterSaves.Load(character)!.ActiveAuras ?? []);
                    bitePlacement = inventory.Add(PlayerInventories.Helmet, 0, 0,
                        new UniqueCatalog(Program.Items).Create("Farrul's Bite", new Random(505))!);
                    Game.ReconcileOwnAuras(restoredConn, validateSkills: true);
                    Assert.DoesNotContain(restoredConn.Auras.Values, aura => aura.EffectId == "AspectOfTheCat");

                    catSource = Assert.Single(Game.GemSkillEffects(restoredConn), gem =>
                        Game.SkillsFor(restoredConn).EffectId(gem.Effect) == "AspectOfTheCat");
                    Assert.True(await Game.ToggleAura("test", restoredWire.Stream, restoredWire.Ciphers, restoredConn,
                        catSource.Id, catSource.Effect, catSource.Name,
                        Game.SupportedReservationCost(restoredConn, catSource.Id)!.Value,
                        Game.SupportedSkill(restoredConn, catSource.Id).Level, 0));
                    restoredConn.PlayerLife = 0;
                    await Game.EndAurasOnDeath("test", restoredWire.Stream, restoredWire.Ciphers, restoredConn);
                    Assert.Equal(ReservationState.CatPhase.None, restoredConn.Reservations.Cat);
                    Assert.Equal(0, restoredConn.Reservations.PresentedCatPhaseBuff);
                    Assert.Empty(restoredConn.Auras);
                }
                finally
                {
                    if (restoredConnection is not null) restoredConnection.Closed = true;
                    if (catConnection is not null) catConnection.Closed = true;
                    Game.AreaUpdateContext.Value = previousContext;
                    PlayerInventories.Forget(character);
                }
            }
            await CatCoreFixture();
        }
        finally
        {
            conn.Closed = true; PlayerInventories.Forget(conn.Character!); lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime; Program.SetGameLogic(previousLogic!); TestEnvironment.ReleaseSaves(path);
        }
    }

    sealed class CatClockSchedule : IPlayerUpdateSchedule
    {
        public double At;
        public double Now(ClientState conn) => At;
        public Task Wait(int milliseconds, CancellationToken stopping) => Task.CompletedTask;
    }

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0); readonly TcpClient writer = new(); readonly TcpClient reader; readonly Task drain;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false); public NetworkStream Stream => writer.GetStream();
        public Wire()
        {
            listener.Start(); var accepting = listener.AcceptTcpClientAsync(); writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult(); drain = Task.Run(async () => { try { var b = new byte[65536]; while (await reader.GetStream().ReadAsync(b) > 0) { } } catch (Exception e) when (e is IOException or ObjectDisposedException) { } });
        }
        public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Stop(); drain.GetAwaiter().GetResult(); }
    }
}
