using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class EssenceSummonTests : IDisposable
{
    readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
    readonly GameServer.Modding.IModuleLifetime? previousLifetime = Game.CombatLifetime;
    readonly ModuleLifetime lifetime = new("Game");
    public EssenceSummonTests()
    {
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
    }
    public void Dispose()
    {
        lifetime.Stop(ex => throw ex);
        Game.CombatLifetime = previousLifetime;
        Program.SetGameLogic(previousLogic!);
    }
    static ClientState Connection(InstanceState? instance = null) => new(new(1, "essence-summons", 300, 300, 100, 100, null, []), 1, 1)
        { Instance = instance ?? new() };
    static SpawnedMonster Monster() => new("source", new(300, 300, 30), 60, 8, 100) { Level = 70, Aggro = true };
    static SkillCatalog.MonsterSummonInfo Summon(string effect) =>
        Program.Skills.MonsterSummon(Program.Skills.EffectRow(effect), 70)!;

    [Fact]
    public void Karui_daemon_uses_native_summon_data_and_an_awake_carriers_cast_and_cooldown()
    {
        VerifyInsectDataAndSecondaryDamage();
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
        var capped = Monster();
        capped.Mods = source.Mods;
        conn.Monsters[42] = capped;
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
        VerifyDeathQueueAndExplosion();
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
        var retained = Program.Instances.Enter(1208, 505);
        var visitor = Connection(retained);
        var carrier = Monster();
        var orphan = Monster();
        visitor.Monsters[42] = carrier;
        visitor.Monsters[43] = orphan;
        var insect = new SpawnedMonster("insect", new(300, 300, 30), 105, 6, 100)
            { Variety = Game.Monsters.Find(Game.EssenceExplosiveInsect), SummonerId = 42 };
        visitor.Monsters[44] = insect;
        Game.RegisterMonsterSummonLocked(orphan, 42, carrier, Summon("EssenceDaemonSummonMovingKaruiSpirit"));
        Program.Instances.Leave(retained);
        Game.CleanupMonsterSummons().Dispose();
        Assert.True(orphan.Dead);
        Assert.True(insect.Dead);
        Assert.False(carrier.Dead);

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

    sealed class ZeroRolls : Random
    {
        public override int Next(int maxValue) => 0;
        public override int Next(int minValue, int maxValue) => minValue;
        public override double NextDouble() => 0;
    }

    static void VerifyInsectDataAndSecondaryDamage()
    {
        var conn = Connection();
        conn.PlayerLife = 10000;
        conn.Pools = new(70, 10000, 100, 0, 0, FireResistance: 0, BlockChance: 75, SpellBlockChance: 75,
            SpellSuppressionChance: 100);
        conn.IncomingHitRandom = new ZeroRolls();
        var attack = Game.EssenceInsectAttack(conn, 70, 0x2e0a, new(new Dictionary<string, int>()));
        Assert.True(attack.Spell);
        Assert.True(attack.Secondary);
        Assert.Equal(0, attack.Damage[DamageType.Physical].Max);
        Assert.True(attack.Damage[DamageType.Fire].Min > 1000);
        Assert.Equal(3, attack.Seconds);
        Assert.Equal(12, attack.Range);
        Assert.Equal(30, Game.EssenceInsectRadius(conn));
        Assert.Equal(60, Game.EssenceInsectRadius(conn, new(new Dictionary<string, int> { ["base_skill_area_of_effect_+%"] = 300 })));
        var raw = Game.GolemData.Skill(Program.Skills, Program.Skills.EffectRow("InsectSuicideExplosionHuge"), 1).Stats;
        var before = raw.OrderBy(p => p.Key).ToArray();
        var native = Game.EssenceInsectSkillStats(conn, attack, attack.SkillStats()).ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Equal(100, native[Program.Stats.Key("total_skill_area_of_effect_radius_+%_including_final")!.Value]);
        Assert.Equal(3000, native[StatKeys.BaseSpellCastTimeMs]);
        Assert.Equal(before, raw.OrderBy(p => p.Key)); // preparing one actor never contaminates cached raw skill data
        var fixedHit = attack with { Damage = DamagePacket.Of(DamageType.Fire, 1000, 1000) };
        var source = Monster();
        var at = conn.Walk.Position(0);
        var secondary = Game.CommitMonsterHitLocked("secondary", conn, 42, source, fixedHit, 0, at, at, false)!;
        Assert.False(secondary.Blocked);
        Assert.Equal(1000, secondary.Damage); // 100% spell suppression and 75% blocks do not cover secondary damage
        var spell = Game.CommitMonsterHitLocked("spell", conn, 42, source, fixedHit with { Secondary = false }, 1, at, at, false)!;
        Assert.True(spell.Blocked);
        Assert.Equal(0, spell.Damage);
        // The minion refresh reads the connection clock; keep it separate from the supplied hit timeline above.
        conn = Connection();
        conn.IncomingHitRandom = new ZeroRolls();
        var definition = Game.GolemData.Find("SummonFlameGolem")!;
        var ally = new SpawnedMonster("ally", new(300, 300, 30), 60, 8, 10000)
            { Hostile = false, Variety = Game.Monsters.Find(definition.Variety), Level = 70 };
        var fighter = new GolemState.Golem(44, ally, definition, 0);
        conn.Summons.Fighters[44] = fighter;
        conn.Monsters[44] = ally;
        fighter.Own[Program.Stats.Key("base_spell_block_%")!.Value] = 75;
        Assert.False(Game.GolemTakeHitLocked(conn, fighter, 42, source, fixedHit with { Secondary = false },
            conn.Clock.Elapsed.TotalSeconds, area: true));
        uint minionLife = ally.Life;
        Assert.True(Game.GolemTakeHitLocked(conn, fighter, 42, source, fixedHit, conn.Clock.Elapsed.TotalSeconds, area: true));
        Assert.True(ally.Life < minionLife);
        conn.Closed = true;
    }

    static void VerifyDeathQueueAndExplosion()
    {
        var conn = Connection();
        conn.PlayerLife = 10000;
        var source = Monster();
        var mod = Program.Items.Mods.ById("EssenceInsectSummonedOnDeath")!;
        source.Mods = Game.MonsterModRolls.Value.Create([mod.Roll(ModSection.Implicit, new Random(505))], [], MonsterRarity.Rare, 70);
        conn.Monsters[42] = source;
        Assert.False(Game.QueueEssenceDeathSpawnLocked(conn, 42, source));
        source.Dead = true; source.Life = 0;
        Assert.True(Game.QueueEssenceDeathSpawnLocked(conn, 42, source));
        Assert.False(Game.QueueEssenceDeathSpawnLocked(conn, 42, source));
        var pending = Assert.Single(Game.ClaimEssenceDeathSpawnsLocked(conn));
        Assert.Equal(Game.EssenceExplosiveInsect, Game.Monsters.Find(pending.Definition.Variety.ToString())!.Id);
        Assert.Equal(1, pending.Definition.Count);
        Assert.Empty(Game.ClaimEssenceDeathSpawnsLocked(conn));
        var peer = Connection(conn.Instance); peer.PlayerLife = 10000;
        conn.Instance.World.Runner = peer;
        Assert.Same(pending, Assert.Single(Game.ClaimEssenceDeathSpawnsLocked(peer)));
        Assert.False(Game.EssenceDeathSpawnCurrentLocked(conn, pending));
        Game.CompleteEssenceDeathSpawnLocked(conn, pending, true); // a retired claimant cannot consume the new claim
        Assert.True(Game.EssenceDeathSpawnCurrentLocked(peer, pending));
        Game.CompleteEssenceDeathSpawnLocked(peer, pending, false);
        Assert.Same(pending, Assert.Single(Game.ClaimEssenceDeathSpawnsLocked(peer)));
        Game.CompleteEssenceDeathSpawnLocked(peer, pending, true);
        Assert.Empty(Game.ClaimEssenceDeathSpawnsLocked(peer));
        Assert.False(Game.QueueEssenceDeathSpawnLocked(peer, 42, source));
        Assert.Null(Game.EssenceDeathDefinitionFor(0));
        Assert.Null(Game.EssenceDeathDefinitionFor(999));

        var insect = new SpawnedMonster("insect", new(300, 300, 30), 105, 6, 100)
        { Variety = Game.Monsters.Find(Game.EssenceExplosiveInsect), Level = 70, ActionGeneration = 17 };
        peer.Monsters[43] = insect;
        peer.Clock.Stop();
        Assert.Null(Game.CommitEssenceInsectExplosionLocked(peer, 43, insect, 17));
        peer.Clock.Start();
        Assert.Null(Game.CommitEssenceInsectExplosionLocked(peer, 43, insect, 16));
        var blast = Game.CommitEssenceInsectExplosionLocked(peer, 43, insect, 17)!;
        Assert.True(insect.Dead);
        Assert.Equal(0u, insect.Life);
        Assert.True(insect.NoRewards);
        Assert.Null(Game.CommitEssenceInsectExplosionLocked(peer, 43, insect, 17));
        Assert.True(Game.InEssenceInsectBlast(blast, (330, 300)));
        Assert.False(Game.InEssenceInsectBlast(blast, (331, 300)));
        Assert.Empty(peer.Minions.Rewarded);
        Assert.DoesNotContain(peer.Minions.OutputQueue, o => o.Kind == PlayerMinions.OutputKind.PlayerKill);
        conn.Closed = peer.Closed = true;
    }
}
