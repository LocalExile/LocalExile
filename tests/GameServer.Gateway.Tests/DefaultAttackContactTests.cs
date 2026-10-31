namespace GameServer.Gateway.Tests;

[CollectionDefinition("Default attack contact environment", DisableParallelization = true)]
public sealed class DefaultAttackContactEnvironment { }

[Collection("Default attack contact environment")]
public class DefaultAttackContactTests : IDisposable
{
    readonly string? previousOffset = Environment.GetEnvironmentVariable("Game_HIT_OFFSET_MS");
    readonly string? previousAo = Environment.GetEnvironmentVariable("Game_PLAYER_AO");

    public DefaultAttackContactTests()
    {
        Environment.SetEnvironmentVariable("Game_HIT_OFFSET_MS", "150");
        Environment.SetEnvironmentVariable("Game_PLAYER_AO", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("Game_HIT_OFFSET_MS", previousOffset);
        Environment.SetEnvironmentVariable("Game_PLAYER_AO", previousAo);
    }

    [Fact]
    public void Stationary_default_attacks_use_the_native_keyframe_without_walk_in_compensation()
    {
        TestSaves.Ensure();
        string character = $"Contact{Guid.NewGuid():N}";
        var conn = new ClientState(new AreaLayout(1, "test", 300, 300, 10, 10, null, []), 1, 1)
            { Character = character };
        try
        {
            string ao = Program.Skills.ClassFor(conn.ClassRow).AnimatedObject;
            double native = new AnimationContacts().AttackContact(ao, Program.WeaponProfileFor(conn, activeSet: true));
            double expected = AttackTiming.ContactDelay(1, native, 0);
            Assert.Equal(expected, Program.DefaultAttackContactDelay(conn, 1, (10, 10), (10, 10), 2, 2), 6);
            // A wait with no movement is still not an approach; neither is an instantaneous relocation.
            Assert.Equal(expected, Program.DefaultAttackContactDelay(conn, 1, (10, 10), (10, 10), 2, 3), 6);
            Assert.Equal(expected, Program.DefaultAttackContactDelay(conn, 1, (10, 10), (20, 10), 2, 2), 6);
            // The production call boundary retains the configured compensation only after actual timed movement.
            Assert.Equal(AttackTiming.ContactDelay(1, native, 0.15),
                Program.DefaultAttackContactDelay(conn, 1, (10, 10), (20, 10), 2, 3), 6);
        }
        finally { PlayerInventories.Forget(character); }
    }
}
