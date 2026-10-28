using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class InstanceStoreTests
{
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    readonly Clock _clock = new();
    InstanceStore NewStore() => new(_clock, TimeSpan.FromMinutes(6));
    const long Created = 1_000;

    static WorldItemObject Sword(int x = 10) => new(x, 20, new Item(0xd9b1a12f, ["Base"], 0x40a1));

    [Fact]
    public void A_dropped_item_is_still_there_once_after_leaving_and_coming_back()
    {
        var store = NewStore();
        var first = store.Enter(4, Created);
        uint id = first.Ground.Add(Sword());
        store.Leave(first);

        var again = store.Enter(4, Created);

        var (gotId, item) = Assert.Single(again.Ground.All());
        Assert.Equal(id, gotId);
        Assert.Equal(10, item.GridX);
    }

    [Fact]
    public void Ssf_dropped_items_cannot_be_transferred_through_a_shared_town_even_after_relogin()
    {
        var store = NewStore();
        var first = store.Enter(4, Created, "FirstSSF");
        uint id = first.Ground.Add(Sword());
        store.Leave(first);
        var other = store.Enter(4, Created, "OtherSSF");
        Assert.Empty(other.Ground.All());
        Assert.Null(other.Ground.Take(id));
        Assert.Empty(store.Enter(4, Created).Ground.All());
        var again = store.Enter(4, Created, "firstssf");
        Assert.Same(first, again);
        Assert.Equal(id, Assert.Single(again.Ground.All()).Id);
    }

    [Fact]
    public void Ssf_private_combat_instances_share_neither_players_nor_ground()
    {
        var registry = new InstanceRegistry(_clock);
        var first = registry.Enter(0xabcd, "First", "SSF Standard", solo: true, soloIdentity: "first-id");
        var other = registry.Enter(0xabcd, "Other", "SSF Standard", solo: true, soloIdentity: "other-id");
        Assert.NotEqual(first.Id, other.Id);
        var store = NewStore();
        var a = store.Enter(first.Id, Created, "first-id");
        var b = store.Enter(other.Id, Created, "other-id");
        a.PublishPlayer(new(1, 7, new("First", 0, 10, 20, 100, 100), 10, 20, 100));
        uint drop = a.Ground.Add(Sword());

        Assert.Empty(b.PlayersExcept(2));
        Assert.Null(b.Player(1));
        Assert.Empty(b.Ground.All());
        Assert.Null(b.Ground.Take(drop));
    }

    [Fact]
    public void Expiring_one_ssf_world_retains_the_other_town_players_and_ordinary_sharing_is_unchanged()
    {
        var store = NewStore();
        var a = store.Enter(4, Created, "FirstSSF");
        var b = store.Enter(4, Created, "OtherSSF");
        var peer = new InstancePlayer(2, 7, new("Other", 0, 25, 20, 100, 100), 25, 20, 100);
        b.PublishPlayer(peer);
        a.Ground.Add(Sword());
        store.Leave(a);
        _clock.Now += TimeSpan.FromMinutes(6);

        var returned = store.Enter(4, Created, "FirstSSF");
        Assert.NotSame(a, returned);
        Assert.Empty(returned.Ground.All());
        Assert.Same(peer, returned.Player(2));
        returned.RemovePlayer(2);
        Assert.Null(b.Player(2));

        var ordinary = store.Enter(9, Created);
        ordinary.Ground.Add(Sword());
        ordinary.PublishPlayer(peer);
        var ordinaryAgain = store.Enter(9, Created);
        Assert.Same(ordinary, ordinaryAgain);
        Assert.Single(ordinaryAgain.Ground.All());
        Assert.Same(peer, ordinaryAgain.Player(2));
    }

    [Fact]
    public void Shared_presence_cannot_survive_full_expiry_or_an_auth_instance_id_reuse()
    {
        var store = NewStore();
        var a = store.Enter(4, Created, "FirstSSF");
        var b = store.Enter(4, Created, "OtherSSF");
        a.PublishPlayer(new(1, 7, new("First", 0, 10, 20, 100, 100), 10, 20, 100));
        Assert.Single(b.PlayersExcept(2));
        Assert.Empty(store.Enter(4, Created + 1, "OtherSSF").PlayersExcept(2));
        Assert.Empty(store.Enter(5, Created, "OtherSSF").PlayersExcept(2));

        store.Leave(a);
        store.Leave(b);
        _clock.Now += TimeSpan.FromMinutes(6);
        var fresh = store.Enter(4, Created, "FirstSSF");
        Assert.NotSame(a, fresh);
        Assert.Empty(fresh.PlayersExcept(2));
    }

    [Fact]
    public void Ground_items_get_ids_from_their_own_range()
    {
        var ground = NewStore().Enter(4, Created).Ground;
        Assert.Equal(InstanceGround.FirstItemId, ground.Add(Sword()));
        Assert.Equal(InstanceGround.FirstItemId + 1, ground.Add(Sword()));
    }

    [Fact]
    public void Only_one_of_two_connections_picks_up_an_item()
    {
        var store = NewStore();
        var a = store.Enter(4, Created);
        var b = store.Enter(4, Created);
        uint id = a.Ground.Add(Sword());

        Assert.NotNull(a.Ground.Take(id));
        Assert.Null(b.Ground.Take(id));
        Assert.Empty(b.Ground.All());
    }

    [Fact]
    public void The_instance_is_wiped_once_it_has_been_empty_for_its_lifetime()
    {
        var store = NewStore();
        var first = store.Enter(4, Created);
        first.Ground.Add(Sword());
        store.Leave(first);

        _clock.Now += TimeSpan.FromMinutes(5);
        var stillThere = store.Enter(4, Created);
        Assert.Single(stillThere.Ground.All());
        store.Leave(stillThere);

        _clock.Now += TimeSpan.FromMinutes(6);
        Assert.Empty(store.Enter(4, Created).Ground.All());
    }

    [Fact]
    public void A_start_weapon_is_put_on_an_instance_s_ground_once_per_character()
    {
        var store = NewStore();
        var weapon = Sword();   // the character's StartWeapon: the same item on every entry
        var first = store.Enter(4, Created);
        uint? id = first.PlaceStartWeapon("Witch", weapon);
        store.Leave(first);

        var again = store.Enter(4, Created);
        Assert.NotNull(id);
        Assert.Null(again.PlaceStartWeapon("witch", new WorldItemObject(11, 20, weapon.Item)));
        Assert.Equal(id, Assert.Single(again.Ground.All()).Id);
        Assert.True(again.IsStartWeapon("Witch", id.Value));
        Assert.False(again.IsStartWeapon("Other", id.Value));
        // Picked up: it is not put back while the instance lasts.
        Assert.NotNull(again.Ground.Take(id.Value));
        Assert.Null(again.PlaceStartWeapon("Witch", weapon));
        Assert.Empty(again.Ground.All());
        // Another character in the same instance gets its own.
        Assert.NotNull(again.PlaceStartWeapon("Other", Sword()));
    }

    [Fact]
    public void A_new_character_with_a_deleted_one_s_name_gets_its_own_start_weapon()
    {
        var state = NewStore().Enter(4, Created);
        uint old = state.PlaceStartWeapon("Witch", Sword())!.Value;
        state.Ground.Take(old);

        // Deleted and created again: a new start weapon item.
        uint? fresh = state.PlaceStartWeapon("Witch", Sword());

        Assert.NotNull(fresh);
        Assert.True(state.IsStartWeapon("Witch", fresh.Value));
        Assert.False(state.IsStartWeapon("Witch", old));
    }

    [Fact]
    public void A_dropped_item_is_not_the_start_weapon()
    {
        var state = NewStore().Enter(4, Created);
        uint dropped = state.Ground.Add(Sword());
        uint weapon = state.PlaceStartWeapon("Witch", Sword())!.Value;

        Assert.False(state.IsStartWeapon("Witch", dropped));
        Assert.True(state.IsStartWeapon("Witch", weapon));
    }

    [Fact]
    public void An_instance_id_reused_after_an_Auth_restart_starts_empty()
    {
        var store = NewStore();
        store.Enter(4, Created).Ground.Add(Sword());

        Assert.Empty(store.Enter(4, Created + 1).Ground.All());
    }
}
