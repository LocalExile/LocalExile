namespace GameServer.Gateway.Tests;

public class DefaultWandReleaseTimingTests
{
    [Theory]
    [InlineData(4097, 366)]
    [InlineData(4098, 233)]
    [InlineData(4106, 300)]
    [InlineData(4112, 200)]
    public void Ordinary_wand_release_uses_the_selected_native_contact_even_for_fast_attacks(int sequence, int contactMs)
    {
        TestSaves.Ensure();
        string character = $"WandTiming{Guid.NewGuid():N}";
        var conn = new ClientState(new AreaLayout(1, "wand-timing", 300, 300, 10, 10, null, []), 1, 1)
            { Character = character, ClassRow = SkillCatalog.WitchRow };
        var type = Assert.IsType<ItemCatalog.BaseType>(Program.Items.Find("Driftwood Wand"));
        PlayerInventories.For(character, Program.Items).Add(PlayerInventories.Weapon1, 0, 0,
            new(type.Hash, type.Components, type.VisualIdentity));
        try
        {
            foreach (double seconds in new[] { 1.2, 0.2, Program.TickSeconds })
            {
                double expected = seconds * contactMs / 600.0;
                Assert.Equal(expected,
                    Program.OrdinaryRangedContactDelay(conn, seconds, OrdinaryRangedAttack.Wand, (ushort)sequence), 9);
            }
        }
        finally { PlayerInventories.Forget(character); }
    }
}
