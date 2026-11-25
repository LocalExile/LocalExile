using GameServer.Core.Protocol.Instance;
using Xunit;

namespace GameServer.Gateway.Tests;

public sealed class SimulationClockTests
{
    [Fact]
    public void SimulationClock_scales_elapsed_time_and_changes_rate_continuously()
    {
        const long freq = 1_000_000; // 1 tick = 1 us
        long now = 0;

        var normal = new SimulationClock(speed: 1.0, timestamp: () => now, frequency: freq, trackLive: false);
        var dynamic = new SimulationClock(speed: 1.0, timestamp: () => now, frequency: freq, trackLive: false);
        var fast100x = new SimulationClock(speed: 100.0, timestamp: () => now, frequency: freq, trackLive: false);

        normal.Start();
        dynamic.Start();
        fast100x.Start();

        now += freq; // 1.0 s wall time
        Assert.Equal(1000, normal.ElapsedMilliseconds);
        Assert.Equal(1000, dynamic.ElapsedMilliseconds);
        Assert.Equal(100_000, fast100x.ElapsedMilliseconds);

        // Switching 1x -> 4x mid-flight must NOT jump elapsed time; only subsequent wall time scales at 4x.
        dynamic.SetSpeed(4.0);
        Assert.Equal(1000, dynamic.ElapsedMilliseconds);
        now += freq; // +1.0 s wall time -> +4.0 s sim time = 5.0 s total
        Assert.Equal(5000, dynamic.ElapsedMilliseconds);

        // Pausing stops game time while wall time advances, and changing speed while paused stays continuous.
        dynamic.Stop();
        Assert.False(dynamic.IsRunning);
        now += 5 * freq;
        dynamic.SetSpeed(2.0);
        Assert.Equal(5000, dynamic.ElapsedMilliseconds);

        dynamic.Start();
        Assert.True(dynamic.IsRunning);
        now += freq / 2; // +0.5 s wall time at 2x -> +1.0 s sim time = 6.0 s total
        Assert.Equal(6000, dynamic.ElapsedMilliseconds);

        // Returning to 1x keeps elapsed continuous and resumes 1:1 pacing.
        dynamic.SetSpeed(1.0);
        Assert.Equal(6000, dynamic.ElapsedMilliseconds);
        now += freq;
        Assert.Equal(7000, dynamic.ElapsedMilliseconds);

        dynamic.Reset();
        Assert.False(dynamic.IsRunning);
        Assert.Equal(0, dynamic.ElapsedMilliseconds);

        dynamic.Restart();
        Assert.True(dynamic.IsRunning);
        now += freq / 2;
        Assert.Equal(500, dynamic.ElapsedMilliseconds);
    }

    [Fact]
    public async Task SimSpeed_command_updates_live_clocks_and_keeps_wire_frame_length_constant()
    {
        double frameSeconds = Program.FrameMs / 1000.0;
        byte[] packet = InstanceProtocol.FrameLength(Program.FrameMs);
        Assert.Equal((byte)0x02, packet[0]);
        Assert.Equal((byte)0xb8, packet[1]);
        Assert.Equal(Program.FrameMs, packet[2]);
        Assert.Equal(Program.FrameMs, AreaFrames.WallWaitMs(frameSeconds, 1.0), precision: 6);
        Assert.Equal(Program.FrameMs / 4.0, AreaFrames.WallWaitMs(frameSeconds, 4.0), precision: 6);
        Assert.Equal(Program.FrameMs / 100.0, AreaFrames.WallWaitMs(frameSeconds, 100.0), precision: 6);
        Assert.Equal(0.0, AreaFrames.WallWaitMs(-0.01, 4.0), precision: 6);

        double previous = Program.SimSpeed;
        try
        {
            var player = new Player(new(1, "sim-speed", 100, 100, 50, 50, null, []), 1, 1) { Instance = new() };
            var replies = new List<string>();
            player.Commands = new ChatCommands
            {
                Send = (_, _) => Task.CompletedTask,
                Reply = text => { replies.Add(text); return Task.CompletedTask; },
            };
            var wired = Program.Commands(player);
            Assert.True(await wired.RunAsync("/sim_speed 2"));
            Assert.Equal(2.0, Program.SimSpeed);
            Assert.Equal(2.0, player.Clock.Speed);
            Assert.Contains(replies, r => r.Contains("2x"));

            replies.Clear();
            Assert.True(await wired.RunAsync("/sim_speed"));
            Assert.Contains(replies, r => r.Contains("Simulation speed is 2x"));

            replies.Clear();
            Assert.True(await wired.RunAsync("/sim_speed 1"));
            Assert.Equal(1.0, Program.SimSpeed);
            Assert.Equal(1.0, player.Clock.Speed);
            Assert.Contains(replies, r => r.Contains("1x"));
        }
        finally
        {
            Program.SetSimSpeed(previous);
        }
    }
}
