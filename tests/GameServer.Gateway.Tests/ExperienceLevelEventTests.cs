using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Fact]
    public async Task A_failed_extra_point_grant_does_not_publish_points_before_the_save_commits()
    {
        using var game = new GatewayCharacter(level: 2, family: AscendancyFamily.Normal, choice: "Slayer");
        string saved = game.SavedJson;
        Saves.FailWrite = name => name == game.Character;
        try { await Assert.ThrowsAsync<IOException>(() => game.Command("/add_passive_points 1")); }
        finally { Saves.FailWrite = null; }

        Assert.Equal(saved, game.SavedJson);
        Assert.Equal((ushort)0, game.State.ExtraPassivePoints);

        await game.Command("/add_passive_points 1");
        Assert.Equal((ushort)1, game.State.ExtraPassivePoints);
        Assert.Equal((ushort)1, CharacterSaves.Load(game.Character)!.ExtraPassivePoints);
    }

    static void AssertExperiencePrefix(byte[] bytes, uint experience)
    {
        byte[] expected = InstanceEntities.PlayerExperienceUpdate(0x1000, experience);
        Assert.Equal(expected, bytes[..expected.Length]);
    }

    sealed class LevelControl
    {
        public List<ControlRequest> Requests { get; } = [];
        public int Level { get; private set; }

        public Task<ControlResponse> Call(ControlRequest request)
        {
            Requests.Add(request);
            Level = request.Level;
            return Task.FromResult(new ControlResponse { Ok = true, Level = Level });
        }
    }

    sealed class DelayedLevelControl(bool applyBeforeReply)
    {
        readonly Lock _gate = new();
        readonly List<ControlRequest> _requests = [];
        readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _level;
        public TaskCompletionSource<ControlRequest> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<ControlRequest> Requests { get { lock (_gate) return _requests.ToArray(); } }
        public int Level { get { lock (_gate) return _level; } }
        public void Release() => _release.TrySetResult(true);

        public async Task<ControlResponse> Call(ControlRequest request)
        {
            bool first;
            lock (_gate)
            {
                first = _requests.Count == 0;
                _requests.Add(request);
                if (!first || applyBeforeReply) _level = request.Level;
            }
            if (first)
            {
                First.TrySetResult(request);
                await _release.Task;
                if (!applyBeforeReply) lock (_gate) _level = request.Level;
            }
            return new ControlResponse { Ok = true, Level = request.Level };
        }
    }

    sealed partial class GatewayCharacter
    {
        public Task<(string? Error, byte[] Bytes)> CommandOn(ClientState state, string line) => Exchange(async (stream, cipher) =>
        {
            state.Commands = ProgramValue<ChatCommands>("Commands", "xp-grant-test", stream, cipher, state);
            Assert.True(await state.Commands.RunAsync(line));
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> CompleteGain(Program.ExperienceChange change,
            Func<ControlRequest, Task<ControlResponse>> control, ClientState? state = null) => Exchange(async (stream, cipher) =>
        {
            await Program.CompleteExperienceGain("xp-event-test", stream, cipher, state ?? State, change, "test", control);
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> Gain(uint amount,
            Func<ControlRequest, Task<ControlResponse>> control) => Exchange(async (stream, cipher) =>
        {
            await Program.GainExperience("xp-event-test", stream, cipher, State, amount, "test", control);
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> PublishGain(Program.ExperienceChange change) => Exchange(async (stream, cipher) =>
        {
            await Program.PublishExperienceChange("xp-event-test", stream, cipher, State, change, "test");
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> QueuedGain(Program.ExperienceChange change,
            Func<ControlRequest, Task<ControlResponse>> control, Func<Task> mutation) => Exchange(async (stream, cipher) =>
        {
            var gate = Program.SendGates.GetValue(cipher, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            var publication = Program.CompleteExperienceGain("xp-event-test", stream, cipher, State, change, "test", control);
            Assert.False(publication.IsCompleted);
            try { await mutation(); }
            finally { gate.Release(); }
            await publication;
            return null;
        });
    }
}
