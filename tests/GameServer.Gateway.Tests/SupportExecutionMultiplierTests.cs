using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class SupportExecutionMultiplierTests
{
    [Fact]
    public void Greater_echo_applies_the_spell_stat_per_repeat()
    {
        int Own(uint name) => name == Stat.SupportGreaterSpellEchoSpellDamagePlusPctFinalPerRepeat ? 10 : 0;
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

    static ushort Equip(Player player, string active, string support, bool weapon, int quality = 0,
        string? additionalSupport = null)
    {
        var items = Game.ItemsOf(player);
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
        return Game.GemSkillEffects(player).Single().Id;
    }

    [Fact]
    public void Bloodthirst_adds_weapon_physical_damage_at_half_life_before_conversion()
    {
        var player = SpellTotemExecutionTests.PlayerOf("Bloodthirst" + Guid.NewGuid().ToString("N"));
        try
        {
            ushort strike = Equip(player, "Molten Strike", "Bloodthirst", weapon: true, quality: 20);
            var supported = Game.SupportedSkill(player, strike);
            Assert.Contains(supported.Supports, s => s.Effect.Id == "SupportBloodthirst");
            player.PlayerLife = 501;
            var above = Game.SupportedHitInputsFor(player, strike)!;
            player.PlayerLife = 500;
            var low = Game.SupportedHitInputsFor(player, strike)!;
            var coefficient = Game.SkillsFor(player).DamageCoefficients(supported.Effect, supported.Level).Added;
            Assert.Equal(20 * coefficient / 10_000d, low.Raw[DamageType.Physical].Min - above.Raw[DamageType.Physical].Min, 8);
            Assert.Equal(20, low.Get(Stat.DamagePlusPct) - above.Get(Stat.DamagePlusPct));
            Assert.True(Game.SupportedHitFrom(low, suppress: false)[DamageType.Fire].Min
                > Game.SupportedHitFrom(above, suppress: false)[DamageType.Fire].Min);

            strike = Equip(player, "Frenzy", "Bloodthirst", weapon: false);
            player.PlayerLife = 500;
            var unarmedLow = Game.SupportedHitInputsFor(player, strike)!;
            player.PlayerLife = 501;
            var unarmedAbove = Game.SupportedHitInputsFor(player, strike)!;
            Assert.Equal(unarmedAbove.Raw[DamageType.Physical], unarmedLow.Raw[DamageType.Physical]);
        }
        finally { PlayerInventories.Forget(player.Character.Name!); }
    }

    [Fact]
    public void Paid_channel_applies_Focused_cost_and_Infused_hit_reduction_while_active()
    {
        var player = SpellTotemExecutionTests.PlayerOf("FocusedChannel" + Guid.NewGuid().ToString("N"));
        var previousLifetime = Game.CombatLifetime;
        var previousLogic = Program.GameLogicOrNull;
        var lifetime = new ModuleLifetime("Game");
        string path = Path.Combine(Path.GetTempPath(), $"paid-channel-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
            ushort skill = Equip(player, "Lightning Tendrils", "Focused Channelling", weapon: false,
                additionalSupport: "Infused Channelling");
            var supported = Game.SupportedSkill(player, skill);
            Assert.Contains(supported.Supports, s => s.Effect.Id == "SupportFocusedChannelling");
            Assert.Contains(supported.Supports, s => s.Effect.Id == "SupportInfusedChannelling");
            Assert.Equal(20, supported.Stats.GetValueOrDefault(Stat.SupportFocusChannelCostPlusPctFinalPerSecondChannellingUpTo100Pct));
            using var statsHook = GameEvents.PlayerStatSources.Add("Game", (p, stats) =>
            {
                if (p != player) return stats;
                stats[Stat.BaseCostPlusPct] = 400;
                stats[Stat.CannotBeStunned] = 1;
                return stats;
            });
            var monster = new SpawnedMonster("test", new(100, 100, 0), 100, 100, 1000);
            var attack = new MonsterAttack(0, 1, 10, DamagePacket.Of(DamageType.Chaos, 100, 100)) { Secondary = true };
            int TakeHit()
            {
                player.PlayerLife = 1000;
                double now = Game.AreaTime(player);
                return Game.CommitMonsterHitLocked(player, 1, monster, attack, now,
                    (100, 100), (100, 100), checkRange: false)!.Damage;
            }
            int baseline = TakeHit();
            player.ChannelSkill = skill;
            Assert.Equal(baseline, TakeHit());
            Assert.True(Game.PayMana(player, skill, out uint start, out uint life));
            Assert.Equal(0u, life);
            Assert.True(start >= 5);
            Game.BeginPaidChannel(player, skill, supported.Effect, player.ActionCount, 1);
            Assert.Equal((int)Math.Round(baseline * .86), TakeHit());

            foreach (var (time, multiplier) in new[] { (.99, 1d), (1d, 1.2), (2.99d, 1.4), (5d, 2d), (9d, 2d), (0d, 1d) })
            {
                uint manaBefore = player.PlayerMana;
                Assert.True(Game.PayChannelTick(player, skill, time, out uint paid, out life, out int hitTaken));
                Assert.Equal((uint)Math.Round(start * multiplier), paid);
                Assert.Equal(0u, life);
                Assert.Equal(-14, hitTaken);
                Assert.Equal(manaBefore - paid, player.PlayerMana);
            }

            player.PlayerMana = start * 2 - 1;
            Assert.False(Game.PayChannelTick(player, skill, 5, out _, out _, out int refusedHitTaken));
            Assert.Equal(0, refusedHitTaken);
            Assert.Equal(start * 2 - 1, player.PlayerMana);
            Game.ChannelReleased(player, Game.AreaTime(player));
            Assert.Equal(baseline, TakeHit());
            ushort unlinked = Equip(player, "Winter Orb", "Increased Area of Effect", weapon: false);
            player.PlayerMana = 1000;
            Assert.True(Game.PayMana(player, unlinked, out uint before, out _));
            player.PlayerMana = 1000;
            Assert.True(Game.PayChannelTick(player, unlinked, 9, out uint later, out _, out int unlinkedHitTaken));
            Assert.Equal(before, later);
            Assert.Equal(0, unlinkedHitTaken);
        }
        finally
        {
            PlayerInventories.Forget(player.Character.Name!);
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
