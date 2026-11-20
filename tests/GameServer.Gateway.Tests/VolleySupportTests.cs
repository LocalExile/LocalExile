using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class VolleySupportTests
{
    // The native client's parallel volley layout function (0x141bbb460) reads
    // virtual_additional_projectiles_fire_parallel_x_dist (0x18b2); Volley Support must publish it.
    [Fact]
    public async Task Volley_support_publishes_spacing_and_releases_from_the_selected_points()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-volley-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "volley", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "VolleyChar", PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            var wand = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1") ?? Program.Items.Find("Driftwood Wand")!);
            var fireball = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemFireball")!);
            var volley = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SupportGemParallelProjectiles")!);
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, wand with
            {
                Sockets = [new ItemSocket(SocketColour.White, fireball), new ItemSocket(SocketColour.White, volley)], Links = [2],
            });
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                "virtual_intelligence_for_requirements" }) conn.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;
            var skills = Game.PlayerSkills(conn);
            var fb = skills.Single(s => Game.SkillEffectId(conn, s.Id) == "Fireball");
            uint xDistKey = Program.Stats.Key("virtual_additional_projectiles_fire_parallel_x_dist")!.Value;
            Assert.NotNull(fb.Stats);
            Assert.Contains((xDistKey, 80), fb.Stats!);
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            GameServer.Gateway.Tests.TestEnvironment.ReleaseSaves(path);
        }

        using var battle = new ArrowRainAndMirageTests.Battle("Split Arrow", "Greater Volley Support", "Barrage Support");
        var c = battle.Conn;
        c.LockstepClient = true;
        c.PlayerStats[Program.Stats.Key("no_barrage_projectile_spread")!.Value] = 1;
        c.Monsters.Clear();
        var bag = Game.ItemsOf(c);
        var bow = bag.Items(PlayerInventories.Weapon1).Single();
        var gem = Game.GemSkillEffects(c).Single();
        foreach (var (barrage, wall, start) in new[] { (false, false, 10d), (false, true, 15d), (true, false, 20d) })
        {
            bag.Change(PlayerInventories.Weapon1, bow.Id, item => item with { Links = barrage ? [3] : [2, 1] });
            c.ProjectileWalls.Clear();
            if (wall) c.ProjectileWalls.Add((102, 96));
            c.Clock.Start();
            Assert.True(await Game.CastGemSkill("volley", battle.Stream, battle.Ciphers, c, gem.Id,
                (100, 100), (175, 100), sequence: 0x8013, actionToken: c.ActionCount, swingAt: start));
            c.Clock.Stop();
            var shots = new SortedDictionary<long, ProjectileEntity>();
            for (int step = 0; step <= 400; step++)
            {
                battle.Session.AdvanceTo(ProjectileRuntime.Time(start + step / 200d));
                foreach (var p in battle.Session.Runtime.Projectiles) shots.TryAdd(p.Id, p);
            }
            // The normal volley visits the centre and paired sides. The wall crosses only the line
            // from the owner's cell to the first positive point. Barrage sweeps nine, then four points.
            int[] slots = barrage ? [-4,-3,-2,-1,0,1,2,3,4,-2,-1,1,2]
                : wall ? [0,-1,2,-2,3,-3,4,-4] : [0,1,-1,2,-2,3,-3,4,-4];
            var arrows = shots.Values.ToArray();
            Assert.Equal(slots.Length, arrows.Length);
            Assert.Equal(barrage ? slots.Length : 1, arrows.Select(p => p.CreatedAt).Distinct().Count());
            Assert.Equal(barrage ? slots.Length : 1, arrows.Select(p => p.EmissionId).Distinct().Count());
            for (int i = 0; i < slots.Length; i++)
            {
                double y = -slots[i] * (barrage ? 7 : 80 / 10.869565);
                Assert.Equal(100 + y, arrows[i].Use.Origin.Y, 4);
                Assert.Equal(100 + y, arrows[i].Use.Aim.Y, 4);
                Assert.Equal(175, arrows[i].Use.Aim.X, 6);
                if (barrage) Assert.Equal(Math.Sign(y), Math.Sign(arrows[i].Direction.Y));
                else Assert.Equal(new ProjectileVector(1, 0), arrows[i].Direction);
            }
        }
    }
}
