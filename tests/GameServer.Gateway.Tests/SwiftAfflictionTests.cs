namespace GameServer.Gateway.Tests;

public sealed class SwiftAfflictionTests
{
    [Fact]
    public void Swift_affliction_shortens_damage_ailments_from_linked_skills_once()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var conn = fixture.Player;
        ushort Equip(string active, bool supported) => Assert.Single(supported
            ? fixture.Equip((active, 1), ("Swift Affliction Support", 1))
            : fixture.Equip((active, 1)));

        ushort fireball = Equip("Fireball", supported: false);
        var ignite = Game.SupportedIgnite(conn, fireball, spell: true, Game.CharacterPlayerStats(conn));
        fireball = Equip("Fireball", supported: true);
        var supportedIgnite = Game.SupportedIgnite(conn, fireball, spell: true, Game.CharacterPlayerStats(conn));
        Assert.Equal(ignite.Seconds * .75, supportedIgnite.Seconds, 6);
        Assert.True(supportedIgnite.Max > ignite.Max);

        ushort strike = Equip("Double Strike", supported: false);
        var bleed = Game.SupportedBleed(conn, strike, spell: false, Game.CharacterPlayerStats(conn), melee: true);
        var poison = Game.SupportedPoison(conn, strike, spell: false, Game.CharacterPlayerStats(conn));
        strike = Equip("Double Strike", supported: true);
        var player = Game.CharacterPlayerStats(conn);
        Assert.Equal(bleed.Seconds * .75, Game.SupportedBleed(conn, strike, spell: false, player, melee: true).Seconds, 6);
        Assert.Equal(poison.Seconds * .75, Game.SupportedPoison(conn, strike, spell: false, player).Seconds, 6);

        ushort puncture = Equip("Puncture", supported: false);
        double punctureDuration = Game.SupportedBleed(conn, puncture, spell: false, Game.CharacterPlayerStats(conn), melee: true).Seconds;
        puncture = Equip("Puncture", supported: true);
        Assert.Equal(punctureDuration * .75,
            Game.SupportedBleed(conn, puncture, spell: false, Game.CharacterPlayerStats(conn), melee: true).Seconds, 6);
    }

    [Fact]
    public void Swift_affliction_and_sadism_multiply_ailment_durations()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        ushort skill = Assert.Single(fixture.Equip(("Fireball", 1), ("Swift Affliction Support", 1), ("Sadism Support", 1)));
        var conn = fixture.Player;
        var ignite = Game.SupportedIgnite(conn, skill, spell: true, Game.CharacterPlayerStats(conn));
        Assert.Equal(4 * .75 * .30 / 1.60, ignite.Seconds, 6);
    }
}
