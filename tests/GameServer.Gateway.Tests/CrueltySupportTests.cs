using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// Cruelty Support (GrantedEffect 11889): "Hits from Supported Skills grant Cruelty", which makes every skill supported
// by Cruelty deal up to 40% more Damage over Time, based on the damage of the hit. The buff is per supported skill,
// keeps the strongest running value and refreshes its 4 s duration; its more Damage over Time is added to that skill's
// own stats while its buff runs, and is presented as one buff (BuffDefinitions 1815) with u82 = 0.
public sealed class CrueltySupportTests
{
    [Fact]
    public void CrueltyPctFor_rounds_caps_and_handles_degenerate_input()
    {
        // 1 / 1000 of maximum life, no increased effect: round(100 * 1 * 0.001^0.2) = round(25.1) = 25.
        Assert.Equal(25, Game.CrueltyPctFor(1, 1000, 0));
        // A full-life hit saturates the 40% cap.
        Assert.Equal(Game.CrueltyMaxPct, Game.CrueltyPctFor(1000, 1000, 0));
        // Increased Cruelty effect (19%) scales the result: round(100 * 1.19 * 0.001^0.2) = round(29.9) = 30.
        Assert.Equal(30, Game.CrueltyPctFor(1, 1000, 19));
        // Damage is clamped to the life the target could lose: overkill never inflates it.
        Assert.Equal(Game.CrueltyPctFor(500, 1000, 0), Game.CrueltyPctFor(100000, 1000, 0));
        // No damage, no life or a non-positive maximum life grants nothing.
        Assert.Equal(0, Game.CrueltyPctFor(0, 1000, 0));
        Assert.Equal(0, Game.CrueltyPctFor(100, 0, 0));
        Assert.Equal(0, Game.CrueltyPctFor(100, -5, 0));
        Assert.Equal(40, Game.CrueltyMaxPct);
        Assert.Equal(4000, Game.CrueltyDurationMs);
    }

    [Fact]
    public void CrueltyOnHit_keeps_the_strongest_value_per_skill_and_refreshes_its_deadline()
    {
        var conn = new ClientState(new(1, "cruelty", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "CrueltyChar", PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
        };
        var skill = new Dictionary<uint, int>
        {
            [Program.Stats.Key("supported_by_cruelty")!.Value] = 1,
            [Program.Stats.Key("cruelty_effect_+%")!.Value] = 0,
            [Program.Stats.Key("support_base_cruelty_duration_ms")!.Value] = 4000,
        };
        const ushort skillId = 0x1234;
        var monster = new SpawnedMonster("target", new(0, 0, 0), 0, 0, 1000) { Hostile = true };

        // First hit: 1 of 1000 life -> 25%.
        lock (conn.Monsters) Game.CrueltyOnHitLocked(conn, skillId, 1, 1000, 1000, skill, 0);
        Assert.Equal(25, conn.SupportGems.CrueltyPct[skillId]);
        double firstUntil = conn.SupportGems.CrueltyUntil[skillId];
        Assert.True(firstUntil > 0);

        // A stronger hit raises the value and moves the deadline forward.
        lock (conn.Monsters) Game.CrueltyOnHitLocked(conn, skillId, 1000, 1000, 1000, skill, 1);
        Assert.Equal(40, conn.SupportGems.CrueltyPct[skillId]);
        Assert.True(conn.SupportGems.CrueltyUntil[skillId] >= firstUntil);

        // A weaker hit never downgrades the value, but still refreshes the deadline.
        double before = conn.SupportGems.CrueltyUntil[skillId];
        lock (conn.Monsters) Game.CrueltyOnHitLocked(conn, skillId, 1, 1000, 1000, skill, 2);
        Assert.Equal(40, conn.SupportGems.CrueltyPct[skillId]);
        Assert.True(conn.SupportGems.CrueltyUntil[skillId] >= before);

        // A skill without the supported_by_cruelty stat never gains the buff.
        var plain = new Dictionary<uint, int> { [Program.Stats.Key("cruelty_effect_+%")!.Value] = 0 };
        lock (conn.Monsters) Game.CrueltyOnHitLocked(conn, 0x9999, 1000, 1000, 1000, plain, 0);
        Assert.False(conn.SupportGems.CrueltyPct.ContainsKey(0x9999));

        // The buff expiring removes the value: clear the deadline, then CrueltyActiveAt reports 0.
        conn.SupportGems.CrueltyUntil[skillId] = 0;
        Assert.Equal(0, Game.CrueltyActivePct(conn, skillId));
    }

    [Fact]
    public void Cruelty_buff_state_injects_its_more_damage_over_time_into_a_supported_skill()
    {
        string path = Path.Combine(Path.GetTempPath(), $"support-cruelty-{Guid.NewGuid():N}.db");
        var previous = Program.GameLogicOrNull;
        Saves.Open(path); Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "cruelty-inject", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "CrueltyInjectChar", PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        foreach (string attr in new[] { "strength", "dexterity", "intelligence" })
            conn.PlayerStats[Program.Stats.Key($"virtual_{attr}_for_requirements")!.Value] = 1000;
        try
        {
            // Equip Infernal Blow with Cruelty Support.
            var items = Game.ItemsOf(conn);
            var sword = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1")!);
            var blow = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemInfernalBlow")!) with
            {
                GemExperience = Program.Items.Gem(Program.Items.Find("Metadata/Items/Gems/SkillGemInfernalBlow")!)!.ExperienceFor(20),
            };
            var cruelty = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SupportGemCruelty")!) with
            {
                GemExperience = Program.Items.Gem(Program.Items.Find("Metadata/Items/Gems/SupportGemCruelty")!)!.ExperienceFor(20),
            };
            items.Add(PlayerInventories.Weapon1, 0, 0, sword with
            {
                Sockets = [new(SocketColour.White, blow), new(SocketColour.White, cruelty)], Links = [2],
            });
            ushort skill = Game.GemSkillEffects(conn).Single().Id;
            var gem = Game.SupportedSkill(conn, skill);
            Assert.Equal(1, gem.Stats.GetValueOrDefault(Program.Stats.Key("supported_by_cruelty")!.Value));

            uint dotKey = Program.Stats.Key("cruelty_damage_over_time_+%_final")!.Value;
            // No running buff: no injected more Damage over Time.
            Assert.Equal(0, gem.Stats.GetValueOrDefault(dotKey));

            // Grant a running buff for this skill: the injection now adds its percentage.
            conn.SupportGems.CrueltyPct[skill] = 30;
            conn.SupportGems.CrueltyUntil[skill] = conn.Clock.Elapsed.TotalSeconds + 4;
            var gem2 = Game.SupportedSkill(conn, skill);
            Assert.Equal(30, gem2.Stats.GetValueOrDefault(dotKey));

            // The buff expiring removes the injection again.
            conn.SupportGems.CrueltyUntil[skill] = 0;
            var gem3 = Game.SupportedSkill(conn, skill);
            Assert.Equal(0, gem3.Stats.GetValueOrDefault(dotKey));
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!); Program.SetGameLogic(previous!); TestEnvironment.ReleaseSaves(path);
        }
    }
}
