using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;
using MonsterRarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

public sealed class ItemSupportModifierTests
{
    // A character with a bow linked to the given gems; the first skill effect found is the supported active skill.
    static void WithConnection(string tag, Action<ClientState> body)
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-{tag}-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, tag, 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "ItemSupportChar", PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                "virtual_intelligence_for_requirements" }) conn.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;
            body(conn);
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            GameServer.Gateway.Tests.TestEnvironment.ReleaseSaves(path);
        }
    }

    static ushort Link(ClientState conn, string skillGem, params string[] supports)
    {
        var bow = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!);
        List<ItemSocket> sockets = [new ItemSocket(SocketColour.White, ItemCatalog.Create(Program.Items.Find(skillGem)!))];
        foreach (string support in supports)
            sockets.Add(new ItemSocket(SocketColour.White, ItemCatalog.Create(Program.Items.Find(support)!)));
        Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, bow with { Sockets = sockets, Links = [(byte)sockets.Count] });
        return Game.GemSkillEffects(conn).Single(g => g.Name == "Tornado Shot").Id;
    }

    static int Stat(IReadOnlyDictionary<uint, int> stats, string name) =>
        Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;

    // Item Rarity Support's base_killed_monster_dropped_item_rarity_+% (40% at gem level 1) reaches the supported
    // skill's stats and, through the monster's last-hit share, the kill's drop rolls. Item Quantity is untouched:
    // its own share keeps multiplying the number of drops.
    [Fact]
    public void Item_rarity_support_raises_the_kill_s_drop_rarity()
    {
        WithConnection("rarity", conn =>
        {
            ushort gem = Link(conn, "Metadata/Items/Gems/SkillGemTornadoShot",
                "Metadata/Items/Gems/SupportGemItemRarity");
            int share = Stat(Game.SupportedSkill(conn, gem).Stats, "base_killed_monster_dropped_item_rarity_+%");
            Assert.Equal(40, share);

            // The kill's drop rolls read the skill's share once the monster carries it from the last hit.
            int player = Game.IncreasedItemRarity(conn);
            var hit = new SpawnedMonster("hit", new(100, 100, 0), 0, 0, 1000) { KilledWithItemRarityPct = share };
            var untouched = new SpawnedMonster("untouched", new(100, 100, 0), 0, 0, 1000);
            Assert.Equal(player + 40, Game.IncreasedItemRarity(conn, hit));
            Assert.Equal(player, Game.IncreasedItemRarity(conn, untouched));
            // Increased rarity raises every better rarity's chance rather than adding a flat chance.
            var plain = ItemDrops.WithIncreasedRarity(ItemDrops.RarityChances(MonsterRarity.Normal), player);
            var raised = ItemDrops.WithIncreasedRarity(ItemDrops.RarityChances(MonsterRarity.Normal), player + 40);
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
        WithConnection("quantity", conn =>
        {
            ushort gem = Link(conn, "Metadata/Items/Gems/SkillGemTornadoShot",
                "Metadata/Items/Gems/SupportGemItemQuantity");
            int share = Stat(Game.SupportedSkill(conn, gem).Stats, "base_killed_monster_dropped_item_quantity_+%");
            Assert.Equal(17, share);

            int Own(string stat) => stat == "base_killed_monster_dropped_item_quantity_+%" ? share : 0;
            int Row(Func<string, int> character) => PanelStats.Selected(character, Own)
                .First(r => r.Stat == "display_skill_increased_item_quantity_+%").Value;
            // The skill row shows the support's share on its own and adds the character's own quantity.
            Assert.Equal(17, Row(_ => 0));
            Assert.Equal(47, Row(stat => stat == "base_item_found_quantity_+%" ? 30 : 0));
        });
    }
}
