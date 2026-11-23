using GameServer.Core.Accounts;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class AttackAddedInputTests : IDisposable
{
    readonly string save = Path.Combine(Path.GetTempPath(), $"attack-added-{Guid.NewGuid():N}.db");
    readonly IGameLogic? previous = Program.GameLogicOrNull;
    readonly GameServer.Modding.IModuleLifetime? previousLifetime = Game.CombatLifetime;
    readonly ModuleLifetime lifetime = new("Game");
    readonly Player player;
    readonly Dictionary<uint, int> overrides = new();
    readonly IDisposable exemptionSubscription;
    readonly IDisposable statSubscription;
    Func<InventoryItem, bool> exemptItem = _ => true;

    public AttackAddedInputTests()
    {
        Saves.Open(save);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        string name = "AttackAdded" + Guid.NewGuid().ToString("N");
        player = new Player(new(1, name, 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Connection = { LockstepClient = false },
            Character = { Name = name, Experience = CharacterStats.ExperienceFor(100) },
            PlayerLife = 1000,
            PlayerMana = 1000,
            Instance = new(),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"]))
        };
        player.Clock.Stop();
        player.Walk.Teleport(100, 100, 0);
        exemptionSubscription = GameEvents.RequirementExemptions.Add("Game",
            p => ReferenceEquals(p, player)
                ? [.. Game.ItemsOf(p).EquipmentSnapshot().Where(e => exemptItem(e.Item)).Select(e => e.Item.Id)]
                : []);
        statSubscription = GameEvents.PlayerStatSources.Add("Game", (p, s) =>
        {
            if (!ReferenceEquals(p, player) || overrides.Count == 0) return s;
            var merged = new Dictionary<uint, int>(s);
            foreach (var (k, v) in overrides) merged[k] = v;
            return merged;
        });
        Set(Stat.GainNoInherentBonusFromStrength, 1);
    }

    public void Dispose()
    {
        statSubscription.Dispose();
        exemptionSubscription.Dispose();
        player.Closed = true;
        var cleanupErrors = new List<Exception>();
        lifetime.Stop(cleanupErrors.Add);
        Game.CombatLifetime = previousLifetime;
        Program.SetGameLogic(previous!);
        PlayerInventories.Forget(player.Character.Name!);
        TestEnvironment.ReleaseSaves(save);
        Assert.Empty(cleanupErrors);
    }

    static uint Key(string name) => Program.Stats.Key(name)!.Value;
    void Set(uint key, int value) => overrides[key] = value;
    static Item Make(string name) => ItemCatalog.Create(Program.Items.Find(name)!);
    static Item Eye(string mod, int minimum, int maximum, string name = "Hypnotic Eye Jewel") => Make(name) with
    {
        Rarity = ItemRarity.Magic,
        Mods = [new(ModSection.Explicit, Program.Items.Mods.ById(mod)!.Hash, [minimum, maximum])],
    };

    static Item Solaris()
    {
        var catalogue = new UniqueCatalog(Program.Items);
        var definition = Assert.IsType<UniqueCatalog.Definition>(catalogue.Find("Eclipse Solaris"));
        return Assert.IsType<Item>(catalogue.Create(definition with
        { Ranges = [new("LightRadiusUnique__1", 0, 20, 20)] }, new Random(17)));
    }

    [Fact]
    public void Equipped_attack_additions_preserve_weapon_scope_and_added_effectiveness()
    {
        Varunastra_keeps_the_additions_on_its_own_hand();
        Claw_and_dagger_additions_belong_to_the_attacking_hand();
        Accuracy_boots_apply_their_less_rating_before_the_attack_grant();
        var items = Game.ItemsOf(player);
        items.Add(PlayerInventories.Weapon1, 0, 0, Make("Driftwood Wand"));
        items.Add(PlayerInventories.Belt, 0, 0, Make("Stygian Vise"));
        var belt = items.Items(PlayerInventories.Belt).Single();
        var weapon = Game.WeaponFor(player);
        Assert.True(weapon.WieldingWand);
        var baseline = Game.CharacterPlayerStats(player);

        foreach (var (type, mod, suffix, minimum, maximum) in new[]
        {
            (DamageType.Physical, "AbyssAddedPhysicalDamageWithWandsJewel1_", "physical_damage_with_wands", 2, 3),
            (DamageType.Lightning, "AbyssAddedLightningDamageWithWandsJewel1_", "lightning_damage_with_wand", 1, 19),
            (DamageType.Fire, "AbyssAddedFireDamageWithWandsJewel1", "fire_damage_with_wand", 5, 11),
            (DamageType.Cold, "AbyssAddedColdDamageWithWandsJewel1__", "cold_damage_with_wand", 4, 9),
        })
        {
            var source = Program.Items.Mods.ById(mod)!;
            Assert.Equal(new[] { "attack_minimum_added_" + suffix, "attack_maximum_added_" + suffix },
                source.Stats.Select(s => Program.Stats.Name(s.Id)));
            Assert.All(source.Stats, s => Assert.False(s.Local));
            items.Change(PlayerInventories.Belt, belt.Id, item => item with
            { Sockets = [item.Sockets[0] with { Gem = Eye(mod, minimum, maximum) }] });
            var character = Game.CharacterPlayerStats(player);
            Assert.Equal(minimum, character[Key("attack_minimum_added_" + suffix)]);
            Assert.Equal(maximum, character[Key("attack_maximum_added_" + suffix)]);
            int Get(uint key) => character.GetValueOrDefault(key);
            var local = weapon.LocalDamage![type];
            Assert.Equal(new DamageRange(local.Min + minimum, local.Max + maximum),
                SupportedAttackDamage.Raw(weapon, Get, new(Game.PlayerAccuracy(player)))[type]);
            Assert.Equal(new DamageRange(local.Min * 2 + minimum * 1.5, local.Max * 2 + maximum * 1.5),
                SupportedAttackDamage.Raw(weapon, Get, new(Game.PlayerAccuracy(player)), 20_000, 15_000)[type]);
            var hit = Game.AttackHit(weapon, character, melee: false, accuracy: new(Game.PlayerAccuracy(player)))[type];
            string slug = type.ToString().ToLowerInvariant();
            Assert.Equal(baseline.GetValueOrDefault(Key($"main_hand_minimum_{slug}_damage")) + minimum,
                character[Key($"main_hand_minimum_{slug}_damage")]);
            Assert.Equal(baseline.GetValueOrDefault(Key($"main_hand_maximum_{slug}_damage")) + maximum,
                character[Key($"main_hand_maximum_{slug}_damage")]);
            Assert.Equal(new DamageRange(local.Min + minimum, local.Max + maximum), hit);
            Assert.Equal(local, Game.WeaponFor(player).LocalDamage![type]);
            Assert.Equal(local, SupportedAttackDamage.Raw(weapon with { WieldingWand = false }, Get, new(Game.PlayerAccuracy(player)))[type]);
            Assert.Equal(local, SupportedAttackDamage.Raw(weapon with { WieldingWand = false, Bow = true }, Get, new(Game.PlayerAccuracy(player)))[type]);
            Assert.Equal(0, DamageStats.RawAdded(DamagePacket.Empty, Get, spell: true)[type].Max);
        }

        items.Add(PlayerInventories.Weapon2, 0, 0, Make("Crude Bow"));
        items.SwapWeaponSets();
        var bow = Game.WeaponFor(player);
        var swapped = Game.CharacterPlayerStats(player);
        Assert.True(bow.Bow);
        Assert.False(bow.WieldingWand);
        Assert.Equal(9, swapped[Key("attack_maximum_added_cold_damage_with_wand")]);
        Assert.Equal(0, Game.AttackHit(bow, swapped, melee: false, accuracy: new(Game.PlayerAccuracy(player)))[DamageType.Cold].Max);
        Assert.Equal(0, swapped.GetValueOrDefault(Key("main_hand_maximum_cold_damage")));
        items.SwapWeaponSets();
        Assert.True(Game.WeaponFor(player).WieldingWand);
        Assert.Equal(9, Game.CharacterPlayerStats(player)[Key("main_hand_maximum_cold_damage")]);
        items.Remove(PlayerInventories.Belt, belt.Id);
        Assert.Equal(0, Game.CharacterPlayerStats(player).GetValueOrDefault(Key("main_hand_maximum_cold_damage")));

        items.Remove(PlayerInventories.Weapon1, items.Items(PlayerInventories.Weapon1).Single().Id);
        items.Add(PlayerInventories.Weapon1, 0, 0, Solaris());
        var solaris = Game.WeaponFor(player);
        var equipped = Game.CharacterPlayerStats(player);
        const uint light = Stat.LightRadiusPlusPct, minimumFire = Stat.MinimumAddedFireDamageToAttacksPer1PctLightRadius,
            maximumFire = Stat.MaximumAddedFireDamageToAttacksPer1PctLightRadius;
        Assert.Equal(20, equipped[light]);
        Assert.Equal(2, equipped[minimumFire]);
        Assert.Equal(5, equipped[maximumFire]);
        Assert.All(Program.Items.Mods.ById("AddedFireDamageFromLightRadiusUnique__1")!.Stats, s => Assert.False(s.Local));
        Assert.Equal(new DamageRange(40, 100), Game.AttackHit(solaris, equipped, melee: false, accuracy: new(Game.PlayerAccuracy(player)))[DamageType.Fire]);
        Assert.Equal(0, solaris.LocalDamage![DamageType.Fire].Max);

        foreach (var (radius, expected) in new[]
        {
            (-25, new DamageRange(0, 0)), (0, new DamageRange(0, 0)),
            (1, new DamageRange(2, 5)), (20, new DamageRange(40, 100)), (50, new DamageRange(100, 250)),
        })
        {
            Set(light, radius);
            var character = Game.CharacterPlayerStats(player);
            int Get(uint key) => character.GetValueOrDefault(key);
            Assert.Equal(expected, SupportedAttackDamage.Raw(solaris, Get, new(Game.PlayerAccuracy(player)))[DamageType.Fire]);
            Assert.Equal(new DamageRange(expected.Min * 1.5, expected.Max * 1.5),
                SupportedAttackDamage.Raw(solaris, Get, new(Game.PlayerAccuracy(player)), 20_000, 15_000)[DamageType.Fire]);
            Assert.Equal(expected, Game.AttackHit(solaris, character, melee: false, accuracy: new(Game.PlayerAccuracy(player)))[DamageType.Fire]);
            Assert.Equal((int)expected.Min, character.GetValueOrDefault(Key("main_hand_minimum_fire_damage")));
            Assert.Equal((int)expected.Max, character.GetValueOrDefault(Stat.MainHandMaximumFireDamage));
            Assert.Equal(expected, SupportedAttackDamage.Raw(solaris with { WieldingWand = false }, Get, new(Game.PlayerAccuracy(player)))[DamageType.Fire]);
            Assert.Equal(0, DamageStats.RawAdded(DamagePacket.Empty, Get, spell: true)[DamageType.Fire].Max);
        }
        overrides.Remove(light);
        items.SwapWeaponSets();
        Assert.True(Game.WeaponFor(player).Bow);
        var inactive = Game.CharacterPlayerStats(player);
        Assert.Equal(0, inactive.GetValueOrDefault(maximumFire));
        Assert.Equal(0, inactive.GetValueOrDefault(Stat.MainHandMaximumFireDamage));
    }

    void Varunastra_keeps_the_additions_on_its_own_hand()
    {
        var originalPools = player.Pools;
        var catalogue = new UniqueCatalog(Program.Items);
        var definition = Assert.IsType<UniqueCatalog.Definition>(catalogue.Find("Varunastra"));
        var sword = Assert.IsType<Item>(catalogue.Create(definition, new Random(17)));
        var items = Game.ItemsOf(player);
        var main = items.Add(PlayerInventories.Weapon1, 0, 0, sword)!;
        var belt = items.Add(PlayerInventories.Belt, 0, 0, Make("Stygian Vise") with
        {
            Sockets = [new(SocketColour.Abyss, Make("Murderous Eye Jewel") with
            {
                Rarity = ItemRarity.Rare,
                Mods = [new(ModSection.Explicit, Program.Items.Mods.ById("AbyssAddedLightningDamageWithClawsJewel1__")!.Hash, [1, 19]),
                    new(ModSection.Explicit, Program.Items.Mods.ById("AbyssAddedFireDamageWithDaggersJewel1")!.Hash, [5, 11])],
            })],
        })!;
        var stats = Game.FinalPlayerStats(player).ToDictionary(s => s.Item1, s => s.Item2);
        int Get(uint key) => stats.GetValueOrDefault(key);
        var weapon = Game.WeaponFor(player);
        Assert.Equal("One Hand Sword", weapon.ItemClass);
        Assert.Equal(1, weapon.LocalSources![Stat.LocalVarunastraWeaponCountsAsAll1hMeleeWeaponTypes]);
        void CheckClawHit(double factor)
        {
            var hand = Game.WeaponFor(player);
            var modifiers = new Dictionary<uint, int>();
            var physical = Game.AttackHit(hand, modifiers, melee: true, accuracy: new(0))[DamageType.Physical];
            Assert.True(physical.Max > 0);
            modifiers[Stat.ClawDamagePlusPct] = 100;
            Assert.Equal(physical.Scale(factor), Game.AttackHit(hand, modifiers, melee: true, accuracy: new(0))[DamageType.Physical]);
        }
        CheckClawHit(2);
        var raw = SupportedAttackDamage.Raw(weapon, Get, new(Game.PlayerAccuracy(player)));
        Assert.Equal(new DamageRange(1, 19), raw[DamageType.Lightning]);
        Assert.Equal(new DamageRange(5, 11), raw[DamageType.Fire]);
        Assert.Equal(new DamageRange(1.5, 28.5),
            SupportedAttackDamage.Raw(weapon, Get, new(Game.PlayerAccuracy(player)), 20_000, 15_000)[DamageType.Lightning]);
        Assert.Equal(new DamageRange(7.5, 16.5),
            SupportedAttackDamage.Raw(weapon, Get, new(Game.PlayerAccuracy(player)), 20_000, 15_000)[DamageType.Fire]);
        Assert.Equal(19, Get(Stat.MainHandMaximumLightningDamage));
        Assert.Equal(11, Get(Stat.MainHandMaximumFireDamage));
        Assert.Equal(1, Get(Stat.MainHandVarunastraWeaponCountsAsAll1hMeleeWeaponTypes));
        Assert.Equal(0, Get(Stat.OffHandVarunastraWeaponCountsAsAll1hMeleeWeaponTypes));
        Assert.Equal(0, Get(Stat.MainHandWeaponType)); // Native one-hand sword enum, not Claw or Dagger.
        var profile = Game.WeaponProfileFor(player);
        Assert.True(profile.Axe && profile.Sword && profile.Mace && profile.Claw && profile.Dagger && profile.OneHandMelee);
        Assert.False(profile.Wand || profile.Bow || profile.Staff || profile.TwoHand || profile.DualClaws || profile.Unarmed);
        var animation = Game.AnimationWeaponProfileFor(player);
        Assert.True(animation.Sword);
        Assert.False(animation.Axe || animation.Mace || animation.Claw || animation.Dagger);
        double swordContact = Game.AttackContactDelay(player, 1, approach: false);
        void Claws(int count)
        {
            Game.ConfigureLeech(player, key => key == Stat.LifeLeechPctIsInstantPerEquippedClaw ? 10 : 0, false);
            var leech = Game.LeechOf(player);
            Assert.Equal((double)count, leech.Hit(0, (10, 0, 0), false, false, false, attack: true).Life);
            leech.Clear(0);
        }
        Claws(1);
        foreach (var (name, allowed) in new[] { ("Cobra Lash", true), ("Ground Slam", true), ("Sweep", false),
            ("Rain of Arrows", false), ("Kinetic Blast", false) })
        {
            int effect = Program.Items.GemEffects(Make(name)).First();
            Assert.Equal(allowed, Game.WeaponRestricted(player, effect) is null);
        }
        var body = items.Add(PlayerInventories.BodyArmour, 0, 0, Make("Simple Robe"))!;
        foreach (var (skill, support, allowed) in new[] { ("Double Strike", "Nightblade Support", true),
            ("Heavy Strike", "Shockwave Support", true), ("Double Strike", "Close Combat Support", true),
            ("Heavy Strike", "Trauma Support", true), ("Rain of Arrows", "Barrage Support", false),
            ("Kinetic Blast", "Barrage Support", false) })
        {
            items.Change(PlayerInventories.BodyArmour, body.Id, item => item with
            { Sockets = [new(SocketColour.White, Make(skill)), new(SocketColour.White, Make(support))], Links = [2] });
            var active = Game.GemSkillEffects(player).Single(g => g.Name == skill);
            Assert.Equal(allowed, Game.SupportedSkill(player, active.Id).Supports.Count == 1);
        }
        items.Change(PlayerInventories.BodyArmour, body.Id, item => item with
        { Sockets = [new(SocketColour.White, Make("Double Strike")), new(SocketColour.White, Make("Nightblade Support"))], Links = [2] });
        var doubleStrike = Game.GemSkillEffects(player).Single(g => g.Name == "Double Strike");
        items.Change(PlayerInventories.Weapon1, main.Id, _ => ItemCatalog.Create(Program.Items.ByHash(sword.TypeHash)!));
        CheckClawHit(1);
        Assert.Empty(Game.SupportedSkill(player, doubleStrike.Id).Supports);
        Assert.NotNull(Game.WeaponRestricted(player, Program.Items.GemEffects(Make("Cobra Lash")).First()));
        Assert.Equal(swordContact, Game.AttackContactDelay(player, 1, approach: false));
        Assert.False(Game.WeaponProfileFor(player).Claw);
        Claws(0);
        var off = items.Add(PlayerInventories.Offhand1, 0, 0, sword)!;
        stats = Game.FinalPlayerStats(player).ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Equal(0, Get(Stat.MainHandVarunastraWeaponCountsAsAll1hMeleeWeaponTypes));
        Assert.Equal(1, Get(Stat.OffHandVarunastraWeaponCountsAsAll1hMeleeWeaponTypes));
        Assert.Equal(0, SupportedAttackDamage.Raw(Game.WeaponFor(player), Get, new(Game.PlayerAccuracy(player)))[DamageType.Fire].Max);
        Assert.Equal(new DamageRange(5, 11), SupportedAttackDamage.Raw(Game.OffhandWeaponFor(player)!, Get, new(Game.PlayerAccuracy(player)))[DamageType.Fire]);
        Assert.Null(Game.WeaponRestricted(player, Program.Items.GemEffects(Make("Cobra Lash")).First()));
        Assert.False(Game.WeaponProfileFor(player).DualClaws);
        Claws(1);
        items.Change(PlayerInventories.Weapon1, main.Id, _ => Make("Nailed Fist"));
        Assert.True(Game.WeaponProfileFor(player).DualClaws);
        Claws(2);
        items.Change(PlayerInventories.Weapon1, main.Id, _ => sword);
        Assert.True(Game.WeaponProfileFor(player).DualClaws);
        Assert.Single(Game.SupportedSkill(player, doubleStrike.Id).Supports);
        Claws(2);
        var stored = items.Add(14, 0, 0, Make("Rusted Sword"))!;
        items.SwapWeaponSets();
        stats = Game.FinalPlayerStats(player).ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Equal(0, Get(Stat.MainHandVarunastraWeaponCountsAsAll1hMeleeWeaponTypes));
        Assert.Equal(0, Get(Stat.OffHandVarunastraWeaponCountsAsAll1hMeleeWeaponTypes));
        Assert.False(Game.WeaponProfileFor(player).Claw);
        Assert.Empty(Game.SupportedSkill(player, doubleStrike.Id).Supports);
        Claws(0);
        items.SwapWeaponSets();
        exemptItem = i => i.Id != main.Id && i.Id != off.Id;
        player.Character.Experience = CharacterStats.ExperienceFor(50);
        player.Pools = Game.PoolsFor(player, 50);
        Assert.Null(Game.ActiveMainHand(player));
        Assert.Null(Game.ActiveOffHand(player));
        Assert.Empty(Game.SupportedSkill(player, doubleStrike.Id).Supports);
        stats = Game.FinalPlayerStats(player).ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Equal(0, Get(Stat.MainHandVarunastraWeaponCountsAsAll1hMeleeWeaponTypes));
        Assert.Equal(0, Get(Stat.OffHandVarunastraWeaponCountsAsAll1hMeleeWeaponTypes));
        Assert.False(Game.WeaponProfileFor(player).Claw);
        Claws(0);
        exemptItem = _ => true;
        player.Character.Experience = CharacterStats.ExperienceFor(100);
        player.Pools = Game.PoolsFor(player, 100);
        Assert.Single(Game.SupportedSkill(player, doubleStrike.Id).Supports);
        items.Remove(PlayerInventories.BodyArmour, body.Id);
        items.Remove(PlayerInventories.Offhand1, off.Id);
        items.Remove(14, stored.Id);
        items.Remove(PlayerInventories.Weapon1, main.Id);
        items.Remove(PlayerInventories.Belt, belt.Id);
        player.Pools = originalPools;
    }

    void Claw_and_dagger_additions_belong_to_the_attacking_hand()
    {
        var items = Game.ItemsOf(player);
        items.Add(PlayerInventories.Belt, 0, 0, Make("Stygian Vise"));
        var belt = items.Items(PlayerInventories.Belt).Single();
        void Equip(byte row, string? name)
        {
            if (items.Items(row) is [var current]) items.Remove(row, current.Id);
            if (name is not null) items.Add(row, 0, 0, Make(name));
        }
        foreach (var (type, family, mod, minimum, maximum) in new[]
        {
            (DamageType.Physical, "claws", "AbyssAddedPhysicalDamageWithClawsJewel1", 2, 3),
            (DamageType.Lightning, "claws", "AbyssAddedLightningDamageWithClawsJewel1__", 1, 19),
            (DamageType.Fire, "claws", "AbyssAddedFireDamageWithClawsJewel1", 5, 11),
            (DamageType.Cold, "claws", "AbyssAddedColdDamageWithClawsJewel1", 4, 9),
            (DamageType.Chaos, "claws", "AbyssAddedChaosDamageWithClawsJewel1", 4, 9),
            (DamageType.Physical, "daggers", "AbyssAddedPhysicalDamageWithDaggersJewel1", 2, 3),
            (DamageType.Lightning, "daggers", "AbyssAddedLightningDamageWithDaggersJewel1", 1, 19),
            (DamageType.Fire, "daggers", "AbyssAddedFireDamageWithDaggersJewel1", 5, 11),
            (DamageType.Cold, "daggers", "AbyssAddedColdDamageWithDaggersJewel1", 4, 9),
            (DamageType.Chaos, "daggers", "AbyssAddedChaosDamageWithDaggersJewel1", 4, 9),
        })
        {
            string slug = type.ToString().ToLowerInvariant();
            string minimumStat = $"attack_minimum_added_{slug}_damage_with_{family}",
                maximumStat = $"attack_maximum_added_{slug}_damage_with_{family}";
            var source = Program.Items.Mods.ById(mod)!;
            Assert.Equal(new[] { minimumStat, maximumStat }, source.Stats.Select(s => Program.Stats.Name(s.Id)));
            Assert.All(source.Stats, s => Assert.False(s.Local));
            items.Change(PlayerInventories.Belt, belt.Id, item => item with
            { Sockets = [new(SocketColour.Abyss, Eye(mod, minimum, maximum, "Murderous Eye Jewel"))] });
            Equip(PlayerInventories.Weapon1, family == "claws" ? "Nailed Fist" : "Glass Shank");
            Equip(PlayerInventories.Offhand1, family == "claws" ? "Glass Shank" : "Nailed Fist");
            if (type == DamageType.Lightning)
            {
                var held = items.Items(PlayerInventories.Weapon1).Single();
                items.Change(PlayerInventories.Weapon1, held.Id, item => item with
                {
                    Rarity = ItemRarity.Magic,
                    Mods = [new(ModSection.Explicit, Program.Items.Mods.ById("LocalAddedLightningDamage1")!.Hash, [1, 5])],
                });
            }
            var character = Game.FinalPlayerStats(player).ToDictionary(s => s.Item1, s => s.Item2);
            int Get(uint key) => character.GetValueOrDefault(key);
            int GetByName(string name) => Program.Stats.Key(name) is uint key ? character.GetValueOrDefault(key) : 0;
            Assert.Equal(minimum, GetByName(minimumStat));
            Assert.Equal(maximum, GetByName(maximumStat));
            var weapon = Game.WeaponFor(player);
            var local = weapon.LocalDamage![type];
            var accuracy = new AttackAccuracyProfile(Game.PlayerAccuracy(player));
            Assert.Equal(new DamageRange(local.Min + minimum, local.Max + maximum),
                SupportedAttackDamage.Raw(weapon, Get, accuracy)[type]);
            Assert.Equal(new DamageRange(local.Min * 2 + minimum * 1.5, local.Max * 2 + maximum * 1.5),
                SupportedAttackDamage.Raw(weapon, Get, accuracy, 20_000, 15_000)[type]);
            Assert.Equal(new DamageRange(local.Min + minimum, local.Max + maximum),
                Game.AttackHit(weapon, character, melee: true, accuracy)[type]);
            Assert.Equal((int)local.Min + minimum, GetByName($"main_hand_minimum_{slug}_damage"));
            Assert.Equal((int)local.Max + maximum, GetByName($"main_hand_maximum_{slug}_damage"));
            Assert.Equal(local, Game.WeaponFor(player).LocalDamage![type]);
            var off = Game.OffhandWeaponFor(player)!;
            Assert.Equal(off.LocalDamage![type], SupportedAttackDamage.Raw(off, Get, accuracy)[type]);
            Assert.Equal((int)off.LocalDamage![type].Max, GetByName($"off_hand_maximum_{slug}_damage"));
            Assert.Equal(0, DamageStats.RawAdded(DamagePacket.Empty, Get, spell: true)[type].Max);

            // The eligible family in the other hand must not grant damage to this hand's different weapon.
            Equip(PlayerInventories.Weapon1, family == "claws" ? "Glass Shank" : "Nailed Fist");
            Equip(PlayerInventories.Offhand1, family == "claws" ? "Nailed Fist" : "Carving Knife");
            character = Game.FinalPlayerStats(player).ToDictionary(s => s.Item1, s => s.Item2);
            weapon = Game.WeaponFor(player);
            off = Game.OffhandWeaponFor(player)!;
            Assert.Equal(weapon.LocalDamage![type], SupportedAttackDamage.Raw(weapon, Get, accuracy)[type]);
            Assert.Equal(new DamageRange(off.LocalDamage![type].Min + minimum, off.LocalDamage![type].Max + maximum),
                SupportedAttackDamage.Raw(off, Get, accuracy)[type]);
            Assert.Equal((int)off.LocalDamage![type].Max + maximum, GetByName($"off_hand_maximum_{slug}_damage"));
        }
        // The final source is dagger chaos. Rune Daggers share the native Dagger family; unrelated hands do not.
        Equip(PlayerInventories.Offhand1, null);
        foreach (var (name, expected) in new (string?, int)[]
        {
            ("Carving Knife", 9), ("Nailed Fist", 0), ("Rusted Sword", 0), ("Crude Bow", 0),
            ("Driftwood Wand", 0), ("Driftwood Club", 0), (null, 0),
        })
        {
            Equip(PlayerInventories.Weapon1, name);
            var character = Game.FinalPlayerStats(player).ToDictionary(s => s.Item1, s => s.Item2);
            Assert.Equal(expected, Game.AttackHit(Game.WeaponFor(player), character, melee: true,
                accuracy: new(Game.PlayerAccuracy(player)))[DamageType.Chaos].Max);
        }
        Equip(PlayerInventories.Weapon1, null);
        items.Remove(PlayerInventories.Belt, belt.Id);
    }

    void Accuracy_boots_apply_their_less_rating_before_the_attack_grant()
    {
        Assert.NotNull(typeof(CharacterStats).GetMethod(nameof(CharacterStats.For),
            [typeof(SkillCatalog.ClassInfo), typeof(int), typeof(CharacterStats.Bonus)]));
        var previousPools = player.Pools;
        var items = Game.ItemsOf(player);
        var weapon = items.Add(PlayerInventories.Weapon1, 0, 0, Make("Driftwood Wand"))!;
        Set(Stat.AccuracyRatingPlusPct, 100);
        player.Pools = Game.PoolsFor(player, 100);
        // Native class row 0 has 14 Dexterity: (100 * 2 + 14 * 2) * 2 = 456.
        Assert.Equal(456, Game.PlayerAccuracy(player));
        var implicitMod = Program.Items.Mods.ById("AddedLightningDamagePerAccuracyReducedAccuracyImplicitR1_")!;
        var boots = items.Add(PlayerInventories.Boots, 0, 0, Make("Cloudwhisper Boots") with
        { ItemLevel = 100, Mods = [new(ModSection.Implicit, implicitMod.Hash, [-25, 1, 6])] })!;
        player.Pools = Game.PoolsFor(player, 100);
        var character = Game.CharacterPlayerStats(player);
        Assert.Equal(-25, character[Stat.BootsImplicitAccuracyRatingPlusPctFinal]);
        Assert.Equal(1, character[Stat.AttackMinimumAddedLightningDamagePer200AccuracyRating]);
        Assert.Equal(6, character[Stat.AttackMaximumAddedLightningDamagePer200AccuracyRating]);
        // 456 * 75% = 342, one complete group of 200. Both halves of the same implicit must apply.
        Assert.Equal((342, new DamageRange(1, 6)),
            (Game.PlayerAccuracy(player), Game.AttackHit(Game.WeaponFor(player), character, melee: false, accuracy: new(Game.PlayerAccuracy(player)))[DamageType.Lightning]));
        items.Remove(PlayerInventories.Boots, boots.Id);
        items.Remove(PlayerInventories.Weapon1, weapon.Id);
        overrides.Remove(Stat.AccuracyRatingPlusPct);
        player.Pools = previousPools;
    }

    [Fact]
    public async Task Wand_projectiles_capture_jewel_and_light_radius_inputs_at_release()
    {
        var items = Game.ItemsOf(player);
        var attackType = Program.Items.FindGem("Kinetic Blast")!.Value.Type;
        var attack = ItemCatalog.Create(attackType) with
        { GemExperience = Program.Items.Gem(attackType)!.ExperienceFor(2) };
        items.Add(PlayerInventories.Weapon1, 0, 0, Solaris() with
        { Sockets = [new(SocketColour.White, attack), new(SocketColour.White, Make("Fireball"))], Links = [1, 1] });
        items.Add(PlayerInventories.Belt, 0, 0, Make("Stygian Vise") with
        { Sockets = [new(SocketColour.Abyss, Eye("AbyssAddedLightningDamageWithWandsJewel1_", 1, 19))] });
        var belt = items.Items(PlayerInventories.Belt).Single();
        Set(Stat.GlobalAlwaysHit, 1);
        Set(Stat.GlobalCannotCrit, 1);
        Set(Stat.BaseDealNoPhysicalDamage, 1);
        Set(Stat.LightningDamagePlusPct, 100);
        // Keep the wand's implicit spell increase out of Kinetic Blast's spell-to-attack transfer in this control.
        Set(Stat.SpellDamagePlusPct, 0);
        var gem = Game.GemSkillEffects(player).Single(g => g.Name == "Kinetic Blast");
        var spell = Game.GemSkillEffects(player).Single(g => g.Name == "Fireball");
        Assert.Equal(2, gem.Level);
        // Native level-two Kinetic Blast has 140.8% base and 141% added damage.
        Assert.Equal(new DamageRange(1.41, 26.79), Game.SupportedHitInputsFor(player, gem.Id)!.Raw[DamageType.Lightning]);
        Assert.Equal(new DamageRange(2.82, 53.58), Game.SupportedProjectileHit(player, gem.Id, area: false)[DamageType.Lightning]);
        Assert.Equal(new DamageRange(56.4, 141), Game.SupportedHitInputsFor(player, gem.Id)!.Raw[DamageType.Fire]);
        Assert.Equal(0, Game.SupportedHit(player, spell.Id)[DamageType.Lightning].Max);
        var spellFire = Game.SupportedHit(player, spell.Id)[DamageType.Fire];
        Set(Stat.LightRadiusPlusPct, 0);
        Assert.Equal(spellFire, Game.SupportedHit(player, spell.Id)[DamageType.Fire]);
        Set(Stat.LightRadiusPlusPct, 20);
        var panel = Game.PlayerSkills(player).Single(s => s.Id == gem.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal(2, panel[Key("main_hand_minimum_lightning_damage")]);
        Assert.Equal(53, panel[Stat.MainHandMaximumLightningDamage]);
        Assert.Equal(56, panel[Key("main_hand_minimum_fire_damage")]);
        Assert.Equal(141, panel[Stat.MainHandMaximumFireDamage]);

        using var wire = new SpellTotemExecutionTests.Wire();
        player.Connection.Session = new("wand-added", wire.Stream, wire.Ciphers);
        var session = new Game.ProjectileSession(player);
        player.ProjectileRuntimeState = session;
        var enemy = new SpawnedMonster("target", new(120, 100, 0), 0, 0, 100000) { Hostile = true };
        var ally = new SpawnedMonster("friend", new(120, 100, 0), 0, 0, 100000) { Hostile = false };
        player.Area.Monsters[200] = enemy;
        player.Area.Monsters[201] = ally;
        try
        {
            var use = Game.CaptureGemProjectile(player, session, gem.Id, gem, (120, 100), new(0));
            var shot = Assert.IsType<ProjectileOffense>(use.Payload);
            Assert.Equal(new DamageRange(2.82, 53.58), shot.Hit[DamageType.Lightning]);
            Assert.Equal(new DamageRange(56.4, 141), shot.Hit[DamageType.Fire]);
            Assert.Equal(1, use.Profile.Count);
            session.Runtime.Release(use);
            items.Remove(PlayerInventories.Belt, belt.Id);
            Set(Stat.LightRadiusPlusPct, 0);
            Assert.Equal(0, Game.SupportedHit(player, gem.Id)[DamageType.Lightning].Max);
            Assert.Equal(0, Game.SupportedHit(player, gem.Id)[DamageType.Fire].Max);
            session.AdvanceTo(ProjectileRuntime.Time(1));
            Assert.InRange(enemy.MaxLife - enemy.Life, 58u, 194u);
            Assert.Equal(ally.MaxLife, ally.Life);
            Assert.Equal(new DamageRange(2.82, 53.58), shot.Hit[DamageType.Lightning]);
            Assert.Equal(new DamageRange(56.4, 141), shot.Hit[DamageType.Fire]);
        }
        finally
        {
            session.Invalidate();
            player.Closed = true;
            await session.OutputCompleted;
        }
    }
}
