using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    // Shadow (classRow 5, base class 6). No node grants points, so each build spends the class's eight.
    // Together they reach the 18 non-start nodes; A also holds both fishing-rod notables and Sea Legs.
    internal static readonly ushort[][] SurfcasterBuilds =
    [
        [48926, 64012, 33031, 37082, 36782, 37553, 42290, 61696],
        [41643, 60505, 34767, 61246, 53922, 6252, 37534, 52944],
        [41643, 60505, 56184, 52264, 36782, 37553, 42290, 61696],
    ];

    static int SurfcasterStat(ClientState state, string name) =>
        Program.CharacterPlayerStats(state).GetValueOrDefault(Program.Stats.Key(name)!.Value);

    static GatewayCharacter Surfcaster() => new(classRow: 5, baseClass: 6, choice: "Surfcaster", refundPoints: 100, level: 60);

    static void SetDev(ClientState state, params (string Name, int Value)[] stats)
    {
        lock (state.Monsters)
            foreach (var (name, value) in stats)
                if (value == 0) state.PlayerStats.Remove(Program.Stats.Key(name)!.Value);
                else state.PlayerStats[Program.Stats.Key(name)!.Value] = value;
        state.Pools = Program.PoolsFor(state, 60);
    }

    [Fact]
    public void Surfcaster_checklist_is_the_actual_graph_and_three_builds_cover_every_node()
    {
        if (!DataAvailable()) return;
        var definition = Program.Ascendancies.Find(6, AscendancyFamily.Phrecia, "Surfcaster")!;
        Assert.Equal(new ushort[] { 18635, 48926, 37082, 53922, 64012, 41643, 60505, 34767, 6252, 33031, 52944, 36782,
            37553, 37534, 52264, 56184, 61696, 42290, 61246 }.Order(), definition.Nodes.Keys.Order());
        Assert.Equal((ushort)18635, definition.StartNode);
        Assert.All(definition.Nodes.Values, n => Assert.Equal(0, n.PointsGranted));
        Assert.Equal(definition.Nodes.Keys.Where(n => n != definition.StartNode).Order(),
            SurfcasterBuilds.SelectMany(b => b).Distinct().Order());
        // The default Phrecia gate stays closed without Game_VERIFIED_SURFCASTER.
        Assert.Throws<AscendancyRuleException>(() => AscendancyClientCompatibility.RequirePlayable(definition.Identity));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Surfcaster_legal_build_paid_full_refund_reallocation_and_reload(int index)
    {
        if (!DataAvailable()) return;
        using var game = Surfcaster();
        var nodes = SurfcasterBuilds[index];
        Assert.Null((await game.Change(nodes, true)).Error);
        Assert.Equal(8, game.State.MainAscendancy.Allocated.Count);
        Assert.Equal(16, SurfcasterStat(game.State, "cast_speed_+%")); // four small nodes, 4% each
        Assert.Equal(nodes.Order(), game.Reload().MainAscendancy.Allocated.Order());
        Assert.Null((await game.Change(nodes.Reverse().ToArray(), false, refundPoints: true)).Error);
        Assert.Empty(game.State.MainAscendancy.Allocated);
        Assert.Equal((ushort)60, game.State.RefundPoints);
        Assert.Equal(0, SurfcasterStat(game.State, "cast_speed_+%"));
        Assert.Null((await game.Change(nodes, true)).Error);
        Assert.Equal(nodes.Order(), game.Reload().MainAscendancy.Allocated.Order());
        Assert.Equal((ushort)60, game.Reload().RefundPoints);
    }

    [Fact]
    public async Task Surfcaster_rod_notables_and_Sea_Legs_match_plain_equivalents()
    {
        if (!DataAvailable()) return;
        using var game = Surfcaster();
        using var plain = Surfcaster();
        Assert.Null((await game.Change(SurfcasterBuilds[0], true)).Error);

        // Sea Legs (61696) against plain +40% cold and lightning resistance with both overcap flags.
        (string, int)[] overcapped = [("base_cold_damage_resistance_%", 100), ("base_lightning_damage_resistance_%", 100)];
        SetDev(game.State, overcapped);
        SetDev(plain.State, [.. overcapped, (ResistanceOvercap.ColdAndLightningResistance, 40),
            (ResistanceOvercap.ColdEvasionFlag, 1), (ResistanceOvercap.LightningCritFlag, 1)]);
        Assert.True(game.State.Pools.ColdOvercapEvasionPct > 0);
        Assert.Equal(plain.State.Pools.ColdOvercapEvasionPct, game.State.Pools.ColdOvercapEvasionPct);
        Assert.Equal(plain.State.Pools.LightningOvercapCritPct, game.State.Pools.LightningOvercapCritPct);
        Assert.Equal(plain.State.Pools.ColdResistance, game.State.Pools.ColdResistance);
        Assert.Equal(plain.State.Pools.LightningResistance, game.State.Pools.LightningResistance);
        Assert.Equal(plain.State.Pools.Evasion, game.State.Pools.Evasion);
        Assert.Equal(plain.State.Pools.SpellCriticalStrikeChance, game.State.Pools.SpellCriticalStrikeChance);

        // Screaming Reels (64012) and Baited Breath (37082) hold only with a Fishing Rod in the active main hand.
        foreach (var character in new[] { game, plain })
            EquipFamilyGem(character, ItemCatalog.Create(Program.Items.Find("Fireball")!));
        var (skill, effect, _, _, _, _) = ProgramValue<IEnumerable<(ushort, int, string, int, int, uint)>>(
            "GemSkillEffects", game.State).Single();
        Assert.Equal(750, Program.Skills.CastMs(effect));
        int? CastMs(ClientState state) => ProgramValue<int?>("CastMs", state, effect, skill);
        (int Speed, int Rate, int Crit) Record(ClientState state)
        {
            var stats = FamilySkills(state).Single(s => s.Id == skill).Stats!.ToDictionary(s => s.Key, s => s.Value);
            int Get(string name) => stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
            return (Get("cast_speed_+%"), Get("hundred_times_casts_per_second"), Get("spell_critical_strike_chance"));
        }
        Assert.Equal(647, CastMs(game.State)); // 750 / 1.16: no rod, no multiplier
        Assert.False(Program.RollCriticalStrike(game.State, 0));

        var rod = Program.Items.Find("FishingRod1")!;
        Assert.Equal("FishingRod", rod.Class);
        foreach (var character in new[] { game, plain })
            character.Items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(rod));
        // round(116 × 1.6 − 100) = 86% (client calculator 0x142709610); plain: the same rod with 86% increased.
        SetDev(game.State);
        SetDev(plain.State, ("base_cast_speed_+%", 86));
        Assert.Equal(403, CastMs(game.State)); // 750 / 1.86
        Assert.Equal(CastMs(plain.State), CastMs(game.State));
        Assert.Equal((86, 248, 10_000), Record(game.State));
        Assert.Equal((86, 248), (Record(plain.State).Speed, Record(plain.State).Rate));
        Assert.NotEqual(10_000, Record(plain.State).Crit);

        // Every hit crits without a roll, even from a 0% base; the panel shows 100% where the base is not 0.
        Assert.Equal(10_000, game.State.Pools.AttackCriticalStrikeChance);
        Assert.Equal(10_000, game.State.Pools.SpellCriticalStrikeChance);
        Assert.True(plain.State.Pools.AttackCriticalStrikeChance is > 0 and < 10_000);
        Assert.True(Program.RollCriticalStrike(game.State, 0, attack: false));
        Assert.False(Program.RollCriticalStrike(plain.State, 0, attack: false));
        // Never deal critical strikes wins.
        SetDev(game.State, ("global_cannot_crit", 1));
        Assert.False(Program.RollCriticalStrike(game.State, 10_000));
        Assert.Equal(0, game.State.Pools.AttackCriticalStrikeChance);
        SetDev(game.State, ("global_cannot_crit", 0));

        // Off the rod, both effects end.
        var held = Assert.Single(game.Items.Items(PlayerInventories.Weapon1));
        Assert.NotNull(game.Items.Remove(PlayerInventories.Weapon1, held.Id));
        SetDev(game.State);
        Assert.Equal(647, CastMs(game.State));
        Assert.False(Program.RollCriticalStrike(game.State, 0));
        Assert.Equal(16, Record(game.State).Speed);
    }
}
