using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using DamageType = GameServer.Core.World.DamageType;

namespace GameServer.Gateway.Tests;

// Task #222 followup: DamageStats.Added's attack increases gained attack_damage_+% (every attack) and gained
// correct melee context: melee_damage_+% applies to actual melee hits only — an attack gem by its own
// ActiveSkillType Melee classification (SkillCatalog.IsMelee), the default attack and the panel's main-hand rows
// by the active weapon set's weapon profile, where an inactive weapon is not there at all and unarmed swings are
// melee. No inference from the weapon's range.
public class MeleeContextTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static Dictionary<uint, int> ManaStats(StatCatalog catalog, int maxMana) =>
        new() { [catalog.Key("maximum_mana")!.Value] = maxMana, [catalog.Key(DamageStats.MaxManaAddedLightning)!.Value] = 6 };

    static Func<string, int> Reader(StatCatalog catalog, IReadOnlyDictionary<uint, int> stats) =>
        name => catalog.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;

    [Fact]
    public void Generic_attack_damage_scales_the_added_pool_bonus_like_the_type_stat()
    {
        var catalog = new StatCatalog(Data());
        var stats = ManaStats(catalog, 1000);
        stats[catalog.Key("attack_damage_+%")!.Value] = 50;

        var packet = DamageStats.Added(DamagePacket.Empty, Reader(catalog, stats), spell: false, melee: false);

        Assert.Equal(90, packet[DamageType.Lightning].Min);
        Assert.Equal(90, packet[DamageType.Lightning].Max);
    }

    [Theory]
    [InlineData(false, 60)]
    [InlineData(true, 90)]
    public void Melee_damage_scales_only_melee_swings(bool melee, double expected)
    {
        var catalog = new StatCatalog(Data());
        var stats = ManaStats(catalog, 1000);
        stats[catalog.Key("melee_damage_+%")!.Value] = 50;

        var packet = DamageStats.Added(DamagePacket.Empty, Reader(catalog, stats), spell: false, melee: melee);

        Assert.Equal(expected, packet[DamageType.Lightning].Min);
        Assert.Equal(expected, packet[DamageType.Lightning].Max);
    }

    [Fact]
    public void A_ranged_hit_keeps_the_generic_attack_increase_but_not_the_melee_one()
    {
        var catalog = new StatCatalog(Data());
        var stats = ManaStats(catalog, 1000);
        stats[catalog.Key("attack_damage_+%")!.Value] = 50;
        stats[catalog.Key("melee_damage_+%")!.Value] = 50;

        var ranged = DamageStats.Added(DamagePacket.Empty, Reader(catalog, stats), spell: false, melee: false);
        var melee = DamageStats.Added(DamagePacket.Empty, Reader(catalog, stats), spell: false, melee: true);

        Assert.Equal(90, ranged[DamageType.Lightning].Min);
        // Increases add: 50% attack + 50% melee = 100 on a melee swing.
        Assert.Equal(120, melee[DamageType.Lightning].Min);
    }

    [Fact]
    public void The_spell_route_takes_neither_attack_increase()
    {
        var catalog = new StatCatalog(Data());
        var stats = ManaStats(catalog, 1000);
        stats[catalog.Key("attack_damage_+%")!.Value] = 50;
        stats[catalog.Key("melee_damage_+%")!.Value] = 50;

        var spell = DamageStats.Added(DamagePacket.Empty, Reader(catalog, stats), spell: true, melee: true);

        // The same dictionary a spell hit sees in the hit plan: attack- and melee-flag increases never apply.
        Assert.True(spell[DamageType.Lightning].IsZero);
    }

    [Fact]
    public void The_shared_attack_packet_constructor_takes_the_context_by_the_call()
    {
        var stats = new Dictionary<uint, int> { [Program.Stats.Key("attack_damage_+%")!.Value] = 50 };
        stats[Program.Stats.Key(DamageStats.MaxManaAddedLightning)!.Value] = 6;
        stats[Program.Stats.Key("maximum_mana")!.Value] = 1000;

        var ranged = Program.AttackHit(new SkillCatalog.Unarmed(833, 2, 5, 8), stats, melee: false);
        var melee = Program.AttackHit(new SkillCatalog.Unarmed(833, 2, 5, 8), stats, melee: true);

        Assert.Equal(90, ranged[DamageType.Lightning].Min);
        Assert.Equal(90, melee[DamageType.Lightning].Min);
    }

    [Fact]
    public void The_gems_melee_classification_uses_the_ActiveSkillType()
    {
        var skills = new SkillCatalog(Data());
        // LeapSlam's ActiveSkill carries ActiveSkillType Melee (row 113); Burning Arrow's does not (row 2037).
        Assert.True(skills.IsMelee(skills.EffectRow("LeapSlam")));
        Assert.False(skills.IsMelee(skills.EffectRow("BurningArrow")));
        Assert.True(skills.IsAttack(skills.EffectRow("BurningArrow")));
    }

    [Fact]
    public void Unarmed_swings_are_melee_and_a_fresh_profile_has_no_weapon_shape()
    {
        // A unique character: PlayerInventories.For reads (and may persist) the start kit through the save database.
        TestSaves.Ensure();
        string character = $"UnarmCtx{Guid.NewGuid():N}".ToString()[..20];
        var conn = new ClientState(new AreaLayout(1, "test", 200, 200, 10, 10, null, []), 1, 1) { Character = character };
        try
        {
            var profile = Program.WeaponProfileFor(conn, activeSet: true);

            Assert.True(profile.Melee);
            Assert.True(profile.Unarmed);
            Assert.False(profile.Bow);
            Assert.False(profile.Wand);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void An_inactive_weapon_is_not_the_swing_context_either()
    {
        TestSaves.Ensure();
        string character = $"MeleeCtx{Guid.NewGuid():N}".ToString()[..20];
        var items = Program.Items;
        ItemCatalog.BaseType wand;
        try
        {
            for (int i = 0; ; i++)
            {
                if (items.Row(i) is { } row && row.Supported && row.Class == "Wand") { wand = row; break; }
                if (i > 60_000) throw new InvalidOperationException("no wand in the item catalog");
            }
            var inventories = PlayerInventories.For(character, items);
            var worn = inventories.Add(PlayerInventories.Weapon1, 0, 0, new Item(wand.Hash, ["Base"], 0x40a1));
            Program.InactiveItems[character] = [worn.Id];
            var conn = new ClientState(new AreaLayout(1, "test", 200, 200, 10, 10, null, []), 1, 1) { Character = character };

            var profile = Program.WeaponProfileFor(conn, activeSet: true);

            // The worn wand's requirements are not met, so it is not there: no Wand shape, the unarmed melee fallback.
            Assert.False(profile.Wand);
            Assert.True(profile.Unarmed);
            Assert.True(profile.Melee);
            // And the added-damage context reflects that: an inactive wand cannot turn the swing ranged.
            Assert.True(Program.MeleeAttack(conn));
        }
        finally
        {
            Program.InactiveItems.TryRemove(character, out _);
            PlayerInventories.Forget(character);
        }
    }

    [Fact]
    public void The_melee_context_helper_selects_the_gem_or_the_weapon()
    {
        var skills = new SkillCatalog(Data());
        TestSaves.Ensure();
        string character = $"ArrowsCtx{Guid.NewGuid():N}".ToString()[..20];
        var conn = new ClientState(new AreaLayout(1, "test", 200, 200, 10, 10, null, []), 1, 1) { Character = character };
        try
        {
            int leapSlam = skills.EffectRow("LeapSlam"), burningArrow = skills.EffectRow("BurningArrow");
            // An attack gem classifies itself: LeapSlam is melee, Burning Arrow is not, whatever the weapon is.
            Assert.True(Program.MeleeAttack(conn, leapSlam));
            Assert.False(Program.MeleeAttack(conn, burningArrow));
            // Everything else (the default attack, the panel) reads the active weapon set; unarmed here.
            Assert.True(Program.MeleeAttack(conn));
            // A non-attack effect falls back to the weapon profile too.
            int fireball = skills.EffectRow("Fireball");
            Assert.True(Program.MeleeAttack(conn, fireball));
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void A_melee_weapon_in_the_inactive_set_cannot_change_the_active_wands_added_damage()
    {
        TestSaves.Ensure();
        string character = "SetsCtx" + Guid.NewGuid().ToString("N");
        var items = PlayerInventories.For(character, Program.Items);
        try
        {
            items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!));
            items.Add(PlayerInventories.Weapon2, 0, 0, ItemCatalog.Create(Program.Items.Find("Driftwood Club")!));
            var conn = new ClientState(new AreaLayout(1, "test", 200, 200, 10, 10, null, []), 1, 1)
                { Character = character };
            var stats = ManaStats(Program.Stats, 1000);
            stats[Program.Stats.Key("melee_damage_+%")!.Value] = 50;
            bool melee = Program.MeleeAttack(conn);
            Assert.False(melee);
            Assert.Equal(60, Program.AttackHit(SkillCatalog.Unarmed.Witch, stats, melee)[DamageType.Lightning].Min);
            items.SwapWeaponSets();
            melee = Program.MeleeAttack(conn);
            Assert.True(melee);
            Assert.Equal(90, Program.AttackHit(SkillCatalog.Unarmed.Witch, stats, melee)[DamageType.Lightning].Min);
        }
        finally { PlayerInventories.Forget(character); }
    }
}
