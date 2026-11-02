using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class RationalDoctrineTests
{
    static int Stat(Item item, string id) =>
        item.Mods.Single(m => Program.Items.Mods.ByHash(m.Hash)!.Id == id).Values[0];

    [Fact]
    public void Rational_Doctrine_exposes_its_ground_mods_and_attribute_rules()
    {
        var jewel = CommandsRules.CreateUniqueJewel("Rational Doctrine");
        Assert.NotNull(jewel);
        Assert.Equal(18, Stat(jewel!, "ConsecratedGroundStationarySTRHighestUnique__1"));
        Assert.Equal(25, Stat(jewel!, "ProfaneGroundCriticalStrikeINTHighestUnique__1"));
        Assert.Equal(4000, Stat(jewel!, "ConsecratedGroundLingersUnique__1"));
        Assert.Equal(4000, Stat(jewel!, "ProfaneGroundLingersUnique__1"));

        var stats = new Dictionary<string, int>
        {
            ["strength"] = 100, ["dexterity"] = 50, ["intelligence"] = 99,
            [FlaskGrounds.ProfaneChanceStat] = 25,
        };
        Assert.Equal(0, FlaskGrounds.ProfaneChance(name => stats.GetValueOrDefault(name)));
        stats["intelligence"] = 100;
        Assert.Equal(25, FlaskGrounds.ProfaneChance(name => stats.GetValueOrDefault(name)));
        stats["intelligence"] = 101;
        Assert.Equal(25, FlaskGrounds.ProfaneChance(name => stats.GetValueOrDefault(name)));

        var conn = new ClientState(new(1, "rational", 100, 100, 100, 100, null, []), 0, 1);
        conn.FlaskGrounds.SetProfane(7, inside: true, alive: true, now: 1, () => 99);
        Assert.Equal(900, global::Game.CriticalChanceOn(conn, 7, 500, null, 900));
        Assert.Equal(500, global::Game.CriticalChanceOn(conn, 8, 500, null, 900));
    }

    [Fact]
    public void Profane_ground_applies_to_monsters_and_lingers_after_they_leave()
    {
        Assert.True(FlaskGrounds.LoadArt("data").ContainsKey(FlaskGrounds.Kind.ProfaneGround));
        Assert.Equal((ushort)1569, FlaskGrounds.ProfaneBuffRow);
        var made = FlaskGrounds.ProfaneGround((3, 4), now: 10);
        Assert.Equal(FlaskGrounds.Kind.ProfaneGround, made.Kind);
        Assert.Equal(10, made.Radius);
        Assert.Equal(14, made.Ends);
        var state = new FlaskGrounds.State();
        state.Configure(stationaryRadius: 0, lingerMs: 0, profaneLingerMs: 4000);
        const double now = 10;
        Assert.True(state.Add(new(FlaskGrounds.Kind.ProfaneGround, 10, 10, 10, now + 4)));

        var added = state.SetProfane(1, inside: true, alive: true, now, () => 42);
        Assert.NotNull(added);
        Assert.Equal((ushort)42, added!.Value.Buff);
        Assert.True(added.Value.Added);
        Assert.True(state.Profane(1));
        Assert.Null(state.SetProfane(1, inside: false, alive: true, now + 3.9, () => 43));
        Assert.True(state.Profane(1));
        var removed = state.SetProfane(1, inside: false, alive: true, now + 4, () => 43);
        Assert.NotNull(removed);
        Assert.Equal((ushort)42, removed!.Value.Buff);
        Assert.False(removed.Value.Added);
        Assert.False(state.Profane(1));

        var second = state.SetProfane(2, inside: true, alive: true, now, () => 44);
        Assert.NotNull(second);
        Assert.True(second!.Value.Added);
        var dead = state.SetProfane(2, inside: false, alive: false, now, () => 45);
        Assert.NotNull(dead);
        Assert.Equal((ushort)44, dead!.Value.Buff);
        Assert.False(dead.Value.Added);

        var consecrated = new FlaskGrounds.State();
        consecrated.Configure(stationaryRadius: 18, lingerMs: 4000);
        Assert.True(consecrated.Add(new(FlaskGrounds.Kind.ConsecratedGround, 10, 10, 18, 100)));
        Assert.True(consecrated.OnConsecrated(alive: true, (10, 10), now));
        Assert.True(consecrated.OnConsecrated(alive: true, (100, 100), now + 3.9));
        Assert.False(consecrated.OnConsecrated(alive: true, (100, 100), now + 4));
    }
}
