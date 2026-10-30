using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class InstanceRegistryTests
{
    const ushort Town = 0x235c, Coast = 0x7ad2;

    sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void A_town_has_one_instance_for_everyone()
    {
        var reg = new InstanceRegistry();
        var a = reg.Enter(Town, "A");
        var b = reg.Enter(Town, "B");
        var c = reg.EnterNew(Town, "C");
        Assert.Same(a, b);
        Assert.Same(a, c);
        Assert.Single(reg.List(Town));
        Assert.True(a.IsTown);
        Assert.Equal(1u, a.Seed);
    }

    [Fact]
    public void Non_town_areas_can_have_many_instances_and_a_plain_entry_returns_to_the_previous_one()
    {
        var reg = new InstanceRegistry { SeedFor = _ => 7 };
        var first = reg.Enter(Coast, "A");
        reg.Enter(Town, "A");                 // leaves the Coast instance (it stays alive, empty)
        var again = reg.Enter(Coast, "A");
        Assert.Same(first, again);
        var fresh = reg.EnterNew(Coast, "A");
        Assert.NotSame(first, fresh);
        Assert.Equal(2, reg.List(Coast).Count);
        Assert.Equal(7u, fresh.Seed);
        Assert.Same(first, reg.EnterExisting(first.Id, "B"));
        Assert.Contains("B", first.Players);
        Assert.Contains("A", fresh.Players);
    }

    [Fact]
    public void A_new_instance_can_be_given_its_seed_but_a_town_keeps_seed_1()
    {
        var reg = new InstanceRegistry { SeedFor = _ => 7 };
        Assert.Equal(3117172586u, reg.EnterNew(Coast, "A", seed: 3117172586).Seed);
        Assert.Equal(7u, reg.EnterNew(Coast, "A").Seed);
        Assert.Equal(1u, reg.EnterNew(Town, "A", seed: 42).Seed);
    }

    [Fact]
    public void An_empty_instance_expires_after_its_lifetime()
    {
        var clock = new FakeClock();
        var reg = new InstanceRegistry(clock) { Lifetime = TimeSpan.FromMinutes(6) };
        var coast = reg.Enter(Coast, "A");
        reg.Leave(coast.Id, "A");
        clock.Now += TimeSpan.FromMinutes(5);
        Assert.Single(reg.List(Coast));                       // still kept
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.Empty(reg.List(Coast));                        // gone after 6 minutes
        Assert.Null(reg.EnterExisting(coast.Id, "A"));
        Assert.NotSame(coast, reg.Enter(Coast, "A"));         // a new one
    }

    [Fact]
    public void An_occupied_instance_never_expires()
    {
        var clock = new FakeClock();
        var reg = new InstanceRegistry(clock);
        var coast = reg.Enter(Coast, "A");
        clock.Now += TimeSpan.FromHours(1);
        Assert.Same(coast, reg.EnterExisting(coast.Id, "B"));
    }

    [Fact]
    public void Time_left_is_the_full_lifetime_while_occupied_then_counts_down()
    {
        var clock = new FakeClock();
        var reg = new InstanceRegistry(clock) { Lifetime = TimeSpan.FromMinutes(6) };
        var coast = reg.Enter(Coast, "A");
        clock.Now += TimeSpan.FromMinutes(10);
        Assert.Equal(TimeSpan.FromMinutes(6), reg.TimeLeft(coast));
        reg.Leave(coast.Id, "A");
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(TimeSpan.FromMinutes(4), reg.TimeLeft(coast));
    }

    [Fact]
    public void A_restarted_gateway_empties_every_instance_and_starts_their_expiry()
    {
        // A Gateway that stopped mid-connection never sent "leave": its players stayed in their instances for good.
        var clock = new FakeClock();
        var reg = new InstanceRegistry(clock) { Lifetime = TimeSpan.FromMinutes(6) };
        var coast = reg.Enter(Coast, "A");
        var emptied = reg.Enter(Coast, "B");
        reg.Leave(emptied.Id, "B");
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.Equal(1, reg.LeaveAll());                      // only A was still inside
        Assert.Empty(coast.Players);
        Assert.Equal(TimeSpan.FromMinutes(6), reg.TimeLeft(coast));
        Assert.Equal(TimeSpan.FromMinutes(5), reg.TimeLeft(emptied)); // an empty one keeps its own expiry
        clock.Now += TimeSpan.FromMinutes(6);
        Assert.Empty(reg.List(Coast));
    }

    [Theory]
    [InlineData("Hardcore")]
    public void Towns_lists_and_explicit_entries_are_partitioned_by_full_league(string league)
    {
        var reg = new InstanceRegistry();
        var standard = reg.Enter(Town, "StandardPlayer", "Standard");
        var other = reg.Enter(Town, "OtherPlayer", league);
        Assert.NotSame(standard, other);
        Assert.Same(other, reg.Enter(Town, "ThirdPlayer", league.ToLowerInvariant()));
        Assert.Equal(other, Assert.Single(reg.List(Town, "OtherPlayer", league)));
        Assert.Null(reg.EnterExisting(standard.Id, "OtherPlayer", league));
        Assert.DoesNotContain("OtherPlayer", standard.Players);
        Assert.Contains("OtherPlayer", other.Players);
    }

    [Fact]
    public void A_character_changing_leagues_cannot_reuse_its_previous_instances()
    {
        var reg = new InstanceRegistry();
        var hardcore = reg.Enter(Coast, "Hero", "Hardcore Allflame");
        var normal = reg.Enter(Coast, "Hero", "Allflame");
        Assert.NotSame(hardcore, normal);
        Assert.Empty(hardcore.Players);
        Assert.Null(reg.EnterExisting(hardcore.Id, "Hero", "Allflame"));
        Assert.Same(normal, reg.Enter(Coast, "Hero", "Allflame"));
    }

    [Theory]
    [InlineData(Coast)]
    public void Ssf_non_town_instances_are_private_even_between_characters_in_the_same_league(ushort area)
    {
        const string league = "SSF Allflame";
        var reg = new InstanceRegistry();
        var a = reg.Enter(area, "A", league, solo: true);
        var b = reg.Enter(area, "B", league, solo: true);
        Assert.NotSame(a, b);
        Assert.Equal(a, Assert.Single(reg.List(area, "A", league, solo: true)));
        Assert.Null(reg.EnterExisting(a.Id, "B", league, solo: true));
        Assert.Null(reg.EnterExisting(a.Id, "B", league, solo: false));
        reg.Leave(a.Id, "A");
        Assert.Same(a, reg.Enter(area, "a", league, solo: true));
        Assert.Same(a, reg.EnterExisting(a.Id, "A", league, solo: true));
    }

    [Fact]
    public void Ssf_towns_can_be_shared_but_not_with_trade_characters()
    {
        var reg = new InstanceRegistry();
        var a = reg.Enter(Town, "A", "SSF Standard", solo: true);
        Assert.Same(a, reg.Enter(Town, "B", "SSF Standard", solo: true));
        Assert.NotSame(a, reg.Enter(Town, "C", "Standard"));
    }

    [Fact]
    public void Reusing_a_deleted_ssf_name_does_not_inherit_the_old_characters_instance()
    {
        var reg = new InstanceRegistry();
        const string league = "SSF Allflame", firstId = "original-id", replacementId = "new-id";
        var old = reg.Enter(Coast, "ReusableName", league, solo: true, soloIdentity: firstId);
        reg.Leave(old.Id, "ReusableName");
        var replacement = reg.Enter(Coast, "ReusableName", league, solo: true, soloIdentity: replacementId);
        Assert.NotSame(old, replacement);
        Assert.Null(reg.EnterExisting(old.Id, "ReusableName", league, solo: true, soloIdentity: replacementId));
        Assert.Equal(replacement, Assert.Single(reg.List(Coast, "ReusableName", league, solo: true,
            soloIdentity: replacementId)));
    }

    [Fact]
    public void An_explicit_instance_must_match_the_requested_area_without_moving_the_character()
    {
        var reg = new InstanceRegistry();
        var town = reg.Enter(Town, "A");
        var coast = reg.Enter(Coast, "B");
        Assert.Null(reg.EnterExisting(coast.Id, "A", expectedArea: Town));
        Assert.Contains("A", town.Players);
        Assert.DoesNotContain("A", coast.Players);
    }

    [Fact]
    public void The_dev_protocol_instance_manager_also_partitions_shared_towns_by_full_league()
    {
        var manager = new InstanceManager();
        var normal = manager.Enter(Guid.NewGuid(), "1_1_town", league: "Standard");
        var hardcore = manager.Enter(Guid.NewGuid(), "1_1_town", league: "Hardcore");
        Assert.NotSame(normal, hardcore);
        Assert.Same(hardcore, manager.Enter(Guid.NewGuid(), "1_1_town", league: "hardcore"));
    }
}
