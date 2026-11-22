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

    static ushort Equip(ClientState conn, string active, string support, bool weapon, int quality = 0,
        string? additionalSupport = null)
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
        ItemSocket[] sockets = additionalSupport is null
            ? [Socket(active, 1, 0), Socket(support, 1, quality)]
            : [Socket(active, 1, 0), Socket(support, 1, quality), Socket(additionalSupport, 1, 20)];
        items.Add(weapon ? PlayerInventories.Weapon1 : PlayerInventories.BodyArmour, 0, 0,
            host with { Sockets = sockets, Links = [(byte)sockets.Length] });
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
    public void Paid_channel_applies_Focused_cost_and_Infused_hit_reduction_while_active()
    {
        var conn = SpellTotemExecutionTests.Connection("FocusedChannel" + Guid.NewGuid().ToString("N"));
        var previousLifetime = Game.CombatLifetime;
        var previousLogic = Program.GameLogicOrNull;
        var lifetime = new ModuleLifetime("Game");
        string path = Path.Combine(Path.GetTempPath(), $"paid-channel-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
            ushort skill = Equip(conn, "Lightning Tendrils", "Focused Channelling", weapon: false,
                additionalSupport: "Infused Channelling");
            var supported = Game.SupportedSkill(conn, skill);
            Assert.Contains(supported.Supports, s => s.Effect.Id == "SupportFocusedChannelling");
            Assert.Contains(supported.Supports, s => s.Effect.Id == "SupportInfusedChannelling");
            Assert.Equal(20, supported.Stats.GetValueOrDefault(Program.Stats.Key(
                "support_focus_channel_cost_+%_final_per_second_channelling_up_to_100%")!.Value));
            conn.PlayerStats[Program.Stats.Key("base_cost_+%")!.Value] = 400;
            conn.PlayerStats[Program.Stats.Key("cannot_be_stunned")!.Value] = 1;
            var monster = new SpawnedMonster("test", new(100, 100, 0), 100, 100, 1000);
            var attack = new MonsterAttack(0, 1, 10, DamagePacket.Of(DamageType.Chaos, 100, 100)) { Secondary = true };
            int TakeHit()
            {
                conn.PlayerLife = 1000;
                double now = conn.Clock.Elapsed.TotalSeconds;
                lock (conn.Monsters)
                    return Game.CommitMonsterHitLocked("test", conn, 1, monster, attack, now,
                        (100, 100), (100, 100), checkRange: false)!.Damage;
            }
            int baseline = TakeHit();
            conn.ChannelSkill = skill;
            Assert.Equal(baseline, TakeHit());
            Assert.True(Game.PayMana(conn, skill, out uint start, out uint life));
            Assert.Equal(0u, life);
            Assert.True(start >= 5);
            lock (conn.Monsters) Game.BeginPaidChannel(conn, skill, supported.Effect, conn.ActionCount, 1);
            Assert.Equal((int)Math.Round(baseline * .86), TakeHit());

            foreach (var (time, multiplier) in new[] { (.99, 1d), (1d, 1.2), (2.99d, 1.4), (5d, 2d), (9d, 2d), (0d, 1d) })
            {
                uint manaBefore = conn.PlayerMana;
                Assert.True(Game.PayChannelTick(conn, skill, time, out uint paid, out life, out int hitTaken));
                Assert.Equal((uint)Math.Round(start * multiplier), paid);
                Assert.Equal(0u, life);
                Assert.Equal(-14, hitTaken);
                Assert.Equal(manaBefore - paid, conn.PlayerMana);
            }

            conn.PlayerMana = start * 2 - 1;
            Assert.False(Game.PayChannelTick(conn, skill, 5, out _, out _, out int refusedHitTaken));
            Assert.Equal(0, refusedHitTaken);
            Assert.Equal(start * 2 - 1, conn.PlayerMana);
            Game.ChannelReleased(conn, conn.Clock.Elapsed.TotalSeconds);
            Assert.Equal(baseline, TakeHit());
            ushort unlinked = Equip(conn, "Winter Orb", "Increased Area of Effect", weapon: false);
            conn.PlayerMana = 1000;
            Assert.True(Game.PayMana(conn, unlinked, out uint before, out _));
            conn.PlayerMana = 1000;
            Assert.True(Game.PayChannelTick(conn, unlinked, 9, out uint later, out _, out int unlinkedHitTaken));
            Assert.Equal(before, later);
            Assert.Equal(0, unlinkedHitTaken);
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
