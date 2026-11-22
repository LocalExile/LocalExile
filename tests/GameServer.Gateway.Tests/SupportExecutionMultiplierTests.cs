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

    [Fact]
    public void Focused_Channelling_charges_each_elapsed_second_and_refuses_an_unaffordable_tick()
    {
        var conn = SpellTotemExecutionTests.Connection("FocusedChannel" + Guid.NewGuid().ToString("N"));
        try
        {
            ushort orb = Equip(conn, "Winter Orb", "Focused Channelling", weapon: false);
            var supported = Game.SupportedSkill(conn, orb);
            Assert.Contains(supported.Supports, s => s.Effect.Id == "SupportFocusedChannelling");
            Assert.Equal(20, supported.Stats.GetValueOrDefault(Program.Stats.Key(
                "support_focus_channel_cost_+%_final_per_second_channelling_up_to_100%")!.Value));
            conn.PlayerStats[Program.Stats.Key("base_cost_+%")!.Value] = 400;
            Assert.True(Game.PayMana(conn, orb, out uint start, out uint life));
            Assert.Equal(0u, life);
            Assert.True(start >= 5);

            foreach (var (time, multiplier) in new[] { (.99, 1d), (1d, 1.2), (2.99d, 1.4), (5d, 2d), (9d, 2d), (0d, 1d) })
            {
                uint manaBefore = conn.PlayerMana;
                Assert.True(Game.PayChannelTick(conn, orb, time, out uint paid, out life));
                Assert.Equal((uint)Math.Round(start * multiplier), paid);
                Assert.Equal(0u, life);
                Assert.Equal(manaBefore - paid, conn.PlayerMana);
            }

            conn.PlayerMana = start * 2 - 1;
            Assert.False(Game.PayChannelTick(conn, orb, 5, out _, out _));
            Assert.Equal(start * 2 - 1, conn.PlayerMana);
            ushort unlinked = Equip(conn, "Winter Orb", "Increased Area of Effect", weapon: false);
            Assert.True(Game.PayMana(conn, unlinked, out uint before, out _));
            conn.PlayerMana = 1000;
            Assert.True(Game.PayChannelTick(conn, unlinked, 9, out uint later, out _));
            Assert.Equal(before, later);
        }
        finally { PlayerInventories.Forget(conn.Character!); }
    }
}
