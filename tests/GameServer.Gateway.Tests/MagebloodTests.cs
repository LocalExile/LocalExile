using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class MagebloodTests
{
    [Fact]
    public async Task Mageblood_applies_the_leftmost_or_foulborn_rightmost_magic_utility_flasks_scaled_by_flask_effect()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mageblood-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        var items = Program.Items;
        var player = new Player(new(1, "mageblood", 100, 100, 100, 100, null, []), 1, 1) { Character = { Name = "MagebloodFlasks" } };
        try
        {
            Item Flask(string name, ItemRarity rarity) => ItemCatalog.Create(items.Find(name)!) with { Rarity = rarity };
            string Effect(string name) => $"buff {items.UtilityFlaskOf(items.Find(name)!)!.BuffRow}";
            // A life flask and a normal utility flask sit between the magic utility flasks and do not count.
            var bar = Game.ItemsOf(player);
            bar.Put(PlayerInventories.Flask, new(301, 0, 0, Flask("Divine Life Flask", ItemRarity.Magic)));
            bar.Put(PlayerInventories.Flask, new(302, 1, 0, Flask("Granite Flask", ItemRarity.Magic)));
            bar.Put(PlayerInventories.Flask, new(303, 2, 0, Flask("Quicksilver Flask", ItemRarity.Normal)));
            bar.Put(PlayerInventories.Flask, new(304, 3, 0, Flask("Jade Flask", ItemRarity.Magic)));
            bar.Put(PlayerInventories.Flask, new(305, 4, 0, Flask("Basalt Flask", ItemRarity.Magic)));
            var mageblood = new UniqueCatalog(items).Create("Mageblood", new Random(5))!;
            var leftmost = items.Mods.ById("MagicUtilityFlasksAlwaysApplyUnique__1")!;
            Item Belt(ModCatalog.Mod flasks) => mageblood with { Mods = [.. mageblood.Mods
                .Select(m => m.Hash == leftmost.Hash ? flasks.With(m.Section, [2], Random.Shared) : m)] };

            HashSet<string> Applied(Item belt, Func<uint, int> character)
            {
                bar.Remove(PlayerInventories.Belt, 310);
                bar.Put(PlayerInventories.Belt, new(310, 0, 0, belt));
                return [.. Game.MagebloodSources(player, character).Select(s => s.Effect)];
            }
            var left = Applied(Belt(leftmost), _ => 0);
            Assert.Contains(Effect("Granite Flask"), left);
            Assert.Contains(Effect("Jade Flask"), left);
            Assert.DoesNotContain(Effect("Basalt Flask"), left);
            Assert.DoesNotContain(Effect("Quicksilver Flask"), left);
            Assert.True(Game.MagebloodBlocksFlaskUse(player, Flask("Basalt Flask", ItemRarity.Magic)));
            Assert.False(Game.MagebloodBlocksFlaskUse(player, Flask("Quicksilver Flask", ItemRarity.Normal)));

            var right = Applied(Belt(items.Mods.ById("MutatedUniqueBelt43MagicUtilityFlasksAlwaysApplyRightmost")!), _ => 0);
            Assert.Contains(Effect("Jade Flask"), right);
            Assert.Contains(Effect("Basalt Flask"), right);
            Assert.DoesNotContain(Effect("Granite Flask"), right);

            // Increased flask effect scales the permanent effect as it scales a use.
            int Evasion(Func<uint, int> character)
            {
                Applied(Belt(leftmost), character);
                return Game.MagebloodSources(player, character).Where(s => s.Effect == Effect("Jade Flask")).Max(s => s.Value);
            }
            Assert.Equal(Evasion(_ => 0) * 3 / 2, Evasion(name => name == Stat.FlaskEffectPlusPct ? 50 : 0));

            // Area entry builds PoolsFor without calling RecomputePoolsAndSpeed; a leftmost Quartz Flask must still set
            // player.Phasing and publish phasing_buff + PhaseThroughObjects immediately without re-slotting the flask.
            bar.Put(PlayerInventories.Flask, new(306, 0, 0, Flask("Quartz Flask", ItemRarity.Magic)));
            Applied(Belt(leftmost), _ => 0);
            player.Phasing = false;
            player.Pools = Game.PoolsFor(player, 90);
            player.PlayerLife = 100;
            Assert.True(player.Phasing);
            Assert.True(player.IsPhasing);
            player.Area.Stats.Created(player.Id, []);
            await Game.SyncPhasing(player);
            player.Area.Stats.Publish();
            Assert.Equal(1, player.Area.Stats.WrittenValue(player.Id, Stat.PhaseThroughObjects));
            Assert.Equal(1, player.Area.Stats.WrittenValue(player.Id, Stat.VirtualPhaseThroughObjects));
            Assert.True(player.Area.Buffs.HasRow(player.Id, 336));
        }
        finally { PlayerInventories.Forget(player.Character.Name!); TestEnvironment.ReleaseSaves(path); }
    }
}
