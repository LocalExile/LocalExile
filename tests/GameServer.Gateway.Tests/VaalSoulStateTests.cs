using GameServer.Core.Protocol.Instance;
using System.Text.Json;

namespace GameServer.Gateway.Tests;

public class VaalSoulStateTests
{
    [Fact]
    public void Sorted_distinct_nonfull_rows_share_one_soul_per_kill_and_stop_at_capacity()
    {
        var state = new VaalSoulState();
        state.Reconcile([(9, 2), (3, 1), (9, 2), (20, 0)]);
        Assert.True(state.Gain()); Assert.Equal(new VaalSoulBudget(3, 1, 1), state.Read()[0]);
        Assert.True(state.Gain()); Assert.True(state.Gain()); Assert.False(state.Gain());
        Assert.Equal(new VaalSoulBudget[] { new(3, 1, 1), new(9, 2, 2), new(20, 0, 0) }, state.Read());
    }

}
