namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Fact]
    public async Task Level_up_capture_cannot_drop_a_concurrent_allocation_or_let_its_bridge_be_refunded()
    {
        using var game = new GatewayCharacter(level: 10, family: AscendancyFamily.Normal, choice: "Slayer");
        Assert.Null((await game.Change([39725], allocate: true)).Error);
        // Stage the production capture/commit phases around another task's completed mutation: no timing window.
        var captured = Program.CaptureLevelChange(game.State, 11);
        Assert.False(captured.Trimmed);
        Assert.Null((await Task.Run(() => game.Change([24377], allocate: true))).Error);
        string allocated = game.SavedJson;

        Program.CommitLevelChange(game.State, captured);
        var refund = await game.Change([39725], allocate: false, refundPoints: true);

        Assert.NotNull(refund.Error); // 24377 still depends on this bridge, in runtime and in the durable tree.
        Assert.Equal(allocated, game.SavedJson);
        Assert.Equal(new ushort[] { 39725, 24377 }, game.State.Passives);
        Assert.Equal(new ushort[] { 39725, 24377 }, CharacterSaves.Load(game.Character)!.Passives);
        Assert.Equal((ushort)20, game.State.RefundPoints);
        Assert.Equal(11, game.State.Pools.Level);
        Assert.True(game.State.SavedPassiveProgress!.Matches(CharacterSaves.Load(game.Character)!));
        Assert.Equal(new ushort[] { 24377, 39725 }, PassivePacket(refund.Bytes, 2).Nodes);
    }

    sealed partial class GatewayCharacter
    {
        public Task<(string? Error, byte[] Bytes)> ApplyLevelOnly(int level) => Exchange(async (stream, cipher) =>
        {
            await ProgramValue<Task>("ApplyLevel", "level-concurrency-test", stream, cipher, State, level);
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> QueuedPassivePublication(Func<Task> mutation) =>
            Exchange(async (stream, cipher) =>
            {
                var gate = Program.SendGates.GetValue(cipher, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync();
                var publication = ProgramValue<Task>("SendPassives", "level-concurrency-test", stream, cipher, State);
                Assert.False(publication.IsCompleted);
                try { await mutation(); }
                finally { gate.Release(); }
                await publication;
                return null;
            });
    }
}
