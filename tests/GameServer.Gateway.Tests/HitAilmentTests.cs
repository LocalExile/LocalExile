using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

// A melee or area skill's hit ignites from the skill's ignite rows, as a projectile's does (MonsterIgnites), and an
// attack bleeds from its bleeding rows (MonsterBleeds).
public sealed class HitAilmentTests
{
    [Fact]
    public void A_melee_hit_ignites_from_its_skills_ignite_rows() => WithClubCharacter(conn =>
    {
        ushort skill = Game.GemSkillEffects(conn).Single(g => g.Name == "Infernal Blow").Id;
        MonsterIgnites.Settings Ignite() => Game.SupportedIgnite(conn, skill, spell: false, Game.CharacterPlayerStats(conn));
        var settings = Ignite();
        // Infernal Blow's fire: the main hand's ignite rows, 4 s, and no chance without a source.
        Assert.Equal(0, settings.Chance);
        Assert.True(settings.Max > 0 && settings.CriticalMax >= settings.Max);
        Assert.Equal(4, settings.Seconds, 6);

        var target = Monster();
        conn.Monsters[1] = target;
        lock (conn.Monsters)
        {
            Assert.Null(Game.IgniteOnHitLocked(conn, 1, target, settings, critical: false, now: 0));
            // A critical strike always ignites: one buff, its damage in the critical range.
            var packets = Game.IgniteOnHitLocked(conn, 1, target, settings, critical: true, now: 0);
            Assert.Single(packets!);
            Assert.InRange(target.Ignites.PerSecond(0), settings.CriticalMin, settings.CriticalMax);
            Assert.Equal(4, target.Ignites.Until, 6);
            // The next ignite sends the buff again, the old instance off first.
            Assert.Equal(2, Game.IgniteOnHitLocked(conn, 1, target, settings, critical: true, now: 1)!.Count);
            // A monster that cannot be ignited is not.
            var immune = Monster(("cannot_be_ignited", 1));
            conn.Monsters[2] = immune;
            Assert.Null(Game.IgniteOnHitLocked(conn, 2, immune, settings, critical: true, now: 0));
        }

        // 100% chance: a non-critical hit ignites too, in the non-critical range.
        conn.PlayerStats[Program.Stats.Key("base_chance_to_ignite_%")!.Value] = 100;
        settings = Ignite();
        Assert.Equal(100, settings.Chance);
        var other = Monster();
        conn.Monsters[3] = other;
        lock (conn.Monsters) Assert.NotNull(Game.IgniteOnHitLocked(conn, 3, other, settings, critical: false, now: 0));
        Assert.InRange(other.Ignites.PerSecond(0), settings.Min, settings.Max);

        // The default attack reads the weapon's rows: the club has no fire until the attack gets added fire.
        Assert.Equal(0, Game.DefaultAttackIgnite(conn, Game.CharacterPlayerStats(conn)).Max);
        conn.PlayerStats[Program.Stats.Key("attack_minimum_added_fire_damage")!.Value] = 10;
        conn.PlayerStats[Program.Stats.Key("attack_maximum_added_fire_damage")!.Value] = 20;
        var attack = Game.DefaultAttackIgnite(conn, Game.CharacterPlayerStats(conn));
        Assert.True(attack.Max > 0 && attack.Chance == 100);
    });

    [Fact]
    public void An_attack_bleeds_from_its_bleeding_rows_and_a_moving_monster_takes_triple() => WithClubCharacter(conn =>
    {
        MonsterIgnites.Settings Bleed() => Game.DefaultAttackBleed(conn, Game.CharacterPlayerStats(conn));
        var settings = Bleed();
        // The club's physical damage: the main hand's bleeding rows for 5 s, and no chance without a source.
        Assert.Equal(0, settings.Chance);
        Assert.True(settings.Max > 0);
        // Character.ot's +50% damage over time multiplier for ailments from critical strikes.
        Assert.Equal(1.5, settings.CriticalMax / settings.Max, 2);
        Assert.Equal(5, settings.Seconds, 6);
        // Its poison reads the same rows: 30% of the physical damage a second, against the bleed's 70%.
        Assert.Equal(3.0 / 7, Game.DefaultAttackPoison(conn, Game.CharacterPlayerStats(conn)).Max / settings.Max, 2);
        ushort skill = Game.GemSkillEffects(conn).Single(g => g.Name == "Infernal Blow").Id;
        Assert.Equal(MonsterIgnites.Settings.None, Game.SupportedBleed(conn, skill, spell: true, Game.CharacterPlayerStats(conn)));

        var target = Monster();
        conn.Monsters[1] = target;
        lock (conn.Monsters)
        {
            // Unlike ignite, a critical strike rolls the chance too.
            Assert.Null(Game.BleedOnHitLocked(conn, 1, target, settings, critical: true, now: 0));
            // The "on Critical Strike" chances count for critical strikes only.
            conn.PlayerStats[Program.Stats.Key("bleed_on_crit_%_with_attacks")!.Value] = 100;
            Assert.Equal((0, 100), (Bleed().Chance, Bleed().CriticalChance));
            Assert.Null(Game.BleedOnHitLocked(conn, 1, target, Bleed(), critical: false, now: 0));
            // A weapon's fixed chances: "25% chance to cause Bleeding on Hit", "50% ... on Critical Strike".
            var fixedChances = Game.BleedSettings([], "main_hand", _ => 0, new Dictionary<string, int>
                { ["local_chance_to_bleed_on_hit_25%"] = 1, ["local_chance_to_bleed_on_crit_50%"] = 1 }, melee: false, bow: false);
            Assert.Equal((25, 75), (fixedChances.Chance, fixedChances.CriticalChance));
            conn.PlayerStats[Program.Stats.Key("bleed_on_hit_with_attacks_%")!.Value] = 100;
            settings = Bleed();
            Assert.Equal(100, settings.Chance);
            Assert.Single(Game.BleedOnHitLocked(conn, 1, target, settings, critical: false, now: 0)!);
            Assert.InRange(target.Bleeds.PerSecond(0), settings.Min, settings.Max);
            Assert.Equal(5, target.Bleeds.Until, 6);
            Assert.False(target.Ignites.Burning);
            // A monster immune to bleeding is not.
            var immune = Monster(("immune_to_bleeding", 1));
            conn.Monsters[2] = immune;
            Assert.Null(Game.BleedOnHitLocked(conn, 2, immune, settings, critical: false, now: 0));
        }
        // Standing still it takes the bleed through its physical damage taken; moving, three times that.
        double still = Game.BleedTaken(target, 0);
        Assert.True(still > 0);
        target.Walk = new(105, 100, 30);
        target.Walk.Order(155, 100, 0);
        Assert.True(target.Walk.Moving(0.1));
        Assert.Equal(3 * still, Game.BleedTaken(target, 0.1), 6);
        // Modifiers to hits never reach it (wiki Bleeding); a bleed support's more multiplier does, and not the poison.
        double poison = Game.DefaultAttackPoison(conn, Game.CharacterPlayerStats(conn)).Max;
        conn.PlayerStats[Program.Stats.Key("melee_damage_+%")!.Value] = 100;
        conn.PlayerStats[Program.Stats.Key("attack_damage_+%")!.Value] = 100;
        Assert.Equal(settings.Max, Bleed().Max, 6);
        conn.PlayerStats[Program.Stats.Key("support_chance_to_bleed_bleeding_damage_+%_final")!.Value] = 50;
        Assert.Equal(1.5, Bleed().Max / settings.Max, 2);
        Assert.Equal(poison, Game.DefaultAttackPoison(conn, Game.CharacterPlayerStats(conn)).Max, 6);
        double supported = Bleed().Max;
        conn.PlayerStats[Program.Stats.Key("bleeding_dot_multiplier_+")!.Value] = 50;
        Assert.Equal(1.5, Bleed().Max / supported, 2);

        // Aggravated (wiki Bleeding): on a standing monster the strongest aggravated bleed adds the moving bonus to the
        // strongest bleed's damage; a moving one already takes it.
        Assert.Equal((2.0, 0.0), (Game.AggravatedExtra(Monster(), 0), Game.AggravatedExtra(target, 0.1)));
        var cuts = new MonsterIgnites();
        cuts.Add(0, 4, 5);
        cuts.Aggravate();
        cuts.Add(0, 10, 5);
        Assert.Equal(10 + 2 * 4, cuts.Advance(1, 1, aggravatedExtra: 2));
        conn.PlayerStats[Program.Stats.Key("base_aggravate_bleeding_on_attack_hit_chance_%")!.Value] = 10;
        conn.PlayerStats[Program.Stats.Key("aggravate_inflicted_bleeding")!.Value] = 1;
        Assert.Equal((10, 10, true), (Bleed().Aggravate, Bleed().CriticalAggravate, Bleed().Aggravated));
        // Crimson Dance: the 8 strongest deal damage at 50% less, never aggravated nor more while it moves.
        double single = Bleed().Max;
        conn.PlayerStats[Program.Stats.Key("keystone_crimson_dance")!.Value] = 1;
        var dance = Bleed();
        Assert.Equal((8, 0, false), (dance.Stacks, dance.Aggravate, dance.Aggravated));
        Assert.Equal(.5, dance.Max / single, 6);
        cuts.Stacks = 8;
        Assert.Equal(14, cuts.Advance(2, 1));
        target.Bleeds.Stacks = 8;
        Assert.Equal((still, 0.0), (Game.BleedTaken(target, 0.1), Game.AggravatedExtra(target, 0)));
        conn.PlayerStats[Program.Stats.Key("cannot_cause_bleeding")!.Value] = 1;
        Assert.Equal(MonsterIgnites.Settings.None, Bleed());
    });

    static SpawnedMonster Monster(params (string, int)[] stats) => new("target", new(105, 100, 0), 0, 0, 100)
        { Mods = new([], [], new(stats.ToDictionary(s => s.Item1, s => s.Item2))) };

    // A level 100 character with a Driftwood Club socketed with Infernal Blow.
    static void WithClubCharacter(Action<ClientState> test)
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-hit-ailment-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "hit-ailment", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "HitAilmentChar", PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            var club = ItemCatalog.Create(Program.Items.Find("Driftwood Club")!);
            var blow = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemInfernalBlow")!);
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                "virtual_intelligence_for_requirements" }) conn.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, club with
            {
                Sockets = [new ItemSocket(SocketColour.White, blow)], Links = [1],
            });
            test(conn);
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            Saves.Db.Dispose();
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }
}
