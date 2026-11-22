using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class SupportExecutionMultiplierTests
{
    [Fact]
    public void Greater_echo_applies_the_spell_stat_per_repeat()
    {
        int Own(string name) => name == "support_greater_spell_echo_spell_damage_+%_final_per_repeat" ? 10 : 0;
        Assert.Equal(1, Game.GreaterEchoHitMore(0, Own));
        Assert.Equal(1.1, Game.GreaterEchoHitMore(1, Own));
        Assert.Equal(1.2, Game.GreaterEchoHitMore(2, Own));
    }

    [Fact]
    public void Channel_damage_counts_completed_seconds_and_stops_at_sixty_percent()
    {
        Assert.Equal(1, Game.FocusedChannelMore(.99, 9));
        Assert.Equal(1.09, Game.FocusedChannelMore(1.99, 9));
        Assert.Equal(1.54, Game.FocusedChannelMore(6, 9));
        Assert.Equal(1.6, Game.FocusedChannelMore(7, 9));
    }

    static ushort Equip(ClientState conn, string active, string support, bool weapon, int quality = 0)
    {
        var items = Game.ItemsOf(conn);
        foreach (var previous in items.EquipmentSnapshot()) items.Remove(previous.Row, previous.Item.Id);
        var host = ItemCatalog.Create(Program.Items.Find(weapon
            ? "Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1"
            : "Metadata/Items/Armours/BodyArmours/BodyStr1")!);
        ItemSocket Socket(string name, int level, int gemQuality)
        {
            var gem = Program.Items.FindGem(name)!.Value;
            return new(SocketColour.White, ItemCatalog.Create(gem.Type) with
            {
                GemVariant = gem.Variant.Index,
                GemExperience = Program.Items.Gem(gem.Type)!.ExperienceFor(level),
                Quality = (byte)gemQuality,
            });
        }
        items.Add(weapon ? PlayerInventories.Weapon1 : PlayerInventories.BodyArmour, 0, 0,
            host with { Sockets = [Socket(active, 1, 0), Socket(support, 1, quality)], Links = [2] });
        return Game.GemSkillEffects(conn).Single().Id;
    }

    [Fact]
    public void Bloodthirst_adds_weapon_physical_damage_at_half_life_before_conversion()
    {
        var conn = SpellTotemExecutionTests.Connection("Bloodthirst" + Guid.NewGuid().ToString("N"));
        try
        {
            ushort strike = Equip(conn, "Molten Strike", "Bloodthirst", weapon: true, quality: 20);
            var supported = Game.SupportedSkill(conn, strike);
            Assert.Contains(supported.Supports, s => s.Effect.Id == "SupportBloodthirst");
            conn.PlayerLife = 501;
            var above = Game.SupportedHitInputsFor(conn, strike)!;
            conn.PlayerLife = 500;
            var low = Game.SupportedHitInputsFor(conn, strike)!;
            var coefficient = Game.SkillsFor(conn).DamageCoefficients(supported.Effect, supported.Level).Added;
            Assert.Equal(20 * coefficient / 10_000d, low.Raw[DamageType.Physical].Min - above.Raw[DamageType.Physical].Min, 8);
            Assert.Equal(20, low.Get("damage_+%") - above.Get("damage_+%"));
            Assert.True(Game.SupportedHitFrom(low, suppress: false)[DamageType.Fire].Min
                > Game.SupportedHitFrom(above, suppress: false)[DamageType.Fire].Min);

            strike = Equip(conn, "Frenzy", "Bloodthirst", weapon: false);
            conn.PlayerLife = 500;
            var unarmedLow = Game.SupportedHitInputsFor(conn, strike)!;
            conn.PlayerLife = 501;
            var unarmedAbove = Game.SupportedHitInputsFor(conn, strike)!;
            Assert.Equal(unarmedAbove.Raw[DamageType.Physical], unarmedLow.Raw[DamageType.Physical]);
        }
        finally { PlayerInventories.Forget(conn.Character!); }
    }
}
