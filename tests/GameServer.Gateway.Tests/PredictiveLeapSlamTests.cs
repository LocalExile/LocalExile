using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public sealed class PredictiveLeapSlamTests
{
    [Fact]
    public void Only_the_predictive_owners_positive_sequence_leap_omits_the_corrective_start_echo()
    {
        var player = PlayerOf(new());
        foreach (bool lockstep in new[] { false, true })
        foreach (string? effect in new[] { "LeapSlam", "LeapSlamAltX", "ShieldCharge", "HeavyStrike", null })
        foreach (ushort sequence in new ushort[] { 0, 0x1038, 0x7fff, 0x8000, 0xfffe, 0xffff })
        {
            player.Connection.LockstepClient = lockstep;
            Assert.Equal(!lockstep && effect == "LeapSlam" && sequence < 0x8000,
                Game.PredictsLeapSlam(player, effect, sequence));
        }
    }

    static Player PlayerOf(InstanceState instance) =>
        new(new(0, "Delve_Main", 2048, 2048, 1041, 651, null, []), 0, 1) { Connection = { LockstepClient = false }, Instance = instance, PlayerLife = 50 };
}
