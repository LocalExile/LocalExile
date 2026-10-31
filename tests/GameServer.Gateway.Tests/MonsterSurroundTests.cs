using GameServer.Core.World;

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

    // A ranged chaser (a Sand Spitter: MonsterVarieties MaximumAttackDistance 30) stands off just inside its reach, where
    // its attack covers the player, instead of walking to the melee contact ring; a melee body beside it still does.
    [Fact]
    public void A_ranged_chaser_stands_off_just_inside_its_reach_while_a_melee_one_comes_to_the_contact_ring()
    {
        var spitter = new MonsterSurround.Asker(1, 160, 100, 1, null, Reach: 30, Ranged: true);
        var melee = new MonsterSurround.Asker(2, 160, 104, 1, null, Reach: 8);
        var slots = MonsterSurround.Assign([spitter, melee], (100, 100), [], Open);

        static double Distance((int X, int Y) c) => Math.Sqrt(Math.Pow(c.X - 100, 2) + Math.Pow(c.Y - 100, 2));
        Assert.InRange(Distance(slots[1]), 28, 30);
        Assert.True(slots[1].X > 100, "on its own side of the player");
        int contact = MonsterSurround.ContactRadius(1);
        Assert.InRange(Distance(slots[2]), contact - 1, contact + 1.5);

        // The stand-off slot is kept while it is still in range; a ranged body whose reach is the contact ring rings outward.
        var again = MonsterSurround.Assign([spitter with { Last = slots[1] }], (100, 100), [], Open);
        Assert.Equal(slots[1], again[1]);
        var pointBlank = MonsterSurround.Assign([new MonsterSurround.Asker(3, 160, 100, 1, null, Reach: 4, Ranged: true)], (100, 100), [], Open);
        Assert.InRange(Distance(pointBlank[3]), contact - 1, contact + 1.5);
    }
}
