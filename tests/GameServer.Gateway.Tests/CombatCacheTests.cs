using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class CombatCacheTests
{
    [Fact]
    public void Permanent_rewards_and_gem_sources_refresh_without_count_or_frame_shortcuts()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-cache-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        var conn = new ClientState(new(1, "cache", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "Cache" + Guid.NewGuid().ToString("N"), PlayerLife = 1000, PlayerMana = 1000,
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
        };
        conn.Clock.Stop();
        try
        {
            var rewards = QuestStaticRewards.Default.Value.Rewards.Where(r => r.Stats.Count > 0
                && !Quests.EveryCharacter.Contains(r.Flag) && !Game.ResistancePenaltyAct5.Contains(r.Flag)
                && !Game.ResistancePenaltyAct10.Contains(r.Flag)).Take(2).ToArray();
            Assert.Equal(2, rewards.Length);
            foreach (var reward in rewards)
            {
                // Replace the flag, keeping count equal; a reset removes its bonus immediately too.
                conn.QuestFlags.Clear(); conn.QuestFlags.Add(reward.Flag);
                var expected = reward.Stats.ToDictionary(p => Program.Stats.Key(p.Stat)!.Value, p => p.Value);
                var first = Game.WithQuestStats(conn, []);
                Assert.Equal(expected.OrderBy(p => p.Key), first.OrderBy(p => p.Key));
                first.Clear();
                Assert.Equal(expected.OrderBy(p => p.Key), Game.WithQuestStats(conn, []).OrderBy(p => p.Key));
            }
            conn.QuestFlags.Clear();
            Assert.Empty(Game.WithQuestStats(conn, []));

            var items = Game.ItemsOf(conn);
            var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemFireball")!);
            var host = items.Add(PlayerInventories.Weapon1, 0, 0,
                ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with { Sockets = [new(SocketColour.White, gem)] });
            var effect = Game.GemSkillEffects(conn).Single(g => Game.SkillsFor(conn).EffectId(g.Effect) == "Fireball");
            var original = Game.SupportedSkillAt(conn, effect.Id, null);
            uint level = Program.Stats.Key(SkillGemLevel.AllSkillGems)!.Value;
            conn.PlayerStats[level] = 2;
            Assert.Equal(original.Level + 2, Game.SupportedSkillAt(conn, effect.Id, null).Level);
            conn.PlayerStats[level] = 3;
            Assert.Equal(original.Level + 3, Game.SupportedSkillAt(conn, effect.Id, null).Level);
            conn.PlayerStats.Remove(level);
            Assert.Equal(original.Level, Game.SupportedSkillAt(conn, effect.Id, null).Level);
            items.Remove(PlayerInventories.Weapon1, host.Id);
            Assert.Equal(-1, Game.SupportedSkillAt(conn, effect.Id, null).Effect);

            foreach (string attribute in new[] { "strength", "dexterity", "intelligence" })
                conn.PlayerStats[Program.Stats.Key("virtual_" + attribute + "_for_requirements")!.Value] = 1000;
            var grace = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemGrace")!);
            host = items.Add(PlayerInventories.Weapon1, 0, 0,
                ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with { Sockets = [new(SocketColour.White, grace)] });
            CharacterSaves.SetAuraActive(conn.Character!, "Grace", true);
            Game.ReconcileOwnAuras(conn);
            Assert.Single(conn.Auras);
            Game.ReconcileOwnAuras(conn); Game.ReconcileOwnAuras(conn);
            uint disabled = Program.Stats.Key("your_spells_are_disabled")!.Value;
            conn.PlayerStats[disabled] = 1;
            Game.ReconcileOwnAuras(conn);
            Assert.Empty(conn.Auras);
            conn.PlayerStats[disabled] = 0; // Same override count: compare the values, not just count.
            Game.ReconcileOwnAuras(conn);
            Assert.Single(conn.Auras);
            Game.ReconcileOwnAuras(conn);
            items.Remove(PlayerInventories.Weapon1, host.Id);
            Game.ReconcileOwnAuras(conn);
            Assert.Empty(conn.Auras);
        }
        finally { PlayerInventories.Forget(conn.Character!); TestEnvironment.ReleaseSaves(path); }
    }

    sealed class Arena : IProjectileWorld
    {
        public readonly List<ProjectileActor> Targets = [];
        public readonly List<ProjectileDamage> Hits = [];
        public int Reads, Snapshots;
        public Action<ProjectileDamage>? AfterHit;
        public IReadOnlyList<ProjectileActor> Actors(long from, long until) { Snapshots++; return Targets.ToArray(); }
        public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) => null;
        public ProjectileHitResult Hit(ProjectileDamage hit) { Hits.Add(hit); AfterHit?.Invoke(hit); return ProjectileHitResult.Hit; }
        public void Publish(ProjectileEvent value) { }
        public void Add(uint id, Func<long, ProjectileVector> position) =>
            Targets.Add(new(new(id), 2, t => { Reads++; return position(t); }, 0));
    }

    [Fact]
    public void Projectile_motion_is_shared_within_a_step_and_refreshed_after_contacts_and_callbacks()
    {
        var world = new Arena();
        for (uint id = 10; id < 110; id++) world.Add(id, _ => new(500, 500));
        var runtime = new ProjectileRuntime(world);
        var profile = new ProjectileProfile("cache") { Speed = 30, Range = 100, Radius = .1, Count = 40, SpreadRadians = 0 };
        runtime.Release(new(profile, new(1), new(1), 1, new(0, 0), new(100, 0)));
        runtime.AdvanceTo(ProjectileRuntime.TickQuanta);
        Assert.Empty(world.Hits);
        Assert.InRange(world.Reads, 1, world.Snapshots * world.Targets.Count * 2);

        world.Targets.Clear();
        // Lower ID wins a coincident contact. Its hit removes that actor and moves the other one away;
        // the remaining flights must not use the previous step's positions or retain the dead target.
        world.Add(20, _ => new(2, 0)); world.Add(10, _ => new(2, 0));
        world.AfterHit = _ => { world.Targets.Clear(); world.Add(30, _ => new(500, 500)); };
        runtime.AdvanceTo(ProjectileRuntime.Time(.1));
        Assert.Single(world.Hits);
        Assert.Equal(10u, world.Hits[0].Target.Id);
        world.AfterHit = null;
        runtime.Schedule(ProjectileRuntime.Time(.11), () => { world.Targets.Clear(); world.Add(40, _ => new(4, 0)); });
        runtime.AdvanceTo(ProjectileRuntime.Time(.2));
        Assert.Contains(world.Hits, h => h.Target.Id == 40);
    }
}
