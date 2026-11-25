using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using Rarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

// "Socketed gems gain 10% of the experience your character earns, before any level penalties."
public sealed class GemExperienceTests
{
    [Fact]
    public void Gem_experience_base_ignores_the_level_penalty()
    {
        // A level 90 character killing a level 1 monster: the character's share is penalised, the gems' is not.
        uint penalised = Progression.MonsterExperience(1_000, 100, Rarity.Normal, 90, 1);
        uint beforePenalty = Progression.MonsterExperience(1_000, 100, Rarity.Normal, 90, 1, levelPenalty: false);
        uint safe = Progression.MonsterExperience(1_000, 100, Rarity.Normal, 1, 1);
        Assert.True(penalised < beforePenalty);
        Assert.Equal(safe, beforePenalty);
        Corruption_and_blue_sockets_change_the_awarded_gem_experience();
    }

    static void Corruption_and_blue_sockets_change_the_awarded_gem_experience()
    {
        var outcomes = new List<(int[] Expected, int[] Actual)>();
        foreach (bool mutated in new[] { false, true })
        foreach (bool dialla in new[] { false, true })
        {
            using var fixture = new TriggeredSummonTests.Fixture();
            var player = fixture.Player;
            var items = Game.ItemsOf(player);
            var uniques = new UniqueCatalog(Program.Items);
            if (mutated)
            {
                const byte slot = 1;
                var jewel = Assert.IsType<Item>(uniques.Create("Mutated Growth", new Random(71)));
                player.Character.Passives.Add(Game.PassivesFor(player).JewelNodeForSlot(slot)!.Value);
                Assert.Null(Game.PassiveJewelRefusal(player, slot, 0, jewel));
                items.Add(PlayerInventories.PassiveJewels, slot, 0, jewel);
            }
            Assert.Equal(mutated ? 10 : 0, Game.PlayerStat(player, Stat.CorruptedGemExperienceGainPlusPct));

            static ItemSocket Gem(string name, SocketColour colour, bool corrupted, int level = 1)
            {
                var type = Program.Items.FindGem(name)!.Value;
                return new(colour, ItemCatalog.Create(type.Type) with
                {
                    Corrupted = corrupted, GemVariant = type.Variant.Index,
                    GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(level),
                });
            }
            var robe = Assert.IsType<Item>(uniques.Create("Dialla's Malefaction", new Random(71)));
            var blueXp = Program.Items.Mods.ById("SocketedGemsInBlueSocketEffectUnique__1")!;
            Assert.Contains(robe.Mods, mod => mod.Hash == blueXp.Hash);
            var body = items.Add(PlayerInventories.BodyArmour, 0, 0, robe with
            {
                Mods = dialla ? robe.Mods : [.. robe.Mods.Where(mod => mod.Hash != blueXp.Hash)],
                Sockets = [Gem("Fireball", SocketColour.Blue, true, level: 2), Gem("Ice Nova", SocketColour.Blue, false),
                    Gem("Heavy Strike", SocketColour.Red, true), Gem("Cleave", SocketColour.Red, false),
                    Gem("Fireball", SocketColour.Blue, true)],
                Links = [1, 1, 1, 1, 1],
            });
            uint weapon = Assert.Single(items.Items(PlayerInventories.Weapon1)).Id;
            items.Change(PlayerInventories.Weapon1, weapon, item => item with
            {
                Sockets = [Gem("Arc", SocketColour.Blue, true), Gem("Freezing Pulse", SocketColour.Blue, false)],
                Links = [1, 1],
            });
            int[] Experience(byte row, uint id) => [.. items.Items(row).Single(i => i.Id == id).Item.Sockets.Select(s => s.Gem!.GemExperience)];
            int[] EquippedExperience() => [.. Experience(PlayerInventories.BodyArmour, body.Id), .. Experience(PlayerInventories.Weapon1, weapon)];
            var before = EquippedExperience();
            var (packets, leveled) = Game.CommitGemExperience(player, 500);
            Assert.False(leveled);
            if (mutated || dialla)
            {
                Assert.Equal(2, packets.Count);
                Assert.All(packets, packet => Assert.Equal(0xbf, packet[1]));
            }
            else Assert.Equal(0xc3, Assert.Single(packets)[1]);
            int[] actual = [.. EquippedExperience().Zip(before, (after, start) => after - start)];
            int corruption = mutated ? 5 : 0, blue = dialla ? 50 : 0;
            int[] expected = [50 + corruption + blue, 50 + blue, 50 + corruption, 50,
                Math.Min(69, 50 + corruption + blue), 50 + corruption, 50];
            Console.WriteLine($"Gem experience, Mutated Growth {mutated}, Dialla blue mod {dialla}: [{string.Join(", ", actual)}]");
            outcomes.Add((expected, actual));
        }
        Assert.All(outcomes, result => Assert.Equal(result.Expected, result.Actual));
    }
}
