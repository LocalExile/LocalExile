using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class HitSummonTests
{
    sealed class Clock : IPlayerUpdateSchedule
    {
        internal double Time;
        public double Now(ClientState conn) => Time;
        public Task Wait(int milliseconds, CancellationToken stopping)
        { stopping.ThrowIfCancellationRequested(); Time += milliseconds / 1000d; return Task.CompletedTask; }
    }
    sealed class ZeroRandom : Random { public override int Next(int maxValue) => 0; }
    sealed class HighRandom : Random { public override int Next(int maxValue) => maxValue - 1; }
    static SpawnedMonster Target(MinionUtilityTests.Fixture f, uint id, MonsterRarity rarity = MonsterRarity.Normal) =>
        f.Conn.Monsters[id] = new("victim", new(100,100,0), 100, 3, 1000) { Hostile = true, Rarity = rarity };
    static void Mark(MinionUtilityTests.Fixture f, SpawnedMonster target, ushort source, double now, Random? random = null) =>
        Game.AbsolutionOnHitLocked(f.Conn, target, Game.SupportedSkill(f.Conn, source).Stats, source, now, random ?? new HighRandom());
    static void Kill(MinionUtilityTests.Fixture f, uint id, SpawnedMonster target, double now)
    {
        target.Life = 0; target.Dead = true;
        Game.CommitAcceptedKillLocked(f.Conn, id, target, now, playerKill: false);
    }

    [Fact]
    public async Task Absolution_marks_non_unique_deaths_consumes_once_and_refreshes_a_living_sentinel_at_the_limit()
    {
        using var f = new MinionUtilityTests.Fixture();
        var clock = new Clock { Time = 1 }; var outer = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, clock);
        try
        {
            ushort source = f.Equip(("Absolution",20)).Single();
            Assert.Null(Game.SummonData.Find("Absolution")); // The damaging spell keeps its ordinary executor.
            var victim = Target(f,900); victim.Ignites.Add(0, 1, 1000); Mark(f,victim,source,clock.Time);
            Assert.Single(f.Conn.Summons.HitMarks); Assert.Empty(f.Conn.Summons.HitSummons);
            clock.Time = 2; Kill(f,900,victim,clock.Time); Kill(f,900,victim,clock.Time);
            Assert.Single(f.Conn.Summons.HitSummons);
            await Game.HitSummonsTick("test",f.Stream,f.Ciphers,f.Conn,clock.Time);
            var first = Assert.Single(f.Conn.Summons.Fighters).Value;
            Assert.True(victim.CorpseConsumed); Assert.Equal(70,first.Monster.Level);
            Assert.Equal(1, f.Conn.IgnitedCorpsesConsumed);
            Assert.Equal("Metadata/Monsters/TemplarJudge/AbsolutionTemplarJudge",first.Monster.Variety!.Id);
            Assert.Equal(17,f.Conn.Summons.Until[first.Id]);
            Assert.Contains(first.Abilities,a=>a.Attack.Spell&&a.Attack.Damage.MaxTotal>0);
            Target(f,999); f.Conn.Walk.Teleport(100,100,2); first.Monster.Walk.Teleport(100,100,2);
            Game.GolemStepLocked("test",f.Stream,f.Ciphers,f.Conn,first,2);
            Assert.True(first.Pending!.Ability.Attack.Spell);
            Assert.Contains(f.Conn.Minions.OutputQueue,p=>p.Log=="Sentinel corpse consumed"&&p.WorldId==900);
            for(uint id=901;id<903;id++)
            { var next=Target(f,id); Mark(f,next,source,2); Kill(f,id,next,2); }
            await Game.HitSummonsTick("test",f.Stream,f.Ciphers,f.Conn,2); Assert.Equal(3,f.Conn.Summons.Fighters.Count);
            first.Monster.Life=1;
            var fourth=Target(f,903); fourth.Ignites.Add(0, 1, 1000); Mark(f,fourth,source,3); Kill(f,903,fourth,3); clock.Time=3;
            await Game.HitSummonsTick("test",f.Stream,f.Ciphers,f.Conn,3);
            Assert.Equal(3,f.Conn.Summons.Fighters.Count); Assert.Same(first,f.Conn.Summons.Fighters[first.Id]);
            Assert.Equal(first.Monster.MaxLife,first.Monster.Life); Assert.Equal(18,f.Conn.Summons.Until[first.Id]);
            Assert.True(fourth.CorpseConsumed);
            Assert.Equal(2, f.Conn.IgnitedCorpsesConsumed);
            await Game.HitSummonsTick("test", f.Stream, f.Ciphers, f.Conn, 3);
            Assert.Equal(2, f.Conn.IgnitedCorpsesConsumed);
            var expired=Target(f,904); Mark(f,expired,source,3); Kill(f,904,expired,6);
            Assert.Empty(f.Conn.Summons.HitSummons); Assert.False(expired.CorpseConsumed);
            clock.Time=18; await Game.SummonsTick("test",f.Stream,f.Ciphers,f.Conn,clock.Time);
            Assert.Empty(f.Conn.Summons.Fighters);
        }
        finally { Game.AreaUpdateContext.Value=outer; }
    }

    [Fact]
    public async Task Rare_and_unique_hits_roll_without_consuming_corpses_and_invalidated_sources_or_shattered_deaths_do_not_spawn()
    {
        using var f = new MinionUtilityTests.Fixture();
        var clock = new Clock { Time = 1 }; var outer = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, clock);
        try
        {
            ushort source=f.Equip(("Absolution",20)).Single();
            var normal=Target(f,900); Mark(f,normal,source,1,new ZeroRandom()); Assert.Empty(f.Conn.Summons.HitSummons);
            var rare=Target(f,901,MonsterRarity.Rare); Mark(f,rare,source,1,new HighRandom()); Assert.Empty(f.Conn.Summons.HitSummons);
            Mark(f,rare,source,1,new ZeroRandom()); Assert.Single(f.Conn.Summons.HitSummons);
            var boss=Target(f,902,MonsterRarity.Unique); Mark(f,boss,source,1,new ZeroRandom());
            await Game.HitSummonsTick("test",f.Stream,f.Ciphers,f.Conn,1);
            Assert.Equal(2,f.Conn.Summons.Fighters.Count); Assert.False(rare.CorpseConsumed); Assert.False(boss.CorpseConsumed);
            Kill(f,902,boss,1); Assert.Empty(f.Conn.Summons.HitSummons);
            var shattered=Target(f,903); Mark(f,shattered,source,1); shattered.Ailments.Shatter(); Kill(f,903,shattered,1);
            await Game.HitSummonsTick("test",f.Stream,f.Ciphers,f.Conn,1);
            Assert.Equal(2,f.Conn.Summons.Fighters.Count); Assert.False(shattered.CorpseConsumed);
            var removed=Target(f,904); Mark(f,removed,source,1); Kill(f,904,removed,1);
            f.Equip(("Summon Skeletons",20));
            await Game.HitSummonsTick("test",f.Stream,f.Ciphers,f.Conn,1);
            Assert.Equal(2,f.Conn.Summons.Fighters.Count); Assert.False(removed.CorpseConsumed);
            source=f.Equip(("Absolution",20)).Single();
            var reused=Target(f,905); Mark(f,reused,source,1); Kill(f,905,reused,1); Target(f,905);
            await Game.HitSummonsTick("test",f.Stream,f.Ciphers,f.Conn,1);
            Assert.Equal(2,f.Conn.Summons.Fighters.Count); Assert.False(reused.CorpseConsumed);
        }
        finally { Game.AreaUpdateContext.Value=outer; }
    }
}
