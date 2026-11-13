using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// The three Spirit Leadership stats that had no reader (tattoos.md, "Loyalty summons"): Akoya's rage regeneration, Ikiaho's
// travel-skill cooldown speed and Rakiata's action-speed floor.
public sealed class LeadershipConsumersTests
{
    [Fact]
    public void Akoya_leadership_regenerates_rage_per_minute()
    {
        // The level 1 Spirit of Akoya's Leadership: 300 Rage per minute (5 per second), from the skill's
        // ancestral_buff_base_rage_regeneration_per_minute value, mapped by RageStats.Inputs.
        var inputs = RageStats.Inputs(name => name == "base_rage_regeneration_per_minute" ? 300 : 0);
        Assert.Equal(300, inputs.RegenerationPerMinute);
        var state = new RageState();
        Assert.Equal(0, state.Read(0, inputs).Current);
        // Fractions are kept: 0.5 s pays 2.5, so two Rage.
        Assert.Equal(2, state.Read(0.5, inputs).Current);
        Assert.Equal(10, state.Read(2, inputs).Current);
        // The Leadership ends: the interval that ends at this read still pays the rate the previous inputs had (10 +
        // 5), then regeneration stops and the ordinary loss starts two seconds after the last point it added.
        Assert.Equal(15, state.Read(3, inputs with { RegenerationPerMinute = 0 }).Current);
        Assert.Equal(5, state.Read(6, inputs with { RegenerationPerMinute = 0 }).Current);
        // It fills to the ordinary maximum and stops there.
        state.Clear();
        Assert.Equal(0, state.Read(0, inputs).Current);
        Assert.Equal(inputs.Maximum, state.Read(10, inputs).Current);
        // cannot_gain_rage blocks regeneration like any other gain.
        state.Clear();
        Assert.Equal(0, state.Read(0, inputs with { CannotGain = true }).Current);
        Assert.Equal(0, state.Read(10, inputs with { CannotGain = true }).Current);
        // Regeneration and the inherent loss settle in the same advance: after a hit's 10 Rage, three seconds pay
        // 15 from regeneration and 10 inherent loss once the 2 s delay is over.
        state.Clear();
        Assert.True(state.Gain(10, 0, inputs));
        Assert.Equal(15, state.Read(3, inputs).Current);
        // A carried balance keeps the regeneration fraction across the Auth transfer.
        state.Clear();
        Assert.Equal(0, state.Read(0, inputs).Current);
        Assert.Equal(2, state.Read(0.5, inputs).Current);
        var restored = new RageState();
        restored.Restore(state.Carry(0.5, inputs), 100);
        Assert.Equal(7, restored.Read(101, inputs).Current);
        // A zero maximum keeps the balance at zero instead of going negative.
        var noRoom = new RageState();
        Assert.Equal(0, noRoom.Read(2, inputs with { MaximumBonus = -30 }).Current);
    }

    [Fact]
    public void Ikiaho_and_Rakiata_leadership_stats_are_consumed()
    {
        // Ikiaho's Leadership: 100% increased travel-skill cooldown recovery from the level 1 spirit, only for a skill
        // the client's own SkillCatalog marks as a travel skill.
        var skills = Program.Skills;
        int travel = skills.EffectRow("FlameDash");
        int plain = skills.EffectRow("Fireball");
        Assert.True(skills.IsTravel(travel));
        Assert.False(skills.IsTravel(plain));
        var speed = Program.Stats.Key("travel_skill_cooldown_speed_+%")!.Value;
        var player = new Dictionary<uint, int> { [speed] = 100 };
        var own = new Dictionary<uint, int>();
        Assert.Equal(100, Game.TravelSkillCooldownSpeed(skills, travel, player, own));
        Assert.Equal(150, Game.TravelSkillCooldownSpeed(skills, travel, player, new Dictionary<uint, int> { [speed] = 50 }));
        Assert.Equal(0, Game.TravelSkillCooldownSpeed(skills, plain, player, own));
        Assert.Equal(0, Game.TravelSkillCooldownSpeed(skills, travel,
            new Dictionary<uint, int>(), new Dictionary<uint, int>()));

        // Rakiata's Leadership: the level 1 spirit's 10 is "action speed cannot be modified below 110% of base value".
        var conn = new ClientState(new(1, "leadership", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "LeadershipConsumers", PlayerLife = 1000,
        };
        static int Get(string name, int value) => name == "action_speed_-%" ? value : 0;
        Assert.Equal(0.5, Game.PlayerActionFactorFrom(conn, name => Get(name, 50)), 6);
        int SpeedFloor(string name) => name switch
        {
            "action_speed_-%" => 50,
            "action_speed_+%_minimum_value_from_rakiatas_leadership" => 10,
            _ => 0,
        };
        Assert.Equal(1.1, Game.PlayerActionFactorFrom(conn, SpeedFloor), 6);
        // A frozen (0) action speed stays frozen: the floor only lifts a lowered positive factor.
        int Frozen(string name) => name switch
        {
            "action_speed_-%" => 100,
            "action_speed_+%_minimum_value_from_rakiatas_leadership" => 10,
            _ => 0,
        };
        Assert.Equal(0, Game.PlayerActionFactorFrom(conn, Frozen), 6);
        // The Leadership itself carries the stat; the server's action-factor path reads it even though the raw source
        // cache does not (the buff is added by CharacterPlayerStats).
        uint floorKey = Program.Stats.Key("action_speed_+%_minimum_value_from_rakiatas_leadership")!.Value;
        Assert.Equal(1, Game.PlayerActionFactor(conn), 6);
        Assert.True(conn.SpiritChieftains.TryTrigger(0, new SingleUseCooldown(80000, 0), 2274, [(floorKey, 10)], 12));
        Assert.Equal(1.1, Game.PlayerActionFactor(conn), 6);
    }
}
