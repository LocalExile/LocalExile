using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class RuthlessRuntimeCatalogTests : IDisposable
{
    readonly List<string> characters = [];

    public RuthlessRuntimeCatalogTests() => TestSaves.Ensure();

    ClientState Connection(bool ruthless)
    {
        string name = "ModeRuntime" + Guid.NewGuid().ToString("N");
        characters.Add(name);
        return new(new(0, "ModeRuntime", 128, 128, 32, 32, null, []), 0, 1)
        {
            Character = name, ClassRow = 1, Ruthless = ruthless, PlayerLife = 285, PlayerMana = 170,
            Pools = new(40, 285, 170, 100, 0, Strength: 100, Dexterity: 100, Intelligence: 100),
        };
    }

    static MethodInfo Method(string name) => typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    static (Dictionary<uint, int> Stats, AttributeModifiers Attributes) Sources(ClientState conn) =>
        ((Dictionary<uint, int> Stats, AttributeModifiers Attributes))
            Method("CharacterStatSources").Invoke(null, [conn, null, null])!;

    [Theory]
    [InlineData(false, "Grace")]
    [InlineData(true, "GraceHardMode")]
    public void Runtime_actor_skills_and_support_resolution_use_the_selected_mode(bool ruthless, string effectName)
    {
        var conn = Connection(ruthless);
        var catalog = Program.ItemsFor(conn);
        var gem = ItemCatalog.Create(catalog.Find("Grace")!);
        var host = ItemCatalog.Create(catalog.Find("Driftwood Wand")!) with { Sockets = [new(SocketColour.Green, gem)] };
        PlayerInventories.For(conn.Character!, catalog).Add(PlayerInventories.Weapon1, 0, 0, host);

        var gemMethod = typeof(Program).GetMethod("GemSkillEffects", BindingFlags.Static | BindingFlags.NonPublic,
            binder: null, types: [typeof(ClientState)], modifiers: null)!;
        var active = Assert.Single((IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>)
            gemMethod.Invoke(null, [conn])!);
        Assert.Equal(effectName, Program.SkillsFor(conn).EffectId(active.Effect));

        // SupportedSkill assigns its own ids. It must use the same mode effects as the Actor's gem list.
        var supported = Method("SupportedSkill").Invoke(null, [conn, active.Id])!;
        Assert.Equal(active.Effect, (int)supported.GetType().GetProperty("Effect")!.GetValue(supported)!);

        var actor = (IReadOnlyList<GrantedSkill>)Method("PlayerSkills").Invoke(null, [conn])!;
        Assert.NotNull(Assert.Single(actor, skill => skill.Id == active.Id));
    }

    [Fact]
    public void A_legacy_or_dev_imported_unavailable_gem_is_not_granted_and_cannot_support_other_skills_in_Ruthless()
    {
        var conn = Connection(true);
        var catalog = Program.ItemsFor(conn);
        var fireball = ItemCatalog.Create(catalog.Find("Fireball")!);
        var frostblink = ItemCatalog.Create(catalog.Find("Frostblink")!);
        var rarity = ItemCatalog.Create(catalog.Find("Item Rarity Support")!);
        var host = ItemCatalog.Create(catalog.Find("Driftwood Wand")!) with
        {
            Sockets = [new(SocketColour.Blue, frostblink), new(SocketColour.Blue, fireball), new(SocketColour.Blue, rarity)],
            Links = [3],
        };
        PlayerInventories.For(conn.Character!, catalog).Add(PlayerInventories.Weapon1, 0, 0, host);
        var gemMethod = typeof(Program).GetMethod("GemSkillEffects", BindingFlags.Static | BindingFlags.NonPublic,
            binder: null, types: [typeof(ClientState)], modifiers: null)!;
        var active = Assert.Single((IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>)
            gemMethod.Invoke(null, [conn])!);
        Assert.Equal("Fireball", active.Name);
        var supported = Method("SupportedSkill").Invoke(null, [conn, active.Id])!;
        Assert.Equal(active.Effect, (int)supported.GetType().GetProperty("Effect")!.GetValue(supported)!);
        Assert.Empty((IEnumerable<GemSupports.Applied>)supported.GetType().GetProperty("Supports")!.GetValue(supported)!);
        Assert.NotNull(Method("GemRequirementsUnmet").Invoke(null, [conn, frostblink]));
        Assert.NotNull(Method("GemRequirementsUnmet").Invoke(null, [conn, rarity]));
    }

    [Fact]
    public void Simultaneous_runtime_characters_get_their_own_passive_alternate()
    {
        var regular = Connection(false);
        var ruthless = Connection(true);
        regular.Passives = [5819];
        ruthless.Passives = [5819];
        var normalStats = Sources(regular).Stats;
        var ruthlessStats = Sources(ruthless).Stats;

        Assert.Equal(10, normalStats.GetValueOrDefault(180u));
        Assert.Equal(0, ruthlessStats.GetValueOrDefault(180u));
        Assert.Equal(10, Sources(regular).Stats.GetValueOrDefault(180u));
    }

    [Theory]
    [InlineData(false, 130)]
    [InlineData(true, 120)]
    public void Mode_passive_attributes_compose_with_equipment_before_pool_scaling_and_raw_overrides(bool ruthless,
        int expectedStrength)
    {
        var conn = Connection(ruthless);
        var catalog = Program.ItemsFor(conn);
        uint strengthPct = Program.Stats.Key("strength_+%")!.Value;
        // Raw-source fixture only, like AttributeGatewayTests: it does not bypass ascendancy selection or admission.
        // The copied Chieftain node has 10% increased Strength ordinarily and no attribute stat in Ruthless.
        conn.MainAscendancy = new(Nodes: [57052]);
        Assert.Equal(ruthless ? 0 : 10,
            Program.PassivesFor(conn).Find(57052)!.Stats.Where(s => s.Key == strengthPct).Sum(s => s.Value));
        var ring = ItemCatalog.Create(catalog.Find("Iron Ring")!) with
        {
            Mods = new[] { (Stat: "additional_strength", Value: 86), (Stat: "strength_+%", Value: 20) }
                .Select(s =>
                {
                    var mod = catalog.Mods.All.Where(m => m.Stats.Any(a => a.Id == s.Stat && !a.Local))
                        .OrderBy(m => m.Level).First();
                    return new ItemMod(ModSection.Explicit, mod.Hash,
                        mod.Stats.Select(a => a.Id == s.Stat ? s.Value : 0).ToArray());
                }).ToArray(),
        };
        PlayerInventories.For(conn.Character!, catalog).Add(6, 0, 0, ring);

        conn.Pools = Program.PoolsFor(conn, 40);
        Assert.Equal(expectedStrength, conn.Pools.Strength); // Witch 14 + gear 86, then the complete increase bucket.
        Assert.Equal(expectedStrength, Program.CharacterPlayerStats(conn)[Program.Stats.Key("strength")!.Value]);
        Assert.Equal(expectedStrength, Program.PoolsFor(conn, 40).Strength);

        conn.PlayerStats[strengthPct] = 0;
        Assert.Equal(100, Program.PoolsFor(conn, 40).Strength); // Explicit raw zero replaces both passive and gear inputs.
        conn.PlayerStats.Remove(strengthPct);
        Assert.Equal(expectedStrength, Program.PoolsFor(conn, 40).Strength);
        conn.MainAscendancy = new();
        Assert.Equal(120, Program.PoolsFor(conn, 40).Strength);
    }

    public void Dispose()
    {
        foreach (string character in characters) PlayerInventories.Forget(character);
    }
}
