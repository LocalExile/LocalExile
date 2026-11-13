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
        var conn = new ClientState(layout, 1, 1);
        var pet = new SpawnedPet(conn.NewWalk(5, 10, 10), 1);
        var to = (X: 65, Y: 10);
        var order = CosmeticsFeature.PetMove(conn, 123, pet, to, 1, following);
        if (following)
        {
            Assert.Equal(InstanceProtocol.Teleport(123, to.X, to.Y), order);
            Assert.Equal(to, pet.Walk.Position(1));
        }
        else Assert.True(pet.Walk.Destination.X < 30);

        var sameRoom = new SpawnedPet(conn.NewWalk(50, 10, 10), 1);
        var walk = CosmeticsFeature.PetMove(conn, 124, sameRoom, to, 1, following);
        Assert.Equal(InstanceProtocol.ActionStarted(124, 50, 10, 65, 10, 0x2909, 0xffff, 0x0408), walk);
        Assert.Equal(to, sameRoom.Walk.Destination);
    }
}
