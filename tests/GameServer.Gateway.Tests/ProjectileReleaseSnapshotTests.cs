using GameServer.Core.Protocol.Instance;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public class ProjectileReleaseSnapshotTests
{
    [Fact]
    public async Task Release_refreshes_damage_and_supports_after_windup_inputs_change()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-release-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "release", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "Release" + Guid.NewGuid().ToString("N"), PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        conn.Clock.Stop();
        var session = new Game.ProjectileSession("release", null!, null!, conn);
        try
        {
            var items = Game.ItemsOf(conn);
            var fireball = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemFireball")!);
            var volley = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SupportGemParallelProjectiles")!);
            var wand = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with
            {
                Sockets = [new ItemSocket(SocketColour.White, fireball), new ItemSocket(SocketColour.White, volley)], Links = [2],
            };
            var host = items.Add(PlayerInventories.Weapon1, 0, 0, wand);
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                "virtual_intelligence_for_requirements" }) conn.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;
            var gem = Game.GemSkillEffects(conn).Single(g => Game.SkillsFor(conn).EffectId(g.Effect) == "Fireball");
            _ = Game.PlayerSkills(conn);
            var cachedSkills = Game.PlayerSkills(conn);
            var (_, leveled) = Game.CommitGemExperience(conn, 100);
            Assert.False(leveled);
            Assert.Same(cachedSkills, Game.PlayerSkills(conn));
            int nextLevel = Program.Items.Gem(Program.Items.ByHash(fireball.TypeHash)!)!.ExperienceFor(2);
            var (_, changedLevel) = Game.CommitGemExperience(conn, checked((uint)nextLevel * 10));
            Assert.True(changedLevel);
            Assert.NotSame(cachedSkills, Game.PlayerSkills(conn));
            gem = Game.GemSkillEffects(conn).Single(g => Game.SkillsFor(conn).EffectId(g.Effect) == "Fireball");
            using var parent = new Game.PlayerStatScope(conn);
            var stale = Game.CharacterPlayerStats(conn);
            _ = Game.SupportedSkill(conn, gem.Id);
            var first = Capture();
            var recipe = Game.ResolveProjectileDefinition(gem.Effect);
            uint damage = Program.Stats.Key("fire_damage_+%")!.Value;
            conn.PlayerStats[damage] = 200;
            var increased = Capture();
            Assert.Same(recipe, Game.ResolveProjectileDefinition(gem.Effect));
            var firstHit = Assert.IsType<ProjectileOffense>(first.Payload).Hit;
            var increasedHit = Assert.IsType<ProjectileOffense>(increased.Payload).Hit;
            Assert.True(increasedHit[GameServer.Core.World.DamageType.Fire].Min > firstHit[GameServer.Core.World.DamageType.Fire].Min);
            Assert.Equal(stale.GetValueOrDefault(damage), Game.CharacterPlayerStats(conn).GetValueOrDefault(damage));
            items.Remove(PlayerInventories.Weapon1, host.Id);
            items.Add(PlayerInventories.Weapon1, 0, 0, wand with { Sockets = [wand.Sockets[0]], Links = [1] });
            var unlinked = Capture();
            Assert.True(first.Profile.Count > unlinked.Profile.Count);
            Assert.Equal(1, unlinked.Profile.Count);

            uint requirement = Program.Stats.Key(Game.AttributeRequirementsStat)!.Value;
            int Expected() => Game.PassivesFor(conn).Stats([.. conn.Passives, .. conn.MainAscendancy.Allocated], conn.MasteryEffects)
                .GetValueOrDefault(requirement);
            Assert.Equal(Expected(), Game.AttributeRequirementsPct(conn));
            using var treeData = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes("data/tables/PassiveSkills.json"));
            var nodes = treeData.RootElement.EnumerateArray()
                .Where(n => n.GetProperty("PassiveSkillGraphId").GetInt32() is > 0 and <= ushort.MaxValue)
                .Select(n => (ushort)n.GetProperty("PassiveSkillGraphId").GetInt32()).Distinct().ToArray();
            ushort changed = nodes.First(n => Game.PassivesFor(conn).Find(n)!.Stats.Any(s => s.Key == requirement && s.Value != 0));
            conn.Passives.Add(changed);
            Assert.NotEqual(0, Expected());
            Assert.Equal(Expected(), Game.AttributeRequirementsPct(conn));
            conn.Passives.Clear();
            Assert.Equal(Expected(), Game.AttributeRequirementsPct(conn));
            conn.PlayerStats[requirement] = -25;
            Assert.Equal(-25, Game.AttributeRequirementsPct(conn));
            conn.PlayerStats[requirement] = 40;
            Assert.Equal(40, Game.AttributeRequirementsPct(conn));
            conn.PlayerStats.Remove(requirement);
            Assert.Equal(Expected(), Game.AttributeRequirementsPct(conn));

            parent.Dispose();
            uint speed = Program.Stats.Key("attack_speed_+%")!.Value;
            conn.PlayerStats[speed] = 0;
            double normalAttack = Game.WeaponFor(conn).AttackSeconds(Game.CharacterPlayerStats(conn));
            foreach (bool lockstep in new[] { false, true })
            foreach (bool nestedMessage in new[] { false, true })
            {
                conn.LockstepClient = lockstep;
                using var message = new Game.PlayerStatScope(conn);
                conn.PlayerStats[speed] = 1000;
                double fastAttack = Game.WeaponFor(conn).AttackSeconds(Game.CharacterPlayerStats(conn));
                Assert.Equal(normalAttack / 11, fastAttack, 8);
                conn.PlayerStats[speed] = 0;
                Assert.Equal(fastAttack, Game.WeaponFor(conn).AttackSeconds(Game.CharacterPlayerStats(conn)));
                var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var continuation = Task.Run(async () =>
                {
                    await resume.Task;
                    if (nestedMessage)
                    {
                        using var nextMessage = new Game.PlayerStatScope(conn);
                        return Game.WeaponFor(conn).AttackSeconds(Game.CharacterPlayerStats(conn));
                    }
                    return Game.WeaponFor(conn).AttackSeconds(Game.CharacterPlayerStats(conn));
                });
                message.Dispose();
                resume.SetResult();
                Assert.Equal(normalAttack, await continuation, 8);
            }

            GameServer.Core.Projectiles.ProjectileUse Capture() =>
                Game.CaptureGemProjectile(conn, session, gem.Id, gem, (120, 100), new SkillCriticalRoll(0));
        }
        finally
        {
            session.Invalidate();
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
