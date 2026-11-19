using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// A Generosity aura does not affect its caster, but it is still running: the source record draws the aura art and the
// received record carries the skill-bar association. Only the self-buff icon is suppressed (display state 0), and both
// records must be removed and restored symmetrically — including when a Game module reload leaves old records behind.
public sealed class GenerosityAuraPresentationTests
{
    static AuraCatalog.Aura Aura(bool affectsSelf) => new(7, Program.Skills.EffectRow("Haste"), "Haste", "Haste",
        100, 155, 0, 50, Level: 20, SourceBuff: 101, AffectsSelf: affectsSelf);

    [Fact]
    public void Generosity_aura_keeps_both_records_and_hides_only_the_received_icon()
    {
        var generosity = Game.AuraBuffs(Aura(false)).ToArray();
        Assert.Equal(2, generosity.Length);
        Assert.Equal(0x21, generosity[0][22] & 0x21); // source: bit0 art, bit5 display state
        Assert.Equal(0x28, generosity[1][22] & 0x28); // received: bit3 active skill, bit5 display state

        var ordinary = Game.AuraBuffs(Aura(true)).ToArray();
        Assert.Equal(2, ordinary.Length);
        Assert.Equal(0x00, ordinary[0][22] & 0x08); // source still has no active-skill association
        Assert.Equal(0x08, ordinary[1][22] & 0x28); // received keeps bit3, no bit5 override
    }

    [Fact]
    public void A_module_reload_removes_both_records_of_an_old_generosity_aura()
    {
        var conn = new Player(new(1, "generosity-reload", 100, 100, 100, 100, null, []), 1, 1)
        {
            PlayerSent = true, PlayerObjectCreated = true, Closed = true,
        };
        var aura = Aura(false);
        conn.PresentedAuras[aura.SkillId] = aura;
        // With no live source (a reload) the old records are the only thing left to retire.
        var packets = Game.WithOwnAuraRecords(conn, () => []);
        Assert.Equal(2, packets.Length);
        static ushort Id(byte[] p) => (ushort)((p[12] << 8) | p[13]); // LoginWriter writes u16 big-endian
        Assert.All(packets, p => Assert.Equal(0x01c2, (p[0] << 8) | p[1])); // RemoveBuffId
        Assert.Contains(packets, p => Id(p) == aura.Buff);
        Assert.Contains(packets, p => Id(p) == aura.SourceBuff);
        Assert.Empty(conn.PresentedAuras);
    }
}
