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
        // Volley Fire belongs to the active Barrage gems. Split Arrow keeps its full parallel sweep.
        c.PlayerStats[Program.Stats.Key("barrage_final_volley_fires_x_additional_projectiles_simultaneously")!.Value] = 6;
        c.PlayerStats[Program.Stats.Key("no_barrage_projectile_spread")!.Value] = 1;
        c.Monsters.Clear();
        var bag = Game.ItemsOf(c);
        var bow = bag.Items(PlayerInventories.Weapon1).Single();
        var reach = new UniqueCatalog(Program.Items).Create("Reach of the Council", new Random(23))!;
        var gem = Game.GemSkillEffects(c).Single();
        foreach (var (unique, barrage, wall, angle, start) in new[]
        {
            (false, false, false, 0, 10d), (false, true, false, 0, 15d),
            (true, false, false, 0, 20d), (true, false, true, 50, 25d), (true, true, false, 0, 30d),
            (true, false, false, -50, 35d)
        })
        {
            c.PlayerStats[Program.Stats.Key("forking_angle_+%")!.Value] = angle;
            c.PlayerStats[Program.Stats.Key("chance_to_fork_extra_projectile_%")!.Value] = wall ? 100 : 0;
            // The unique provides Greater Volley to its socketed active gem even with the support unlinked.
            bag.Change(PlayerInventories.Weapon1, bow.Id, item => (unique ? reach : bow.Item) with
            {
                Sockets = item.Sockets, Links = barrage ? [3] : unique ? [1, 1, 1] : [2, 1]
            });
            c.ProjectileWalls.Clear();
            if (wall) c.ProjectileWalls.Add((102, 96));
            c.Monsters.Clear();
            if (!barrage)
            {
                foreach (int y in new[] { 85, 93, 107, 115 })
                    foreach (int x in new[] { 125, 157 })
                        c.Monsters[(uint)(x * 1000 + y)] = new("target", new(x, y, 0), 0, 0, 1000000) { Hostile = true };
                foreach (int side in new[] { -1, 1 })
                    foreach (var (x, offset) in new[] { (125, 29), (119, 39), (114, 49) })
                    {
                        int y = 100 + side * offset;
                        c.Monsters[(uint)(x * 1000 + y)] = new("target", new(x, y, 0), 0, 0, 1000000) { Hostile = true };
                    }
            }
            c.Clock.Start();
            Assert.True(await Game.CastGemSkill("volley", battle.Stream, battle.Ciphers, c, gem.Id,
                (100, 100), (175, 100), sequence: 0x8013, actionToken: c.ActionCount, swingAt: start));
            c.Clock.Stop();
            var shots = new SortedDictionary<long, (ProjectileEntity Shot, ProjectileVector Outbound)>();
            for (int step = 0; step <= 400; step++)
            {
                battle.Session.AdvanceTo(ProjectileRuntime.Time(start + step / 200d));
                foreach (var p in battle.Session.Runtime.Projectiles) shots.TryAdd(p.Id, (p, p.Direction));
            }
            // The normal volley visits the centre and paired sides. The wall crosses only the line
            // from the owner's cell to the first positive point. Barrage sweeps nine, then four points.
            int[] slots = barrage ? [-4,-3,-2,-1,0,1,2,3,4,-2,-1,1,2]
                : wall ? [0,-1,2,-2,3,-3,4,-4] : [0,1,-1,2,-2,3,-3,4,-4];
            var arrows = shots.Values.Where(p => p.Shot.ParentId == 0).ToArray();
            Assert.Equal(slots.Length, arrows.Length);
            Assert.Equal(barrage ? slots.Length : 1, arrows.Select(p => p.Shot.CreatedAt).Distinct().Count());
            Assert.Equal(barrage ? slots.Length : 1, arrows.Select(p => p.Shot.EmissionId).Distinct().Count());
            for (int i = 0; i < slots.Length; i++)
            {
                double y = -slots[i] * (barrage ? 7 : 80 / 10.869565);
                var (shot, outbound) = arrows[i];
                Assert.Equal(100 + y, shot.Use.Origin.Y, 4);
                Assert.Equal(100 + y, shot.Use.Aim.Y, 4);
                Assert.Equal(175, shot.Use.Aim.X, 6);
                if (barrage) Assert.Equal(Math.Sign(y), Math.Sign(outbound.Y));
                else Assert.Equal(new ProjectileVector(1, 0), outbound);
                bool returns = unique && !barrage && Math.Abs(slots[i]) == 3;
                Assert.Equal(returns, shot.Profile.Returns);
                Assert.Equal(returns ? 1 : 0, shot.Phase);
                var children = shots.Values.Where(p => p.Shot.ParentId == shot.Id).ToArray();
                if (unique && !barrage && Math.Abs(slots[i]) == 2)
                {
                    // Each side aims 100 cells from the impact cell. The extra child is emitted between the sides.
                    (int sideX, int sideY) = angle switch { 50 => (38, 92), -50 => (92, 38), _ => (71, 71) };
                    (int X, int Y)[] offsets = wall ? [(sideX, -sideY), (100, 0), (sideX, sideY)] : [(sideX, -sideY), (sideX, sideY)];
                    int impactX = (int)Math.Floor(shot.Position.X + .5), laneY = slots[i] > 0 ? 85 : 115;
                    Assert.Equal(offsets.Length, children.Length);
                    for (int j = 0; j < offsets.Length; j++)
                    {
                        var aim = new ProjectileVector(impactX + offsets[j].X, laneY + offsets[j].Y);
                        Assert.InRange((children[j].Outbound - (aim - shot.Position).Unit).Length, 0, 1e-10);
                    }
                }
                else Assert.Empty(children);
                if (!barrage && Math.Abs(slots[i]) == 1)
                {
                    // A third-point return can also damage these targets. Follow the first-point arrow itself.
                    if (unique) Assert.True(shot.Position.X > 157);
                    else Assert.InRange(shot.Position.X, 120, 125);
                    int laneY = slots[i] > 0 ? 93 : 107;
                    Assert.True(c.Monsters[(uint)(125000 + laneY)].Life < 1000000);
                    Assert.Equal(unique, c.Monsters[(uint)(157000 + laneY)].Life < 1000000);
                }
                if (!barrage && Math.Abs(slots[i]) == 4)
                {
                    int side = -Math.Sign(slots[i]);
                    // Two targets behind and outside the original flight require two successive chains.
                    Assert.Equal(unique, (shot.Position.Y - 100) * side > 40);
                    foreach (var (x, offset) in new[] { (119, 39), (114, 49) })
                        Assert.Equal(unique, c.Monsters[(uint)(x * 1000 + 100 + side * offset)].Life < 1000000);
                }
            }
        }
    }
}
