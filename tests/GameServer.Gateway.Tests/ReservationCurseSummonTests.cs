using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class ReservationCurseSummonTests
{
    sealed class FixedProjectileRuntime(long initialTicks) : IProjectileRuntime
    {
        long ticks = initialTicks;
        public bool Active { get; private set; } = true;
        public double NowTicks => ticks;
        public void Advance(double seconds) => ticks += ProjectileRuntime.Time(seconds);
        public void Invalidate() => Active = false;
    }

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
            var weapon = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with { Sockets = [new(SocketColour.White, gem)] };
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, weapon);
            var source = Game.GemSkillEffects(conn).Single(g => Program.Skills.EffectId(g.Effect) == "SummonSkeletons");
            var skeleton = catalog.Find("SummonSkeletons")!;
            int limit = Game.SummonLimit(conn, skeleton, Game.SupportedSkill(conn, source.Id));
            Assert.Equal(5, limit);
            for (int i = 0; i < limit + 2; i++) Assert.NotEqual(0u, await Game.SummonFighter("test", wire.Stream, wire.Ciphers, conn, source.Id, skeleton, (100, 100)));
            Assert.Equal(limit, conn.Summons.Fighters.Count); Assert.Empty(conn.Golems.All);
            Assert.All(conn.Summons.Fighters.Values, g => Assert.Contains(g.Abilities, a => a.Attack.Damage.MaxTotal > 0));
            var summonedSkeleton = conn.Summons.Fighters.Values.First();
            double Chaos() => summonedSkeleton.Abilities.Max(a => a.Attack.Damage[GameServer.Core.World.DamageType.Chaos].Max);
            double baseChaos = Chaos();
            var shield = ItemCatalog.Create(Program.Items.Find("Titanium Spirit Shield")!);
            var inventories = Game.ItemsOf(conn);
            var wornShield = inventories.Add(PlayerInventories.Offhand1, 0, 0, shield);
            var eye = new UniqueCatalog(Program.Items).Create("Dead Reckoning", new Random(3))!;
            conn.Passives.Add(Program.Passives.JewelNodeForSlot(1)!.Value);
            inventories.Put(PlayerInventories.PassiveJewels, new(192, 1, 0, eye));
            double now = conn.Clock.Elapsed.TotalSeconds;
            await Game.SummonsTick("test", wire.Stream, wire.Ciphers, conn, now + 1);
            Assert.True(Chaos() > baseChaos);
            inventories.Remove(PlayerInventories.Offhand1, wornShield.Id);
            await Game.SummonsTick("test", wire.Stream, wire.Ciphers, conn, now + 2);
            Assert.Equal(baseChaos, Chaos());
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
                Game.ProjectileSession? blindProjectiles = null;
                IProjectileRuntime? blindClock = null;
                IProjectileRuntime? priorBlindRuntime = null;
                GameServer.Core.Projectiles.ProjectileUse? frozenBlindUse = null;
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
                    var darkray = new UniqueCatalog(Program.Items).Create("Darkray Vectors", new Random(504))!;
                    var darkrayPlacement = inventory.Add(PlayerInventories.Boots, 0, 0, darkray);
                    catConn.Pools = Game.PoolsFor(catConn, 100);
                    catConn.PlayerLife = catConn.Pools.Life;
                    catConn.PlayerMana = catConn.Pools.Mana;
                    Assert.Empty(catConn.PlayerStats);
                    Assert.Equal(100, catConn.Pools.Level);
                    Assert.False(Game.IsInactive(catConn, darkrayPlacement.Id));
                    var replica = new UniqueCatalog(Program.Items).Create("Replica Farrul's Fur", new Random(504))!;
                    var replicaMods = replica.Mods.Select(mod => Program.Items.Mods.ByHash(mod.Hash)!.Id).ToArray();
                    Assert.Contains("CatsAgilityDurationUnique__1", replicaMods);
                    Assert.DoesNotContain("CatsStealthDurationUnique__1_", replicaMods);

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
                    {
                        var definitions = GameServer.Core.ClientTables.Table("BuffDefinitions");
                        Assert.False(definitions[1037].Bool("IsRecovery"));
                        Assert.False(definitions[1038].Bool("IsRecovery"));
                        Assert.Equal(new[] { 556, 7521, 12561 },
                            definitions[1037].Ints("StatsKeys"));
                        Assert.Equal(new[] { 70, 218 },
                            definitions[1038].Ints("StatsKeys"));
                    }

                    ushort attackSource = Assert.Single(Game.GemSkillEffects(catConn), gem =>
                        Game.SkillsFor(catConn).EffectId(gem.Effect) == "TornadoShot").Id;
                    var fireballSource = Assert.Single(Game.GemSkillEffects(catConn), gem =>
                        Game.SkillsFor(catConn).EffectId(gem.Effect) == "Fireball");
                    ushort fireball = fireballSource.Id;
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
                    static bool TryCompactUInt(ReadOnlySpan<byte> bytes, ref int at, out uint value)
                    {
                        value = 0;
                        if (at >= bytes.Length) return false;
                        uint first = bytes[at++];
                        if (first < 0x80) { value = first; return true; }
                        int following = first < 0xc0 ? 1 : first < 0xe0 ? 2 : first < 0xf0 ? 3 : 4;
                        if (bytes.Length - at < following) return false;
                        if (following == 1) value = (first & 0x3f) << 8 | bytes[at++];
                        else if (following == 2) value = (first & 0x1f) << 16 | (uint)bytes[at++] << 8 | bytes[at++];
                        else if (following == 3) value = (first & 0x0f) << 24 | (uint)bytes[at++] << 16
                            | (uint)bytes[at++] << 8 | bytes[at++];
                        else value = (uint)bytes[at++] << 24 | (uint)bytes[at++] << 16
                            | (uint)bytes[at++] << 8 | bytes[at++];
                        return true;
                    }
                    static bool TryCompactInt(ReadOnlySpan<byte> bytes, ref int at, out int value)
                    {
                        static int Signed(uint value, int bits) => (value & (1u << (bits - 1))) != 0
                            ? (int)(value | ~((1u << bits) - 1)) : (int)value;
                        value = 0;
                        if (at >= bytes.Length) return false;
                        uint first = bytes[at++];
                        if (first < 0x80) { value = Signed(first & 0x7f, 7); return true; }
                        int following = first < 0xc0 ? 1 : first < 0xe0 ? 2 : first < 0xf0 ? 3 : 4;
                        if (bytes.Length - at < following) return false;
                        if (following == 1) value = Signed((first & 0x3f) << 8 | bytes[at++], 14);
                        else if (following == 2) value = Signed((first & 0x1f) << 16 | (uint)bytes[at++] << 8
                            | bytes[at++], 21);
                        else if (following == 3) value = Signed((first & 0x0f) << 24 | (uint)bytes[at++] << 16
                            | (uint)bytes[at++] << 8 | bytes[at++], 28);
                        else value = (int)((uint)bytes[at++] << 24 | (uint)bytes[at++] << 16
                            | (uint)bytes[at++] << 8 | bytes[at++]);
                        return true;
                    }
                    static IEnumerable<Dictionary<uint, int>> PublishedStats(IEnumerable<byte[]> packets)
                    {
                        var header = InstanceEntities.StatsUpdate(Program.PlayerId, []).AsSpan(0, 12).ToArray();
                        foreach (var packet in packets)
                        {
                            int scan = 0;
                            while (scan <= packet.Length - header.Length)
                            {
                                int found = packet.AsSpan(scan).IndexOf(header);
                                if (found < 0) break;
                                int start = scan + found;
                                int cursor = start + header.Length;
                                if (!TryCompactUInt(packet, ref cursor, out uint count) || count > 10_000)
                                {
                                    scan = start + 1;
                                    continue;
                                }
                                var stats = new Dictionary<uint, int>();
                                bool complete = true;
                                for (uint i = 0; i < count; i++)
                                {
                                    if (!TryCompactUInt(packet, ref cursor, out uint key)
                                        || !TryCompactInt(packet, ref cursor, out int value)
                                        || !stats.TryAdd(key, value))
                                    {
                                        complete = false;
                                        break;
                                    }
                                }
                                if (!complete)
                                {
                                    scan = start + 1;
                                    continue;
                                }
                                yield return stats;
                                scan = cursor;
                            }
                        }
                    }
                    static void AssertPublished(IEnumerable<byte[]> packets,
                        params (string Name, int Value)[] expected)
                    {
                        var snapshots = PublishedStats(packets).ToArray();
                        Assert.NotEmpty(snapshots);
                        var keys = expected.Select(row => (Key: Program.Stats.Key(row.Name)!.Value, row.Name, row.Value)).ToArray();
                        Assert.True(snapshots.Any(snapshot => keys.All(row => snapshot.ContainsKey(row.Key))),
                            "0x1b3 has no full snapshot with all selected stats: "
                            + string.Join("; ", snapshots.Select(snapshot => $"[{string.Join(",", snapshot.Keys)}]")));
                        foreach (var snapshot in snapshots)
                            foreach (var (key, name, value) in keys)
                            {
                                if (!snapshot.TryGetValue(key, out int actual)) continue;
                                Assert.True(actual == value,
                                    $"0x1b3 published stat {key} ({name}) as {actual}, expected {value}");
                            }
                    }

                    var catState = catConn.Reservations;
                    int Stat(IReadOnlyDictionary<uint, int> stats, string name) =>
                        Program.Stats.Key(name) is uint statKey ? stats.GetValueOrDefault(statKey) : 0;
                    List<byte[]> initial;
                    using (new Game.PlayerStatScope(catConn, fresh: true))
                    {
                        var primedScope = Game.BuildScopeOf(catConn)!;
                        _ = Game.FinalPlayerStats(catConn).ToList();
                        var primedCharacter = primedScope.Stats;
                        var primedFinal = primedScope.Final;
                        Assert.Equal(0, Stat(primedCharacter, "has_cat_aspect"));
                        initial = await Capture(async () => Assert.True(await Game.ToggleAura("test", catWire.Stream,
                            catWire.Ciphers, catConn, catSource.Id, catSource.Effect, catSource.Name, catCost,
                            catGrant.Level, 0)));
                        var restoredScope = Game.BuildScopeOf(catConn);
                        Assert.Same(primedScope, restoredScope);
                        Assert.Equal(primedCharacter, restoredScope!.Stats);
                        Assert.Equal(primedFinal, restoredScope.Final);
                    }
                    AssertPublished(initial, ("has_cat_aspect", 1), ("has_cats_stealth", 1),
                        ("has_cats_agility", 0));
                    var catAura = Assert.Single(catConn.Auras.Values, aura => aura.EffectId == "AspectOfTheCat");
                    var chargeSession = Game.ChargesOf(catConn);
                    Assert.Equal(0, chargeSession.Effective(ChargeType.Frenzy));
                    Assert.Equal(0, chargeSession.Effective(ChargeType.Power));
                    Assert.False(catConn.IsPhasing);
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
                    void CheckPounce(bool stealth)
                    {
                        var pounce = new UniqueCatalog(Program.Items).Create("Farrul's Pounce", new Random(504))!;
                        var placed = inventory.Add(PlayerInventories.Gloves, 0, 0, pounce);
                        try
                        {
                            using var fresh = new Game.PlayerStatScope(catConn, fresh: true);
                            var current = Game.CharacterPlayerStats(catConn);
                            Assert.False(Game.IsInactive(catConn, placed.Id));
                            Assert.Equal(stealth ? 1 : 0, Stat(current, "global_bleed_on_hit"));
                            Assert.Equal(stealth ? 1 : 0, Stat(current, "keystone_crimson_dance"));
                            var bleed = Game.BleedSettings([], "main_hand", name => Stat(current, name), null,
                                melee: true, bow: false);
                            Assert.Equal(stealth ? 100 : 0, bleed.Chance);
                            Assert.Equal(stealth ? 8 : 1, bleed.Stacks);
                            var raw = Game.CharacterStatSources(catConn).Stats;
                            int Read(string name) => Stat(raw, name);
                            var panel = PanelStats.Compute(Read).ToDictionary(p => p.Stat, p => p.Value);
                            Assert.Equal(stealth ? 100 : 0, panel.GetValueOrDefault("virtual_total_main_hand_chance_to_bleed_on_hit_%"));
                            var selected = PanelStats.Selected(Read, _ => 0).ToDictionary(p => p.Stat, p => p.Value);
                            Assert.Equal(stealth ? 100 : 0, selected["virtual_total_main_hand_chance_to_bleed_on_hit_%"]);
                            var prohibited = PanelStats.Selected(Read, name => name == "cannot_cause_bleeding" ? 1 : 0)
                                .ToDictionary(p => p.Stat, p => p.Value);
                            Assert.Equal(0, prohibited["virtual_total_main_hand_chance_to_bleed_on_hit_%"]);
                        }
                        finally { inventory.Remove(PlayerInventories.Gloves, placed.Id); }
                        using var removedScope = new Game.PlayerStatScope(catConn, fresh: true);
                        var removed = Game.CharacterPlayerStats(catConn);
                        Assert.Equal(0, Stat(removed, "global_bleed_on_hit"));
                        Assert.Equal(0, Stat(removed, "keystone_crimson_dance"));
                    }
                    CheckPounce(stealth: true);
                    int attackMsStealth = (int)Math.Round(Game.SupportedAttackSeconds(catConn, attackSource) * 1000);

                    // Actual Farrul's Bite: the accepted-hit ailment consumer passes its full captured critical Blind
                    // into the owner-backed state read by gameplay.
                    var biteBlindMod = Assert.Single(bite.Mods, mod =>
                        Program.Items.Mods.ByHash(mod.Hash)?.Id == "CritsBlindChanceWithCatsStealthUnique__1");
                    Assert.Equal("%_chance_to_blind_on_critical_strike_while_you_have_cats_stealth",
                        Assert.Single(Program.Items.Mods.ByHash(biteBlindMod.Hash)!.Stats).Id);
                    int rolledBiteBlind = Assert.Single(biteBlindMod.Values);
                    Assert.InRange(rolledBiteBlind, 10, 20);
                    Assert.Equal(18, rolledBiteBlind);
                    Assert.Equal(rolledBiteBlind,
                        Stat(stealthStats, "%_chance_to_blind_on_critical_strike"));
                    Assert.Equal(0, Stat(stealthStats, "global_chance_to_blind_on_hit_%"));
                    const string biteDamageStat = "hit_and_ailment_damage_+%_vs_blinded_enemies";
                    var biteDamageMod = Assert.Single(bite.Mods, mod =>
                        Program.Items.Mods.ByHash(mod.Hash)?.Id == "DamageAgainstBlindedEnemiesUnique__1");
                    Assert.Equal(biteDamageStat,
                        Assert.Single(Program.Items.Mods.ByHash(biteDamageMod.Hash)!.Stats).Id);
                    int rolledBiteDamage = Assert.Single(biteDamageMod.Values);
                    Assert.InRange(rolledBiteDamage, 40, 50);
                    Assert.Equal(rolledBiteDamage, Stat(stealthStats, biteDamageStat));

                    var owner = Game.ResourceEffectsOf(catConn);
                    Assert.True(Game.ResourceEffectsCurrent(catConn, owner),
                        "the Bite probe requires current module rules and the exact source owner");
                    uint attackBlindId = 0xC470A1u, spellBlindId = 0xC470A2u, noncritBlindId = 0xC470A3u;
                    uint deadBlindId = 0xC470A4u, controlBlindId = 0xC470A5u, immuneBlindId = 0xC470A6u;
                    uint staleBlindId = 0xC470A8u, nonhostileBlindId = 0xC470A9u, unregisteredBlindId = 0xC470AAu;
                    uint biteDamageControlId = 0xC470ADu, bitePoisonLifecycleId = 0xC470AEu;
                    uint[] probeIds = [attackBlindId, spellBlindId, noncritBlindId, deadBlindId, controlBlindId,
                        immuneBlindId, staleBlindId, nonhostileBlindId, biteDamageControlId, bitePoisonLifecycleId];
                    Assert.NotSame(conn.Clock, catConn.Clock);
                    priorBlindRuntime = catConn.ProjectileRuntimeState;
                    blindClock = new FixedProjectileRuntime(
                        ProjectileRuntime.Time(Game.ResourceEffectsTime(catConn)));
                    catConn.ProjectileRuntimeState = blindClock;
                    double blindNow = Game.ResourceEffectsTime(catConn);
                    Assert.Equal(blindClock.NowTicks / ProjectileRuntime.QuantaPerSecond, blindNow, 9);
                    Assert.Equal(blindNow, Game.ResourceEffectsTime(catConn), 9);
                    Assert.False(catConn.Paused);
                    Assert.Null(catConn.ProjectileRuntime);
                    int blindSeed = Enumerable.Range(1, 10_000)
                        .First(seed => new Random(seed).Next(100) < rolledBiteBlind);
                    SpawnedMonster Probe(uint id, string label, bool dead = false, bool immune = false, bool hostile = true)
                    {
                        var probe = new SpawnedMonster(label, new(100, 100, 1), 100, 8, 1_000_000)
                        {
                            Level = 100, Hostile = hostile, Dead = dead, Life = dead ? 0u : 1_000_000u,
                        };
                        if (immune)
                            probe.Mods = MonsterModSet.None with
                            {
                                Effects = new MonsterModEffects(new Dictionary<string, int> { ["cannot_be_blinded"] = 1 }),
                            };
                        catConn.Monsters[id] = probe;
                        return probe;
                    }
                    bool AcceptedBiteHit(uint id, SpawnedMonster target, bool spell, Random random)
                    {
                        Assert.Same(target, catConn.Monsters[id]);
                        Game.AilmentsOnHitLocked(catConn, id, target, DamagePacket.Of(DamageType.Physical, 1, 1),
                            spell, critical: true, criticalMultiplier: 100, stealthStats, null, blindNow,
                            random, blind: BlindHitInput.Capture(stealthStats));
                        return Game.Blinded(catConn, id);
                    }

                    SpawnedMonster? retainedBlindTarget = null;
                    uint retainedBlindId = 0;
                    try
                    {
                        lock (catConn.Monsters)
                        {
                            var staleTarget = Probe(staleBlindId, "stale-replaced-blind");
                            var staleReplacement = Probe(staleBlindId, "same-id-replacement");
                            var nonhostileTarget = Probe(nonhostileBlindId, "nonhostile-blind", hostile: false);
                            var unregisteredTarget = new SpawnedMonster("unregistered-blind", new(100, 100, 1),
                                100, 8, 1_000_000) { Hostile = true };
                            var rejectedRandom = new CountingRandom();
                            Assert.False(Game.InflictMonsterBlind(catConn, staleBlindId, staleTarget, blindNow,
                                (50, 0, 4), rejectedRandom));
                            Assert.False(Game.InflictMonsterBlind(catConn, nonhostileBlindId, nonhostileTarget,
                                blindNow, (50, 0, 4), rejectedRandom));
                            Assert.False(Game.InflictMonsterBlind(catConn, unregisteredBlindId, unregisteredTarget,
                                blindNow, (50, 0, 4), rejectedRandom));
                            Assert.Equal(0, rejectedRandom.Rolls);
                            Assert.DoesNotContain(staleBlindId, owner.Targets.Keys);
                            Assert.DoesNotContain(nonhostileBlindId, owner.Targets.Keys);
                            Assert.DoesNotContain(unregisteredBlindId, owner.Targets.Keys);
                            Assert.Null(Game.BlindPresentation(catConn, stealthStats));
                            Assert.Same(staleReplacement, catConn.Monsters[staleBlindId]);

                            var attackTarget = Probe(attackBlindId, "bite-crit-blind-attack");
                            var spellTarget = Probe(spellBlindId, "bite-crit-blind-spell");
                            var noncritTarget = Probe(noncritBlindId, "bite-crit-blind-noncrit");
                            var deadTarget = Probe(deadBlindId, "bite-crit-blind-dead", dead: true);
                            var controlTarget = Probe(controlBlindId, "independent-global-blind-control");
                            var immuneTarget = Probe(immuneBlindId, "independent-global-blind-immune", immune: true);

                            // This positive control uses the existing generic global pipeline only to distinguish the
                            // owner state/lifetime setup from missing Bite wiring. It is not Bite producer evidence.
                            uint globalBlindKey = Program.Stats.Key("global_chance_to_blind_on_hit_%")!.Value;
                            var controlPlayer = new Dictionary<uint, int>(stealthStats) { [globalBlindKey] = 100 };
                            Assert.True(Game.InflictMonsterBlind(catConn, controlBlindId, controlTarget, blindNow,
                                Game.BlindInputs(controlPlayer), new Random(1)));
                            Assert.True(Game.Blinded(catConn, controlBlindId));
                            Assert.Equal(80, Game.MonsterBlindRating(catConn, controlBlindId, controlTarget, 100,
                                blindNow, stealthStats));
                            Assert.False(Game.InflictMonsterBlind(catConn, immuneBlindId, immuneTarget, blindNow,
                                Game.BlindInputs(controlPlayer), new Random(1)));
                            Assert.False(Game.Blinded(catConn, immuneBlindId));

                            // This starts at the accepted-hit ailment consumer with a controlled critical result; it does
                            // not claim the native skill selected or rolled the critical strike. Agility is not staged:
                            // moving the accelerated Cat schedule would alter the fixture's existing phase timing.
                            var attackRandom = new CountingRandom();
                            var spellRandom = new CountingRandom();
                            bool attackOwned = AcceptedBiteHit(attackBlindId, attackTarget, spell: false, attackRandom);
                            bool spellOwned = AcceptedBiteHit(spellBlindId, spellTarget, spell: true, spellRandom);
                            Game.AilmentsOnHitLocked(catConn, noncritBlindId, noncritTarget,
                                DamagePacket.Of(DamageType.Physical, 1, 1),
                                spell: false, critical: false, criticalMultiplier: 100, stealthStats, null, blindNow,
                                new Random(blindSeed), blind: BlindHitInput.Capture(stealthStats));
                            Game.AilmentsOnHitLocked(catConn, deadBlindId, deadTarget,
                                DamagePacket.Of(DamageType.Physical, 1, 1),
                                spell: false, critical: true, criticalMultiplier: 100, stealthStats, null, blindNow,
                                new Random(blindSeed), blind: BlindHitInput.Capture(stealthStats));
                            Assert.True(attackOwned && spellOwned,
                                $"actual Bite critical inputs did not reach owned Blind: attack={attackOwned}, spell={spellOwned}");
                            Assert.Equal(1, attackRandom.Rolls);
                            Assert.Equal(1, spellRandom.Rolls);
                            Assert.False(Game.Blinded(catConn, noncritBlindId));
                            Assert.False(Game.Blinded(catConn, deadBlindId));
                            retainedBlindTarget = attackTarget;
                            retainedBlindId = attackBlindId;

                            int attackRating = Game.MonsterBlindRating(catConn, attackBlindId, attackTarget, 100,
                                blindNow, stealthStats);
                            int spellRating = Game.MonsterBlindRating(catConn, spellBlindId, spellTarget, 100,
                                blindNow, stealthStats);
                            Assert.True(Game.Blinded(catConn, attackBlindId) && attackRating == 80
                                    && Game.Blinded(catConn, spellBlindId) && spellRating == 80,
                                $"Farrul's Bite raw {rolledBiteBlind} folded to crit Blind {Stat(stealthStats, "%_chance_to_blind_on_critical_strike")}; "
                                + $"attack owned={Game.Blinded(catConn, attackBlindId)} rating={attackRating}; "
                                + $"spell owned={Game.Blinded(catConn, spellBlindId)} rating={spellRating}; "
                                + $"independent global control owned={Game.Blinded(catConn, controlBlindId)}");

                            var attackState = owner.TargetBlind(attackBlindId, attackTarget);
                            var attackBlindValue = attackState.Read(blindNow);
                            Assert.Equal(20, attackBlindValue.Less);
                            Assert.Equal(blindNow + 4, attackBlindValue.Until, 9);
                            Assert.False(GameServer.Core.ClientTables.Table("BuffDefinitions")[179].Bool("IsRecovery"));
                            double presentationBefore = Game.ResourceEffectsTime(catConn);
                            var presentation = Assert.IsType<byte[]>(Game.BlindPresentation(catConn, stealthStats));
                            double presentationAfter = Game.ResourceEffectsTime(catConn);
                            var attackBuff = InstanceEntities.AddBuff(attackBlindId, attackState.Buff, 179,
                                Program.PlayerId, u68: 0);
                            int attackBuffAt = presentation.AsSpan().IndexOf(attackBuff.AsSpan(0, 23));
                            Assert.True(attackBuffAt >= 0, "the owned row-179 Blind packet was not published");
                            Assert.Equal(31, attackBuff.Length);
                            Assert.True(attackBuffAt + attackBuff.Length <= presentation.Length,
                                "the owned row-179 Blind packet was truncated");
                            var publishedAttackBuff = presentation.AsSpan(attackBuffAt, attackBuff.Length);
                            Assert.Equal(InstanceEntities.AddBuffId,
                                BinaryPrimitives.ReadUInt16BigEndian(publishedAttackBuff));
                            Assert.Equal(attackBlindId,
                                BinaryPrimitives.ReadUInt32BigEndian(publishedAttackBuff[2..]));
                            Assert.Equal(attackState.Buff,
                                BinaryPrimitives.ReadUInt16BigEndian(publishedAttackBuff[12..]));
                            Assert.Equal(179, BinaryPrimitives.ReadUInt16BigEndian(publishedAttackBuff[14..]));
                            Assert.Equal(Program.PlayerId,
                                BinaryPrimitives.ReadUInt32BigEndian(publishedAttackBuff[16..]));
                            Assert.Equal(0x02, presentation[attackBuffAt + 22]);
                            float remaining = BitConverter.UInt32BitsToSingle(
                                BinaryPrimitives.ReadUInt32BigEndian(publishedAttackBuff[23..]));
                            float minimumRemaining = (float)Math.Max(0, blindNow + 4 - presentationAfter);
                            float maximumRemaining = (float)(blindNow + 4 - presentationBefore);
                            Assert.InRange(remaining, minimumRemaining - 1e-3f, maximumRemaining + 1e-3f);
                            Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(publishedAttackBuff[27..]));
                            var attackStats = InstanceEntities.StatsUpdate(attackBlindId,
                                [(973u, 1), (15023u, -20), (15024u, -20)]);
                            Assert.Equal(1, Occurrences([presentation], attackStats));
                            Assert.Null(Game.BlindPresentation(catConn, stealthStats));

                            var spellState = owner.TargetBlind(spellBlindId, spellTarget);
                            var spellRemove = InstanceEntities.RemoveBuff(spellBlindId, spellState.Buff);
                            spellState.Clear();
                            var removal = Assert.IsType<byte[]>(Game.BlindPresentation(catConn, stealthStats));
                            Assert.Equal(1, Occurrences([removal], spellRemove));
                            Assert.Equal(1, Occurrences([removal], InstanceEntities.StatsUpdate(spellBlindId,
                                [(973u, 0), (15023u, 0), (15024u, 0)])));
                            Assert.False(Game.Blinded(catConn, spellBlindId));

                            const uint futureBlindId = 0xC470ABu;
                            var futureBlindTarget = Probe(futureBlindId, "isolated-future-blind");
                            Assert.True(Game.InflictMonsterBlind(catConn, futureBlindId, futureBlindTarget, blindNow,
                                (100, 50, 1), new Random(1)));
                            Assert.True(Game.InflictMonsterBlind(catConn, futureBlindId, futureBlindTarget, blindNow + 1,
                                (100, 0, 6), new Random(1)));
                            var futureState = owner.TargetBlind(futureBlindId, futureBlindTarget);
                            Assert.Equal(70, Game.MonsterBlindRating(catConn, futureBlindId, futureBlindTarget, 100,
                                blindNow + .5, stealthStats));
                            var futureBlindValue = futureState.Read(blindNow + 1.5);
                            Assert.Equal(20, futureBlindValue.Less);
                            Assert.Equal(blindNow + 7, futureBlindValue.Until, 9);
                            Assert.True(Game.Blinded(catConn, futureBlindId));
                            Assert.Equal(80, Game.MonsterBlindRating(catConn, futureBlindId, futureBlindTarget, 100,
                                blindNow + 1.5, stealthStats));
                            catConn.Monsters.Remove(futureBlindId);
                            owner.CollectRetiredTargets(catConn);
                            Assert.DoesNotContain(futureBlindId, owner.Targets.Keys);
                            attackBlindValue = attackState.Read(blindNow);
                            Assert.Equal(20, attackBlindValue.Less);
                            Assert.Equal(blindNow + 4, attackBlindValue.Until, 9);

                            // Controlled deterministic contact fixture for the missing Bite damage consumer. Keep the
                            // real bow's local spread and add a valid constant 100 physical range before the ordinary
                            // capture. The Bite roll is additive to the same existing increased bucket, so the blinded
                            // and unblinded expected intervals remain independent for every actual damage roll.
                            var savedDamageRuntime = catConn.ProjectileRuntimeState;
                            Game.ProjectileSession? poisonFutureSession = null;
                            var biteDamageSession = new Game.ProjectileSession(
                                "cat-bite-damage", catWire.Stream, catWire.Ciphers, catConn);
                            try
                            {
                                double damageAt = Game.ResourceEffectsTime(catConn);
                                long damageNow = ProjectileRuntime.Time(damageAt);
                                biteDamageSession.Runtime.AdvanceTo(damageNow);
                                catConn.ProjectileRuntime = biteDamageSession;
                                double alignedAt = biteDamageSession.Runtime.Now
                                    / (double)ProjectileRuntime.QuantaPerSecond;
                                Assert.InRange(alignedAt, damageAt - 1d / ProjectileRuntime.QuantaPerSecond,
                                    damageAt + 1d / ProjectileRuntime.QuantaPerSecond);
                                damageAt = Game.ResourceEffectsTime(catConn);
                                var actualWeapon = Game.WeaponFor(catConn);
                                var savedPlayerStats = catConn.PlayerStats.ToArray();
                                try
                                {
                                    void Override(string name, int value)
                                    {
                                        uint key = Program.Stats.Key(name)
                                            ?? throw new InvalidOperationException($"missing stat {name}");
                                        catConn.PlayerStats[key] = value;
                                    }
                                    void Zero(string name)
                                    {
                                        if (Program.Stats.Key(name) is uint key) catConn.PlayerStats[key] = 0;
                                    }
                                    foreach (string damageType in new[] { "fire", "cold", "lightning", "chaos" })
                                    {
                                        Zero($"attack_minimum_added_{damageType}_damage");
                                        Zero($"attack_maximum_added_{damageType}_damage");
                                    }
                                    foreach (string name in new[]
                                    {
                                        "attack_minimum_added_physical_damage_per_25_strength",
                                        "attack_maximum_added_physical_damage_per_25_strength",
                                        "attack_minimum_added_physical_damage_per_25_dexterity",
                                        "attack_maximum_added_physical_damage_per_25_dexterity",
                                        "minimum_added_fire_damage_to_attacks_per_10_strength",
                                        "maximum_added_fire_damage_to_attacks_per_10_strength",
                                        "minimum_added_cold_damage_to_attacks_per_10_dexterity",
                                        "maximum_added_cold_damage_to_attacks_per_10_dexterity",
                                        "minimum_added_lightning_damage_to_attacks_per_10_intelligence",
                                        "maximum_added_lightning_damage_to_attacks_per_10_intelligence",
                                        "minimum_added_chaos_damage_to_attacks_per_50_strength",
                                        "maximum_added_chaos_damage_to_attacks_per_50_strength",
                                        "attack_skills_have_added_lightning_damage_equal_to_%_of_maximum_mana",
                                        "added_physical_damage_to_attacks_equal_to_%_maximum_mana",
                                        "added_fire_damage_to_attacks_equal_to_%_maximum_life",
                                        "non_chaos_damage_to_add_as_chaos_damage_%",
                                    })
                                        Zero(name);
                                    foreach (string targetType in new[] { "lightning", "cold", "fire", "chaos" })
                                        foreach (string prefix in new[] { "", "base_", "skill_", "attack_" })
                                        {
                                            Zero($"{prefix}physical_damage_%_to_convert_to_{targetType}");
                                            Zero($"{prefix}physical_damage_%_to_add_as_{targetType}");
                                        }
                                    foreach (string increased in new[]
                                    {
                                        "attack_damage_+%", "projectile_damage_+%", "physical_damage_+%",
                                        "elemental_damage_+%", "fire_damage_+%", "cold_damage_+%",
                                        "lightning_damage_+%", "chaos_damage_+%", "spell_damage_+%",
                                        "damage_+%_while_leeching", "attack_damage_+%_while_leeching",
                                        "damage_+%_while_life_leeching", "damage_+%_while_mana_leeching",
                                        "damage_+%_while_es_leeching", "damage_+%_on_full_energy_shield",
                                        "physical_damage_+%_while_life_leeching", "keystone_strong_bowman",
                                        "is_life_leeching", "is_mana_leeching", "is_es_leeching",
                                        "on_full_energy_shield", "base_extra_damage_rolls",
                                        "base_chance_to_deal_triple_damage_%", "chance_to_deal_double_damage_%",
                                        "spell_chance_to_deal_double_damage_%",
                                    })
                                        Zero(increased);
                                    uint physicalMinKey = Program.Stats.Key("attack_minimum_added_physical_damage")
                                        ?? throw new InvalidOperationException("missing physical minimum source key");
                                    uint physicalMaxKey = Program.Stats.Key("attack_maximum_added_physical_damage")
                                        ?? throw new InvalidOperationException("missing physical maximum source key");
                                    const int physicalAddedMin = 100;
                                    const int physicalAddedMax = 100;
                                    catConn.PlayerStats[physicalMinKey] = physicalAddedMin;
                                    catConn.PlayerStats[physicalMaxKey] = physicalAddedMax;
                                    Override("damage_+%", 100);
                                    foreach (string damageType in new[] { "physical", "cold", "lightning", "chaos" })
                                    {
                                        Zero($"spell_minimum_added_{damageType}_damage");
                                        Zero($"spell_maximum_added_{damageType}_damage");
                                    }
                                    Override("spell_minimum_added_fire_damage", 100);
                                    Override("spell_maximum_added_fire_damage", 100);
                                    Override("area_damage_+%", 100);
                                    Override("base_chance_to_poison_on_hit_%", 100);
                                    Assert.Equal(physicalAddedMin, catConn.PlayerStats[physicalMinKey]);
                                    Assert.Equal(physicalAddedMax, catConn.PlayerStats[physicalMaxKey]);

                                    using (new Game.PlayerStatScope(catConn, fresh: true))
                                    {
                                        var damageStats = Game.CharacterPlayerStats(catConn);
                                        int Get(string name) => Stat(damageStats, name);
                                        Assert.Equal(40, rolledBiteDamage);
                                        Assert.Equal(rolledBiteDamage, Get(biteDamageStat));
                                        Assert.Equal(physicalAddedMin, Get("attack_minimum_added_physical_damage"));
                                        Assert.Equal(physicalAddedMax, Get("attack_maximum_added_physical_damage"));
                                        var localPhysical = actualWeapon.LocalDamage?[DamageType.Physical]
                                            ?? new DamageRange(actualWeapon.MinDamage, actualWeapon.MaxDamage);
                                        var expectedRaw = localPhysical.Add(new DamageRange(physicalAddedMin,
                                            physicalAddedMax));
                                        var raw = SupportedAttackDamage.Raw(actualWeapon, Get);
                                        var rawPhysical = Assert.Single(raw.Components);
                                        Assert.Equal(DamageType.Physical, rawPhysical.Type);
                                        Assert.Equal(expectedRaw, rawPhysical.Range);
                                        Assert.True(raw.MinTotal == expectedRaw.Min && raw.MaxTotal == expectedRaw.Max,
                                            $"controlled source raw was {raw}; expected only physical {expectedRaw} from "
                                            + $"local weapon {localPhysical} plus added {physicalAddedMin}-"
                                            + $"{physicalAddedMax}");
                                        var conversion = DamageStats.Conversion(damageStats, Program.Stats, attack: true);
                                        var converted = Assert.Single(conversion.Trace(raw));
                                        Assert.Equal(DamageType.Physical, converted.Type);
                                        Assert.Equal(DamageTypeHistory.Physical, converted.History);
                                        Assert.Equal(expectedRaw, converted.Range);

                                        // The explicit additive bucket stays wide until its double common subtotal. It
                                        // cancels an int.MinValue source with 1000 net added, which a saturated int
                                        // would turn into zero damage.
                                        var wideRaw = DamagePacket.Of(DamageType.Physical, 100, 100);
                                        int WideGet(string name) => name == "damage_+%" ? int.MinValue : 0;
                                        long wideAdditional = -(long)int.MinValue + 1000;
                                        var wideHit = PlayerHitDamage.Resolve(wideRaw, WideGet, new DamageConversion(),
                                            spell: false, projectile: true, additionalIncreased: wideAdditional);
                                        Assert.Equal(1100, wideHit[DamageType.Physical].Min, 9);
                                        var wideRows = AilmentPanelStats.Compute(new("main_hand", Spell: false), wideRaw,
                                            new DamageConversion(), WideGet, wideAdditional);
                                        const string wideBleedRow =
                                            "minimum_bleeding_damage_to_deal_per_minute_from_non_critical_main_hand_damage";
                                        Assert.Equal(46200, wideRows.Single(row => row.Stat == wideBleedRow).Value);
                                        var wideWeapon = new SkillCatalog.Unarmed(600, 100, 100, 100) { Melee = false };
                                        uint wideDamageKey = Program.Stats.Key("damage_+%")
                                            ?? throw new InvalidOperationException("missing damage_+% source key");
                                        var wideCharacter = new Dictionary<uint, int> { [wideDamageKey] = int.MinValue };
                                        var wideCapture = OrdinaryRangedShot.Capture(wideWeapon, wideCharacter, [],
                                            Program.Stats, 100, 0, 100, wideAdditional);
                                        Assert.Equal(1100, wideCapture.Hit[DamageType.Physical].Min, 9);

                                        double chargeMore = ChargeEffects.DamageMore(Get, spell: false);
                                        double rageMore = Math.Max(0, 100d + Get("attack_damage_+%_final_from_rage")) / 100d;
                                        double maximumMore = Math.Max(0,
                                            100d + Get(MaximumAttackDamage.FromAscendancy)) / 100d;
                                        double sourceMore = chargeMore * rageMore;
                                        double unblindedSourceMin = expectedRaw.Min * 2d * sourceMore;
                                        double unblindedSourceMax = expectedRaw.Max * 2d * sourceMore * maximumMore;
                                        double blindedSourceMin = expectedRaw.Min
                                            * (2d + rolledBiteDamage / 100d) * sourceMore;
                                        double blindedSourceMax = expectedRaw.Max
                                            * (2d + rolledBiteDamage / 100d) * sourceMore * maximumMore;
                                        Assert.True(chargeMore > 0 && rageMore > 0 && maximumMore > 0,
                                            $"invalid actual source factors: charge={chargeMore}, rage={rageMore}, "
                                            + $"maximum={maximumMore}");
                                        int unblindedFloorMin = (int)Math.Floor(unblindedSourceMin);
                                        int unblindedFloorMax = (int)Math.Floor(unblindedSourceMax);
                                        int blindedFloorMin = (int)Math.Floor(blindedSourceMin);
                                        int blindedFloorMax = (int)Math.Floor(blindedSourceMax);
                                        double wrongPostMoreMin = unblindedSourceMin * (1d + rolledBiteDamage / 100d);
                                        double wrongPostMoreMax = unblindedSourceMax * (1d + rolledBiteDamage / 100d);
                                        Assert.True(blindedFloorMin > unblindedFloorMax,
                                            $"expected contact intervals overlap: unblinded "
                                            + $"{unblindedSourceMin:0.###}-{unblindedSourceMax:0.###}, blinded "
                                            + $"{blindedSourceMin:0.###}-{blindedSourceMax:0.###}");
                                        int wrongPostMoreFloorMin = (int)Math.Floor(wrongPostMoreMin);
                                        int wrongPostMoreFloorMax = (int)Math.Floor(wrongPostMoreMax);
                                        Assert.True(blindedFloorMax < wrongPostMoreFloorMin
                                                || blindedFloorMin > wrongPostMoreFloorMax,
                                            $"blinded interval {blindedSourceMin:0.###}-{blindedSourceMax:0.###} overlaps "
                                            + $"wrong post-more {wrongPostMoreMin:0.###}-{wrongPostMoreMax:0.###}");

                                        string ordinaryDamageId = Assert.IsType<string>(
                                            OrdinaryRangedAttack.ProjectileId(Game.WeaponProfileFor(catConn, activeSet: true)));
                                        Assert.Equal(OrdinaryRangedAttack.Arrow, ordinaryDamageId);
                                        var aimAt = catConn.Walk.Position(Game.ProjectileWorldTime(catConn));
                                        var capturedDamageUse = Game.CaptureOrdinaryRangedProjectile(catConn,
                                            biteDamageSession, ((int)aimAt.X, (int)aimAt.Y + 10), ordinaryDamageId, 0,
                                            new SkillCriticalRoll(0), null);
                                        var capturedDamageShot = Assert.IsType<ProjectileOffense>(capturedDamageUse.Payload);
                                        Assert.Equal(unblindedSourceMin,
                                            capturedDamageShot.Hit[DamageType.Physical].Min, 9);
                                        Assert.Equal(unblindedSourceMax,
                                            capturedDamageShot.Hit[DamageType.Physical].Max, 9);
                                        Assert.Equal([DamageType.Physical],
                                            capturedDamageShot.Hit.Components.Select(component => component.Type));

                                        // Only contact controls are replaced. The captured Hit and every conditional
                                        // output field remain the actual producer snapshot for future consumers.
                                        ProjectileOffense ControlOrdinary(ProjectileOffense shot) => shot with
                                        {
                                            Accuracy = 100, CriticalChance = 0, CriticalMultiplier = 100,
                                            AccuracyProfile = new AttackAccuracyProfile(100, 0, 0, true),
                                            AlwaysHits = true, CriticalRoll = null, VsBlindedCriticalChance = null,
                                            VsProfaneCriticalChance = null, SecondFormCriticalChance = null,
                                            AreaHit = null, Penetration = default, ResistanceMods = default,
                                            ImpaleChance = 0, ImpaleSettings = null, CriticalImpaleSettings = null,
                                            Leech = new(DamagePacket.Empty, DamagePacket.Empty, DamagePacket.Empty),
                                            SkillStats = new Dictionary<uint, int>(), MaimChance = 0,
                                            FarShot = false, PointBlank = false, ReturnMultiplier = 1, HitPacket = false,
                                        };
                                        var controlledShot = ControlOrdinary(capturedDamageShot);
                                        Assert.Same(capturedDamageShot.Hit, controlledShot.Hit);
                                        var contactPlayer = Game.CharacterPlayerStats(catConn);
                                        var gambler = Game.GamblerFor(contactPlayer);
                                        Assert.Equal(0, gambler.DamageExtraRolls);
                                        Assert.Equal(0, gambler.TripleDamageChance);
                                        Assert.Equal(0, gambler.DoubleDamageChance);
                                        Assert.Equal(0, gambler.SpellDoubleDamageChance);
                                        Assert.True(gambler.DamageFactor > 0,
                                            $"gambler factor was {gambler.DamageFactor}");
                                        int ContactDamage(ProjectileEntity projectile, uint id, SpawnedMonster target,
                                            string label, int? expectedConditionalBonus = null)
                                        {
                                            using (new Game.PlayerStatScope(catConn, fresh: true))
                                            {
                                                var currentContactPlayer =
                                                    Game.CharacterPlayerStats(catConn);
                                                if (expectedConditionalBonus is int expected)
                                                    Assert.Equal(expected,
                                                        Stat(currentContactPlayer, biteDamageStat));
                                                Assert.Same(target, catConn.Monsters[id]);
                                                Assert.False(target.Dead);
                                                Assert.Equal(0, target.Effects.PhysicalReduction);
                                                Assert.Equal(0, target.Effects["base_damage_taken_+%"]);
                                                Assert.Equal(0, target.Effects["physical_damage_taken_+%"]);
                                                uint before = target.Life;
                                                long at = biteDamageSession.Runtime.Now;
                                                Assert.Equal(ProjectileHitResult.Hit, biteDamageSession.Hit(new ProjectileDamage(
                                                    projectile, biteDamageSession.ActorKey(id, target), at, label,
                                                    projectile.Position)));
                                                Assert.Same(target, catConn.Monsters[id]);
                                                if (expectedConditionalBonus is int expectedAfter)
                                                    Assert.Equal(expectedAfter, Stat(
                                                        Game.CharacterPlayerStats(catConn),
                                                        biteDamageStat));
                                                return checked((int)(before - target.Life));
                                            }
                                        }
                                        ProjectileEntity ReleaseWith(ProjectileUse use, ProjectileOffense offense)
                                        {
                                            long useId = biteDamageSession.Runtime.Release(use with
                                            {
                                                Payload = offense,
                                                Seed = 1,
                                            });
                                            return Assert.Single(biteDamageSession.Runtime.Projectiles,
                                                candidate => candidate.UseId == useId);
                                        }
                                        ProjectileEntity ReleaseControlled() => ReleaseWith(capturedDamageUse,
                                            controlledShot);

                                        var unblindedTarget = Probe(biteDamageControlId, "bite-damage-unblinded-control");
                                        Assert.False(Game.Blinded(catConn, biteDamageControlId));
                                        int unblindedDamage = ContactDamage(ReleaseControlled(), biteDamageControlId,
                                            unblindedTarget, "bite-damage-unblinded-control",
                                            expectedConditionalBonus: rolledBiteDamage);

                                        attackTarget.Life = attackTarget.MaxLife;
                                        attackTarget.Dead = false;
                                        Assert.True(Game.Blinded(catConn, attackBlindId));
                                        Assert.True(Game.ResourceEffectsCurrent(catConn, owner));
                                        Assert.True(owner.TargetBlind(attackBlindId, attackTarget)
                                            .Read(damageAt).Less > 0);
                                        int blindedDamage = ContactDamage(ReleaseControlled(), attackBlindId,
                                            attackTarget, "bite-damage-blinded-conditional",
                                            expectedConditionalBonus: rolledBiteDamage);
                                        string observed = $"raw={raw}, captured={capturedDamageShot.Hit}, "
                                            + $"chargeMore={chargeMore:0.######}, rageMore={rageMore:0.######}, "
                                            + $"maximumMore={maximumMore:0.######}, "
                                            + $"gamblerMore={gambler.DamageFactor:0.######}, "
                                            + $"itemRoll={rolledBiteDamage}, unblindedActual={unblindedDamage}, "
                                            + $"blindedActual={blindedDamage}, "
                                            + $"unblindedExpected={unblindedSourceMin:0.###}-"
                                            + $"{unblindedSourceMax:0.###}, "
                                            + $"blindedExpected={blindedSourceMin:0.###}-{blindedSourceMax:0.###}, "
                                            + $"floors={unblindedFloorMin}-{unblindedFloorMax} and "
                                            + $"{blindedFloorMin}-{blindedFloorMax}, wrongPostMore="
                                            + $"{wrongPostMoreMin:0.###}-{wrongPostMoreMax:0.###}";
                                        Assert.True(unblindedDamage >= unblindedFloorMin
                                                && unblindedDamage <= unblindedFloorMax,
                                            $"unblinded ordinary contact was outside its independent interval. {observed}");
                                        Assert.InRange(unblindedDamage, unblindedFloorMin, unblindedFloorMax);
                                        Assert.True(blindedDamage >= blindedFloorMin
                                                && blindedDamage <= blindedFloorMax,
                                            $"Farrul's Bite {biteDamageStat} is present in the actual ordinary "
                                            + $"capture, but blinded living target contact was outside the positive "
                                            + $"additive interval. {observed}");
                                        Assert.InRange(blindedDamage, blindedFloorMin, blindedFloorMax);

                                        // PREHIT/LIFECYCLE: the controlled accepted-hit Blind applies only after the
                                        // damage profile is selected. Poison chance is a labelled contact control; the
                                        // real Bite roll, raw ranges and DAMAGE profiles above remain untouched.
                                        BlindHitInput damagePoisonBlind = controlledShot.Blind with
                                        {
                                            GlobalChance = 100,
                                            AttackChance = 0,
                                            CriticalChance = 0,
                                            Seconds = .001,
                                        };
                                        var damagePoisonShot = controlledShot with { Blind = damagePoisonBlind };
                                        Assert.Same(capturedDamageShot.Hit, damagePoisonShot.Hit);
                                        Assert.Same(capturedDamageShot.VsBlinded, damagePoisonShot.VsBlinded);
                                        Assert.Equal(100, damagePoisonBlind.Chance(spell: false, critical: false));
                                        Assert.Equal(.001, damagePoisonBlind.Seconds, 9);

                                        var damagePoisonBaseline = capturedDamageShot.Poison;
                                        var damagePoisonPositive = Assert.IsType<ProjectileOffenseAlternate>(
                                            capturedDamageShot.VsBlinded).Poison;
                                        double damagePoisonFaster = Math.Max(0, 1
                                            + (Get("faster_poison_%")
                                                + Get("damaging_ailments_deal_damage_+%_faster")) / 100d);
                                        double damagePoisonChargeMore =
                                            ChargeEffects.DamageMore(Get, spell: false, ailment: true);
                                        int damagePoisonIncreased = Get("damage_+%") + Get("damage_over_time_+%")
                                            + Get("base_ailment_damage_+%") + Get("chaos_damage_+%")
                                            + Get("chaos_damage_over_time_+%") + Get("base_poison_damage_+%")
                                            + Get("physical_damage_+%");
                                        double damagePoisonMultiplier = Math.Max(0, 1
                                            + ((double)Get("dot_multiplier_+") + Get("chaos_dot_multiplier_+")
                                                + Get("ailment_dot_multiplier_+")
                                                + Get("poison_dot_multiplier_+")) / 100d);
                                        double damagePoisonMore = Math.Max(0,
                                            1 + Get("delirium_aura_damage_over_time_+%_final") / 100d)
                                            * Math.Max(0,
                                                1 + Get("herald_of_agony_poison_damage_+%_final") / 100d);
                                        double DamagePoisonPerSecond(double raw, int conditional) =>
                                            Math.Round(raw * Math.Max(0,
                                                    1 + (damagePoisonIncreased + conditional) / 100d)
                                                * damagePoisonMultiplier * damagePoisonMore * .3
                                                * damagePoisonChargeMore * 60) / 60d * damagePoisonFaster;
                                        double damagePoisonBaselineMin =
                                            DamagePoisonPerSecond(expectedRaw.Min, 0);
                                        double damagePoisonBaselineMax =
                                            DamagePoisonPerSecond(expectedRaw.Max, 0);
                                        double damagePoisonPositiveMin =
                                            DamagePoisonPerSecond(expectedRaw.Min, rolledBiteDamage);
                                        double damagePoisonPositiveMax =
                                            DamagePoisonPerSecond(expectedRaw.Max, rolledBiteDamage);
                                        Assert.Equal(100, damagePoisonBaseline.Chance);
                                        Assert.Equal(100, damagePoisonPositive.Chance);
                                        Assert.Equal(damagePoisonBaselineMin, damagePoisonBaseline.Min, 9);
                                        Assert.Equal(damagePoisonBaselineMax, damagePoisonBaseline.Max, 9);
                                        Assert.Equal(damagePoisonPositiveMin, damagePoisonPositive.Min, 9);
                                        Assert.Equal(damagePoisonPositiveMax, damagePoisonPositive.Max, 9);
                                        Assert.True(damagePoisonPositive.Min > damagePoisonBaseline.Max,
                                            $"Poison ranges overlap: baseline "
                                            + $"{damagePoisonBaseline.Min:0.###}-{damagePoisonBaseline.Max:0.###}, "
                                            + $"positive {damagePoisonPositive.Min:0.###}-"
                                            + $"{damagePoisonPositive.Max:0.###}");
                                        Assert.True(damagePoisonBaseline.Seconds > .002
                                            && damagePoisonPositive.Seconds > .002,
                                            "the expiry chronology requires poison to outlive the controlled Blind");

                                        var damagePoisonTarget =
                                            Probe(bitePoisonLifecycleId, "bite-prehit-poison-lifecycle");
                                        Assert.False(Game.Blinded(catConn, bitePoisonLifecycleId));
                                        Assert.Equal(0, damagePoisonTarget.Poisons.Stacks(damageAt));
                                        Assert.Equal(0, damagePoisonTarget.Poisons.TotalDps(damageAt));
                                        int ContactFrozenDamage(string label,
                                            int expectedConditionalBonus)
                                        {
                                            using (new Game.PlayerStatScope(catConn, fresh: true))
                                            {
                                                var currentContactPlayer =
                                                    Game.CharacterPlayerStats(catConn);
                                                Assert.Equal(expectedConditionalBonus,
                                                    Stat(currentContactPlayer, biteDamageStat));
                                                var projectile = ReleaseWith(capturedDamageUse,
                                                    damagePoisonShot);
                                                Assert.Same(damagePoisonTarget,
                                                    catConn.Monsters[bitePoisonLifecycleId]);
                                                Assert.False(damagePoisonTarget.Dead);
                                                Assert.Equal(0,
                                                    damagePoisonTarget.Effects.PhysicalReduction);
                                                Assert.Equal(0,
                                                    damagePoisonTarget.Effects["base_damage_taken_+%"]);
                                                Assert.Equal(0,
                                                    damagePoisonTarget.Effects["physical_damage_taken_+%"]);
                                                uint before = damagePoisonTarget.Life;
                                                Assert.Equal(ProjectileHitResult.Hit,
                                                    biteDamageSession.Hit(new ProjectileDamage(
                                                        projectile,
                                                        biteDamageSession.ActorKey(
                                                            bitePoisonLifecycleId,
                                                            damagePoisonTarget),
                                                        biteDamageSession.Runtime.Now, label,
                                                        projectile.Position)));
                                                Assert.Same(damagePoisonTarget,
                                                    catConn.Monsters[bitePoisonLifecycleId]);
                                                Assert.Equal(expectedConditionalBonus, Stat(
                                                    Game.CharacterPlayerStats(catConn),
                                                    biteDamageStat));
                                                return checked((int)(before
                                                    - damagePoisonTarget.Life));
                                            }
                                        }

                                        int damagePoisonFirstHit = ContactDamage(
                                            ReleaseWith(capturedDamageUse, damagePoisonShot),
                                            bitePoisonLifecycleId, damagePoisonTarget,
                                            "bite-poison-first-unblinded",
                                            expectedConditionalBonus: rolledBiteDamage);
                                        Assert.InRange(damagePoisonFirstHit, unblindedFloorMin, unblindedFloorMax);
                                        Assert.True(Game.Blinded(catConn, bitePoisonLifecycleId));
                                        Assert.Equal(1, damagePoisonTarget.Poisons.Stacks(damageAt));
                                        double damagePoisonFirstDps =
                                            damagePoisonTarget.Poisons.TotalDps(damageAt);
                                        Assert.InRange(damagePoisonFirstDps,
                                            damagePoisonBaseline.Min, damagePoisonBaseline.Max);
                                        Assert.True(damagePoisonFirstDps < damagePoisonPositive.Min);

                                        damagePoisonTarget.Life = damagePoisonTarget.MaxLife;
                                        double damagePoisonBeforeSecond =
                                            damagePoisonTarget.Poisons.TotalDps(damageAt);
                                        int damagePoisonSecondHit = ContactDamage(
                                            ReleaseWith(capturedDamageUse, damagePoisonShot),
                                            bitePoisonLifecycleId, damagePoisonTarget,
                                            "bite-poison-second-already-blinded",
                                            expectedConditionalBonus: rolledBiteDamage);
                                        Assert.InRange(damagePoisonSecondHit, blindedFloorMin, blindedFloorMax);
                                        Assert.True(Game.Blinded(catConn, bitePoisonLifecycleId));
                                        Assert.Equal(2, damagePoisonTarget.Poisons.Stacks(damageAt));
                                        double damagePoisonEarned =
                                            damagePoisonTarget.Poisons.TotalDps(damageAt);
                                        double damagePoisonSecondDelta =
                                            damagePoisonEarned - damagePoisonBeforeSecond;
                                        Assert.Equal(damagePoisonBeforeSecond + damagePoisonSecondDelta,
                                            damagePoisonEarned, 9);
                                        Assert.InRange(damagePoisonSecondDelta,
                                            damagePoisonPositive.Min, damagePoisonPositive.Max);
                                        Assert.True(damagePoisonSecondDelta > damagePoisonBaseline.Max);

                                        uint damagePoisonSourceKey =
                                            Program.Stats.Key(biteDamageStat)!.Value;
                                        bool damagePoisonSourceExisted =
                                            catConn.PlayerStats.TryGetValue(damagePoisonSourceKey,
                                                out int damagePoisonSourceValue);
                                        try
                                        {
                                            // Source-loss control: old positive captures and earned poison
                                            // remain frozen. The next already-Blind contact still selects positive.
                                            catConn.PlayerStats[damagePoisonSourceKey] = 0;
                                            using (new Game.PlayerStatScope(catConn, fresh: true))
                                                Assert.Equal(0, Stat(Game.CharacterPlayerStats(catConn),
                                                    biteDamageStat));
                                            Assert.Equal(damagePoisonEarned,
                                                damagePoisonTarget.Poisons.TotalDps(damageAt), 9);
                                            damagePoisonTarget.Life = damagePoisonTarget.MaxLife;
                                            int damagePoisonSourceLossHit = ContactFrozenDamage(
                                                "bite-poison-positive-after-source-loss",
                                                expectedConditionalBonus: 0);
                                            Assert.InRange(damagePoisonSourceLossHit,
                                                blindedFloorMin, blindedFloorMax);
                                            Assert.Equal(3,
                                                damagePoisonTarget.Poisons.Stacks(damageAt));
                                            double damagePoisonAfterSourceLoss =
                                                damagePoisonTarget.Poisons.TotalDps(damageAt);
                                            double damagePoisonSourceLossDelta =
                                                damagePoisonAfterSourceLoss - damagePoisonEarned;
                                            Assert.Equal(damagePoisonEarned + damagePoisonSourceLossDelta,
                                                damagePoisonAfterSourceLoss, 9);
                                            Assert.InRange(damagePoisonSourceLossDelta,
                                                damagePoisonPositive.Min, damagePoisonPositive.Max);
                                            Assert.True(damagePoisonAfterSourceLoss
                                                > damagePoisonEarned + damagePoisonBaseline.Max);

                                            // Bind a fresh EMPTY session already advanced 2 ms ahead, so old
                                            // projectiles do not advance and an already-past expiry cannot reverse.
                                            double damagePoisonFutureAt = damageAt + .002;
                                            long damagePoisonFutureNow =
                                                ProjectileRuntime.Time(damagePoisonFutureAt);
                                            Assert.True(damagePoisonFutureNow > damageAt
                                                * ProjectileRuntime.QuantaPerSecond);
                                            poisonFutureSession = new Game.ProjectileSession(
                                                "cat-bite-poison-future", catWire.Stream,
                                                catWire.Ciphers, catConn);
                                            Assert.Empty(poisonFutureSession.Runtime.Projectiles);
                                            poisonFutureSession.Runtime.AdvanceTo(damagePoisonFutureNow);
                                            Assert.Empty(poisonFutureSession.Runtime.Projectiles);
                                            catConn.ProjectileRuntime = poisonFutureSession;
                                            Assert.InRange(Game.ResourceEffectsTime(catConn),
                                                damagePoisonFutureAt
                                                    - 1d / ProjectileRuntime.QuantaPerSecond,
                                                damagePoisonFutureAt
                                                    + 1d / ProjectileRuntime.QuantaPerSecond);
                                            damagePoisonFutureAt = Game.ResourceEffectsTime(catConn);
                                            Assert.False(Game.Blinded(catConn,
                                                bitePoisonLifecycleId));
                                            Assert.Equal(3,
                                                damagePoisonTarget.Poisons.Stacks(
                                                    damagePoisonFutureAt));
                                            Assert.True(damagePoisonFutureAt
                                                < damagePoisonTarget.Poisons.Until);
                                            Assert.Equal(damagePoisonAfterSourceLoss,
                                                damagePoisonTarget.Poisons.TotalDps(
                                                    damagePoisonFutureAt), 9);

                                            // The next contact starts unblinded: its hit and newly applied
                                            // poison use baseline, even though its own Blind applies afterward.
                                            damagePoisonTarget.Life = damagePoisonTarget.MaxLife;
                                            int damagePoisonExpiredHit;
                                            using (new Game.PlayerStatScope(catConn, fresh: true))
                                            {
                                                var damagePoisonFutureContactPlayer =
                                                    Game.CharacterPlayerStats(catConn);
                                                Assert.Equal(0, Stat(
                                                    damagePoisonFutureContactPlayer,
                                                    biteDamageStat));
                                                long damagePoisonFutureUse =
                                                    poisonFutureSession.Runtime.Release(
                                                    capturedDamageUse with
                                                    {
                                                        Payload = damagePoisonShot,
                                                        Seed = 1,
                                                    });
                                                var damagePoisonFutureProjectile =
                                                    Assert.Single(poisonFutureSession.Runtime.Projectiles,
                                                        candidate =>
                                                            candidate.UseId == damagePoisonFutureUse);
                                                uint damagePoisonFutureLife =
                                                    damagePoisonTarget.Life;
                                                Assert.Equal(ProjectileHitResult.Hit,
                                                    poisonFutureSession.Hit(new ProjectileDamage(
                                                        damagePoisonFutureProjectile,
                                                        poisonFutureSession.ActorKey(
                                                            bitePoisonLifecycleId,
                                                            damagePoisonTarget),
                                                        poisonFutureSession.Runtime.Now,
                                                        "bite-poison-expired-contact",
                                                        damagePoisonFutureProjectile.Position)));
                                                Assert.Equal(0, Stat(
                                                    Game.CharacterPlayerStats(catConn),
                                                    biteDamageStat));
                                                damagePoisonExpiredHit = checked((int)(
                                                    damagePoisonFutureLife
                                                        - damagePoisonTarget.Life));
                                            }
                                            Assert.InRange(damagePoisonExpiredHit,
                                                unblindedFloorMin, unblindedFloorMax);
                                            Assert.True(Game.Blinded(catConn,
                                                bitePoisonLifecycleId));
                                            Assert.Equal(4,
                                                damagePoisonTarget.Poisons.Stacks(
                                                    damagePoisonFutureAt));
                                            double damagePoisonAfterExpiry =
                                                damagePoisonTarget.Poisons.TotalDps(
                                                    damagePoisonFutureAt);
                                            double damagePoisonExpiredDelta =
                                                damagePoisonAfterExpiry
                                                    - damagePoisonAfterSourceLoss;
                                            Assert.Equal(damagePoisonAfterSourceLoss
                                                + damagePoisonExpiredDelta,
                                                damagePoisonAfterExpiry, 9);
                                            Assert.InRange(damagePoisonExpiredDelta,
                                                damagePoisonBaseline.Min,
                                                damagePoisonBaseline.Max);
                                            Assert.True(damagePoisonExpiredDelta
                                                < damagePoisonPositive.Min);
                                        }
                                        finally
                                        {
                                            catConn.ProjectileRuntimeState = savedDamageRuntime;
                                            if (poisonFutureSession is not null)
                                                poisonFutureSession.Invalidate();
                                            if (damagePoisonSourceExisted)
                                                catConn.PlayerStats[damagePoisonSourceKey] =
                                                    damagePoisonSourceValue;
                                            else
                                                catConn.PlayerStats.Remove(damagePoisonSourceKey);
                                            using (new Game.PlayerStatScope(catConn,
                                                fresh: true))
                                                Assert.Equal(rolledBiteDamage,
                                                    Stat(Game.CharacterPlayerStats(catConn),
                                                        biteDamageStat));
                                        }

                                        // A real Fireball and Tornado Shot capture use the same current Bite source. Generic
                                        // 100%, area 100% and spell-added fire 100 are explicit fixture inputs; only contact
                                        // critical/accuracy/resistance controls are changed below.
                                        var damageGemCasterStats = Game.CharacterPlayerStats(catConn);
                                        int DamageGemGet(string name) => Stat(damageGemCasterStats, name);
                                        Assert.Equal(rolledBiteDamage, DamageGemGet(biteDamageStat));
                                        Assert.Equal(100, DamageGemGet("damage_+%"));
                                        Assert.Equal(100, DamageGemGet("area_damage_+%"));

                                        var damageGemFireballInputs = Assert.IsType<Game.SupportedHitInputs>(
                                            Game.SupportedHitInputsFor(catConn, fireball, area: false));
                                        Assert.Equal(SkillHitKind.Spell, damageGemFireballInputs.Plan.Kind);
                                        Assert.False(damageGemFireballInputs.Attack);
                                        Assert.True(damageGemFireballInputs.Projectile);
                                        var damageGemFireballPlanFire =
                                            Assert.Single(damageGemFireballInputs.Plan.SpellBase.Components);
                                        var damageGemFireballRawFire =
                                            Assert.Single(damageGemFireballInputs.Raw.Components);
                                        Assert.Equal(DamageType.Fire, damageGemFireballPlanFire.Type);
                                        Assert.Equal(DamageType.Fire, damageGemFireballRawFire.Type);
                                        double damageGemFireballEffectiveness = Game.AddedEffectiveness(catConn,
                                            fireballSource.Effect, fireballSource.Level);
                                        var damageGemExpectedRaw = damageGemFireballPlanFire.Range.Add(
                                            new DamageRange(100, 100).Scale(damageGemFireballEffectiveness));
                                        Assert.Equal(damageGemExpectedRaw, damageGemFireballRawFire.Range);
                                        var damageGemFireballConversion =
                                            Assert.Single(damageGemFireballInputs.Conversion.Trace(
                                                damageGemFireballInputs.Raw));
                                        Assert.Equal(DamageType.Fire, damageGemFireballConversion.Type);
                                        Assert.Equal(DamageTypeHistory.Fire, damageGemFireballConversion.History);
                                        Assert.Equal(damageGemExpectedRaw, damageGemFireballConversion.Range);
                                        double damageGemSpellChargeMore =
                                            ChargeEffects.DamageMore(DamageGemGet, spell: true);
                                        double damageGemSpellAuraMore =
                                            Math.Max(0, 1 + DamageGemGet(
                                                "spell_damage_aura_spell_damage_+%_final") / 100d)
                                            * Math.Max(0, 1 + DamageGemGet(
                                                "righteous_fire_spell_damage_+%_final") / 100d);
                                        Assert.Equal(1, damageGemSpellChargeMore, 9);
                                        Assert.Equal(1, damageGemSpellAuraMore, 9);
                                        var damageGemBlindedFireballRaw = damageGemExpectedRaw.Scale(
                                            (2d + rolledBiteDamage / 100d) * damageGemSpellChargeMore
                                            * damageGemSpellAuraMore);
                                        var damageGemBlindedAreaRaw = damageGemExpectedRaw.Scale(
                                            (3d + rolledBiteDamage / 100d) * damageGemSpellChargeMore
                                            * damageGemSpellAuraMore);
                                        var damageGemBaselineFireballRaw = damageGemExpectedRaw.Scale(
                                            2d * damageGemSpellChargeMore * damageGemSpellAuraMore);
                                        var damageGemBaselineAreaRaw = damageGemExpectedRaw.Scale(
                                            3d * damageGemSpellChargeMore * damageGemSpellAuraMore);

                                        var damageGemAim = catConn.Walk.Position(
                                            Game.ProjectileWorldTime(catConn));
                                        var capturedDamageGemUse = Game.CaptureGemProjectile(catConn,
                                            biteDamageSession, fireball, fireballSource,
                                            ((int)damageGemAim.X, (int)damageGemAim.Y), new SkillCriticalRoll(0));
                                        var capturedDamageGemShot =
                                            Assert.IsType<ProjectileOffense>(capturedDamageGemUse.Payload);
                                        Assert.True(capturedDamageGemShot.Spell);
                                        Assert.Equal(fireball, capturedDamageGemShot.SourceSkill);
                                        Assert.Equal(damageGemBaselineFireballRaw.Min,
                                            capturedDamageGemShot.Hit[DamageType.Fire].Min, 9);
                                        Assert.Equal(damageGemBaselineAreaRaw.Min,
                                            capturedDamageGemShot.AreaHit![DamageType.Fire].Min, 9);
                                        var damageGemPositiveHalf = Assert.IsType<ProjectileOffenseAlternate>(
                                            capturedDamageGemShot.VsBlinded);
                                        Assert.Equal(damageGemBlindedFireballRaw.Min,
                                            damageGemPositiveHalf.Hit[DamageType.Fire].Min, 9);
                                        Assert.Equal(damageGemBlindedAreaRaw.Min,
                                            damageGemPositiveHalf.AreaHit![DamageType.Fire].Min, 9);
                                        Assert.Equal(100, capturedDamageGemShot.Poison.Chance);
                                        Assert.Equal(100, damageGemPositiveHalf.Poison.Chance);
                                        double damageGemAilmentChargeMore =
                                            ChargeEffects.DamageMore(DamageGemGet, spell: true, ailment: true);
                                        Assert.Equal(1, damageGemAilmentChargeMore, 9);
                                        // AilmentPanelStats publishes a rounded per-minute integer. The independent
                                        // oracle applies that documented conversion, not a production helper.
                                        double DamageGemAilmentPerSecond(double perSecond) =>
                                            Math.Round(perSecond * 60) / 60;
                                        double damageGemBaselineIgnite = DamageGemAilmentPerSecond(
                                            damageGemExpectedRaw.Min * 2d * .9 * damageGemAilmentChargeMore);
                                        double damageGemPositiveIgnite = DamageGemAilmentPerSecond(
                                            damageGemExpectedRaw.Min * (2d + rolledBiteDamage / 100d) * .9
                                            * damageGemAilmentChargeMore);
                                        Assert.Equal(damageGemBaselineIgnite,
                                            capturedDamageGemShot.Ignite.Min, 9);
                                        Assert.Equal(damageGemPositiveIgnite,
                                            damageGemPositiveHalf.Ignite.Min, 9);
                                        Assert.Equal(0, capturedDamageGemShot.Poison.Min);
                                        Assert.Equal(0, damageGemPositiveHalf.Poison.Min);

                                        ProjectileOffense ControlGem(ProjectileOffense shot) => shot with
                                        {
                                            Accuracy = 100, CriticalChance = 0, CriticalMultiplier = 100,
                                            AccuracyProfile = null, AlwaysHits = true, CriticalRoll = null,
                                            VsBlindedCriticalChance = null, VsProfaneCriticalChance = null,
                                            SecondFormCriticalChance = null, Penetration = default,
                                            ResistanceMods = default, ImpaleChance = 0, ImpaleSettings = null,
                                            CriticalImpaleSettings = null,
                                            Leech = new(DamagePacket.Empty, DamagePacket.Empty, DamagePacket.Empty),
                                            MaimChance = 0, FarShot = false, PointBlank = false,
                                            ReturnMultiplier = 1, HitPacket = false,
                                        };
                                        var controlledDamageGemShot = ControlGem(capturedDamageGemShot);
                                        Assert.Same(capturedDamageGemShot.Hit, controlledDamageGemShot.Hit);
                                        Assert.Same(capturedDamageGemShot.AreaHit, controlledDamageGemShot.AreaHit);
                                        Assert.Same(capturedDamageGemShot.VsBlinded,
                                            controlledDamageGemShot.VsBlinded);
                                        Assert.Equal(1, gambler.DamageFactor, 9);

                                        (int Min, int Max) FloorRange(DamageRange range) =>
                                            ((int)Math.Floor(range.Min), (int)Math.Floor(range.Max));
                                        var damageGemBaselineDirect = FloorRange(damageGemBaselineFireballRaw);
                                        var damageGemPositiveDirect = FloorRange(damageGemBlindedFireballRaw);
                                        var damageGemBaselineArea = FloorRange(damageGemBaselineAreaRaw);
                                        var damageGemPositiveArea = FloorRange(damageGemBlindedAreaRaw);
                                        Assert.True(damageGemPositiveDirect.Min > damageGemBaselineDirect.Max);
                                        Assert.True(damageGemPositiveArea.Min > damageGemBaselineArea.Max);

                                        unblindedTarget.Life = unblindedTarget.MaxLife;
                                        int damageGemUnblindedDirect = ContactDamage(
                                            ReleaseWith(capturedDamageGemUse, controlledDamageGemShot),
                                            biteDamageControlId, unblindedTarget, "gem-fireball-unblinded-direct",
                                            expectedConditionalBonus: rolledBiteDamage);
                                        unblindedTarget.Life = unblindedTarget.MaxLife;
                                        var damageGemUnblindedAreaProjectile =
                                            ReleaseWith(capturedDamageGemUse, controlledDamageGemShot);
                                        var damageGemUnblindedAreaContact = new ProjectileDamage(
                                            damageGemUnblindedAreaProjectile,
                                            biteDamageSession.ActorKey(biteDamageControlId, unblindedTarget),
                                            biteDamageSession.Runtime.Now, "gem-fireball-unblinded-area",
                                            damageGemUnblindedAreaProjectile.Position, 1)
                                        { IsArea = true };
                                        uint damageGemAreaLifeBefore = unblindedTarget.Life;
                                        Assert.Equal(ProjectileHitResult.Hit,
                                            biteDamageSession.Hit(damageGemUnblindedAreaContact));
                                        int damageGemUnblindedArea = checked((int)(
                                            damageGemAreaLifeBefore - unblindedTarget.Life));

                                        attackTarget.Life = attackTarget.MaxLife;
                                        int damageGemBlindedDirect = ContactDamage(
                                            ReleaseWith(capturedDamageGemUse, controlledDamageGemShot),
                                            attackBlindId, attackTarget, "gem-fireball-blinded-direct",
                                            expectedConditionalBonus: rolledBiteDamage);
                                        attackTarget.Life = attackTarget.MaxLife;
                                        var damageGemBlindedAreaProjectile =
                                            ReleaseWith(capturedDamageGemUse, controlledDamageGemShot);
                                        var damageGemBlindedAreaContact = new ProjectileDamage(
                                            damageGemBlindedAreaProjectile,
                                            biteDamageSession.ActorKey(attackBlindId, attackTarget),
                                            biteDamageSession.Runtime.Now, "gem-fireball-blinded-area",
                                            damageGemBlindedAreaProjectile.Position, 1)
                                        { IsArea = true };
                                        uint damageGemBlindedAreaLifeBefore = attackTarget.Life;
                                        Assert.Equal(ProjectileHitResult.Hit,
                                            biteDamageSession.Hit(damageGemBlindedAreaContact));
                                        int damageGemBlindedArea = checked((int)(
                                            damageGemBlindedAreaLifeBefore - attackTarget.Life));
                                        Assert.InRange(damageGemUnblindedDirect,
                                            damageGemBaselineDirect.Min, damageGemBaselineDirect.Max);
                                        Assert.InRange(damageGemBlindedDirect,
                                            damageGemPositiveDirect.Min, damageGemPositiveDirect.Max);
                                        Assert.InRange(damageGemUnblindedArea,
                                            damageGemBaselineArea.Min, damageGemBaselineArea.Max);
                                        Assert.InRange(damageGemBlindedArea,
                                            damageGemPositiveArea.Min, damageGemPositiveArea.Max);

                                        // Tornado Shot is an attack capture. Its profile is asserted rather than flown: the
                                        // existing shared runtime has no unbound terrain/actor world for another volley.
                                        var damageGemAttack = Assert.Single(Game.GemSkillEffects(catConn), gem =>
                                            Game.SkillsFor(catConn).EffectId(gem.Effect) == "TornadoShot");
                                        var damageGemTornadoInputs = Assert.IsType<Game.SupportedHitInputs>(
                                            Game.SupportedHitInputsFor(catConn, damageGemAttack.Id, area: false));
                                        Assert.Equal(SkillHitKind.Attack, damageGemTornadoInputs.Plan.Kind);
                                        Assert.True(damageGemTornadoInputs.Attack);
                                        Assert.True(damageGemTornadoInputs.Projectile);
                                        var damageGemTornadoRaw =
                                            Assert.Single(damageGemTornadoInputs.Raw.Components);
                                        Assert.Equal(DamageType.Physical, damageGemTornadoRaw.Type);
                                        Assert.Single(damageGemTornadoInputs.Conversion.Trace(
                                            damageGemTornadoInputs.Raw));
                                        double damageGemAttackChargeMore =
                                            ChargeEffects.DamageMore(damageGemTornadoInputs.Get, spell: false);
                                        double damageGemAttackRageMore = Math.Max(0,
                                            100d + damageGemTornadoInputs.Get(
                                                "attack_damage_+%_final_from_rage")) / 100d;
                                        double damageGemAttackMaximumMore = Math.Max(0,
                                            100d + damageGemTornadoInputs.Get(
                                                MaximumAttackDamage.FromAscendancy)) / 100d;
                                        double damageGemAttackVolatilityMin = Math.Max(0,
                                            1 + damageGemTornadoInputs.Get(
                                                "minimum_attack_damage_+%_final_from_volatility_support") / 100d);
                                        double damageGemAttackVolatilityMax = Math.Max(0,
                                            1 + damageGemTornadoInputs.Get(
                                                "maximum_attack_damage_+%_final_from_volatility_support") / 100d);
                                        Assert.Equal(1, damageGemAttackChargeMore, 9);
                                        Assert.Equal(1, damageGemAttackRageMore, 9);
                                        Assert.Equal(1, damageGemAttackMaximumMore, 9);
                                        Assert.Equal(1, damageGemAttackVolatilityMin, 9);
                                        Assert.Equal(1, damageGemAttackVolatilityMax, 9);
                                        var damageGemTornadoAim = catConn.Walk.Position(
                                            Game.ProjectileWorldTime(catConn));
                                        var capturedDamageGemTornadoUse = Game.CaptureGemProjectile(catConn,
                                            biteDamageSession, damageGemAttack.Id, damageGemAttack,
                                            ((int)damageGemTornadoAim.X, (int)damageGemTornadoAim.Y),
                                            new SkillCriticalRoll(0));
                                        var capturedDamageGemTornado = Assert.IsType<ProjectileOffense>(
                                            capturedDamageGemTornadoUse.Payload);
                                        var damageGemTornadoPositive = Assert.IsType<ProjectileOffenseAlternate>(
                                            capturedDamageGemTornado.VsBlinded);
                                        var damageGemTornadoBaselineHit =
                                            damageGemTornadoRaw.Range.Scale(2d * damageGemAttackChargeMore
                                                * damageGemAttackRageMore * damageGemAttackVolatilityMin);
                                        var damageGemTornadoPositiveHit =
                                            damageGemTornadoRaw.Range.Scale(
                                                (2d + rolledBiteDamage / 100d) * damageGemAttackChargeMore
                                                * damageGemAttackRageMore * damageGemAttackVolatilityMin);
                                        var damageGemTornadoBaselineArea =
                                            damageGemTornadoRaw.Range.Scale(3d * damageGemAttackChargeMore
                                                * damageGemAttackRageMore * damageGemAttackVolatilityMin);
                                        var damageGemTornadoPositiveArea =
                                            damageGemTornadoRaw.Range.Scale(
                                                (3d + rolledBiteDamage / 100d) * damageGemAttackChargeMore
                                                * damageGemAttackRageMore * damageGemAttackVolatilityMin);
                                        double damageGemAttackAilmentChargeMore =
                                            ChargeEffects.DamageMore(damageGemTornadoInputs.Get,
                                                spell: false, ailment: true);
                                        Assert.Equal(1, damageGemAttackAilmentChargeMore, 9);
                                        Assert.Equal(damageGemTornadoBaselineHit.Min,
                                            capturedDamageGemTornado.Hit[DamageType.Physical].Min, 9);
                                        Assert.Equal(damageGemTornadoBaselineArea.Min,
                                            capturedDamageGemTornado.AreaHit![DamageType.Physical].Min, 9);
                                        Assert.Equal(damageGemTornadoPositiveHit.Min,
                                            damageGemTornadoPositive.Hit[DamageType.Physical].Min, 9);
                                        Assert.Equal(damageGemTornadoPositiveArea.Min,
                                            damageGemTornadoPositive.AreaHit![DamageType.Physical].Min, 9);
                                        Assert.Equal(DamageGemAilmentPerSecond(
                                                damageGemTornadoRaw.Range.Min * 2d * .7
                                                * damageGemAttackAilmentChargeMore),
                                            capturedDamageGemTornado.Bleed.Min, 9);
                                        Assert.Equal(DamageGemAilmentPerSecond(
                                                damageGemTornadoRaw.Range.Min
                                                    * (2d + rolledBiteDamage / 100d) * .7
                                                    * damageGemAttackAilmentChargeMore),
                                            damageGemTornadoPositive.Bleed.Min, 9);
                                        Assert.Equal(DamageGemAilmentPerSecond(
                                                damageGemTornadoRaw.Range.Min * 2d * .3
                                                * damageGemAttackAilmentChargeMore),
                                            capturedDamageGemTornado.Poison.Min, 9);
                                        Assert.Equal(DamageGemAilmentPerSecond(
                                                damageGemTornadoRaw.Range.Min
                                                    * (2d + rolledBiteDamage / 100d) * .3
                                                    * damageGemAttackAilmentChargeMore),
                                            damageGemTornadoPositive.Poison.Min, 9);

                                        // Frozen source control: keep the positive captures. A current-stat loss makes a
                                        // fresh capture explicit zero; restoring the exact override makes that zero stay
                                        // baseline even against the already-Blinded target.
                                        uint damageGemSourceKey =
                                            Program.Stats.Key(biteDamageStat)!.Value;
                                        bool damageGemOverrideExisted =
                                            catConn.PlayerStats.TryGetValue(damageGemSourceKey,
                                                out int damageGemOverrideValue);
                                        catConn.PlayerStats[damageGemSourceKey] = 0;
                                        int damageGemSourceLossStats;
                                        using (new Game.PlayerStatScope(catConn, fresh: true))
                                            damageGemSourceLossStats =
                                                Stat(Game.CharacterPlayerStats(catConn), biteDamageStat);
                                        Assert.Equal(0, damageGemSourceLossStats);

                                        attackTarget.Life = attackTarget.MaxLife;
                                        int damageGemFrozenPositiveOrdinary = ContactDamage(
                                            ReleaseControlled(), attackBlindId, attackTarget,
                                            "ordinary-frozen-positive-after-source-loss",
                                            expectedConditionalBonus: 0);
                                        attackTarget.Life = attackTarget.MaxLife;
                                        int damageGemFrozenPositiveSpell = ContactDamage(
                                            ReleaseWith(capturedDamageGemUse, controlledDamageGemShot),
                                            attackBlindId, attackTarget,
                                            "gem-frozen-positive-after-source-loss",
                                            expectedConditionalBonus: 0);
                                        Assert.InRange(damageGemFrozenPositiveOrdinary,
                                            blindedFloorMin, blindedFloorMax);
                                        Assert.InRange(damageGemFrozenPositiveSpell,
                                            damageGemPositiveDirect.Min, damageGemPositiveDirect.Max);

                                        ProjectileUse capturedDamageZeroUse;
                                        ProjectileOffense capturedDamageZeroShot;
                                        ProjectileUse capturedDamageGemZeroUse;
                                        ProjectileOffense capturedDamageGemZeroShot;
                                        using (new Game.PlayerStatScope(catConn, fresh: true))
                                        {
                                            var damageGemZeroStats = Game.CharacterPlayerStats(catConn);
                                            Assert.Equal(0, Stat(damageGemZeroStats, biteDamageStat));
                                            capturedDamageZeroUse = Game.CaptureOrdinaryRangedProjectile(catConn,
                                                biteDamageSession, ((int)aimAt.X, (int)aimAt.Y + 10),
                                                ordinaryDamageId, 0, new SkillCriticalRoll(0), null);
                                            capturedDamageZeroShot = Assert.IsType<ProjectileOffense>(
                                                capturedDamageZeroUse.Payload);
                                            Assert.Null(capturedDamageZeroShot.VsBlinded);
                                            capturedDamageGemZeroUse = Game.CaptureGemProjectile(catConn,
                                                biteDamageSession, fireball, fireballSource,
                                                ((int)damageGemAim.X, (int)damageGemAim.Y),
                                                new SkillCriticalRoll(0));
                                            capturedDamageGemZeroShot = Assert.IsType<ProjectileOffense>(
                                                capturedDamageGemZeroUse.Payload);
                                            Assert.Null(capturedDamageGemZeroShot.VsBlinded);
                                            Assert.Equal(damageGemBaselineFireballRaw.Min,
                                                capturedDamageGemZeroShot.Hit[DamageType.Fire].Min, 9);
                                            Assert.Equal(damageGemBaselineAreaRaw.Min,
                                                capturedDamageGemZeroShot.AreaHit![DamageType.Fire].Min, 9);
                                        }

                                        if (damageGemOverrideExisted)
                                            catConn.PlayerStats[damageGemSourceKey] = damageGemOverrideValue;
                                        else
                                            catConn.PlayerStats.Remove(damageGemSourceKey);
                                        using (new Game.PlayerStatScope(catConn, fresh: true))
                                            Assert.Equal(rolledBiteDamage,
                                                Stat(Game.CharacterPlayerStats(catConn), biteDamageStat));

                                        var controlledDamageZero = ControlOrdinary(capturedDamageZeroShot);
                                        var controlledDamageGemZero = ControlGem(capturedDamageGemZeroShot);
                                        Assert.Null(controlledDamageZero.VsBlinded);
                                        Assert.Null(controlledDamageGemZero.VsBlinded);
                                        attackTarget.Life = attackTarget.MaxLife;
                                        int damageGemFrozenZeroOrdinary = ContactDamage(
                                            ReleaseWith(capturedDamageZeroUse, controlledDamageZero),
                                            attackBlindId, attackTarget,
                                            "ordinary-frozen-zero-after-source-gain",
                                            expectedConditionalBonus: rolledBiteDamage);
                                        attackTarget.Life = attackTarget.MaxLife;
                                        int damageGemFrozenZeroSpell = ContactDamage(
                                            ReleaseWith(capturedDamageGemZeroUse, controlledDamageGemZero),
                                            attackBlindId, attackTarget,
                                            "gem-frozen-zero-after-source-gain",
                                            expectedConditionalBonus: rolledBiteDamage);
                                        Assert.InRange(damageGemFrozenZeroOrdinary,
                                            unblindedFloorMin, unblindedFloorMax);
                                        Assert.InRange(damageGemFrozenZeroSpell,
                                            damageGemBaselineDirect.Min, damageGemBaselineDirect.Max);
                                    }
                                }
                                finally
                                {
                                    catConn.PlayerStats.Clear();
                                    foreach (var (key, value) in savedPlayerStats)
                                        catConn.PlayerStats[key] = value;
                                    Assert.Equal(savedPlayerStats.Length, catConn.PlayerStats.Count);
                                    foreach (var (key, value) in savedPlayerStats)
                                        Assert.Equal(value, catConn.PlayerStats[key]);
                                    using (new Game.PlayerStatScope(catConn, fresh: true))
                                    {
                                        var restoredStats = Game.CharacterPlayerStats(catConn);
                                        Assert.Equal(rolledBiteDamage, Stat(restoredStats, biteDamageStat));
                                        Assert.Equal(1, Stat(restoredStats, "has_cat_aspect"));
                                        Assert.Equal(1, Stat(restoredStats, "has_cats_stealth"));
                                    }
                                    Assert.True(Game.ResourceEffectsCurrent(catConn, owner));
                                }
                            }
                            finally
                            {
                                catConn.ProjectileRuntimeState = savedDamageRuntime;
                                poisonFutureSession?.Invalidate();
                                biteDamageSession.Invalidate();
                            }
                        }
                    }
                    finally
                    {
                        lock (catConn.Monsters)
                            foreach (uint id in probeIds)
                                if (id != retainedBlindId || retainedBlindTarget is null) catConn.Monsters.Remove(id);
                    }

                    // Capture a real Fireball projectile while Bite qualifies, then hit after later source removal in the
                    // fixture's off lifecycle. The controlled critical and reaction seed isolate the captured Blind family.
                    blindProjectiles = new Game.ProjectileSession("cat-blind-projectile", catWire.Stream, catWire.Ciphers, catConn);
                    var projectileAim = catConn.Walk.Position(Game.ProjectileWorldTime(catConn));
                    var capturedBlindUse = Game.CaptureGemProjectile(catConn, blindProjectiles, fireball, fireballSource,
                        ((int)projectileAim.X, (int)projectileAim.Y), new SkillCriticalRoll(0));
                    var capturedBlind = Assert.IsType<ProjectileOffense>(capturedBlindUse.Payload).Blind;
                    Assert.Equal(rolledBiteBlind, capturedBlind.Chance(spell: true, critical: true));
                    Assert.Equal(0, capturedBlind.Chance(spell: true, critical: false));
                    Assert.Equal(4, capturedBlind.Seconds, 6);
                    Assert.Equal(0, capturedBlind.Effect);
                    frozenBlindUse = capturedBlindUse with
                    {
                        Payload = Assert.IsType<ProjectileOffense>(capturedBlindUse.Payload) with { CriticalChance = 10_000 },
                    };

                    // Exercise the ordinary ranged release through the production runtime scheduler. The caller's
                    // PlayerStatScope is the actual Cat phase snapshot; CaptureOrdinaryRangedProjectile must restore it
                    // after taking its own release snapshot.
                    async Task ScheduledOrdinaryAfter(Func<Task> boundary,
                        ReservationState.CatPhase expectedPhase, int expectedBlind)
                    {
                        var session = Assert.IsType<Game.ProjectileSession>(blindProjectiles);
                        string projectileId = Assert.IsType<string>(
                            OrdinaryRangedAttack.ProjectileId(Game.WeaponProfileFor(catConn, activeSet: true)));
                        Assert.Equal(OrdinaryRangedAttack.Arrow, projectileId);
                        var previousRuntime = catConn.ProjectileRuntimeState;
                        Assert.Same(blindClock, previousRuntime);
                        try
                        {
                            await boundary();
                            Assert.Same(blindClock, catConn.ProjectileRuntimeState);
                            Assert.False(catConn.Paused);
                            lock (catConn.Monsters)
                            {
                                long now = ProjectileRuntime.Time(Game.ResourceEffectsTime(catConn));
                                session.Runtime.AdvanceTo(now);
                                catConn.ProjectileRuntime = session;
                                session.Runtime.Schedule(now, () =>
                                {
                                    Assert.Equal(expectedPhase, catState.Cat);
                                    var at = catConn.Walk.Position(session.Runtime.Now
                                        / (double)ProjectileRuntime.QuantaPerSecond);
                                    var capturedUse = Game.CaptureOrdinaryRangedProjectile(catConn, session,
                                        ((int)at.X, (int)at.Y), projectileId, 0, new SkillCriticalRoll(0), null);
                                    var blind = Assert.IsType<ProjectileOffense>(capturedUse.Payload).Blind;
                                    Assert.Equal(expectedBlind, blind.Chance(spell: false, critical: true));
                                    Assert.Equal(0, blind.Chance(spell: false, critical: false));
                                });
                                Assert.Empty(session.Runtime.Projectiles);
                                session.Runtime.AdvanceTo(session.Runtime.Now);
                            }
                        }
                        finally
                        {
                            lock (catConn.Monsters) catConn.ProjectileRuntimeState = previousRuntime;
                            Assert.Same(blindClock, catConn.ProjectileRuntimeState);
                        }
                    }

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
                    List<byte[]> atStealthBoundary;
                    using (new Game.PlayerStatScope(catConn, fresh: true))
                    {
                        var primedScope = Game.BuildScopeOf(catConn)!;
                        _ = Game.FinalPlayerStats(catConn).ToList();
                        var primedCharacter = new Dictionary<uint, int>(primedScope.Stats);
                        var primedFinal = primedScope.Final!.ToArray();
                        Assert.Equal(1, Stat(primedCharacter, "has_cats_stealth"));
                        Assert.Equal(rolledBiteBlind,
                            Stat(primedCharacter, "%_chance_to_blind_on_critical_strike"));
                        schedule.At = stealthDeadline;
                        atStealthBoundary = await Capture(async () =>
                        {
                            await ScheduledOrdinaryAfter(
                                () => Game.ReservationSkillsTick("test", catWire.Stream, catWire.Ciphers,
                                    catConn, schedule.At),
                                ReservationState.CatPhase.Agility, 0);
                        });
                        var restoredScope = Game.BuildScopeOf(catConn);
                        Assert.Same(primedScope, restoredScope);
                        Assert.Equal(primedCharacter, restoredScope!.Stats);
                        Assert.Equal(primedFinal, restoredScope.Final);
                    }
                    Assert.Equal(ReservationState.CatPhase.Agility, catState.Cat);
                    Assert.Equal(104, catState.CatPhaseStartedAt, 3);
                    Assert.Equal(110, catState.CatPhaseDeadline, 3);
                    Assert.Equal(2, PhaseValues().Count);
                    Assert.Equal(1, Occurrences(atStealthBoundary,
                        InstanceEntities.RemoveBuff(Program.PlayerId, stealthInstance)));
                    Assert.Equal(1, Occurrences(atStealthBoundary, PhasePacket(catState.CatPhaseBuff, 6)));

                    // The earned Blind owns its deadline rather than the Cat phase. The actual Agility transition leaves
                    // it running, and repeated reads cannot extend it.
                    var earnedState = owner.TargetBlind(retainedBlindId, retainedBlindTarget!);
                    double earnedUntil = earnedState.Read(blindNow + 1.5).Until;
                    Assert.Equal(earnedUntil, earnedState.Read(blindNow + 2).Until);
                    Assert.Equal(80, Game.MonsterBlindRating(catConn, retainedBlindId, retainedBlindTarget!, 100,
                        blindNow + 2, stealthStats));

                    var agilityStats = Game.CharacterPlayerStats(catConn);
                    Assert.Equal(1, Stat(agilityStats, "has_cat_aspect"));
                    Assert.Equal(0, Stat(agilityStats, "has_cats_stealth"));
                    Assert.Equal(1, Stat(agilityStats, "has_cats_agility"));
                    CheckPounce(stealth: false);
                    Assert.True(Stat(agilityStats, "attack_speed_+%") >= 20);
                    Assert.True(Stat(agilityStats, "base_cast_speed_+%") >= 20);
                    Assert.True((int)Math.Round(Game.SupportedAttackSeconds(catConn, attackSource) * 1000) < attackMsStealth);
                    Assert.True(Game.CastMs(catConn, Game.SupportedSkill(catConn, fireball).Effect, fireball)!.Value < castMsOff);
                    Assert.Equal(0, PlayerDamageTaken.AvoidPct(DamageType.Physical,
                        name => Stat(agilityStats, name)));
                    Assert.True(Hit(avoidSeed)!.RawDamage > 0);
                    var agilityAim = catConn.Walk.Position(Game.ProjectileWorldTime(catConn));
                    var capturedAgilityUse = Game.CaptureGemProjectile(catConn, blindProjectiles, fireball, fireballSource,
                        ((int)agilityAim.X, (int)agilityAim.Y), new SkillCriticalRoll(0));
                    var agilityBlind = Assert.IsType<ProjectileOffense>(capturedAgilityUse.Payload).Blind;
                    Assert.Equal(0, agilityBlind.Chance(spell: true, critical: true));

                    double agilityDeadline = catState.CatPhaseDeadline;
                    schedule.At = agilityDeadline - .001;
                    await Game.ReservationSkillsTick("test", catWire.Stream, catWire.Ciphers, catConn, schedule.At);
                    Assert.Equal(ReservationState.CatPhase.Agility, catState.Cat);
                    List<byte[]> atAgilityBoundary;
                    using (new Game.PlayerStatScope(catConn, fresh: true))
                    {
                        var primedScope = Game.BuildScopeOf(catConn)!;
                        _ = Game.FinalPlayerStats(catConn).ToList();
                        var primedCharacter = new Dictionary<uint, int>(primedScope.Stats);
                        var primedFinal = primedScope.Final!.ToArray();
                        Assert.Equal(0, Stat(primedCharacter, "has_cats_stealth"));
                        Assert.Equal(1, Stat(primedCharacter, "has_cats_agility"));
                        Assert.Equal(0, Stat(primedCharacter, "%_chance_to_blind_on_critical_strike"));
                        schedule.At = agilityDeadline;
                        atAgilityBoundary = await Capture(async () =>
                        {
                            await ScheduledOrdinaryAfter(
                                () => Game.ReservationSkillsTick("test", catWire.Stream, catWire.Ciphers,
                                    catConn, schedule.At),
                                ReservationState.CatPhase.Stealth, rolledBiteBlind);
                        });
                        var restoredScope = Game.BuildScopeOf(catConn);
                        Assert.Same(primedScope, restoredScope);
                        Assert.Equal(primedCharacter, restoredScope!.Stats);
                        Assert.Equal(primedFinal, restoredScope.Final);
                    }
                    Assert.Equal(ReservationState.CatPhase.Stealth, catState.Cat);
                    Assert.Equal(10, catState.CatPhaseStartedAt - 100, 3);
                    Assert.Equal(1, Occurrences(atAgilityBoundary, PhasePacket(catState.CatPhaseBuff, 4)));

                    // A projectile captured in Agility keeps its zero Blind family even though its critical contact
                    // happens after the later Stealth transition and current Bite stats have changed.
                    const uint agilityBlindTargetId = 0xC470ACu;
                    var agilityBlindTarget = new SpawnedMonster("agility-release-stealth-contact",
                        new(catConn.Walk.Position(Game.ProjectileWorldTime(catConn)).X + 10,
                            catConn.Walk.Position(Game.ProjectileWorldTime(catConn)).Y, 0), 100, 8, 1_000_000)
                    {
                        Hostile = true,
                    };
                    var projectileSession = Assert.IsType<Game.ProjectileSession>(blindProjectiles);
                    var agilityUse = capturedAgilityUse with
                    {
                        Payload = Assert.IsType<ProjectileOffense>(capturedAgilityUse.Payload) with { CriticalChance = 10_000 },
                    };
                    lock (catConn.Monsters)
                    {
                        double projectileAt = Game.ResourceEffectsTime(catConn);
                        Assert.Equal(blindNow, projectileAt, 9);
                        projectileSession.Runtime.AdvanceTo(ProjectileRuntime.Time(projectileAt));
                        Assert.Same(blindClock, catConn.ProjectileRuntimeState);
                        Assert.Equal(ProjectileRuntime.Time(blindNow), projectileSession.Runtime.Now);
                        Assert.False(catConn.Paused);
                        catConn.Monsters[agilityBlindTargetId] = agilityBlindTarget;
                    }
                    (ProjectileEntity Projectile, long At) ReleaseActual(ProjectileUse use)
                    {
                        lock (catConn.Monsters)
                        {
                            long useId = projectileSession.Runtime.Release(use);
                            return (Assert.Single(projectileSession.Runtime.Projectiles,
                                projectile => projectile.UseId == useId), projectileSession.Runtime.Now);
                        }
                    }
                    long HitActual(ProjectileEntity projectile, uint id, SpawnedMonster target, string label)
                    {
                        lock (catConn.Monsters)
                        {
                            Assert.Same(target, catConn.Monsters[id]);
                            long at = projectileSession.Runtime.Now;
                            Assert.Same(blindClock, catConn.ProjectileRuntimeState);
                            Assert.Equal(ProjectileRuntime.Time(blindNow), at);
                            Assert.False(catConn.Paused);
                            Assert.Equal(ProjectileHitResult.Hit, projectileSession.Hit(new ProjectileDamage(projectile,
                                projectileSession.ActorKey(id, target), at, label, projectile.Position)));
                            Assert.Same(target, catConn.Monsters[id]);
                            Assert.False(target.Dead);
                            Assert.NotEqual(0u, target.Life);
                            return at;
                        }
                    }
                    lock (catConn.Monsters)
                    {
                        for (int seed = 1; seed <= 128; seed++)
                        {
                            var actual = ReleaseActual(agilityUse with { Seed = seed });
                            _ = HitActual(actual.Projectile, agilityBlindTargetId, agilityBlindTarget,
                                "agility-release-stealth-contact");
                            Assert.False(Game.Blinded(catConn, agilityBlindTargetId));
                            Assert.DoesNotContain(agilityBlindTargetId, owner.Targets.Keys);
                        }
                        catConn.Monsters.Remove(agilityBlindTargetId);
                    }

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

                    // Source removal happens only after the original phase assertions. Once the Cat root is off, taking
                    // Bite away cannot remove the already-earned Blind, and a projectile keeps the Blind input captured
                    // while Bite qualified even though later duration/effect values differ.
                    lock (catConn.Monsters)
                    {
                        const double quantum = 1d / ProjectileRuntime.QuantaPerSecond;
                        Assert.Same(blindClock, catConn.ProjectileRuntimeState);
                        Assert.False(catConn.Paused);
                        double retainedNow = Game.ResourceEffectsTime(catConn);
                        Assert.True(retainedNow < blindNow + 4,
                            $"the retained Blind's lifecycle clock {retainedNow:0.000000} already reached "
                            + $"its deadline {blindNow + 4:0.000000}");
                        Assert.InRange(retainedNow, blindNow - quantum, blindNow + quantum);
                        var retainedStateAfterOff = owner.TargetBlind(retainedBlindId, retainedBlindTarget!);
                        Assert.InRange(retainedStateAfterOff.Read(retainedNow).Until,
                            blindNow + 4 - quantum, blindNow + 4 + quantum);
                        double retainedUntilAfterOff = retainedStateAfterOff.Read(blindNow + 1.5).Until;
                        Assert.Equal(blindNow + 4, retainedUntilAfterOff, 6);
                        Assert.Equal(retainedUntilAfterOff, retainedStateAfterOff.Read(blindNow + 2).Until);
                    }
                    var removedBite = inventory.Remove(PlayerInventories.Helmet, bitePlacement.Id);
                    Assert.NotNull(removedBite);
                    Dictionary<uint, int> noBiteStats;
                    lock (catConn.Monsters)
                    {
                        Game.EnsureOwnAuraPools(catConn);
                        noBiteStats = Game.CharacterPlayerStats(catConn);
                        Assert.Equal(0, Stat(noBiteStats, "%_chance_to_blind_on_critical_strike"));
                        Assert.True(Game.Blinded(catConn, retainedBlindId));
                        var replacementAt = retainedBlindTarget!.Walk.Position(blindNow + 2);
                        var replacement = new SpawnedMonster("replacement-earned-blind",
                            new(replacementAt.X, replacementAt.Y, 1), 100, 8, 1_000_000) { Hostile = true };
                        catConn.Monsters[retainedBlindId] = replacement;
                        Assert.False(Game.Blinded(catConn, retainedBlindId));
                        catConn.Monsters[retainedBlindId] = retainedBlindTarget;
                    }

                    const uint frozenBlindTargetId = 0xC470A7u;
                    var frozenBlindTarget = new SpawnedMonster("frozen-bite-blind",
                        new(catConn.Walk.Position(Game.ProjectileWorldTime(catConn)).X + 10,
                            catConn.Walk.Position(Game.ProjectileWorldTime(catConn)).Y, 0), 100, 8, 1_000_000)
                    {
                        Hostile = true,
                    };
                    var frozenUse = Assert.IsType<GameServer.Core.Projectiles.ProjectileUse>(frozenBlindUse);
                    uint blindDurationKey = Program.Stats.Key("blind_duration_+%")!.Value;
                    uint blindEffectKey = Program.Stats.Key("blind_effect_+%")!.Value;
                    lock (catConn.Monsters)
                    {
                        catConn.PlayerStats[blindDurationKey] = 100;
                        catConn.PlayerStats[blindEffectKey] = 50;
                        catConn.Monsters[frozenBlindTargetId] = frozenBlindTarget;
                        ProjectileEntity? frozenProjectile = null;
                        long frozenAt = 0;
                        for (int seed = 1; seed <= 128 && frozenProjectile is null; seed++)
                        {
                            var actual = ReleaseActual(frozenUse with { Seed = seed });
                            long at = HitActual(actual.Projectile, frozenBlindTargetId, frozenBlindTarget, "frozen-blind");
                            if (Game.Blinded(catConn, frozenBlindTargetId))
                            {
                                frozenProjectile = actual.Projectile;
                                frozenAt = at;
                            }
                        }
                        Assert.True(frozenProjectile is not null,
                            $"the captured {rolledBiteBlind}% Bite Blind did not trigger in 128 critical contacts");
                        Assert.True(Game.Blinded(catConn, frozenBlindTargetId));
                        Assert.Equal(80, Game.MonsterBlindRating(catConn, frozenBlindTargetId, frozenBlindTarget,
                            100, frozenAt / (double)ProjectileRuntime.QuantaPerSecond, noBiteStats));
                        var frozenState = owner.TargetBlind(frozenBlindTargetId, frozenBlindTarget);
                        double frozenBlindAt = frozenAt / (double)ProjectileRuntime.QuantaPerSecond;
                        var frozenBlindValue = frozenState.Read(frozenBlindAt);
                        Assert.Equal(20, frozenBlindValue.Less);
                        Assert.Equal(frozenBlindAt + 4, frozenBlindValue.Until, 9);
                        catConn.PlayerStats.Remove(blindDurationKey);
                        catConn.PlayerStats.Remove(blindEffectKey);
                        catConn.Monsters.Remove(frozenBlindTargetId);
                        catConn.Monsters.Remove(retainedBlindId);
                    }
                    bitePlacement = inventory.Add(PlayerInventories.Helmet, 0, 0, removedBite.Item);
                    Game.ReconcileOwnAuras(catConn, validateSkills: true);
                    catGrant = Assert.Single(Game.WornGrantedSkillsOf(catConn),
                        grant => grant.EffectId == "AspectOfTheCat");
                    catSource = Assert.Single(Game.GemSkillEffects(catConn), gem =>
                        Game.SkillsFor(catConn).EffectId(gem.Effect) == "AspectOfTheCat");

                    furPlacement = inventory.Add(PlayerInventories.BodyArmour, 0, 0, fur);
                    Game.EnsureOwnAuraPools(catConn);
                    var freeCatCost = Game.SupportedReservationCost(catConn, catSource.Id)!.Value;
                    Assert.Equal(0, freeCatCost.ManaPercent);
                    Assert.Equal(0, freeCatCost.ManaFlat);
                    catConn.PlayerMana = 123;
                    List<byte[]> chargedInitial;
                    using (new Game.PlayerStatScope(catConn, fresh: true))
                    {
                        var primedScope = Game.BuildScopeOf(catConn)!;
                        _ = Game.FinalPlayerStats(catConn).ToList();
                        var primedCharacter = primedScope.Stats;
                        var primedFinal = primedScope.Final;
                        Assert.Equal(0, Stat(primedCharacter, "current_frenzy_charges"));
                        Assert.Equal(0, Stat(primedCharacter, "current_power_charges"));
                        Assert.Equal(0, Stat(primedCharacter, "virtual_phase_through_objects"));
                        chargedInitial = await Capture(async () => Assert.True(await Game.ToggleAura("test",
                            catWire.Stream, catWire.Ciphers, catConn, catSource.Id, catSource.Effect, catSource.Name,
                            freeCatCost, catGrant.Level, 0)));
                        var restoredScope = Game.BuildScopeOf(catConn);
                        Assert.Same(primedScope, restoredScope);
                        Assert.Equal(primedCharacter, restoredScope!.Stats);
                        Assert.Equal(primedFinal, restoredScope.Final);
                    }
                    AssertPublished(chargedInitial, ("has_cat_aspect", 1), ("has_cats_stealth", 1),
                        ("has_cats_agility", 0), ("current_frenzy_charges", 4), ("current_power_charges", 3),
                        ("phase_through_objects", 1), ("virtual_phase_through_objects", 1));
                    Assert.Equal(123u, catConn.PlayerMana);
                    Assert.Equal(ReservationState.CatPhase.Stealth, catState.Cat);
                    Assert.Equal(6, catState.CatPhaseDeadline - catState.CatPhaseStartedAt, 3);
                    Assert.Equal(1036, catConn.PresentedAuras[catSource.Id].BuffRow);
                    int maxFrenzy = chargeSession.Limits(ChargeType.Frenzy).Maximum;
                    int maxPower = chargeSession.Limits(ChargeType.Power).Maximum;
                    Assert.Equal(4, maxFrenzy);
                    Assert.Equal(3, maxPower);
                    Assert.Equal(maxFrenzy, chargeSession.Effective(ChargeType.Frenzy));
                    Assert.Equal(maxPower, chargeSession.Effective(ChargeType.Power));
                    Assert.Equal(0, chargeSession.Effective(ChargeType.Endurance));
                    double frenzyExpiry = chargeSession.State.ExpiresAt(ChargeType.Frenzy);
                    double powerExpiry = chargeSession.State.ExpiresAt(ChargeType.Power);
                    Assert.Equal(schedule.At + chargeSession.Limits(ChargeType.Frenzy).DurationMs / 1000.0,
                        frenzyExpiry, 3);
                    Assert.Equal(schedule.At + chargeSession.Limits(ChargeType.Power).DurationMs / 1000.0,
                        powerExpiry, 3);
                    var chargedStats = Game.CharacterPlayerStats(catConn);
                    Assert.Equal(maxFrenzy, Stat(chargedStats, "current_frenzy_charges"));
                    Assert.Equal(maxPower, Stat(chargedStats, "current_power_charges"));
                    Assert.Equal(1, Stat(chargedStats, "phase_through_objects"));
                    Assert.Equal(1, Stat(chargedStats, "virtual_phase_through_objects"));
                    Assert.True(catConn.IsPhasing);
                    Assert.True(Game.SupportedAttackSeconds(catConn, attackSource) < attackMsStealth);
                    Assert.True(Game.SupportedCriticalChance(catConn, attackSource) > attackChanceOff);

                    catConn.PlayerSent = true;
                    catConn.PlayerLife = catConn.Pools.Life;
                    catConn.Walk.Teleport(51, 50, schedule.At);
                    var blockingMonster = new SpawnedMonster("cat-phase-blocker", new(50, 50, 8), 60, 2, 23);
                    lock (catConn.Monsters) catConn.Monsters[0x4050] = blockingMonster;
                    var blockingBody = new ClientPathfinder.Blocker(50, 50, LivingCollision.Size(blockingMonster));
                    lock (catConn.Monsters)
                    {
                        using var frame = catConn.BeginMonsterBlockerFrame(schedule.At);
                        Assert.DoesNotContain(blockingBody, frame.ForPlayer);
                    }

                    await Game.ReservationSkillsTick("test", catWire.Stream, catWire.Ciphers, catConn, schedule.At);
                    await Game.SendOwnAuraChanges("cat-core", catWire.Stream, catWire.Ciphers, catConn);
                    Assert.Equal(frenzyExpiry, chargeSession.State.ExpiresAt(ChargeType.Frenzy));
                    Assert.Equal(powerExpiry, chargeSession.State.ExpiresAt(ChargeType.Power));

                    inventory.Remove(PlayerInventories.Boots, darkrayPlacement.Id);
                    Game.EnsureOwnAuraPools(catConn);
                    chargeSession.Refresh(schedule.At);
                    Assert.True(catConn.IsPhasing);
                    Assert.Equal(3, chargeSession.Limits(ChargeType.Frenzy).Maximum);
                    Assert.Equal(3, chargeSession.State.Held(ChargeType.Frenzy));
                    Assert.Equal(maxPower, chargeSession.Effective(ChargeType.Power));
                    Assert.Equal(frenzyExpiry, chargeSession.State.ExpiresAt(ChargeType.Frenzy));
                    Assert.Equal(powerExpiry, chargeSession.State.ExpiresAt(ChargeType.Power));

                    darkrayPlacement = inventory.Add(PlayerInventories.Boots, 0, 0, darkray);
                    Game.EnsureOwnAuraPools(catConn);
                    chargeSession.Refresh(schedule.At);
                    Assert.True(catConn.IsPhasing);
                    Assert.Equal(maxFrenzy, chargeSession.Limits(ChargeType.Frenzy).Maximum);
                    Assert.Equal(3, chargeSession.State.Held(ChargeType.Frenzy));
                    Assert.Equal(frenzyExpiry, chargeSession.State.ExpiresAt(ChargeType.Frenzy));
                    Assert.Equal(powerExpiry, chargeSession.State.ExpiresAt(ChargeType.Power));

                    // Replacing the root owner at the exact same game time is a new Stealth entry, while a
                    // replacement that keeps it qualified grants the current maximum and preserves both charge types.
                    Assert.True(await Game.ToggleAura("test", catWire.Stream, catWire.Ciphers, catConn, catSource.Id,
                        catSource.Effect, catSource.Name, freeCatCost, catGrant.Level, 0));
                    Assert.Equal(3, chargeSession.State.Held(ChargeType.Frenzy));
                    Assert.True(await Game.ToggleAura("test", catWire.Stream, catWire.Ciphers, catConn, catSource.Id,
                        catSource.Effect, catSource.Name, freeCatCost, catGrant.Level, 0));
                    Assert.Equal(schedule.At, catState.CatPhaseStartedAt, 3);
                    Assert.Equal(maxFrenzy, chargeSession.State.Held(ChargeType.Frenzy));
                    Assert.Equal(maxPower, chargeSession.State.Held(ChargeType.Power));

                    catConn.Phasing = true;
                    catConn.PhasingUntil = 0;
                    Assert.True(catConn.Phasing);
                    Assert.True(catConn.IsPhasing);
                    catConn.Phasing = false;
                    catConn.PhasingUntil = catConn.Clock.Elapsed.TotalSeconds + 100;
                    Assert.False(catConn.Phasing);
                    Assert.True(catConn.IsPhasing);

                    var fixedBody = new ClientPathfinder.Blocker(60, 50, 1);
                    catConn.SetBlocker(0x4051, 60, 50, 1);
                    lock (catConn.Monsters)
                    {
                        using var frame = catConn.BeginMonsterBlockerFrame(schedule.At);
                        Assert.DoesNotContain(blockingBody, frame.ForPlayer);
                        Assert.Contains(fixedBody, frame.ForPlayer);
                    }

                    double chargedStealthDeadline = catState.CatPhaseDeadline;
                    schedule.At = chargedStealthDeadline;
                    await Game.ReservationSkillsTick("test", catWire.Stream, catWire.Ciphers, catConn, schedule.At);
                    chargeSession.Refresh(schedule.At);
                    Assert.Equal(ReservationState.CatPhase.Agility, catState.Cat);
                    Assert.False(catState.CatPhasing);
                    Assert.False(catConn.Phasing);
                    Assert.True(catConn.IsPhasing);
                    Assert.Equal(0, chargeSession.State.Held(ChargeType.Frenzy));
                    Assert.Equal(maxPower, chargeSession.State.Held(ChargeType.Power));
                    Assert.Equal(schedule.At + 4, chargeSession.State.ExpiresAt(ChargeType.Power), 3);

                    double lastStealthBeforeDurationReprice = catState.CatLastStealthStartedAt;
                    double powerExpiryBeforeDurationReprice = chargeSession.State.ExpiresAt(ChargeType.Power);
                    uint stealthDurationKey = Program.Stats.Key("cats_stealth_duration_ms_+")!.Value;
                    catConn.PlayerStats[stealthDurationKey] = -2000;
                    await Game.ReservationSkillsTick("test", catWire.Stream, catWire.Ciphers, catConn, schedule.At);
                    chargeSession.Refresh(schedule.At);
                    Assert.Equal(ReservationState.CatPhase.Agility, catState.Cat);
                    Assert.Equal(lastStealthBeforeDurationReprice, catState.CatLastStealthStartedAt, 3);
                    Assert.Equal(0, chargeSession.State.Held(ChargeType.Frenzy));
                    Assert.Equal(powerExpiryBeforeDurationReprice,
                        chargeSession.State.ExpiresAt(ChargeType.Power), 3);
                    catConn.PlayerStats.Remove(stealthDurationKey);

                    lock (catConn.Monsters)
                    {
                        using var frame = catConn.BeginMonsterBlockerFrame(schedule.At);
                        Assert.DoesNotContain(blockingBody, frame.ForPlayer);
                        Assert.Contains(fixedBody, frame.ForPlayer);
                    }
                    lock (catConn.Monsters)
                    {
                        double actualNow = catConn.Clock.Elapsed.TotalSeconds;
                        Assert.True(Game.ItemConditionFlags(catConn, actualNow)["_while_phasing"]);
                        catConn.PhasingUntil = 0;
                        Assert.False(catConn.IsPhasing);
                        Assert.False(Game.ItemConditionFlags(catConn, actualNow)["_while_phasing"]);
                    }
                    lock (catConn.Monsters)
                    {
                        using var frame = catConn.BeginMonsterBlockerFrame(schedule.At);
                        Assert.Contains(blockingBody, frame.ForPlayer);
                        Assert.Contains(fixedBody, frame.ForPlayer);
                    }

                    double chargedAgilityDeadline = catState.CatPhaseDeadline;
                    schedule.At = chargedAgilityDeadline;
                    await Game.ReservationSkillsTick("test", catWire.Stream, catWire.Ciphers, catConn, schedule.At);
                    Assert.Equal(ReservationState.CatPhase.Stealth, catState.Cat);
                    Assert.True(catState.CatPhasing);
                    Assert.True(catConn.IsPhasing);
                    Assert.Equal(maxFrenzy, chargeSession.Effective(ChargeType.Frenzy));
                    Assert.Equal(maxPower, chargeSession.Effective(ChargeType.Power));
                    Assert.Equal(schedule.At + chargeSession.Limits(ChargeType.Frenzy).DurationMs / 1000.0,
                        chargeSession.State.ExpiresAt(ChargeType.Frenzy), 3);
                    Assert.Equal(schedule.At + chargeSession.Limits(ChargeType.Power).DurationMs / 1000.0,
                        chargeSession.State.ExpiresAt(ChargeType.Power), 3);
                    lock (catConn.Monsters)
                    {
                        using var frame = catConn.BeginMonsterBlockerFrame(schedule.At);
                        Assert.DoesNotContain(blockingBody, frame.ForPlayer);
                        Assert.Contains(fixedBody, frame.ForPlayer);
                    }

                    double regrantedFrenzyExpiry = chargeSession.State.ExpiresAt(ChargeType.Frenzy);
                    inventory.Remove(PlayerInventories.BodyArmour, furPlacement.Id);
                    Game.EnsureOwnAuraPools(catConn);
                    Assert.False(catConn.IsPhasing);
                    var withoutFurStats = Game.CharacterPlayerStats(catConn);
                    Assert.Equal(0, Stat(withoutFurStats, "phase_through_objects"));
                    Assert.Equal(0, Stat(withoutFurStats, "virtual_phase_through_objects"));
                    Assert.Equal(maxFrenzy, chargeSession.Effective(ChargeType.Frenzy));
                    Assert.Equal(maxPower, chargeSession.Effective(ChargeType.Power));
                    furPlacement = inventory.Add(PlayerInventories.BodyArmour, 0, 0, fur);
                    Game.EnsureOwnAuraPools(catConn);
                    Assert.True(catConn.IsPhasing);
                    Assert.Equal(regrantedFrenzyExpiry, chargeSession.State.ExpiresAt(ChargeType.Frenzy));
                    Assert.Equal(maxFrenzy, chargeSession.State.Held(ChargeType.Frenzy));
                    Assert.Equal(maxPower, chargeSession.State.Held(ChargeType.Power));

                    // A bounded catch-up from the most recent missed Stealth cannot truncate an independently earned
                    // later charge, and a charge whose own duration ended cannot be resurrected by that old entry.
                    chargeSession.Refresh(1135);
                    var catchupSources = Game.CharacterStatSources(catConn).Stats;
                    int ChargeStat(string name) => Program.Stats.Key(name) is uint key
                        ? catConn.PlayerStats.GetValueOrDefault(key, catchupSources.GetValueOrDefault(key)) : 0;
                    chargeSession.Observe(ChargeStat);
                    _ = chargeSession.Admit(1135,
                        [new ChargeEvent.Grant(ChargeType.Frenzy, 1, "independent later gain")],
                        ChargeStat, () => Game.WeaponProfileFor(catConn), true, new Random(504));
                    Assert.Equal(1, chargeSession.State.Held(ChargeType.Frenzy));
                    double laterExpiry = chargeSession.State.ExpiresAt(ChargeType.Frenzy);
                    Assert.True(laterExpiry > 1135);

                    schedule.At = 1135.5;
                    await Game.ReservationSkillsTick("test", catWire.Stream, catWire.Ciphers, catConn, schedule.At);
                    Assert.Equal(ReservationState.CatPhase.Agility, catState.Cat);
                    Assert.Equal(1125, catState.CatLastStealthStartedAt, 3);
                    Assert.Equal(1, chargeSession.State.Held(ChargeType.Frenzy));
                    Assert.Equal(laterExpiry, chargeSession.State.ExpiresAt(ChargeType.Frenzy));
                    Assert.Equal(0, chargeSession.State.Held(ChargeType.Power));

                    schedule.At = 1137;
                    await Game.ReservationSkillsTick("test", catWire.Stream, catWire.Ciphers, catConn, schedule.At);
                    Assert.Equal(ReservationState.CatPhase.Stealth, catState.Cat);
                    Assert.Equal(1137, catState.CatLastStealthStartedAt, 3);
                    Assert.Equal(maxFrenzy, chargeSession.State.Held(ChargeType.Frenzy));
                    Assert.Equal(maxPower, chargeSession.State.Held(ChargeType.Power));
                    Assert.Equal(1137 + chargeSession.Limits(ChargeType.Frenzy).DurationMs / 1000.0,
                        chargeSession.State.ExpiresAt(ChargeType.Frenzy), 3);
                    Assert.Equal(1137 + chargeSession.Limits(ChargeType.Power).DurationMs / 1000.0,
                        chargeSession.State.ExpiresAt(ChargeType.Power), 3);

                    inventory.Remove(PlayerInventories.BodyArmour, furPlacement.Id);
                    Game.EnsureOwnAuraPools(catConn);
                    var repriced = catConn.Auras[catSource.Id];
                    Assert.Equal(2500, repriced.ManaPercent);
                    Assert.Equal(123u, catConn.PlayerMana);
                    Assert.Equal(0, repriced.LifePercent);

                    var savedCatIntent = CharacterSaves.Load(character)!.ActiveAuras!.ToArray();
                    int earnedFrenzy = chargeSession.State.Held(ChargeType.Frenzy);
                    int earnedPower = chargeSession.State.Held(ChargeType.Power);
                    List<byte[]> catOff;
                    using (new Game.PlayerStatScope(catConn, fresh: true))
                    {
                        var primedScope = Game.BuildScopeOf(catConn)!;
                        _ = Game.FinalPlayerStats(catConn).ToList();
                        var primedCharacter = primedScope.Stats;
                        var primedFinal = primedScope.Final;
                        Assert.Equal(1, Stat(primedCharacter, "has_cat_aspect"));
                        catOff = await Capture(async () => Assert.True(await Game.ToggleAura("test", catWire.Stream,
                            catWire.Ciphers, catConn, catSource.Id, catSource.Effect, catSource.Name, catCost,
                            catGrant.Level, 0)));
                        var restoredScope = Game.BuildScopeOf(catConn);
                        Assert.Same(primedScope, restoredScope);
                        Assert.Equal(primedCharacter, restoredScope!.Stats);
                        Assert.Equal(primedFinal, restoredScope.Final);
                    }
                    AssertPublished(catOff, ("has_cat_aspect", 0), ("has_cats_stealth", 0),
                        ("has_cats_agility", 0), ("phase_through_objects", 0),
                        ("virtual_phase_through_objects", 0), ("current_frenzy_charges", earnedFrenzy),
                        ("current_power_charges", earnedPower));
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
                    if (catConnection is not null) catConnection.ProjectileRuntimeState = priorBlindRuntime;
                    blindProjectiles?.Invalidate();
                    blindClock?.Invalidate();
                    if (restoredConnection is not null) restoredConnection.Closed = true;
                    if (catConnection is not null) catConnection.Closed = true;
                    Game.AreaUpdateContext.Value = previousContext;
                    PlayerInventories.Forget(character);
                }
            }
            await CatCoreFixture();
            SiphoningFixture(conn, lifetime);
            await CatCryFixture(conn, lifetime);
            await AvianAspectFixture(conn, lifetime);
            await AvianSharingFixture(conn, lifetime);
            await TwisterFixture(conn, lifetime);
            await CrabAspectFixture(conn, lifetime);
            await SelfVulnerabilityFixture(conn, lifetime);
            await MadnessFixture(conn, lifetime);
            await SpiderAspectFixture(conn, lifetime);
            DefaultMeleeBiteProfileFixture();
            await RealAreaSkillHitBiteDamageFixture();
            await SacrificePrepaidConsumerFixture();
            await RealProjectileSacrificePaidProducerFixture();
            await TempestShieldBiteConsumerFixture();
            await FootstepBiteDamageFixture();

            void DefaultMeleeBiteProfileFixture()
            {
                string character = "DefaultMeleeBite" + Guid.NewGuid().ToString("N");
                const byte witchRow = 1;
                PlayerInventories.SetClass(character, witchRow);
                CharacterSaves.Write(character, new(new Dictionary<byte, List<CharacterSaves.SavedPlacement>>(),
                    Experience: CharacterStats.ExperienceFor(100)));
                var schedule = new CatClockSchedule { At = 300 };
                var updates = new PlayerUpdatesState(lifetime, schedule);
                var previousContext = Game.AreaUpdateContext.Value;
                Game.AreaUpdateContext.Value = updates;
                using var meleeWire = new Wire();
                ClientState? meleeConnection = null;
                try
                {
                    meleeConnection = new ClientState(conn.Layout, 0xC471, 1)
                    {
                        Character = character, CharacterId = Guid.NewGuid(), Account = "default-melee-bite-account",
                        League = "Standard", LeagueForm = "Standard", ClassRow = witchRow, Instance = new(),
                        PlayerSent = true, PlayerObjectCreated = true, PlayerUpdatesState = updates,
                        Experience = CharacterStats.ExperienceFor(100), LeagueModule = conn.LeagueModule,
                        Wire = ("default-melee-bite", meleeWire.Stream, meleeWire.Ciphers), Pools = new(100, 1, 1, 0, 0),
                    };
                    var meleeConn = meleeConnection;
                    Assert.Empty(meleeConn.PlayerStats);

                    var inventory = Game.ItemsOf(meleeConn);
                    foreach (byte row in new byte[] { PlayerInventories.Weapon1, PlayerInventories.Offhand1,
                                 PlayerInventories.Helmet, 5, 6, 7 })
                        foreach (var old in inventory.Items(row)) inventory.Remove(row, old.Id);

                    var sword = ItemCatalog.Create(Program.Items.Find(
                        "Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1")!);
                    var swordPlacement = inventory.Add(PlayerInventories.Weapon1, 0, 0, sword);
                    var astramentisPlacement = inventory.Add(5, 0, 0,
                        new UniqueCatalog(Program.Items).Create("Astramentis", new Random(504))!);
                    var firstRingPlacement = inventory.Add(6, 0, 0,
                        new UniqueCatalog(Program.Items).Create("Le Heup of All", new Random(504))!);
                    var secondRingPlacement = inventory.Add(7, 0, 0,
                        new UniqueCatalog(Program.Items).Create("Le Heup of All", new Random(505))!);
                    var bite = new UniqueCatalog(Program.Items).Create("Farrul's Bite", new Random(504))!;
                    var bitePlacement = inventory.Add(PlayerInventories.Helmet, 0, 0, bite);
                    meleeConn.Pools = Game.PoolsFor(meleeConn, 100);
                    meleeConn.PlayerLife = meleeConn.Pools.Life;
                    meleeConn.PlayerMana = meleeConn.Pools.Mana;
                    Game.ReconcileOwnAuras(meleeConn, validateSkills: true);
                    Assert.Empty(meleeConn.PlayerStats);
                    Assert.Equal(100, meleeConn.Pools.Level);
                    Assert.False(Game.IsInactive(meleeConn, swordPlacement.Id),
                        "the actual OneHandSword1 must meet its real requirements");
                    Assert.False(Game.IsInactive(meleeConn, astramentisPlacement.Id));
                    Assert.False(Game.IsInactive(meleeConn, firstRingPlacement.Id));
                    Assert.False(Game.IsInactive(meleeConn, secondRingPlacement.Id));
                    Assert.False(Game.IsInactive(meleeConn, bitePlacement.Id));
                    Assert.DoesNotContain(meleeConn.Auras.Values,
                        aura => aura.EffectId == "AspectOfTheCat");

                    const string biteDamageStat = "hit_and_ailment_damage_+%_vs_blinded_enemies";
                    var biteDamageMod = Assert.Single(bite.Mods, mod =>
                        Program.Items.Mods.ByHash(mod.Hash)?.Id == "DamageAgainstBlindedEnemiesUnique__1");
                    Assert.Equal(biteDamageStat,
                        Assert.Single(Program.Items.Mods.ByHash(biteDamageMod.Hash)!.Stats).Id);
                    int rolledBiteDamage = Assert.Single(biteDamageMod.Values);
                    Assert.InRange(rolledBiteDamage, 40, 50);
                    Assert.Equal(40, rolledBiteDamage);

                    void Zero(string name)
                    {
                        if (Program.Stats.Key(name) is uint key) meleeConn.PlayerStats[key] = 0;
                    }
                    void Override(string name, int value)
                    {
                        uint key = Program.Stats.Key(name)
                            ?? throw new InvalidOperationException("missing required stat " + name);
                        meleeConn.PlayerStats[key] = value;
                    }
                    foreach (string name in new[]
                    {
                        "attack_damage_+%", "physical_damage_+%", "fire_damage_+%", "cold_damage_+%",
                        "lightning_damage_+%", "chaos_damage_+%", "elemental_damage_+%", "melee_damage_+%",
                        "melee_physical_damage_+%", "area_damage_+%", "projectile_damage_+%",
                        "damage_over_time_+%", "base_ailment_damage_+%", "physical_damage_over_time_+%",
                        "fire_damage_over_time_+%", "chaos_damage_over_time_+%", "bleeding_damage_+%",
                        "burn_damage_+%", "ignite_damage_+%", "base_poison_damage_+%",
                        "damage_+%_while_leeching", "attack_damage_+%_while_leeching",
                        "damage_+%_while_life_leeching", "damage_+%_while_mana_leeching",
                        "damage_+%_while_es_leeching", "physical_damage_+%_while_life_leeching",
                        "damage_+%_on_full_energy_shield", "is_life_leeching", "is_mana_leeching",
                        "is_es_leeching", "on_full_energy_shield", "base_extra_damage_rolls",
                        "base_chance_to_deal_triple_damage_%", "chance_to_deal_double_damage_%",
                        "spell_chance_to_deal_double_damage_%", "attack_damage_+%_final_from_rage",
                        "dot_multiplier_+", "physical_dot_multiplier_+", "fire_dot_multiplier_+",
                        "chaos_dot_multiplier_+", "ailment_dot_multiplier_+", "bleeding_dot_multiplier_+",
                        "ignite_dot_multiplier_+", "poison_dot_multiplier_+", "critical_ailment_dot_multiplier_+",
                        "faster_bleed_%", "faster_burn_%", "faster_poison_%",
                        "damaging_ailments_deal_damage_+%_faster", "delirium_aura_damage_over_time_+%_final",
                        "herald_of_agony_poison_damage_+%_final", "herald_of_purity_physical_damage_+%_final",
                        "chance_to_freeze_shock_ignite_%", "chance_to_ignite_%",
                        "cannot_cause_bleeding", "cannot_poison_enemies", "never_damaging_ailments",
                    })
                        Zero(name);
                    foreach (string targetType in new[] { "fire", "cold", "lightning", "chaos" })
                        foreach (string prefix in new[] { "", "base_", "skill_", "attack_" })
                        {
                            Zero($"{prefix}physical_damage_%_to_convert_to_{targetType}");
                            Zero($"{prefix}physical_damage_%_to_add_as_{targetType}");
                        }
                    Override("damage_+%", 100);
                    Override("attack_minimum_added_physical_damage", 100);
                    Override("attack_maximum_added_physical_damage", 100);
                    Override("physical_damage_%_to_convert_to_fire", 50);
                    Override("chance_to_poison_on_hit_with_attacks_%", 100);
                    Override("bleed_on_hit_with_attacks_%", 100);
                    Override("bleed_on_crit_%_with_attacks", 100);
                    Override("base_chance_to_ignite_%", 100);

                    int Stat(IReadOnlyDictionary<uint, int> stats, string name) =>
                        stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
                    int attackEffect = Game.SkillEffectId(meleeConn, SkillCatalog.FirstSkillId) is { } attackId
                        ? Game.SkillsFor(meleeConn).EffectRow(attackId) : -1;
                    Assert.True(attackEffect >= 0);

                    Game.DefaultMeleePrehitProfile? TryProfile(uint id, SpawnedMonster target, int expectedBonus)
                    {
                        lock (meleeConn.Monsters)
                        using (new Game.PlayerStatScope(meleeConn, fresh: true))
                        {
                            var player = Game.CharacterPlayerStats(meleeConn);
                            Assert.Equal(expectedBonus, Stat(player, biteDamageStat));
                            Assert.Equal(0, Stat(player, "has_cat_aspect"));
                            var weapon = Game.WeaponFor(meleeConn);
                            Assert.True(weapon.Melee);
                            Assert.True(Game.WeaponProfileFor(meleeConn, activeSet: true).Sword);
                            var own = Game.SkillsFor(meleeConn).EffectStats(attackEffect, 1)
                                .ToDictionary(pair => pair.Item1, pair => pair.Item2);
                            var conversion = DamageStats.Conversion(player,
                                own.Select(pair => (pair.Key, pair.Value)), Program.Stats, attack: true);
                            return Game.DefaultMeleePrehitProfileLocked(meleeConn, id, target, player, weapon,
                                conversion, own,
                                Game.ResourceEffectsTime(meleeConn));
                        }
                    }
                    Game.DefaultMeleePrehitProfile Profile(uint id, SpawnedMonster target, int expectedBonus) =>
                        Assert.IsType<Game.DefaultMeleePrehitProfile>(TryProfile(id, target, expectedBonus));
                    void AssertAdditive(MonsterIgnites.Settings baseline, MonsterIgnites.Settings positive,
                        string label)
                    {
                        Assert.Equal(baseline.Chance, positive.Chance);
                        Assert.Equal(baseline.Seconds, positive.Seconds, 9);
                        Assert.InRange(positive.Min, baseline.Min * 1.2 - 1d / 30, baseline.Min * 1.2 + 1d / 30);
                        Assert.InRange(positive.Max, baseline.Max * 1.2 - 1d / 30, baseline.Max * 1.2 + 1d / 30);
                        Assert.True(positive.Min > baseline.Max,
                            $"{label} positive range {positive.Min:0.###}-{positive.Max:0.###} did not exceed "
                            + $"baseline {baseline.Min:0.###}-{baseline.Max:0.###}");
                    }
                    void AssertAdditivePoison(MonsterPoisons.Settings baseline, MonsterPoisons.Settings positive)
                    {
                        Assert.Equal(baseline.Chance, positive.Chance);
                        Assert.Equal(baseline.Seconds, positive.Seconds, 9);
                        Assert.InRange(positive.Min, baseline.Min * 1.2 - 1d / 30, baseline.Min * 1.2 + 1d / 30);
                        Assert.InRange(positive.Max, baseline.Max * 1.2 - 1d / 30, baseline.Max * 1.2 + 1d / 30);
                        Assert.True(positive.Min > baseline.Max,
                            $"poison positive range {positive.Min:0.###}-{positive.Max:0.###} did not exceed "
                            + $"baseline {baseline.Min:0.###}-{baseline.Max:0.###}");
                    }

                    using (new Game.PlayerStatScope(meleeConn, fresh: true))
                    {
                        var player = Game.CharacterPlayerStats(meleeConn);
                        int Get(string name) => Stat(player, name);
                        Assert.Equal(40, Get(biteDamageStat));
                        var weapon = Game.WeaponFor(meleeConn);
                        var localPhysical = weapon.LocalDamage?[DamageType.Physical]
                            ?? new DamageRange(weapon.MinDamage, weapon.MaxDamage);
                        var expectedRaw = localPhysical.Add(new DamageRange(100, 100));
                        var raw = SupportedAttackDamage.Raw(weapon, Get);
                        Assert.Equal(expectedRaw, Assert.Single(raw.Components).Range);
                        var own = Game.SkillsFor(meleeConn).EffectStats(attackEffect, 1);
                        var conversion = DamageStats.Conversion(player, own, Program.Stats, attack: true);
                        var paths = conversion.Trace(raw).ToArray();
                        Assert.Equal(2, paths.Length);
                        Assert.Equal(DamageType.Physical, paths[0].Type);
                        Assert.Equal(DamageType.Fire, paths[1].Type);
                        Assert.Equal(expectedRaw.Min * .5, paths[0].Range.Min, 9);
                        Assert.Equal(expectedRaw.Max * .5, paths[0].Range.Max, 9);
                        Assert.Equal(expectedRaw.Min * .5, paths[1].Range.Min, 9);
                        Assert.Equal(expectedRaw.Max * .5, paths[1].Range.Max, 9);
                    }

                    const uint baselineId = 0xC471A1u, blindedId = 0xC471A2u, immuneId = 0xC471A3u;
                    const uint lifecycleId = 0xC471A4u, friendlyId = 0xC471A5u, deadId = 0xC471A6u;
                    const uint staleId = 0xC471A7u, unregisteredId = 0xC471A8u;
                    SpawnedMonster Probe(uint id, string label, bool dead = false, bool immune = false,
                        bool hostile = true)
                    {
                        var probe = new SpawnedMonster(label, new(100, 100, 1), 100, 8, 1_000_000)
                        {
                            Level = 100, Hostile = hostile, Dead = dead, Life = dead ? 0u : 1_000_000u,
                        };
                        if (immune)
                            probe.Mods = MonsterModSet.None with
                            {
                                Effects = new MonsterModEffects(new Dictionary<string, int>
                                {
                                    ["cannot_be_blinded"] = 1,
                                }),
                            };
                        if (id != unregisteredId) meleeConn.Monsters[id] = probe;
                        return probe;
                    }
                    lock (meleeConn.Monsters)
                    {
                        var baselineTarget = Probe(baselineId, "default-melee-bite-unblinded");
                        var blindedTarget = Probe(blindedId, "default-melee-bite-owned-blind");
                        var immuneTarget = Probe(immuneId, "default-melee-bite-blind-immune", immune: true);
                        var lifecycleTarget = Probe(lifecycleId, "default-melee-bite-prehit-lifecycle");
                        var friendlyTarget = Probe(friendlyId, "default-melee-bite-friendly", hostile: false);
                        var deadTarget = Probe(deadId, "default-melee-bite-dead", dead: true);
                        var staleTarget = Probe(staleId, "default-melee-bite-stale");
                        var staleReplacement = Probe(staleId, "default-melee-bite-stale-replacement");
                        var unregisteredTarget = Probe(unregisteredId, "default-melee-bite-unregistered");
                        double now = Game.ResourceEffectsTime(meleeConn);
                        Assert.True(Game.InflictMonsterBlind(meleeConn, blindedId, blindedTarget, now,
                            (100, 0, 4), new Random(1)));
                        Assert.True(Game.Blinded(meleeConn, blindedId));
                        Assert.False(Game.InflictMonsterBlind(meleeConn, immuneId, immuneTarget, now,
                            (100, 0, 4), new Random(1)));
                        Assert.False(Game.Blinded(meleeConn, immuneId));

                        var baseline = Profile(baselineId, baselineTarget, rolledBiteDamage);
                        Assert.False(Game.Blinded(meleeConn, baselineId));
                        var positive = Profile(blindedId, blindedTarget, rolledBiteDamage);
                        Assert.True(positive.Hit.MinTotal > baseline.Hit.MaxTotal,
                            $"Farrul's Bite {biteDamageStat}={rolledBiteDamage} was present on the unblinded "
                            + $"source, and the separate target was Blinded, but default-melee hit "
                            + $"{positive.Hit.MinTotal}-{positive.Hit.MaxTotal} did not exceed baseline "
                            + $"{baseline.Hit.MinTotal}-{baseline.Hit.MaxTotal}");
                        Assert.Equal(baseline.Hit.MinTotal * 1.2, positive.Hit.MinTotal, 6);
                        Assert.Equal(baseline.Hit.MaxTotal * 1.2, positive.Hit.MaxTotal, 6);
                        AssertAdditive(baseline.Ignite, positive.Ignite, "ignite");
                        AssertAdditive(baseline.Bleed, positive.Bleed, "bleed");
                        AssertAdditivePoison(baseline.Poison, positive.Poison);

                        var immune = Profile(immuneId, immuneTarget, rolledBiteDamage);
                        Assert.Equal(baseline.Hit.MinTotal, immune.Hit.MinTotal, 9);
                        Assert.Equal(baseline.Hit.MaxTotal, immune.Hit.MaxTotal, 9);
                        Assert.Equal(baseline.Ignite, immune.Ignite);
                        Assert.Equal(baseline.Bleed, immune.Bleed);
                        Assert.Equal(baseline.Poison, immune.Poison);

                        Assert.Null(TryProfile(friendlyId, friendlyTarget, rolledBiteDamage));
                        Assert.Null(TryProfile(deadId, deadTarget, rolledBiteDamage));
                        Assert.Null(TryProfile(staleId, staleTarget, rolledBiteDamage));
                        Assert.Same(staleReplacement, meleeConn.Monsters[staleId]);
                        Assert.Null(TryProfile(unregisteredId, unregisteredTarget, rolledBiteDamage));
                        Assert.DoesNotContain(unregisteredId, meleeConn.Monsters.Keys);
                        Assert.Equal(1_000_000u, friendlyTarget.Life);
                        Assert.Equal(0u, deadTarget.Life);
                        Assert.Equal(1_000_000u, staleTarget.Life);
                        Assert.Equal(1_000_000u, unregisteredTarget.Life);

                        // The profile exists before the contact's own Blind/ailment side effects. It remains immutable,
                        // while the next profile on the now-Blinded exact target selects the conditional additive values.
                        var frozen = Profile(lifecycleId, lifecycleTarget, rolledBiteDamage);
                        Assert.False(Game.Blinded(meleeConn, lifecycleId));
                        var player = Game.CharacterPlayerStats(meleeConn);
                        var controlledBlind = new BlindHitInput(100, 0, 100, 0, 4);
                        Game.AilmentsOnHitLocked(meleeConn, lifecycleId, lifecycleTarget,
                            DamagePacket.Of(DamageType.Physical, 100, 100), spell: false, critical: true,
                            criticalMultiplier: 100, player, null, now, new Random(1),
                            poison: frozen.Poison, blind: controlledBlind);
                        Assert.True(Game.Blinded(meleeConn, lifecycleId));
                        Assert.NotNull(Game.IgniteOnHitLocked(meleeConn, lifecycleId, lifecycleTarget,
                            frozen.Ignite, critical: true, now));
                        Assert.NotNull(Game.BleedOnHitLocked(meleeConn, lifecycleId, lifecycleTarget,
                            frozen.Bleed, critical: true, now));
                        Assert.Equal(baseline.Hit.MinTotal, frozen.Hit.MinTotal, 9);
                        Assert.Equal(baseline.Hit.MaxTotal, frozen.Hit.MaxTotal, 9);
                        Assert.Equal(baseline.Ignite, frozen.Ignite);
                        Assert.Equal(baseline.Bleed, frozen.Bleed);
                        Assert.Equal(baseline.Poison, frozen.Poison);
                        double poisonBefore = lifecycleTarget.Poisons.TotalDps(now);
                        double igniteBefore = lifecycleTarget.Ignites.PerSecond(now);
                        double bleedBefore = lifecycleTarget.Bleeds.PerSecond(now);
                        Assert.InRange(poisonBefore, baseline.Poison.Min, baseline.Poison.Max);
                        Assert.InRange(igniteBefore, baseline.Ignite.Min, baseline.Ignite.Max);
                        Assert.InRange(bleedBefore, baseline.Bleed.Min, baseline.Bleed.Max);
                        Assert.True(poisonBefore < positive.Poison.Min
                            && igniteBefore < positive.Ignite.Min && bleedBefore < positive.Bleed.Min);

                        var afterFirstBlind = Profile(lifecycleId, lifecycleTarget, rolledBiteDamage);
                        Assert.True(afterFirstBlind.Hit.MinTotal > frozen.Hit.MaxTotal);
                        AssertAdditive(frozen.Ignite, afterFirstBlind.Ignite, "post-Blind ignite");
                        AssertAdditive(frozen.Bleed, afterFirstBlind.Bleed, "post-Blind bleed");
                        AssertAdditivePoison(frozen.Poison, afterFirstBlind.Poison);

                        var removedBite = inventory.Remove(PlayerInventories.Helmet, bitePlacement.Id);
                        Assert.NotNull(removedBite);
                        Game.ReconcileOwnAuras(meleeConn, validateSkills: true);
                        Assert.True(Game.Blinded(meleeConn, lifecycleId));
                        var noSource = Profile(lifecycleId, lifecycleTarget, expectedBonus: 0);
                        Assert.Equal(baseline.Hit.MinTotal, noSource.Hit.MinTotal, 9);
                        Assert.Equal(baseline.Hit.MaxTotal, noSource.Hit.MaxTotal, 9);
                        Assert.Equal(baseline.Ignite, noSource.Ignite);
                        Assert.Equal(baseline.Bleed, noSource.Bleed);
                        Assert.Equal(baseline.Poison, noSource.Poison);
                        Assert.Equal(poisonBefore, lifecycleTarget.Poisons.TotalDps(now), 9);
                        Assert.Equal(igniteBefore, lifecycleTarget.Ignites.PerSecond(now), 9);
                        Assert.Equal(bleedBefore, lifecycleTarget.Bleeds.PerSecond(now), 9);
                        bitePlacement = inventory.Add(PlayerInventories.Helmet, 0, 0, removedBite.Item);
                        Game.ReconcileOwnAuras(meleeConn, validateSkills: true);
                        Assert.True(Game.Blinded(meleeConn, lifecycleId));
                    }
                }
                finally
                {
                    if (meleeConnection is not null) meleeConnection.Closed = true;
                    Game.AreaUpdateContext.Value = previousContext;
                    PlayerInventories.Forget(character);
                }
            }

            async Task RealAreaSkillHitBiteDamageFixture()
            {
                string character = "AreaSkillHitBite" + Guid.NewGuid().ToString("N");
                const byte witchRow = 1;
                PlayerInventories.SetClass(character, witchRow);
                CharacterSaves.Write(character, new(new Dictionary<byte, List<CharacterSaves.SavedPlacement>>(),
                    Experience: CharacterStats.ExperienceFor(100)));
                var updates = new PlayerUpdatesState(lifetime, new CatClockSchedule { At = 300 });
                var previousContext = Game.AreaUpdateContext.Value;
                Game.AreaUpdateContext.Value = updates;
                using var areaWire = new Wire();
                ClientState? areaConnection = null;
                IProjectileRuntime? previousRuntime = null;
                FixedProjectileRuntime? fixedRuntime = null;
                try
                {
                    areaConnection = new ClientState(conn.Layout, 0xC472, 1)
                    {
                        Character = character, CharacterId = Guid.NewGuid(), Account = "area-hit-bite-account",
                        League = "Standard", LeagueForm = "Standard", ClassRow = witchRow, Instance = new(),
                        PlayerSent = true, PlayerObjectCreated = true, PlayerUpdatesState = updates,
                        Experience = CharacterStats.ExperienceFor(100), LeagueModule = conn.LeagueModule,
                        Wire = ("area-hit-bite", areaWire.Stream, areaWire.Ciphers), Pools = new(100, 1, 1, 0, 0),
                    };
                    var areaConn = areaConnection;
                    Assert.Empty(areaConn.PlayerStats);

                    var inventory = Game.ItemsOf(areaConn);
                    foreach (byte row in new byte[] { PlayerInventories.Weapon1, PlayerInventories.Offhand1,
                                 PlayerInventories.Helmet, 5, 6, 7 })
                        foreach (var old in inventory.Items(row)) inventory.Remove(row, old.Id);

                    Item Gem(string name, int level)
                    {
                        var type = Program.Items.FindGem(name)
                            ?? throw new InvalidOperationException("missing creatable gem " + name);
                        return ItemCatalog.Create(type.Type) with
                        {
                            GemVariant = type.Variant.Index,
                            GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(level),
                        };
                    }
                    var areaGem = Gem("Ice Nova", 20);
                    var cleaveGem = Gem("Cleave", 20);
                    var sword = ItemCatalog.Create(Program.Items.Find(
                        "Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1")!) with
                    {
                        Sockets = [new(SocketColour.White, areaGem), new(SocketColour.White, cleaveGem)],
                        Links = [2],
                    };
                    var swordPlacement = inventory.Add(PlayerInventories.Weapon1, 0, 0, sword);
                    var astramentisPlacement = inventory.Add(5, 0, 0,
                        new UniqueCatalog(Program.Items).Create("Astramentis", new Random(504))!);
                    var firstRingPlacement = inventory.Add(6, 0, 0,
                        new UniqueCatalog(Program.Items).Create("Le Heup of All", new Random(504))!);
                    var secondRingPlacement = inventory.Add(7, 0, 0,
                        new UniqueCatalog(Program.Items).Create("Le Heup of All", new Random(505))!);
                    var bite = new UniqueCatalog(Program.Items).Create("Farrul's Bite", new Random(504))!;
                    var bitePlacement = inventory.Add(PlayerInventories.Helmet, 0, 0, bite);
                    areaConn.Pools = Game.PoolsFor(areaConn, 100);
                    areaConn.PlayerLife = areaConn.Pools.Life;
                    areaConn.PlayerMana = areaConn.Pools.Mana;
                    Game.ReconcileOwnAuras(areaConn, validateSkills: true);
                    Assert.Empty(areaConn.PlayerStats);
                    Assert.Equal(100, areaConn.Pools.Level);
                    Assert.False(Game.IsInactive(areaConn, swordPlacement.Id));
                    Assert.False(Game.IsInactive(areaConn, astramentisPlacement.Id));
                    Assert.False(Game.IsInactive(areaConn, firstRingPlacement.Id));
                    Assert.False(Game.IsInactive(areaConn, secondRingPlacement.Id));
                    Assert.False(Game.IsInactive(areaConn, bitePlacement.Id));
                    Assert.Equal(2, sword.Sockets.Count);

                    const string biteDamageStat = "hit_and_ailment_damage_+%_vs_blinded_enemies";
                    var biteDamageMod = Assert.Single(bite.Mods, mod =>
                        Program.Items.Mods.ByHash(mod.Hash)?.Id == "DamageAgainstBlindedEnemiesUnique__1");
                    Assert.Equal(biteDamageStat,
                        Assert.Single(Program.Items.Mods.ByHash(biteDamageMod.Hash)!.Stats).Id);
                    int rolledBiteDamage = Assert.Single(biteDamageMod.Values);
                    Assert.InRange(rolledBiteDamage, 40, 50);
                    Assert.Equal(40, rolledBiteDamage);

                    void SetStat(string name, int value)
                    {
                        uint key = Program.Stats.Key(name)
                            ?? throw new InvalidOperationException("missing required stat " + name);
                        areaConn.PlayerStats[key] = value;
                    }
                    void ZeroIfPresent(string name)
                    {
                        if (Program.Stats.Key(name) is uint key) areaConn.PlayerStats[key] = 0;
                    }

                    // Labelled source controls: the current real spell base remains, while one fixed
                    // spell-added physical pair dominates its spread and no other damage bucket contributes.
                    foreach (string name in new[]
                    {
                        "spell_damage_+%", "area_damage_+%", "attack_damage_+%", "melee_damage_+%",
                        "melee_physical_damage_+%", "physical_damage_+%", "cold_damage_+%",
                        "fire_damage_+%", "lightning_damage_+%", "chaos_damage_+%", "elemental_damage_+%",
                        "projectile_damage_+%", "damage_+%_while_leeching",
                        "damage_+%_while_life_leeching", "damage_+%_while_mana_leeching",
                        "damage_+%_while_es_leeching", "damage_+%_on_full_energy_shield",
                        "physical_damage_+%_while_life_leeching", "is_life_leeching", "is_mana_leeching",
                        "is_es_leeching", "on_full_energy_shield", "strong_casting",
                        "base_extra_damage_rolls", "base_chance_to_deal_triple_damage_%",
                        "chance_to_deal_double_damage_%", "spell_chance_to_deal_double_damage_%",
                        "spell_damage_aura_spell_damage_+%_final",
                        "righteous_fire_spell_damage_+%_final",
                        "wrath_aura_spell_lightning_damage_+%_final",
                        "skitterbots_trap_mine_damage_+%_final",
                        "slayer_damage_+%_final_from_distance",
                        "support_slashing_damage_+%_final_from_distance",
                        "shield_crush_damage_+%_final_from_distance",
                        "damage_over_time_+%", "base_ailment_damage_+%", "physical_damage_over_time_+%",
                        "fire_damage_over_time_+%", "chaos_damage_over_time_+%", "bleeding_damage_+%",
                        "burn_damage_+%", "ignite_damage_+%", "base_poison_damage_+%",
                        "faster_bleed_%", "faster_burn_%", "faster_poison_%",
                        "damaging_ailments_deal_damage_+%_faster",
                    })
                        SetStat(name, 0);
                    foreach (string type in new[] { "physical", "cold", "fire", "lightning", "chaos" })
                    {
                        SetStat($"spell_minimum_added_{type}_damage", 0);
                        SetStat($"spell_maximum_added_{type}_damage", 0);
                        SetStat($"attack_minimum_added_{type}_damage", 0);
                        SetStat($"attack_maximum_added_{type}_damage", 0);
                    }
                    SetStat("spell_minimum_added_physical_damage", 10_000);
                    SetStat("spell_maximum_added_physical_damage", 10_000);
                    SetStat("damage_+%", 100);
                    SetStat("global_cannot_crit", 1);
                    foreach (string targetType in new[] { "fire", "cold", "lightning", "chaos" })
                        foreach (string prefix in new[] { "", "base_", "skill_", "spell_", "attack_" })
                        {
                            ZeroIfPresent($"{prefix}physical_damage_%_to_convert_to_{targetType}");
                            ZeroIfPresent($"{prefix}physical_damage_%_to_add_as_{targetType}");
                        }

                    var source = Assert.Single(Game.GemSkillEffects(areaConn), gem =>
                        Game.SkillsFor(areaConn).EffectId(gem.Effect) == "IceNova");
                    var plan = SkillHitPlan.ForSkill(Game.SkillsFor(areaConn), source.Effect, source.Level, source.Quality);
                    Assert.Equal(SkillHitKind.Spell, plan.Kind);
                    Assert.True(plan.CanHit);
                    Assert.Equal(DamageType.Cold,
                        Assert.Single(plan.SpellBase.Components).Type);

                    previousRuntime = areaConn.ProjectileRuntimeState;
                    fixedRuntime = new FixedProjectileRuntime(
                        ProjectileRuntime.Time(Game.ResourceEffectsTime(areaConn)));
                    areaConn.ProjectileRuntimeState = fixedRuntime;
                    double now = Game.ResourceEffectsTime(areaConn);
                    Assert.Equal(fixedRuntime.NowTicks / ProjectileRuntime.QuantaPerSecond, now, 9);
                    Assert.False(areaConn.Paused);
                    Assert.Null(areaConn.ProjectileRuntime);

                    var owner = Game.ResourceEffectsOf(areaConn);
                    Assert.True(Game.ResourceEffectsCurrent(areaConn, owner));
                    var from = areaConn.Walk.Position(Game.ProjectileWorldTime(areaConn));
                    var baselineAt = (X: from.X + 1, Y: from.Y);
                    var blindedAt = (X: from.X - 1, Y: from.Y);
                    var centre = Game.SpellCentre(areaConn, source.Effect, from, from);
                    Assert.Equal(from, centre);
                    var skillStats = Game.SupportedSkill(areaConn, source.Id).Stats;
                    int radius = Game.SkillRadius(areaConn, skillStats, spell: true, source.Effect);
                    Assert.True(Math.Sqrt(Math.Pow(baselineAt.X - centre.X, 2)
                        + Math.Pow(baselineAt.Y - centre.Y, 2)) <= radius);
                    Assert.True(Math.Sqrt(Math.Pow(blindedAt.X - centre.X, 2)
                        + Math.Pow(blindedAt.Y - centre.Y, 2)) <= radius);

                    const uint baselineId = 0xC472A1u, blindedId = 0xC472A2u;
                    SpawnedMonster Target(uint id, string label, (int X, int Y) at, bool hostile = true)
                    {
                        var target = new SpawnedMonster(label, new(at.X, at.Y, 0), 0, 0, 1_000_000)
                        {
                            Level = 100, Hostile = hostile, Life = 1_000_000,
                        };
                        areaConn.Monsters[id] = target;
                        return target;
                    }
                    SpawnedMonster baselineTarget, blindedTarget;
                    lock (areaConn.Monsters)
                    {
                        baselineTarget = Target(baselineId, "area-hit-bite-unblinded", baselineAt);
                        blindedTarget = Target(blindedId, "area-hit-bite-owned-blind", blindedAt);
                        Assert.Same(baselineTarget, areaConn.Monsters[baselineId]);
                        Assert.Same(blindedTarget, areaConn.Monsters[blindedId]);
                        Assert.True(Game.InflictMonsterBlind(areaConn, blindedId, blindedTarget, now,
                            (100, 0, 4), new Random(1)));
                        Assert.True(Game.Blinded(areaConn, blindedId));
                        Assert.False(Game.Blinded(areaConn, baselineId));
                        var blind = owner.TargetBlind(blindedId, blindedTarget).Read(now);
                        Assert.Equal(20, blind.Less);
                        Assert.Equal(now + 4, blind.Until, 9);
                    }

                    void AssertNeutralTarget(SpawnedMonster target)
                    {
                        Assert.Null(target.Variety);
                        Assert.Equal(MonsterModSet.None, target.Mods);
                        Assert.Equal(0, target.Effects.PhysicalReduction);
                        Assert.Equal(0, target.Effects.Resistance(DamageType.Physical));
                        Assert.Equal(0, target.Effects.Resistance(DamageType.Cold));
                        Assert.Equal(1, target.Effects.DamageTaken(spell: true), 9);
                        Assert.True(target.Hostile && !target.Dead && target.Life > 0);
                    }
                    AssertNeutralTarget(baselineTarget);
                    AssertNeutralTarget(blindedTarget);

                    uint baselineLifeBefore = baselineTarget.Life, blindedLifeBefore = blindedTarget.Life;
                    int sourceDamageBefore, sourceBiteBefore, sourcePhysicalMinBefore, sourcePhysicalMaxBefore;
                    DamageRange rawPhysical = default, rawCold = default;
                    (int Min, int Max) baselineBounds, additiveBounds, wrongMoreBounds;
                    using (new Game.PlayerStatScope(areaConn, fresh: true))
                    {
                        var player = Game.CharacterPlayerStats(areaConn);
                        int Stat(string name) => player.GetValueOrDefault(Program.Stats.Key(name)!.Value);
                        sourceBiteBefore = Stat(biteDamageStat);
                        sourceDamageBefore = Stat("damage_+%");
                        sourcePhysicalMinBefore = Stat("spell_minimum_added_physical_damage");
                        sourcePhysicalMaxBefore = Stat("spell_maximum_added_physical_damage");
                        Assert.Equal(rolledBiteDamage, sourceBiteBefore);
                        Assert.Equal(100, sourceDamageBefore);
                        Assert.Equal(10_000, sourcePhysicalMinBefore);
                        Assert.Equal(10_000, sourcePhysicalMaxBefore);

                        var inputs = Assert.IsType<Game.SupportedHitInputs>(
                            Game.SupportedHitInputsFor(areaConn, source.Id));
                        Assert.Equal(SkillHitKind.Spell, inputs.Plan.Kind);
                        Assert.False(inputs.Attack);
                        Assert.True(inputs.Area);
                        Assert.Equal(rolledBiteDamage, inputs.Get(biteDamageStat));
                        Assert.Equal(100, inputs.Get("damage_+%"));
                        Assert.Equal(1, inputs.Get("global_cannot_crit"));
                        foreach (string zero in new[]
                        {
                            "spell_damage_+%", "area_damage_+%", "physical_damage_+%",
                            "cold_damage_+%", "elemental_damage_+%", "projectile_damage_+%",
                            "base_extra_damage_rolls", "base_chance_to_deal_triple_damage_%",
                            "chance_to_deal_double_damage_%", "spell_chance_to_deal_double_damage_%",
                        })
                            Assert.Equal(0, inputs.Get(zero));
                        double effectiveness = Game.AddedEffectiveness(areaConn, source.Effect, source.Level);
                        double fixedPhysical = 10_000 * effectiveness;
                        rawPhysical = inputs.Raw[DamageType.Physical];
                        rawCold = inputs.Raw[DamageType.Cold];
                        Assert.Equal(fixedPhysical, inputs.Raw[DamageType.Physical].Min, 9);
                        Assert.Equal(fixedPhysical, inputs.Raw[DamageType.Physical].Max, 9);
                        Assert.Equal([DamageType.Physical, DamageType.Cold],
                            inputs.Raw.Components.Select(component => component.Type).ToArray());
                        var converted = inputs.Conversion.Trace(inputs.Raw).ToArray();
                        Assert.Equal([DamageType.Physical, DamageType.Cold],
                            converted.Select(path => path.Type).Order().ToArray());
                        Assert.Equal(inputs.Raw.MinTotal, converted.Sum(path => path.Range.Min), 9);
                        Assert.Equal(inputs.Raw.MaxTotal, converted.Sum(path => path.Range.Max), 9);
                        Assert.Equal(1, ChargeEffects.DamageMore(inputs.Get, spell: true), 9);
                        var gambler = Game.GamblerFor(player);
                        Assert.Equal(0, gambler.DamageExtraRolls);
                        Assert.Equal(0, gambler.TripleDamageChance);
                        Assert.Equal(0, gambler.DoubleDamageChance);
                        Assert.Equal(0, gambler.SpellDoubleDamageChance);
                        Assert.Equal(1, gambler.DamageFactor, 9);

                        (int Min, int Max) Bounds(DamagePacket hit) =>
                            ((int)Math.Floor(hit.MinTotal), (int)Math.Floor(hit.MaxTotal));
                        var baselineSource = inputs.Raw.Scale(2d);
                        var additiveSource = inputs.Raw.Scale(2d + rolledBiteDamage / 100d);
                        var wrongPostMoreSource = baselineSource.Scale(1d + rolledBiteDamage / 100d);
                        baselineBounds = Bounds(baselineSource);
                        additiveBounds = Bounds(additiveSource);
                        wrongMoreBounds = Bounds(wrongPostMoreSource);
                        Assert.True(additiveBounds.Min > baselineBounds.Max,
                            $"missing-Bonus oracle intervals overlap: baseline {baselineBounds}, "
                            + $"additive {additiveBounds}");
                        Assert.True(additiveBounds.Max < wrongMoreBounds.Min
                                || additiveBounds.Min > wrongMoreBounds.Max,
                            $"additive {additiveBounds} overlaps wrong-more {wrongMoreBounds}");

                        // One real immediate area-skill invocation. The fresh scope keeps its first-contact source
                        // snapshot uncached; no ring, wave, projectile, placed skill or real-time delay is involved.
                        await Game.AreaSkillHit("area-hit-bite", areaWire.Stream, areaWire.Ciphers, areaConn,
                            source.Id, source, from, from, immediate: true,
                            criticalRoll: new SkillCriticalRoll(0));
                    }

                    int baselineDamage = checked((int)(baselineLifeBefore - baselineTarget.Life));
                    int blindedDamage = checked((int)(blindedLifeBefore - blindedTarget.Life));
                    Assert.True(baselineDamage >= baselineBounds.Min && baselineDamage <= baselineBounds.Max,
                        $"unconditional area hit {baselineDamage} was outside {baselineBounds}; "
                        + $"raw Physical {rawPhysical} + Cold {rawCold}, Bite={sourceBiteBefore}, "
                        + $"life {baselineLifeBefore}->{baselineTarget.Life}.");
                    Assert.True(Game.Blinded(areaConn, blindedId));
                    var blindAfter = owner.TargetBlind(blindedId, blindedTarget).Read(now);
                    Assert.Equal(20, blindAfter.Less);
                    Assert.Equal(now + 4, blindAfter.Until, 9);
                    Assert.True(blindedDamage >= additiveBounds.Min && blindedDamage <= additiveBounds.Max,
                        $"Farrul's Bite {biteDamageStat}={sourceBiteBefore} was present on the same real "
                        + $"AreaSkillHit source, and target 0x{blindedId:x} was owned-Blinded, but the actual "
                        + $"committed damage {blindedDamage} was outside the independent additive interval "
                        + $"{additiveBounds}; raw Physical {rawPhysical} + Cold {rawCold}, "
                        + $"unconditional={baselineBounds}, wrong-more={wrongMoreBounds}, "
                        + $"life {blindedLifeBefore}->{blindedTarget.Life}, "
                        + $"actual unblinded={baselineDamage}, blinded={blindedDamage}.");

                    Assert.Same(baselineTarget, areaConn.Monsters[baselineId]);
                    Assert.Same(blindedTarget, areaConn.Monsters[blindedId]);
                    Assert.False(baselineTarget.Dead);
                    Assert.False(blindedTarget.Dead);
                    using (new Game.PlayerStatScope(areaConn, fresh: true))
                    {
                        var player = Game.CharacterPlayerStats(areaConn);
                        Assert.Equal(sourceDamageBefore,
                            player.GetValueOrDefault(Program.Stats.Key("damage_+%")!.Value));
                        Assert.Equal(sourceBiteBefore,
                            player.GetValueOrDefault(Program.Stats.Key(biteDamageStat)!.Value));
                        Assert.Equal(sourcePhysicalMinBefore, player.GetValueOrDefault(
                            Program.Stats.Key("spell_minimum_added_physical_damage")!.Value));
                        Assert.Equal(sourcePhysicalMaxBefore, player.GetValueOrDefault(
                            Program.Stats.Key("spell_maximum_added_physical_damage")!.Value));
                    }

                    // The same real contact path for a legal one-hand-sword Cleave: physical remains for bleed and
                    // converts partly to fire for ignite. Its first unblinded victim applies its own controlled Blind,
                    // but the immutable prehit profile must keep that hit and its three ailments on baseline values.
                    SetStat("attack_minimum_added_physical_damage", 1_000);
                    SetStat("attack_maximum_added_physical_damage", 1_000);
                    SetStat("physical_damage_%_to_convert_to_fire", 50);
                    SetStat("chance_to_poison_on_hit_with_attacks_%", 100);
                    SetStat("bleed_on_hit_with_attacks_%", 100);
                    SetStat("base_chance_to_ignite_%", 100);
                    SetStat("global_chance_to_blind_on_hit_%", 100);
                    SetStat("accuracy_rating", 1_000_000);
                    areaConn.Pools = areaConn.Pools with { Accuracy = 1_000_000 };

                    var cleave = Assert.Single(Game.GemSkillEffects(areaConn), gem =>
                        Game.SkillsFor(areaConn).EffectId(gem.Effect) == "Cleave");
                    var cleavePlan = SkillHitPlan.ForSkill(Game.SkillsFor(areaConn), cleave.Effect,
                        cleave.Level, cleave.Quality);
                    Assert.Equal(SkillHitKind.Attack, cleavePlan.Kind);
                    Assert.True(cleavePlan.CanHit);
                    Assert.True(Game.SkillsFor(areaConn).IsMelee(cleave.Effect));

                    const uint cleaveUnblindId = 0xC472B1u, cleaveBlindId = 0xC472B2u;
                    const uint cleaveImmuneId = 0xC472B3u, cleaveFriendlyId = 0xC472B4u;
                    const uint cleaveDeadId = 0xC472B5u;
                    SpawnedMonster cleaveUnblind, cleaveBlind, cleaveImmune, cleaveFriendly, cleaveDead;
                    lock (areaConn.Monsters)
                    {
                        cleaveUnblind = Target(cleaveUnblindId, "area-cleave-first-unblinded", from);
                        cleaveBlind = Target(cleaveBlindId, "area-cleave-preblinded", from);
                        cleaveImmune = Target(cleaveImmuneId, "area-cleave-cannot-be-blinded", from);
                        cleaveImmune.Mods = MonsterModSet.None with
                        {
                            Effects = new MonsterModEffects(new Dictionary<string, int>
                            {
                                ["cannot_be_blinded"] = 1,
                            }),
                        };
                        cleaveFriendly = Target(cleaveFriendlyId, "area-cleave-friendly", from, hostile: false);
                        cleaveDead = Target(cleaveDeadId, "area-cleave-dead", from);
                        cleaveDead.Dead = true;
                        cleaveDead.Life = 0;
                        Assert.True(Game.InflictMonsterBlind(areaConn, cleaveBlindId, cleaveBlind,
                            Game.ResourceEffectsTime(areaConn), (100, 0, 4), new Random(1)));
                        Assert.True(Game.Blinded(areaConn, cleaveBlindId));
                        Assert.False(Game.Blinded(areaConn, cleaveUnblindId));
                        Assert.False(Game.Blinded(areaConn, cleaveImmuneId));
                    }

                    Game.SupportedHitInputs cleaveInputs;
                    Game.AreaSkillDamageProfile cleaveBaseline, cleavePositive;
                    MonsterIgnites.Settings cleaveBaselineIgnite, cleavePositiveIgnite;
                    MonsterIgnites.Settings cleaveBaselineBleed, cleavePositiveBleed;
                    MonsterPoisons.Settings cleaveBaselinePoison, cleavePositivePoison;
                    using (new Game.PlayerStatScope(areaConn, fresh: true))
                    {
                        var player = Game.CharacterPlayerStats(areaConn);
                        cleaveInputs = Assert.IsType<Game.SupportedHitInputs>(
                            Game.SupportedHitInputsFor(areaConn, cleave.Id));
                        Assert.True(cleaveInputs.Attack && cleaveInputs.Melee && cleaveInputs.Area);
                        Assert.Equal(rolledBiteDamage, cleaveInputs.Get(biteDamageStat));
                        Assert.Equal(100, cleaveInputs.Get("damage_+%"));
                        foreach (string zero in new[]
                        {
                            "attack_damage_+%", "melee_damage_+%", "melee_physical_damage_+%",
                            "physical_damage_+%", "fire_damage_+%", "area_damage_+%",
                            "damage_over_time_+%", "base_ailment_damage_+%", "fire_damage_over_time_+%",
                            "bleeding_damage_+%", "burn_damage_+%", "base_poison_damage_+%",
                            "faster_bleed_%", "faster_burn_%", "faster_poison_%",
                            "slayer_damage_+%_final_from_distance",
                            "support_slashing_damage_+%_final_from_distance",
                            "shield_crush_damage_+%_final_from_distance",
                        })
                            Assert.Equal(0, cleaveInputs.Get(zero));
                        var converted = cleaveInputs.Conversion.Trace(cleaveInputs.Raw).ToArray();
                        Assert.Equal([DamageType.Physical, DamageType.Fire],
                            converted.Select(path => path.Type).Order().ToArray());

                        cleaveBaseline = new(Game.SupportedHitFrom(cleaveInputs, suppress: true),
                            Game.SupportedIgnite(areaConn, cleave.Id, spell: false, player,
                                additionalIncreased: 0, capturedInputs: cleaveInputs),
                            Game.SupportedBleed(areaConn, cleave.Id, spell: false, player,
                                melee: true, additionalIncreased: 0, capturedInputs: cleaveInputs),
                            Game.SupportedPoison(areaConn, cleave.Id, spell: false, player,
                                additionalIncreased: 0, capturedInputs: cleaveInputs));
                        cleavePositive = new(Game.SupportedHitFrom(cleaveInputs, suppress: true, rolledBiteDamage),
                            Game.SupportedIgnite(areaConn, cleave.Id, spell: false, player,
                                additionalIncreased: rolledBiteDamage, capturedInputs: cleaveInputs),
                            Game.SupportedBleed(areaConn, cleave.Id, spell: false, player,
                                melee: true, additionalIncreased: rolledBiteDamage, capturedInputs: cleaveInputs),
                            Game.SupportedPoison(areaConn, cleave.Id, spell: false, player,
                                additionalIncreased: rolledBiteDamage, capturedInputs: cleaveInputs));
                        cleaveBaselineIgnite = cleaveBaseline.Ignite;
                        cleavePositiveIgnite = cleavePositive.Ignite;
                        cleaveBaselineBleed = cleaveBaseline.Bleed;
                        cleavePositiveBleed = cleavePositive.Bleed;
                        cleaveBaselinePoison = cleaveBaseline.Poison;
                        cleavePositivePoison = cleavePositive.Poison;
                    }

                    (int Min, int Max) HitBounds(DamagePacket hit) =>
                        ((int)Math.Floor(hit.MinTotal), (int)Math.Floor(hit.MaxTotal));
                    var cleaveBaselineBounds = HitBounds(cleaveBaseline.Hit);
                    var cleavePositiveBounds = HitBounds(cleavePositive.Hit);
                    var cleaveWrongMore = HitBounds(cleaveBaseline.Hit.Scale(1 + rolledBiteDamage / 100d));
                    Assert.Equal(cleaveBaseline.Hit.MinTotal * 1.2, cleavePositive.Hit.MinTotal, 9);
                    Assert.Equal(cleaveBaseline.Hit.MaxTotal * 1.2, cleavePositive.Hit.MaxTotal, 9);
                    void AssertOneFifth(MonsterIgnites.Settings baseline, MonsterIgnites.Settings positive)
                    {
                        Assert.InRange(positive.Min, baseline.Min * 1.2 - 1d / 30, baseline.Min * 1.2 + 1d / 30);
                        Assert.InRange(positive.Max, baseline.Max * 1.2 - 1d / 30, baseline.Max * 1.2 + 1d / 30);
                        Assert.InRange(positive.CriticalMin,
                            baseline.CriticalMin * 1.2 - 1d / 30, baseline.CriticalMin * 1.2 + 1d / 30);
                        Assert.InRange(positive.CriticalMax,
                            baseline.CriticalMax * 1.2 - 1d / 30, baseline.CriticalMax * 1.2 + 1d / 30);
                        Assert.Equal(baseline.Chance, positive.Chance);
                        Assert.Equal(baseline.Seconds, positive.Seconds, 9);
                    }
                    AssertOneFifth(cleaveBaselineIgnite, cleavePositiveIgnite);
                    AssertOneFifth(cleaveBaselineBleed, cleavePositiveBleed);
                    Assert.InRange(cleavePositivePoison.Min,
                        cleaveBaselinePoison.Min * 1.2 - 1d / 30, cleaveBaselinePoison.Min * 1.2 + 1d / 30);
                    Assert.InRange(cleavePositivePoison.Max,
                        cleaveBaselinePoison.Max * 1.2 - 1d / 30, cleaveBaselinePoison.Max * 1.2 + 1d / 30);
                    Assert.InRange(cleavePositivePoison.CriticalMin,
                        cleaveBaselinePoison.CriticalMin * 1.2 - 1d / 30,
                        cleaveBaselinePoison.CriticalMin * 1.2 + 1d / 30);
                    Assert.InRange(cleavePositivePoison.CriticalMax,
                        cleaveBaselinePoison.CriticalMax * 1.2 - 1d / 30,
                        cleaveBaselinePoison.CriticalMax * 1.2 + 1d / 30);
                    Assert.Equal(cleaveBaselinePoison.Chance, cleavePositivePoison.Chance);
                    Assert.Equal(cleaveBaselinePoison.Seconds, cleavePositivePoison.Seconds, 9);
                    Assert.True(cleavePositiveBounds.Min > cleaveBaselineBounds.Max);
                    Assert.True(cleavePositiveBounds.Max < cleaveWrongMore.Min
                        || cleavePositiveBounds.Min > cleaveWrongMore.Max);
                    Assert.True(cleaveBaselineIgnite.Min > 0 && cleaveBaselineBleed.Min > 0
                        && cleaveBaselinePoison.Min > 0);
                    Assert.True(cleavePositiveIgnite.Min > cleaveBaselineIgnite.Max);
                    Assert.True(cleavePositiveBleed.Min > cleaveBaselineBleed.Max);
                    Assert.True(cleavePositivePoison.Min > cleaveBaselinePoison.Max);

                    uint cleaveUnblindBefore = cleaveUnblind.Life, cleaveBlindBefore = cleaveBlind.Life;
                    uint cleaveImmuneBefore = cleaveImmune.Life, cleaveFriendlyBefore = cleaveFriendly.Life;
                    using (new Game.PlayerStatScope(areaConn, fresh: true))
                    {
                        Assert.Equal(rolledBiteDamage,
                            Game.CharacterPlayerStats(areaConn).GetValueOrDefault(Program.Stats.Key(biteDamageStat)!.Value));
                        await Game.AreaSkillHit("area-cleave-bite", areaWire.Stream, areaWire.Ciphers, areaConn,
                            cleave.Id, cleave, from, from, immediate: true,
                            criticalRoll: new SkillCriticalRoll(0));
                    }
                    int cleaveUnblindDamage = checked((int)(cleaveUnblindBefore - cleaveUnblind.Life));
                    int cleaveBlindDamage = checked((int)(cleaveBlindBefore - cleaveBlind.Life));
                    int cleaveImmuneDamage = checked((int)(cleaveImmuneBefore - cleaveImmune.Life));
                    Assert.InRange(cleaveUnblindDamage, cleaveBaselineBounds.Min, cleaveBaselineBounds.Max);
                    Assert.InRange(cleaveBlindDamage, cleavePositiveBounds.Min, cleavePositiveBounds.Max);
                    Assert.InRange(cleaveImmuneDamage, cleaveBaselineBounds.Min, cleaveBaselineBounds.Max);
                    Assert.True(Game.Blinded(areaConn, cleaveUnblindId),
                        "the first Cleave hit did not apply its controlled Blind");
                    Assert.True(Game.Blinded(areaConn, cleaveBlindId));
                    Assert.False(Game.Blinded(areaConn, cleaveImmuneId));
                    Assert.Equal(cleaveFriendlyBefore, cleaveFriendly.Life);
                    Assert.Equal(0u, cleaveDead.Life);
                    Assert.False(Game.Blinded(areaConn, cleaveFriendlyId));
                    Assert.False(Game.Blinded(areaConn, cleaveDeadId));

                    double firstContact = cleaveUnblind.Ignites.LastApplied;
                    Assert.True(firstContact > 0);
                    Assert.Equal(firstContact, cleaveUnblind.Bleeds.LastApplied, 9);
                    double earnedIgnite = cleaveUnblind.Ignites.PerSecond(firstContact);
                    double earnedBleed = cleaveUnblind.Bleeds.PerSecond(firstContact);
                    double earnedPoison = cleaveUnblind.Poisons.TotalDps(firstContact);
                    Assert.InRange(earnedIgnite, cleaveBaselineIgnite.Min, cleaveBaselineIgnite.Max);
                    Assert.InRange(earnedBleed, cleaveBaselineBleed.Min, cleaveBaselineBleed.Max);
                    Assert.InRange(earnedPoison, cleaveBaselinePoison.Min, cleaveBaselinePoison.Max);
                    Assert.True(earnedIgnite < cleavePositiveIgnite.Min
                        && earnedBleed < cleavePositiveBleed.Min && earnedPoison < cleavePositivePoison.Min,
                        "the same hit's newly applied Blind boosted one of its own damaging ailments");

                    lock (areaConn.Monsters)
                        foreach (uint id in areaConn.Monsters.Keys.Where(id => id != cleaveUnblindId).ToArray())
                            areaConn.Monsters.Remove(id);
                    uint cleaveSecondBefore = cleaveUnblind.Life;
                    await Game.AreaSkillHit("area-cleave-bite-second", areaWire.Stream, areaWire.Ciphers, areaConn,
                        cleave.Id, cleave, from, from, immediate: true,
                        criticalRoll: new SkillCriticalRoll(0));
                    double secondContact = cleaveUnblind.Ignites.LastApplied;
                    Assert.True(secondContact > firstContact);
                    Assert.Equal(secondContact, cleaveUnblind.Bleeds.LastApplied, 9);
                    int cleaveSecondDamage = checked((int)(cleaveSecondBefore - cleaveUnblind.Life));
                    Assert.InRange(cleaveSecondDamage, cleavePositiveBounds.Min, cleavePositiveBounds.Max);
                    double secondIgnite = cleaveUnblind.Ignites.PerSecond(secondContact);
                    double secondBleed = cleaveUnblind.Bleeds.PerSecond(secondContact);
                    double secondPoison = cleaveUnblind.Poisons.TotalDps(secondContact);
                    Assert.InRange(secondIgnite, cleavePositiveIgnite.Min, cleavePositiveIgnite.Max);
                    Assert.InRange(secondBleed, cleavePositiveBleed.Min, cleavePositiveBleed.Max);
                    double firstPoisonActive = secondContact < firstContact + cleaveBaselinePoison.Seconds
                        ? earnedPoison : 0;
                    Assert.InRange(secondPoison - firstPoisonActive,
                        cleavePositivePoison.Min, cleavePositivePoison.Max);
                    double igniteDeadline = cleaveUnblind.Ignites.Until;
                    double bleedDeadline = cleaveUnblind.Bleeds.Until;
                    double poisonDeadline = cleaveUnblind.Poisons.Until;

                    var removedBite = inventory.Remove(PlayerInventories.Helmet, bitePlacement.Id);
                    Assert.NotNull(removedBite);
                    Game.ReconcileOwnAuras(areaConn, validateSkills: true);
                    Assert.True(Game.Blinded(areaConn, cleaveUnblindId));
                    Assert.Equal(earnedIgnite, cleaveUnblind.Ignites.PerSecond(firstContact), 9);
                    Assert.Equal(earnedBleed, cleaveUnblind.Bleeds.PerSecond(firstContact), 9);
                    Assert.Equal(earnedPoison, cleaveUnblind.Poisons.TotalDps(firstContact), 9);
                    Assert.Equal(igniteDeadline, cleaveUnblind.Ignites.Until, 9);
                    Assert.Equal(bleedDeadline, cleaveUnblind.Bleeds.Until, 9);
                    Assert.Equal(poisonDeadline, cleaveUnblind.Poisons.Until, 9);

                    const uint cleaveWithdrawId = 0xC472B6u;
                    SpawnedMonster cleaveWithdraw;
                    lock (areaConn.Monsters)
                    {
                        cleaveWithdraw = Target(cleaveWithdrawId, "area-cleave-source-withdrawn", from);
                        Assert.True(Game.InflictMonsterBlind(areaConn, cleaveWithdrawId, cleaveWithdraw,
                            Game.ResourceEffectsTime(areaConn), (100, 0, 4), new Random(1)));
                        Assert.True(Game.Blinded(areaConn, cleaveWithdrawId));
                    }
                    uint cleaveAfterWithdrawBefore = cleaveWithdraw.Life;
                    using (new Game.PlayerStatScope(areaConn, fresh: true))
                    {
                        Assert.Equal(0, Game.CharacterPlayerStats(areaConn)
                            .GetValueOrDefault(Program.Stats.Key(biteDamageStat)!.Value));
                        await Game.AreaSkillHit("area-cleave-bite-withdrawn", areaWire.Stream, areaWire.Ciphers, areaConn,
                            cleave.Id, cleave, from, from, immediate: true,
                            criticalRoll: new SkillCriticalRoll(0));
                        Assert.Equal(0, Game.CharacterPlayerStats(areaConn)
                            .GetValueOrDefault(Program.Stats.Key(biteDamageStat)!.Value));
                    }
                    double withdrawContact = cleaveWithdraw.Ignites.LastApplied;
                    Assert.True(withdrawContact > secondContact);
                    Assert.Equal(withdrawContact, cleaveWithdraw.Bleeds.LastApplied, 9);
                    int cleaveAfterWithdrawDamage =
                        checked((int)(cleaveAfterWithdrawBefore - cleaveWithdraw.Life));
                    Assert.InRange(cleaveAfterWithdrawDamage,
                        cleaveBaselineBounds.Min, cleaveBaselineBounds.Max);
                    Assert.True(Game.Blinded(areaConn, cleaveWithdrawId));
                    Assert.InRange(cleaveWithdraw.Ignites.PerSecond(withdrawContact),
                        cleaveBaselineIgnite.Min, cleaveBaselineIgnite.Max);
                    Assert.InRange(cleaveWithdraw.Bleeds.PerSecond(withdrawContact),
                        cleaveBaselineBleed.Min, cleaveBaselineBleed.Max);
                    Assert.InRange(cleaveWithdraw.Poisons.TotalDps(withdrawContact),
                        cleaveBaselinePoison.Min, cleaveBaselinePoison.Max);
                }
                finally
                {
                    if (areaConnection is not null)
                    {
                        areaConnection.ProjectileRuntimeState = previousRuntime;
                        areaConnection.Closed = true;
                    }
                    fixedRuntime?.Invalidate();
                    Game.AreaUpdateContext.Value = previousContext;
                    PlayerInventories.Forget(character);
                }
            }

            async Task SacrificePrepaidConsumerFixture()
            {
                // This fixture exercises only AreaSkillHit's addedChaos consumer boundary. The positive argument is a
                // controlled prepaid input; it does not call, observe, or prove Sacrifice's real life-payment producer.
                string character = "SacrificeConsumer" + Guid.NewGuid().ToString("N");
                const byte witchRow = 1;
                PlayerInventories.SetClass(character, witchRow);
                CharacterSaves.Write(character, new(new Dictionary<byte, List<CharacterSaves.SavedPlacement>>(),
                    Experience: CharacterStats.ExperienceFor(100)));
                var updates = new PlayerUpdatesState(lifetime, new CatClockSchedule { At = 300 });
                var previousContext = Game.AreaUpdateContext.Value;
                Game.AreaUpdateContext.Value = updates;
                using var sacrificeWire = new Wire();
                ClientState? sacrificeConnection = null;
                IProjectileRuntime? previousRuntime = null;
                FixedProjectileRuntime? fixedRuntime = null;
                try
                {
                    sacrificeConnection = new ClientState(conn.Layout, 0xC473, 1)
                    {
                        Character = character, CharacterId = Guid.NewGuid(), Account = "sacrifice-consumer-account",
                        League = "Standard", LeagueForm = "Standard", ClassRow = witchRow, Instance = new(),
                        PlayerSent = true, PlayerObjectCreated = true, PlayerUpdatesState = updates,
                        Experience = CharacterStats.ExperienceFor(100), LeagueModule = conn.LeagueModule,
                        Wire = ("sacrifice-consumer", sacrificeWire.Stream, sacrificeWire.Ciphers),
                        Pools = new(100, 1, 1, 0, 0),
                    };
                    var sacrificeConn = sacrificeConnection;
                    Assert.Empty(sacrificeConn.PlayerStats);

                    var inventory = Game.ItemsOf(sacrificeConn);
                    foreach (byte row in new byte[] { PlayerInventories.Weapon1, PlayerInventories.Offhand1,
                                 PlayerInventories.Helmet, 5, 6, 7 })
                        foreach (var old in inventory.Items(row)) inventory.Remove(row, old.Id);

                    Item Gem(string name, int level)
                    {
                        var type = Program.Items.FindGem(name)
                            ?? throw new InvalidOperationException("missing creatable gem " + name);
                        return ItemCatalog.Create(type.Type) with
                        {
                            GemVariant = type.Variant.Index,
                            GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(level),
                        };
                    }
                    var iceNovaGem = Gem("Ice Nova", 1);
                    var sacrificeSupportGem = Gem("Sacrifice Support", 1);
                    var brutalitySupportGem = Gem("Brutality Support", 1);
                    var sword = ItemCatalog.Create(Program.Items.Find(
                        "Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1")!) with
                    {
                        Sockets = [new(SocketColour.White, iceNovaGem),
                            new(SocketColour.White, sacrificeSupportGem)],
                        Links = [2],
                    };
                    var swordPlacement = inventory.Add(PlayerInventories.Weapon1, 0, 0, sword);
                    var astramentisPlacement = inventory.Add(5, 0, 0,
                        new UniqueCatalog(Program.Items).Create("Astramentis", new Random(504))!);
                    var firstRingPlacement = inventory.Add(6, 0, 0,
                        new UniqueCatalog(Program.Items).Create("Le Heup of All", new Random(504))!);
                    var secondRingPlacement = inventory.Add(7, 0, 0,
                        new UniqueCatalog(Program.Items).Create("Le Heup of All", new Random(505))!);
                    var bite = new UniqueCatalog(Program.Items).Create("Farrul's Bite", new Random(504))!;
                    var bitePlacement = inventory.Add(PlayerInventories.Helmet, 0, 0, bite);
                    sacrificeConn.Pools = Game.PoolsFor(sacrificeConn, 100);
                    sacrificeConn.PlayerLife = sacrificeConn.Pools.Life;
                    sacrificeConn.PlayerMana = sacrificeConn.Pools.Mana;
                    Game.ReconcileOwnAuras(sacrificeConn, validateSkills: true);
                    Assert.Empty(sacrificeConn.PlayerStats);
                    Assert.Equal(100, sacrificeConn.Pools.Level);
                    Assert.False(Game.IsInactive(sacrificeConn, swordPlacement.Id));
                    Assert.False(Game.IsInactive(sacrificeConn, astramentisPlacement.Id));
                    Assert.False(Game.IsInactive(sacrificeConn, firstRingPlacement.Id));
                    Assert.False(Game.IsInactive(sacrificeConn, secondRingPlacement.Id));
                    Assert.False(Game.IsInactive(sacrificeConn, bitePlacement.Id));
                    Assert.Equal(2, sword.Sockets.Count);
                    Assert.Equal((byte)2, Assert.Single(sword.Links!));

                    var source = Assert.Single(Game.GemSkillEffects(sacrificeConn), gem =>
                        Game.SkillsFor(sacrificeConn).EffectId(gem.Effect) == "IceNova");
                    Assert.Equal(1, source.Level);
                    var admitted = Game.SupportedSkill(sacrificeConn, source.Id);
                    Assert.Equal(source.Effect, admitted.Effect);
                    Assert.Equal(source.Level, admitted.Level);
                    var sacrifice = Assert.Single(admitted.Supports,
                        support => support.Effect.Id == "SupportSacrifice");
                    Assert.Equal(1, sacrifice.Gem.Level);
                    int SupportStat(string name)
                    {
                        var key = Program.Stats.Key(name)
                            ?? throw new InvalidOperationException("missing required stat " + name);
                        return Assert.Single(sacrifice.Stats, stat => stat.Key == key).Value;
                    }
                    int sacrificePercent = SupportStat("support_sacrifice_sacrifice_%_of_current_life");
                    int gainPercent = SupportStat(
                        "support_sacrifice_gain_%_of_sacrificed_life_as_added_chaos_damage");
                    Assert.Equal(20, sacrificePercent);
                    Assert.Equal(35, gainPercent);

                    var biteDamageMod = Assert.Single(bite.Mods, mod =>
                        Program.Items.Mods.ByHash(mod.Hash)?.Id == "DamageAgainstBlindedEnemiesUnique__1");
                    string biteDamageStat =
                        Assert.Single(Program.Items.Mods.ByHash(biteDamageMod.Hash)!.Stats).Id;
                    int rolledBiteDamage = Assert.Single(biteDamageMod.Values);
                    Assert.Equal(40, rolledBiteDamage);

                    void SetStat(string name, int value)
                    {
                        uint key = Program.Stats.Key(name)
                            ?? throw new InvalidOperationException("missing required stat " + name);
                        sacrificeConn.PlayerStats[key] = value;
                    }
                    void ZeroIfPresent(string name)
                    {
                        if (Program.Stats.Key(name) is uint key) sacrificeConn.PlayerStats[key] = 0;
                    }
                    foreach (string name in new[]
                    {
                        "spell_damage_+%", "area_damage_+%", "attack_damage_+%", "melee_damage_+%",
                        "melee_physical_damage_+%", "physical_damage_+%", "cold_damage_+%",
                        "fire_damage_+%", "lightning_damage_+%", "chaos_damage_+%", "elemental_damage_+%",
                        "projectile_damage_+%", "damage_+%_while_leeching",
                        "damage_+%_while_life_leeching", "damage_+%_while_mana_leeching",
                        "damage_+%_while_es_leeching", "damage_+%_on_full_energy_shield",
                        "physical_damage_+%_while_life_leeching", "is_life_leeching", "is_mana_leeching",
                        "is_es_leeching", "on_full_energy_shield", "strong_casting",
                        "base_extra_damage_rolls", "base_chance_to_deal_triple_damage_%",
                        "chance_to_deal_double_damage_%", "spell_chance_to_deal_double_damage_%",
                        "spell_damage_aura_spell_damage_+%_final",
                        "righteous_fire_spell_damage_+%_final",
                        "wrath_aura_spell_lightning_damage_+%_final",
                        "skitterbots_trap_mine_damage_+%_final",
                        "slayer_damage_+%_final_from_distance",
                        "support_slashing_damage_+%_final_from_distance",
                        "shield_crush_damage_+%_final_from_distance",
                        "damage_over_time_+%", "base_ailment_damage_+%", "physical_damage_over_time_+%",
                        "fire_damage_over_time_+%", "chaos_damage_over_time_+%", "bleeding_damage_+%",
                        "burn_damage_+%", "ignite_damage_+%", "base_poison_damage_+%",
                        "faster_bleed_%", "faster_burn_%", "faster_poison_%",
                        "damaging_ailments_deal_damage_+%_faster", "chance_to_freeze_shock_ignite_%",
                        "chance_to_ignite_%", "cannot_cause_bleeding", "cannot_poison_enemies",
                        "never_damaging_ailments", "additional_poisons_+_to_apply_vs_non_poisoned_enemies",
                    })
                        ZeroIfPresent(name);
                    foreach (string type in new[] { "physical", "cold", "fire", "lightning", "chaos" })
                    {
                        SetStat($"spell_minimum_added_{type}_damage", 0);
                        SetStat($"spell_maximum_added_{type}_damage", 0);
                        SetStat($"attack_minimum_added_{type}_damage", 0);
                        SetStat($"attack_maximum_added_{type}_damage", 0);
                    }
                    SetStat("damage_+%", 100);
                    SetStat("global_cannot_crit", 1);
                    SetStat("base_chance_to_poison_on_hit_%", 100);
                    foreach (string targetType in new[] { "fire", "cold", "lightning", "chaos" })
                        foreach (string prefix in new[] { "", "base_", "skill_", "spell_", "attack_" })
                        {
                            ZeroIfPresent($"{prefix}physical_damage_%_to_convert_to_{targetType}");
                            ZeroIfPresent($"{prefix}physical_damage_%_to_add_as_{targetType}");
                        }

                    var plan = SkillHitPlan.ForSkill(Game.SkillsFor(sacrificeConn),
                        source.Effect, source.Level, source.Quality);
                    Assert.Equal(SkillHitKind.Spell, plan.Kind);
                    Assert.True(plan.CanHit);
                    Assert.Equal(DamageType.Cold, Assert.Single(plan.SpellBase.Components).Type);

                    previousRuntime = sacrificeConn.ProjectileRuntimeState;
                    fixedRuntime = new FixedProjectileRuntime(
                        ProjectileRuntime.Time(Game.ResourceEffectsTime(sacrificeConn)));
                    sacrificeConn.ProjectileRuntimeState = fixedRuntime;
                    double now = Game.ResourceEffectsTime(sacrificeConn);
                    Assert.Equal(fixedRuntime.NowTicks / ProjectileRuntime.QuantaPerSecond, now, 9);
                    Assert.False(sacrificeConn.Paused);
                    Assert.Null(sacrificeConn.ProjectileRuntime);

                    var owner = Game.ResourceEffectsOf(sacrificeConn);
                    Assert.True(Game.ResourceEffectsCurrent(sacrificeConn, owner));
                    var from = sacrificeConn.Walk.Position(Game.ProjectileWorldTime(sacrificeConn));
                    Assert.Equal(from, Game.SpellCentre(sacrificeConn, source.Effect, from, from));
                    var skillStats = Game.SupportedSkill(sacrificeConn, source.Id).Stats;
                    int radius = Game.SkillRadius(sacrificeConn, skillStats, spell: true, source.Effect);
                    Assert.True(radius > 0);

                    SpawnedMonster Target(uint id, string label)
                    {
                        var target = new SpawnedMonster(label, new(from.X, from.Y, 0), 0, 0, 1_000_000)
                        {
                            Level = 100, Hostile = true, Life = 1_000_000,
                        };
                        sacrificeConn.Monsters[id] = target;
                        return target;
                    }
                    void AssertNeutralTarget(SpawnedMonster target)
                    {
                        Assert.Null(target.Variety);
                        Assert.Equal(MonsterModSet.None, target.Mods);
                        Assert.Equal(0, target.Effects.Resistance(DamageType.Physical));
                        Assert.Equal(0, target.Effects.Resistance(DamageType.Cold));
                        Assert.Equal(0, target.Effects.Resistance(DamageType.Chaos));
                        Assert.Equal(1, target.Effects.DamageTaken(spell: true), 9);
                        Assert.True(target.Hostile && !target.Dead && target.Life > 0);
                    }
                    (int Min, int Max) FloorComponents(DamagePacket hit) => (
                        hit.Components.Sum(component => (int)Math.Floor(component.Range.Min)),
                        hit.Components.Sum(component => (int)Math.Floor(component.Range.Max)));

                    const uint zeroUnblindId = 0xC473A1u, zeroBlindId = 0xC473A2u;
                    SpawnedMonster zeroUnblind, zeroBlind;
                    lock (sacrificeConn.Monsters)
                    {
                        zeroUnblind = Target(zeroUnblindId, "sacrifice-zero-unblinded");
                        zeroBlind = Target(zeroBlindId, "sacrifice-zero-owned-blind");
                        Assert.True(Game.InflictMonsterBlind(sacrificeConn, zeroBlindId, zeroBlind, now,
                            (100, 0, 4), new Random(1)));
                        Assert.True(Game.Blinded(sacrificeConn, zeroBlindId));
                        Assert.False(Game.Blinded(sacrificeConn, zeroUnblindId));
                    }
                    AssertNeutralTarget(zeroUnblind);
                    AssertNeutralTarget(zeroBlind);

                    (int Min, int Max) zeroNormalBounds, zeroBlindBounds;
                    uint zeroUnblindBefore = zeroUnblind.Life, zeroBlindBefore = zeroBlind.Life;
                    using (new Game.PlayerStatScope(sacrificeConn, fresh: true))
                    {
                        var player = Game.CharacterPlayerStats(sacrificeConn);
                        Assert.Equal(rolledBiteDamage,
                            player.GetValueOrDefault(Program.Stats.Key(biteDamageStat)!.Value));
                        var inputs = Assert.IsType<Game.SupportedHitInputs>(
                            Game.SupportedHitInputsFor(sacrificeConn, source.Id));
                        Assert.Equal(SkillHitKind.Spell, inputs.Plan.Kind);
                        Assert.False(inputs.Attack);
                        Assert.True(inputs.Area);
                        Assert.Equal(rolledBiteDamage, inputs.Get(biteDamageStat));
                        Assert.Equal(100, inputs.Get("damage_+%"));
                        Assert.Equal(1, inputs.Get("global_cannot_crit"));
                        Assert.Equal([DamageType.Cold],
                            inputs.Raw.Components.Select(component => component.Type).ToArray());
                        Assert.Equal(0, inputs.Raw[DamageType.Physical].Min, 9);
                        Assert.Equal(0, inputs.Raw[DamageType.Fire].Min, 9);
                        Assert.Equal(0, inputs.Raw[DamageType.Lightning].Min, 9);
                        Assert.Equal(0, inputs.Raw[DamageType.Chaos].Min, 9);
                        var zeroNormal = Game.SupportedHitFrom(inputs, suppress: true);
                        var zeroBlindHit = Game.SupportedHitFrom(inputs, suppress: true, rolledBiteDamage);
                        Assert.Equal(2d, zeroNormal.MinTotal / inputs.Raw.MinTotal, 9);
                        Assert.Equal(2.4, zeroBlindHit.MinTotal / inputs.Raw.MinTotal, 9);
                        zeroNormalBounds = FloorComponents(zeroNormal);
                        zeroBlindBounds = FloorComponents(zeroBlindHit);

                        await Game.AreaSkillHit("sacrifice-zero", sacrificeWire.Stream,
                            sacrificeWire.Ciphers, sacrificeConn, source.Id, source, from, from,
                            immediate: true, criticalRoll: new SkillCriticalRoll(0));
                    }
                    int zeroUnblindDamage = checked((int)(zeroUnblindBefore - zeroUnblind.Life));
                    int zeroBlindDamage = checked((int)(zeroBlindBefore - zeroBlind.Life));
                    Assert.InRange(zeroUnblindDamage, zeroNormalBounds.Min, zeroNormalBounds.Max);
                    Assert.InRange(zeroBlindDamage, zeroBlindBounds.Min, zeroBlindBounds.Max);
                    Assert.False(zeroUnblind.Poisons.Poisoned);
                    Assert.False(zeroBlind.Poisons.Poisoned);
                    Assert.True(Game.Blinded(sacrificeConn, zeroBlindId));

                    lock (sacrificeConn.Monsters)
                    {
                        sacrificeConn.Monsters.Remove(zeroUnblindId);
                        sacrificeConn.Monsters.Remove(zeroBlindId);
                    }

                    SetStat("global_chance_to_blind_on_hit_%", 100);
                    const uint sacrificeUnblindId = 0xC473B1u, sacrificeBlindId = 0xC473B2u;
                    const uint sacrificeNewBlindId = 0xC473B3u;
                    SpawnedMonster sacrificeUnblind, sacrificeBlind, sacrificeNewBlind;
                    lock (sacrificeConn.Monsters)
                    {
                        sacrificeUnblind = Target(sacrificeUnblindId, "sacrifice-positive-unblinded");
                        sacrificeBlind = Target(sacrificeBlindId, "sacrifice-positive-owned-blind");
                        sacrificeNewBlind = Target(sacrificeNewBlindId, "sacrifice-positive-first-blind");
                        Assert.True(Game.InflictMonsterBlind(sacrificeConn, sacrificeBlindId, sacrificeBlind,
                            Game.ResourceEffectsTime(sacrificeConn), (100, 0, 4), new Random(1)));
                        Assert.True(Game.Blinded(sacrificeConn, sacrificeBlindId));
                        Assert.False(Game.Blinded(sacrificeConn, sacrificeUnblindId));
                        Assert.False(Game.Blinded(sacrificeConn, sacrificeNewBlindId));
                    }
                    AssertNeutralTarget(sacrificeUnblind);
                    AssertNeutralTarget(sacrificeBlind);
                    AssertNeutralTarget(sacrificeNewBlind);

                    uint playerLifeBefore = sacrificeConn.PlayerLife;
                    long representativeLifeShare = (long)playerLifeBefore * sacrificePercent / 100;
                    int representativeAddedChaos = checked((int)(representativeLifeShare * gainPercent / 100));
                    Assert.True(representativeAddedChaos > 0);

                    (int Min, int Max) intendedNormalBounds, intendedBlindBounds;
                    (int Min, int Max) legacyNormalBounds, legacyBlindBounds;
                    MonsterPoisons.Settings intendedNormalPoison, intendedBlindPoison;
                    double effectiveness, rawAddedChaos;
                    double beforeCall = 0, afterCall = 0;
                    uint sacrificeUnblindBefore = sacrificeUnblind.Life;
                    uint sacrificeBlindBefore = sacrificeBlind.Life;
                    uint sacrificeNewBlindBefore = sacrificeNewBlind.Life;
                    using (new Game.PlayerStatScope(sacrificeConn, fresh: true))
                    {
                        var player = Game.CharacterPlayerStats(sacrificeConn);
                        Assert.Equal(rolledBiteDamage,
                            player.GetValueOrDefault(Program.Stats.Key(biteDamageStat)!.Value));
                        Assert.Equal(100, player.GetValueOrDefault(Program.Stats.Key("damage_+%")!.Value));
                        var inputs = Assert.IsType<Game.SupportedHitInputs>(
                            Game.SupportedHitInputsFor(sacrificeConn, source.Id));
                        Assert.Equal(SkillHitKind.Spell, inputs.Plan.Kind);
                        Assert.False(inputs.Attack);
                        Assert.True(inputs.Area);
                        Assert.Equal(rolledBiteDamage, inputs.Get(biteDamageStat));
                        Assert.Equal(100, inputs.Get("damage_+%"));
                        Assert.Equal(1, inputs.Get("global_cannot_crit"));
                        Assert.Equal([DamageType.Cold],
                            inputs.Raw.Components.Select(component => component.Type).ToArray());
                        Assert.Equal(0, inputs.Raw[DamageType.Chaos].Min, 9);

                        effectiveness = Game.AddedEffectiveness(sacrificeConn, source.Effect, source.Level);
                        Assert.Equal(2.3, effectiveness, 9);
                        rawAddedChaos = representativeAddedChaos * effectiveness;
                        var intendedInputs = inputs with
                        {
                            Raw = inputs.Raw.Add(DamageType.Chaos,
                                new DamageRange(rawAddedChaos, rawAddedChaos)),
                        };
                        var normalWithoutExtra = Game.SupportedHitFrom(inputs, suppress: true);
                        var blindWithoutExtra = Game.SupportedHitFrom(
                            inputs, suppress: true, rolledBiteDamage);
                        var intendedNormal = Game.SupportedHitFrom(intendedInputs, suppress: true);
                        var intendedBlind = Game.SupportedHitFrom(
                            intendedInputs, suppress: true, rolledBiteDamage);
                        var legacyNormal = normalWithoutExtra.Add(
                            DamageType.Chaos, new(representativeAddedChaos, representativeAddedChaos));
                        var legacyBlind = blindWithoutExtra.Add(
                            DamageType.Chaos, new(representativeAddedChaos, representativeAddedChaos));
                        Assert.Equal(2d, normalWithoutExtra.MinTotal / inputs.Raw.MinTotal, 9);
                        Assert.Equal(2.4, blindWithoutExtra.MinTotal / inputs.Raw.MinTotal, 9);
                        Assert.Equal(2d,
                            (intendedNormal.MinTotal - normalWithoutExtra.MinTotal) / rawAddedChaos, 9);
                        Assert.Equal(2.4,
                            (intendedBlind.MinTotal - blindWithoutExtra.MinTotal) / rawAddedChaos, 9);
                        Assert.Equal(1d,
                            (legacyNormal.MinTotal - normalWithoutExtra.MinTotal) / representativeAddedChaos, 9);
                        Assert.Equal(1d,
                            (legacyBlind.MinTotal - blindWithoutExtra.MinTotal) / representativeAddedChaos, 9);

                        intendedNormalBounds = FloorComponents(intendedNormal);
                        intendedBlindBounds = FloorComponents(intendedBlind);
                        legacyNormalBounds = FloorComponents(legacyNormal);
                        legacyBlindBounds = FloorComponents(legacyBlind);
                        Assert.True(intendedNormalBounds.Min > legacyNormalBounds.Max,
                            $"normal intervals overlap: intended {intendedNormalBounds}, "
                            + $"legacy {legacyNormalBounds}");
                        Assert.True(intendedBlindBounds.Min > legacyBlindBounds.Max,
                            $"Blind intervals overlap: intended {intendedBlindBounds}, "
                            + $"legacy {legacyBlindBounds}");

                        intendedNormalPoison = Game.SupportedPoison(sacrificeConn, source.Id, spell: true,
                            player, additionalIncreased: 0, capturedInputs: intendedInputs);
                        intendedBlindPoison = Game.SupportedPoison(sacrificeConn, source.Id, spell: true,
                            player, additionalIncreased: rolledBiteDamage, capturedInputs: intendedInputs);
                        const double minuteRounding = 1d / 30;
                        double expectedNormalPoison = .3 * rawAddedChaos * 2;
                        double expectedBlindPoison = expectedNormalPoison * 1.2;
                        Assert.InRange(intendedNormalPoison.Min,
                            expectedNormalPoison - minuteRounding, expectedNormalPoison + minuteRounding);
                        Assert.InRange(intendedNormalPoison.Max,
                            expectedNormalPoison - minuteRounding, expectedNormalPoison + minuteRounding);
                        Assert.InRange(intendedBlindPoison.Min,
                            expectedBlindPoison - minuteRounding, expectedBlindPoison + minuteRounding);
                        Assert.InRange(intendedBlindPoison.Max,
                            expectedBlindPoison - minuteRounding, expectedBlindPoison + minuteRounding);
                        Assert.Equal(100, intendedNormalPoison.Chance);
                        Assert.Equal(intendedNormalPoison.Seconds, intendedBlindPoison.Seconds, 9);
                        Assert.Equal(MonsterIgnites.Settings.None,
                            Game.SupportedBleed(sacrificeConn, source.Id, spell: true, player,
                                additionalIncreased: rolledBiteDamage, capturedInputs: intendedInputs));

                        var callAdmitted = Game.SupportedSkill(sacrificeConn, source.Id);
                        Assert.Equal(source.Effect, callAdmitted.Effect);
                        Assert.Equal(source.Level, callAdmitted.Level);
                        Assert.Single(callAdmitted.Supports, support => support.Effect.Id == "SupportSacrifice");
                        Assert.Equal(rolledBiteDamage,
                            player.GetValueOrDefault(Program.Stats.Key(biteDamageStat)!.Value));
                        Assert.Equal(effectiveness,
                            Game.AddedEffectiveness(sacrificeConn, callAdmitted.Effect, callAdmitted.Level), 9);
                        Console.WriteLine("[gateway] #504 Sacrifice consumer boundary: prepaid addedChaos is a "
                            + $"controlled input ({representativeAddedChaos} raw, {rawAddedChaos:0.###} after "
                            + $"{effectiveness:0.###} effectiveness). This is PREPAID CONSUMER COVERAGE ONLY: "
                            + "the real Sacrifice PAYMENT and runtime projectile producer remain OPEN.");
                        beforeCall = Game.ProjectileWorldTime(sacrificeConn);
                        await Game.AreaSkillHit("sacrifice-positive", sacrificeWire.Stream,
                            sacrificeWire.Ciphers, sacrificeConn, source.Id, source, from, from,
                            immediate: true, criticalRoll: new SkillCriticalRoll(0),
                            addedChaos: representativeAddedChaos);
                        afterCall = Game.ProjectileWorldTime(sacrificeConn);
                        Assert.Equal(playerLifeBefore, sacrificeConn.PlayerLife);
                    }

                    int sacrificeUnblindDamage = checked((int)(sacrificeUnblindBefore - sacrificeUnblind.Life));
                    int sacrificeBlindDamage = checked((int)(sacrificeBlindBefore - sacrificeBlind.Life));
                    int sacrificeNewBlindDamage = checked((int)(sacrificeNewBlindBefore - sacrificeNewBlind.Life));
                    double normalPoisonQueryAt = sacrificeUnblind.Poisons.Until - intendedNormalPoison.Seconds / 2;
                    double blindPoisonQueryAt = sacrificeBlind.Poisons.Until - intendedBlindPoison.Seconds / 2;
                    double neutralPoisonDuration = intendedNormalPoison.Seconds;
                    double firstPoisonUntil = sacrificeNewBlind.Poisons.Until;
                    double firstPoisonStart = firstPoisonUntil - neutralPoisonDuration;
                    double newBlindPoisonQueryAt = firstPoisonUntil - neutralPoisonDuration / 2;
                    double normalPoisonStart = sacrificeUnblind.Poisons.Until - intendedNormalPoison.Seconds;
                    double blindPoisonStart = sacrificeBlind.Poisons.Until - intendedBlindPoison.Seconds;
                    Assert.InRange(normalPoisonStart, beforeCall, afterCall);
                    Assert.InRange(blindPoisonStart, beforeCall, afterCall);
                    Assert.InRange(firstPoisonStart, beforeCall, afterCall);
                    double actualPoison = sacrificeUnblind.Poisons.TotalDps(normalPoisonQueryAt);
                    double actualBlindPoison = sacrificeBlind.Poisons.TotalDps(blindPoisonQueryAt);
                    double actualNewBlindPoison = sacrificeNewBlind.Poisons.TotalDps(newBlindPoisonQueryAt);
                    bool actualPoisoned = sacrificeUnblind.Poisons.Poisoned;
                    Assert.True(sacrificeUnblindDamage >= intendedNormalBounds.Min
                            && sacrificeUnblindDamage <= intendedNormalBounds.Max,
                        $"Sacrifice added {representativeAddedChaos} chaos entered the real immediate "
                        + $"IceNova AreaSkillHit as prepaid consumer input on a legal Witch 100 with actual "
                        + $"Bite {rolledBiteDamage} and real Sacrifice {sacrificePercent}%/{gainPercent}%; "
                        + $"added effectiveness {effectiveness:0.###} makes {rawAddedChaos:0.###} raw chaos, "
                        + $"then generic 100% doubles it before resolution. Expected damage "
                        + $"{intendedNormalBounds}, legacy post-resolution flat append {legacyNormalBounds}, "
                        + $"actual {sacrificeUnblindDamage}; poison actual {actualPoison:0.###} "
                        + $"(poisoned {actualPoisoned}) vs intended raw-chaos "
                        + $"{intendedNormalPoison.Min:0.###}-{intendedNormalPoison.Max:0.###}; "
                        + $"target life {sacrificeUnblindBefore}->{sacrificeUnblind.Life}. "
                        + "This does not prove real life sacrifice.");
                    Assert.True(sacrificeBlindDamage >= intendedBlindBounds.Min
                            && sacrificeBlindDamage <= intendedBlindBounds.Max,
                        $"Sacrifice raw chaos + Bite {rolledBiteDamage} damage on an owned-Blind target "
                        + $"expected {intendedBlindBounds}, legacy post-resolution flat append "
                        + $"{legacyBlindBounds}, actual {sacrificeBlindDamage}; intended poison "
                        + $"{intendedBlindPoison.Min:0.###}-{intendedBlindPoison.Max:0.###}; "
                        + $"target life {sacrificeBlindBefore}->{sacrificeBlind.Life}.");
                    Assert.True(sacrificeNewBlindDamage >= intendedNormalBounds.Min
                            && sacrificeNewBlindDamage <= intendedNormalBounds.Max,
                        $"the first positive hit's new Blind changed its own normal hit profile: expected "
                        + $"{intendedNormalBounds}, actual {sacrificeNewBlindDamage}.");
                    Assert.InRange(actualPoison,
                        intendedNormalPoison.Min - 1d / 30, intendedNormalPoison.Min + 1d / 30);
                    Assert.InRange(actualBlindPoison,
                        intendedBlindPoison.Min - 1d / 30, intendedBlindPoison.Min + 1d / 30);
                    Assert.InRange(actualNewBlindPoison,
                        intendedNormalPoison.Min - 1d / 30, intendedNormalPoison.Min + 1d / 30);
                    Assert.True(actualPoisoned);
                    Assert.True(Game.Blinded(sacrificeConn, sacrificeBlindId));
                    Assert.True(Game.Blinded(sacrificeConn, sacrificeNewBlindId),
                        "the first positive hit did not apply its controlled Blind");
                    Assert.False(sacrificeUnblind.Ignites.Burning);
                    Assert.False(sacrificeNewBlind.Ignites.Burning);

                    void KeepOnly(uint id)
                    {
                        lock (sacrificeConn.Monsters)
                            foreach (uint stale in sacrificeConn.Monsters.Keys.Where(key => key != id).ToArray())
                                sacrificeConn.Monsters.Remove(stale);
                    }
                    SpawnedMonster FreshTarget(uint id, string label)
                    {
                        lock (sacrificeConn.Monsters)
                        {
                            foreach (uint stale in sacrificeConn.Monsters.Keys.ToArray())
                                sacrificeConn.Monsters.Remove(stale);
                            return Target(id, label);
                        }
                    }

                    KeepOnly(sacrificeNewBlindId);
                    fixedRuntime.Advance(.125);
                    uint secondLifeBefore = sacrificeNewBlind.Life;
                    double secondBeforeCall = 0, secondAfterCall = 0;
                    using (new Game.PlayerStatScope(sacrificeConn, fresh: true))
                    {
                        var player = Game.CharacterPlayerStats(sacrificeConn);
                        Assert.Equal(rolledBiteDamage,
                            player.GetValueOrDefault(Program.Stats.Key(biteDamageStat)!.Value));
                        Assert.Equal(rolledBiteDamage, Game.SupportedHitInputsFor(
                            sacrificeConn, source.Id)!.Get(biteDamageStat));
                        secondBeforeCall = Game.ProjectileWorldTime(sacrificeConn);
                        await Game.AreaSkillHit("sacrifice-positive-second", sacrificeWire.Stream,
                            sacrificeWire.Ciphers, sacrificeConn, source.Id, source, from, from,
                            immediate: true, criticalRoll: new SkillCriticalRoll(0),
                            addedChaos: representativeAddedChaos);
                        secondAfterCall = Game.ProjectileWorldTime(sacrificeConn);
                    }
                    int secondDamage = checked((int)(secondLifeBefore - sacrificeNewBlind.Life));
                    Assert.InRange(secondDamage, intendedBlindBounds.Min, intendedBlindBounds.Max);
                    double secondPoisonUntil = sacrificeNewBlind.Poisons.Until;
                    double secondPoisonStart = secondPoisonUntil - neutralPoisonDuration;
                    Assert.InRange(secondPoisonStart, secondBeforeCall, secondAfterCall);
                    Assert.True(secondPoisonStart > firstPoisonStart);
                    double firstOnlyQueryAt =
                        (firstPoisonStart + Math.Min(firstPoisonUntil, secondPoisonStart)) / 2;
                    Assert.True(firstPoisonStart <= firstOnlyQueryAt && firstOnlyQueryAt < firstPoisonUntil
                        && firstOnlyQueryAt < secondPoisonStart);
                    Assert.Equal(1, sacrificeNewBlind.Poisons.Stacks(firstOnlyQueryAt));
                    double firstOnlyDps = sacrificeNewBlind.Poisons.TotalDps(firstOnlyQueryAt);
                    Assert.InRange(firstOnlyDps,
                        intendedNormalPoison.Min - 1d / 30, intendedNormalPoison.Min + 1d / 30);
                    double secondOnlyQueryAt = (secondPoisonStart + secondPoisonUntil) / 2;
                    double firstContribution =
                        firstPoisonStart <= secondOnlyQueryAt && secondOnlyQueryAt < firstPoisonUntil
                            ? intendedNormalPoison.Min
                            : 0;
                    double secondOnlyDps = sacrificeNewBlind.Poisons.TotalDps(secondOnlyQueryAt)
                        - firstContribution;
                    Assert.InRange(secondOnlyDps,
                        intendedBlindPoison.Min - 1d / 30, intendedBlindPoison.Min + 1d / 30);

                    const uint isolatedBiteId = 0xC473C7u;
                    var isolatedBite = FreshTarget(isolatedBiteId, "sacrifice-isolated-bite-poison");
                    lock (sacrificeConn.Monsters)
                        Assert.True(Game.InflictMonsterBlind(sacrificeConn, isolatedBiteId, isolatedBite,
                            Game.ResourceEffectsTime(sacrificeConn), (100, 0, 4), new Random(1)));
                    uint isolatedBiteBefore = isolatedBite.Life;
                    double isolatedBiteBeforeCall = 0, isolatedBiteAfterCall = 0;
                    using (new Game.PlayerStatScope(sacrificeConn, fresh: true))
                    {
                        Assert.Equal(rolledBiteDamage,
                            Game.CharacterPlayerStats(sacrificeConn)
                                .GetValueOrDefault(Program.Stats.Key(biteDamageStat)!.Value));
                        isolatedBiteBeforeCall = Game.ProjectileWorldTime(sacrificeConn);
                        await Game.AreaSkillHit("sacrifice-isolated-bite", sacrificeWire.Stream,
                            sacrificeWire.Ciphers, sacrificeConn, source.Id, source, from, from,
                            immediate: true, criticalRoll: new SkillCriticalRoll(0),
                            addedChaos: representativeAddedChaos);
                        isolatedBiteAfterCall = Game.ProjectileWorldTime(sacrificeConn);
                    }
                    int isolatedBiteDamage = checked((int)(isolatedBiteBefore - isolatedBite.Life));
                    Assert.InRange(isolatedBiteDamage, intendedBlindBounds.Min, intendedBlindBounds.Max);
                    double isolatedBiteQueryAt =
                        isolatedBite.Poisons.Until - intendedBlindPoison.Seconds / 2;
                    double isolatedBiteStart =
                        isolatedBite.Poisons.Until - intendedBlindPoison.Seconds;
                    Assert.InRange(isolatedBiteStart, isolatedBiteBeforeCall, isolatedBiteAfterCall);
                    Assert.Equal(1, isolatedBite.Poisons.Stacks(isolatedBiteQueryAt));
                    Assert.InRange(isolatedBite.Poisons.TotalDps(isolatedBiteQueryAt),
                        intendedBlindPoison.Min - 1d / 30, intendedBlindPoison.Min + 1d / 30);

                    var removedBite = inventory.Remove(PlayerInventories.Helmet, bitePlacement.Id);
                    Assert.NotNull(removedBite);
                    Game.ReconcileOwnAuras(sacrificeConn, validateSkills: true);
                    Assert.InRange(sacrificeNewBlind.Poisons.TotalDps(firstOnlyQueryAt),
                        intendedNormalPoison.Min - 1d / 30, intendedNormalPoison.Min + 1d / 30);
                    Assert.Equal(firstOnlyDps,
                        sacrificeNewBlind.Poisons.TotalDps(firstOnlyQueryAt), 9);
                    Assert.Equal(secondOnlyDps,
                        sacrificeNewBlind.Poisons.TotalDps(secondOnlyQueryAt) - firstContribution, 9);
                    Assert.Equal(secondPoisonUntil, sacrificeNewBlind.Poisons.Until, 9);

                    const uint withdrawnId = 0xC473C6u;
                    var withdrawn = FreshTarget(withdrawnId, "sacrifice-source-withdrawn");
                    lock (sacrificeConn.Monsters)
                        Assert.True(Game.InflictMonsterBlind(sacrificeConn, withdrawnId, withdrawn,
                            Game.ResourceEffectsTime(sacrificeConn), (100, 0, 4), new Random(1)));
                    uint withdrawnLifeBefore = withdrawn.Life;
                    double withdrawnBeforeCall = 0, withdrawnAfterCall = 0;
                    using (new Game.PlayerStatScope(sacrificeConn, fresh: true))
                    {
                        Assert.Equal(0, Game.CharacterPlayerStats(sacrificeConn)
                            .GetValueOrDefault(Program.Stats.Key(biteDamageStat)!.Value));
                        Assert.Equal(0, Game.SupportedHitInputsFor(sacrificeConn, source.Id)!
                            .Get(biteDamageStat));
                        withdrawnBeforeCall = Game.ProjectileWorldTime(sacrificeConn);
                        await Game.AreaSkillHit("sacrifice-positive-withdrawn", sacrificeWire.Stream,
                            sacrificeWire.Ciphers, sacrificeConn, source.Id, source, from, from,
                            immediate: true, criticalRoll: new SkillCriticalRoll(0),
                            addedChaos: representativeAddedChaos);
                        withdrawnAfterCall = Game.ProjectileWorldTime(sacrificeConn);
                    }
                    int withdrawnDamage = checked((int)(withdrawnLifeBefore - withdrawn.Life));
                    Assert.InRange(withdrawnDamage, intendedNormalBounds.Min, intendedNormalBounds.Max);
                    double withdrawnPoisonQueryAt =
                        withdrawn.Poisons.Until - intendedNormalPoison.Seconds / 2;
                    double withdrawnPoisonStart =
                        withdrawn.Poisons.Until - intendedNormalPoison.Seconds;
                    Assert.InRange(withdrawnPoisonStart, withdrawnBeforeCall, withdrawnAfterCall);
                    Assert.InRange(withdrawn.Poisons.TotalDps(withdrawnPoisonQueryAt),
                        intendedNormalPoison.Min - 1d / 30, intendedNormalPoison.Min + 1d / 30);

                    uint zeroAfterWithdrawBefore = withdrawn.Life;
                    double zeroAfterWithdrawDps = withdrawn.Poisons.TotalDps(withdrawnPoisonQueryAt);
                    double zeroAfterWithdrawUntil = withdrawn.Poisons.Until;
                    await Game.AreaSkillHit("sacrifice-zero-after-withdraw", sacrificeWire.Stream,
                        sacrificeWire.Ciphers, sacrificeConn, source.Id, source, from, from,
                        immediate: true, criticalRoll: new SkillCriticalRoll(0));
                    int zeroAfterWithdrawDamage =
                        checked((int)(zeroAfterWithdrawBefore - withdrawn.Life));
                    Assert.InRange(zeroAfterWithdrawDamage, zeroNormalBounds.Min, zeroNormalBounds.Max);
                    Assert.Equal(zeroAfterWithdrawDps,
                        withdrawn.Poisons.TotalDps(withdrawnPoisonQueryAt), 9);
                    Assert.Equal(zeroAfterWithdrawUntil, withdrawn.Poisons.Until, 9);

                    const uint negativeId = 0xC473C1u;
                    var negative = FreshTarget(negativeId, "sacrifice-negative-extra");
                    uint negativeBefore = negative.Life;
                    await Game.AreaSkillHit("sacrifice-negative", sacrificeWire.Stream, sacrificeWire.Ciphers,
                        sacrificeConn, source.Id, source, from, from, immediate: true,
                        criticalRoll: new SkillCriticalRoll(0), addedChaos: -1);
                    int negativeDamage = checked((int)(negativeBefore - negative.Life));
                    Assert.InRange(negativeDamage, zeroNormalBounds.Min, zeroNormalBounds.Max);
                    Assert.False(negative.Poisons.Poisoned);

                    SetStat("base_chance_to_poison_on_hit_%", 0);
                    const uint zeroChanceId = 0xC473C2u;
                    var zeroChance = FreshTarget(zeroChanceId, "sacrifice-zero-poison-chance");
                    uint zeroChanceBefore = zeroChance.Life;
                    await Game.AreaSkillHit("sacrifice-zero-poison-chance", sacrificeWire.Stream,
                        sacrificeWire.Ciphers, sacrificeConn, source.Id, source, from, from,
                        immediate: true, criticalRoll: new SkillCriticalRoll(0),
                        addedChaos: representativeAddedChaos);
                    int zeroChanceDamage = checked((int)(zeroChanceBefore - zeroChance.Life));
                    Assert.InRange(zeroChanceDamage, intendedNormalBounds.Min, intendedNormalBounds.Max);
                    Assert.False(zeroChance.Poisons.Poisoned);
                    SetStat("base_chance_to_poison_on_hit_%", 100);

                    const uint poisonImmuneId = 0xC473C3u;
                    var poisonImmune = FreshTarget(poisonImmuneId, "sacrifice-poison-immune");
                    poisonImmune.Mods = MonsterModSet.None with
                    {
                        Effects = new MonsterModEffects(new Dictionary<string, int>
                        {
                            ["cannot_be_poisoned"] = 1,
                        }),
                    };
                    uint poisonImmuneBefore = poisonImmune.Life;
                    await Game.AreaSkillHit("sacrifice-poison-immune", sacrificeWire.Stream,
                        sacrificeWire.Ciphers, sacrificeConn, source.Id, source, from, from,
                        immediate: true, criticalRoll: new SkillCriticalRoll(0),
                        addedChaos: representativeAddedChaos);
                    int poisonImmuneDamage = checked((int)(poisonImmuneBefore - poisonImmune.Life));
                    Assert.InRange(poisonImmuneDamage, intendedNormalBounds.Min, intendedNormalBounds.Max);
                    Assert.False(poisonImmune.Poisons.Poisoned);

                    const uint blindImmuneId = 0xC473C4u;
                    var blindImmune = FreshTarget(blindImmuneId, "sacrifice-blind-immune");
                    blindImmune.Mods = MonsterModSet.None with
                    {
                        Effects = new MonsterModEffects(new Dictionary<string, int>
                        {
                            ["cannot_be_blinded"] = 1,
                        }),
                    };
                    uint blindImmuneBefore = blindImmune.Life;
                    await Game.AreaSkillHit("sacrifice-blind-immune", sacrificeWire.Stream,
                        sacrificeWire.Ciphers, sacrificeConn, source.Id, source, from, from,
                        immediate: true, criticalRoll: new SkillCriticalRoll(0),
                        addedChaos: representativeAddedChaos);
                    int blindImmuneDamage = checked((int)(blindImmuneBefore - blindImmune.Life));
                    Assert.InRange(blindImmuneDamage, intendedNormalBounds.Min, intendedNormalBounds.Max);
                    Assert.False(Game.Blinded(sacrificeConn, blindImmuneId));
                    double blindImmunePoisonQueryAt =
                        blindImmune.Poisons.Until - intendedNormalPoison.Seconds / 2;
                    Assert.InRange(blindImmune.Poisons.TotalDps(blindImmunePoisonQueryAt),
                        intendedNormalPoison.Min - 1d / 30, intendedNormalPoison.Min + 1d / 30);

                    const uint forbiddenId = 0xC473C5u;
                    var forbidden = FreshTarget(forbiddenId, "sacrifice-chaos-prohibited");
                    uint forbiddenBefore = forbidden.Life;
                    try
                    {
                        Assert.NotNull(inventory.Change(PlayerInventories.Weapon1, swordPlacement.Id,
                            item => item with
                            {
                                Sockets = [new(SocketColour.White, iceNovaGem),
                                    new(SocketColour.White, sacrificeSupportGem),
                                    new(SocketColour.White, brutalitySupportGem)],
                                Links = [3],
                            }));
                        Game.ReconcileOwnAuras(sacrificeConn, validateSkills: true);
                        var forbiddenGem = Game.SupportedSkill(sacrificeConn, source.Id);
                        var brutality = Assert.Single(forbiddenGem.Supports,
                            support => support.Effect.Id == "SupportBrutality");
                        Assert.Equal(1, brutality.Gem.Level);
                        Assert.NotNull(Program.Stats.Key("base_deal_no_chaos_damage"));
                        var forbiddenInputs = Assert.IsType<Game.SupportedHitInputs>(
                            Game.SupportedHitInputsFor(sacrificeConn, source.Id));
                        Assert.NotEqual(0, forbiddenInputs.Get("base_deal_no_chaos_damage"));
                        Assert.True(DamageStats.Prohibited(DamageType.Chaos, forbiddenInputs.Get));
                        Assert.True(MonsterPoisons.Prohibited(forbiddenInputs.Get));
                        var forbiddenRaw = forbiddenInputs with
                        {
                            Raw = forbiddenInputs.Raw.Add(DamageType.Chaos,
                                new DamageRange(rawAddedChaos, rawAddedChaos)),
                        };
                        Assert.True(Game.SupportedHitFrom(forbiddenRaw, suppress: true).IsZero,
                            "the real linked Brutality prohibition did not suppress the augmented hit");
                        await Game.AreaSkillHit("sacrifice-chaos-prohibited", sacrificeWire.Stream,
                            sacrificeWire.Ciphers, sacrificeConn, source.Id, source, from, from,
                            immediate: true, criticalRoll: new SkillCriticalRoll(0),
                            addedChaos: representativeAddedChaos);
                    }
                    finally
                    {
                        Assert.NotNull(inventory.Change(PlayerInventories.Weapon1, swordPlacement.Id,
                            item => item with { Sockets = sword.Sockets, Links = sword.Links }));
                        Game.ReconcileOwnAuras(sacrificeConn, validateSkills: true);
                    }
                    Assert.Equal(forbiddenBefore, forbidden.Life);
                    Assert.False(forbidden.Poisons.Poisoned);

                    inventory.Add(PlayerInventories.Helmet, 0, 0, bite);
                    Game.ReconcileOwnAuras(sacrificeConn, validateSkills: true);
                    using (new Game.PlayerStatScope(sacrificeConn, fresh: true))
                        Assert.Equal(rolledBiteDamage,
                            Game.CharacterPlayerStats(sacrificeConn)
                                .GetValueOrDefault(Program.Stats.Key(biteDamageStat)!.Value));
                }
                finally
                {
                    if (sacrificeConnection is not null)
                    {
                        sacrificeConnection.ProjectileRuntimeState = previousRuntime;
                        sacrificeConnection.Closed = true;
                    }
                    fixedRuntime?.Invalidate();
                    Game.AreaUpdateContext.Value = previousContext;
                    PlayerInventories.Forget(character);
                }
            }

            async Task RealProjectileSacrificePaidProducerFixture()
            {
                // This is the first producer test: the control proves a real Fireball use reaches the production
                // projectile runtime and pays its canonical mana cost. Only then does the linked use assert that
                // accepted CastGemSkill paid Sacrifice's current-life share. No prepaid argument or manual subtraction
                // enters either cast.
                string character = "ProjectileSacrificePaid" + Guid.NewGuid().ToString("N");
                const byte witchRow = 1;
                PlayerInventories.SetClass(character, witchRow);
                CharacterSaves.Write(character, new(new Dictionary<byte, List<CharacterSaves.SavedPlacement>>(),
                    Experience: CharacterStats.ExperienceFor(100)));
                var updates = new PlayerUpdatesState(lifetime, new CatClockSchedule { At = 300 });
                var previousContext = Game.AreaUpdateContext.Value;
                Game.AreaUpdateContext.Value = updates;
                using var paidWire = new Wire();
                ClientState? paidConnection = null;
                IProjectileRuntime? previousRuntime = null;
                Game.ProjectileSession? paidSession = null;
                try
                {
                    paidConnection = new ClientState(conn.Layout, 0xC473D0, 1)
                    {
                        Character = character, CharacterId = Guid.NewGuid(), Account = "projectile-sacrifice-paid-account",
                        League = "Standard", LeagueForm = "Standard", ClassRow = witchRow, Instance = new(),
                        PlayerSent = true, PlayerObjectCreated = true, PlayerUpdatesState = updates,
                        Experience = CharacterStats.ExperienceFor(100), LeagueModule = conn.LeagueModule,
                        Wire = ("projectile-sacrifice-paid", paidWire.Stream, paidWire.Ciphers),
                        Pools = new(100, 1, 1, 0, 0),
                    };
                    var paidConn = paidConnection;
                    var inventory = Game.ItemsOf(paidConn);
                    foreach (byte row in new byte[] { PlayerInventories.Weapon1, PlayerInventories.Offhand1,
                                 PlayerInventories.Helmet, 5, 6, 7 })
                        foreach (var old in inventory.Items(row)) inventory.Remove(row, old.Id);

                    Item Gem(string name, int level)
                    {
                        var type = Program.Items.FindGem(name)
                            ?? throw new InvalidOperationException("missing creatable gem " + name);
                        return ItemCatalog.Create(type.Type) with
                        {
                            GemVariant = type.Variant.Index,
                            GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(level),
                        };
                    }
                    void SetStat(string name, int value)
                    {
                        uint key = Program.Stats.Key(name)
                            ?? throw new InvalidOperationException("missing required stat " + name);
                        paidConn.PlayerStats[key] = value;
                    }
                    void AssertNoChaos(string label, DamagePacket packet)
                    {
                        Assert.Equal(0, packet[DamageType.Chaos].Min);
                        Assert.Equal(0, packet[DamageType.Chaos].Max);
                    }
                    uint CanonicalManaCost(ushort skillId)
                    {
                        uint key = Program.Stats.Key("mana_cost")
                            ?? throw new InvalidOperationException("missing required stat mana_cost");
                        return (uint)Math.Max(0, Game.SupportedStats(paidConn, skillId).GetValueOrDefault(key));
                    }
                    double ReleaseAt(double acceptedAt, int castMs)
                    {
                        double castSeconds = castMs / 1000d;
                        double releaseDelay = castSeconds * Game.Animations.SpellRelease(
                            Game.SkillsFor(paidConn).ClassFor(paidConn.ClassRow).AnimatedObject);
                        return acceptedAt + releaseDelay;
                    }

                    var fireballGem = Gem("Fireball", 1);
                    var sacrificeGem = Gem("Sacrifice Support", 1);
                    var controlSword = ItemCatalog.Create(Program.Items.Find(
                        "Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1")!) with
                    {
                        Sockets = [new(SocketColour.White, fireballGem)],
                    };
                    var controlPlacement = inventory.Add(PlayerInventories.Weapon1, 0, 0, controlSword);
                    inventory.Add(5, 0, 0,
                        new UniqueCatalog(Program.Items).Create("Astramentis", new Random(504))!);
                    inventory.Add(6, 0, 0,
                        new UniqueCatalog(Program.Items).Create("Le Heup of All", new Random(504))!);
                    inventory.Add(7, 0, 0,
                        new UniqueCatalog(Program.Items).Create("Le Heup of All", new Random(505))!);
                    var bite = new UniqueCatalog(Program.Items).Create("Farrul's Bite", new Random(504))!;
                    var bitePlacement = inventory.Add(PlayerInventories.Helmet, 0, 0, bite);
                    paidConn.Pools = Game.PoolsFor(paidConn, 100);
                    paidConn.PlayerLife = paidConn.Pools.Life;
                    paidConn.PlayerMana = paidConn.Pools.Mana;
                    Game.ReconcileOwnAuras(paidConn, validateSkills: true);
                    Assert.Equal(100, paidConn.Pools.Level);
                    Assert.False(Game.IsInactive(paidConn, controlPlacement.Id));
                    Assert.False(Game.IsInactive(paidConn, bitePlacement.Id));

                    var biteDamageMod = Assert.Single(bite.Mods, mod =>
                        Program.Items.Mods.ByHash(mod.Hash)?.Id == "DamageAgainstBlindedEnemiesUnique__1");
                    string biteDamageStat =
                        Assert.Single(Program.Items.Mods.ByHash(biteDamageMod.Hash)!.Stats).Id;
                    int rolledBiteDamage = Assert.Single(biteDamageMod.Values);
                    Assert.Equal(40, rolledBiteDamage);

                    // Dev controls, not an ordinary build: 100% generic damage, guaranteed poison chance and no
                    // criticals keep the consumer arithmetic explicit. Real attributes and requirements still come
                    // from the actual Witch, Astramentis, Le Heup of All and socketed gems.
                    foreach (string type in new[] { "physical", "cold", "fire", "lightning", "chaos" })
                    {
                        SetStat($"spell_minimum_added_{type}_damage", 0);
                        SetStat($"spell_maximum_added_{type}_damage", 0);
                        SetStat($"attack_minimum_added_{type}_damage", 0);
                        SetStat($"attack_maximum_added_{type}_damage", 0);
                    }
                    SetStat("damage_+%", 100);
                    SetStat("global_cannot_crit", 1);
                    SetStat("global_chance_to_blind_on_hit_%", 100);

                    var controlSource = Assert.Single(Game.GemSkillEffects(paidConn), gem =>
                        Game.SkillsFor(paidConn).EffectId(gem.Effect) == "Fireball");
                    Assert.Equal(1, controlSource.Level);
                    Assert.DoesNotContain(Game.SupportedSkill(paidConn, controlSource.Id).Supports,
                        support => support.Effect.Id == "SupportSacrifice");
                    var from = paidConn.Walk.Position(Game.ProjectileWorldTime(paidConn));
                    (int X, int Y)? clearAim = null;
                    foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int steps = 1;
                        for (; steps <= 12; steps++)
                            if (ProjectileGrid.Blocks(paidConn.Layout.Id, paidConn.Seed,
                                    from.X + dx * steps, from.Y + dy * steps) == true) break;
                        if (steps > 8)
                        {
                            clearAim = (from.X + dx * 8, from.Y + dy * 8);
                            break;
                        }
                    }
                    Assert.NotNull(clearAim);
                    var aim = clearAim.Value;
                    const uint controlTargetId = 0xC473D1u;
                    var controlTarget = new SpawnedMonster("fireball-unlinked-control",
                        new(aim.X, aim.Y, 0), 0, 0, 1_000_000)
                    {
                        Level = 100, Hostile = true, Life = 1_000_000,
                    };
                    lock (paidConn.Monsters)
                    {
                        paidConn.Monsters[controlTargetId] = controlTarget;
                        Assert.True(Game.InflictMonsterBlind(paidConn, controlTargetId, controlTarget,
                            Game.ResourceEffectsTime(paidConn), (100, 0, 4), new Random(1)));
                        Assert.True(Game.Blinded(paidConn, controlTargetId));
                    }

                    previousRuntime = paidConn.ProjectileRuntimeState;
                    paidSession = new Game.ProjectileSession(
                        "fireball-unlinked-control", paidWire.Stream, paidWire.Ciphers, paidConn);
                    paidConn.ProjectileRuntime = paidSession;
                    Assert.Same(paidSession, paidConn.ProjectileRuntime);
                    Assert.True(paidSession.Active);
                    Assert.False(paidConn.Paused);
                    Assert.False(Game.InTown(paidConn));
                    Assert.False(paidConn.Closed);
                    Assert.False(paidConn.LeavingArea);
                    Assert.True(Game.CurrentGameRules);

                    bool controlAccepted;
                    uint controlLifeBefore, controlLifeAfter, controlManaBefore, controlManaAfter;
                    uint controlManaCost;
                    int controlCastMs;
                    double controlAcceptedAt, controlReleaseAt;
                    ProjectileOffense controlShot;
                    using (new Game.PlayerStatScope(paidConn, fresh: true))
                    {
                        var player = Game.CharacterPlayerStats(paidConn);
                        Assert.Equal(rolledBiteDamage,
                            player.GetValueOrDefault(Program.Stats.Key(biteDamageStat)!.Value));
                        Assert.Equal(100, player.GetValueOrDefault(Program.Stats.Key("damage_+%")!.Value));
                        Assert.Equal(1, player.GetValueOrDefault(Program.Stats.Key("global_cannot_crit")!.Value));
                        controlManaCost = CanonicalManaCost(controlSource.Id);
                        Assert.True(controlManaCost > 0);
                        controlCastMs = Game.CastMs(paidConn, controlSource.Effect, controlSource.Id)
                            ?? throw new InvalidOperationException("Fireball has no production cast duration");
                        Assert.True(controlCastMs > 0);
                        controlLifeBefore = paidConn.PlayerLife;
                        controlManaBefore = paidConn.PlayerMana;
                        controlAcceptedAt = Game.ProjectileWorldTime(paidConn);
                        controlReleaseAt = ReleaseAt(controlAcceptedAt, controlCastMs);
                        controlAccepted = await Game.CastGemSkill(
                            "fireball-unlinked-control", paidWire.Stream, paidWire.Ciphers, paidConn,
                            controlSource.Id, from, aim, targetId: controlTargetId, swingAt: controlAcceptedAt);
                        controlLifeAfter = paidConn.PlayerLife;
                        controlManaAfter = paidConn.PlayerMana;
                    }
                    Assert.True(controlAccepted,
                        "the real unlinked Fireball control was rejected before the paid-producer assertion");
                    Assert.Equal(controlManaCost, controlManaBefore - controlManaAfter);
                    Assert.Equal(controlLifeBefore, controlLifeAfter);

                    paidSession.AdvanceTo(Math.Max(
                        paidSession.Runtime.Now, ProjectileRuntime.Time(controlReleaseAt)));
                    var controlProjectile = Assert.Single(paidSession.Runtime.Projectiles);
                    controlShot = Assert.IsType<ProjectileOffense>(controlProjectile.Use.Payload);
                    AssertNoChaos("control direct", controlShot.Hit);
                    if (controlShot.AreaHit is { } controlArea) AssertNoChaos("control area", controlArea);
                    Assert.Equal(0, controlShot.Poison.Chance);
                    Assert.Equal(0, controlShot.Poison.Min);
                    Assert.Equal(0, controlShot.Poison.Max);
                    var controlBlind = Assert.IsType<ProjectileOffenseAlternate>(controlShot.VsBlinded);
                    AssertNoChaos("control blinded direct", controlBlind.Hit);
                    if (controlBlind.AreaHit is { } controlBlindArea)
                        AssertNoChaos("control blinded area", controlBlindArea);
                    Assert.Equal(0, controlBlind.Poison.Chance);
                    Assert.Equal(0, controlBlind.Poison.Min);
                    Assert.Equal(0, controlBlind.Poison.Max);

                    using (new Game.PlayerStatScope(paidConn, fresh: true))
                    {
                        var inputs = Assert.IsType<Game.SupportedHitInputs>(
                            Game.SupportedHitInputsFor(paidConn, controlSource.Id, area: false));
                        Assert.Equal(100, inputs.Get("damage_+%"));
                        Assert.Equal(rolledBiteDamage, inputs.Get(biteDamageStat));
                        var expectedNormal = Game.SupportedHitFrom(inputs, suppress: true);
                        var expectedBlind = Game.SupportedHitFrom(
                            inputs, suppress: true, rolledBiteDamage);
                        Assert.Equal(expectedNormal.MinTotal, controlShot.Hit.MinTotal, 9);
                        Assert.Equal(expectedNormal.MaxTotal, controlShot.Hit.MaxTotal, 9);
                        Assert.Equal(expectedBlind.MinTotal, controlBlind.Hit.MinTotal, 9);
                        Assert.Equal(expectedBlind.MaxTotal, controlBlind.Hit.MaxTotal, 9);
                        Assert.Equal(2d, expectedNormal.MinTotal / inputs.Raw.MinTotal, 9);
                        Assert.Equal(2.4, expectedBlind.MinTotal / inputs.Raw.MinTotal, 9);
                    }

                    uint controlContactBefore = controlTarget.Life;
                    for (int step = 0; step < 120 && controlTarget.Life == controlContactBefore; step++)
                        paidSession.AdvanceTo(paidSession.Runtime.Now + ProjectileRuntime.Time(1d / 60));
                    bool controlContacted = controlTarget.Life < controlContactBefore;
                    bool controlPoisoned;
                    lock (paidConn.Monsters) controlPoisoned = controlTarget.Poisons.Poisoned;
                    Assert.False(controlPoisoned);

                    paidConn.ProjectileRuntime = null;
                    paidSession.Invalidate();
                    await paidSession.OutputCompleted;
                    paidSession = null;

                    var removedControl = inventory.Remove(PlayerInventories.Weapon1, controlPlacement.Id);
                    Assert.NotNull(removedControl);
                    var linkedSword = controlSword with
                    {
                        Sockets = [new(SocketColour.White, fireballGem),
                            new(SocketColour.White, sacrificeGem)],
                        Links = [2],
                    };
                    var linkedPlacement = inventory.Add(PlayerInventories.Weapon1, 0, 0, linkedSword);
                    Assert.False(Game.IsInactive(paidConn, linkedPlacement.Id));
                    Game.ReconcileOwnAuras(paidConn, validateSkills: true);
                    var source = Assert.Single(Game.GemSkillEffects(paidConn), gem =>
                        Game.SkillsFor(paidConn).EffectId(gem.Effect) == "Fireball");
                    Assert.Equal(1, source.Level);
                    SetStat("base_chance_to_poison_on_hit_%", 100);
                    Game.ReconcileOwnAuras(paidConn, validateSkills: true);

                    const uint strictTargetId = 0xC473D2u;
                    var strictTarget = new SpawnedMonster("fireball-linked-sacrifice",
                        new(aim.X, aim.Y, 0), 0, 0, 1_000_000)
                    {
                        Level = 100, Hostile = true, Life = 1_000_000,
                    };
                    lock (paidConn.Monsters)
                    {
                        paidConn.Monsters[strictTargetId] = strictTarget;
                        Assert.True(Game.InflictMonsterBlind(paidConn, strictTargetId, strictTarget,
                            Game.ResourceEffectsTime(paidConn), (100, 0, 4), new Random(1)));
                        Assert.True(Game.Blinded(paidConn, strictTargetId));
                        Assert.Equal(0, strictTarget.Poisons.Stacks(
                            Game.ResourceEffectsTime(paidConn)));
                    }

                    paidSession = new Game.ProjectileSession(
                        "fireball-linked-sacrifice", paidWire.Stream, paidWire.Ciphers, paidConn);
                    paidConn.ProjectileRuntime = paidSession;
                    Assert.Same(paidSession, paidConn.ProjectileRuntime);
                    Assert.True(paidSession.Active);

                    bool strictAccepted;
                    uint strictLifeBefore, strictLifeAfter, strictManaBefore, strictManaAfter;
                    uint strictManaCost;
                    int sacrificePercent, gainPercent, strictCastMs;
                    int expectedSacrificed, expectedRawPrepaid;
                    double effectiveness, expectedRawChaos;
                    double strictAcceptedAt, strictReleaseAt;
                    double actualChaosNormal, actualChaosBlind, actualChaosArea;
                    int actualPoisonChance;
                    DamagePacket expectedStrictHit, expectedStrictAreaHit;
                    DamagePacket expectedStrictBlindHit, expectedStrictBlindAreaHit;
                    MonsterPoisons.Settings expectedStrictPoison, expectedStrictBlindPoison;
                    using (new Game.PlayerStatScope(paidConn, fresh: true))
                    {
                        var admitted = Game.SupportedSkill(paidConn, source.Id);
                        Assert.Equal(source.Effect, admitted.Effect);
                        Assert.Equal(source.Level, admitted.Level);
                        var sacrifice = Assert.Single(admitted.Supports,
                            support => support.Effect.Id == "SupportSacrifice");
                        Assert.Equal(1, sacrifice.Gem.Level);
                        int SupportStat(string name)
                        {
                            uint key = Program.Stats.Key(name)
                                ?? throw new InvalidOperationException("missing required stat " + name);
                            return Assert.Single(sacrifice.Stats, stat => stat.Key == key).Value;
                        }
                        sacrificePercent = SupportStat(
                            "support_sacrifice_sacrifice_%_of_current_life");
                        gainPercent = SupportStat(
                            "support_sacrifice_gain_%_of_sacrificed_life_as_added_chaos_damage");
                        Assert.Equal(20, sacrificePercent);
                        Assert.Equal(35, gainPercent);
                        effectiveness = Game.AddedEffectiveness(
                            paidConn, admitted.Effect, admitted.Level);
                        Assert.True(effectiveness > 0);
                        var player = Game.CharacterPlayerStats(paidConn);
                        Assert.Equal(rolledBiteDamage,
                            player.GetValueOrDefault(Program.Stats.Key(biteDamageStat)!.Value));
                        Assert.Equal(100, player.GetValueOrDefault(Program.Stats.Key("damage_+%")!.Value));
                        Assert.Equal(100, player.GetValueOrDefault(
                            Program.Stats.Key("base_chance_to_poison_on_hit_%")!.Value));
                        Assert.Equal(1, player.GetValueOrDefault(Program.Stats.Key("global_cannot_crit")!.Value));
                        strictManaCost = CanonicalManaCost(source.Id);
                        Assert.True(strictManaCost > 0);
                        strictCastMs = Game.CastMs(paidConn, source.Effect, source.Id)
                            ?? throw new InvalidOperationException("linked Fireball has no production cast duration");
                        Assert.True(strictCastMs > 0);
                        strictLifeBefore = paidConn.PlayerLife;
                        strictManaBefore = paidConn.PlayerMana;
                        strictAcceptedAt = Game.ProjectileWorldTime(paidConn);
                        strictReleaseAt = ReleaseAt(strictAcceptedAt, strictCastMs);
                        strictAccepted = await Game.CastGemSkill(
                            "fireball-linked-sacrifice", paidWire.Stream, paidWire.Ciphers, paidConn,
                            source.Id, from, aim, targetId: strictTargetId, swingAt: strictAcceptedAt);
                        strictLifeAfter = paidConn.PlayerLife;
                        strictManaAfter = paidConn.PlayerMana;
                        expectedSacrificed = (int)Math.Min(
                            (long)strictLifeBefore - 1,
                            (long)strictLifeBefore * sacrificePercent / 100);
                        expectedRawPrepaid = checked((int)(
                            (long)expectedSacrificed * gainPercent / 100));
                        expectedRawChaos = expectedRawPrepaid * effectiveness;
                        var strictInputs = Assert.IsType<Game.SupportedHitInputs>(
                            Game.SupportedHitInputsFor(paidConn, source.Id, area: false,
                                capturedGem: admitted));
                        var strictAreaInputs = Assert.IsType<Game.SupportedHitInputs>(
                            Game.SupportedHitInputsFor(paidConn, source.Id, area: true,
                                capturedGem: admitted));
                        var augmented = strictInputs.Raw.Add(DamageType.Chaos,
                            new(expectedRawChaos, expectedRawChaos));
                        var augmentedArea = strictAreaInputs.Raw.Add(DamageType.Chaos,
                            new(expectedRawChaos, expectedRawChaos));
                        strictInputs = strictInputs with { Raw = augmented };
                        strictAreaInputs = strictAreaInputs with { Raw = augmentedArea };
                        expectedStrictHit = Game.SupportedHitFrom(strictInputs, suppress: false);
                        expectedStrictAreaHit = Game.SupportedHitFrom(strictAreaInputs, suppress: false);
                        expectedStrictBlindHit = Game.SupportedHitFrom(strictInputs, suppress: false,
                            additionalIncreased: rolledBiteDamage);
                        expectedStrictBlindAreaHit = Game.SupportedHitFrom(strictAreaInputs, suppress: false,
                            additionalIncreased: rolledBiteDamage);
                        expectedStrictPoison = Game.SupportedPoison(paidConn, source.Id, spell: true,
                            player, capturedInputs: strictInputs);
                        expectedStrictBlindPoison = Game.SupportedPoison(paidConn, source.Id, spell: true,
                            player, additionalIncreased: rolledBiteDamage, capturedInputs: strictInputs);
                        Assert.Equal(92, expectedRawPrepaid);
                        Assert.Equal(3d, effectiveness, 9);
                        Assert.Equal(276d, expectedRawChaos, 9);
                    }

                    Assert.True(strictAccepted,
                        "the real linked Fireball was rejected before the paid-producer assertion");
                    Assert.Equal(strictManaCost, strictManaBefore - strictManaAfter);
                    Assert.Equal(strictLifeBefore - (uint)expectedSacrificed, strictLifeAfter);
                    paidSession.AdvanceTo(Math.Max(
                        paidSession.Runtime.Now, ProjectileRuntime.Time(strictReleaseAt)));
                    var strictProjectile = Assert.Single(paidSession.Runtime.Projectiles);
                    var strictShot = Assert.IsType<ProjectileOffense>(strictProjectile.Use.Payload);
                    actualChaosNormal = strictShot.Hit[DamageType.Chaos].Min;
                    actualChaosBlind = Assert.IsType<ProjectileOffenseAlternate>(
                        strictShot.VsBlinded).Hit[DamageType.Chaos].Min;
                    actualChaosArea = strictShot.AreaHit?[DamageType.Chaos].Min ?? 0;
                    actualPoisonChance = strictShot.Poison.Chance;
                    Assert.Equal(100, actualPoisonChance);
                    const double tickRounding = 1d / 30;
                    Assert.InRange(actualChaosNormal,
                        expectedStrictHit[DamageType.Chaos].Min - tickRounding,
                        expectedStrictHit[DamageType.Chaos].Min + tickRounding);
                    Assert.InRange(actualChaosArea,
                        expectedStrictAreaHit[DamageType.Chaos].Min - tickRounding,
                        expectedStrictAreaHit[DamageType.Chaos].Min + tickRounding);
                    Assert.InRange(actualChaosBlind,
                        expectedStrictBlindHit[DamageType.Chaos].Min - tickRounding,
                        expectedStrictBlindHit[DamageType.Chaos].Min + tickRounding);
                    Assert.Equal(expectedStrictHit.MinTotal, strictShot.Hit.MinTotal, 6);
                    Assert.Equal(expectedStrictHit.MaxTotal, strictShot.Hit.MaxTotal, 6);
                    if (strictShot.AreaHit is { } strictArea)
                    {
                        Assert.Equal(expectedStrictAreaHit.MinTotal, strictArea.MinTotal, 6);
                        Assert.Equal(expectedStrictAreaHit.MaxTotal, strictArea.MaxTotal, 6);
                    }
                    if (strictShot.VsBlinded is { } strictBlind)
                    {
                        Assert.Equal(expectedStrictBlindHit.MinTotal, strictBlind.Hit.MinTotal, 6);
                        Assert.Equal(expectedStrictBlindHit.MaxTotal, strictBlind.Hit.MaxTotal, 6);
                        Assert.InRange(strictBlind.Poison.Min,
                            expectedStrictBlindPoison.Min - tickRounding,
                            expectedStrictBlindPoison.Min + tickRounding);
                        Assert.InRange(strictBlind.Poison.Max,
                            expectedStrictBlindPoison.Max - tickRounding,
                            expectedStrictBlindPoison.Max + tickRounding);
                        if (strictBlind.AreaHit is { } strictBlindArea)
                        {
                            Assert.Equal(expectedStrictBlindAreaHit.MinTotal,
                                strictBlindArea.MinTotal, 6);
                            Assert.Equal(expectedStrictBlindAreaHit.MaxTotal,
                                strictBlindArea.MaxTotal, 6);
                        }
                    }
                    Assert.InRange(strictShot.Poison.Min,
                        expectedStrictPoison.Min - tickRounding,
                        expectedStrictPoison.Min + tickRounding);
                    Assert.InRange(strictShot.Poison.Max,
                        expectedStrictPoison.Max - tickRounding,
                        expectedStrictPoison.Max + tickRounding);

                    uint strictContactBefore = strictTarget.Life;
                    for (int step = 0; step < 120 && strictTarget.Life == strictContactBefore; step++)
                        paidSession.AdvanceTo(paidSession.Runtime.Now + ProjectileRuntime.Time(1d / 60));
                    bool strictContacted = strictTarget.Life < strictContactBefore;
                    int actualTargetDamage = checked((int)(strictContactBefore - strictTarget.Life));
                    int actualPoisonStacks;
                    double actualPoisonDps;
                    lock (paidConn.Monsters)
                    {
                        actualPoisonStacks = strictTarget.Poisons.Stacks(Game.ProjectileWorldTime(paidConn));
                        actualPoisonDps = strictTarget.Poisons.TotalDps(Game.ProjectileWorldTime(paidConn));
                    }
                    Assert.True(strictContacted);
                    Assert.Equal(1, actualPoisonStacks);
                    Assert.InRange(actualPoisonDps,
                        expectedStrictBlindPoison.Min - tickRounding,
                        expectedStrictBlindPoison.Min + tickRounding);
                    double expectedNormalChaos = expectedRawChaos * 2;
                    double expectedBlindChaos = expectedRawChaos * 2.4;
                    double expectedNormalPoison = .3 * expectedRawChaos * 2;
                    Console.WriteLine("[gateway] #504 paid projectile producer: "
                        + $"controlAccepted={controlAccepted}, controlMana={controlManaBefore}->{controlManaAfter} "
                        + $"(cost {controlManaCost}), controlLife={controlLifeBefore}->{controlLifeAfter}, "
                        + $"controlContacted={controlContacted}, controlPoisoned={controlPoisoned}; "
                        + $"Sacrifice support level 1 {sacrificePercent}%/{gainPercent}%, "
                        + $"castAccepted={strictAccepted}, mana={strictManaBefore}->{strictManaAfter} "
                        + $"(cost {strictManaCost}), life={strictLifeBefore}->{strictLifeAfter}; "
                        + $"expected sacrificed={expectedSacrificed}, rawPrepaid={expectedRawPrepaid}, "
                        + $"effectiveness={effectiveness:0.###}, expectedRawChaos={expectedRawChaos:0.###}, "
                        + $"normalChaos={expectedNormalChaos:0.###}, blindChaos={expectedBlindChaos:0.###}, "
                        + $"normalPoison={expectedNormalPoison:0.###}; actual projectile chaos "
                        + $"{actualChaosNormal:0.###}/{actualChaosBlind:0.###}/{actualChaosArea:0.###}, "
                        + $"poisonChance={actualPoisonChance}, contacted={strictContacted}, "
                        + $"targetDamage={actualTargetDamage}, poisonStacks={actualPoisonStacks}, "
                        + $"poisonDps={actualPoisonDps:0.###}.");

                    Assert.True(expectedSacrificed == checked((int)(strictLifeBefore - strictLifeAfter)),
                        $"the actual accepted linked Fireball paid {strictManaCost} mana and "
                        + $"{strictLifeBefore - strictLifeAfter} Life; "
                        + $"Sacrifice level 1 requires {sacrificePercent}% current Life and "
                        + $"{gainPercent}% as added chaos, so {strictLifeBefore} Life expects "
                        + $"{expectedSacrificed} sacrificed and {expectedRawChaos:0.###} raw chaos after "
                        + $"{effectiveness:0.###} effectiveness. Actual projectile chaos was "
                        + $"{actualChaosNormal}/{actualChaosBlind}/{actualChaosArea}; target contact "
                        + $"was {strictContacted}, poison stacks {actualPoisonStacks}, expected normal poison "
                        + $"{expectedNormalPoison:0.###}.");

                    paidConn.ProjectileRuntime = null;
                    paidSession.Invalidate();
                    await paidSession.OutputCompleted;
                    paidSession = null;
                    lock (paidConn.Monsters)
                    {
                        paidConn.Monsters.Remove(controlTargetId);
                        paidConn.Monsters.Remove(strictTargetId);
                    }
                    var removedLinked = inventory.Remove(PlayerInventories.Weapon1, linkedPlacement.Id);
                    Assert.NotNull(removedLinked);

                    // Spell Echo is a real second release of the same use, and Volley is a real multi-projectile
                    // release. Both consume the one frozen receipt; changing Life between callbacks cannot repay it.
                    var spellEchoGem = Gem("Spell Echo Support", 1);
                    var volleyGem = Gem("Volley Support", 1);
                    foreach (var oldBody in inventory.Items(PlayerInventories.BodyArmour))
                        inventory.Remove(PlayerInventories.BodyArmour, oldBody.Id);
                    var repeatBodyType = Program.Items.Find(
                        "Metadata/Items/Armours/BodyArmours/BodyStr1")
                        ?? throw new InvalidOperationException("missing four-socket body armour");
                    var repeatBody = ItemCatalog.Create(repeatBodyType) with
                    {
                        Sockets = [new(SocketColour.White, fireballGem),
                            new(SocketColour.White, sacrificeGem),
                            new(SocketColour.White, spellEchoGem),
                            new(SocketColour.White, volleyGem)],
                        Links = [4],
                    };
                    Assert.True(CurrencyEffects.SocketCapacity(repeatBody, repeatBodyType) >= 4);
                    var repeatPlacement = inventory.Add(PlayerInventories.BodyArmour, 0, 0, repeatBody);
                    Assert.False(Game.IsInactive(paidConn, repeatPlacement.Id));
                    Game.ReconcileOwnAuras(paidConn, validateSkills: true);
                    var repeatSource = Assert.Single(Game.GemSkillEffects(paidConn), gem =>
                        Game.SkillsFor(paidConn).EffectId(gem.Effect) == "Fireball");
                    Assert.Equal(1, Game.SpellRepeats(paidConn, repeatSource.Id, repeatSource.Effect));

                    bool repeatAccepted;
                    uint repeatLifeBefore, repeatLifeAfter, repeatManaBefore, repeatManaAfter;
                    uint repeatManaCost;
                    int repeatCastMs, repeatSacrificed;
                    double repeatAcceptedAt, repeatFirstReleaseAt, repeatSecondReleaseAt;
                    double repeatExpectedChaos;
                    using (new Game.PlayerStatScope(paidConn, fresh: true))
                    {
                        var admitted = Game.SupportedSkill(paidConn, repeatSource.Id);
                        Assert.Contains(admitted.Supports,
                            support => support.Effect.Id == "SupportSpellEcho");
                        Assert.Contains(admitted.Supports,
                            support => support.Effect.Id == "SupportVolley");
                        int Stat(string name) => Program.Stats.Key(name) is uint key
                            ? admitted.Stats.GetValueOrDefault(key) : 0;
                        int percent = Stat("support_sacrifice_sacrifice_%_of_current_life");
                        int gain = Stat(
                            "support_sacrifice_gain_%_of_sacrificed_life_as_added_chaos_damage");
                        repeatLifeBefore = paidConn.PlayerLife;
                        repeatManaBefore = paidConn.PlayerMana;
                        repeatSacrificed = (int)Math.Min((long)repeatLifeBefore - 1,
                            (long)repeatLifeBefore * percent / 100);
                        long rawGain = (long)repeatSacrificed * gain / 100;
                        double effectiveExtra = rawGain
                            * Game.AddedEffectiveness(paidConn, admitted.Effect, admitted.Level);
                        var repeatInputs = Assert.IsType<Game.SupportedHitInputs>(
                            Game.SupportedHitInputsFor(paidConn, repeatSource.Id, area: false,
                                capturedGem: admitted));
                        repeatInputs = repeatInputs with
                        {
                            Raw = repeatInputs.Raw.Add(DamageType.Chaos,
                                new(effectiveExtra, effectiveExtra)),
                        };
                        repeatExpectedChaos = Game.SupportedHitFrom(repeatInputs, suppress: false)
                            [DamageType.Chaos].Min;
                        repeatManaCost = CanonicalManaCost(repeatSource.Id);
                        repeatCastMs = Game.CastMs(paidConn, repeatSource.Effect, repeatSource.Id)
                            ?? throw new InvalidOperationException("repeating Fireball has no cast duration");
                        repeatAcceptedAt = Game.ProjectileWorldTime(paidConn);
                        repeatFirstReleaseAt = ReleaseAt(repeatAcceptedAt, repeatCastMs);
                        repeatSecondReleaseAt = repeatFirstReleaseAt + repeatCastMs / 1000d;
                        repeatAccepted = await Game.CastGemSkill(
                            "fireball-linked-sacrifice-repeat", paidWire.Stream, paidWire.Ciphers,
                            paidConn, repeatSource.Id, from, aim, swingAt: repeatAcceptedAt);
                        repeatLifeAfter = paidConn.PlayerLife;
                        repeatManaAfter = paidConn.PlayerMana;
                    }
                    Assert.True(repeatAccepted);
                    paidSession = Assert.IsType<Game.ProjectileSession>(paidConn.ProjectileRuntime);
                    Assert.Equal(repeatSacrificed, checked((int)(
                        repeatLifeBefore - repeatLifeAfter)));
                    Assert.Equal(repeatManaCost, repeatManaBefore - repeatManaAfter);

                    void AssertFrozenChaos(ProjectileEntity[] released, double expected)
                    {
                        Assert.NotEmpty(released);
                        foreach (var projectile in released)
                        {
                            var offense = Assert.IsType<ProjectileOffense>(projectile.Use.Payload);
                            Assert.InRange(offense.Hit[DamageType.Chaos].Min,
                                expected - tickRounding, expected + tickRounding);
                        }
                    }

                    paidSession.AdvanceTo(Math.Max(
                        paidSession.Runtime.Now, ProjectileRuntime.Time(repeatFirstReleaseAt)));
                    var firstRepeatRelease = paidSession.Runtime.Projectiles
                        .Where(projectile => projectile.Use.RepeatsLeft == 1).ToArray();
                    Assert.True(firstRepeatRelease.Length > 1,
                        "Spell Echo plus Volley did not release more than one real projectile");
                    AssertFrozenChaos(firstRepeatRelease, repeatExpectedChaos);

                    uint lifeBetweenReleases = checked(paidConn.PlayerLife + 13);
                    paidConn.PlayerLife = lifeBetweenReleases;
                    paidSession.AdvanceTo(Math.Max(
                        paidSession.Runtime.Now, ProjectileRuntime.Time(repeatSecondReleaseAt)));
                    var secondRepeatRelease = paidSession.Runtime.Projectiles
                        .Where(projectile => projectile.Use.RepeatsLeft == 0).ToArray();
                    Assert.True(secondRepeatRelease.Length > 1);
                    AssertFrozenChaos(secondRepeatRelease, repeatExpectedChaos);
                    Assert.Equal(lifeBetweenReleases, paidConn.PlayerLife);

                    paidConn.ProjectileRuntime = null;
                    paidSession.Invalidate();
                    await paidSession.OutputCompleted;
                    paidSession = null;

                    // A rejected normal mana admission cannot reach the Sacrifice producer or schedule a release.
                    uint repeatManaRestore = paidConn.PlayerMana;
                    paidConn.PlayerMana = 0;
                    uint noManaLife = paidConn.PlayerLife;
                    paidSession = new Game.ProjectileSession(
                        "fireball-sacrifice-no-mana", paidWire.Stream, paidWire.Ciphers, paidConn);
                    paidConn.ProjectileRuntime = paidSession;
                    double noManaAcceptedAt = Game.ProjectileWorldTime(paidConn);
                    int noManaCastMs = Game.CastMs(paidConn, repeatSource.Effect, repeatSource.Id)
                        ?? throw new InvalidOperationException("no-mana Fireball has no cast duration");
                    bool noManaAccepted = await Game.CastGemSkill(
                        "fireball-sacrifice-no-mana", paidWire.Stream, paidWire.Ciphers, paidConn,
                        repeatSource.Id, from, aim, swingAt: noManaAcceptedAt);
                    Assert.False(noManaAccepted);
                    Assert.Equal(noManaLife, paidConn.PlayerLife);
                    Assert.Equal(0u, paidConn.PlayerMana);
                    paidSession.AdvanceTo(Math.Max(paidSession.Runtime.Now,
                        ProjectileRuntime.Time(noManaAcceptedAt + noManaCastMs / 1000d)));
                    Assert.Empty(paidSession.Runtime.Projectiles);
                    paidConn.ProjectileRuntime = null;
                    paidSession.Invalidate();
                    await paidSession.OutputCompleted;
                    paidSession = null;
                    paidConn.PlayerMana = repeatManaRestore;

                    // A stale source identity is refused before any sacrifice mutation.
                    var removedRepeat = inventory.Remove(PlayerInventories.BodyArmour, repeatPlacement.Id);
                    Assert.NotNull(removedRepeat);
                    Game.ReconcileOwnAuras(paidConn, validateSkills: true);
                    uint noSourceLife = paidConn.PlayerLife;
                    bool removedSourceAccepted = await Game.CastGemSkill(
                        "fireball-sacrifice-source-removed", paidWire.Stream, paidWire.Ciphers,
                        paidConn, repeatSource.Id, from, aim);
                    Assert.False(removedSourceAccepted);
                    Assert.Equal(noSourceLife, paidConn.PlayerLife);

                    // Empower raises the admitted effective level while the source tuple remains the raw level-1
                    // gem. The paid producer must use that admitted gem and still pay exactly once.
                    var empowerGem = Gem("Empower Support", 2);
                    var empoweredSword = controlSword with
                    {
                        Sockets = [new(SocketColour.White, fireballGem),
                            new(SocketColour.White, sacrificeGem),
                            new(SocketColour.White, empowerGem)],
                        Links = [3],
                    };
                    var empoweredPlacement = inventory.Add(PlayerInventories.Weapon1, 0, 0, empoweredSword);
                    Assert.False(Game.IsInactive(paidConn, empoweredPlacement.Id));
                    Game.ReconcileOwnAuras(paidConn, validateSkills: true);
                    var empoweredSource = Assert.Single(Game.GemSkillEffects(paidConn), gem =>
                        Game.SkillsFor(paidConn).EffectId(gem.Effect) == "Fireball");
                    paidSession = new Game.ProjectileSession(
                        "sacrifice-empower-2", paidWire.Stream, paidWire.Ciphers, paidConn);
                    paidConn.ProjectileRuntime = paidSession;
                    uint empoweredLifeBefore, empoweredLifeAfter;
                    uint empoweredManaBefore, empoweredManaAfter, empoweredManaCost;
                    int empoweredSacrificed;
                    bool empoweredAccepted;
                    using (new Game.PlayerStatScope(paidConn, fresh: true))
                    {
                        var admitted = Game.SupportedSkill(paidConn, empoweredSource.Id);
                        Assert.Contains(admitted.Supports,
                            support => support.Effect.Id == "SupportEmpower");
                        Assert.True(admitted.Level > empoweredSource.Level);
                        int percent = Program.Stats.Key(
                            "support_sacrifice_sacrifice_%_of_current_life") is uint percentKey
                            ? admitted.Stats.GetValueOrDefault(percentKey) : 0;
                        empoweredLifeBefore = paidConn.PlayerLife;
                        empoweredManaBefore = paidConn.PlayerMana;
                        empoweredSacrificed = (int)Math.Min((long)empoweredLifeBefore - 1,
                            (long)empoweredLifeBefore * percent / 100);
                        empoweredManaCost = CanonicalManaCost(empoweredSource.Id);
                        empoweredAccepted = await Game.CastGemSkill(
                            "sacrifice-empower-2", paidWire.Stream, paidWire.Ciphers, paidConn,
                            empoweredSource.Id, from, aim,
                            swingAt: Game.ProjectileWorldTime(paidConn));
                        empoweredLifeAfter = paidConn.PlayerLife;
                        empoweredManaAfter = paidConn.PlayerMana;
                    }
                    Assert.True(empoweredAccepted);
                    Assert.True(empoweredSacrificed > 0);
                    Assert.Equal(empoweredLifeBefore - (uint)empoweredSacrificed,
                        empoweredLifeAfter);
                    Assert.Equal(empoweredManaCost, empoweredManaBefore - empoweredManaAfter);
                    paidConn.ProjectileRuntime = null;
                    paidSession.Invalidate();
                    await paidSession.OutputCompleted;
                    paidSession = null;
                    var removedEmpowered = inventory.Remove(PlayerInventories.Weapon1,
                        empoweredPlacement.Id);
                    Assert.NotNull(removedEmpowered);
                    Game.ReconcileOwnAuras(paidConn, validateSkills: true);

                    // Brutality's real prohibition sees the newly added raw chaos packet and removes both its
                    // chaos damage and poison. The accepted use still pays Sacrifice once.
                    var etherealKnivesGem = Gem("Ethereal Knives", 1);
                    var brutalityGem = Gem("Brutality Support", 1);
                    var forbiddenSword = controlSword with
                    {
                        Sockets = [new(SocketColour.White, etherealKnivesGem),
                            new(SocketColour.White, sacrificeGem),
                            new(SocketColour.White, brutalityGem)],
                        Links = [3],
                    };
                    var forbiddenPlacement = inventory.Add(PlayerInventories.Weapon1, 0, 0, forbiddenSword);
                    Assert.False(Game.IsInactive(paidConn, forbiddenPlacement.Id));
                    Game.ReconcileOwnAuras(paidConn, validateSkills: true);
                    var forbiddenSource = Assert.Single(Game.GemSkillEffects(paidConn), gem =>
                        Game.SkillsFor(paidConn).EffectId(gem.Effect) == "EtherealKnives");
                    const uint forbiddenTargetId = 0xC473D3u;
                    var forbiddenTarget = new SpawnedMonster("sacrifice-brutality",
                        new(aim.X, aim.Y, 0), 0, 0, 1_000_000)
                    {
                        Level = 100, Hostile = true, Life = 1_000_000,
                    };
                    lock (paidConn.Monsters) paidConn.Monsters[forbiddenTargetId] = forbiddenTarget;
                    paidSession = new Game.ProjectileSession(
                        "sacrifice-brutality", paidWire.Stream, paidWire.Ciphers, paidConn);
                    paidConn.ProjectileRuntime = paidSession;
                    uint forbiddenLifeBefore, forbiddenLifeAfter;
                    bool forbiddenAccepted;
                    double forbiddenAcceptedAt, forbiddenReleaseAt;
                    using (new Game.PlayerStatScope(paidConn, fresh: true))
                    {
                        var admitted = Game.SupportedSkill(paidConn, forbiddenSource.Id);
                        Assert.Contains(admitted.Supports,
                            support => support.Effect.Id == "SupportBrutality");
                        Assert.Contains(admitted.Supports,
                            support => support.Effect.Id == "SupportSacrifice");
                        int percent = Program.Stats.Key("support_sacrifice_sacrifice_%_of_current_life")
                            is uint percentKey ? admitted.Stats.GetValueOrDefault(percentKey) : 0;
                        forbiddenLifeBefore = paidConn.PlayerLife;
                        forbiddenAcceptedAt = Game.ProjectileWorldTime(paidConn);
                        int castMs = Game.CastMs(paidConn, forbiddenSource.Effect, forbiddenSource.Id)
                            ?? throw new InvalidOperationException("Ethereal Knives has no cast duration");
                        forbiddenReleaseAt = ReleaseAt(forbiddenAcceptedAt, castMs);
                        forbiddenAccepted = await Game.CastGemSkill(
                            "sacrifice-brutality", paidWire.Stream, paidWire.Ciphers, paidConn,
                            forbiddenSource.Id, from, aim, targetId: forbiddenTargetId,
                            swingAt: forbiddenAcceptedAt);
                        forbiddenLifeAfter = paidConn.PlayerLife;
                        Assert.True(percent > 0);
                        Assert.True(forbiddenLifeAfter < forbiddenLifeBefore);
                    }
                    Assert.True(forbiddenAccepted);
                    paidSession.AdvanceTo(Math.Max(
                        paidSession.Runtime.Now, ProjectileRuntime.Time(forbiddenReleaseAt)));
                    var forbiddenProjectiles = paidSession.Runtime.Projectiles.ToArray();
                    Assert.NotEmpty(forbiddenProjectiles);
                    foreach (var forbiddenProjectile in forbiddenProjectiles)
                    {
                        var forbiddenShot = Assert.IsType<ProjectileOffense>(
                            forbiddenProjectile.Use.Payload);
                        AssertNoChaos("Brutality direct", forbiddenShot.Hit);
                        if (forbiddenShot.AreaHit is { } forbiddenArea)
                            AssertNoChaos("Brutality area", forbiddenArea);
                    }
                    uint forbiddenContactBefore = forbiddenTarget.Life;
                    for (int step = 0; step < 120
                        && forbiddenTarget.Life == forbiddenContactBefore; step++)
                        paidSession.AdvanceTo(paidSession.Runtime.Now
                            + ProjectileRuntime.Time(1d / 60));
                    Assert.True(forbiddenTarget.Life < forbiddenContactBefore);
                    lock (paidConn.Monsters)
                        Assert.False(forbiddenTarget.Poisons.Poisoned);
                    Assert.Equal(100, Game.SupportedStats(paidConn, forbiddenSource.Id)
                        .GetValueOrDefault(Program.Stats.Key("base_chance_to_poison_on_hit_%")!.Value));

                    paidConn.ProjectileRuntime = null;
                    paidSession.Invalidate();
                    await paidSession.OutputCompleted;
                    paidSession = null;
                    lock (paidConn.Monsters) paidConn.Monsters.Remove(forbiddenTargetId);
                    var removedForbidden = inventory.Remove(PlayerInventories.Weapon1,
                        forbiddenPlacement.Id);
                    Assert.NotNull(removedForbidden);

                    // Life 1 and 20% of 5 both preserve the health floor; the latter also floors 35% of the
                    // one sacrificed life to zero, so the release gets no added chaos.
                    var lowSword = controlSword with
                    {
                        Sockets = [new(SocketColour.White, fireballGem),
                            new(SocketColour.White, sacrificeGem)],
                        Links = [2],
                    };
                    var lowPlacement = inventory.Add(PlayerInventories.Weapon1, 0, 0, lowSword);
                    Game.ReconcileOwnAuras(paidConn, validateSkills: true);
                    var lowSource = Assert.Single(Game.GemSkillEffects(paidConn), gem =>
                        Game.SkillsFor(paidConn).EffectId(gem.Effect) == "Fireball");
                    uint manaBeforeLow = paidConn.PlayerMana;

                    paidConn.PlayerLife = 1;
                    paidSession = new Game.ProjectileSession(
                        "sacrifice-life-one", paidWire.Stream, paidWire.Ciphers, paidConn);
                    paidConn.ProjectileRuntime = paidSession;
                    double lifeOneAt = Game.ProjectileWorldTime(paidConn);
                    int lifeOneCastMs = Game.CastMs(paidConn, lowSource.Effect, lowSource.Id)
                        ?? throw new InvalidOperationException("life-one Fireball has no cast duration");
                    Assert.True(await Game.CastGemSkill(
                        "sacrifice-life-one", paidWire.Stream, paidWire.Ciphers, paidConn,
                        lowSource.Id, from, aim, swingAt: lifeOneAt));
                    Assert.Equal(1u, paidConn.PlayerLife);
                    paidSession.AdvanceTo(Math.Max(paidSession.Runtime.Now,
                        ProjectileRuntime.Time(ReleaseAt(lifeOneAt, lifeOneCastMs))));
                    var lifeOneShot = Assert.IsType<ProjectileOffense>(
                        Assert.Single(paidSession.Runtime.Projectiles).Use.Payload);
                    AssertNoChaos("life-one paid release", lifeOneShot.Hit);

                    paidConn.ProjectileRuntime = null;
                    paidSession.Invalidate();
                    await paidSession.OutputCompleted;
                    paidSession = null;
                    paidConn.PlayerMana = manaBeforeLow;
                    paidConn.PlayerLife = 5;
                    paidSession = new Game.ProjectileSession(
                        "sacrifice-life-rounding", paidWire.Stream, paidWire.Ciphers, paidConn);
                    paidConn.ProjectileRuntime = paidSession;
                    double lowLifeAt = Game.ProjectileWorldTime(paidConn);
                    int lowLifeCastMs = Game.CastMs(paidConn, lowSource.Effect, lowSource.Id)
                        ?? throw new InvalidOperationException("low-life Fireball has no cast duration");
                    Assert.True(await Game.CastGemSkill(
                        "sacrifice-life-rounding", paidWire.Stream, paidWire.Ciphers, paidConn,
                        lowSource.Id, from, aim, swingAt: lowLifeAt));
                    Assert.Equal(4u, paidConn.PlayerLife);
                    paidSession.AdvanceTo(Math.Max(paidSession.Runtime.Now,
                        ProjectileRuntime.Time(ReleaseAt(lowLifeAt, lowLifeCastMs))));
                    var roundingShot = Assert.IsType<ProjectileOffense>(
                        Assert.Single(paidSession.Runtime.Projectiles).Use.Payload);
                    AssertNoChaos("rounded-zero paid release", roundingShot.Hit);

                    paidConn.ProjectileRuntime = null;
                    paidSession.Invalidate();
                    await paidSession.OutputCompleted;
                    paidSession = null;
                    var removedLow = inventory.Remove(PlayerInventories.Weapon1, lowPlacement.Id);
                    Assert.NotNull(removedLow);
                    Game.ReconcileOwnAuras(paidConn, validateSkills: true);
                    paidConn.PlayerLife = 100;
                    paidConn.PlayerMana = paidConn.Pools.Mana;
                    uint finalNoSourceLife = paidConn.PlayerLife;
                    Assert.False(await Game.CastGemSkill(
                        "sacrifice-final-source-removed", paidWire.Stream, paidWire.Ciphers,
                        paidConn, lowSource.Id, from, aim));
                    Assert.Equal(finalNoSourceLife, paidConn.PlayerLife);
                }
                finally
                {
                    if (paidConnection is not null) paidConnection.ProjectileRuntimeState = previousRuntime;
                    if (paidSession is not null)
                    {
                        paidSession.Invalidate();
                        await paidSession.OutputCompleted;
                    }
                    if (paidConnection is not null) paidConnection.Closed = true;
                    Game.AreaUpdateContext.Value = previousContext;
                    PlayerInventories.Forget(character);
                }
            }

            async Task TempestShieldBiteConsumerFixture()
            {
                // A controlled pending proc enters the real reservation consumer. It deliberately does not call the
                // blocked-hit producer: Tempest Shield aura ownership and target selection are real, while admission
                // from an actual enemy block remains a separate proof boundary.
                string character = "TempestShieldBiteConsumer" + Guid.NewGuid().ToString("N");
                const byte witchRow = 1;
                PlayerInventories.SetClass(character, witchRow);
                CharacterSaves.Write(character, new(new Dictionary<byte, List<CharacterSaves.SavedPlacement>>(),
                    Experience: CharacterStats.ExperienceFor(100)));
                var schedule = new CatClockSchedule { At = 300 };
                var updates = new PlayerUpdatesState(lifetime, schedule);
                var previousContext = Game.AreaUpdateContext.Value;
                Game.AreaUpdateContext.Value = updates;
                using var tempestWire = new Wire();
                ClientState? tempestConnection = null;
                IProjectileRuntime? previousRuntime = null;
                FixedProjectileRuntime? fixedRuntime = null;
                try
                {
                    tempestConnection = new ClientState(conn.Layout, 0xC474, 1)
                    {
                        Character = character, CharacterId = Guid.NewGuid(), Account = "tempest-bite-consumer-account",
                        League = "Standard", LeagueForm = "Standard", ClassRow = witchRow, Instance = new(),
                        PlayerSent = true, PlayerObjectCreated = true, PlayerUpdatesState = updates,
                        Experience = CharacterStats.ExperienceFor(100), LeagueModule = conn.LeagueModule,
                        Wire = ("tempest-bite-consumer", tempestWire.Stream, tempestWire.Ciphers),
                        Pools = new(100, 1, 1, 0, 0),
                    };
                    var tempestConn = tempestConnection;
                    Assert.Empty(tempestConn.PlayerStats);

                    var inventory = Game.ItemsOf(tempestConn);
                    foreach (byte row in new byte[] { PlayerInventories.Weapon1, PlayerInventories.Offhand1,
                                 PlayerInventories.Helmet, 5, 6, 7 })
                        foreach (var old in inventory.Items(row)) inventory.Remove(row, old.Id);

                    Item Gem(string name, int level)
                    {
                        var type = Program.Items.FindGem(name)
                            ?? throw new InvalidOperationException("missing creatable gem " + name);
                        return ItemCatalog.Create(type.Type) with
                        {
                            GemVariant = type.Variant.Index,
                            GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(level),
                        };
                    }

                    var tempestGem = Gem("Tempest Shield", 1);
                    var heraldGem = Gem("Herald of Ice", 1);
                    var thunderGem = Gem("Herald of Thunder", 1);
                    var sword = ItemCatalog.Create(Program.Items.Find(
                        "Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1")!) with
                    {
                        Sockets = [new(SocketColour.White, tempestGem), new(SocketColour.White, heraldGem),
                            new(SocketColour.White, thunderGem)],
                    };
                    var shield = ItemCatalog.Create(Program.Items.Find(
                        "Metadata/Items/Armours/Shields/ShieldStr1")!);
                    var swordPlacement = inventory.Add(PlayerInventories.Weapon1, 0, 0, sword);
                    var shieldPlacement = inventory.Add(PlayerInventories.Offhand1, 0, 0, shield);
                    var astramentisPlacement = inventory.Add(5, 0, 0,
                        new UniqueCatalog(Program.Items).Create("Astramentis", new Random(504))!);
                    var firstRingPlacement = inventory.Add(6, 0, 0,
                        new UniqueCatalog(Program.Items).Create("Le Heup of All", new Random(504))!);
                    var secondRingPlacement = inventory.Add(7, 0, 0,
                        new UniqueCatalog(Program.Items).Create("Le Heup of All", new Random(505))!);
                    var bite = new UniqueCatalog(Program.Items).Create("Farrul's Bite", new Random(504))!;
                    var bitePlacement = inventory.Add(PlayerInventories.Helmet, 0, 0, bite);
                    tempestConn.Pools = Game.PoolsFor(tempestConn, 100);
                    tempestConn.PlayerLife = tempestConn.Pools.Life;
                    tempestConn.PlayerMana = tempestConn.Pools.Mana;
                    Game.ReconcileOwnAuras(tempestConn, validateSkills: true);
                    Assert.Empty(tempestConn.PlayerStats);
                    Assert.Equal(100, tempestConn.Pools.Level);
                    Assert.False(Game.IsInactive(tempestConn, swordPlacement.Id));
                    Assert.False(Game.IsInactive(tempestConn, shieldPlacement.Id));
                    Assert.False(Game.IsInactive(tempestConn, astramentisPlacement.Id));
                    Assert.False(Game.IsInactive(tempestConn, firstRingPlacement.Id));
                    Assert.False(Game.IsInactive(tempestConn, secondRingPlacement.Id));
                    Assert.False(Game.IsInactive(tempestConn, bitePlacement.Id));

                    var biteDamageMod = Assert.Single(bite.Mods, mod =>
                        Program.Items.Mods.ByHash(mod.Hash)?.Id == "DamageAgainstBlindedEnemiesUnique__1");
                    string biteDamageStat =
                        Assert.Single(Program.Items.Mods.ByHash(biteDamageMod.Hash)!.Stats).Id;
                    int rolledBiteDamage = Assert.Single(biteDamageMod.Values);
                    Assert.Equal(40, rolledBiteDamage);

                    void SetStat(string name, int value)
                    {
                        uint key = Program.Stats.Key(name)
                            ?? throw new InvalidOperationException("missing required stat " + name);
                        tempestConn.PlayerStats[key] = value;
                    }
                    void ZeroIfPresent(string name)
                    {
                        if (Program.Stats.Key(name) is uint key) tempestConn.PlayerStats[key] = 0;
                    }
                    foreach (string name in new[]
                    {
                        "spell_damage_+%", "lightning_damage_+%", "chaos_damage_+%", "elemental_damage_+%",
                        "critical_strike_chance_+%", "spell_critical_strike_chance_+%",
                        "base_extra_damage_rolls", "base_chance_to_deal_triple_damage_%",
                        "chance_to_deal_double_damage_%", "spell_chance_to_deal_double_damage_%",
                        "global_chance_to_blind_on_hit_%", "base_chance_to_poison_on_hit_%",
                        "chance_to_freeze_shock_ignite_%", "chance_to_ignite_%",
                    })
                        ZeroIfPresent(name);
                    foreach (string type in new[] { "physical", "cold", "fire", "lightning", "chaos" })
                    {
                        SetStat($"spell_minimum_added_{type}_damage", 0);
                        SetStat($"spell_maximum_added_{type}_damage", 0);
                        SetStat($"attack_minimum_added_{type}_damage", 0);
                        SetStat($"attack_maximum_added_{type}_damage", 0);
                    }
                    foreach (string targetType in new[] { "fire", "cold", "lightning", "chaos" })
                        foreach (string sourceType in new[] { "physical", "lightning", "cold", "fire" })
                            foreach (string prefix in new[] { "", "base_", "skill_", "spell_", "attack_" })
                            {
                                ZeroIfPresent($"{prefix}{sourceType}_damage_%_to_convert_to_{targetType}");
                                ZeroIfPresent($"{prefix}{sourceType}_damage_%_to_add_as_{targetType}");
                            }
                    SetStat("damage_+%", 100);
                    SetStat("global_cannot_crit", 1);
                    SetStat("spell_minimum_added_chaos_damage", 10_000);
                    SetStat("spell_maximum_added_chaos_damage", 10_000);

                    var source = Assert.Single(Game.GemSkillEffects(tempestConn), gem =>
                        Game.SkillsFor(tempestConn).EffectId(gem.Effect) == "TempestShield");
                    Assert.Equal(1, source.Level);
                    Assert.True(Program.Skills.HasType(source.Effect, "HasReservation"));
                    Assert.True(Game.SkillsFor(tempestConn).IsSpell(source.Effect));
                    Assert.False(Game.SkillsFor(tempestConn).IsAttack(source.Effect));
                    var supported = Game.SupportedSkill(tempestConn, source.Id);
                    Assert.True(supported.RequirementsMet);
                    Assert.Equal(source.Effect, supported.Effect);
                    Assert.Equal(1, supported.Level);
                    var heraldSource = Assert.Single(Game.GemSkillEffects(tempestConn), gem =>
                        Game.SkillsFor(tempestConn).EffectId(gem.Effect) == "HeraldOfIce");
                    var heraldSupported = Game.SupportedSkill(tempestConn, heraldSource.Id);
                    Assert.True(heraldSupported.RequirementsMet);
                    Assert.True(AuraCatalog.Supported("TempestShield"));
                    var reservation = Game.SupportedReservationCost(tempestConn, source.Id);
                    Assert.NotNull(reservation);
                    Assert.True(reservation.Value.ManaFlat + reservation.Value.ManaPercent > 0);
                    Assert.True(await Game.ToggleAura("test", tempestWire.Stream, tempestWire.Ciphers,
                        tempestConn, source.Id, source.Effect, source.Name, reservation.Value,
                        supported.Level, supported.Quality));
                    Assert.True(tempestConn.Auras.TryGetValue(source.Id, out var activeAura));
                    Assert.Equal("TempestShield", activeAura!.EffectId);
                    Assert.Equal(112, activeAura.BuffRow);
                    int reservedMana = Game.ReservedMana(tempestConn);
                    Assert.True(reservedMana > 0 && reservedMana <= tempestConn.Pools.Mana,
                        $"Tempest Shield reservation must fit: {reservedMana}/{tempestConn.Pools.Mana}");

                    previousRuntime = tempestConn.ProjectileRuntimeState;
                    fixedRuntime = new FixedProjectileRuntime(ProjectileRuntime.Time(schedule.At));
                    tempestConn.ProjectileRuntimeState = fixedRuntime;
                    Assert.Same(fixedRuntime, tempestConn.ProjectileRuntimeState);
                    Assert.Null(tempestConn.ProjectileRuntime);
                    Assert.True(fixedRuntime.Active);
                    Assert.False(tempestConn.Paused);
                    Assert.False(tempestConn.Closed);
                    Assert.False(tempestConn.LeavingArea);
                    Assert.False(Game.InTown(tempestConn));
                    Assert.True(Game.CurrentGameRules);
                    double now = Game.ResourceEffectsTime(tempestConn);
                    Assert.Equal(schedule.At, now, 9);
                    var owner = Game.ResourceEffectsOf(tempestConn);
                    Assert.True(Game.ResourceEffectsCurrent(tempestConn, owner));

                    var targetAt = tempestConn.Walk.Position(now);
                    Assert.NotNull(Program.Stats.Key("never_shock"));
                    Assert.True(MonsterElementalAilments.CannotInflict("shock", name => name == "never_shock" ? 1 : 0));
                    MonsterModSet ShockImmune() => MonsterModSet.None with
                    {
                        Effects = new(new Dictionary<string, int> { ["never_shock"] = 1 }),
                    };
                    SpawnedMonster Target(uint id, string label, bool shockImmune = true, bool hostile = true)
                    {
                        var target = new SpawnedMonster(label, new(targetAt.X, targetAt.Y, 0), 0, 0, 1_000_000)
                        {
                            Level = 100, Hostile = hostile, Life = 1_000_000,
                            Mods = shockImmune ? ShockImmune() : MonsterModSet.None,
                        };
                        lock (tempestConn.Monsters) tempestConn.Monsters[id] = target;
                        return target;
                    }
                    void AssertNeutralTarget(SpawnedMonster target)
                    {
                        Assert.Null(target.Variety);
                        Assert.Equal(1, target.Mods.Effects["never_shock"]);
                        Assert.Equal(0, target.Ailments.Magnitude(MonsterElementalAilments.Kind.Shock, now));
                        Assert.Equal(0, target.Effects.Resistance(DamageType.Lightning));
                        Assert.Equal(0, target.Effects.Resistance(DamageType.Chaos));
                        Assert.Equal(1, target.Effects.DamageTaken(spell: true), 9);
                        Assert.Equal(targetAt, target.Walk.Position(now));
                        Assert.True(target.Hostile && !target.Dead && target.Life > 0);
                    }
                    async Task<int> RunProc(uint id, SpawnedMonster target, string label, double at)
                    {
                        uint before = target.Life;
                        lock (tempestConn.Monsters)
                        {
                            Assert.Same(target, tempestConn.Monsters[id]);
                            Assert.Empty(tempestConn.Reservations.Procs);
                            tempestConn.Reservations.Procs.Enqueue(
                                new ReservationState.Proc(source.Id, target.Walk.Position(at), id));
                        }
                        await Game.ReservationSkillsTick(label, tempestWire.Stream, tempestWire.Ciphers,
                            tempestConn, at);
                        Assert.Empty(tempestConn.Reservations.Procs);
                        return checked((int)(before - target.Life));
                    }
                    Game.ReservationDamageProfile Profile(Game.SupportedHitInputs inputs, long increased) => new(
                        Game.SupportedHitFrom(inputs, suppress: true, additionalIncreased: increased),
                        Game.SupportedIgnite(tempestConn, source.Id, true, Game.CharacterPlayerStats(tempestConn),
                            additionalIncreased: increased, capturedInputs: inputs),
                        Game.SupportedBleed(tempestConn, source.Id, true, Game.CharacterPlayerStats(tempestConn),
                            additionalIncreased: increased, capturedInputs: inputs),
                        Game.SupportedPoison(tempestConn, source.Id, true, Game.CharacterPlayerStats(tempestConn),
                            additionalIncreased: increased, capturedInputs: inputs));
                    (int Min, int Max) FloorComponents(DamagePacket hit) => (
                        hit.Components.Sum(component => (int)Math.Floor(component.Range.Min)),
                        hit.Components.Sum(component => (int)Math.Floor(component.Range.Max)));

                    (int Min, int Max) normalBounds, intendedBlindBounds, wrongMoreBounds;
                    double effectiveness, rawChaos, rawLightning;
                    Game.SupportedHitInputs initialInputs;
                    Game.ReservationDamageProfile normal, intendedBlind;
                    using (new Game.PlayerStatScope(tempestConn, fresh: true))
                    {
                        var player = Game.CharacterPlayerStats(tempestConn);
                        Assert.Equal(rolledBiteDamage,
                            player.GetValueOrDefault(Program.Stats.Key(biteDamageStat)!.Value));
                        Assert.Equal(100, player.GetValueOrDefault(Program.Stats.Key("damage_+%")!.Value));
                        Assert.Equal(1, player.GetValueOrDefault(Program.Stats.Key("global_cannot_crit")!.Value));
                        initialInputs = Assert.IsType<Game.SupportedHitInputs>(
                            Game.SupportedHitInputsFor(tempestConn, source.Id, capturedGem: supported));
                        Assert.Equal(SkillHitKind.Spell, initialInputs.Plan.Kind);
                        Assert.False(initialInputs.Attack);
                        Assert.False(initialInputs.Area);
                        Assert.Equal(rolledBiteDamage, initialInputs.Get(biteDamageStat));
                        Assert.Equal(100, initialInputs.Get("damage_+%"));
                        Assert.Equal(1, initialInputs.Get("global_cannot_crit"));
                        Assert.Equal([DamageType.Lightning, DamageType.Chaos],
                            initialInputs.Raw.Components.Select(component => component.Type).ToArray());
                        effectiveness = Game.AddedEffectiveness(tempestConn, source.Effect, source.Level);
                        Assert.True(effectiveness > 0);
                        rawChaos = initialInputs.Raw[DamageType.Chaos].Min;
                        rawLightning = initialInputs.Raw[DamageType.Lightning].Min;
                        Assert.Equal(rawChaos, initialInputs.Raw[DamageType.Chaos].Max, 9);
                        Assert.Equal(10_000 * effectiveness, rawChaos, 6);
                        Assert.True(rawLightning > 0);

                        normal = Profile(initialInputs, 0);
                        intendedBlind = Profile(initialInputs, rolledBiteDamage);
                        var wrongMore = normal.Hit.Scale(1.4);
                        Assert.Equal(2d, normal.Hit.MinTotal / initialInputs.Raw.MinTotal, 9);
                        Assert.Equal(2.4, intendedBlind.Hit.MinTotal / initialInputs.Raw.MinTotal, 9);
                        Assert.Equal(2.8, wrongMore.MinTotal / initialInputs.Raw.MinTotal, 9);
                        Assert.Equal(MonsterIgnites.Settings.None, normal.Bleed);
                        Assert.Equal(MonsterIgnites.Settings.None, intendedBlind.Bleed);
                        normalBounds = FloorComponents(normal.Hit);
                        intendedBlindBounds = FloorComponents(intendedBlind.Hit);
                        wrongMoreBounds = FloorComponents(wrongMore);
                        Assert.True(intendedBlindBounds.Min > normalBounds.Max,
                            $"normal {normalBounds} and intended Blind {intendedBlindBounds} overlap");
                        Assert.True(wrongMoreBounds.Min > intendedBlindBounds.Max,
                            $"intended Blind {intendedBlindBounds} and wrong more {wrongMoreBounds} overlap");
                    }

                    // A pure-packet seam control: Herald secondary damage cannot borrow the spell's large added chaos.
                    var heraldAura = new AuraCatalog.Aura(heraldSource.Id, heraldSource.Effect, "HeraldOfIce",
                        heraldSource.Name, 1, 0, 0, 0, Level: heraldSupported.Level);
                    var heraldPacket = Game.ReservationHitPacket(tempestConn, heraldAura);
                    Assert.Equal(0, heraldPacket[DamageType.Chaos].Max);

                    const uint controlId = 0xC47401u;
                    var control = Target(controlId, "tempest-unblinded-control");
                    AssertNeutralTarget(control);
                    Assert.False(Game.Blinded(tempestConn, controlId));
                    int controlDamage = await RunProc(controlId, control, "tempest-bite-control", now);
                    Assert.True(controlDamage >= normalBounds.Min && controlDamage <= normalBounds.Max,
                        $"actual source Bite {rolledBiteDamage}, generic damage 100%, normal bounds "
                        + $"{normalBounds}, intended {intendedBlindBounds}, actual unblinded control "
                        + $"{controlDamage}, target life {1_000_000}->{control.Life}");
                    lock (tempestConn.Monsters) tempestConn.Monsters.Remove(controlId);

                    const uint blindId = 0xC47402u;
                    var blindTarget = Target(blindId, "tempest-owned-blind-positive");
                    AssertNeutralTarget(blindTarget);
                    lock (tempestConn.Monsters)
                    {
                        Assert.Same(blindTarget, tempestConn.Monsters[blindId]);
                        Assert.True(Game.InflictMonsterBlind(tempestConn, blindId, blindTarget, now,
                            (100, 0, 4), new Random(1)));
                    }
                    Assert.True(Game.Blinded(tempestConn, blindId));
                    Assert.True(Game.ResourceEffectsCurrent(tempestConn, owner));
                    uint blindBefore = blindTarget.Life;
                    int blindDamage = await RunProc(blindId, blindTarget, "tempest-bite-positive", now);
                    Assert.True(blindDamage >= intendedBlindBounds.Min && blindDamage <= intendedBlindBounds.Max,
                        $"controlled Tempest Shield proc consumer: source Bite {rolledBiteDamage}, "
                        + $"support count {supported.Supports.Count}, effectiveness {effectiveness:0.######}, "
                        + $"raw Lightning {rawLightning:0.###}, raw Chaos {rawChaos:0.###}, "
                        + $"normal {normalBounds}, intended additive Blind {intendedBlindBounds}, "
                        + $"wrong more {wrongMoreBounds}, actual {blindDamage}, target life "
                        + $"{blindBefore}->{blindTarget.Life}, aura reservation {reservedMana}/"
                        + $"{tempestConn.Pools.Mana}, owned Blind {Game.Blinded(tempestConn, blindId)}; "
                        + "this is the controlled proc consumer boundary, not a real block producer.");
                    lock (tempestConn.Monsters) tempestConn.Monsters.Remove(blindId);

                    var heraldBounds = new Dictionary<string, (int Min, int Max)>();
                    var heraldHistory = new List<(SpawnedMonster Victim, double At, double Poison, double PoisonUntil,
                        double Ignite, double IgniteUntil)>();
                    async Task CheckHerald(string effectId, uint firstId, bool sourceRemoved)
                    {
                        var grant = Assert.Single(Game.GemSkillEffects(tempestConn), g =>
                            Game.SkillsFor(tempestConn).EffectId(g.Effect) == effectId);
                        var admitted = Game.SupportedSkill(tempestConn, grant.Id);
                        Assert.True(admitted.RequirementsMet);
                        if (!tempestConn.Auras.ContainsKey(grant.Id))
                        {
                            var cost = Game.SupportedReservationCost(tempestConn, grant.Id);
                            Assert.NotNull(cost);
                            Assert.True(await Game.ToggleAura("herald-bite", tempestWire.Stream, tempestWire.Ciphers,
                                tempestConn, grant.Id, grant.Effect, grant.Name, cost.Value, admitted.Level, admitted.Quality));
                        }
                        Assert.True(tempestConn.Auras.TryGetValue(grant.Id, out var aura));
                        Assert.Equal(effectId, aura!.EffectId);
                        SetStat("global_minimum_added_chaos_damage", 10_000);
                        SetStat("global_maximum_added_chaos_damage", 10_000);
                        SetStat("spell_damage_+%", 900);
                        // A secondary hit must ignore this deliberately much larger spell-only flat source.
                        SetStat("spell_minimum_added_chaos_damage", 1_000_000);
                        SetStat("spell_maximum_added_chaos_damage", 1_000_000);
                        SetStat("global_minimum_added_fire_damage", 1_000);
                        SetStat("global_maximum_added_fire_damage", 1_000);
                        SetStat("base_chance_to_ignite_%", 100);
                        SetStat("base_chance_to_poison_on_hit_%", 100);
                        double at = Game.ResourceEffectsTime(tempestConn);
                        var raw = Game.SkillsFor(tempestConn).SecondaryHitDamage(grant.Effect, admitted.Level, admitted.Quality)
                            .Add(DamageType.Fire, new(1_000 * Game.AddedEffectiveness(tempestConn, grant.Effect, admitted.Level),
                                1_000 * Game.AddedEffectiveness(tempestConn, grant.Effect, admitted.Level)))
                            .Add(DamageType.Chaos, new(10_000 * Game.AddedEffectiveness(tempestConn, grant.Effect, admitted.Level),
                                10_000 * Game.AddedEffectiveness(tempestConn, grant.Effect, admitted.Level)));
                        (int Min, int Max) Bounds(double factor) =>
                            (raw.Components.Sum(c => (int)Math.Floor(c.Range.Min * factor)),
                                raw.Components.Sum(c => (int)Math.Floor(c.Range.Max * factor)));
                        var baseline = Bounds(2);
                        var blinded = Bounds(2.4);
                        Assert.True(blinded.Min > baseline.Max);
                        if (!sourceRemoved) heraldBounds[effectId] = baseline;
                        else Assert.Equal(heraldBounds[effectId], baseline);
                        double Poison(double factor) => Math.Round(raw[DamageType.Chaos].Min * .3 * factor * 60) / 60;
                        double Ignite(double factor) => Math.Round(raw[DamageType.Fire].Min * .9 * factor * 60) / 60;
                        void CheckAilments(SpawnedMonster victim, double factor, double poisonBefore = 0)
                        {
                            Console.WriteLine($"#504 {effectId} real secondary ailment state factor={factor} "
                                + $"poison={victim.Poisons.TotalDps(at)-poisonBefore}/{Poison(factor)} "
                                + $"ignite={victim.Ignites.PerSecond(at)}/{Ignite(factor)} sourceRemoved={sourceRemoved}");
                            Assert.Equal(Poison(factor), victim.Poisons.TotalDps(at) - poisonBefore, 6);
                            Assert.Equal(Ignite(factor), victim.Ignites.PerSecond(at), 6);
                            Assert.Equal(at + 2, victim.Poisons.Until, 9);
                            Assert.Equal(at + 4, victim.Ignites.Until, 9);
                            Assert.False(victim.Bleeds.Burning);
                        }
                        async Task<int> Proc(uint id, SpawnedMonster victim, string phase)
                        {
                            uint before = victim.Life;
                            Assert.Empty(tempestConn.Reservations.Procs);
                            tempestConn.Reservations.Procs.Enqueue(new(grant.Id, victim.Walk.Position(at), id));
                            await Game.ReservationSkillsTick(effectId + "-" + phase, tempestWire.Stream, tempestWire.Ciphers,
                                tempestConn, at);
                            Assert.Empty(tempestConn.Reservations.Procs);
                            return checked((int)(before - victim.Life));
                        }
                        var positive = Target(firstId, effectId + "-blinded");
                        if (!sourceRemoved)
                        {
                            Assert.False(Game.Blinded(tempestConn, firstId));
                            int control = await Proc(firstId, positive, "unblinded-control");
                            Assert.InRange(control, baseline.Min, baseline.Max);
                            CheckAilments(positive, 2);
                        }
                        Assert.True(Game.InflictMonsterBlind(tempestConn, firstId, positive, at,
                            (100, 0, 4), new Random(1)));
                        Assert.True(Game.Blinded(tempestConn, firstId));
                        var neighbor = Target(firstId + 1, effectId + "-unblinded-neighbor");
                        uint neighborBefore = neighbor.Life;
                        double previousPoison = positive.Poisons.TotalDps(at);
                        int damage = await Proc(firstId, positive, sourceRemoved ? "source-off" : "blinded-positive");
                        var expected = sourceRemoved ? baseline : blinded;
                        Assert.True(damage >= expected.Min && damage <= expected.Max,
                            $"{effectId} controlled secondary consumer, Bite removed={sourceRemoved}: actual {damage}, "
                            + $"normal {baseline}, additive {blinded}, wrong more {Bounds(2.8)}; spell-only sources must not apply");
                        CheckAilments(positive, sourceRemoved ? 2 : 2.4, previousPoison);
                        if (effectId == "HeraldOfIce")
                        {
                            Assert.InRange(checked((int)(neighborBefore - neighbor.Life)), baseline.Min, baseline.Max);
                            CheckAilments(neighbor, 2);
                        }
                        else
                        {
                            Assert.Equal(neighborBefore, neighbor.Life);
                            Assert.False(neighbor.Poisons.Poisoned);
                            Assert.False(neighbor.Ignites.Burning);
                        }
                        if (!sourceRemoved)
                            heraldHistory.Add((positive, at, positive.Poisons.TotalDps(at), positive.Poisons.Until,
                                positive.Ignites.PerSecond(at), positive.Ignites.Until));
                        lock (tempestConn.Monsters)
                        {
                            tempestConn.Monsters.Remove(firstId);
                            tempestConn.Monsters.Remove(firstId + 1);
                        }
                        if (!sourceRemoved)
                        {
                            SetStat("global_chance_to_blind_on_hit_%", 100);
                            var firstBlind = Target(firstId + 2, effectId + "-first-new-blind");
                            Assert.False(Game.Blinded(tempestConn, firstId + 2));
                            Assert.InRange(await Proc(firstId + 2, firstBlind, "first-new-blind"), baseline.Min, baseline.Max);
                            CheckAilments(firstBlind, 2);
                            Assert.True(Game.Blinded(tempestConn, firstId + 2));
                            Assert.InRange(await Proc(firstId + 2, firstBlind, "already-blinded"), blinded.Min, blinded.Max);
                            CheckAilments(firstBlind, 2.4, Poison(2));
                            SetStat("global_chance_to_blind_on_hit_%", 0);
                            lock (tempestConn.Monsters) tempestConn.Monsters.Remove(firstId + 2);
                            var immune = Target(firstId + 3, effectId + "-blind-immune");
                            immune.Mods = ShockImmune() with { Effects = new(new Dictionary<string, int>
                                { ["never_shock"] = 1, ["cannot_be_blinded"] = 1 }) };
                            Assert.False(Game.InflictMonsterBlind(tempestConn, firstId + 3, immune, at,
                                (100, 0, 4), new Random(1)));
                            Assert.InRange(await Proc(firstId + 3, immune, "blind-immune"), baseline.Min, baseline.Max);
                            CheckAilments(immune, 2);
                            lock (tempestConn.Monsters) tempestConn.Monsters.Remove(firstId + 3);
                            SetStat("base_deal_no_damage", 1);
                            var prohibited = Target(firstId + 4, effectId + "-prohibited");
                            Assert.Equal(0, await Proc(firstId + 4, prohibited, "prohibited"));
                            Assert.False(prohibited.Poisons.Poisoned);
                            Assert.False(prohibited.Ignites.Burning);
                            SetStat("base_deal_no_damage", 0);
                            lock (tempestConn.Monsters) tempestConn.Monsters.Remove(firstId + 4);
                            SetStat("base_chance_to_poison_on_hit_%", 0);
                            SetStat("chance_to_poison_on_hit_with_attacks_%", 100);
                            SetStat("spells_chance_to_poison_on_hit_%", 100);
                            var familyOnly = Target(firstId + 5, effectId + "-attack-spell-only-poison");
                            Assert.InRange(await Proc(firstId + 5, familyOnly, "attack-spell-only-poison"), baseline.Min, baseline.Max);
                            Assert.False(familyOnly.Poisons.Poisoned);
                            Assert.Equal(Ignite(2), familyOnly.Ignites.PerSecond(at), 6);
                            SetStat("chance_to_poison_on_hit_with_attacks_%", 0);
                            SetStat("spells_chance_to_poison_on_hit_%", 0);
                            SetStat("base_chance_to_poison_on_hit_%", 100);
                            lock (tempestConn.Monsters) tempestConn.Monsters.Remove(firstId + 5);
                            SetStat("never_damaging_ailments", 1);
                            var noAilments = Target(firstId + 6, effectId + "-ailments-prohibited");
                            Assert.InRange(await Proc(firstId + 6, noAilments, "ailments-prohibited"), baseline.Min, baseline.Max);
                            Assert.False(noAilments.Poisons.Poisoned);
                            Assert.False(noAilments.Ignites.Burning);
                            SetStat("never_damaging_ailments", 0);
                            lock (tempestConn.Monsters) tempestConn.Monsters.Remove(firstId + 6);
                        }
                        Console.WriteLine($"#504 {effectId} secondary consumer sourceRemoved={sourceRemoved} "
                            + $"actual={damage} normal={baseline} additive={blinded}; active aura {grant.Id}, controlled queue only");
                        SetStat("global_minimum_added_fire_damage", 0);
                        SetStat("global_maximum_added_fire_damage", 0);
                        SetStat("base_chance_to_ignite_%", 0);
                        SetStat("base_chance_to_poison_on_hit_%", 0);
                        SetStat("global_minimum_added_chaos_damage", 0);
                        SetStat("global_maximum_added_chaos_damage", 0);
                        SetStat("spell_damage_+%", 0);
                        SetStat("spell_minimum_added_chaos_damage", 10_000);
                        SetStat("spell_maximum_added_chaos_damage", 10_000);
                    }
                    await CheckHerald("HeraldOfIce", 0xC47500, false);
                    await CheckHerald("HeraldOfThunder", 0xC47600, false);

                    SetStat("spell_minimum_added_fire_damage", 1_000);
                    SetStat("spell_maximum_added_fire_damage", 1_000);
                    SetStat("base_chance_to_ignite_%", 100);
                    SetStat("base_chance_to_poison_on_hit_%", 100);
                    SetStat("global_chance_to_blind_on_hit_%", 100);

                    Game.ReservationDamageProfile expandedNormal, expandedBlind;
                    double expandedRawFire, expandedRawChaos;
                    (int Min, int Max) expandedNormalBounds, expandedBlindBounds;
                    using (new Game.PlayerStatScope(tempestConn, fresh: true))
                    {
                        var player = Game.CharacterPlayerStats(tempestConn);
                        Assert.Equal(rolledBiteDamage,
                            player.GetValueOrDefault(Program.Stats.Key(biteDamageStat)!.Value));
                        var inputs = Assert.IsType<Game.SupportedHitInputs>(
                            Game.SupportedHitInputsFor(tempestConn, source.Id, capturedGem: supported));
                        Assert.Equal(rolledBiteDamage, inputs.Get(biteDamageStat));
                        Assert.False(inputs.Area);
                        expandedRawFire = inputs.Raw[DamageType.Fire].Min;
                        expandedRawChaos = inputs.Raw[DamageType.Chaos].Min;
                        Assert.Equal(1_000 * effectiveness, expandedRawFire, 6);
                        Assert.Equal(expandedRawFire, inputs.Raw[DamageType.Fire].Max, 9);
                        expandedNormal = Profile(inputs, 0);
                        expandedBlind = Profile(inputs, rolledBiteDamage);
                        double expectedNormalIgnite = .9 * expandedRawFire * (1 + 100 / 100d);
                        double expectedNormalPoison = .3 * expandedRawChaos * (1 + 100 / 100d);
                        Assert.Equal(expectedNormalIgnite, expandedNormal.Ignite.Min, 9);
                        Assert.Equal(expectedNormalPoison, expandedNormal.Poison.Min, 9);
                        Assert.Equal(1.2, expandedBlind.Ignite.Min / expandedNormal.Ignite.Min, 9);
                        Assert.Equal(1.2, expandedBlind.Poison.Min / expandedNormal.Poison.Min, 9);
                        Assert.Equal(MonsterIgnites.Settings.None, expandedNormal.Bleed);
                        Assert.Equal(MonsterIgnites.Settings.None, expandedBlind.Bleed);
                        expandedNormalBounds = FloorComponents(expandedNormal.Hit);
                        expandedBlindBounds = FloorComponents(expandedBlind.Hit);
                        Assert.True(expandedBlindBounds.Min > expandedNormalBounds.Max);
                    }
                    uint chainKey = Program.Stats.Key("number_of_chains")
                        ?? throw new InvalidOperationException("missing required stat number_of_chains");
                    Assert.Equal(1, supported.Stats.GetValueOrDefault(chainKey));

                    // This is the first contact on a currently unblinded target: profile selection happens before its
                    // own on-hit Blind, so only the second contact uses the additive Bite profile.
                    const uint ownBlindId = 0xC47403u;
                    var ownBlind = Target(ownBlindId, "tempest-first-new-blind");
                    AssertNeutralTarget(ownBlind);
                    Assert.False(Game.Blinded(tempestConn, ownBlindId));
                    int firstContact = await RunProc(ownBlindId, ownBlind, "tempest-first-new-blind", now);
                    Assert.InRange(firstContact, expandedNormalBounds.Min, expandedNormalBounds.Max);
                    Assert.True(Game.Blinded(tempestConn, ownBlindId));
                    Assert.Equal(now, ownBlind.Ignites.LastApplied, 9);
                    Assert.Equal(expandedNormal.Ignite.Min, ownBlind.Ignites.PerSecond(now), 9);
                    Assert.Equal(now + expandedNormal.Ignite.Seconds, ownBlind.Ignites.Until, 9);
                    Assert.Equal(1, ownBlind.Poisons.Stacks(now));
                    Assert.Equal(expandedNormal.Poison.Min, ownBlind.Poisons.TotalDps(now), 9);
                    double firstPoisonUntil = ownBlind.Poisons.Until;
                    Assert.Equal(now + expandedNormal.Poison.Seconds, firstPoisonUntil, 9);

                    fixedRuntime.Advance(1.25);
                    double secondStart = Game.ResourceEffectsTime(tempestConn);
                    Assert.Equal(now + 1.25, secondStart, 9);
                    double firstOnlyMidpoint = (firstPoisonUntil - expandedNormal.Poison.Seconds + firstPoisonUntil) / 2;
                    Assert.True(firstOnlyMidpoint < secondStart);
                    Assert.Equal(1, ownBlind.Poisons.Stacks(firstOnlyMidpoint));
                    double poisonBeforeSecond = ownBlind.Poisons.TotalDps(secondStart);
                    Assert.Equal(expandedNormal.Poison.Min, poisonBeforeSecond, 9);

                    // Tempest Shield has one native chain here: this proc contacts the targeted Blind victim and then
                    // one equally close unblinded neighbor, proving that the selected profiles do not leak across them.
                    const uint nextUnblindId = 0xC47404u;
                    var nextUnblind = Target(nextUnblindId, "tempest-next-unblinded");
                    AssertNeutralTarget(nextUnblind);
                    uint neighborBefore = nextUnblind.Life;
                    int secondContact = await RunProc(ownBlindId, ownBlind, "tempest-owned-blind-second", secondStart);
                    Assert.InRange(secondContact, expandedBlindBounds.Min, expandedBlindBounds.Max);
                    int nextDamage = checked((int)(neighborBefore - nextUnblind.Life));
                    Assert.InRange(nextDamage, expandedNormalBounds.Min, expandedNormalBounds.Max);
                    Assert.True(Game.Blinded(tempestConn, nextUnblindId));
                    Assert.Equal(expandedNormal.Ignite.Min, nextUnblind.Ignites.PerSecond(secondStart), 9);
                    Assert.Equal(expandedNormal.Poison.Min, nextUnblind.Poisons.TotalDps(secondStart), 9);
                    Assert.Equal(secondStart, ownBlind.Ignites.LastApplied, 9);
                    Assert.Equal(expandedBlind.Ignite.Min, ownBlind.Ignites.PerSecond(secondStart), 9);
                    Assert.Equal(secondStart + expandedBlind.Ignite.Seconds, ownBlind.Ignites.Until, 9);
                    Assert.Equal(2, ownBlind.Poisons.Stacks(secondStart));
                    Assert.Equal(poisonBeforeSecond + expandedBlind.Poison.Min,
                        ownBlind.Poisons.TotalDps(secondStart), 9);
                    Assert.Equal(expandedNormal.Poison.Min, ownBlind.Poisons.TotalDps(firstOnlyMidpoint), 9);
                    Assert.Equal(secondStart + expandedBlind.Poison.Seconds, ownBlind.Poisons.Until, 9);
                    double firstOnlyAfter = firstPoisonUntil + .001;
                    Assert.True(firstOnlyAfter < secondStart + expandedBlind.Poison.Seconds);
                    Assert.Equal(1, ownBlind.Poisons.Stacks(firstOnlyAfter));
                    Assert.Equal(expandedBlind.Poison.Min, ownBlind.Poisons.TotalDps(firstOnlyAfter), 9);
                    lock (tempestConn.Monsters)
                    {
                        tempestConn.Monsters.Remove(nextUnblindId);
                        tempestConn.Monsters.Remove(ownBlindId);
                    }
                    double oldPoisonDps = ownBlind.Poisons.TotalDps(secondStart);
                    double oldPoisonUntil = ownBlind.Poisons.Until;
                    double oldIgniteDps = ownBlind.Ignites.PerSecond(secondStart);
                    double oldIgniteUntil = ownBlind.Ignites.Until;

                    const uint blindImmuneId = 0xC47405u;
                    var blindImmune = Target(blindImmuneId, "tempest-blind-immune");
                    blindImmune.Mods = ShockImmune() with
                    {
                        Effects = new(new Dictionary<string, int> { ["never_shock"] = 1, ["cannot_be_blinded"] = 1 }),
                    };
                    Assert.False(Game.Blinded(tempestConn, blindImmuneId));
                    Assert.False(Game.InflictMonsterBlind(tempestConn, blindImmuneId, blindImmune, secondStart,
                        (100, 0, 4), new Random(1)));
                    int immuneDamage = await RunProc(blindImmuneId, blindImmune, "tempest-blind-immune", secondStart);
                    Assert.InRange(immuneDamage, expandedNormalBounds.Min, expandedNormalBounds.Max);
                    Assert.False(Game.Blinded(tempestConn, blindImmuneId));
                    lock (tempestConn.Monsters) tempestConn.Monsters.Remove(blindImmuneId);

                    async Task AssertExcluded(uint id, SpawnedMonster target, string label)
                    {
                        int damage = await RunProc(id, target, label, secondStart);
                        Assert.Equal(0, damage);
                        Assert.False(target.Ignites.Burning);
                        Assert.False(target.Poisons.Poisoned);
                        lock (tempestConn.Monsters) tempestConn.Monsters.Remove(id);
                    }
                    await AssertExcluded(0xC47406u,
                        Target(0xC47406u, "tempest-friendly", hostile: false), "tempest-friendly");
                    var dead = Target(0xC47407u, "tempest-dead");
                    dead.Dead = true;
                    dead.Life = 0;
                    await AssertExcluded(0xC47407u, dead, "tempest-dead");
                    var asleep = Target(0xC47408u, "tempest-asleep");
                    asleep.Asleep = true;
                    await AssertExcluded(0xC47408u, asleep, "tempest-asleep");
                    var noDamage = Target(0xC47409u, "tempest-no-damage");
                    noDamage.KingGrace = true;
                    await AssertExcluded(0xC47409u, noDamage, "tempest-no-damage");

                    uint prohibitionKey = Program.Stats.Key("base_deal_no_damage")
                        ?? throw new InvalidOperationException("missing required stat base_deal_no_damage");
                    SetStat("base_deal_no_damage", 1);
                    using (new Game.PlayerStatScope(tempestConn, fresh: true))
                    {
                        var inputs = Assert.IsType<Game.SupportedHitInputs>(
                            Game.SupportedHitInputsFor(tempestConn, source.Id, capturedGem: supported));
                        Assert.Equal(1, inputs.Get("base_deal_no_damage"));
                        Assert.True(DamageStats.Prohibited(DamageType.Chaos, inputs.Get));
                        Assert.Equal(0, Game.SupportedHitFrom(inputs, suppress: true).MaxTotal);
                        var prohibitedIgnite = Game.SupportedIgnite(tempestConn, source.Id, true,
                            Game.CharacterPlayerStats(tempestConn), capturedInputs: inputs);
                        var prohibitedPoison = Game.SupportedPoison(tempestConn, source.Id, true,
                            Game.CharacterPlayerStats(tempestConn), capturedInputs: inputs);
                        Assert.Equal(0, prohibitedIgnite.Min + prohibitedIgnite.Max);
                        Assert.Equal(0, prohibitedPoison.Min + prohibitedPoison.Max);
                    }
                    const uint prohibitedId = 0xC4740Au;
                    var prohibited = Target(prohibitedId, "tempest-prohibited-damage");
                    uint prohibitedBefore = prohibited.Life;
                    Assert.Equal(0, await RunProc(prohibitedId, prohibited, "tempest-prohibited-damage", secondStart));
                    Assert.Equal(prohibitedBefore, prohibited.Life);
                    Assert.False(prohibited.Ignites.Burning);
                    Assert.False(prohibited.Poisons.Poisoned);
                    SetStat("base_deal_no_damage", 0);
                    lock (tempestConn.Monsters) tempestConn.Monsters.Remove(prohibitedId);

                    var removedBite = inventory.Remove(PlayerInventories.Helmet, bitePlacement.Id);
                    Assert.NotNull(removedBite);
                    Game.ReconcileOwnAuras(tempestConn, validateSkills: true);
                    Assert.True(tempestConn.Auras.TryGetValue(source.Id, out var stillActive));
                    Assert.Equal("TempestShield", stillActive!.EffectId);
                    var stillSupported = Game.SupportedSkill(tempestConn, source.Id);
                    Assert.True(stillSupported.RequirementsMet);
                    Assert.Equal(source.Effect, stillSupported.Effect);
                    using (new Game.PlayerStatScope(tempestConn, fresh: true))
                    {
                        var player = Game.CharacterPlayerStats(tempestConn);
                        Assert.Equal(0, player.GetValueOrDefault(Program.Stats.Key(biteDamageStat)!.Value));
                        var inputs = Assert.IsType<Game.SupportedHitInputs>(
                            Game.SupportedHitInputsFor(tempestConn, source.Id, capturedGem: stillSupported));
                        Assert.Equal(0, inputs.Get(biteDamageStat));
                    }
                    foreach (var earned in heraldHistory)
                    {
                        Assert.Equal(earned.Poison, earned.Victim.Poisons.TotalDps(earned.At), 9);
                        Assert.Equal(earned.PoisonUntil, earned.Victim.Poisons.Until, 9);
                        Assert.Equal(earned.Ignite, earned.Victim.Ignites.PerSecond(earned.At), 9);
                        Assert.Equal(earned.IgniteUntil, earned.Victim.Ignites.Until, 9);
                    }
                    Assert.Equal(oldPoisonDps, ownBlind.Poisons.TotalDps(secondStart), 9);
                    Assert.Equal(oldPoisonUntil, ownBlind.Poisons.Until, 9);
                    Assert.Equal(oldIgniteDps, ownBlind.Ignites.PerSecond(secondStart), 9);
                    Assert.Equal(oldIgniteUntil, ownBlind.Ignites.Until, 9);

                    const uint noBiteId = 0xC4740Bu;
                    var noBiteTarget = Target(noBiteId, "tempest-source-off");
                    Assert.True(Game.InflictMonsterBlind(tempestConn, noBiteId, noBiteTarget, secondStart,
                        (100, 0, 4), new Random(1)));
                    Assert.True(Game.Blinded(tempestConn, noBiteId));
                    int noBiteDamage = await RunProc(noBiteId, noBiteTarget, "tempest-source-off", secondStart);
                    Assert.InRange(noBiteDamage, expandedNormalBounds.Min, expandedNormalBounds.Max);
                    Assert.True(Game.Blinded(tempestConn, noBiteId));
                    Assert.Equal(expandedNormal.Ignite.Min, noBiteTarget.Ignites.PerSecond(secondStart), 9);
                    Assert.Equal(expandedNormal.Poison.Min, noBiteTarget.Poisons.TotalDps(secondStart), 9);
                    lock (tempestConn.Monsters) tempestConn.Monsters.Remove(noBiteId);
                    Assert.True(reservedMana > 0 && reservedMana < tempestConn.Pools.Mana);
                    SetStat("spell_minimum_added_fire_damage", 0);
                    SetStat("spell_maximum_added_fire_damage", 0);
                    SetStat("global_chance_to_blind_on_hit_%", 0);
                    await CheckHerald("HeraldOfIce", 0xC47700, true);
                    await CheckHerald("HeraldOfThunder", 0xC47800, true);

                    // The real cast pays mana and creates/arms the placed object on a controlled update clock.
                    // No placed packet or damage profile is fabricated by this fixture.
                    targetAt = (100, 100);
                    tempestConn.Walk.Teleport(targetAt.X, targetAt.Y, schedule.At);
                    inventory.Add(PlayerInventories.Helmet, 0, 0, bite);
                    SetStat("spell_minimum_added_fire_damage", 1_000);
                    SetStat("spell_maximum_added_fire_damage", 1_000);
                    SetStat("spell_minimum_added_chaos_damage", 10_000);
                    SetStat("spell_maximum_added_chaos_damage", 10_000);
                    SetStat("base_chance_to_ignite_%", 100);
                    SetStat("base_chance_to_poison_on_hit_%", 100);
                    var placedTick = typeof(Game).GetMethod("PlacedSkillsTick",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                    Assert.NotNull(placedTick);
                    var mineLeechItem = new UniqueCatalog(Program.Items).Create("Machina Mitts", new Random(504))!;
                    var mineLeechPlacement = inventory.Add(PlayerInventories.Gloves, 0, 0, mineLeechItem);
                    Game.ReconcileOwnAuras(tempestConn, validateSkills: true);
                    Assert.Equal(100, Game.CharacterPlayerStats(tempestConn)
                        .GetValueOrDefault(Program.Stats.Key("mine_damage_leeched_as_life_to_you_permyriad")!.Value));
                    foreach (var (name, mineKind, firstId) in new[]
                        { ("Fire Trap", false, 0xC47900u), ("Stormblast Mine", true, 0xC47A00u) })
                    {
                        var placedGem = Gem(name, 1);
                        Assert.NotNull(inventory.Change(PlayerInventories.Weapon1, swordPlacement.Id,
                            item => item with { Sockets = [new(SocketColour.White, placedGem)], Links = [] }));
                        Game.ReconcileOwnAuras(tempestConn, validateSkills: true);
                        var placedSource = Assert.Single(Game.GemSkillEffects(tempestConn), g => g.Name == name);
                        var placedSupported = Game.SupportedSkill(tempestConn, placedSource.Id);
                        Assert.True(placedSupported.RequirementsMet);
                        Assert.Equal((!mineKind, mineKind), Game.PlacedKind(placedSupported));
                        double placedEffectiveness = Game.AddedEffectiveness(tempestConn, placedSource.Effect, placedSupported.Level);
                        var basePacket = Game.SkillsFor(tempestConn).HitDamage(placedSource.Effect, placedSupported.Level);
                        var placedRaw = basePacket
                            .Add(DamageType.Fire, new(1_000 * placedEffectiveness, 1_000 * placedEffectiveness))
                            .Add(DamageType.Chaos, new(10_000 * placedEffectiveness, 10_000 * placedEffectiveness));
                        (int Min, int Max) PlacedBounds(double factor) =>
                            (placedRaw.Components.Sum(c => (int)Math.Floor(c.Range.Min * factor)),
                                placedRaw.Components.Sum(c => (int)Math.Floor(c.Range.Max * factor)));
                        var placedNormalBounds = PlacedBounds(2); var placedBlindBounds = PlacedBounds(2.4);
                        Assert.True(placedBlindBounds.Min > placedNormalBounds.Max);
                        async Task<(int Normal, int Blind, SpawnedMonster Plain, SpawnedMonster Blinded, double At)> PlacedContact(uint id)
                        {
                            tempestConn.Monsters.Clear();tempestConn.Placed.Grounds.Clear();
                            tempestConn.RecentEvents.Clear();
                            Game.LeechOf(tempestConn).Clear(Game.ResourceEffectsTime(tempestConn));
                            tempestConn.PlayerLife = (uint)Math.Max(1, tempestConn.Pools.Life / 2);
                            Assert.Empty(tempestConn.Placed.All);
                            var plain = Target(id, name + "-plain");
                            var blinded = Target(id + 1, name + "-blinded");
                            Assert.True(Game.InflictMonsterBlind(tempestConn, id + 1, blinded,
                                Game.ResourceEffectsTime(tempestConn), (100, 0, 20), new Random(1)));
                            Assert.True(Game.Blinded(tempestConn, id + 1));
                            tempestConn.PlayerMana = tempestConn.Pools.Mana;
                            uint manaBefore = tempestConn.PlayerMana;
                            schedule.AdvanceOnWait = true;
                            try
                            {
                                Assert.True(await Game.CastGemSkill("placed-bite-real-cast", tempestWire.Stream,
                                    tempestWire.Ciphers, tempestConn, placedSource.Id, targetAt, targetAt));
                            }
                            finally { schedule.AdvanceOnWait = false; }
                            Assert.True(tempestConn.PlayerMana < manaBefore);
                            var placed = Assert.Single(tempestConn.Placed.All.Values);
                            Assert.Equal(mineKind, placed.Mine);
                            Assert.False(placed.Landed);
                            double at = placed.Armed + .1;
                            schedule.At = at;
                            fixedRuntime.Advance(at - Game.ResourceEffectsTime(tempestConn));
                            if (mineKind) Game.DetonateMinesLocked(tempestConn, at);
                            Assert.False(tempestConn.RecentEvents.Happened(StatConditions.DetonatedMine,
                                tempestConn.Clock.Elapsed.TotalSeconds)); // Scheduling is not a detonation.
                            await (Task)placedTick.Invoke(null,
                                ["placed-bite-real-trigger", tempestWire.Stream, tempestWire.Ciphers, tempestConn, at])!;
                            Assert.Empty(tempestConn.Placed.All);
                            Assert.True(placed.Landed);
                            Assert.Equal(mineKind, tempestConn.RecentEvents.Happened(StatConditions.DetonatedMine,
                                tempestConn.Clock.Elapsed.TotalSeconds));
                            bool mineLeech = mineKind && (plain.Life < 1_000_000 || blinded.Life < 1_000_000)
                                && Game.CharacterPlayerStats(tempestConn).GetValueOrDefault(
                                    Program.Stats.Key("mine_damage_leeched_as_life_to_you_permyriad")!.Value) > 0;
                            Assert.Equal(mineLeech, Game.LeechOf(tempestConn).Active(at).Life);
                            return (checked((int)(1_000_000 - plain.Life)), checked((int)(1_000_000 - blinded.Life)), plain, blinded, at);
                        }
                        var contact = await PlacedContact(firstId);
                        Assert.InRange(contact.Normal, placedNormalBounds.Min, placedNormalBounds.Max);
                        Console.WriteLine($"#504 actual placed cast {name} paid/armed/triggered normal={contact.Normal} "
                            + $"blind={contact.Blind} expectedNormal={placedNormalBounds} expectedAdditive={placedBlindBounds}");
                        Assert.InRange(contact.Blind, placedBlindBounds.Min, placedBlindBounds.Max);
                        double expectedNormalPoison = Math.Round(.3 * placedRaw[DamageType.Chaos].Min * 2 * 60) / 60;
                        double expectedBlindPoison = Math.Round(.3 * placedRaw[DamageType.Chaos].Min * 2.4 * 60) / 60;
                        Assert.Equal(expectedNormalPoison, contact.Plain.Poisons.TotalDps(contact.At), 6);
                        Assert.Equal(expectedBlindPoison, contact.Blinded.Poisons.TotalDps(contact.At), 6);
                        Assert.InRange(contact.Plain.Ignites.PerSecond(contact.At),
                            .9 * placedRaw[DamageType.Fire].Min * 2 - .02, .9 * placedRaw[DamageType.Fire].Max * 2 + .02);
                        Assert.InRange(contact.Blinded.Ignites.PerSecond(contact.At),
                            .9 * placedRaw[DamageType.Fire].Min * 2.4 - .02, .9 * placedRaw[DamageType.Fire].Max * 2.4 + .02);
                        Assert.False(contact.Plain.Bleeds.Burning);
                        Assert.False(contact.Blinded.Bleeds.Burning);
                        double savedPoison = contact.Blinded.Poisons.TotalDps(contact.At);
                        double savedPoisonUntil = contact.Blinded.Poisons.Until;
                        double savedIgnite = contact.Blinded.Ignites.PerSecond(contact.At);
                        double savedIgniteUntil = contact.Blinded.Ignites.Until;
                        double? groundDps = mineKind ? null : Assert.Single(tempestConn.Placed.Grounds).DamagePerSecond;
                        SetStat("global_chance_to_blind_on_hit_%", 100);
                        var firstBlindContact = await PlacedContact(firstId + 4);
                        Assert.InRange(firstBlindContact.Normal, placedNormalBounds.Min, placedNormalBounds.Max);
                        Assert.True(Game.Blinded(tempestConn, firstId + 4));
                        Assert.Equal(expectedNormalPoison, firstBlindContact.Plain.Poisons.TotalDps(firstBlindContact.At), 6);
                        Assert.InRange(firstBlindContact.Blind, placedBlindBounds.Min, placedBlindBounds.Max);
                        SetStat("global_chance_to_blind_on_hit_%", 0);
                        if (!mineKind) Assert.Equal(groundDps!.Value, Assert.Single(tempestConn.Placed.Grounds).DamagePerSecond, 9);
                        var helmet = Assert.Single(inventory.Items(PlayerInventories.Helmet));
                        Assert.NotNull(inventory.Remove(PlayerInventories.Helmet, helmet.Id));
                        Game.ReconcileOwnAuras(tempestConn, validateSkills: true);
                        Assert.True(Game.SupportedSkill(tempestConn, placedSource.Id).RequirementsMet);
                        var sourceOff = await PlacedContact(firstId + 8);
                        Assert.InRange(sourceOff.Normal, placedNormalBounds.Min, placedNormalBounds.Max);
                        Assert.InRange(sourceOff.Blind, placedNormalBounds.Min, placedNormalBounds.Max);
                        Assert.Equal(expectedNormalPoison, sourceOff.Blinded.Poisons.TotalDps(sourceOff.At), 6);
                        if (!mineKind) Assert.Equal(groundDps!.Value, Assert.Single(tempestConn.Placed.Grounds).DamagePerSecond, 9);
                        Assert.Equal(savedPoison, contact.Blinded.Poisons.TotalDps(contact.At), 9);
                        Assert.Equal(savedPoisonUntil, contact.Blinded.Poisons.Until, 9);
                        Assert.Equal(savedIgnite, contact.Blinded.Ignites.PerSecond(contact.At), 9);
                        Assert.Equal(savedIgniteUntil, contact.Blinded.Ignites.Until, 9);
                        inventory.Add(PlayerInventories.Helmet, 0, 0, bite);
                        Game.ReconcileOwnAuras(tempestConn, validateSkills: true);
                        SetStat("base_deal_no_damage", 1);
                        var forbidden = await PlacedContact(firstId + 12);
                        Assert.Equal(0, forbidden.Normal);
                        Assert.Equal(0, forbidden.Blind);
                        Assert.False(forbidden.Plain.Poisons.Poisoned);
                        Assert.False(forbidden.Blinded.Poisons.Poisoned);
                        Assert.False(forbidden.Plain.Ignites.Burning);
                        Assert.False(forbidden.Blinded.Ignites.Burning);
                        if (!mineKind) Assert.Equal(0, Assert.Single(tempestConn.Placed.Grounds).DamagePerSecond);
                        SetStat("base_deal_no_damage", 0);
                        if (mineKind)
                        {
                            Assert.NotNull(inventory.Remove(PlayerInventories.Gloves, mineLeechPlacement.Id));
                            Game.ReconcileOwnAuras(tempestConn, validateSkills: true);
                            await PlacedContact(firstId + 16); // A real mine hit after the owner's leech item is removed.
                            Assert.False(Game.LeechOf(tempestConn).Active(Game.ResourceEffectsTime(tempestConn)).Life);
                        }
                        tempestConn.Placed.Grounds.Clear();
                    }


                    // Herald of Ash's overkill burn is non-ailment damage. Its queued overkill amount is controlled,
                    // while the equipped source, aura admission and actual strongest-burn consumer are real.
                    var ashGem = Gem("Herald of Ash", 1);
                    Assert.NotNull(inventory.Change(PlayerInventories.Weapon1, swordPlacement.Id,
                        item => item with { Sockets = [new(SocketColour.White, ashGem)], Links = [] }));
                    Game.ReconcileOwnAuras(tempestConn, validateSkills: true);
                    var ashSource = Assert.Single(Game.GemSkillEffects(tempestConn), g => g.Name == "Herald of Ash");
                    var ashSupported = Game.SupportedSkill(tempestConn, ashSource.Id);
                    Assert.True(ashSupported.RequirementsMet);
                    var ashCost = Game.SupportedReservationCost(tempestConn, ashSource.Id);
                    Assert.NotNull(ashCost);
                    tempestConn.PlayerMana = tempestConn.Pools.Mana;
                    Assert.True(await Game.ToggleAura("ash-boundary", tempestWire.Stream, tempestWire.Ciphers,
                        tempestConn, ashSource.Id, ashSource.Effect, ashSource.Name, ashCost.Value,
                        ashSupported.Level, ashSupported.Quality));
                    Assert.True(tempestConn.Auras.TryGetValue(ashSource.Id, out var ashAura));
                    Assert.Equal("HeraldOfAsh", ashAura!.EffectId);
                    SetStat("damage_+%", 700);
                    SetStat("fire_damage_+%", 800);
                    SetStat("elemental_damage_+%", 900);
                    SetStat("burn_damage_+%", 50);
                    SetStat("damage_over_time_+%", 100);
                    SetStat("global_chance_to_blind_on_hit_%", 100);
                    const uint overkill = 1_000;
                    int AshOwn(string n) => ashSupported.Stats.GetValueOrDefault(Program.Stats.Key(n)!.Value);
                    double expectedAsh = overkill * AshOwn("herald_of_ash_burning_%_overkill_damage_per_minute") / 6000d
                        * 2.5 * (1 + AshOwn("herald_of_ash_burning_damage_+%_final") / 100d);
                    Assert.True(expectedAsh > 0);
                    async Task<SpawnedMonster> AshProc(uint id, bool preBlind)
                    {
                        tempestConn.Monsters.Clear();
                        var target = Target(id, "ash-non-ailment-boundary");
                        double at = Game.ResourceEffectsTime(tempestConn);
                        if (preBlind) Assert.True(Game.InflictMonsterBlind(tempestConn, id, target, at,
                            (100, 0, 20), new Random(1)));
                        Assert.Empty(tempestConn.Reservations.Procs);
                        tempestConn.Reservations.Procs.Enqueue(new(ashSource.Id, targetAt, overkill));
                        await Game.ReservationSkillsTick("ash-controlled-overkill", tempestWire.Stream,
                            tempestWire.Ciphers, tempestConn, at);
                        Assert.Empty(tempestConn.Reservations.Procs);
                        Assert.Equal(1_000_000u, target.Life);
                        Assert.False(target.Ignites.Burning);
                        Assert.False(target.Poisons.Poisoned);
                        Assert.Equal(preBlind, Game.Blinded(tempestConn, id));
                        return target;
                    }
                    double ashAt = Game.ResourceEffectsTime(tempestConn);
                    var ashPlain = await AshProc(0xC47B00, false);
                    var ashBlind = await AshProc(0xC47B01, true);
                    Assert.Equal(expectedAsh, ashPlain.HeraldBurns.PerSecond(ashAt), 9);
                    Assert.Equal(expectedAsh, ashBlind.HeraldBurns.PerSecond(ashAt), 9);
                    Assert.Equal(ashAt + 4, ashBlind.HeraldBurns.Until, 9);
                    SetStat("never_damaging_ailments", 1);
                    var notAnAilment = await AshProc(0xC47B02, true);
                    Assert.Equal(expectedAsh, notAnAilment.HeraldBurns.PerSecond(ashAt), 9);
                    SetStat("never_damaging_ailments", 0);
                    SetStat("base_deal_no_damage", 1);
                    var ashForbidden = await AshProc(0xC47B03, true);
                    Console.WriteLine($"#504 actual Ash non-ailment consumer overkill={overkill} normal={expectedAsh} "
                        + $"plain={ashPlain.HeraldBurns.PerSecond(ashAt)} blind={ashBlind.HeraldBurns.PerSecond(ashAt)} "
                        + $"neverAilments={notAnAilment.HeraldBurns.PerSecond(ashAt)} prohibited={ashForbidden.HeraldBurns.PerSecond(ashAt)}");
                    Assert.False(ashForbidden.HeraldBurns.Burning);
                    SetStat("base_deal_no_damage", 0);
                    SetStat("base_deal_no_fire_damage", 1);
                    var ashFireForbidden = await AshProc(0xC47B04, true);
                    Assert.False(ashFireForbidden.HeraldBurns.Burning);
                    SetStat("base_deal_no_fire_damage", 0);
                    Assert.Equal(expectedAsh, ashBlind.HeraldBurns.PerSecond(ashAt), 9);
                    Assert.Equal(ashAt + 4, ashBlind.HeraldBurns.Until, 9);

                    _ = prohibitionKey;
                }
                finally
                {
                    if (tempestConnection is not null) tempestConnection.ProjectileRuntimeState = previousRuntime;
                    fixedRuntime?.Invalidate();
                    if (tempestConnection is not null) tempestConnection.Closed = true;
                    Game.AreaUpdateContext.Value = previousContext;
                    PlayerInventories.Forget(character);
                }
            }

            async Task FootstepBiteDamageFixture()
            {
                // A real connected Polytheist allocation supplies the footstep grant. The callback is controlled;
                // walking animation admission and its native presentation remain separate proof boundaries.
                const string optIn = "Game_VERIFIED_POLYTHEIST";
                string? previousOptIn = Environment.GetEnvironmentVariable(optIn);
                var oldContext = Game.AreaUpdateContext.Value;
                var provider = new ModuleLifetime("PhreciaAscendancies");
                ClientState? self = null;
                Game.ProjectileSession? runtime = null;
                string character = "FootstepBite" + Guid.NewGuid().ToString("N");
                using var footWire = new Wire();
                try
                {
                    Environment.SetEnvironmentVariable(optIn, "1");
                    var catalog = new AscendancyCatalog();
                    var definition = Assert.Single(catalog.Definitions, d => d.Name == "Polytheist");
                    var grant = Assert.Single(new PassiveGrantedSkills(Program.Skills).For(definition.Nodes.Keys),
                        g => PassiveGrantedSkills.IsFootstepSkill(g.EffectId));
                    var parent = new Dictionary<ushort, ushort> { [definition.StartNode] = definition.StartNode };
                    var frontier = new Queue<ushort>(); frontier.Enqueue(definition.StartNode);
                    while (frontier.TryDequeue(out ushort node))
                        foreach (ushort next in definition.Nodes[node].Adjacent)
                            if (parent.TryAdd(next, node)) frontier.Enqueue(next);
                    Assert.True(parent.ContainsKey(grant.Node));
                    var path = new List<ushort>();
                    for (ushort node = grant.Node; node != definition.StartNode; node = parent[node]) path.Add(node);
                    path.Reverse();
                    var progress = new AscendancyProgress(definition.Identity, path, AscendancyRules.AwardKeys);
                    Assert.Equal(definition, new AscendancyRules(catalog).Validate(definition.BaseClass, progress));
                    byte row = checked((byte)Program.Skills.RowOfClass(definition.BaseClass));
                    PlayerInventories.SetClass(character, row);
                    CharacterSaves.Write(character, new(new Dictionary<byte, List<CharacterSaves.SavedPlacement>>(),
                        Experience: CharacterStats.ExperienceFor(100), MainAscendancy: progress));
                    var policy = (IAscendancyFamilyPolicy)Activator.CreateInstance(
                        typeof(GameServer.Modules.PhreciaAscendancies.PhreciaAscendanciesModule).Assembly.GetType(
                            "GameServer.Modules.PhreciaAscendancies.PhreciaFamilyPolicy", throwOnError: true)!, nonPublic: true)!;
                    using var registration = AscendancyPolicies.Register(provider, policy);
                    var schedule = new CatClockSchedule { At = 300 };
                    var updates = new PlayerUpdatesState(lifetime, schedule);
                    Game.AreaUpdateContext.Value = updates;
                    self = new ClientState(conn.Layout, 0xC480, 1)
                    {
                        Character = character, CharacterId = Guid.NewGuid(), Account = "footstep-bite-account",
                        League = "Standard", LeagueForm = "Standard", ClassRow = row, MainAscendancy = progress,
                        Instance = new(), PlayerSent = true, PlayerObjectCreated = true, PlayerUpdatesState = updates,
                        Experience = CharacterStats.ExperienceFor(100), Wire = ("footstep-bite", footWire.Stream, footWire.Ciphers),
                        Pools = new(100, 1, 1, 0, 0),
                        LeagueModule = new(new("footstep-bite", "Standard", false, false),
                            new HashSet<string>(["Game", "PhreciaAscendancies"], StringComparer.OrdinalIgnoreCase)),
                    };
                    Assert.Same(provider, AscendancyPolicies.SourceOwner(self));
                    Assert.True(policy.Supports(definition.Identity));
                    var inventory = Game.ItemsOf(self);
                    var bite = new UniqueCatalog(Program.Items).Create("Farrul's Bite", new Random(504))!;
                    var helmet = inventory.Add(PlayerInventories.Helmet, 0, 0, bite);
                    inventory.Add(5, 0, 0, new UniqueCatalog(Program.Items).Create("Astramentis", new Random(504))!);
                    inventory.Add(6, 0, 0, new UniqueCatalog(Program.Items).Create("Le Heup of All", new Random(504))!);
                    inventory.Add(7, 0, 0, new UniqueCatalog(Program.Items).Create("Le Heup of All", new Random(505))!);
                    self.Pools = Game.PoolsFor(self, 100); self.PlayerLife = self.Pools.Life; self.PlayerMana = self.Pools.Mana;
                    Assert.False(Game.IsInactive(self, helmet.Id));
                    Assert.Empty(self.PlayerStats);
                    void Set(string name, int value)
                    {
                        uint key = Program.Stats.Key(name) ?? throw new InvalidOperationException("Missing canonical stat " + name);
                        self.PlayerStats[key] = value;
                    }
                    foreach (string name in new[] { "spell_damage_+%", "fire_damage_+%", "chaos_damage_+%", "elemental_damage_+%",
                        "area_damage_+%", "damage_over_time_+%", "fire_damage_over_time_+%", "burn_damage_+%", "ignite_damage_+%",
                        "base_poison_damage_+%", "base_ailment_damage_+%", "base_extra_damage_rolls",
                        "base_chance_to_deal_triple_damage_%", "chance_to_deal_double_damage_%" })
                        if (Program.Stats.Key(name) is uint key) self.PlayerStats[key] = 0;
                    Set("damage_+%", 100); Set("global_cannot_crit", 1);
                    Set("spell_minimum_added_fire_damage", 10_000); Set("spell_maximum_added_fire_damage", 10_000);
                    Set("spell_minimum_added_chaos_damage", 10_000); Set("spell_maximum_added_chaos_damage", 10_000);
                    Set("base_chance_to_ignite_%", 100); Set("base_chance_to_poison_on_hit_%", 100);
                    Set("global_chance_to_blind_on_hit_%", 100);
                    runtime = new Game.ProjectileSession("footstep-bite", footWire.Stream, footWire.Ciphers, self);
                    self.ProjectileRuntimeState = runtime;
                    runtime.Runtime.AdvanceTo(ProjectileRuntime.Time(300));
                    Assert.Equal(300, Game.ProjectileWorldTime(self), 9);
                    Assert.Equal(300, Game.ResourceEffectsTime(self), 9);
                    Assert.Contains(grant, Game.FootstepSkills(self, 300));
                    Assert.DoesNotContain(Game.GemSkillEffects(self), g => Game.SkillsFor(self).EffectId(g.Effect) == grant.EffectId);
                    DamagePacket raw;
                    using (new Game.PlayerStatScope(self, fresh: true))
                    {
                        var player = Game.CharacterPlayerStats(self);
                        Assert.Equal(40, player.GetValueOrDefault(Program.Stats.Key(PlayerHitDamage.VsBlindedDamageStat)!.Value));
                        Assert.Equal(100, player.GetValueOrDefault(Program.Stats.Key("damage_+%")!.Value));
                        var own = Game.SkillsFor(self).EffectStats(grant.Effect, grant.Level).ToDictionary(x => x.Item1, x => x.Item2);
                        int Get(string n) => Program.Stats.Key(n) is uint k ? player.GetValueOrDefault(k) + own.GetValueOrDefault(k) : 0;
                        var plan = SkillHitPlan.ForSkill(Game.SkillsFor(self), grant.Effect, grant.Level);
                        Assert.Equal(SkillHitKind.Spell, plan.Kind);
                        raw = DamageStats.RawAdded(plan.SpellBase, Get, spell: true,
                            effectiveness: Game.AddedEffectiveness(self, grant.Effect, grant.Level));
                    }
                    (int Min, int Max) Bounds(double factor) => (raw.Components.Sum(c => (int)Math.Floor(c.Range.Min * factor)),
                        raw.Components.Sum(c => (int)Math.Floor(c.Range.Max * factor)));
                    var normal = Bounds(2); var blinded = Bounds(2.4);
                    Assert.True(blinded.Min > normal.Max);
                    var at = self.Walk.Position(300);
                    SpawnedMonster Target(uint id, string label)
                    {
                        var m = new SpawnedMonster(label, new(at.X, at.Y, 0), 0, 0, 1_000_000)
                            { Hostile = true, Level = 100, Life = 1_000_000 };
                        self.Monsters[id] = m; return m;
                    }
                    var control = Target(0xC48001, "footstep-unblinded");
                    var positive = Target(0xC48002, "footstep-owned-blind");
                    var neighbor = Target(0xC48003, "footstep-unblinded-neighbor");
                    Assert.True(Game.InflictMonsterBlind(self, 0xC48002, positive, 300, (100, 0, 4), new Random(1)));
                    await Game.TriggerFootstepSkill("footstep-bite", footWire.Stream, footWire.Ciphers, self, grant);
                    Assert.InRange(checked((int)(1_000_000 - control.Life)), normal.Min, normal.Max);
                    Assert.InRange(checked((int)(1_000_000 - neighbor.Life)), normal.Min, normal.Max);
                    int damage = checked((int)(1_000_000 - positive.Life));
                    Assert.True(damage >= blinded.Min && damage <= blinded.Max,
                        $"Actual legal {grant.EffectId} callback source Bite40/generic100: unblind {normal}, "
                        + $"additive Blind {blinded}, actual {damage}; connected class/source {definition.Name}/{grant.Node}");
                    Assert.True(Game.Blinded(self, 0xC48001));
                    double normalPoison = .3 * raw[DamageType.Chaos].Min * 2;
                    Assert.Equal(normalPoison, control.Poisons.TotalDps(300), 6);
                    Assert.Equal(normalPoison * 1.2, positive.Poisons.TotalDps(300), 6);
                    Assert.InRange(control.Ignites.PerSecond(300), .9 * raw[DamageType.Fire].Min * 2 - 1d / 30,
                        .9 * raw[DamageType.Fire].Max * 2 + 1d / 30);
                    Assert.InRange(positive.Ignites.PerSecond(300), .9 * raw[DamageType.Fire].Min * 2.4 - 1d / 30,
                        .9 * raw[DamageType.Fire].Max * 2.4 + 1d / 30);
                    Assert.False(control.Bleeds.Burning);
                    double oldPoison = positive.Poisons.TotalDps(300), oldPoisonUntil = positive.Poisons.Until;
                    double oldIgnite = positive.Ignites.PerSecond(300), oldIgniteUntil = positive.Ignites.Until;
                    self.Monsters.Clear();
                    Assert.NotNull(inventory.Remove(PlayerInventories.Helmet, helmet.Id));
                    var removed = Target(0xC48004, "footstep-source-off-owned-blind");
                    Assert.True(Game.InflictMonsterBlind(self, 0xC48004, removed, 300, (100, 0, 4), new Random(1)));
                    await Game.TriggerFootstepSkill("footstep-bite-source-off", footWire.Stream, footWire.Ciphers, self, grant);
                    Assert.InRange(checked((int)(1_000_000 - removed.Life)), normal.Min, normal.Max);
                    Assert.Equal(normalPoison, removed.Poisons.TotalDps(300), 6);
                    Assert.Equal(oldPoison, positive.Poisons.TotalDps(300), 6); Assert.Equal(oldPoisonUntil, positive.Poisons.Until);
                    Assert.Equal(oldIgnite, positive.Ignites.PerSecond(300), 6); Assert.Equal(oldIgniteUntil, positive.Ignites.Until);
                    Set("base_deal_no_damage", 1);
                    self.Monsters.Clear(); var prohibited = Target(0xC48005, "footstep-prohibited");
                    await Game.TriggerFootstepSkill("footstep-bite-prohibited", footWire.Stream, footWire.Ciphers, self, grant);
                    Assert.Equal(1_000_000u, prohibited.Life); Assert.False(prohibited.Ignites.Burning); Assert.False(prohibited.Poisons.Poisoned);
                    Set("base_deal_no_damage", 0);
                    self.MainAscendancy = progress with { Nodes = path.Where(n => n != grant.Node).ToArray() };
                    Assert.DoesNotContain(grant, Game.FootstepSkills(self, 300));
                    await Game.TriggerFootstepSkill("footstep-bite-retired", footWire.Stream, footWire.Ciphers, self, grant);
                    Assert.Equal(1_000_000u, prohibited.Life);
                    Console.WriteLine($"#504 actual connected Polytheist {grant.EffectId} callback: damage={damage}, "
                        + $"normal={normal}, blinded={blinded}, normalPoison={normalPoison}; source-off/prohibition/retirement passed");
                }
                finally
                {
                    if (self is not null) { self.Closed = true; self.ProjectileRuntimeState = null; }
                    runtime?.Invalidate(); Game.AreaUpdateContext.Value = oldContext;
                    Environment.SetEnvironmentVariable(optIn, previousOptIn); PlayerInventories.Forget(character);
                    provider.Stop(_ => { });
                }
            }

            async Task ReplicaFurFixture()
            {
                string character = "ReplicaFurRuntime" + Guid.NewGuid().ToString("N");
                const byte witchRow = 1;
                PlayerInventories.SetClass(character, witchRow);
                CharacterSaves.Write(character, new(new Dictionary<byte, List<CharacterSaves.SavedPlacement>>(),
                    Experience: CharacterStats.ExperienceFor(100)));
                var schedule = new CatClockSchedule { At = 200 };
                var updates = new PlayerUpdatesState(lifetime, schedule);
                var previousContext = Game.AreaUpdateContext.Value;
                Game.AreaUpdateContext.Value = updates;
                using var catWire = new Wire();
                ClientState? catConnection = null;
                ClientState? restoredConnection = null;
                try
                {
                    catConnection = new ClientState(conn.Layout, 0xC480, 1)
                    {
                        Character = character, CharacterId = Guid.NewGuid(), Account = "replica-fur-account",
                        League = "Standard", LeagueForm = "Standard", ClassRow = witchRow, Instance = new(),
                        PlayerSent = true, PlayerObjectCreated = true, PlayerUpdatesState = updates,
                        Experience = CharacterStats.ExperienceFor(100), LeagueModule = conn.LeagueModule,
                        Wire = ("replica-fur", catWire.Stream, catWire.Ciphers), Pools = new(100, 1, 1, 0, 0),
                    };
                    var catConn = catConnection;
                    var inventory = Game.ItemsOf(catConn);
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
                    inventory.Add(5, 0, 0, new UniqueCatalog(Program.Items).Create("Astramentis", new Random(504))!);
                    inventory.Add(6, 0, 0, new UniqueCatalog(Program.Items).Create("Le Heup of All", new Random(504))!);
                    inventory.Add(7, 0, 0, new UniqueCatalog(Program.Items).Create("Le Heup of All", new Random(505))!);
                    inventory.Add(PlayerInventories.Boots, 0, 0,
                        new UniqueCatalog(Program.Items).Create("Darkray Vectors", new Random(504))!);
                    var bite = new UniqueCatalog(Program.Items).Create("Farrul's Bite", new Random(504))!;
                    var bitePlacement = inventory.Add(PlayerInventories.Helmet, 0, 0, bite);
                    var replica = new UniqueCatalog(Program.Items).Create("Replica Farrul's Fur", new Random(504))!;
                    var replicaMods = replica.Mods.Select(mod => Program.Items.Mods.ByHash(mod.Hash)!.Id).ToArray();
                    Assert.Contains("CatsAgilityDurationUnique__1", replicaMods);
                    Assert.DoesNotContain("CatsStealthDurationUnique__1_", replicaMods);
                    var replicaPlacement = inventory.Add(PlayerInventories.BodyArmour, 0, 0, replica);
                    catConn.Pools = Game.PoolsFor(catConn, 100);
                    catConn.PlayerLife = catConn.Pools.Life;
                    catConn.PlayerMana = catConn.Pools.Mana;
                    Game.ReconcileOwnAuras(catConn, validateSkills: true);
                    var catSource = Assert.Single(Game.GemSkillEffects(catConn), gem =>
                        Game.SkillsFor(catConn).EffectId(gem.Effect) == "AspectOfTheCat");
                    var catGrant = Assert.Single(Game.WornGrantedSkillsOf(catConn),
                        grant => grant.EffectId == "AspectOfTheCat");
                    var catCost = Game.SupportedReservationCost(catConn, catSource.Id)!.Value;
                    Assert.Equal(0, catCost.ManaPercent);
                    Assert.Equal(0, catCost.ManaFlat);

                    var state = catConn.Reservations;
                    int Stat(IReadOnlyDictionary<uint, int> stats, string name) =>
                        stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
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
                    static bool TryCompactUInt(ReadOnlySpan<byte> bytes, ref int at, out uint value)
                    {
                        value = 0;
                        if (at >= bytes.Length) return false;
                        uint first = bytes[at++];
                        if (first < 0x80) { value = first; return true; }
                        int following = first < 0xc0 ? 1 : first < 0xe0 ? 2 : first < 0xf0 ? 3 : 4;
                        if (bytes.Length - at < following) return false;
                        if (following == 1) value = (first & 0x3f) << 8 | bytes[at++];
                        else if (following == 2) value = (first & 0x1f) << 16 | (uint)bytes[at++] << 8 | bytes[at++];
                        else if (following == 3) value = (first & 0x0f) << 24 | (uint)bytes[at++] << 16
                            | (uint)bytes[at++] << 8 | bytes[at++];
                        else value = (uint)bytes[at++] << 24 | (uint)bytes[at++] << 16
                            | (uint)bytes[at++] << 8 | bytes[at++];
                        return true;
                    }
                    static bool TryCompactInt(ReadOnlySpan<byte> bytes, ref int at, out int value)
                    {
                        static int Signed(uint value, int bits) => (value & (1u << (bits - 1))) != 0
                            ? (int)(value | ~((1u << bits) - 1)) : (int)value;
                        value = 0;
                        if (at >= bytes.Length) return false;
                        uint first = bytes[at++];
                        if (first < 0x80) { value = Signed(first & 0x7f, 7); return true; }
                        int following = first < 0xc0 ? 1 : first < 0xe0 ? 2 : first < 0xf0 ? 3 : 4;
                        if (bytes.Length - at < following) return false;
                        if (following == 1) value = Signed((first & 0x3f) << 8 | bytes[at++], 14);
                        else if (following == 2) value = Signed((first & 0x1f) << 16 | (uint)bytes[at++] << 8
                            | bytes[at++], 21);
                        else if (following == 3) value = Signed((first & 0x0f) << 24 | (uint)bytes[at++] << 16
                            | (uint)bytes[at++] << 8 | bytes[at++], 28);
                        else value = (int)((uint)bytes[at++] << 24 | (uint)bytes[at++] << 16
                            | (uint)bytes[at++] << 8 | bytes[at++]);
                        return true;
                    }
                    static IEnumerable<Dictionary<uint, int>> PublishedStats(IEnumerable<byte[]> packets)
                    {
                        var header = InstanceEntities.StatsUpdate(Program.PlayerId, []).AsSpan(0, 12).ToArray();
                        foreach (var packet in packets)
                        {
                            int scan = 0;
                            while (scan <= packet.Length - header.Length)
                            {
                                int found = packet.AsSpan(scan).IndexOf(header);
                                if (found < 0) break;
                                int start = scan + found;
                                int cursor = start + header.Length;
                                if (!TryCompactUInt(packet, ref cursor, out uint count) || count > 10_000)
                                {
                                    scan = start + 1;
                                    continue;
                                }
                                var stats = new Dictionary<uint, int>();
                                bool complete = true;
                                for (uint i = 0; i < count; i++)
                                {
                                    if (!TryCompactUInt(packet, ref cursor, out uint key)
                                        || !TryCompactInt(packet, ref cursor, out int value)
                                        || !stats.TryAdd(key, value))
                                    {
                                        complete = false;
                                        break;
                                    }
                                }
                                if (!complete)
                                {
                                    scan = start + 1;
                                    continue;
                                }
                                yield return stats;
                                scan = cursor;
                            }
                        }
                    }
                    static void AssertPublished(IEnumerable<byte[]> packets,
                        params (string Name, int Value)[] expected)
                    {
                        var snapshots = PublishedStats(packets).ToArray();
                        Assert.NotEmpty(snapshots);
                        var keys = expected.Select(row => (Key: Program.Stats.Key(row.Name)!.Value, row.Name, row.Value)).ToArray();
                        Assert.True(snapshots.Any(snapshot => keys.All(row => snapshot.ContainsKey(row.Key))),
                            "0x1b3 has no full snapshot with all selected stats");
                        foreach (var snapshot in snapshots)
                            foreach (var (key, name, value) in keys)
                                if (snapshot.TryGetValue(key, out int actual))
                                    Assert.True(actual == value,
                                        $"0x1b3 published stat {key} ({name}) as {actual}, expected {value}");
                    }
                    byte[] PhasePacket(ushort instance, double remaining) => InstanceEntities.AddBuff(Program.PlayerId,
                        instance, Game.CatAspectPhaseRow(state.Cat), Program.PlayerId, state.CatOwner!.Value.Skill,
                        u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, remaining)));
                    byte[] OnslaughtPacket(ushort instance, double remaining) => InstanceEntities.AddBuff(Program.PlayerId,
                        instance, Program.Items.Onslaught!.BuffRow, Program.PlayerId,
                        u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, remaining)));

                    List<byte[]> initial;
                    using (new Game.PlayerStatScope(catConn, fresh: true))
                    {
                        var primedScope = Game.BuildScopeOf(catConn)!;
                        _ = Game.FinalPlayerStats(catConn).ToList();
                        var primedCharacter = primedScope.Stats;
                        Assert.Equal(0, Stat(primedCharacter, "current_frenzy_charges"));
                        Assert.Equal(0, Stat(primedCharacter, "current_endurance_charges"));
                        Assert.Equal(0, Stat(primedCharacter, "currently_has_onslaught"));
                        initial = await Capture(async () => Assert.True(await Game.ToggleAura("test", catWire.Stream,
                            catWire.Ciphers, catConn, catSource.Id, catSource.Effect, catSource.Name, catCost,
                            catGrant.Level, 0)));
                        var restoredScope = Game.BuildScopeOf(catConn);
                        Assert.Same(primedScope, restoredScope);
                    }
                    AssertPublished(initial, ("has_cat_aspect", 1), ("has_cats_stealth", 1),
                        ("has_cats_agility", 0), ("currently_has_onslaught", 0),
                        ("current_frenzy_charges", 0), ("current_endurance_charges", 0),
                        ("current_power_charges", 0));
                    Assert.Equal(4, state.CatPhaseDeadline - state.CatPhaseStartedAt, 3);
                    Assert.Equal(double.NegativeInfinity, state.CatLastAgilityStartedAt);
                    Assert.Equal(0, state.CatOnslaughtBuff);
                    Assert.False(Game.HasOnslaught(catConn));
                    Assert.False(Game.CatOnslaughtActiveLocked(catConn));

                    var chargeSession = Game.ChargesOf(catConn);
                    Assert.Equal(0, chargeSession.Effective(ChargeType.Frenzy));
                    Assert.Equal(0, chargeSession.Effective(ChargeType.Endurance));
                    int attackMsStealth = (int)Math.Round(Game.SupportedAttackSeconds(catConn,
                        Assert.Single(Game.GemSkillEffects(catConn),
                            gem => Game.SkillsFor(catConn).EffectId(gem.Effect) == "TornadoShot").Id) * 1000);
                    var fireball = Assert.Single(Game.GemSkillEffects(catConn),
                        gem => Game.SkillsFor(catConn).EffectId(gem.Effect) == "Fireball").Id;
                    int castMsStealth = Game.CastMs(catConn,
                        Game.SupportedSkill(catConn, fireball).Effect, fireball)!.Value;
                    int movementStealth = catConn.Pools.MovementVelocityPct;
                    uint onslaughtEffectKey = Program.Stats.Key("onslaught_effect_+%")!.Value;
                    catConn.PlayerStats[onslaughtEffectKey] = 50;

                    List<byte[]> agilityEntry;
                    using (new Game.PlayerStatScope(catConn, fresh: true))
                    {
                        var primedScope = Game.BuildScopeOf(catConn)!;
                        _ = Game.FinalPlayerStats(catConn).ToList();
                        var primedCharacter = new Dictionary<uint, int>(primedScope.Stats);
                        var primedFinal = primedScope.Final!.ToArray();
                        Assert.Equal(1, Stat(primedCharacter, "has_cat_aspect"));
                        Assert.Equal(1, Stat(primedCharacter, "has_cats_stealth"));
                        Assert.Equal(0, Stat(primedCharacter, "has_cats_agility"));
                        Assert.Equal(0, Stat(primedCharacter, "current_frenzy_charges"));
                        Assert.Equal(0, Stat(primedCharacter, "current_endurance_charges"));
                        Assert.Equal(0, Stat(primedCharacter, "currently_has_onslaught"));
                        schedule.At = state.CatPhaseDeadline;
                        agilityEntry = await Capture(() => Game.ReservationSkillsTick("test", catWire.Stream,
                            catWire.Ciphers, catConn, schedule.At));
                        var restoredScope = Game.BuildScopeOf(catConn);
                        Assert.Same(primedScope, restoredScope);
                        Assert.Equal(primedCharacter, restoredScope!.Stats);
                        Assert.Equal(primedFinal, restoredScope.Final);
                    }
                    AssertPublished(agilityEntry, ("has_cat_aspect", 1), ("has_cats_stealth", 0),
                        ("has_cats_agility", 1), ("currently_has_onslaught", 1),
                        ("current_frenzy_charges", 4), ("current_endurance_charges", 3),
                        ("current_power_charges", 0));
                    Assert.Equal(ReservationState.CatPhase.Agility, state.Cat);
                    Assert.Equal(8, state.CatPhaseDeadline - state.CatPhaseStartedAt, 3);
                    Assert.Equal(schedule.At, state.CatLastAgilityStartedAt, 3);
                    Assert.Equal(schedule.At, state.CatObservedAgilityEntryAt, 3);
                    Assert.Equal(1, Occurrences(agilityEntry, PhasePacket(state.CatPhaseBuff, 8)));
                    var actorHeader = InstanceEntities.PlayerSkillsChange(Program.PlayerId, [], []).AsSpan(0, 12).ToArray();
                    Assert.Contains(agilityEntry, packet => packet.AsSpan().IndexOf(actorHeader) >= 0);
                    Assert.NotEqual(0, state.CatOnslaughtBuff);
                    var ownedOnslaught = OnslaughtPacket(state.CatOnslaughtBuff, 8);
                    Assert.Equal(31, ownedOnslaught.Length);
                    Assert.Equal(new byte[4], ownedOnslaught[^4..]);
                    Assert.Equal(1, Occurrences(agilityEntry, ownedOnslaught));
                    chargeSession.Refresh(schedule.At);
                    Assert.Equal(4, chargeSession.Limits(ChargeType.Frenzy).Maximum);
                    Assert.Equal(3, chargeSession.Limits(ChargeType.Endurance).Maximum);
                    Assert.Equal(4, chargeSession.Effective(ChargeType.Frenzy));
                    Assert.Equal(3, chargeSession.Effective(ChargeType.Endurance));
                    Assert.Equal(0, chargeSession.Effective(ChargeType.Power));
                    Assert.False(chargeSession.Converted(ChargeType.Frenzy));
                    Assert.False(chargeSession.Converted(ChargeType.Endurance));
                    double frenzyExpiry = chargeSession.State.ExpiresAt(ChargeType.Frenzy);
                    double enduranceExpiry = chargeSession.State.ExpiresAt(ChargeType.Endurance);
                    Assert.Equal(schedule.At + chargeSession.Limits(ChargeType.Frenzy).DurationMs / 1000.0,
                        frenzyExpiry, 3);
                    Assert.Equal(schedule.At + chargeSession.Limits(ChargeType.Endurance).DurationMs / 1000.0,
                        enduranceExpiry, 3);
                    uint agilityDurationKey = Program.Stats.Key("aspect_of_the_cat_base_secondary_duration")!.Value;
                    double repricedPhaseStart = state.CatPhaseStartedAt;
                    ushort repricedOldOnslaught = state.PresentedCatOnslaughtBuff;
                    catConn.PlayerStats[agilityDurationKey] = 4000;
                    schedule.At += .25;
                    var durationRepriced = await Capture(() => Game.ReservationSkillsTick("test", catWire.Stream,
                        catWire.Ciphers, catConn, schedule.At));
                    chargeSession.Refresh(schedule.At);
                    Assert.Equal(ReservationState.CatPhase.Agility, state.Cat);
                    Assert.Equal(repricedPhaseStart, state.CatPhaseStartedAt, 3);
                    Assert.Equal(repricedPhaseStart + 10, state.CatPhaseDeadline, 3);
                    Assert.Equal(frenzyExpiry, chargeSession.State.ExpiresAt(ChargeType.Frenzy), 3);
                    Assert.Equal(enduranceExpiry, chargeSession.State.ExpiresAt(ChargeType.Endurance), 3);
                    Assert.NotEqual(repricedOldOnslaught, state.PresentedCatOnslaughtBuff);
                    Assert.Equal(1, Occurrences(durationRepriced,
                        InstanceEntities.RemoveBuff(Program.PlayerId, repricedOldOnslaught)));
                    var repricedOnslaught = OnslaughtPacket(state.PresentedCatOnslaughtBuff,
                        state.CatPhaseDeadline - schedule.At);
                    Assert.Equal(31, repricedOnslaught.Length);
                    Assert.Equal(new byte[4], repricedOnslaught[^4..]);
                    Assert.Equal(1, Occurrences(durationRepriced, repricedOnslaught));

                    ushort restoredOldOnslaught = state.PresentedCatOnslaughtBuff;
                    catConn.PlayerStats.Remove(agilityDurationKey);
                    schedule.At += .25;
                    var durationRestored = await Capture(() => Game.ReservationSkillsTick("test", catWire.Stream,
                        catWire.Ciphers, catConn, schedule.At));
                    chargeSession.Refresh(schedule.At);
                    Assert.Equal(ReservationState.CatPhase.Agility, state.Cat);
                    Assert.Equal(repricedPhaseStart, state.CatPhaseStartedAt, 3);
                    Assert.Equal(repricedPhaseStart + 8, state.CatPhaseDeadline, 3);
                    Assert.Equal(frenzyExpiry, chargeSession.State.ExpiresAt(ChargeType.Frenzy), 3);
                    Assert.Equal(enduranceExpiry, chargeSession.State.ExpiresAt(ChargeType.Endurance), 3);
                    Assert.NotEqual(restoredOldOnslaught, state.PresentedCatOnslaughtBuff);
                    Assert.Equal(1, Occurrences(durationRestored,
                        InstanceEntities.RemoveBuff(Program.PlayerId, restoredOldOnslaught)));
                    var restoredOnslaught = OnslaughtPacket(state.PresentedCatOnslaughtBuff,
                        state.CatPhaseDeadline - schedule.At);
                    Assert.Equal(31, restoredOnslaught.Length);
                    Assert.Equal(new byte[4], restoredOnslaught[^4..]);
                    Assert.Equal(1, Occurrences(durationRestored, restoredOnslaught));

                    var agilityStats = Game.CharacterPlayerStats(catConn);
                    Assert.Equal(4, Stat(agilityStats, "current_frenzy_charges"));
                    Assert.Equal(3, Stat(agilityStats, "current_endurance_charges"));
                    Assert.Equal(0, Stat(agilityStats, "current_power_charges"));
                    Assert.Equal(1, Stat(agilityStats, "currently_has_onslaught"));
                    Assert.Equal(1.16, ChargeEffects.DamageMore(name => Stat(agilityStats, name)), 3);
                    Assert.Equal((12, 12), Game.PlayerDamageReduction(catConn, agilityStats));
                    var utilityOnslaught = Game.UtilityStats(catConn, name => Stat(agilityStats, name))
                        .Where(row => row.Stat is "attack_speed_+%" or "base_cast_speed_+%"
                            or "base_movement_velocity_+%").ToArray();
                    Assert.Equal(3, utilityOnslaught.Length);
                    Assert.All(utilityOnslaught, row => Assert.Equal(30, row.Value));
                    Assert.True(catConn.Pools.MovementVelocityPct >= movementStealth + 30);
                    Assert.True((int)Math.Round(Game.SupportedAttackSeconds(catConn,
                        Assert.Single(Game.GemSkillEffects(catConn),
                            gem => Game.SkillsFor(catConn).EffectId(gem.Effect) == "TornadoShot").Id) * 1000)
                        < attackMsStealth);
                    Assert.True(Game.CastMs(catConn,
                        Game.SupportedSkill(catConn, fireball).Effect, fireball)!.Value < castMsStealth);
                    var flags = Game.ItemConditionFlags(catConn, catConn.Clock.Elapsed.TotalSeconds);
                    Assert.True(flags["_while_onslaught_is_active"]);
                    Assert.True(flags["_while_have_onslaught"]);

                    double agilityStart = state.CatPhaseStartedAt;
                    double agilityDeadline = state.CatPhaseDeadline;
                    double lastStealth = state.CatLastStealthStartedAt;
                    uint stealthDurationKey = Program.Stats.Key("cats_stealth_duration_ms_+")!.Value;
                    catConn.PlayerStats[stealthDurationKey] = -2000;
                    schedule.At += .25;
                    await Game.ReservationSkillsTick("test", catWire.Stream, catWire.Ciphers, catConn, schedule.At);
                    chargeSession.Refresh(schedule.At);
                    Assert.Equal(ReservationState.CatPhase.Agility, state.Cat);
                    Assert.Equal(agilityStart, state.CatPhaseStartedAt, 3);
                    Assert.Equal(agilityDeadline, state.CatPhaseDeadline, 3);
                    Assert.Equal(lastStealth, state.CatLastStealthStartedAt, 3);
                    Assert.Equal(frenzyExpiry, chargeSession.State.ExpiresAt(ChargeType.Frenzy), 3);
                    Assert.Equal(enduranceExpiry, chargeSession.State.ExpiresAt(ChargeType.Endurance), 3);
                    catConn.PlayerStats.Remove(stealthDurationKey);

                    var kill = Game.KillOnslaughtOf(catConn);
                    kill.Until = kill.SentUntil = schedule.At + 5;
                    var catSources = Game.CatOnslaughtSources(catConn, name => Stat(agilityStats, name)).ToList();
                    var killSources = Game.KillOnslaughtSources(catConn, name => Stat(agilityStats, name)).ToList();
                    var silverSources = UtilityFlasks.OnslaughtSources(Program.Items.Onslaught!, 1.8).ToList();
                    var strongest = UtilityFlasks.Strongest(catSources.Concat(killSources).Concat(silverSources));
                    Assert.Equal(3, strongest.Count);
                    Assert.All(strongest, row => Assert.Equal(36, row.Value));
                    Assert.True(Game.HasOnslaught(catConn));

                    ushort presentedOnslaught = state.PresentedCatOnslaughtBuff;
                    inventory.Remove(PlayerInventories.BodyArmour, replicaPlacement.Id);
                    var replicaRemoved = await Capture(() => Game.SendOwnAuraChanges("test", catWire.Stream,
                        catWire.Ciphers, catConn));
                    Assert.Equal(1, Occurrences(replicaRemoved,
                        InstanceEntities.RemoveBuff(Program.PlayerId, presentedOnslaught)));
                    Assert.Equal(0, state.PresentedCatOnslaughtBuff);
                    Assert.True(Game.HasOnslaught(catConn));
                    Assert.Equal(1, Stat(Game.CharacterPlayerStats(catConn), "currently_has_onslaught"));
                    var foreignFlags = Game.ItemConditionFlags(catConn, catConn.Clock.Elapsed.TotalSeconds);
                    Assert.True(foreignFlags["_while_onslaught_is_active"]);
                    Assert.True(foreignFlags["_while_have_onslaught"]);
                    Assert.Equal(4, chargeSession.Effective(ChargeType.Frenzy));
                    Assert.Equal(3, chargeSession.Effective(ChargeType.Endurance));
                    Assert.Equal(frenzyExpiry, chargeSession.State.ExpiresAt(ChargeType.Frenzy), 3);
                    Assert.Equal(enduranceExpiry, chargeSession.State.ExpiresAt(ChargeType.Endurance), 3);

                    replicaPlacement = inventory.Add(PlayerInventories.BodyArmour, 0, 0, replica);
                    var replicaReturned = await Capture(() => Game.SendOwnAuraChanges("test", catWire.Stream,
                        catWire.Ciphers, catConn));
                    Assert.NotEqual(0, state.CatOnslaughtBuff);
                    Assert.Equal(1, Occurrences(replicaReturned, OnslaughtPacket(state.CatOnslaughtBuff,
                        state.CatPhaseDeadline - schedule.At)));
                    Assert.Equal(frenzyExpiry, chargeSession.State.ExpiresAt(ChargeType.Frenzy), 3);
                    Assert.Equal(enduranceExpiry, chargeSession.State.ExpiresAt(ChargeType.Endurance), 3);
                    kill.Until = kill.SentUntil = double.NegativeInfinity;
                    Assert.True(Game.HasOnslaught(catConn));
                    Assert.Equal(1, Stat(Game.CharacterPlayerStats(catConn), "currently_has_onslaught"));

                    ushort agilityOnslaught = state.PresentedCatOnslaughtBuff;
                    schedule.At = state.CatPhaseDeadline;
                    var nextStealth = await Capture(() => Game.ReservationSkillsTick("test", catWire.Stream,
                        catWire.Ciphers, catConn, schedule.At));
                    Assert.Equal(ReservationState.CatPhase.Stealth, state.Cat);
                    Assert.Equal(4, chargeSession.Effective(ChargeType.Frenzy));
                    Assert.Equal(3, chargeSession.Effective(ChargeType.Endurance));
                    Assert.Equal(frenzyExpiry, chargeSession.State.ExpiresAt(ChargeType.Frenzy), 3);
                    Assert.Equal(enduranceExpiry, chargeSession.State.ExpiresAt(ChargeType.Endurance), 3);
                    Assert.Equal(1, Occurrences(nextStealth,
                        InstanceEntities.RemoveBuff(Program.PlayerId, agilityOnslaught)));

                    schedule.At = state.CatPhaseDeadline;
                    await Game.ReservationSkillsTick("test", catWire.Stream, catWire.Ciphers, catConn, schedule.At);
                    chargeSession.Refresh(schedule.At);
                    Assert.Equal(ReservationState.CatPhase.Agility, state.Cat);
                    Assert.Equal(4, chargeSession.State.Held(ChargeType.Frenzy));
                    Assert.Equal(3, chargeSession.State.Held(ChargeType.Endurance));
                    Assert.Equal(schedule.At + chargeSession.Limits(ChargeType.Frenzy).DurationMs / 1000.0,
                        chargeSession.State.ExpiresAt(ChargeType.Frenzy), 3);

                    schedule.At = 235;
                    chargeSession.Refresh(schedule.At);
                    var catchupSources = Game.CharacterStatSources(catConn).Stats;
                    int ChargeStat(string name) => Program.Stats.Key(name) is uint key
                        ? catConn.PlayerStats.GetValueOrDefault(key, catchupSources.GetValueOrDefault(key)) : 0;
                    chargeSession.Observe(ChargeStat);
                    _ = chargeSession.Admit(235,
                        [new ChargeEvent.Grant(ChargeType.Frenzy, 1, "independent later gain")],
                        ChargeStat, () => Game.WeaponProfileFor(catConn), true, new Random(504));
                    double laterExpiry = chargeSession.State.ExpiresAt(ChargeType.Frenzy);
                    schedule.At = 239.5;
                    await Game.ReservationSkillsTick("test", catWire.Stream, catWire.Ciphers, catConn, schedule.At);
                    Assert.Equal(ReservationState.CatPhase.Stealth, state.Cat);
                    Assert.Equal(228, state.CatLastAgilityStartedAt, 3);
                    Assert.Equal(1, chargeSession.State.Held(ChargeType.Frenzy));
                    Assert.Equal(laterExpiry, chargeSession.State.ExpiresAt(ChargeType.Frenzy), 3);

                    double genuineAgilityAt = state.CatPhaseDeadline;
                    schedule.At = genuineAgilityAt;
                    await Game.ReservationSkillsTick("test", catWire.Stream, catWire.Ciphers, catConn, schedule.At);
                    Assert.Equal(ReservationState.CatPhase.Agility, state.Cat);
                    Assert.Equal(genuineAgilityAt, state.CatLastAgilityStartedAt, 3);
                    Assert.Equal(genuineAgilityAt + 8, state.CatPhaseDeadline, 3);
                    Assert.True(Game.HasOnslaught(catConn));
                    ushort longSkipOldOnslaught = state.PresentedCatOnslaughtBuff;
                    Assert.NotEqual(0, longSkipOldOnslaught);

                    schedule.At = genuineAgilityAt + 25;
                    var longSkipOnslaught = await Capture(() => Game.ReservationSkillsTick("test",
                        catWire.Stream, catWire.Ciphers, catConn, schedule.At));
                    Assert.Equal(ReservationState.CatPhase.Agility, state.Cat);
                    Assert.Equal(genuineAgilityAt + 24, state.CatLastAgilityStartedAt, 3);
                    Assert.Equal(genuineAgilityAt + 32, state.CatPhaseDeadline, 3);
                    Assert.Equal(7, state.CatPhaseDeadline - schedule.At, 3);
                    Assert.True(Game.HasOnslaught(catConn));
                    Assert.NotEqual(longSkipOldOnslaught, state.PresentedCatOnslaughtBuff);
                    Assert.Equal(1, Occurrences(longSkipOnslaught,
                        InstanceEntities.RemoveBuff(Program.PlayerId, longSkipOldOnslaught)));
                    var longSkipOwnedOnslaught = OnslaughtPacket(state.PresentedCatOnslaughtBuff, 7);
                    Assert.Equal(31, longSkipOwnedOnslaught.Length);
                    Assert.Equal(new byte[4], longSkipOwnedOnslaught[^4..]);
                    Assert.Equal(1, Occurrences(longSkipOnslaught, longSkipOwnedOnslaught));

                    Assert.True(await Game.ToggleAura("test", catWire.Stream, catWire.Ciphers, catConn, catSource.Id,
                        catSource.Effect, catSource.Name, catCost, catGrant.Level, 0));
                    chargeSession.Die();
                    inventory.Remove(PlayerInventories.BodyArmour, replicaPlacement.Id);
                    Assert.True(await Game.ToggleAura("test", catWire.Stream, catWire.Ciphers, catConn, catSource.Id,
                        catSource.Effect, catSource.Name, catCost, catGrant.Level, 0));
                    Assert.Equal(4, state.CatPhaseDeadline - state.CatPhaseStartedAt, 3);
                    Assert.False(Game.HasOnslaught(catConn));
                    Assert.Equal(0, chargeSession.Effective(ChargeType.Frenzy));
                    Assert.Equal(0, chargeSession.Effective(ChargeType.Endurance));

                    schedule.At = state.CatPhaseDeadline;
                    await Game.ReservationSkillsTick("test", catWire.Stream, catWire.Ciphers, catConn, schedule.At);
                    Assert.Equal(ReservationState.CatPhase.Agility, state.Cat);
                    Assert.Equal(6, state.CatPhaseDeadline - state.CatPhaseStartedAt, 3);
                    Assert.False(Game.HasOnslaught(catConn));
                    Assert.Equal(0, chargeSession.Effective(ChargeType.Frenzy));
                    Assert.Equal(0, chargeSession.Effective(ChargeType.Endurance));
                    double midAgilityStart = state.CatPhaseStartedAt;
                    replicaPlacement = inventory.Add(PlayerInventories.BodyArmour, 0, 0, replica);
                    var midEquip = await Capture(() => Game.SendOwnAuraChanges("test", catWire.Stream,
                        catWire.Ciphers, catConn));
                    Assert.Equal(midAgilityStart, state.CatPhaseStartedAt, 3);
                    Assert.Equal(8, state.CatPhaseDeadline - state.CatPhaseStartedAt, 3);
                    Assert.True(Game.HasOnslaught(catConn));
                    Assert.Equal(0, chargeSession.Effective(ChargeType.Frenzy));
                    Assert.Equal(0, chargeSession.Effective(ChargeType.Endurance));
                    Assert.Equal(1, Occurrences(midEquip, OnslaughtPacket(state.CatOnslaughtBuff,
                        state.CatPhaseDeadline - schedule.At)));

                    inventory.Remove(PlayerInventories.BodyArmour, replicaPlacement.Id);
                    Game.EnsureOwnAuraPools(catConn);
                    inventory.Add(PlayerInventories.BodyArmour, 0, 0, replica);
                    Game.EnsureOwnAuraPools(catConn);
                    Assert.Equal(0, chargeSession.Effective(ChargeType.Frenzy));
                    Assert.Equal(0, chargeSession.Effective(ChargeType.Endurance));
                    schedule.At = state.CatPhaseDeadline;
                    await Game.ReservationSkillsTick("test", catWire.Stream, catWire.Ciphers, catConn, schedule.At);
                    Assert.Equal(ReservationState.CatPhase.Stealth, state.Cat);
                    Assert.Equal(0, chargeSession.Effective(ChargeType.Frenzy));
                    Assert.Equal(0, chargeSession.Effective(ChargeType.Endurance));
                    schedule.At = state.CatPhaseDeadline;
                    await Game.ReservationSkillsTick("test", catWire.Stream, catWire.Ciphers, catConn, schedule.At);
                    chargeSession.Refresh(schedule.At);
                    Assert.Equal(ReservationState.CatPhase.Agility, state.Cat);
                    Assert.Equal(4, chargeSession.Effective(ChargeType.Frenzy));
                    Assert.Equal(3, chargeSession.Effective(ChargeType.Endurance));

                    var savedCatIntent = CharacterSaves.Load(character)!.ActiveAuras!.ToArray();
                    ushort finalOnslaught = state.PresentedCatOnslaughtBuff;
                    inventory.Remove(PlayerInventories.Helmet, bitePlacement.Id);
                    var sourceGone = await Capture(() => Game.SendOwnAuraChanges("test", catWire.Stream,
                        catWire.Ciphers, catConn));
                    Assert.Equal(ReservationState.CatPhase.None, state.Cat);
                    Assert.Equal(0, state.PresentedCatOnslaughtBuff);
                    Assert.Equal(1, Occurrences(sourceGone, InstanceEntities.RemoveBuff(Program.PlayerId, finalOnslaught)));
                    Assert.False(Game.HasOnslaught(catConn));
                    Assert.Equal(0, Stat(Game.CharacterPlayerStats(catConn), "currently_has_onslaught"));
                    Assert.Equal(4, chargeSession.Effective(ChargeType.Frenzy));
                    Assert.Equal(3, chargeSession.Effective(ChargeType.Endurance));

                    CharacterSaves.SetAuraActive(character, "AspectOfTheCat", false);
                    bitePlacement = inventory.Add(PlayerInventories.Helmet, 0, 0,
                        new UniqueCatalog(Program.Items).Create("Farrul's Bite", new Random(505))!);
                    Game.ReconcileOwnAuras(catConn, validateSkills: true);
                    Assert.DoesNotContain(catConn.Auras.Values, aura => aura.EffectId == "AspectOfTheCat");
                    Assert.Equal(ReservationState.CatPhase.None, state.Cat);

                    using var restoredWire = new Wire();
                    restoredConnection = new ClientState(conn.Layout, 0xC481, 1)
                    {
                        Character = character, CharacterId = Guid.NewGuid(), Account = "replica-fur-account",
                        League = "Standard", LeagueForm = "Standard", ClassRow = witchRow, Instance = new(),
                        PlayerSent = true, PlayerObjectCreated = true, PlayerUpdatesState = updates,
                        Experience = CharacterStats.ExperienceFor(100), LeagueModule = conn.LeagueModule,
                        Wire = ("replica-fur-restored", restoredWire.Stream, restoredWire.Ciphers),
                        Pools = new(100, 1, 1, 0, 0),
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
                    Assert.Equal(4, restoredConn.Reservations.CatPhaseDeadline
                        - restoredConn.Reservations.CatPhaseStartedAt, 3);
                    Assert.False(Game.HasOnslaught(restoredConn));
                    Assert.Equal(0, Game.ChargesOf(restoredConn).Effective(ChargeType.Frenzy));
                    Assert.Equal(0, Game.ChargesOf(restoredConn).Effective(ChargeType.Endurance));
                    schedule.At = restoredConn.Reservations.CatPhaseDeadline;
                    await Game.ReservationSkillsTick("test", restoredWire.Stream, restoredWire.Ciphers,
                        restoredConn, schedule.At);
                    var restoredCharges = Game.ChargesOf(restoredConn);
                    restoredCharges.Refresh(schedule.At);
                    Assert.Equal(ReservationState.CatPhase.Agility, restoredConn.Reservations.Cat);
                    Assert.Equal(4, restoredCharges.Effective(ChargeType.Frenzy));
                    Assert.Equal(3, restoredCharges.Effective(ChargeType.Endurance));
                    Assert.True(Game.HasOnslaught(restoredConn));

                    restoredConn.PlayerLife = 0;
                    Game.ChargesOnDeath(restoredConn);
                    await Game.EndAurasOnDeath("test", restoredWire.Stream, restoredWire.Ciphers, restoredConn);
                    Assert.Equal(ReservationState.CatPhase.None, restoredConn.Reservations.Cat);
                    Assert.Equal(0, restoredConn.Reservations.PresentedCatPhaseBuff);
                    Assert.Equal(0, restoredConn.Reservations.PresentedCatOnslaughtBuff);
                    Assert.Equal(0, restoredCharges.Effective(ChargeType.Frenzy));
                    Assert.Equal(0, restoredCharges.Effective(ChargeType.Endurance));
                    Assert.False(Game.HasOnslaught(restoredConn));
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
            await ReplicaFurFixture();
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
        public bool AdvanceOnWait;
        public double Now(ClientState conn) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            if (AdvanceOnWait) At += milliseconds / 1000.0;
            return Task.CompletedTask;
        }
    }

    sealed class CountingRandom : Random
    {
        public int Rolls { get; private set; }
        public override int Next(int maxValue) { Rolls++; return 0; }
        public override int Next(int minValue, int maxValue) { Rolls++; return minValue; }
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
