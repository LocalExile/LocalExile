using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

// The nine plain-stat tattoo rows of issue #372 whose gameplay consumers already exist: the catalogue rows must match
// the client's PassiveSkillOverrides, and the replacement path must put each stat into the character's stat map.
public sealed class TattooPlainStatsTests
{
    // The nine rows, the passive node each replaces (Small Strength/Dexterity/Intelligence) and the stat/value the
    // client's PassiveSkillOverrides carries.
    static readonly (string Name, ushort Node, string Stat, int Value)[] Rows =
    [
        ("Tattoo of the Hinekora Warmonger", 21678, "minion_maximum_life_+%", 5),
        ("Tattoo of the Kitava Blood Drinker", 21301, "base_life_leech_from_attack_damage_permyriad", 50),
        ("Tattoo of the Ngamahu Woodcarver", 21301, "totem_life_+%", 5),
        ("Tattoo of the Tasalio Bladedancer", 21301, "maim_on_hit_%", 5),
        ("Tattoo of the Tasalio Scout", 27659, "mark_skills_curse_effect_+%", 4),
        ("Tattoo of the Tasalio Warrior", 27659, "attacks_chance_to_blind_on_hit_%", 5),
        ("Tattoo of the Tawhoa Herbalist", 27659, "flask_duration_+%", 4),
        ("Tattoo of the Tawhoa Naturalist", 27659, "flask_life_to_recover_+%", 8),
        ("Tattoo of the Tukohama Warcaller", 21301, "warcry_cooldown_speed_+%", 8),
    ];

    [Fact]
    public void Every_plain_stat_tattoo_maps_its_client_stats()
    {
        foreach (var (name, _, stat, value) in Rows)
        {
            var stats = TattooStats.For(name);
            Assert.NotNull(stats);
            Assert.Contains((Program.Stats.Key(stat)!.Value, value), stats);
        }
    }

    [Fact]
    public void Tattoo_replacement_stats_reach_the_character_stat_map()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-tattoo-plain-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "tattoo-plain", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "TattooPlainChar", PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 100, Dexterity: 100, Intelligence: 100),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            foreach (var (name, node, stat, value) in Rows)
            {
                conn.Passives.Clear();
                conn.Passives.Add(node);
                conn.Tattoos.Clear();
                conn.Tattoos[node] = name;
                conn.TattooRevision++;
                var stats = Game.CharacterPlayerStats(conn);
                Assert.Equal(value, stats.GetValueOrDefault(Program.Stats.Key(stat)!.Value));
            }
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
