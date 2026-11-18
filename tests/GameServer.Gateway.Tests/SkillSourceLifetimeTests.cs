using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

static class SkillSourceLifetimeCases
{
    public static void Shared_physical_markers_retire_old_leases_and_transfer_one_activation_writer()
    {
        var store = new GemSourceLifetimes.SourceStore();
        var key = new GemSkillIds.Gem(100, 0, 42);
        var marker = Assert.IsType<GemSourceLifetimes.SourceMarker>(store.Capture(key));
        var first = new GemSourceLifetimes();
        var second = new GemSourceLifetimes();
        var old = first.Capture(key, marker);
        var peer = second.Capture(key, marker);
        Assert.Same(old, first.Capture(key, marker));
        Guid owner = Guid.NewGuid(), arrival = Guid.NewGuid();
        Assert.True(store.ClaimActivation("golem", 10, marker, owner));
        Assert.Equal(owner, store.ActivationWriter("golem", 10)!.Value.Owner);
        Assert.True(store.ClaimActivation("golem", 10, marker, arrival));
        Assert.Equal(arrival, store.ActivationWriter("golem", 10)!.Value.Owner);
        store.RetireSocket(100, 1);
        Assert.True(first.IsCurrent(old));
        store.RetireSocket(100, 0);
        Assert.False(first.IsCurrent(old));
        Assert.True(peer.Retired);
        Assert.Null(store.ActivationWriter("golem", 10));
        Assert.False(store.ClaimActivation("golem", 10, marker, owner));
        var replacement = Assert.IsType<GemSourceLifetimes.SourceMarker>(store.Capture(key));
        Assert.NotSame(marker, replacement);
        Assert.NotSame(old, first.Capture(key, replacement));
        Assert.True(old.Retired);
        store.RetireOwner();
        Assert.True(replacement.Retired);
        Assert.Null(store.Capture(key));
    }

    public static void Accepted_socket_edits_retire_the_source_and_skill_ids_separate_source_kinds()
    {
        var items = new PlayerInventories();
        var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemFireball")!);
        var body = ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
        { Sockets = [new ItemSocket(SocketColour.White, gem)] };
        var host = items.Add(PlayerInventories.BodyArmour, 0, 0, body);
        var key = new GemSkillIds.Gem(host.Id, 0, 42);
        var marker = Assert.IsType<GemSourceLifetimes.SourceMarker>(items.CaptureGemSourceLifetime(key));
        Assert.Null(items.Unsocket(PlayerInventories.BodyArmour, host.Id, 1, () => 200, out _));
        Assert.False(marker.Retired);
        Assert.NotNull(items.Unsocket(PlayerInventories.BodyArmour, host.Id, 0, () => 200, out _));
        Assert.True(marker.Retired);

        var ids = new GemSkillIds();
        ushort[] pool = [10, 11, 12, 13];
        var socket = new GemSkillIds.Gem(1, 0, 42);
        var worn = new GemSkillIds.Gem(1, 0, 42, GemSkillIds.SourceKind.WornGrant);
        var passive = new GemSkillIds.Gem(1, 0, 42, GemSkillIds.SourceKind.PassiveGrant);
        var assigned = ids.Assign([socket, worn, passive], pool);
        Assert.Equal(3, assigned.Distinct().Count());
        var movedSocket = socket with { Host = 2 };
        var movedWorn = worn with { Host = 2 };
        var next = ids.Assign([movedSocket, movedWorn, passive], pool);
        Assert.Equal(assigned[0], next[0]);
        Assert.NotEqual(assigned[1], next[1]);
        Assert.Equal(assigned[2], next[2]);
    }
}
