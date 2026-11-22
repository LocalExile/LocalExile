using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class BloodMagicTests
{
    [Theory]
    [InlineData("SkillGemBloodreap", 0, 25)]
    [InlineData("SkillGemBloodreap", 5, 50)]
    [InlineData("SkillGemExsanguinate", 0, 16)]
    [InlineData("SkillGemCorruptingFever", 0, 78)]
    public void Native_life_cost_is_required_before_admission_and_paid_once(string gemName, int charges, uint expected)
    {
        string path = Path.Combine(Path.GetTempPath(), $"native-life-{Guid.NewGuid():N}.db");
        var previous = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var player = SpellTotemExecutionTests.PlayerOf(gemName);
        try
        {
            var wand = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!);
            var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + gemName)!);
            Game.ItemsOf(player).Add(PlayerInventories.Weapon1, 0, 0, wand with
            {
                Sockets = [new(SocketColour.White, gem)], Links = [1],
            });
            var skill = Game.GemSkillEffects(player).Single();
            player.BloodCharges = charges;
            player.PlayerLife = expected;
            Assert.False(Game.CanPaySkillCost(player, skill.Id));
            Assert.False(Game.PayMana(player, skill.Id, out _, out _));
            Assert.Equal(expected, player.PlayerLife);
            player.PlayerLife++;
            Assert.True(Game.CanPaySkillCost(player, skill.Id));
            Assert.True(Game.PayMana(player, skill.Id, out uint mana, out uint life));
            Assert.Equal(expected, life);
            Assert.Equal(0u, mana);
            Assert.Equal(1u, player.PlayerLife);
            Assert.Equal(1000u, player.PlayerMana);
        }
        finally
        {
            PlayerInventories.Forget(gemName);
            Program.SetGameLogic(previous!);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    [Fact]
    public void Reap_gains_a_charge_only_when_no_hit_enemy_dies()
    {
        Assert.Equal(1, BloodCharges.Settle(2, 1, 1, 5));
        Assert.Equal(3, BloodCharges.Settle(2, 0, 2, 5));
        Assert.Equal(2, BloodCharges.Settle(2, 0, 0, 5));
    }

    // Blood Magic removes all Mana and grants 10% more maximum Life (ReminderTextKeystoneBloodMagic).
    [Fact]
    public void Blood_magic_keystone_removes_mana_and_raises_life()
    {
        var stats = new Dictionary<uint, int>
        {
            [Stat.BaseMaximumLife] = 100,
            [Stat.MaximumLifePlusPct] = 50,
            [Stat.BaseMaximumMana] = 50,
        };
        var without = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1, Game.PassiveBonus(stats, default, default));
        stats[Stat.KeystoneBloodMagic] = 1;
        var with = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1, Game.PassiveBonus(stats, default, default));
        // (38 + 12 + 7 from strength + 100) x 150% = 235.5, then 10% more = 259.05.
        Assert.Equal(235u, without.Life);
        Assert.Equal(259u, with.Life);
        Assert.True(without.Mana > 0);
        Assert.Equal(0u, with.Mana);
    }

    // With Blood Magic an aura's Mana reservation is taken from Life instead, Petrified Blood's too.
    [Fact]
    public async Task Blood_magic_keystone_reserves_life()
    {
        using var f = new MinionUtilityTests.Fixture();
        bool bloodMagic = false;
        using var sources = GameEvents.PlayerStatSources.Add("Game", (p, stats) => !bloodMagic || !ReferenceEquals(p, f.Player)
            ? stats : new Dictionary<uint, int>(stats) { [Stat.KeystoneBloodMagic] = 1 });

        ushort purity = f.Equip(("Purity of Fire", 1)).Single();
        var mana = Game.SupportedReservationCost(f.Player, purity)!.Value;
        Assert.False(Game.ReservationContext(f.Player, purity).Life);
        Assert.True(mana.ManaPercent > 0);
        bloodMagic = true;
        f.Player.PlayerSkillsCache = null;
        var life = Game.SupportedReservationCost(f.Player, purity)!.Value;
        Assert.True(Game.ReservationContext(f.Player, purity).Life);
        Assert.Equal((0, 0, mana.ManaFlat, mana.ManaPercent),
            (life.ManaFlat, life.ManaPercent, life.LifeFlat, life.LifePercent));

        ushort id = f.Equip(("Petrified Blood", 1)).Single();
        var petrified = Game.SupportedSkill(f.Player, id);
        var cost = Game.SupportedReservationCost(f.Player, id)!.Value;
        Assert.True(await Game.TogglePetrifiedBlood(f.Player, id, petrified.Effect, "Petrified Blood", cost,
            petrified.Level, petrified.Quality));
        Assert.True(f.Player.PetrifiedBlood.ReservesLife);
        Assert.Equal(cost.LifePercent, f.Player.PetrifiedBlood.ManaPercent);
        Assert.True(Game.ReservedLife(f.Player) > 0);
    }

    // The Blood Magic keystone turns a skill's whole Mana cost into a Life cost: Tornado Shot's 9 Mana is paid as
    // 9 Life, Mana stays full, and no Lifetap buff comes with it (that buff belongs to the support gem).
}
