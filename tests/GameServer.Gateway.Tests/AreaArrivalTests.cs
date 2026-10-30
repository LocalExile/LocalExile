using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class AreaArrivalTests
{
    const ushort Town = 0x235c, Coast = 0x7ad2, Strand = 0xf00a, MudFlats = 0x1234;

    // A 100 x 100 area whose walkable cells are x >= 50; spawn (70, 70); one exit to The Coast at (40, 20).
    static AreaLayout Layout(ushort hash = Coast, bool walkMap = true) => new(hash, "test", 100, 100, 70, 70,
        walkMap ? (x, y) => x >= 50 && x < 100 && y >= 0 && y < 100 : null,
        [(0x2000, new AreaTransitionObject(GridX: 40, GridY: 20, DestinationHash16: Town))]);

    [Fact]
    public void Coming_through_an_exit_arrives_on_a_walkable_cell_next_to_the_exit_back()
    {
        var (x, y) = Layout().Arrival(cameFrom: Town);
        Assert.True(x >= 50, $"({x}, {y}) is not walkable");
        Assert.InRange(Math.Max(Math.Abs(x - 40), Math.Abs(y - 20)), 6, 20);
    }

    [Fact]
    public void A_login_or_an_area_without_an_exit_back_arrives_at_the_spawn()
    {
        Assert.Equal((70, 70), Layout().Arrival(cameFrom: null));
        Assert.Equal((70, 70), Layout().Arrival(cameFrom: MudFlats));
    }

    [Fact]
    public void Without_a_walk_map_the_arrival_is_beside_the_exit()
    {
        Assert.Equal((48, 20), Layout(walkMap: false).Arrival(cameFrom: Town));
    }

    [Fact]
    public void Resurrect_at_checkpoint_revives_next_to_the_activated_waypoint()
    {
        var layout = Layout() with { Waypoint = new WaypointObject(80, 30) };
        var (x, y) = layout.Checkpoint(waypointActivated: true);
        Assert.True(x >= 50, $"({x}, {y}) is not walkable");
        Assert.InRange(Math.Max(Math.Abs(x - 80), Math.Abs(y - 30)), 6, 20);
    }

    [Fact]
    public void Without_an_activated_waypoint_the_checkpoint_is_where_the_player_entered()
    {
        Assert.Equal((70, 70), (Layout() with { Waypoint = new WaypointObject(80, 30) }).Checkpoint(waypointActivated: false));
        Assert.Equal((70, 70), Layout().Checkpoint(waypointActivated: true)); // the waypoint's place is not known
    }

    [Fact]
    public void The_Twilight_Strand_arrives_at_the_towns_one_way_entrance()
    {
        Assert.Equal((123, 308), Layout(hash: Town).Arrival(cameFrom: Strand));
    }

    // A generated layout's one-way entrance ("entranceNdisabled": The Submerged Passage's Depths return for a player
    // coming up from The Flooded Depths) when no exit leads back; an in-area gate to the same area does not count.
    [Fact]
    public void A_one_way_entrance_of_a_generated_layout_is_the_arrival_from_its_area()
    {
        var layout = Layout() with
        {
            EntranceFrom = from => from == MudFlats ? (80, 60) : null,
            AreaTransitions = [(0x2000, new AreaTransitionObject(GridX: 40, GridY: 20, DestinationHash16: Town)),
                (0x2001, new AreaTransitionObject(GridX: 90, GridY: 90, DestinationHash16: Coast) { TeleportTo = (60, 10) })],
        };
        var (x, y) = layout.Arrival(cameFrom: MudFlats);
        Assert.True(x >= 50, $"({x}, {y}) is not walkable");
        Assert.InRange(Math.Max(Math.Abs(x - 80), Math.Abs(y - 60)), 6, 20);
        Assert.Equal((70, 70), layout.Arrival(cameFrom: Coast));
    }
}
