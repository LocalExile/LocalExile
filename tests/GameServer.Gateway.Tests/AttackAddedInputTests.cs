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
    readonly ClientState conn;

    public AttackAddedInputTests()
    {
        Saves.Open(save);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        conn = SpellTotemExecutionTests.Connection("AttackAdded" + Guid.NewGuid().ToString("N"));
        conn.Clock.Stop();
        conn.Walk.Teleport(100, 100, 0);
        Set("gain_no_inherent_bonus_from_strength", 1);
    }

    public void Dispose()
    {
        conn.Closed = true;
        var cleanupErrors = new List<Exception>();
        lifetime.Stop(cleanupErrors.Add);
        Game.CombatLifetime = previousLifetime;
        Program.SetGameLogic(previous!);
        PlayerInventories.Forget(conn.Character!);
        TestEnvironment.ReleaseSaves(save);
        Assert.Empty(cleanupErrors);
    }

    static uint Key(string name) => Program.Stats.Key(name)!.Value;
    void Set(string name, int value) => conn.PlayerStats[Key(name)] = value;
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
        var items = Game.ItemsOf(conn);
        items.Add(PlayerInventories.Weapon1, 0, 0, Make("Driftwood Wand"));
        items.Add(PlayerInventories.Belt, 0, 0, Make("Stygian Vise"));
        var belt = items.Items(PlayerInventories.Belt).Single();
        var weapon = Game.WeaponFor(conn);
        Assert.True(weapon.WieldingWand);
        var baseline = Game.CharacterPlayerStats(conn);

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
                source.Stats.Select(s => s.Id));
            Assert.All(source.Stats, s => Assert.False(s.Local));
            items.Change(PlayerInventories.Belt, belt.Id, item => item with
            { Sockets = [item.Sockets[0] with { Gem = Eye(mod, minimum, maximum) }] });
            var character = Game.CharacterPlayerStats(conn);
            Assert.Equal(minimum, character[Key("attack_minimum_added_" + suffix)]);
            Assert.Equal(maximum, character[Key("attack_maximum_added_" + suffix)]);
            int Get(string name) => Program.Stats.Key(name) is uint key ? character.GetValueOrDefault(key) : 0;
            var local = weapon.LocalDamage![type];
            Assert.Equal(new DamageRange(local.Min + minimum, local.Max + maximum),
                SupportedAttackDamage.Raw(weapon, Get, new(Game.PlayerAccuracy(conn)))[type]);
            Assert.Equal(new DamageRange(local.Min * 2 + minimum * 1.5, local.Max * 2 + maximum * 1.5),
                SupportedAttackDamage.Raw(weapon, Get, new(Game.PlayerAccuracy(conn)), 20_000, 15_000)[type]);
            var hit = Game.AttackHit(weapon, character, melee: false, accuracy: new(Game.PlayerAccuracy(conn)))[type];
            string slug = type.ToString().ToLowerInvariant();
            Assert.Equal(baseline.GetValueOrDefault(Key($"main_hand_minimum_{slug}_damage")) + minimum,
                character[Key($"main_hand_minimum_{slug}_damage")]);
            Assert.Equal(baseline.GetValueOrDefault(Key($"main_hand_maximum_{slug}_damage")) + maximum,
                character[Key($"main_hand_maximum_{slug}_damage")]);
            Assert.Equal(new DamageRange(local.Min + minimum, local.Max + maximum), hit);
            Assert.Equal(local, Game.WeaponFor(conn).LocalDamage![type]);
            Assert.Equal(local, SupportedAttackDamage.Raw(weapon with { WieldingWand = false }, Get, new(Game.PlayerAccuracy(conn)))[type]);
            Assert.Equal(local, SupportedAttackDamage.Raw(weapon with { WieldingWand = false, Bow = true }, Get, new(Game.PlayerAccuracy(conn)))[type]);
            Assert.Equal(0, DamageStats.RawAdded(DamagePacket.Empty, Get, spell: true)[type].Max);
        }

        items.Add(PlayerInventories.Weapon2, 0, 0, Make("Crude Bow"));
        items.SwapWeaponSets();
        var bow = Game.WeaponFor(conn);
        var swapped = Game.CharacterPlayerStats(conn);
        Assert.True(bow.Bow);
        Assert.False(bow.WieldingWand);
        Assert.Equal(9, swapped[Key("attack_maximum_added_cold_damage_with_wand")]);
        Assert.Equal(0, Game.AttackHit(bow, swapped, melee: false, accuracy: new(Game.PlayerAccuracy(conn)))[DamageType.Cold].Max);
        Assert.Equal(0, swapped.GetValueOrDefault(Key("main_hand_maximum_cold_damage")));
        items.SwapWeaponSets();
        Assert.True(Game.WeaponFor(conn).WieldingWand);
        Assert.Equal(9, Game.CharacterPlayerStats(conn)[Key("main_hand_maximum_cold_damage")]);
        items.Remove(PlayerInventories.Belt, belt.Id);
        Assert.Equal(0, Game.CharacterPlayerStats(conn).GetValueOrDefault(Key("main_hand_maximum_cold_damage")));

        items.Remove(PlayerInventories.Weapon1, items.Items(PlayerInventories.Weapon1).Single().Id);
        items.Add(PlayerInventories.Weapon1, 0, 0, Solaris());
        var solaris = Game.WeaponFor(conn);
        var equipped = Game.CharacterPlayerStats(conn);
        const string light = "light_radius_+%", minimumFire = "minimum_added_fire_damage_to_attacks_per_1%_light_radius",
            maximumFire = "maximum_added_fire_damage_to_attacks_per_1%_light_radius";
        Assert.Equal(20, equipped[Key(light)]);
        Assert.Equal(2, equipped[Key(minimumFire)]);
        Assert.Equal(5, equipped[Key(maximumFire)]);
        Assert.All(Program.Items.Mods.ById("AddedFireDamageFromLightRadiusUnique__1")!.Stats, s => Assert.False(s.Local));
        Assert.Equal(new DamageRange(40, 100), Game.AttackHit(solaris, equipped, melee: false, accuracy: new(Game.PlayerAccuracy(conn)))[DamageType.Fire]);
        Assert.Equal(0, solaris.LocalDamage![DamageType.Fire].Max);

        foreach (var (radius, expected) in new[]
        {
            (-25, new DamageRange(0, 0)), (0, new DamageRange(0, 0)),
            (1, new DamageRange(2, 5)), (20, new DamageRange(40, 100)), (50, new DamageRange(100, 250)),
        })
        {
            Set(light, radius);
            var character = Game.CharacterPlayerStats(conn);
            int Get(string name) => Program.Stats.Key(name) is uint key ? character.GetValueOrDefault(key) : 0;
            Assert.Equal(expected, SupportedAttackDamage.Raw(solaris, Get, new(Game.PlayerAccuracy(conn)))[DamageType.Fire]);
            Assert.Equal(new DamageRange(expected.Min * 1.5, expected.Max * 1.5),
                SupportedAttackDamage.Raw(solaris, Get, new(Game.PlayerAccuracy(conn)), 20_000, 15_000)[DamageType.Fire]);
            Assert.Equal(expected, Game.AttackHit(solaris, character, melee: false, accuracy: new(Game.PlayerAccuracy(conn)))[DamageType.Fire]);
            Assert.Equal((int)expected.Min, character.GetValueOrDefault(Key("main_hand_minimum_fire_damage")));
            Assert.Equal((int)expected.Max, character.GetValueOrDefault(Key("main_hand_maximum_fire_damage")));
            Assert.Equal(expected, SupportedAttackDamage.Raw(solaris with { WieldingWand = false }, Get, new(Game.PlayerAccuracy(conn)))[DamageType.Fire]);
            Assert.Equal(0, DamageStats.RawAdded(DamagePacket.Empty, Get, spell: true)[DamageType.Fire].Max);
        }
        conn.PlayerStats.Remove(Key(light));
        items.SwapWeaponSets();
        Assert.True(Game.WeaponFor(conn).Bow);
        var inactive = Game.CharacterPlayerStats(conn);
        Assert.Equal(0, inactive.GetValueOrDefault(Key(maximumFire)));
        Assert.Equal(0, inactive.GetValueOrDefault(Key("main_hand_maximum_fire_damage")));
    }

    void Varunastra_keeps_the_additions_on_its_own_hand()
    {
        var originalPools = conn.Pools;
        var catalogue = new UniqueCatalog(Program.Items);
        var definition = Assert.IsType<UniqueCatalog.Definition>(catalogue.Find("Varunastra"));
        var sword = Assert.IsType<Item>(catalogue.Create(definition, new Random(17)));
        var items = Game.ItemsOf(conn);
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
        var stats = Game.FinalPlayerStats(conn).ToDictionary(s => s.Item1, s => s.Item2);
        int Get(string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        var weapon = Game.WeaponFor(conn);
        Assert.Equal("One Hand Sword", weapon.ItemClass);
        Assert.Equal(1, weapon.LocalSources!["local_varunastra_weapon_counts_as_all_1h_melee_weapon_types"]);
        void CheckClawHit(double factor)
        {
            var hand = Game.WeaponFor(conn);
            var modifiers = new Dictionary<uint, int>();
            var physical = Game.AttackHit(hand, modifiers, melee: true, accuracy: new(0))[DamageType.Physical];
            Assert.True(physical.Max > 0);
            modifiers[Key("claw_damage_+%")] = 100;
            Assert.Equal(physical.Scale(factor), Game.AttackHit(hand, modifiers, melee: true, accuracy: new(0))[DamageType.Physical]);
        }
        CheckClawHit(2);
        var raw = SupportedAttackDamage.Raw(weapon, Get, new(Game.PlayerAccuracy(conn)));
        Assert.Equal(new DamageRange(1, 19), raw[DamageType.Lightning]);
        Assert.Equal(new DamageRange(5, 11), raw[DamageType.Fire]);
        Assert.Equal(new DamageRange(1.5, 28.5),
            SupportedAttackDamage.Raw(weapon, Get, new(Game.PlayerAccuracy(conn)), 20_000, 15_000)[DamageType.Lightning]);
        Assert.Equal(new DamageRange(7.5, 16.5),
            SupportedAttackDamage.Raw(weapon, Get, new(Game.PlayerAccuracy(conn)), 20_000, 15_000)[DamageType.Fire]);
        Assert.Equal(19, Get("main_hand_maximum_lightning_damage"));
        Assert.Equal(11, Get("main_hand_maximum_fire_damage"));
        Assert.Equal(1, Get("main_hand_varunastra_weapon_counts_as_all_1h_melee_weapon_types"));
        Assert.Equal(0, Get("off_hand_varunastra_weapon_counts_as_all_1h_melee_weapon_types"));
        Assert.Equal(0, Get("main_hand_weapon_type")); // Native one-hand sword enum, not Claw or Dagger.
        var profile = Game.WeaponProfileFor(conn);
        Assert.True(profile.Axe && profile.Sword && profile.Mace && profile.Claw && profile.Dagger && profile.OneHandMelee);
        Assert.False(profile.Wand || profile.Bow || profile.Staff || profile.TwoHand || profile.DualClaws || profile.Unarmed);
        var animation = Game.AnimationWeaponProfileFor(conn);
        Assert.True(animation.Sword);
        Assert.False(animation.Axe || animation.Mace || animation.Claw || animation.Dagger);
        double swordContact = Game.AttackContactDelay(conn, 1, approach: false);
        void Claws(int count)
        {
            Game.ConfigureLeech(conn, name => name == "life_leech_%_is_instant_per_equipped_claw" ? 10 : 0, new Dictionary<uint, int>(), false);
            var leech = Game.LeechOf(conn);
            Assert.Equal((double)count, leech.Hit(0, (10, 0, 0), false, false, false, attack: true).Life);
            leech.Clear(0);
        }
        Claws(1);
        foreach (var (name, allowed) in new[] { ("Cobra Lash", true), ("Ground Slam", true), ("Sweep", false),
            ("Rain of Arrows", false), ("Kinetic Blast", false) })
        {
            int effect = Program.Items.GemEffects(Make(name)).First();
            Assert.Equal(allowed, Game.WeaponRestricted(conn, effect) is null);
        }
        var body = items.Add(PlayerInventories.BodyArmour, 0, 0, Make("Simple Robe"))!;
        foreach (var (skill, support, allowed) in new[] { ("Double Strike", "Nightblade Support", true),
            ("Heavy Strike", "Shockwave Support", true), ("Double Strike", "Close Combat Support", true),
            ("Heavy Strike", "Trauma Support", true), ("Rain of Arrows", "Barrage Support", false),
            ("Kinetic Blast", "Barrage Support", false) })
        {
            items.Change(PlayerInventories.BodyArmour, body.Id, item => item with
            { Sockets = [new(SocketColour.White, Make(skill)), new(SocketColour.White, Make(support))], Links = [2] });
            var active = Game.GemSkillEffects(conn).Single(g => g.Name == skill);
            Assert.Equal(allowed, Game.SupportedSkill(conn, active.Id).Supports.Count == 1);
        }
        items.Change(PlayerInventories.BodyArmour, body.Id, item => item with
        { Sockets = [new(SocketColour.White, Make("Double Strike")), new(SocketColour.White, Make("Nightblade Support"))], Links = [2] });
        var doubleStrike = Game.GemSkillEffects(conn).Single(g => g.Name == "Double Strike");
        items.Change(PlayerInventories.Weapon1, main.Id, _ => ItemCatalog.Create(Program.Items.ByHash(sword.TypeHash)!));
        CheckClawHit(1);
        Assert.Empty(Game.SupportedSkill(conn, doubleStrike.Id).Supports);
        Assert.NotNull(Game.WeaponRestricted(conn, Program.Items.GemEffects(Make("Cobra Lash")).First()));
        Assert.Equal(swordContact, Game.AttackContactDelay(conn, 1, approach: false));
        Assert.False(Game.WeaponProfileFor(conn).Claw);
        Claws(0);
        var off = items.Add(PlayerInventories.Offhand1, 0, 0, sword)!;
        stats = Game.FinalPlayerStats(conn).ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Equal(0, Get("main_hand_varunastra_weapon_counts_as_all_1h_melee_weapon_types"));
        Assert.Equal(1, Get("off_hand_varunastra_weapon_counts_as_all_1h_melee_weapon_types"));
        Assert.Equal(0, SupportedAttackDamage.Raw(Game.WeaponFor(conn), Get, new(Game.PlayerAccuracy(conn)))[DamageType.Fire].Max);
        Assert.Equal(new DamageRange(5, 11), SupportedAttackDamage.Raw(Game.OffhandWeaponFor(conn)!, Get, new(Game.PlayerAccuracy(conn)))[DamageType.Fire]);
        Assert.Null(Game.WeaponRestricted(conn, Program.Items.GemEffects(Make("Cobra Lash")).First()));
        Assert.False(Game.WeaponProfileFor(conn).DualClaws);
        Claws(1);
        items.Change(PlayerInventories.Weapon1, main.Id, _ => Make("Nailed Fist"));
        Assert.True(Game.WeaponProfileFor(conn).DualClaws);
        Claws(2);
        items.Change(PlayerInventories.Weapon1, main.Id, _ => sword);
        Assert.True(Game.WeaponProfileFor(conn).DualClaws);
        Assert.Single(Game.SupportedSkill(conn, doubleStrike.Id).Supports);
        Claws(2);
        var stored = items.Add(14, 0, 0, Make("Rusted Sword"))!;
        items.SwapWeaponSets();
        stats = Game.FinalPlayerStats(conn).ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Equal(0, Get("main_hand_varunastra_weapon_counts_as_all_1h_melee_weapon_types"));
        Assert.Equal(0, Get("off_hand_varunastra_weapon_counts_as_all_1h_melee_weapon_types"));
        Assert.False(Game.WeaponProfileFor(conn).Claw);
        Assert.Empty(Game.SupportedSkill(conn, doubleStrike.Id).Supports);
        Claws(0);
        items.SwapWeaponSets();
        conn.Experience = CharacterStats.ExperienceFor(50);
        conn.Pools = Game.PoolsFor(conn, 50);
        Assert.Null(Game.ActiveMainHand(conn));
        Assert.Null(Game.ActiveOffHand(conn));
        Assert.Empty(Game.SupportedSkill(conn, doubleStrike.Id).Supports);
        stats = Game.FinalPlayerStats(conn).ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Equal(0, Get("main_hand_varunastra_weapon_counts_as_all_1h_melee_weapon_types"));
        Assert.Equal(0, Get("off_hand_varunastra_weapon_counts_as_all_1h_melee_weapon_types"));
        Assert.False(Game.WeaponProfileFor(conn).Claw);
        Claws(0);
        conn.Experience = CharacterStats.ExperienceFor(100);
        conn.Pools = Game.PoolsFor(conn, 100);
        Assert.Single(Game.SupportedSkill(conn, doubleStrike.Id).Supports);
        items.Remove(PlayerInventories.BodyArmour, body.Id);
        items.Remove(PlayerInventories.Offhand1, off.Id);
        items.Remove(14, stored.Id);
        items.Remove(PlayerInventories.Weapon1, main.Id);
        items.Remove(PlayerInventories.Belt, belt.Id);
        conn.Pools = originalPools;
    }

    void Claw_and_dagger_additions_belong_to_the_attacking_hand()
    {
        var items = Game.ItemsOf(conn);
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
            Assert.Equal(new[] { minimumStat, maximumStat }, source.Stats.Select(s => s.Id));
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
            var character = Game.FinalPlayerStats(conn).ToDictionary(s => s.Item1, s => s.Item2);
            int Get(string name) => Program.Stats.Key(name) is uint key ? character.GetValueOrDefault(key) : 0;
            Assert.Equal(minimum, Get(minimumStat));
            Assert.Equal(maximum, Get(maximumStat));
            var weapon = Game.WeaponFor(conn);
            var local = weapon.LocalDamage![type];
            var accuracy = new AttackAccuracyProfile(Game.PlayerAccuracy(conn));
            Assert.Equal(new DamageRange(local.Min + minimum, local.Max + maximum),
                SupportedAttackDamage.Raw(weapon, Get, accuracy)[type]);
            Assert.Equal(new DamageRange(local.Min * 2 + minimum * 1.5, local.Max * 2 + maximum * 1.5),
                SupportedAttackDamage.Raw(weapon, Get, accuracy, 20_000, 15_000)[type]);
            Assert.Equal(new DamageRange(local.Min + minimum, local.Max + maximum),
                Game.AttackHit(weapon, character, melee: true, accuracy)[type]);
            Assert.Equal((int)local.Min + minimum, Get($"main_hand_minimum_{slug}_damage"));
            Assert.Equal((int)local.Max + maximum, Get($"main_hand_maximum_{slug}_damage"));
            Assert.Equal(local, Game.WeaponFor(conn).LocalDamage![type]);
            var off = Game.OffhandWeaponFor(conn)!;
            Assert.Equal(off.LocalDamage![type], SupportedAttackDamage.Raw(off, Get, accuracy)[type]);
            Assert.Equal((int)off.LocalDamage![type].Max, Get($"off_hand_maximum_{slug}_damage"));
            Assert.Equal(0, DamageStats.RawAdded(DamagePacket.Empty, Get, spell: true)[type].Max);

            // The eligible family in the other hand must not grant damage to this hand's different weapon.
            Equip(PlayerInventories.Weapon1, family == "claws" ? "Glass Shank" : "Nailed Fist");
            Equip(PlayerInventories.Offhand1, family == "claws" ? "Nailed Fist" : "Carving Knife");
            character = Game.FinalPlayerStats(conn).ToDictionary(s => s.Item1, s => s.Item2);
            weapon = Game.WeaponFor(conn);
            off = Game.OffhandWeaponFor(conn)!;
            Assert.Equal(weapon.LocalDamage![type], SupportedAttackDamage.Raw(weapon, Get, accuracy)[type]);
            Assert.Equal(new DamageRange(off.LocalDamage![type].Min + minimum, off.LocalDamage![type].Max + maximum),
                SupportedAttackDamage.Raw(off, Get, accuracy)[type]);
            Assert.Equal((int)off.LocalDamage![type].Max + maximum, Get($"off_hand_maximum_{slug}_damage"));
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
            var character = Game.FinalPlayerStats(conn).ToDictionary(s => s.Item1, s => s.Item2);
            Assert.Equal(expected, Game.AttackHit(Game.WeaponFor(conn), character, melee: true,
                accuracy: new(Game.PlayerAccuracy(conn)))[DamageType.Chaos].Max);
        }
        Equip(PlayerInventories.Weapon1, null);
        items.Remove(PlayerInventories.Belt, belt.Id);
    }

    void Accuracy_boots_apply_their_less_rating_before_the_attack_grant()
    {
        Assert.NotNull(typeof(CharacterStats).GetMethod(nameof(CharacterStats.For),
            [typeof(SkillCatalog.ClassInfo), typeof(int), typeof(CharacterStats.Bonus)]));
        var previousPools = conn.Pools;
        var items = Game.ItemsOf(conn);
        var weapon = items.Add(PlayerInventories.Weapon1, 0, 0, Make("Driftwood Wand"))!;
        Set("accuracy_rating_+%", 100);
        conn.Pools = Game.PoolsFor(conn, 100);
        // Native class row 0 has 14 Dexterity: (100 * 2 + 14 * 2) * 2 = 456.
        Assert.Equal(456, Game.PlayerAccuracy(conn));
        var implicitMod = Program.Items.Mods.ById("AddedLightningDamagePerAccuracyReducedAccuracyImplicitR1_")!;
        var boots = items.Add(PlayerInventories.Boots, 0, 0, Make("Cloudwhisper Boots") with
        { ItemLevel = 100, Mods = [new(ModSection.Implicit, implicitMod.Hash, [-25, 1, 6])] })!;
        conn.Pools = Game.PoolsFor(conn, 100);
        var character = Game.CharacterPlayerStats(conn);
        Assert.Equal(-25, character[Key("boots_implicit_accuracy_rating_+%_final")]);
        Assert.Equal(1, character[Key("attack_minimum_added_lightning_damage_per_200_accuracy_rating")]);
        Assert.Equal(6, character[Key("attack_maximum_added_lightning_damage_per_200_accuracy_rating")]);
        // 456 * 75% = 342, one complete group of 200. Both halves of the same implicit must apply.
        Assert.Equal((342, new DamageRange(1, 6)),
            (Game.PlayerAccuracy(conn), Game.AttackHit(Game.WeaponFor(conn), character, melee: false, accuracy: new(Game.PlayerAccuracy(conn)))[DamageType.Lightning]));
        items.Remove(PlayerInventories.Boots, boots.Id);
        items.Remove(PlayerInventories.Weapon1, weapon.Id);
        conn.PlayerStats.Remove(Key("accuracy_rating_+%"));
        conn.Pools = previousPools;
    }

    [Fact]
    public async Task Wand_projectiles_capture_jewel_and_light_radius_inputs_at_release()
    {
        var items = Game.ItemsOf(conn);
        var attackType = Program.Items.FindGem("Kinetic Blast")!.Value.Type;
        var attack = ItemCatalog.Create(attackType) with
        { GemExperience = Program.Items.Gem(attackType)!.ExperienceFor(2) };
        items.Add(PlayerInventories.Weapon1, 0, 0, Solaris() with
        { Sockets = [new(SocketColour.White, attack), new(SocketColour.White, Make("Fireball"))], Links = [1, 1] });
        items.Add(PlayerInventories.Belt, 0, 0, Make("Stygian Vise") with
        { Sockets = [new(SocketColour.Abyss, Eye("AbyssAddedLightningDamageWithWandsJewel1_", 1, 19))] });
        var belt = items.Items(PlayerInventories.Belt).Single();
        Set("global_always_hit", 1);
        Set("global_cannot_crit", 1);
        Set("base_deal_no_physical_damage", 1);
        Set("lightning_damage_+%", 100);
        // Keep the wand's implicit spell increase out of Kinetic Blast's spell-to-attack transfer in this control.
        Set("spell_damage_+%", 0);
        var gem = Game.GemSkillEffects(conn).Single(g => g.Name == "Kinetic Blast");
        var spell = Game.GemSkillEffects(conn).Single(g => g.Name == "Fireball");
        Assert.Equal(2, gem.Level);
        // Native level-two Kinetic Blast has 140.8% base and 141% added damage.
        Assert.Equal(new DamageRange(1.41, 26.79), Game.SupportedHitInputsFor(conn, gem.Id)!.Raw[DamageType.Lightning]);
        Assert.Equal(new DamageRange(2.82, 53.58), Game.SupportedProjectileHit(conn, gem.Id, area: false)[DamageType.Lightning]);
        Assert.Equal(new DamageRange(56.4, 141), Game.SupportedHitInputsFor(conn, gem.Id)!.Raw[DamageType.Fire]);
        Assert.Equal(0, Game.SupportedHit(conn, spell.Id)[DamageType.Lightning].Max);
        var spellFire = Game.SupportedHit(conn, spell.Id)[DamageType.Fire];
        Set("light_radius_+%", 0);
        Assert.Equal(spellFire, Game.SupportedHit(conn, spell.Id)[DamageType.Fire]);
        Set("light_radius_+%", 20);
        var panel = Game.PlayerSkills(conn).Single(s => s.Id == gem.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal(2, panel[Key("main_hand_minimum_lightning_damage")]);
        Assert.Equal(53, panel[Key("main_hand_maximum_lightning_damage")]);
        Assert.Equal(56, panel[Key("main_hand_minimum_fire_damage")]);
        Assert.Equal(141, panel[Key("main_hand_maximum_fire_damage")]);

        using var wire = new SpellTotemExecutionTests.Wire();
        var session = new Game.ProjectileSession("wand-added", wire.Stream, wire.Ciphers, conn);
        conn.ProjectileRuntimeState = session;
        var enemy = new SpawnedMonster("target", new(120, 100, 0), 0, 0, 100000) { Hostile = true };
        var ally = new SpawnedMonster("friend", new(120, 100, 0), 0, 0, 100000) { Hostile = false };
        conn.Monsters[200] = enemy;
        conn.Monsters[201] = ally;
        try
        {
            var use = Game.CaptureGemProjectile(conn, session, gem.Id, gem, (120, 100), new(0));
            var shot = Assert.IsType<ProjectileOffense>(use.Payload);
            Assert.Equal(new DamageRange(2.82, 53.58), shot.Hit[DamageType.Lightning]);
            Assert.Equal(new DamageRange(56.4, 141), shot.Hit[DamageType.Fire]);
            Assert.Equal(1, use.Profile.Count);
            session.Runtime.Release(use);
            items.Remove(PlayerInventories.Belt, belt.Id);
            Set("light_radius_+%", 0);
            Assert.Equal(0, Game.SupportedHit(conn, gem.Id)[DamageType.Lightning].Max);
            Assert.Equal(0, Game.SupportedHit(conn, gem.Id)[DamageType.Fire].Max);
            session.AdvanceTo(ProjectileRuntime.Time(1));
            Assert.InRange(enemy.MaxLife - enemy.Life, 58u, 194u);
            Assert.Equal(ally.MaxLife, ally.Life);
            Assert.Equal(new DamageRange(2.82, 53.58), shot.Hit[DamageType.Lightning]);
            Assert.Equal(new DamageRange(56.4, 141), shot.Hit[DamageType.Fire]);
        }
        finally
        {
            session.Invalidate();
            conn.Closed = true;
            await session.OutputCompleted;
        }
    }
}
