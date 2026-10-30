using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task An_observed_player_tick_survives_idle_work_and_publishes_low_Life_leech_gates(bool nativeCoast, bool recover)
    {
        using var game = new GatewayCharacter(level: 100, family: AscendancyFamily.Normal, choice: "Elementalist",
            classRow: SkillCatalog.WitchRow, baseClass: 3);
        var state = new ClientState(game.State.Layout, 0, 1)
            { Character = game.Character, ClassRow = SkillCatalog.WitchRow, Instance = new InstanceState() };
        ProgramValue<object?>("LoadProgress", state, 100);
        var inventory = PlayerInventories.For(game.Character, Program.Items);
        ItemMod Mod(string stat, int value)
        {
            var mod = Program.Items.Mods.All.First(m => m.Stats.Any(s => s.Id == stat));
            return new(ModSection.Explicit, mod.Hash, mod.Stats.Select(s => s.Id == stat ? value : 0).ToArray());
        }
        inventory.Add(6, 0, 0, ItemCatalog.Create(Program.Items.Find("Iron Ring")!) with
            { Mods = [Mod("additional_all_attributes", 200)] });
        var body = Program.Items.Find("Plate Vest")!;
        var basis = Program.Items.Defences(body)!.Value.AtRoll(0);
        var graceType = Program.Items.Find("Metadata/Items/Gems/SkillGemGrace")!;
        var grace = ItemCatalog.Create(graceType) with { GemExperience = Program.Items.Gem(graceType)!.ExperienceFor(20) };
        inventory.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(body, defenceBaseRoll: 0) with
        {
            Mods = [Mod("local_base_physical_damage_reduction_rating", 120 - basis.Armour),
                Mod("local_base_evasion_rating", 120 - basis.Evasion), Mod("local_energy_shield", 120 - basis.EnergyShield)],
            Sockets = [new(SocketColour.White, grace)],
        });
        state.Use(nativeCoast ? AreaLayouts.For(AreaLayouts.TheCoast, 1)
            : new(ushort.MaxValue, "ObservedLeechTick", 128, 128, 32, 32, (_, _) => true, []));
        state.Pools = Program.PoolsFor(state, 100);
        state.PlayerLife = state.Pools.Life;
        state.PlayerMana = state.Pools.Mana;
        state.PlayerSent = true;
        typeof(Program).GetMethod("RestoreRememberedAuras", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [state, new[] { "Grace" }]);
        Assert.Equal(1345u, state.Pools.Life);
        Assert.Equal(750u, state.Pools.Mana);
        Assert.Equal(148u, state.Pools.EnergyShield); //120 local ES*(1+floor(232 Intelligence/10)%)=147.6.

        var result = await game.ObserveLeechPanelTick(state, recover);

        Assert.Equal(672u, state.PlayerLife);
        var expected = InstanceEntities.StatsUpdate(Program.PlayerId,
            ProgramValue<List<(uint, int)>>("PlayerStatsWithCleared", state));
        Assert.True(result.Bytes.AsSpan().IndexOf(expected) >= 0, "The actual tick never published the final low-Life stat snapshot.");
        var current = Program.CharacterPlayerStats(state);
        Assert.Equal(1, current[Program.Stats.Key("cannot_leech_life")!.Value]);
        Assert.Equal(1, current[Program.Stats.Key("cannot_leech_mana")!.Value]);
        Assert.Equal(0, current[Program.Stats.Key("cannot_leech_energy_shield")!.Value]);
    }

    sealed partial class GatewayCharacter
    {
        public Task<(string? Error, byte[] Bytes)> ObserveLeechPanelTick(ClientState state, bool recover) => Exchange(async (stream, cipher) =>
        {
            var ticker = (Task)GatewayMethod("RunPlayerTick").Invoke(null, ["leech-tick-audit", stream, cipher, state])!;
            var recovery = recover ? (Task)GatewayMethod("RunResourceRecovery").Invoke(null,
                ["leech-tick-audit", stream, cipher, state])! : Task.CompletedTask;
            try
            {
                // Observe the task explicitly: an exception in idle quest/collision work must not become a silent timeout.
                await Task.WhenAny(ticker, Task.Delay(1100));
                if (ticker.IsCompleted) await ticker;
                Assert.False(ticker.IsCompleted, "The player tick ended during idle work.");
                state.Commands = ProgramValue<ChatCommands>("Commands", "leech-tick-audit", stream, cipher, state);
                foreach (var (name, value) in new (string, int)[]
                {
                    ("life_leech_speed_+%", 500),
                    ("maximum_life_leech_rate_+%", 40), ("maximum_mana_leech_rate_+%", 40),
                    ("maximum_energy_shield_leech_rate_+%", 40),
                    ("maximum_life_leech_amount_per_leech_+%", 100), ("maximum_mana_leech_amount_per_leech_+%", 50),
                    ("maximum_energy_shield_leech_amount_per_leech_is_doubled", 1),
                    ("maximum_leech_rate_+%", 20), ("map_player_maximum_leech_rate_+%", -50),
                    ("base_cannot_leech", 1), ("base_cannot_leech", 0), ("base_cannot_leech_life", -1),
                    ("base_cannot_leech_life", 0), ("cannot_leech_or_regenerate_mana", 1),
                    ("maximum_life_leech_rate_+%", 0), ("maximum_mana_leech_rate_+%", 0),
                    ("maximum_energy_shield_leech_rate_+%", 0),
                    ("maximum_life_leech_amount_per_leech_+%", 0), ("maximum_mana_leech_amount_per_leech_+%", 0),
                    ("maximum_energy_shield_leech_amount_per_leech_is_doubled", 0),
                    ("maximum_leech_rate_+%", 0), ("map_player_maximum_leech_rate_+%", 0),
                    ("cannot_leech_or_regenerate_mana", 0), ("life_leech_speed_+%", 0),
                    ("cannot_leech_when_on_low_life", 1),
                })
                {
                    Assert.True(await state.Commands.RunAsync($"/set_stat {name} {value}"));
                    await Task.Delay(17);
                    if (ticker.IsCompleted) await ticker;
                    if (recovery.IsCompleted) await recovery;
                }
                Assert.True(await state.Commands.RunAsync("/hit chaos 673"));
                await Task.WhenAny(ticker, Task.Delay(900));
                if (ticker.IsCompleted) await ticker;
                Assert.False(ticker.IsCompleted, "The player tick ended after the low-Life transition.");
            }
            finally
            {
                state.Closed = true;
                await Task.WhenAll(ticker, recovery).WaitAsync(TimeSpan.FromSeconds(5));
            }
            return null;
        });
    }
}
