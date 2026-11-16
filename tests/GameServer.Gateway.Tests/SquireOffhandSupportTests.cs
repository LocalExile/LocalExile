using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// The Squire: "Socketed Support Gems can also Support Skills from your Main Hand"
// (support_gems_socketed_in_off_hand_also_support_main_hand_skills). Every support gem in the shield, linked or not,
// supports the main hand's gems; the same gems in a shield without the mod do not.
public sealed class SquireOffhandSupportTests
{
    static ClientState Connection(string name)
    {
        var conn = new ClientState(new(1, name, 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = name, PlayerLife = 2000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 2000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        foreach (string attr in new[] { "strength", "dexterity", "intelligence" })
            conn.PlayerStats[Program.Stats.Key($"virtual_{attr}_for_requirements")!.Value] = 1000;
        return conn;
    }

    static Item Gem(string name)
    {
        var type = Program.Items.FindGem(name)!.Value;
        return ItemCatalog.Create(type.Type) with
        {
            GemVariant = type.Variant.Index,
            GemExperience = Program.Items.Gem(type.Type)!.ExperienceFor(20),
        };
    }

    [Fact]
    public void Squire_supports_from_the_off_hand_reach_main_hand_skills_unlinked()
    {
        string path = Path.Combine(Path.GetTempPath(), $"squire-{Guid.NewGuid():N}.db");
        var previous = Program.GameLogicOrNull;
        Saves.Open(path); Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = Connection("SquireAcceptance");
        try
        {
            var items = Game.ItemsOf(conn);
            var sword = ItemCatalog.Create(Program.Items.Find(
                "Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1")!);
            items.Add(PlayerInventories.Weapon1, 0, 0, sword with
            {
                Sockets = [new(SocketColour.White, Gem("Cleave"))], Links = [1],
            });
            ushort cleave = Game.GemSkillEffects(conn).Single().Id;
            ItemSocket[] sockets = [new(SocketColour.White, Gem("Increased Area of Effect")),
                new(SocketColour.White, Gem("Melee Physical Damage")), new(SocketColour.White, null)];
            string[] Supports() => [.. Game.SupportedSkillAt(conn, cleave, null).Supports.Select(s => s.Effect.Id).Order()];

            var squire = Assert.IsType<Item>(Game.Uniques.Create("The Squire", new Random(17)));
            var shield = items.Add(PlayerInventories.Offhand1, 0, 0, squire with { Sockets = sockets, Links = [1, 1, 1] });
            Assert.Equal(["SupportIncreasedAreaOfEffect", "SupportMeleePhysicalDamage"], Supports());

            // The same base and gems without the mod support nothing in the main hand.
            items.Remove(PlayerInventories.Offhand1, shield.Id);
            items.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.ByHash(squire.TypeHash)!) with
            {
                Sockets = sockets, Links = [1, 1, 1],
            });
            Assert.Empty(Supports());
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!); Program.SetGameLogic(previous!); TestEnvironment.ReleaseSaves(path);
        }
    }
}
