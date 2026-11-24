using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// S→C 0x53 sends the two per-slot option masks back over, so a slot whose skill is attack-in-place by its own data
// (ActiveSkillType AttackInPlaceIsDefault, row 82, which artillery_ballista's ActiveSkills row carries) must be sent
// its own default instead of 0: a bar sent with 0 unticks the box the client put there itself, its ground press then
// comes with flag 0x0001 clear, its action asks MoveTo for the aim at the weapon's range, and it walks up to a far
// aim while every following action start puts the bit back. The first box a character touches seeds from the slots'
// own defaults, so ticking one slot keeps another slot's default; from then on the saved mask is the whole truth,
// and unticking the ballista's own box wins over its default.
public sealed class AttackInPlaceDefaultTests
{
    static Item Gem(string name) => ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + name)!);

    static Player Build(string name)
    {
        var player = new Player(new(1, name, 300, 300, 100, 100, null, []), 1, 1)
        {
            Character = { Name = name, Experience = CharacterStats.ExperienceFor(100) },
            PlayerLife = 1000,
            PlayerMana = 1000,
            Instance = new(),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new GameServer.Core.Accounts.League("test", "Standard", false, false),
                new HashSet<string>(["Game"])),
            Connection = { LockstepClient = true, PlayerSent = true },
        };
        var items = Game.ItemsOf(player);
        items.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
        {
            Sockets = [new ItemSocket(SocketColour.White, Gem("SkillGemArtilleryBallista"))],
        });
        items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Crude Bow")!));
        return player;
    }

    [Fact]
    public void Artillery_ballista_on_slot_2_keeps_its_attack_in_place_default_through_the_first_tick()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-attack-in-place-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        string character = $"AttackInPlace{Guid.NewGuid():N}";
        try
        {
            var player = Build(character);
            ushort skill = Game.GemSkillEffects(player).Single(g => g.Name == "Artillery Ballista").Id;
            // The right mouse button is slot 2, where the user has the ballista and where GGG's own bars carry the bit.
            var saved = new Dictionary<int, string> { [2] = Game.SkillBarEntry(player, skill)! };
            var ids = Game.SkillBarIds(player, saved);
            Assert.Equal(skill, ids[2]);
            Assert.Equal(SkillCatalog.FirstSkillId, ids[0]);          // the default attack: not attack-in-place

            ushort defaults = Game.AttackInPlaceDefaults(player, ids);
            Assert.Equal((ushort)0x0004, defaults);
            byte[] bar = InstanceProtocol.SkillBar(ids, defaults, 0);
            Assert.Equal(new byte[] { 0x04, 0x00, 0x00, 0x00 }, bar[^4..]);

            // The player ticks another slot: the first touch seeds from the slots' own defaults, so the ballista keeps its box.
            CharacterSaves.SetSkillBarOption(character, attackInPlace: true, 5, true, defaults);
            Assert.Equal((ushort)0x0024, CharacterSaves.Load(character)!.AttackInPlaceSlots);
            // And unticking the ballista's own box wins over the default, now and on every later send.
            CharacterSaves.SetSkillBarOption(character, attackInPlace: true, 2, false, defaults);
            var save = CharacterSaves.Load(character)!;
            Assert.Equal((ushort)0x0020, save.AttackInPlaceSlots);
            Assert.Equal(new byte[] { 0x20, 0x00, 0x00, 0x00 },
                InstanceProtocol.SkillBar(ids, save.AttackInPlaceSlots ?? Game.AttackInPlaceDefaults(player, ids), 0)[^4..]);
        }
        finally
        {
            PlayerInventories.Forget(character);
            Program.SetGameLogic(previousLogic!);
            GameServer.Gateway.Tests.TestEnvironment.ReleaseSaves(path);
        }
    }
}
