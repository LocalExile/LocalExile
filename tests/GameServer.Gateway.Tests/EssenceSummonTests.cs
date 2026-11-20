using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class EssenceSummonTests
{
    static ClientState Connection() => new(new(1, "essence-summons", 300, 300, 100, 100, null, []), 1, 1) { Instance = new() };
    static SpawnedMonster Monster() => new("source", new(300, 300, 30), 60, 8, 100) { Level = 70, Aggro = true };
    static SkillCatalog.MonsterSummonInfo Summon(string effect) =>
        Program.Skills.MonsterSummon(Program.Skills.EffectRow(effect), 70)!;

    [Fact]
    public void Karui_daemon_uses_native_summon_data_and_an_awake_carriers_cast_and_cooldown()
    {
        var summon = Summon("EssenceDaemonSummonMovingKaruiSpirit");
        Assert.Equal(Game.EssenceKaruiSpirit, Game.Monsters.Find(summon.Variety.ToString())!.Id);
        Assert.Equal(1, summon.Count);
        Assert.Equal(1, summon.CastSeconds);
        Assert.Equal(6, summon.CooldownSeconds);
        var conn = Connection();
        var source = Monster();
        var mod = Program.Items.Mods.ById(Game.EssenceKaruiSummonMod)!;
        source.Mods = Game.MonsterModRolls.Value.Create([mod.Roll(ModSection.Implicit, new Random(505))], [], MonsterRarity.Rare, 70);
        conn.Monsters[42] = source;
        Assert.Empty(Game.EssenceSummonsDueLocked(conn, 0));
        Assert.Empty(Game.EssenceSummonsDueLocked(conn, .999));
        Assert.Same(source, Assert.Single(Game.EssenceSummonsDueLocked(conn, 1)).Monster);
        Assert.Empty(Game.EssenceSummonsDueLocked(conn, 1));
        Assert.Empty(Game.EssenceSummonsDueLocked(conn, 5.999));
        Assert.Empty(Game.EssenceSummonsDueLocked(conn, 6));
        Assert.Single(Game.EssenceSummonsDueLocked(conn, 7));
        source.Asleep = true;
        Assert.Empty(Game.EssenceSummonsDueLocked(conn, 100));
        source.Asleep = false;
        Assert.Empty(Game.EssenceSummonsDueLocked(conn, 100));
        Assert.Single(Game.EssenceSummonsDueLocked(conn, 101)); // no catch-up waves
        Assert.Empty(Game.EssenceSummonsDueLocked(conn, 106));
        source.Dead = true;
        Assert.Empty(Game.EssenceSummonsDueLocked(conn, 107));
        Assert.False(Game.MonsterSummonerCurrentLocked(conn, 42, source));
        source.Dead = false;
        conn.Monsters[42] = Monster();
        Assert.False(Game.MonsterSummonerCurrentLocked(conn, 42, source));
        conn.Monsters[42] = source;
        conn.Closed = true;
        Assert.Empty(Game.EssenceSummonsDueLocked(conn, 107));
        Assert.False(Game.MonsterSummonerCurrentLocked(conn, 42, source));
        var variety = Game.Monsters.Find(Game.EssenceKaruiSpirit)!;
        var kit = BossKits.Kit(Connection(), variety, 70);
        Assert.Equal(new[] { "Melee", "EssenceKaruiSpiritHeavyStrike" }, kit.Select(s => Program.Skills.EffectId(s.Effect)));
        Assert.True(kit[1].Attack.Damage.MinTotal > kit[0].Attack.Damage.MinTotal);
        Assert.True(kit[1].Attack.Seconds < kit[0].Attack.Seconds);
        var spirit = Monster();
        spirit.Mods = Game.EssenceSummonModifiers(variety)!;
        Assert.Equal(0, Game.MonsterTakes(spirit, DamagePacket.Of(DamageType.Physical, 1000, 1000), new Random(505), false));
        conn.Closed = false;
        for (uint id = 50; id < 53; id++)
            conn.Monsters[id] = new("spirit", new(300, 300, 30), 60, 8, 100) { Variety = variety, SummonerId = 42 };
        Assert.False(Game.EssenceSummonCanSpawnLocked(conn, 42, variety));
        Assert.True(Game.EssenceSummonCanSpawnLocked(conn, 43, variety)); // each carrier owns its cap
        Assert.Empty(Game.EssenceSummonsDueLocked(conn, 200));
        Assert.Empty(Game.EssenceSummonsDueLocked(conn, 201));
        conn.Monsters[50].Dead = true;
        Assert.True(Game.EssenceSummonCanSpawnLocked(conn, 42, variety));
        Assert.Empty(Game.EssenceSummonsDueLocked(conn, 206));
        Assert.Single(Game.EssenceSummonsDueLocked(conn, 207));
    }

    [Fact]
    public void Native_summon_parent_flags_end_children_without_kills_and_ignore_dormancy_or_reused_ids()
    {
        foreach (string effect in new[] { "EssenceDaemonSummonMovingKaruiSpirit", "EssenceDaemonSummonSpectralSkull" })
        {
            var summon = Summon(effect);
            Assert.True(summon.NoRewards);
            Assert.True(summon.DiesWithParent);
            Assert.True(summon.DiesWhenParentDeleted);
            foreach (string end in new[] { "dead", "zero-life", "deleted", "replaced" })
            {
                var conn = Connection();
                var parent = Monster();
                var child = Monster();
                conn.Monsters[42] = parent;
                conn.Monsters[43] = child;
                Game.RegisterMonsterSummonLocked(child, 42, parent, summon);
                Assert.True(child.NoRewards);
                Assert.Equal(42u, child.SummonerId);
                Assert.Equal(0, Game.TickMonsterSummonParentsLocked(conn, 1));
                conn.Monsters.Remove(42);
                conn.Instance.World.DormantMonsters[42] = parent;
                Assert.Equal(0, Game.TickMonsterSummonParentsLocked(conn, 1));
                conn.Instance.World.DormantMonsters.Remove(42);
                conn.Monsters[42] = parent;
                if (end == "dead") parent.Dead = true;
                if (end == "zero-life") parent.Life = 0;
                if (end == "deleted") conn.Monsters.Remove(42);
                if (end == "replaced") conn.Monsters[42] = Monster();
                conn.Clock.Stop();
                Assert.Equal(0, Game.TickMonsterSummonParentsLocked(conn, 2));
                conn.Clock.Start();
                conn.Instance.World.Runner = Connection();
                Assert.Equal(0, Game.TickMonsterSummonParentsLocked(conn, 2));
                conn.Instance.World.Runner = conn;
                Assert.Equal(1, Game.TickMonsterSummonParentsLocked(conn, 2));
                Assert.True(child.Dead);
                Assert.Equal(0u, child.Life);
                Assert.Equal(1, child.ActionGeneration);
                Assert.Empty(conn.Minions.Rewarded);
                Assert.DoesNotContain(conn.Minions.OutputQueue, o => o.Kind == PlayerMinions.OutputKind.PlayerKill);
                Assert.Equal(0, Game.TickMonsterSummonParentsLocked(conn, 3));
            }
        }
        var ordinary = new SkillCatalog.MonsterSummonInfo(5022, 1, 0, false, 1, 6);
        var c = Connection();
        var p = Monster();
        var independent = Monster();
        c.Monsters[43] = independent;
        Game.RegisterMonsterSummonLocked(independent, 42, p, ordinary);
        Assert.Equal(0, Game.TickMonsterSummonParentsLocked(c, 1));
        Assert.False(independent.Dead);
        Assert.False(independent.NoRewards);
    }
}
