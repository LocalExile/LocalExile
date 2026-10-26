namespace GameServer.Gateway.Tests;

public class InstantLocationSkillTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly SkillCatalog Skills = new(Data());
    static int NoStats(string _) => 0;
    static ClientState Connection(Func<int, int, bool>? walkable = null) =>
        new(new AreaLayout(1, "test", 300, 300, 100, 100, walkable ?? ((_, _) => true), []), 1, 1)
        { PlayerSent = true, PlayerLife = 100 };

    [Theory]
    [InlineData("Frostblink", true, true)]
    [InlineData("FrostblinkAltX", false, false)]
    [InlineData("FrostblinkMercenary", false, false)]
    [InlineData("Fireball", false, false)]
    [InlineData("Haste", false, true)]
    [InlineData("RighteousFire", false, true)]
    [InlineData("PlayerMelee", false, false)]
    [InlineData("missing", false, false)]
    public void Catalog_classification_keeps_noninstant_and_unimplemented_movement_off_both_routes(
        string effectId, bool location, bool objectTarget)
    {
        int effect = Skills.EffectRow(effectId);
        Assert.Equal(location, InstantLocationSkill.Supported(Skills, effect));
        Assert.Equal(objectTarget, InstantLocationSkill.ObjectTargetSupported(Skills, effect));
    }

    [Theory]
    [InlineData(110u, 100u, 0, 132, 100)]
    [InlineData(200u, 100u, 0, 142, 100)]
    [InlineData(140u, 100u, 0, 140, 100)]
    [InlineData(90u, 100u, 0, 68, 100)]
    [InlineData(200u, 200u, 0, 130, 130)]
    [InlineData(0u, 0u, 0, 70, 70)]
    [InlineData(200u, 100u, 50, 163, 100)]
    public void Endpoint_uses_native_minimum_maximum_modifier_and_signed_vector_rounding(
        uint x, uint y, int pct, int expectedX, int expectedY) =>
        Assert.Equal(((int X, int Y)?)(expectedX, expectedY), InstantLocationSkill.Endpoint((100, 100), (x, y), pct));

    [Fact]
    public void Endpoint_matches_the_manual_native_ground_instant_resolver_captures()
    {
        Assert.Equal(((int X, int Y)?)(314, 208), InstantLocationSkill.Endpoint((290, 229), (300, 220), 0));
        Assert.Equal(((int X, int Y)?)(311, 220), InstantLocationSkill.Endpoint((279, 221), (310, 220), 0));
        Assert.Equal(((int X, int Y)?)(269, 220), InstantLocationSkill.Endpoint((311, 220), (200, 220), 0));
    }

    [Theory]
    [InlineData(100u, 100u, 0)]
    [InlineData(uint.MaxValue, 100u, 0)]
    [InlineData(100u, 0x80000000u, 0)]
    [InlineData(200u, 100u, int.MaxValue)]
    [InlineData(200u, 100u, -100)]
    [InlineData(200u, 100u, int.MinValue)]
    public void Degenerate_unsigned_and_unrepresentable_requests_fail_closed(uint x, uint y, int pct) =>
        Assert.Null(InstantLocationSkill.Endpoint((100, 100), (x, y), pct));

    [Fact]
    public void Captured_grid_validation_resolves_from_current_walk_without_replacing_an_action()
    {
        var conn = Connection();
        conn.ActionCount = 17; conn.ChannelSkill = 0x104; conn.NextAttackAt = 20;
        conn.Walk.Order(180, 100, conn.Clock.Elapsed.TotalSeconds);
        var destination = conn.Walk.Destination;
        var before = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        Assert.True(InstantLocationSkill.TryResolve(conn, conn.ProjectileEpoch, (250, 100), false, NoStats, out var from, out var to));
        var after = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        // This is a live stopwatch walk: full-suite scheduling may advance whole cells between observations.
        Assert.InRange(from.X, before.X, after.X);
        Assert.Equal(100, from.Y);
        Assert.Equal((from.X + 42, 100), to);
        Assert.Equal(destination, conn.Walk.Destination);
        Assert.Equal(17, conn.ActionCount); Assert.Equal((ushort)0x104, conn.ChannelSkill); Assert.Equal(20, conn.NextAttackAt);
        Assert.True(InstantLocationSkill.TryCommit(conn, conn.ProjectileEpoch, (140, 100), false, NoStats, out to));
        Assert.Equal((140, 100), to);
        Assert.Equal(to, conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds));
        Assert.Equal(to, conn.Walk.Destination);
        Assert.Equal(17, conn.ActionCount); Assert.Equal((ushort)0x104, conn.ChannelSkill); Assert.Equal(20, conn.NextAttackAt);
    }

    [Theory]
    [InlineData("town")]
    [InlineData("dead")]
    [InlineData("closed")]
    [InlineData("paused")]
    [InlineData("entering")]
    [InlineData("leaving")]
    [InlineData("pending")]
    [InlineData("old-area")]
    [InlineData("disabled")]
    public void Invalid_player_and_area_states_are_rejected_without_walk_or_action_mutation(string state)
    {
        var conn = Connection(); int epoch = conn.ProjectileEpoch;
        switch (state)
        {
            case "dead": conn.PlayerLife = 0; break;
            case "closed": conn.Closed = true; break;
            case "paused": conn.Clock.Stop(); break;
            case "entering": conn.PlayerSent = false; break;
            case "leaving": conn.LeavingArea = true; break;
            case "pending": conn.PendingTransfer = new(2, 0, false); break;
            case "old-area": conn.ProjectileEpoch++; break;
        }
        Assert.False(InstantLocationSkill.TryResolve(conn, epoch, (140, 100), state == "town",
            name => name == "your_movement_skills_are_disabled" && state == "disabled" ? 1 : 0, out _, out _));
        Assert.False(InstantLocationSkill.TryCommit(conn, epoch, (140, 100), state == "town",
            name => name == "your_movement_skills_are_disabled" && state == "disabled" ? 1 : 0, out _));
        Assert.Equal((100, 100), conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds));
        Assert.Equal(0, conn.ActionCount); Assert.Equal((ushort)0, conn.ChannelSkill);
        Assert.Empty(conn.SkillReadyAt);
    }

    [Fact]
    public void Queued_acceptance_rechecks_a_wall_or_area_change_after_the_request_was_validated()
    {
        var conn = Connection(); int epoch = conn.ProjectileEpoch;
        Assert.True(InstantLocationSkill.TryResolve(conn, epoch, (140, 100), false, NoStats, out _, out _));
        conn.Barrier = new HashSet<(int, int)> { (120, 100) };
        Assert.False(InstantLocationSkill.TryCommit(conn, epoch, (140, 100), false, NoStats, out _));
        conn.Barrier = new HashSet<(int, int)>();
        conn.TryBeginAreaChange(new(2, 0, false), out _);
        Assert.False(InstantLocationSkill.TryCommit(conn, epoch, (140, 100), false, NoStats, out _));
        Assert.Equal((100, 100), conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds));
        Assert.Equal(0, conn.ActionCount);
    }

    [Theory]
    [InlineData("unknown-grid")]
    [InlineData("wall")]
    [InlineData("barrier")]
    [InlineData("corner")]
    [InlineData("bounds")]
    [InlineData("occupied")]
    public void Unknown_or_blocked_terrain_is_rejected_instead_of_guessing_a_native_fallback(string state)
    {
        var layout = new AreaLayout(1, "test", 300, 300, 100, 100,
            state == "unknown-grid" ? null : (x, y) => state != "wall" || x != 120, []);
        var conn = new ClientState(layout, 1, 1) { PlayerSent = true, PlayerLife = 100 };
        if (state == "barrier") conn.Barrier = new HashSet<(int, int)> { (120, 100) };
        if (state == "corner") conn.Barrier = new HashSet<(int, int)> { (101, 100) };
        if (state == "occupied") conn.SetBlocker(10, 140, 100, ClientState.NpcBlockerSize);
        (uint X, uint Y) aim = state == "corner" ? (140u, 140u) : state == "bounds" ? (uint.MaxValue, 100u) : (140u, 100u);
        Assert.False(InstantLocationSkill.TryResolve(conn, conn.ProjectileEpoch, aim, false, NoStats, out _, out _));
    }
}
