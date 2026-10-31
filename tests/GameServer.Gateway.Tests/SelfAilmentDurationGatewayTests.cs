using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    const ushort SelfDurationHarshLessons = 27623;
    const string SelfDurationAll = "base_all_ailment_duration_on_self_+%";
    static readonly string[] SelfDurationAilments = ["ignite", "chill", "freeze", "shock"];
    static uint SelfDurationKey(string name) => Program.Stats.Key(name)!.Value;

    static Dictionary<uint, int> SelfDurationPublished(GatewayCharacter game)
    {
        game.State.Pools = Program.PoolsFor(game.State, game.State.Pools.Level);
        return LevelPanelPublished(game);
    }

    static void SelfDurationAssertAll(IReadOnlyDictionary<uint, int> published, int expected)
    {
        foreach (string ailment in SelfDurationAilments)
            Assert.Equal(expected, published[SelfDurationKey($"self_{ailment}_duration_-%")]);
    }

    static void SelfDurationAddHarshLessons(GatewayCharacter game)
    {
        Assert.Equal(17087u, SelfDurationKey(SelfDurationAll));
        var tree = Program.Passives;
        Assert.Equal("Harsh Lessons", tree.Find(SelfDurationHarshLessons)!.Name);
        Assert.Equal(-20, tree.Stats([SelfDurationHarshLessons])[SelfDurationKey(SelfDurationAll)]);
        // Isolate a real node's sources. This does not claim a disconnected node
        // is a valid saved tree; graph admission has independent coverage.
        game.State.Passives.Add(SelfDurationHarshLessons);
    }

    [Fact]
    public void Self_ailment_real_Harsh_Lessons_publishes_all_four_reductions_and_clears_after_removal()
    {
        using var game = LevelPanelCharacter(100);
        SelfDurationAddHarshLessons(game);
        SelfDurationAssertAll(SelfDurationPublished(game), 20);
        SelfDurationAssertAll(SelfDurationPublished(game), 20);
        Assert.Equal(-20, Program.CharacterPlayerStats(game.State)[SelfDurationKey(SelfDurationAll)]);
        Assert.DoesNotContain(SelfDurationKey(SelfDurationAll), game.State.PlayerStats.Keys);

        Assert.True(game.State.Passives.Remove(SelfDurationHarshLessons));
        SelfDurationAssertAll(SelfDurationPublished(game), 0);
        var afterClear = SelfDurationPublished(game);
        foreach (string ailment in SelfDurationAilments)
        {
            uint key = SelfDurationKey($"self_{ailment}_duration_-%");
            Assert.DoesNotContain(key, game.State.SentStatKeys);
            Assert.DoesNotContain(key, afterClear.Keys);
        }
        SelfDurationAddHarshLessons(game);
        SelfDurationAssertAll(SelfDurationPublished(game), 20);
    }

    [Fact]
    public void Self_ailment_typed_elemental_and_real_global_sources_compose_once_and_remove_independently()
    {
        using var game = LevelPanelCharacter(100);
        SelfDurationAddHarshLessons(game);
        foreach (string ailment in SelfDurationAilments)
            game.State.PlayerStats[SelfDurationKey($"base_self_{ailment}_duration_-%")] = 20;
        uint elemental = SelfDurationKey("self_elemental_status_duration_-%");
        game.State.PlayerStats[elemental] = -50;
        SelfDurationAssertAll(SelfDurationPublished(game), -10);
        SelfDurationAssertAll(SelfDurationPublished(game), -10);

        Assert.True(game.State.PlayerStats.Remove(elemental));
        SelfDurationAssertAll(SelfDurationPublished(game), 40);
        foreach (string ailment in SelfDurationAilments)
            Assert.True(game.State.PlayerStats.Remove(SelfDurationKey($"base_self_{ailment}_duration_-%")));
        SelfDurationAssertAll(SelfDurationPublished(game), 20);
        Assert.True(game.State.Passives.Remove(SelfDurationHarshLessons));
        SelfDurationAssertAll(SelfDurationPublished(game), 0);
    }

    [Fact]
    public void Self_ailment_real_positive_duration_modifier_publishes_signed_reductions_and_cancels_Harsh_Lessons()
    {
        using var game = LevelPanelCharacter(100);
        var catalog = Program.Items;
        var inventory = ProgramValue<PlayerInventories>("ItemsOf", game.State);
        var mod = catalog.Mods.ById("WeaponTreeLocalEvasionRatingIncreasedAilmentDurationOnSelf1")!;
        Assert.Equal(34583, mod.Row);
        Assert.Equal(1, mod.Domain);
        Assert.Equal(mod.Id, catalog.Mods.ByHash(mod.Hash)!.Id);
        Assert.Equal(new[] { "local_base_evasion_rating", SelfDurationAll }, mod.Stats.Select(s => s.Id));
        Assert.Equal(new[] { 100, 20 }, mod.Stats.Select(s => s.Min));
        Assert.Equal(new[] { 100, 20 }, mod.Stats.Select(s => s.Max));
        Assert.True(mod.Stats[0].Local);
        Assert.False(mod.Stats[1].Local);
        // Actual native modifier/vector on an isolated carrier; no claim that
        // this weapon-tree modifier legally rolls on the ring used by the test.
        var ring = inventory.Add(6, 0, 0, LevelPanelCarrier(catalog, "Iron Ring",
            new ItemMod(ModSection.Explicit, mod.Hash, [100, 20])));
        SelfDurationAssertAll(SelfDurationPublished(game), -20);
        SelfDurationAssertAll(SelfDurationPublished(game), -20);

        SelfDurationAddHarshLessons(game);
        SelfDurationAssertAll(SelfDurationPublished(game), 0);
        Assert.NotNull(inventory.Remove(6, ring.Id));
        SelfDurationAssertAll(SelfDurationPublished(game), 20);
        Assert.True(game.State.Passives.Remove(SelfDurationHarshLessons));
        SelfDurationAssertAll(SelfDurationPublished(game), 0);
    }

    [Fact]
    public void Self_ailment_explicit_final_replacements_including_zero_keep_precedence_over_real_sources()
    {
        using var game = LevelPanelCharacter(100);
        SelfDurationAddHarshLessons(game);
        SelfDurationAssertAll(SelfDurationPublished(game), 20);
        uint ignite = SelfDurationKey("self_ignite_duration_-%");
        game.State.PlayerStats[ignite] = 777;
        var supplied = SelfDurationPublished(game);
        Assert.Equal(777, supplied[ignite]);
        foreach (string ailment in SelfDurationAilments.Skip(1))
            Assert.Equal(20, supplied[SelfDurationKey($"self_{ailment}_duration_-%")]);

        foreach (string ailment in SelfDurationAilments)
            game.State.PlayerStats[SelfDurationKey($"self_{ailment}_duration_-%")] = 0;
        SelfDurationAssertAll(SelfDurationPublished(game), 0);
        Assert.Equal(-20, Program.CharacterPlayerStats(game.State)[SelfDurationKey(SelfDurationAll)]);
        foreach (string ailment in SelfDurationAilments)
            Assert.True(game.State.PlayerStats.Remove(SelfDurationKey($"self_{ailment}_duration_-%")));
        SelfDurationAssertAll(SelfDurationPublished(game), 20);
    }
}
