using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

// A melee or area skill's hit ignites from the skill's ignite rows, as a projectile's does (MonsterIgnites).
public sealed class HitIgniteTests
{
    [Fact]
    public void A_melee_hit_ignites_from_its_skills_ignite_rows()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-hit-ignite-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "hit-ignite", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "HitIgniteChar", PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
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
            ushort skill = Game.GemSkillEffects(conn).Single(g => g.Name == "Infernal Blow").Id;
            MonsterIgnites.Settings Ignite() => Game.SupportedIgnite(conn, skill, spell: false, Game.CharacterPlayerStats(conn));
            var settings = Ignite();
            // Infernal Blow's fire: the main hand's ignite rows, 4 s, and no chance without a source.
            Assert.Equal(0, settings.Chance);
            Assert.True(settings.Max > 0 && settings.CriticalMax >= settings.Max);
            Assert.Equal(4, settings.Seconds, 6);

            SpawnedMonster Monster(params (string, int)[] stats) => new("target", new(105, 100, 0), 0, 0, 100)
                { Mods = new([], [], new(stats.ToDictionary(s => s.Item1, s => s.Item2))) };
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
