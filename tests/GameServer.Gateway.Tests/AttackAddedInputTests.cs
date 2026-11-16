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
    static Item Eye(string mod, int minimum, int maximum) => Make("Hypnotic Eye Jewel") with
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
    public void Equipped_wand_and_light_radius_inputs_preserve_attack_scope_and_added_effectiveness()
    {
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
                SupportedAttackDamage.Raw(weapon, Get)[type]);
            Assert.Equal(new DamageRange(local.Min * 2 + minimum * 1.5, local.Max * 2 + maximum * 1.5),
                SupportedAttackDamage.Raw(weapon, Get, 20_000, 15_000)[type]);
            var hit = Game.AttackHit(weapon, character, melee: false)[type];
            string slug = type.ToString().ToLowerInvariant();
            Assert.Equal(baseline.GetValueOrDefault(Key($"main_hand_minimum_{slug}_damage")) + minimum,
                character[Key($"main_hand_minimum_{slug}_damage")]);
            Assert.Equal(baseline.GetValueOrDefault(Key($"main_hand_maximum_{slug}_damage")) + maximum,
                character[Key($"main_hand_maximum_{slug}_damage")]);
            Assert.Equal(new DamageRange(local.Min + minimum, local.Max + maximum), hit);
            Assert.Equal(local, Game.WeaponFor(conn).LocalDamage![type]);
            Assert.Equal(local, SupportedAttackDamage.Raw(weapon with { WieldingWand = false }, Get)[type]);
            Assert.Equal(local, SupportedAttackDamage.Raw(weapon with { WieldingWand = false, Bow = true }, Get)[type]);
            Assert.Equal(0, DamageStats.RawAdded(DamagePacket.Empty, Get, spell: true)[type].Max);
        }

        items.Add(PlayerInventories.Weapon2, 0, 0, Make("Crude Bow"));
        items.SwapWeaponSets();
        var bow = Game.WeaponFor(conn);
        var swapped = Game.CharacterPlayerStats(conn);
        Assert.True(bow.Bow);
        Assert.False(bow.WieldingWand);
        Assert.Equal(9, swapped[Key("attack_maximum_added_cold_damage_with_wand")]);
        Assert.Equal(0, Game.AttackHit(bow, swapped, melee: false)[DamageType.Cold].Max);
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
        Assert.Equal(new DamageRange(40, 100), Game.AttackHit(solaris, equipped, melee: false)[DamageType.Fire]);
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
            Assert.Equal(expected, SupportedAttackDamage.Raw(solaris, Get)[DamageType.Fire]);
            Assert.Equal(new DamageRange(expected.Min * 1.5, expected.Max * 1.5),
                SupportedAttackDamage.Raw(solaris, Get, 20_000, 15_000)[DamageType.Fire]);
            Assert.Equal(expected, Game.AttackHit(solaris, character, melee: false)[DamageType.Fire]);
            Assert.Equal((int)expected.Min, character.GetValueOrDefault(Key("main_hand_minimum_fire_damage")));
            Assert.Equal((int)expected.Max, character.GetValueOrDefault(Key("main_hand_maximum_fire_damage")));
            Assert.Equal(expected, SupportedAttackDamage.Raw(solaris with { WieldingWand = false }, Get)[DamageType.Fire]);
            Assert.Equal(0, DamageStats.RawAdded(DamagePacket.Empty, Get, spell: true)[DamageType.Fire].Max);
        }
        conn.PlayerStats.Remove(Key(light));
        items.SwapWeaponSets();
        Assert.True(Game.WeaponFor(conn).Bow);
        var inactive = Game.CharacterPlayerStats(conn);
        Assert.Equal(0, inactive.GetValueOrDefault(Key(maximumFire)));
        Assert.Equal(0, inactive.GetValueOrDefault(Key("main_hand_maximum_fire_damage")));
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
