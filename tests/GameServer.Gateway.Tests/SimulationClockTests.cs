using GameServer.Core.Protocol.Instance;
using Xunit;

namespace GameServer.Gateway.Tests;

public sealed class SimulationClockTests
{
    [Fact]
    public void SimulationClock_scales_elapsed_time_and_preserves_pause_resume()
    {
        const long freq = 1_000_000; // 1 tick = 1 us
        long now = 0;

        var normal = new SimulationClock(speed: 1.0, timestamp: () => now, frequency: freq);
        var fast4x = new SimulationClock(speed: 4.0, timestamp: () => now, frequency: freq);
        var fast100x = new SimulationClock(speed: 100.0, timestamp: () => now, frequency: freq);

        normal.Start();
        fast4x.Start();
        fast100x.Start();

        now += freq; // 1.0 s wall time
        Assert.Equal(1000, normal.ElapsedMilliseconds);
        Assert.Equal(4000, fast4x.ElapsedMilliseconds);
        Assert.Equal(100_000, fast100x.ElapsedMilliseconds);

        // Pausing stops game time while wall time advances, and resuming continues cleanly.
        fast4x.Stop();
        Assert.False(fast4x.IsRunning);
        now += 5 * freq; // 5.0 s wall time paused
        Assert.Equal(4000, fast4x.ElapsedMilliseconds);

        fast4x.Start();
        Assert.True(fast4x.IsRunning);
        now += freq / 2; // 0.5 s wall time -> +2.0 s sim time
        Assert.Equal(6000, fast4x.ElapsedMilliseconds);

        fast4x.Reset();
        Assert.False(fast4x.IsRunning);
        Assert.Equal(0, fast4x.ElapsedMilliseconds);

        fast4x.Restart();
        Assert.True(fast4x.IsRunning);
        now += freq / 4; // 0.25 s wall time -> +1.0 s sim time
        Assert.Equal(1000, fast4x.ElapsedMilliseconds);
    }

    [Fact]
    public void AreaFrames_wall_wait_scales_while_wire_frame_length_stays_constant()
    {
        double frameSeconds = Program.FrameMs / 1000.0;

        // S->C 0x2b8 always reports the deterministic frame delta (default 33 ms) regardless of simulation speed.
        byte[] packet = InstanceProtocol.FrameLength(Program.FrameMs);
        Assert.Equal((byte)0x02, packet[0]);
        Assert.Equal((byte)0xb8, packet[1]);
        Assert.Equal(Program.FrameMs, packet[2]);

        // Wall-clock wait shrinks inversely with simulation speed.
        Assert.Equal(Program.FrameMs, AreaFrames.WallWaitMs(frameSeconds, 1.0), precision: 6);
        Assert.Equal(Program.FrameMs / 4.0, AreaFrames.WallWaitMs(frameSeconds, 4.0), precision: 6);
        Assert.Equal(Program.FrameMs / 100.0, AreaFrames.WallWaitMs(frameSeconds, 100.0), precision: 6);
        Assert.Equal(0.0, AreaFrames.WallWaitMs(0.0, 4.0), precision: 6);
        Assert.Equal(0.0, AreaFrames.WallWaitMs(-0.01, 4.0), precision: 6);
    }
}
