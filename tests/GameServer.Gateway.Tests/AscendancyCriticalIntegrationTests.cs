using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Theory]
    [InlineData(null, 720, 1080)]
    [InlineData("global_cannot_crit", 0, 0)]
    [InlineData("virtual_global_cannot_crit", 0, 0)]
    public void Selected_spell_creation_and_refresh_use_the_complete_signed_player_critical_sum(
        string? prohibition, int fireballChance, int pulseChance)
    {
        Assert.True(DataAvailable());
        using var game = new GatewayCharacter(classRow: 5, baseClass: 6, choice: "Surfcaster", level: 20);
        EquipBlueGems(game, "Fireball", "Freezing Pulse");
        void Set(string name, int value) => game.State.PlayerStats[Program.Stats.Key(name)!.Value] = value;
        Set(ResistanceOvercap.LightningCritFlag, 1);
        Set("base_lightning_damage_resistance_%", 120);
        Set("critical_strike_chance_+%", int.MaxValue);
        Set("spell_critical_strike_chance_+%", int.MinValue);
        if (prohibition is not null) Set(prohibition, 1);
        game.State.Pools = Program.PoolsFor(game.State, 20);
        Assert.Equal(45, game.State.Pools.LightningOvercapCritPct);
        Assert.Equal(44, game.State.Pools.SpellCriticalStrikeChancePct);
        Assert.Equal(fireballChance, game.State.Pools.SpellCriticalStrikeChance);
        var raw = Program.CharacterPlayerStats(game.State);
        Assert.Equal(int.MaxValue, raw[Program.Stats.Key("critical_strike_chance_+%")!.Value]);
        uint chanceKey = Program.Stats.Key("spell_critical_strike_chance")!.Value;
        var created = ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", game.State);
        var inherited = ProgramValue<IEnumerable<(uint, int)>>("FinalPlayerStats", game.State)
            .ToDictionary(s => s.Item1, s => s.Item2);
        inherited[chanceKey] = 9999; // A final player display value is never the selected spell's base chance.
        var refreshed = ProgramValue<IEnumerable<GrantedSkill>>("GemSkills", game.State,
            inherited.Select(s => (s.Key, s.Value))).ToArray();
        foreach (var (effect, expected) in new[] { ("Fireball", fireballChance), ("FreezingPulse", pulseChance) })
        {
            var gem = game.GemEffects().Single(g => g.Effect == Program.Skills.EffectRow(effect));
            Assert.Equal(expected, created.Single(s => s.Id == gem.Id).Stats!.Single(s => s.Key == chanceKey).Value);
            Assert.Equal(expected, refreshed.Single(s => s.Id == gem.Id).Stats!.Single(s => s.Key == chanceKey).Value);
        }
    }
}
