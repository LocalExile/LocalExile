using GameServer.Core.Protocol.Instance;
using GameServer.Modules.CosmeticsAndPets;

namespace GameServer.Gateway.Tests;

public class PetRoomTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_pet_rejoins_its_owner_across_disconnected_rooms_only_when_following(bool following)
    {
        var layout = new AreaLayout(1, "arena", 80, 20, 5, 10, (x, _) => x != 30, []);
        var player = new Player(layout, 1, 1);
        var pet = new SpawnedPet(player.NewWalk(5, 10, 10), 1);
        var to = (X: 65, Y: 10);
        var order = CosmeticsFeature.PetMove(player, 123, pet, to, 1, following);
        if (following)
        {
            Assert.Equal(InstanceProtocol.Teleport(123, to.X, to.Y), order);
            Assert.Equal(to, pet.Walk.Position(1));
        }
        else Assert.True(pet.Walk.Destination.X < 30);

        var sameRoom = new SpawnedPet(player.NewWalk(50, 10, 10), 1);
        var walk = CosmeticsFeature.PetMove(player, 124, sameRoom, to, 1, following);
        Assert.Equal(InstanceProtocol.ActionStarted(124, 50, 10, 65, 10, 0x2909, 0xffff, 0x0408), walk);
        Assert.Equal(to, sameRoom.Walk.Destination);
    }

    [Fact]
    public void Pet_speed_changes_match_the_client_and_preserve_an_in_flight_walk_after_unload()
    {
        var player = new Player(new AreaLayout(1, "pets", 300, 50, 10, 10, null, []), 1, 1);
        const uint id = 123;
        foreach (bool following in new[] { false, true })
        {
            var pet = new SpawnedPet(player.NewWalk(10, 10, 37), 1);
            player.Pets[id] = pet;
            double baseline = GameEvents.ResolveMovementSpeed(player, id, 37);
            using var speed = GameEvents.MovementSpeed.Add("Game", (c, actor, value) =>
                ReferenceEquals(c, player) && actor == id ? value * 2 : value);
            Assert.Equal(baseline * 2, GameEvents.ResolveMovementSpeed(player, id, 37));
            Assert.Equal(baseline, GameEvents.ResolveMovementSpeed(player, id + 1, 37));
            CosmeticsFeature.PetMove(player, id, pet, (158, 10), 0, following);
            Assert.Equal(74, pet.Walk.Speed);
            Assert.Equal(2, pet.Walk.ArrivesAt);
            var before = pet.Walk.Position(1);
            speed.Dispose();
            CosmeticsFeature.PetSpeedUpdate(player, id, pet, 1);
            Assert.Equal(before, pet.Walk.Position(1));
            Assert.Equal(37, pet.Walk.Speed);
            Assert.Equal(3, pet.Walk.ArrivesAt);
        }
    }
}
