using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

// The ten Loyalty rows of issue #372: each replaces a small attribute passive with a level 1 Summon Spirit skill and
// its trigger stat; the Spirit chieftain machinery (SpiritChieftains, the Leadership buff and the chieftain minion)
// reads the same client data as the Ancestral Commander nodes.
public sealed class TattooLoyaltyTests
{
    static readonly (string Name, string Trigger)[] Rows =
    [
        ("Loyalty Tattoo of Ahuana", "has_trigger_on_suppress_hit_from_unique"),
        ("Loyalty Tattoo of Akoya", "has_trigger_on_reaching_maximum_rage_in_presence_of_unique"),
        ("Loyalty Tattoo of Ikiaho", "has_trigger_on_travel_skill_use_in_presence_of_unique"),
        ("Loyalty Tattoo of Kahuturoa", "has_trigger_on_crit_by_unique_enemy"),
        ("Loyalty Tattoo of Kaom", "has_trigger_on_totem_death_in_presence_of_unique"),
        ("Loyalty Tattoo of Kiloava", "has_trigger_on_block_hit_from_unique"),
        ("Loyalty Tattoo of Maata", "has_trigger_on_reaching_low_life_in_presence_of_unique"),
        ("Loyalty Tattoo of Rakiata", "has_trigger_on_crit_vs_marked_unique"),
        ("Loyalty Tattoo of Tawhanuku", "has_trigger_on_es_recharge_in_presence_of_unique"),
        ("Loyalty Tattoo of Utula", "has_trigger_on_taking_savage_hit_from_unique"),
    ];

    [Fact]
    public void Every_loyalty_tattoo_maps_its_trigger_and_grants_a_skill()
    {
        foreach (var (name, trigger) in Rows)
        {
            var stats = TattooStats.For(name);
            Assert.NotNull(stats);
            Assert.Contains((Program.Stats.Key(trigger)!.Value, 1), stats);
            var entry = TattooCatalogue.Find(name);
            Assert.NotNull(entry);
            Assert.True(entry!.GrantsSkill);
            Assert.True(entry.Effect > 0);
        }
    }

    [Fact]
    public void The_loyalty_grants_are_level_1_spirit_summons_with_leadership_and_a_presence_check()
    {
        var conn = new ClientState(new(1, "tattoo-loyalty", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "TattooLoyaltyChar", PlayerLife = 1000, PlayerMana = 100, Instance = new(),
            Pools = new(100, 1000, 1000, int.MaxValue, 0),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        string path = Path.Combine(Path.GetTempPath(), $"localexile-tattoo-loyalty-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
        var skills = Game.SkillsFor(conn);
        foreach (var (name, _) in Rows)
        {
            var entry = TattooCatalogue.Find(name)!;
            var level = skills.EffectsPerLevel(entry.Effect);
            Assert.NotNull(level);
            Assert.Equal(1, level!.Value.Level);
            string? effectId = skills.EffectId(level.Value.Effect);
            Assert.NotNull(effectId);
            Assert.StartsWith("TriggerSummonedAncestor", effectId);
            var own = skills.EffectStats(level.Value.Effect, level.Value.Level)
                .ToDictionary(stat => stat.Item1, stat => stat.Item2);
            var leadership = SpiritChieftains.LeadershipOf(own);
            Assert.NotNull(leadership);
            Assert.True(leadership!.Value.Row > 0);
            Assert.NotNull(leadership.Value.Minion);
            // The trigger spends the skill's own stored use (80 s at level 1, the cooldown group 24 the ten share) and
            // the buff lasts the skill's duration.
            var plan = SingleUseCooldown.ForSkill(skills, Program.Stats, level.Value.Effect, level.Value.Level,
                new Dictionary<uint, int>(), own);
            Assert.NotNull(plan);
            Assert.Equal(80000, plan!.Value.BaseMilliseconds);
            Assert.True(own.GetValueOrDefault(Program.Stats.Key("base_skill_effect_duration")!.Value) > 0, name);
        }

        // "while a Unique Enemy is in your Presence": within 80 cells of the player, hostile and alive.
        conn.Monsters[1] = new SpawnedMonster("Spirit of Kaom", conn.NewMonsterWalk(1, 105, 100, 30, 1, false), 0, 0, 100)
        {
            Rarity = MonsterRarity.Unique,
        };
        Assert.True(Game.UniqueInPresenceLocked(conn, 0));
        conn.Monsters.Clear();
        Assert.False(Game.UniqueInPresenceLocked(conn, 0));

        // The tattoo is a triggered grant: its trigger stat names it and its PassiveSkillOverrides.Effect is the
        // level 1 Summon Spirit skill the Spirit chieftain path triggers (cooldown, Leadership, chieftain).
        conn.Passives.Add(21301);
        conn.Tattoos = new Dictionary<ushort, string> { [21301] = "Loyalty Tattoo of Kiloava" };
        uint blockKey = Program.Stats.Key("has_trigger_on_block_hit_from_unique")!.Value;
        var grants = Game.TattooTriggeredGrantsLocked(conn, blockKey).ToList();
        var grant = Assert.Single(grants);
        Assert.Equal(1, grant.Level);
        Assert.StartsWith("TriggerSummonedAncestorKiloava", grant.EffectId);
        Assert.Empty(Game.TattooTriggeredGrantsLocked(conn, Program.Stats.Key("has_trigger_on_crit_by_unique_enemy")!.Value));
        // Tattoos are saved apart from allocation: refunding the node ends its trigger.
        conn.Passives.Remove(21301);
        Assert.Empty(Game.TattooTriggeredGrantsLocked(conn, blockKey));
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    [Fact]
    public void The_ancestral_spirit_nodes_keep_their_stored_use()
    {
        // The five Ancestral Spirit options grant the same TriggerSummonedAncestor skills as the Loyalty tattoos.
        // They are Spells, so IsSpell alone admits their stored use after the src/ name rule was removed.
        var skills = Program.Skills;
        var grants = new PassiveGrantedSkills(skills).For(new ushort[] { 59500, 17140, 17511, 16639, 62534 });
        Assert.Equal(5, grants.Count);
        foreach (var grant in grants)
        {
            Assert.StartsWith("TriggerSummonedAncestor", grant.EffectId);
            Assert.True(skills.IsSpell(grant.Effect), grant.EffectId);
            var own = skills.EffectStats(grant.Effect, grant.Level).ToDictionary(stat => stat.Item1, stat => stat.Item2);
            var plan = SingleUseCooldown.ForSkill(skills, Program.Stats, grant.Effect, grant.Level,
                new Dictionary<uint, int>(), own);
            Assert.NotNull(plan);
            Assert.Equal(20000, plan!.Value.BaseMilliseconds);
            Assert.Equal(1, plan.Value.Uses);
        }
    }
}
