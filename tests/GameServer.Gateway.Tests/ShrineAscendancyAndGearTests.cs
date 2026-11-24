using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Data.Terrain.Generation;
using GameServer.Modules.Shrines;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class ShrineAscendancyAndGearTests
{
    static uint Key(string name) => Program.Stats.Key(name)!.Value;

    static void WithTestCharacter(Action<Player> body)
    {
        string path = Path.Combine(Path.GetTempPath(), $"shrines-test-{Guid.NewGuid():N}.db");
        var oldLogic = Program.GameLogicOrNull;
        var oldLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        var player = new Player(new(1, "shrines", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character =
            {
                Name = "Hero" + Guid.NewGuid().ToString("N")[..8],
                ClassRow = 0,
                Experience = CharacterStats.LevelStarts[80],
            },
            PlayerLife = 3000,
            PlayerMana = 2000,
            Connection = { PlayerSent = true },
            Instance = new(),
            Pools = new(80, 3000, 2000, 0, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        player.Clock.Stop();
        TestRequirements.Waive(player);
        try
        {
            body(player);
        }
        finally
        {
            PlayerInventories.Forget(player.Character.Name!);
            lifetime.Stop(_ => { });
            Game.CombatLifetime = oldLifetime;
            Program.SetGameLogic(oldLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    [Fact]
    public void Shrines_Atlas_Domination_Woodland_Shrines_Blunderbore_and_Soulcord_work()
    {
        bool Walkable(int x, int y) => x >= 0 && y >= 0;
        var roomObjects = new[] { new PlacedTileObject(ShrinePlacement.Type, "Metadata/Shrines/generic_shrine.ao", 10, 10, 0f, 0f) };

        // All That Glitters: map_shrines_chance_to_be_greed_shrine_%
        var greedPlan = ShrinePlacement.Plan(roomObjects, Walkable, seed: 42, greedChancePct: 100);
        Assert.Single(greedPlan);
        Assert.Equal("Greed", greedPlan[0].Shrine.Id);

        // Drawn to Power: map_shrines_have_guaranteed_magic_pack
        var (magicRule, _) = ShrineGuards.Resolve(greedPlan[0].Shrine, mapArea: true, _ => null, magic: true);
        Assert.Equal(MonsterRarity.Magic, magicRule.Rarity);

        // Blunderbore (MassiveLesser 259 + BrutalLesser 243) + Soulcord + scaling by shrine_buff_effect_on_self_+%
        WithTestCharacter(player =>
        {
            var stats = new Dictionary<uint, int>
            {
                [Key("has_unique_massive_shrine_effect")] = 1,
                [Key("has_unique_brutal_shrine_effect")] = 1,
                [Key("soulcord_have_diamond_shrine_effect_if_affected_by_no_flasks")] = 1,
                [Key("shrine_buff_effect_on_self_+%")] = 50,
            };
            var defs = ShrineGearEffects.ActiveDefinitions(stats);
            Assert.Contains(defs, d => d.Id == "MassiveLesser" && d.BuffRow == 259);
            Assert.Contains(defs, d => d.Id == "BrutalLesser" && d.BuffRow == 243);
            var withGear = ShrineGearEffects.AddStats(player, stats);
            Assert.Equal(1, withGear[Key("has_shrine_effect")]);
            Assert.Equal(1, withGear[Key("always_crit")]);
            // MassiveLesser base maximum_life_+% is 20 -> with 50% increased shrine effect = 30
            Assert.Equal(30, withGear[Key("maximum_life_+%")]);
            // BrutalLesser base damage_+% is 20 -> with 50% increased shrine effect = 30
            Assert.Equal(30, withGear[Key("damage_+%")]);
        });

        // Guardian Harmony of Purpose (61372: gain_shrine_buff_every_x_ms_from_ascendancy = 10000)
        ushort harmonyNode = Program.Passives.FindById("AscendancyGuardian11")!.GraphId;
        Assert.Equal((ushort)61372, harmonyNode);
        Assert.Contains(Program.Passives.Find(harmonyNode)!.Stats,
            kv => Program.Stats.Name(kv.Key) == "gain_shrine_buff_every_x_ms_from_ascendancy" && kv.Value == 10000);

        // Woodland Shrines (AscendancyCustomPathfinder12) grants TriggeredSummonLesserShrine Level 20 (10s CD)
        ushort woodlandGraphId = Program.Passives.FindById("AscendancyCustomPathfinder12")!.GraphId;
        var passive = new PassiveGrantedSkills(Program.Skills, "data").For([woodlandGraphId]);
        Assert.Contains(passive, g => g.EffectId == "TriggeredSummonLesserShrine" && g.Level == 20);
        var lesserGrant = passive.First(g => g.EffectId == "TriggeredSummonLesserShrine");
        Assert.Equal(10_000, Program.Skills.CooldownMs(lesserGrant.Effect, lesserGrant.Level));
    }
}
