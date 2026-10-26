using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class InstantObjectSkillTests
{
    static int NoStats(string _) => 0;
    internal static ClientState Connection() => new(new AreaLayout(1, "test", 400, 400, 280, 220, (_, _) => true, []), 1, 1)
        { PlayerSent = true, PlayerLife = 100, PlayerMana = 100, ActionCount = 17, ChannelSkill = 0x104,
            ChannelSkillSequence = 3, NextAttackAt = 20 };

    internal static SpawnedMonster Target(ClientState conn, int x = 300, int y = 220, int size = 3)
    {
        var target = new SpawnedMonster("target", new(x, y, 1, width: 400, height: 400), 0, 4, 100)
            { Variety = new(0, "target", "target", "base", null, null, ObjectSize: size) };
        conn.Monsters.Add(42, target);
        return target;
    }

    [Theory]
    [InlineData(300, 220, 312, 220, 317, 220, 317, 220)]
    [InlineData(330, 220, 330, 220, 335, 220, 322, 220)]
    [InlineData(300, 240, 303, 243, 307, 247, 307, 247)]
    [InlineData(300, 230, 309, 234, 314, 236, 314, 236)]
    public void Size_aware_trace_and_outer_clamp_match_manual_native_object_confirmations(
        int x, int y, int sx, int sy, int ex, int ey, int finalX, int finalY)
    {
        var trace = Assert.IsType<((int X, int Y) Start, (int X, int Y) End)>(
            InstantLocationSkill.ObjectAim((280, 220), (x, y), 1, 2));
        Assert.Equal(((sx, sy), (ex, ey)), trace);
        var conn = Connection(); Target(conn, x, y);
        Assert.True(InstantLocationSkill.TryResolveObject(conn, conn.ProjectileEpoch, 42, false, NoStats,
            out var from, out var to, out var aim));
        Assert.Equal((280, 220), from); Assert.Equal((x, y), aim); Assert.Equal((finalX, finalY), to);
        Assert.Equal((280, 220), conn.Walk.Destination);
        Assert.Equal(17, conn.ActionCount); Assert.Equal((ushort)0x104, conn.ChannelSkill);
        Assert.Equal(100u, conn.PlayerMana); Assert.Empty(conn.SkillReadyAt);
    }

    [Theory]
    [InlineData(1, 315)] // Positioned size 0 is a known size, not an unknown target.
    [InlineData(3, 317)]
    [InlineData(7, 321)]
    public void Catalog_object_size_is_converted_to_native_size_once(int objectSize, int endX)
    {
        var conn = Connection(); Target(conn, size: objectSize);
        Assert.True(InstantLocationSkill.TryResolveObject(conn, conn.ProjectileEpoch, 42, false, NoStats,
            out _, out var to, out _));
        Assert.Equal((endX, 220), to);
    }

    [Fact]
    public void Reflected_partial_terrain_trace_rejects_instead_of_guessing_native_fallback()
    {
        Assert.Equal(((248, 220), (243, 220)), InstantLocationSkill.ObjectAim((280, 220), (260, 220), 1, 2));
        var conn = Connection(); Target(conn, 260, 220);
        conn.Barrier = new HashSet<(int, int)> { (245, 220) };
        // The manual native capture retained 248 then fell back to 264. That terrain search is unsupported here.
        Assert.False(InstantLocationSkill.TryCommitObject(conn, conn.ProjectileEpoch, 42, false, NoStats, out _, out _));
        Assert.Equal((280, 220), conn.Walk.Destination); Assert.Empty(conn.SkillReadyAt);
    }

    [Fact]
    public void A_partial_object_extension_beyond_the_final_clamp_is_still_rejected()
    {
        var conn = Connection(); Target(conn, 330, 220);
        conn.Barrier = new HashSet<(int, int)> { (333, 220) };
        Assert.True(InstantLocationSkill.TryResolve(conn, conn.ProjectileEpoch, (335, 220), false, NoStats,
            out _, out var ground));
        Assert.Equal((322, 220), ground); // The ground path never reaches the blocked object extension.
        Assert.False(InstantLocationSkill.TryResolveObject(conn, conn.ProjectileEpoch, 42, false, NoStats,
            out _, out _, out _));
    }

    [Fact]
    public void Travel_distance_modifies_the_outer_clamp_after_the_size_aware_object_trace()
    {
        var conn = Connection(); Target(conn, 330, 220);
        Assert.True(InstantLocationSkill.TryResolveObject(conn, conn.ProjectileEpoch, 42, false,
            name => name == "skill_maximum_travel_distance_+%" ? 50 : 0, out _, out var to, out _));
        Assert.Equal((335, 220), to);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("dead")]
    [InlineData("zero-life")]
    [InlineData("unknown-size")]
    [InlineData("invalid-size")]
    [InlineData("oversized")]
    [InlineData("moving")]
    [InlineData("coincident")]
    [InlineData("clamped-inside-target")]
    public void Unsupported_targets_fail_without_placement_or_action_side_effects(string state)
    {
        var conn = Connection();
        var target = Target(conn, state == "coincident" ? 280 : state == "clamped-inside-target" ? 320 : 300,
            size: state == "invalid-size" ? 0 : state == "oversized" ? 129 : state == "clamped-inside-target" ? 10 : 3);
        if (state == "missing") conn.Monsters.Clear();
        if (state == "dead") target.Dead = true;
        if (state == "zero-life") target.Life = 0;
        if (state == "unknown-size") target = conn.Monsters[42] = new("unknown", target.Walk, 0, 4, 100);
        if (state == "moving") target.Walk.Order(380, 220, conn.Clock.Elapsed.TotalSeconds);
        Assert.False(InstantLocationSkill.TryCommitObject(conn, conn.ProjectileEpoch, 42, false, NoStats, out _, out _));
        Assert.Equal((280, 220), conn.Walk.Destination); Assert.Equal(17, conn.ActionCount);
        Assert.Equal((ushort)0x104, conn.ChannelSkill); Assert.Equal(20, conn.NextAttackAt);
        Assert.Equal(100u, conn.PlayerMana); Assert.Empty(conn.SkillReadyAt);
    }

    [Fact]
    public void Gate_rechecks_current_player_and_stopped_target_without_replacing_the_original_action()
    {
        var conn = Connection(); var target = Target(conn);
        Assert.True(InstantLocationSkill.TryResolveObject(conn, conn.ProjectileEpoch, 42, false, NoStats,
            out _, out var initial, out _));
        Assert.Equal((317, 220), initial);
        conn.Walk.Teleport(285, 220, conn.Clock.Elapsed.TotalSeconds);
        target.Walk.Teleport(315, 220, conn.Clock.Elapsed.TotalSeconds);
        Assert.True(InstantLocationSkill.TryCommitObject(conn, conn.ProjectileEpoch, 42, false, NoStats,
            out var aim, out var to));
        Assert.Equal((315, 220), aim); Assert.Equal((322, 220), to);
        Assert.Equal(to, conn.Walk.Destination); Assert.Equal(17, conn.ActionCount);
        Assert.Equal((ushort)0x104, conn.ChannelSkill); Assert.Equal((ushort)3, conn.ChannelSkillSequence);
    }

    [Theory]
    [InlineData("target-walk")]
    [InlineData("target-death")]
    [InlineData("pause")]
    [InlineData("transfer")]
    [InlineData("player-death")]
    public void A_preflight_success_does_not_authorize_a_later_invalid_confirmation(string change)
    {
        var conn = Connection(); var target = Target(conn); int epoch = conn.ProjectileEpoch;
        Assert.True(InstantLocationSkill.TryResolveObject(conn, epoch, 42, false, NoStats, out _, out _, out _));
        if (change == "target-walk") target.Walk.Order(380, 220, conn.Clock.Elapsed.TotalSeconds);
        if (change == "target-death") target.Life = 0;
        if (change == "pause") conn.Clock.Stop();
        if (change == "transfer") conn.TryBeginAreaChange(new(2, 0, false), out _);
        if (change == "player-death") conn.PlayerLife = 0;
        Assert.False(InstantLocationSkill.TryCommitObject(conn, epoch, 42, false, NoStats, out _, out _));
        Assert.Equal((280, 220), conn.Walk.Destination); Assert.Equal(17, conn.ActionCount);
    }
}
