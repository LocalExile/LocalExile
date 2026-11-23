using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;
using MonsterRarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

public sealed class ItemSupportModifierTests
{
    // A character with a bow linked to the given gems; the first skill effect found is the supported active skill.
    static void WithConnection(string tag, Action<Player> body)
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-{tag}-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var player = new Player(new(1, tag, 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = { Name = "ItemSupportChar", Experience = CharacterStats.ExperienceFor(100) },
            PlayerLife = 1000,
            PlayerMana = 1000,
            Instance = new(),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"]))
        };
        TestRequirements.Waive(player);
        try
        {
            body(player);
        }
        finally
        {
            PlayerInventories.Forget(player.Character.Name!);
            Program.SetGameLogic(previousLogic!);
            GameServer.Gateway.Tests.TestEnvironment.ReleaseSaves(path);
        }
    }

    static ushort Link(Player player, string skillGem, params string[] supports)
    {
        var bow = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!);
        List<ItemSocket> sockets = [new ItemSocket(SocketColour.White, ItemCatalog.Create(Program.Items.Find(skillGem)!))];
        foreach (string support in supports)
            sockets.Add(new ItemSocket(SocketColour.White, ItemCatalog.Create(Program.Items.Find(support)!)));
        Game.ItemsOf(player).Add(PlayerInventories.Weapon1, 0, 0, bow with { Sockets = sockets, Links = [(byte)sockets.Count] });
        return Game.GemSkillEffects(player).Single(g => g.Name == "Tornado Shot").Id;
    }

    static int Stat(IReadOnlyDictionary<uint, int> stats, uint name) =>
        stats.GetValueOrDefault(name);

    // Item Rarity Support's base_killed_monster_dropped_item_rarity_+% (40% at gem level 1) reaches the supported
    // skill's stats and, through the monster's last-hit share, the kill's drop rolls. Item Quantity is untouched:
    // its own share keeps multiplying the number of drops.
    [Fact]
    public void Item_rarity_support_raises_the_kill_s_drop_rarity()
    {
        WithConnection("rarity", player =>
        {
            ushort gem = Link(player, "Metadata/Items/Gems/SkillGemTornadoShot",
                "Metadata/Items/Gems/SupportGemItemRarity");
            int share = Stat(Game.SupportedSkill(player, gem).Stats, global::Stat.BaseKilledMonsterDroppedItemRarityPlusPct);
            Assert.Equal(40, share);

            // The kill's drop rolls read the skill's share once the monster carries it from the last hit.
            int playerValue = Game.IncreasedItemRarity(player);
            var hit = new SpawnedMonster("hit", new(100, 100, 0), 0, 0, 1000) { KilledWithItemRarityPct = share };
            var untouched = new SpawnedMonster("untouched", new(100, 100, 0), 0, 0, 1000);
            Assert.Equal(playerValue + 40, Game.IncreasedItemRarity(player, hit));
            Assert.Equal(playerValue, Game.IncreasedItemRarity(player, untouched));
            // Increased rarity raises every better rarity's chance rather than adding a flat chance.
            var plain = ItemDrops.WithIncreasedRarity(ItemDrops.RarityChances(MonsterRarity.Normal), playerValue);
            var raised = ItemDrops.WithIncreasedRarity(ItemDrops.RarityChances(MonsterRarity.Normal), playerValue + 40);
            Assert.Equal(plain.Unique * 1.4, raised.Unique, 9);
            Assert.Equal(plain.Rare * 1.4, raised.Rare, 9);
            Assert.Equal(plain.Magic * 1.4, raised.Magic, 9);
        });
    }

    // Item Quantity Support's share (17% at gem level 1) reaches the skill panel's Misc row, which the client draws
    // from display_skill_increased_item_quantity_+%; the rarity row already worked that way.
    [Fact]
    public void Item_quantity_support_shows_on_the_skill_panel()
    {
        WithConnection("quantity", player =>
        {
            ushort gem = Link(player, "Metadata/Items/Gems/SkillGemTornadoShot",
                "Metadata/Items/Gems/SupportGemItemQuantity");
            int share = Stat(Game.SupportedSkill(player, gem).Stats, global::Stat.BaseKilledMonsterDroppedItemQuantityPlusPct);
            Assert.Equal(17, share);

            int Own(uint stat) => stat == global::Stat.BaseKilledMonsterDroppedItemQuantityPlusPct ? share : 0;
            int Row(Func<uint, int> character) => PanelStats.Selected(character, Own)
                .First(r => r.Stat == global::Stat.DisplaySkillIncreasedItemQuantityPlusPct).Value;
            // The skill row shows the support's share on its own and adds the character's own quantity.
            Assert.Equal(17, Row(_ => 0));
            Assert.Equal(47, Row(stat => stat == global::Stat.BaseItemFoundQuantityPlusPct ? 30 : 0));
        });
    }
}
