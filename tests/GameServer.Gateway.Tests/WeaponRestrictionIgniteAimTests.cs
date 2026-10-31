using System.Reflection;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// An inactive weapon leaves the hands (model, restrictions), Burning Arrow's ignite, and the aimed flight height.
public sealed class WeaponRestrictionIgniteAimTests : IDisposable
{
    readonly string name = "WeaponIgnite" + Guid.NewGuid().ToString("N");

    ClientState Character()
    {
        TestSaves.Ensure();
        return new(new(ushort.MaxValue, "WeaponIgniteTest", 128, 128, 32, 32, null, []), 0, 1)
            { Character = name, ClassRow = SkillCatalog.WitchRow };
    }

    PlayerInventories Inventory => PlayerInventories.For(name, Program.Items);

    static Item Ring(string stat, int value)
    {
        var mod = Program.Items.Mods.All.Where(m => m.Stats.Any(a => a.Id == stat && !a.Local)).OrderBy(m => m.Level).First();
        return ItemCatalog.Create(Program.Items.Find("Iron Ring")!) with
            { Mods = [new ItemMod(ModSection.Explicit, mod.Hash, mod.Stats.Select(a => a.Id == stat ? value : 0).ToArray())] };
    }

    static T Call<T>(string method, params object[] args) =>
        (T)typeof(Program).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!;

    static int BurningArrow => Program.Skills.EffectRow("BurningArrow");

    [Fact]
    public void Burning_arrow_needs_a_bow_and_carries_its_ignite_chance()
    {
        Assert.Equal(new[] { 13 }, Program.Skills.WeaponRestrictions(BurningArrow).Order());
        Assert.Empty(Program.Skills.WeaponRestrictions(Program.Skills.EffectRow("Fireball")));
        Assert.Contains(Program.Skills.EffectStats(BurningArrow, 1),
            s => s.Item1 == Program.Stats.Key("base_chance_to_ignite_%") && s.Item2 == 25);
    }

    [Fact]
    public void An_inactive_bow_leaves_the_model_and_greys_out_bow_skills_until_it_qualifies()
    {
        var conn = Character();
        Inventory.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Harbinger Bow")!));
        conn.Pools = Program.PoolsFor(conn, 100);
        IReadOnlyList<VisibleItem> Model() => Call<IReadOnlyList<VisibleItem>>("Looks", conn, Inventory);

        Assert.NotEmpty(Program.InactiveItems[name]);
        Assert.All(Model().Where(l => l.Slot is 0 or 1), l => Assert.Equal(0, l.VisualIdentity));
        Assert.Equal("the equipped weapon cannot use this skill", Call<string?>("WeaponRestricted", conn, BurningArrow));
        Assert.Null(Call<string?>("WeaponRestricted", conn, Program.Skills.EffectRow("Fireball")));
        string inactive = Program.HandsKey(Model());

        Inventory.Add(6, 0, 0, Ring("additional_dexterity", 300));
        conn.Pools = Program.PoolsFor(conn, 100);
        Assert.Empty(Program.InactiveItems[name]);
        Assert.Contains(Model(), l => l.Slot == 1 && l.VisualIdentity != 0);   // BowSetup: the bow's art in slot 1
        Assert.Null(Call<string?>("WeaponRestricted", conn, BurningArrow));
        Assert.NotEqual(inactive, Program.HandsKey(Model()));   // SendModelIfHandsChanged re-sends the model
    }

    [Fact]
    public void Ignite_settings_come_from_the_panel_rows_chance_and_duration()
    {
        var rows = new[]
        {
            ("minimum_ignite_damage_to_deal_per_minute_from_non_critical_main_hand_damage", 600),
            ("maximum_ignite_damage_to_deal_per_minute_from_non_critical_main_hand_damage", 1200),
            ("minimum_ignite_damage_to_deal_per_minute_from_critical_main_hand_damage", 900),
            ("maximum_ignite_damage_to_deal_per_minute_from_critical_main_hand_damage", 1800),
        };
        var stats = new Dictionary<string, int> { ["base_chance_to_ignite_%"] = 25, ["chance_to_freeze_shock_ignite_%"] = 5,
            ["ignite_duration_+%"] = 50 };
        var s = MonsterIgnites.Settings.From(rows, "main_hand", k => stats.GetValueOrDefault(k));
        Assert.Equal(new MonsterIgnites.Settings(30, 10, 20, 15, 30, 6), s);
        stats["never_ignite"] = 1;
        Assert.Equal(MonsterIgnites.Settings.None, MonsterIgnites.Settings.From(rows, "main_hand", k => stats.GetValueOrDefault(k)));
    }

    [Fact]
    public void Only_the_strongest_ignite_burns_and_a_weaker_longer_one_resumes()
    {
        var ignites = new MonsterIgnites();
        var random = new Random(7);
        // A critical hit always ignites; a fixed range rolls its own value.
        Assert.True(ignites.TryApply(new(0, 100, 100, 100, 100, 4), critical: true, now: 0, avoid: 0, immune: false, random));
        Assert.True(ignites.TryApply(new(100, 10, 10, 10, 10, 8), critical: false, now: 1, avoid: 0, immune: false, random));
        Assert.Equal(8 + 1, ignites.Until);
        Assert.Equal(400, ignites.Advance(4, factor: 1));        // the 100/s ignite for 0..4 s
        Assert.Equal(25, ignites.Advance(9, factor: .5));        // then the 10/s one, halved by resistance, to 9 s
        Assert.False(ignites.Burning);
        Assert.False(ignites.TryApply(new(0, 10, 10, 10, 10, 4), critical: false, now: 9, avoid: 0, immune: false, random));
        Assert.False(ignites.TryApply(new(100, 10, 10, 10, 10, 4), critical: true, now: 9, avoid: 0, immune: true, random));
        Assert.False(ignites.TryApply(new(100, 10, 10, 10, 10, 4), critical: true, now: 9, avoid: 100, immune: false, random));
    }

    [Fact]
    public void Burning_arrow_of_vigour_adds_fire_from_maximum_life()
    {
        var stats = new Dictionary<string, int> { ["maximum_life"] = 1000, ["added_fire_damage_to_attacks_equal_to_%_maximum_life"] = 21 };
        var hit = DamageStats.RawAdded(DamagePacket.Empty, k => stats.GetValueOrDefault(k), spell: false);
        Assert.Equal(new DamageRange(210, 210), hit[DamageType.Fire]);
        Assert.Equal(new DamageRange(0, 0), DamageStats.RawAdded(DamagePacket.Empty, k => stats.GetValueOrDefault(k), spell: true)[DamageType.Fire]);
    }

    // The 1_1_2 cliff face the user aimed at (2026-10-30): heights 2, 13, 23 over two cells, nothing blocked in the grid.
    [Fact]
    public void A_shot_aimed_below_a_cliff_hits_its_face_and_one_aimed_on_top_flies_over()
    {
        int? Height(int x, int y) => x < 10 ? 2 : x == 10 ? 13 : 23;
        bool Trace(double aimX, out double fraction)
        {
            var origin = new ProjectileVector(0, 0);
            var ground = ProjectileAimHeight.Ground(origin, new(aimX, 0), Height)!.Value;
            var wall = ProjectileGeometry.TraceGrid(origin, new(30, 0), (x, y) => ProjectileAimHeight.Blocks(x, y, ground, Height));
            fraction = wall?.Fraction ?? 1;
            return wall is not null;
        }

        Assert.True(Trace(8, out double low));     // the red point in front of the face
        Assert.InRange(low * 30, 10, 11);          // stops entering the 23-step cell
        Assert.False(Trace(20, out _));            // the green point on top
        Assert.Equal(23, ProjectileAimHeight.Ground(new(20, 0), new(0, 0), Height));   // shooting down keeps the top
        Assert.Null(ProjectileAimHeight.Ground(new(0, 0), new(5, 0), (_, _) => null));  // no heights: plain grid
    }

    public void Dispose()
    {
        PlayerInventories.Forget(name);
        Program.InactiveItems.TryRemove(name, out _);
    }
}
