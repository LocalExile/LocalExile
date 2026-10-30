namespace GameServer.Gateway.Tests;

public class QuestBarrierTests
{
    // Two open rooms (x 0–19 and 40–59, y 0–19) joined by a corridor at y 8–11; everything else is rock.
    const int W = 60, H = 20;
    static bool Walkable(int x, int y) => x < 20 || x >= 40 || (y >= 8 && y <= 11);

    [Fact]
    public void A_wall_line_across_the_corridor_closes_it()
    {
        // The wall stands in the rock above the corridor and points down across it (angle π/2).
        var cells = QuestBarriers.Cells(Walkable, W, H, (30, 2), MathF.PI / 2, from: (5, 10), guarded: [(55, 10)]);
        // The corridor's cells within 2.5 of x = 30, and nothing in the rooms or the rock.
        Assert.Equal(5 * 4, cells.Count);
        Assert.All(cells, c => Assert.True(c.X is >= 28 and <= 32 && c.Y is >= 8 and <= 11));
    }

}
