namespace GameServer.Gateway.Tests;

public class MonsterSurroundTests
{
    static bool Open(int x, int y, int size) => true;

    [Fact]
    public void A_pack_from_one_side_spreads_around_the_player_instead_of_queueing_in_a_wedge()
    {
        // Twenty bodies of size 1 coming from the east, one behind the other.
        var askers = Enumerable.Range(0, 20).Select(i =>
            new MonsterSurround.Asker((uint)(i + 1), 120 + i * 3, 100 + (i % 3) - 1, 1, null)).ToList();
        var slots = MonsterSurround.Assign(askers, (100, 100), [], Open);

        Assert.Equal(20, slots.Count);
        // No two cells overlap (Chebyshev ≥ 1 + 1 + 2), and none is inside the player's body.
        var cells = slots.Values.ToList();
        for (int i = 0; i < cells.Count; i++)
        {
            Assert.True(Math.Max(Math.Abs(cells[i].X - 100), Math.Abs(cells[i].Y - 100)) >= MonsterSurround.ContactRadius(1) - 1);
            for (int j = i + 1; j < cells.Count; j++)
                Assert.True(Math.Max(Math.Abs(cells[i].X - cells[j].X), Math.Abs(cells[i].Y - cells[j].Y)) >= 4);
        }
        // The innermost ring is used on both sides of the player, not only on the side the pack came from.
        int contact = MonsterSurround.ContactRadius(1);
        var inner = cells.Where(c => Math.Sqrt(Math.Pow(c.X - 100, 2) + Math.Pow(c.Y - 100, 2)) <= contact + 1).ToList();
        Assert.True(inner.Count >= 5);
        Assert.Contains(inner, c => c.X < 100);
    }

    [Fact]
    public void A_slot_stays_while_it_is_free_and_the_player_stands()
    {
        var first = MonsterSurround.Assign([new(1, 130, 100, 1, null)], (100, 100), [], Open)[1];
        var again = MonsterSurround.Assign([new(1, first.X + 1, first.Y, 1, first)], (100, 100), [], Open)[1];
        Assert.Equal(first, again);
    }

    [Fact]
    public void Standing_bodies_and_unwalkable_cells_are_never_given_out()
    {
        var slot = MonsterSurround.Assign([new(1, 130, 100, 1, null)], (100, 100), [(104, 100, 1)],
            (x, y, _) => y >= 100)[1];
        Assert.True(slot.Y >= 100);
        Assert.True(Math.Max(Math.Abs(slot.X - 104), Math.Abs(slot.Y - 100)) >= 4);
    }
}
