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
    static Player Connection(InstanceState? instance = null) => new(new(1, "essence-summons", 300, 300, 100, 100, null, []), 1, 1)
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
        var player = Connection();
        var source = Monster();
        var mod = Program.Items.Mods.ById(Game.EssenceKaruiSummonMod)!;
        source.Mods = Game.MonsterModRolls.Create([mod.Roll(ModSection.Implicit, new Random(505))], [], MonsterRarity.Rare, 70);
        player.Area.Monsters[42] = source;
        Assert.Empty(Game.EssenceSummonsDueLocked(player, 0));
        Assert.Empty(Game.EssenceSummonsDueLocked(player, .999));
        Assert.Same(source, Assert.Single(Game.EssenceSummonsDueLocked(player, 1)).Monster);
        Assert.Empty(Game.EssenceSummonsDueLocked(player, 1));
        Assert.Empty(Game.EssenceSummonsDueLocked(player, 5.999));
        Assert.Empty(Game.EssenceSummonsDueLocked(player, 6));
        Assert.Single(Game.EssenceSummonsDueLocked(player, 7));
        source.Asleep = true;
        Assert.Empty(Game.EssenceSummonsDueLocked(player, 100));
        source.Asleep = false;
        Assert.Empty(Game.EssenceSummonsDueLocked(player, 100));
        Assert.Single(Game.EssenceSummonsDueLocked(player, 101)); // no catch-up waves
        Assert.Empty(Game.EssenceSummonsDueLocked(player, 106));
        source.Dead = true;
        Assert.Empty(Game.EssenceSummonsDueLocked(player, 107));
        Assert.False(Game.MonsterSummonerCurrentLocked(player, 42, source));
        source.Dead = false;
        player.Area.Monsters[42] = Monster();
        Assert.False(Game.MonsterSummonerCurrentLocked(player, 42, source));
        player.Area.Monsters[42] = source;
        player.Closed = true;
        Assert.Empty(Game.EssenceSummonsDueLocked(player, 107));
        Assert.False(Game.MonsterSummonerCurrentLocked(player, 42, source));
        var variety = Game.Monsters.Find(Game.EssenceKaruiSpirit)!;
        var kit = BossKits.Kit(Connection(), variety, 70);
        Assert.Equal(new[] { "Melee", "EssenceKaruiSpiritHeavyStrike" }, kit.Select(s => Program.Skills.EffectId(s.Effect)));
        Assert.True(kit[1].Attack.Damage.MinTotal > kit[0].Attack.Damage.MinTotal);
        Assert.True(kit[1].Attack.Seconds < kit[0].Attack.Seconds);
        var spirit = Monster();
        spirit.Mods = Game.EssenceSummonModifiers(variety)!;
        Assert.Equal(0, Game.MonsterTakes(spirit, DamagePacket.Of(DamageType.Physical, 1000, 1000), new Random(505), false));
        player.Closed = false;
        var capped = Monster();
        capped.Mods = source.Mods;
        player.Area.Monsters[42] = capped;
        for (uint id = 50; id < 53; id++)
            player.Area.Monsters[id] = new("spirit", new(300, 300, 30), 60, 8, 100) { Variety = variety, SummonerId = 42 };
        Assert.False(Game.EssenceSummonCanSpawnLocked(player, 42, variety));
        Assert.True(Game.EssenceSummonCanSpawnLocked(player, 43, variety)); // each carrier owns its cap
        Assert.Empty(Game.EssenceSummonsDueLocked(player, 200));
        Assert.Empty(Game.EssenceSummonsDueLocked(player, 201));
        player.Area.Monsters[50].Dead = true;
        Assert.True(Game.EssenceSummonCanSpawnLocked(player, 42, variety));
        Assert.Empty(Game.EssenceSummonsDueLocked(player, 206));
        Assert.Single(Game.EssenceSummonsDueLocked(player, 207));
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
                var player = Connection();
                var parent = Monster();
                var child = Monster();
                player.Area.Monsters[42] = parent;
                player.Area.Monsters[43] = child;
                Game.RegisterMonsterSummonLocked(child, 42, parent, summon);
                Assert.True(child.NoRewards);
                Assert.Equal(42u, child.SummonerId);
                Assert.Equal(0, Game.TickMonsterSummonParentsLocked(player, 1));
                player.Area.Monsters.Remove(42);
                player.Area.DormantMonsters[42] = parent;
                Assert.Equal(0, Game.TickMonsterSummonParentsLocked(player, 1));
                player.Area.DormantMonsters.Remove(42);
                player.Area.Monsters[42] = parent;
                if (end == "dead") parent.Dead = true;
                if (end == "zero-life") parent.Life = 0;
                if (end == "deleted") player.Area.Monsters.Remove(42);
                if (end == "replaced") player.Area.Monsters[42] = Monster();
                player.Clock.Stop();
                Assert.Equal(0, Game.TickMonsterSummonParentsLocked(player, 2));
                player.Clock.Start();
                player.Instance.World.Runner = Connection();
                Assert.Equal(0, Game.TickMonsterSummonParentsLocked(player, 2));
                player.Instance.World.Runner = player;
                Assert.Equal(1, Game.TickMonsterSummonParentsLocked(player, 2));
                Assert.True(child.Dead);
                Assert.Equal(0u, child.Life);
                Assert.Equal(1, child.ActionGeneration);
                Assert.Empty(player.Minions.Rewarded);
                Assert.DoesNotContain(player.Minions.OutputQueue, o => o.Kind == PlayerMinions.OutputKind.PlayerKill);
                Assert.Equal(0, Game.TickMonsterSummonParentsLocked(player, 3));
            }
        }
        var retained = Program.Instances.Enter(1208, 505);
        var visitor = Connection(retained);
        var carrier = Monster();
        var orphan = Monster();
        visitor.Area.Monsters[42] = carrier;
        visitor.Area.Monsters[43] = orphan;
        var insect = new SpawnedMonster("insect", new(300, 300, 30), 105, 6, 100)
            { Variety = Game.Monsters.Find(Game.EssenceExplosiveInsect), SummonerId = 42 };
        visitor.Area.Monsters[44] = insect;
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
        c.Area.Monsters[43] = independent;
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
        var player = Connection();
        player.PlayerLife = 10000;
        player.Pools = new(70, 10000, 100, 0, 0, FireResistance: 0, BlockChance: 75, SpellBlockChance: 75,
            SpellSuppressionChance: 100);
        player.IncomingHitRandom = new ZeroRolls();
        var attack = Game.EssenceInsectAttack(player, 70, 0x2e0a, new(new Dictionary<uint, int>()));
        Assert.True(attack.Spell);
        Assert.True(attack.Secondary);
        Assert.Equal(0, attack.Damage[DamageType.Physical].Max);
        Assert.True(attack.Damage[DamageType.Fire].Min > 1000);
        Assert.Equal(3, attack.Seconds);
        Assert.Equal(12, attack.Range);
        Assert.Equal(30, Game.EssenceInsectRadius(player));
        Assert.Equal(60, Game.EssenceInsectRadius(player, new(new Dictionary<uint, int> { [Stat.BaseSkillAreaOfEffectPlusPct] = 300 })));
        var raw = Game.GolemData.Skill(Program.Skills, Program.Skills.EffectRow("InsectSuicideExplosionHuge"), 1).Stats;
        var before = raw.OrderBy(p => p.Key).ToArray();
        var native = Game.EssenceInsectSkillStats(player, attack, attack.SkillStats()).ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Equal(100, native[Stat.TotalSkillAreaOfEffectRadiusPlusPctIncludingFinal]);
        Assert.Equal(3000, native[Stat.BaseSpellCastTimeMs]);
        Assert.Equal(before, raw.OrderBy(p => p.Key)); // preparing one actor never contaminates cached raw skill data
        var fixedHit = attack with { Damage = DamagePacket.Of(DamageType.Fire, 1000, 1000) };
        var source = Monster();
        var at = player.Walk.Position(0);
        var secondary = Game.CommitMonsterHitLocked(player, 42, source, fixedHit, 0, at, at, false)!;
        Assert.False(secondary.Blocked);
        Assert.Equal(1000, secondary.Damage); // 100% spell suppression and 75% blocks do not cover secondary damage
        var spell = Game.CommitMonsterHitLocked(player, 42, source, fixedHit with { Secondary = false }, 1, at, at, false)!;
        Assert.True(spell.Blocked);
        Assert.Equal(0, spell.Damage);
        // The minion refresh reads the connection clock; keep it separate from the supplied hit timeline above.
        player = Connection();
        player.IncomingHitRandom = new ZeroRolls();
        var definition = Game.GolemData.Find("SummonFlameGolem")!;
        var ally = new SpawnedMonster("ally", new(300, 300, 30), 60, 8, 10000)
            { Hostile = false, Variety = Game.Monsters.Find(definition.Variety), Level = 70 };
        var fighter = new GolemState.Golem(44, ally, definition, 0);
        player.Summons.Fighters[44] = fighter;
        player.Area.Monsters[44] = ally;
        fighter.Own[Stat.BaseSpellBlockPct] = 75;
        Assert.False(Game.GolemTakeHitLocked(player, fighter, 42, source, fixedHit with { Secondary = false },
            Game.AreaTime(player), area: true));
        uint minionLife = ally.Life;
        Assert.True(Game.GolemTakeHitLocked(player, fighter, 42, source, fixedHit, Game.AreaTime(player), area: true));
        Assert.True(ally.Life < minionLife);
        player.Closed = true;
    }

    static void VerifyDeathQueueAndExplosion()
    {
        var player = Connection();
        player.PlayerLife = 10000;
        var source = Monster();
        var mod = Program.Items.Mods.ById("EssenceInsectSummonedOnDeath")!;
        source.Mods = Game.MonsterModRolls.Create([mod.Roll(ModSection.Implicit, new Random(505))], [], MonsterRarity.Rare, 70);
        player.Area.Monsters[42] = source;
        Assert.False(Game.QueueEssenceDeathSpawnLocked(player, 42, source));
        source.Dead = true; source.Life = 0;
        Assert.True(Game.QueueEssenceDeathSpawnLocked(player, 42, source));
        Assert.False(Game.QueueEssenceDeathSpawnLocked(player, 42, source));
        var pending = Assert.Single(Game.ClaimEssenceDeathSpawnsLocked(player));
        Assert.Equal(Game.EssenceExplosiveInsect, Game.Monsters.Find(pending.Definition.Variety.ToString())!.Id);
        Assert.Equal(1, pending.Definition.Count);
        Assert.Empty(Game.ClaimEssenceDeathSpawnsLocked(player));
        var peer = Connection(player.Instance); peer.PlayerLife = 10000;
        player.Instance.World.Runner = peer;
        Assert.Same(pending, Assert.Single(Game.ClaimEssenceDeathSpawnsLocked(peer)));
        Assert.False(Game.EssenceDeathSpawnCurrentLocked(player, pending));
        Game.CompleteEssenceDeathSpawnLocked(player, pending, true); // a retired claimant cannot consume the new claim
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
        peer.Area.Monsters[43] = insect;
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
        player.Closed = peer.Closed = true;
    }
}
