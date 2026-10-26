using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

[Collection("Attack base critical Gateway")]
public sealed class UpstreamMergeIntegrationTests
{
    static void Set(ClientState conn, string name, int value) =>
        conn.PlayerStats[Program.Stats.Key(name)!.Value] = value;

    static void Flask(ClientState conn, params (string Stat, int Value)[] stats)
    {
        var utility = new UtilityFlasks.Utility(0, 121, "test", stats, 6, 20, 40);
        conn.UtilityFlaskEffects.Start(new(1, 0, "test", utility, 1, null,
            stats.Select(s => new UtilityFlasks.Source("test", s.Stat, s.Value)).ToArray(), 0, 6));
    }

    [Fact]
    public void Diamond_and_Topaz_compose_with_Sea_Legs_and_complete_developer_overrides()
    {
        TestSaves.Ensure();
        string name = "MergeCrit" + Guid.NewGuid().ToString("N");
        var conn = new ClientState(new(0, "MergeTest", 128, 128, 32, 32, null, []), 0, 1)
        { Character = name, ClassRow = 1 };
        try
        {
            Set(conn, AttackBaseCriticalStrike.UnarmedBaseStat, 1000);
            Set(conn, ResistanceOvercap.LightningCritFlag, 1);
            Flask(conn, ("critical_strike_chance_+%", 100), ("base_lightning_damage_resistance_%", 120));
            conn.Pools = Program.PoolsFor(conn, 20);
            Assert.Equal(45, conn.Pools.LightningOvercapCritPct);
            Assert.Equal(2450, conn.Pools.AttackCriticalStrikeChance);
            Assert.Equal(1225, conn.Pools.SpellCriticalStrikeChance);
            Assert.Equal(145, Program.CharacterPlayerStats(conn)[Program.Stats.Key("critical_strike_chance_+%")!.Value]);

            // Each dev value replaces its whole input, including the running flask's contribution.
            Set(conn, "critical_strike_chance_+%", 0);
            Set(conn, "base_lightning_damage_resistance_%", 100);
            conn.Pools = Program.PoolsFor(conn, 20);
            Assert.Equal(25, conn.Pools.LightningOvercapCritPct);
            Assert.Equal(1250, conn.Pools.AttackCriticalStrikeChance);
            Assert.Equal(625, conn.Pools.SpellCriticalStrikeChance);
            Assert.Equal(25, Program.CharacterPlayerStats(conn)[Program.Stats.Key("critical_strike_chance_+%")!.Value]);
        }
        finally { PlayerInventories.Forget(name); }
    }

    [Fact]
    public void Hideout_edit_and_atomic_ascendancy_save_preserve_each_others_fields()
    {
        TestSaves.Ensure();
        string name = "MergeSave" + Guid.NewGuid().ToString("N");
        var ascendancy = new AscendancyProgress(new(AscendancyFamily.Phrecia, "Whisperer", AscendancyCatalog.Revision),
            [2266], ["normal", "cruel"]);
        CharacterSaves.Write(name, new([], MainAscendancy: ascendancy, RefundPoints: 20));
        var before = CharacterSaves.PassiveProgressSnapshot.From(CharacterSaves.Load(name));
        CharacterSaves.SetHideoutPlacement(name, "HideoutForest", "stash", new(110, 220, 1.5f));
        CharacterSaves.SetMainAscendancy(name, ascendancy with { Nodes = [] }, refundPoints: 15, expected: before);
        var saved = CharacterSaves.Load(name)!;
        Assert.Equal(ascendancy.Selection, saved.MainAscendancy!.Selection);
        Assert.Empty(saved.MainAscendancy.Allocated);
        Assert.Equal(new[] { "normal", "cruel" }, saved.MainAscendancy.EarnedAwards);
        Assert.Equal((ushort)15, saved.RefundPoints);
        Assert.Equal(new HideoutPlacement(110, 220, 1.5f), saved.HideoutPlacements!["HideoutForest"]["stash"]);
        CharacterSaves.SetHideoutPlacement(name, "HideoutForest", "stash", new(120, 230, 0));
        Assert.Equal((ushort)15, CharacterSaves.Load(name)!.RefundPoints);
    }
}

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Fact]
    public async Task Instant_flask_recovery_respects_reserved_life_and_sends_the_reservation()
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter(classRow: 6, baseClass: 5, choice: "Architect of Chaos", refundPoints: 60, level: 20);
        EquipBlueGems(game, "Clarity");
        var definition = Program.Ascendancies.Find(game.State.MainAscendancy.Selection!)!;
        Assert.Null((await game.Change(PathTo(definition, 9529), allocate: true)).Error);
        Assert.True((await game.Cast("Clarity")).Accepted);
        int reserved = AuraCatalog.ReservedLife(game.State.Pools, game.State.Auras.Values);
        Assert.True(reserved > 0);
        uint cap = game.State.PlayerMaxLife - (uint)reserved;
        var instant = Program.Items.Mods.ById("FlaskFullInstantRecovery1")!;
        var flask = ItemCatalog.Create(Program.Items.Find("Small Life Flask")!) with
        {
            Charges = 21, Rarity = ItemRarity.Magic,
            Mods = [instant.Roll(ModSection.Explicit, new Random(1))],
        };
        game.Items.Add(PlayerInventories.Flask, 0, 0, flask);
        game.State.PlayerLife = cap - 1;
        var result = await game.UseMergeFlask();
        Assert.Null(result.Error);
        Assert.Equal(cap, game.State.PlayerLife);
        Assert.True(result.Bytes.AsSpan().IndexOf(InstanceProtocol.LifeUpdate(Program.PlayerId, cap, (uint)reserved)) >= 0);
    }

    sealed partial class GatewayCharacter
    {
        public Task<(string? Error, byte[] Bytes)> UseMergeFlask() => Exchange(async (stream, cipher) =>
        {
            await ProgramValue<Task>("UseFlask", "merge-flask", stream, cipher, State, 0u);
            return null;
        });
    }
}
