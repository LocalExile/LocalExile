using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class ItemEffectRuleTests
{
    [Fact]
    public void Kill_rules_match_the_original_independent_rolls_and_refreshes()
    {
        // Invalid data must fail before a candidate world snapshot is used by combat.
        var world = GameData.Current;
        var mark = Assert.Single(world.FxRules, rule => rule.Id == "granted_mark");
        var impossible = mark with { When = [("block", "{}", null)] };
        var error = Assert.Throws<InvalidDataException>(() =>
            ItemEffectRules.Validate(world with { FxRules = [impossible] }));
        Assert.Contains(mark.Id, error.Message);
        ItemEffectRules.Validate(world);

        var player = new Player(new(1, "onslaught-rules", 100, 100, 100, 100, null, []), 1, 1);
        var stats = new Dictionary<uint, int>();
        void Compare()
        {
            Game.ConfigureKillOnslaught(player, stats.GetValueOrDefault);
            var actual = Game.KillOnslaughtOf(player);
            var original = new KillOnslaught { Rolls = KillOnslaught.From(stats.GetValueOrDefault) };
            Assert.Equal(original.Rolls.ToArray(), actual.Rolls.ToArray());
            var expectedRandom = new Random(1375);
            var actualRandom = new Random(1375);
            actual.Until = double.NegativeInfinity;
            for (int i = 0; i < 100; i++)
            {
                double now = i * 0.25;
                Assert.Equal(original.Kill(now, expectedRandom), actual.Kill(now, actualRandom));
                Assert.Equal(original.Until, actual.Until);
            }
            Assert.Equal(expectedRandom.Next(), actualRandom.Next());
        }
        Compare();
        stats[Stat.ChanceToGainOnslaughtOnKillPct] = 25;
        stats[Stat.ChanceToGainOnslaughtOnKillFor4SecondsPct] = 100;
        stats[Stat.ChanceToGainOnslaughtOnKillFor10SecondsPct] = 50;
        Compare();
        stats[Stat.OnslaughtTimeGrantedOnKillMs] = 6000;
        stats[Stat.GainOnslaughtOnKillMsWhileAffectedByHaste] = 7500;
        Compare();
        stats[Stat.OnslaughtBuffDurationOnKillMs] = 8000;
        Compare();
    }

    [Fact]
    public void Ring_swap_casts_each_granted_mark_through_the_same_hit_rule()
    {
        using var battle = new ArrowRainAndMirageTests.Battle("Fireball");
        var player = battle.Player;
        player.Clock.Start();
        var items = Game.ItemsOf(player);
        var random = new Random(1375);
        var stats = new Dictionary<uint, int>();
        SpawnedMonster Target(uint id, MonsterRarity rarity) =>
            player.Area.Monsters[id] = new("mark target", new(108, 100, 0), 0, 0, 2000)
                { Hostile = true, Rarity = rarity, ObjectId = id };
        var rare = Target(401, MonsterRarity.Rare);
        var unique = Target(402, MonsterRarity.Unique);
        var normal = Target(403, MonsterRarity.Normal);
        var magic = Target(404, MonsterRarity.Magic);
        Item Ring(string mod) => ItemCatalog.Create(Program.Items.Find("Iron Ring")!) with
        {
            Rarity = ItemRarity.Rare, Mods = [Program.Items.Mods.ById(mod)!.Roll(ModSection.Explicit, random)]
        };
        void Hit(SpawnedMonster monster, double now, bool spell = false, bool proxy = false) =>
            Game.AilmentsOnHitLocked(player, monster.ObjectId, monster, DamagePacket.Empty, spell, false, 100,
                stats, null, now, random, playerHit: !proxy);
        var ring = items.Add(PlayerInventories.Ring1, 0, 0, Ring("CurseOnHitAssassinsMarkNewUber1"));
        if (Environment.GetEnvironmentVariable("Game_EFFECT_RULES") == "0")
        {
            uint mana = player.PlayerMana;
            Hit(rare, 1);
            Assert.DoesNotContain(Game.GemSkillEffects(player), s => s.Name == "AssassinsMarkTriggered");
            Assert.Empty(rare.Curses.All);
            Assert.Equal(mana, player.PlayerMana);
            return;
        }
        var grant = Assert.Single(Game.GemSkillEffects(player), s => s.Name == "AssassinsMarkTriggered");
        Assert.Equal(8, grant.Level);
        Assert.NotNull(Game.GemSkillInstanceDisabled(player, grant.Effect, grant.Id));
        Assert.Null(Game.GemSkillInstanceDisabled(player, grant.Effect, grant.Id, triggered: true));
        uint initial = player.PlayerMana;
        Hit(normal, 1);
        Hit(magic, 1);
        Hit(rare, 1, proxy: true);
        Assert.Empty(rare.Curses.All);
        Assert.Empty(normal.Curses.All);
        Assert.Empty(magic.Curses.All);
        battle.StatOverrides[Stat.YourSpellsAreDisabled] = 1;
        Hit(rare, 1);
        Assert.Empty(rare.Curses.All);
        Assert.Equal(initial, player.PlayerMana);
        battle.StatOverrides.Remove(Stat.YourSpellsAreDisabled);
        player.PlayerMana = 0;
        Hit(rare, 1);
        Assert.Empty(rare.Curses.All);
        Assert.False(player.SkillReadyAt.ContainsKey(grant.Effect));
        player.PlayerMana = initial;
        Hit(rare, 1);
        Assert.Equal("AssassinsMark", Assert.Single(rare.Curses.All).Value.Effect);
        Assert.Equal(initial - 24, player.PlayerMana);
        Assert.Equal(1.5, player.SkillReadyAt[grant.Effect], 6);
        rare.Life = 0;
        Hit(unique, 1.49);
        Assert.Empty(unique.Curses.All);
        Hit(unique, 1.5, spell: true);
        Assert.Equal("AssassinsMark", Assert.Single(unique.Curses.All).Value.Effect);
        Assert.Equal(initial - 48, player.PlayerMana);

        items.Change(PlayerInventories.Ring1, ring.Id, _ => Ring("CurseOnHitPoachersMarkNewUber2__"));
        rare.Life = rare.MaxLife;
        rare.Curses.Clear();
        Hit(rare, 3);
        Assert.Empty(rare.Curses.All); // The previous ring's living marked enemy still counts.
        unique.Life = 0;
        Hit(rare, 3, spell: true);
        var second = Assert.Single(Game.GemSkillEffects(player), s => s.Name == "PoachersMarkTriggered");
        Assert.Equal(12, second.Level);
        var poacher = Assert.Single(rare.Curses.All).Value;
        Assert.Equal(("PoachersMark", second.Id), (poacher.Effect, poacher.Skill));
        Assert.Equal(initial - 48 - 27, player.PlayerMana);
        Hit(rare, 5);
        Assert.Equal(initial - 48 - 27, player.PlayerMana);
        items.Remove(PlayerInventories.Ring1, ring.Id);
        rare.Curses.Clear();
        Hit(rare, 6);
        Assert.Empty(rare.Curses.All);
    }
}
