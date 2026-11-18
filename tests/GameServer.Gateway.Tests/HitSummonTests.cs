using GameServer.Core.Protocol.Instance;
using GameServer.Core.Projectiles;
using System.Reflection;

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
        Game.HitSummonsOnHitLocked(f.Conn, target, Game.SupportedSkill(f.Conn, source).Stats, source, now, random ?? new HighRandom());
    static void Kill(MinionUtilityTests.Fixture f, uint id, SpawnedMonster target, double now)
    {
        target.Life = 0; target.Dead = true;
        Game.CommitAcceptedKillLocked(f.Conn, id, target, now, playerKill: false);
    }

    [Fact]
    public async Task Absolution_marks_non_unique_deaths_consumes_once_and_refreshes_a_living_sentinel_at_the_limit()
    {
        await DominanceRegression();
        await VaalRegression();
        await VaalSentinelRegression();
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
        await PhantasmRegression();
        await PhantasmProjectileRegression();
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
    static async Task DominanceRegression()
    {
        using var f = new MinionUtilityTests.Fixture();
        var clock = new Clock { Time = 1 }; var outer = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, clock);
        try
        {
            ushort source = f.Equip(("Dominating Blow",20)).Single();
            f.Conn.PlayerStats[Program.Stats.Key("minion_damage_+%")!.Value] = 37;
            Assert.Equal(37, Game.CharacterPlayerStats(f.Conn).GetValueOrDefault(Program.Stats.Key("minion_damage_+%")!.Value));
            foreach (var (id, rarity) in new[] { (910u, MonsterRarity.Normal), (911u, MonsterRarity.Magic), (912u, MonsterRarity.Rare) })
            {
                var victim = Target(f,id,rarity);
                if (rarity == MonsterRarity.Rare)
                    victim.Mods = Game.MonsterModRolls.Value.Roll(Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie")!, 68, rarity, new Random(41));
                Mark(f,victim,source,1); Kill(f,id,victim,1);
                await Game.HitSummonsTick("test",f.Stream,f.Ciphers,f.Conn,1);
                var sent = Assert.Single(f.Conn.Summons.Fighters.Values, g => g.Monster.Rarity == rarity);
                Assert.True(victim.CorpseConsumed);
                Assert.Equal(70,sent.Monster.Level);
                Assert.Equal(31,f.Conn.Summons.Until[sent.Id]);
                Assert.Equal(rarity == MonsterRarity.Normal ? 9 : rarity == MonsterRarity.Magic ? 3 : 1,sent.Limit);
                Assert.Equal(victim.Mods.Affixes,sent.InheritedMods.Affixes);
                Assert.Equal(sent.InheritedMods.Effects["damage_+%"] + 37, sent.Monster.Mods.Effects["damage_+%"]);
            }
            var old = f.Conn.Summons.Fighters.Values.Single(g => g.Monster.Rarity == MonsterRarity.Rare);
            var next = Target(f,913,MonsterRarity.Rare); Mark(f,next,source,2); Kill(f,913,next,2);
            clock.Time=2; await Game.HitSummonsTick("test",f.Stream,f.Ciphers,f.Conn,2);
            Assert.False(f.Conn.Summons.Fighters.ContainsKey(old.Id));
            Assert.Single(f.Conn.Summons.Fighters.Values, g => g.Monster.Rarity == MonsterRarity.Rare);
            var boss=Target(f,914,MonsterRarity.Unique); Mark(f,boss,source,2,new ZeroRandom());
            await Game.HitSummonsTick("test",f.Stream,f.Ciphers,f.Conn,2);
            Assert.Equal(2,f.Conn.Summons.Fighters.Values.Count(g=>g.Definition.Kind=="DominatingNormal"));
            Assert.False(boss.CorpseConsumed);
            var shared=Target(f,915); Mark(f,shared,source,2);
            var killer=new ClientState(f.Conn.Layout,1,1) { Instance=f.Conn.Instance,PlayerLife=100,Character="party-killer" };
            var template=new PlayerObject(f.Conn.Character!,0,100,100,1000,1000);
            f.Conn.Instance.PublishPlayer(new(f.Conn.PeerId,1,template,100,100,1000,f.Conn));
            shared.Life=0; shared.Dead=true;
            Game.CommitAcceptedKillLocked(killer,915,shared,2,playerKill:true);
            await Game.HitSummonsTick("test",f.Stream,f.Ciphers,f.Conn,2);
            Assert.True(shared.CorpseConsumed);
            Assert.Equal(3,f.Conn.Summons.Fighters.Values.Count(g=>g.Definition.Kind=="DominatingNormal"));
        }
        finally { Game.AreaUpdateContext.Value=outer; }
    }

    static async Task PhantasmRegression()
    {
        using var f = new MinionUtilityTests.Fixture();
        var clock = new Clock { Time = 1 }; var outer = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, clock);
        try
        {
            f.Equip(("Fireball",20),("Summon Phantasm Support",1));
            var host=Game.ItemsOf(f.Conn).Items(PlayerInventories.Weapon1).Single();
            Game.ItemsOf(f.Conn).Change(PlayerInventories.Weapon1,host.Id,item=>item with { Links=[2] });
            ushort source=Game.GemSkillEffects(f.Conn).Single().Id;
            var rare=Target(f,920,MonsterRarity.Rare);
            Game.PhantasmsOnHitLocked(f.Conn,rare,source,1,new HighRandom()); Assert.Empty(f.Conn.Summons.HitSummons);
            Game.PhantasmsOnHitLocked(f.Conn,rare,source,1,new ZeroRandom());
            await Game.HitSummonsTick("test",f.Stream,f.Ciphers,f.Conn,1);
            var ghost=Assert.Single(f.Conn.Summons.Fighters).Value;
            Assert.Equal("Phantasm",ghost.Definition.Kind); Assert.Equal(4,ghost.Monster.Level);
            Assert.Equal(16,f.Conn.Summons.Until[ghost.Id]);
            Assert.Contains(ghost.Abilities,a=>a.Attack.Spell&&a.Attack.Damage.MaxTotal>0);
            ghost.Monster.Walk.Teleport(100,100,1); Game.GolemStepLocked("test",f.Stream,f.Ciphers,f.Conn,ghost,1);
            Assert.True(ghost.Pending!.Ability.Attack.Spell);
            Game.PhantasmsOnHitLocked(f.Conn,rare,source,1,new ZeroRandom(),ghost); Assert.Empty(f.Conn.Summons.HitSummons);
            rare.Life=0; rare.Dead=true;
            Game.PhantasmsOnHitLocked(f.Conn,rare,source,1,new ZeroRandom());
            Assert.Equal(2,f.Conn.Summons.HitSummons.Count);
            f.Conn.Summons.HitSummons.Clear(); rare.Life=1000; rare.Dead=false;
            for(int i=0;i<7;i++) Game.PhantasmsOnHitLocked(f.Conn,rare,source,1,new ZeroRandom());
            await Game.HitSummonsTick("test",f.Stream,f.Ciphers,f.Conn,1);
            Assert.Equal(5,f.Conn.Summons.Fighters.Count);
            Game.PhantasmsOnHitLocked(f.Conn,rare,source,1,new ZeroRandom());
            Game.ItemsOf(f.Conn).Change(PlayerInventories.Weapon1,host.Id,item=>item with { Links=[1,1] });
            await Game.HitSummonsTick("test",f.Stream,f.Ciphers,f.Conn,1);
            Assert.Equal(5,f.Conn.Summons.Fighters.Count);
            await Game.SummonsTick("test",f.Stream,f.Ciphers,f.Conn,1);
            Assert.Empty(f.Conn.Summons.Fighters);
        }
        finally { Game.AreaUpdateContext.Value=outer; }
    }

    static async Task PhantasmProjectileRegression()
    {
        double baselineSpeed = 0;
        foreach (bool supported in new[] { false, true })
        {
            using var f = new MinionUtilityTests.Fixture();
            var clock = new Clock { Time = 1 }; var outer = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, clock);
            try
            {
                f.Equip(supported
                    ? [("Fireball", 20), ("Summon Phantasm Support", 1), ("Greater Multiple Projectiles Support", 20), ("Faster Projectiles Support", 20)]
                    : [("Fireball", 20), ("Summon Phantasm Support", 1)]);
                var host = Game.ItemsOf(f.Conn).Items(PlayerInventories.Weapon1).Single();
                Game.ItemsOf(f.Conn).Change(PlayerInventories.Weapon1, host.Id, item => item with { Links = [(byte)item.Sockets.Count] });
                ushort source = Game.GemSkillEffects(f.Conn).Single().Id;
                var target = Target(f, 940, MonsterRarity.Rare);
                target.Walk.Teleport(140, 100, 1);
                Game.PhantasmsOnHitLocked(f.Conn, target, source, 1, new ZeroRandom());
                await Game.HitSummonsTick("test", f.Stream, f.Ciphers, f.Conn, 1);
                var ghost = Assert.Single(f.Conn.Summons.Fighters).Value;
                f.Conn.Clock.Reset();
                ghost.Monster.Walk.Teleport(100, 100, 1);
                var ability = ghost.Abilities.First(a => a.Effect is 9427 or 9428);
                Assert.NotNull(ability.Projectile);
                typeof(Game).GetMethod("GolemAbilityContactLocked", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
                    .Invoke(null, ["test", f.Stream, f.Ciphers, f.Conn, ghost,
                        new GolemState.Action(ability, 940, target, (140, 100), 1, ghost.Monster.ActionGeneration), 1d]);
                var session = Game.ProjectilesFor("test", f.Stream, f.Ciphers, f.Conn);
                session.AdvanceTo(ProjectileRuntime.Time(1));
                var shots = session.Runtime.Projectiles.ToArray();
                Assert.Equal(supported ? 5 : 1, shots.Length);
                Assert.All(shots, shot => Assert.True(shot.Use.Profile.Pierce < 0 || shot.Use.Profile.Pierce == int.MaxValue));
                if (!supported) baselineSpeed = Assert.Single(shots).Speed;
                else
                {
                    Assert.Equal(5, shots.Select(shot => shot.Direction).Distinct().Count());
                    int speed = ghost.Own[Program.Stats.Key("base_projectile_speed_+%")!.Value];
                    Assert.True(speed > 0);
                    Assert.Equal(baselineSpeed * (1 + speed / 100d), shots[0].Speed, 3);
                }
            }
            finally { Game.AreaUpdateContext.Value = outer; }
        }
    }

    static async Task VaalRegression()
    {
        using var f = new MinionUtilityTests.Fixture();
        var clock = new Clock { Time=1 }; var outer=Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value=new(Game.CombatLifetime!,clock);
        try
        {
            f.Equip(("Vaal Summon Skeletons",1));
            var vaal=Game.GemSkillEffects(f.Conn).Single(g=>Game.SkillsFor(f.Conn).EffectId(g.Effect)=="VaalSummonSkeletons");
            Game.RefreshVaalSouls(f.Conn);
            bool Admit()=>Game.TryBeginSkillCooldownUse(f.Conn,vaal.Id,vaal.Effect,null,false,()=>f.Conn.ActionCount++,out _);
            Assert.False(Admit());
            for(int i=0;i<50;i++) Assert.True(f.Conn.VaalSouls.Gain());
            Assert.True(Admit());
            Assert.True(await Game.CastGemSkill("test",f.Stream,f.Ciphers,f.Conn,vaal.Id,(100,100),(120,100)));
            Assert.Equal(0u,Assert.Single(f.Conn.VaalSouls.Read()).Current);
            Assert.Equal(18,f.Conn.Summons.Fighters.Count);
            foreach(var (kind,count) in new[]{("Melee",14),("Ranged",2),("Spellcaster",1),("General",1)})
                Assert.Equal(count,f.Conn.Summons.Fighters.Values.Count(g=>g.Monster.Variety!.Id.Contains(kind,StringComparison.Ordinal)));
            var archer=f.Conn.Summons.Fighters.Values.First(g=>g.Monster.Variety!.Id.Contains("Ranged",StringComparison.Ordinal));
            Assert.Contains(archer.Abilities,a=>a.Projectile is not null);
            var enemy=Target(f,930); archer.Monster.Walk.Teleport(500,500,clock.Time); enemy.Walk.Teleport(510,500,clock.Time);
            Game.GolemStepLocked("test",f.Stream,f.Ciphers,f.Conn,archer,clock.Time);
            Assert.Equal(930u,archer.Target);
            clock.Time=22; await Game.SummonsTick("test",f.Stream,f.Ciphers,f.Conn,clock.Time); Assert.Empty(f.Conn.Summons.Fighters);

            // The native supports apply, but Vaal proxy use budgets are not implemented. Reject before payment.
            foreach (string support in new[] { "Spell Totem Support", "Trap Support", "Blastchain Mine Support" })
            {
                f.Equip(("Vaal Summon Skeletons",1),(support,1));
                var host = Game.ItemsOf(f.Conn).Items(PlayerInventories.Weapon1).Single();
                Game.ItemsOf(f.Conn).Change(PlayerInventories.Weapon1,host.Id,item=>item with { Links=[2] });
                vaal = Game.GemSkillEffects(f.Conn).Single(g=>Game.SkillsFor(f.Conn).EffectId(g.Effect)=="VaalSummonSkeletons");
                var supported = Game.SupportedSkill(f.Conn,vaal.Id);
                Assert.True(Game.IsTotemSkill(f.Conn,vaal.Id) || Game.PlacedKind(supported) is not (false,false));
                Assert.Equal("Vaal minion skills through totems, traps or mines are not implemented",
                    Game.GemSkillInstanceDisabled(f.Conn,vaal.Effect,vaal.Id));
                var ordinary = Game.GemSkillEffects(f.Conn).Single(g=>Game.SkillsFor(f.Conn).EffectId(g.Effect)=="SummonSkeletons");
                Assert.Null(Game.ActorSkillInstanceDisabled(f.Conn,ordinary.Effect,ordinary.Id));
                Game.RefreshVaalSouls(f.Conn);
                while (Assert.Single(f.Conn.VaalSouls.Read()).Current < 50) Assert.True(f.Conn.VaalSouls.Gain());
                int action = f.Conn.ActionCount, cooldowns = f.Conn.SingleUseCooldowns.Count;
                var deadlines = f.Conn.SkillReadyAt.ToArray();
                uint mana = f.Conn.PlayerMana;
                double prevention = f.Conn.VaalPreventionUntil;
                var admission = f.Conn.VaalUse;
                Assert.False(Admit());
                Assert.False(await Game.CastGemSkill("test",f.Stream,f.Ciphers,f.Conn,vaal.Id,(100,100),(120,100)));
                Assert.Equal(50u,Assert.Single(f.Conn.VaalSouls.Read()).Current);
                Assert.Equal(action,f.Conn.ActionCount);
                Assert.Equal(mana,f.Conn.PlayerMana);
                Assert.Equal(prevention,f.Conn.VaalPreventionUntil);
                Assert.Same(admission,f.Conn.VaalUse);
                Assert.Equal(cooldowns,f.Conn.SingleUseCooldowns.Count);
                Assert.Equal(deadlines,f.Conn.SkillReadyAt.ToArray());
                Assert.Empty(f.Conn.Summons.Fighters);
                Assert.Empty(f.Conn.Totems.All);
                Assert.Empty(f.Conn.Placed.All);
            }

        }
        finally { Game.AreaUpdateContext.Value=outer; }
    }

    static async Task VaalSentinelRegression()
    {
        Assert.Equal("02BC00001000000000000000001509000102000200000404C132000300000404C13200",
            Convert.ToHexString(InstanceEntities.PlayerMinionsChange(0x1000,
                [new(0x4000002,0xc104,50), new(0x4000003,0xc104,50)])));
        using var f = new MinionUtilityTests.Fixture();
        var clock = new Clock { Time = 1 }; var outer = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, clock);
        try
        {
            foreach (bool absolution in new[] { true, false })
            {
                f.Equip((absolution ? "Vaal Absolution" : "Vaal Domination",20));
                string vaalId = absolution ? "VaalAbsolution" : "VaalDomination";
                var vaal = Game.GemSkillEffects(f.Conn).Single(g => Game.SkillsFor(f.Conn).EffectId(g.Effect) == vaalId);
                ushort source = Game.GemSkillEffects(f.Conn).Single(g => Game.SkillsFor(f.Conn).EffectId(g.Effect)
                    == (absolution ? "Absolution" : "DominatingBlow")).Id;
                Game.RefreshVaalSouls(f.Conn); for (int j=0;j<90;j++) f.Conn.VaalSouls.Gain();
                bool Admit() => Game.TryBeginSkillCooldownUse(f.Conn, vaal.Id, vaal.Effect, null, false,
                    () => f.Conn.ActionCount++, out _);
                Assert.False(Admit());
                var victim = Target(f,940,MonsterRarity.Rare); Mark(f,victim,source,clock.Time); Kill(f,940,victim,clock.Time);
                await Game.HitSummonsTick("test",f.Stream,f.Ciphers,f.Conn,clock.Time);
                var normal = Assert.Single(f.Conn.Summons.Fighters).Value;
                Assert.NotNull(Game.SentinelOwnershipPresentation(f.Conn));
                var owned = Assert.Single(f.Conn.Summons.PresentedOwnership).Value;
                Assert.Equal((ushort)(0xc000 | vaal.Id), owned.Source);
                var proxy=Game.PlayerSkills(f.Conn).Single(s=>s.Id==owned.Source);
                var active=Game.PlayerSkills(f.Conn).Single(s=>s.Id==vaal.Id);
                Assert.Equal(absolution?0:1, active.Stats!.Single(s=>s.Key==Program.Stats.Key("skill_behaviour_variation")).Value);
                Assert.Equal(((uint)0x1000, source), proxy.Parent!.Value);
                Assert.Equal(absolution ? 50 : 3, owned.Type);
                Assert.Null(Game.SentinelOwnershipPresentation(f.Conn));
                normal.Monster.Life = normal.Monster.MaxLife / 2;
                Assert.True(Admit());
                Assert.True(await Game.CastGemSkill("test",f.Stream,f.Ciphers,f.Conn,vaal.Id,(100,100),(120,100),normal.Id));
                var ascended = Assert.Single(f.Conn.Summons.Fighters).Value;
                Assert.NotEqual(normal.Id,ascended.Id); Assert.Equal(vaalId,ascended.Definition.Kind);
                Assert.Equal(normal.Monster.Level,ascended.Monster.Level);
                Assert.Equal(source,ascended.Source); Assert.Same(normal.InheritedMods,ascended.InheritedMods);
                Assert.NotNull(Game.SentinelOwnershipPresentation(f.Conn));
                Assert.Equal(absolution ? 51 : 4,Assert.Single(f.Conn.Summons.PresentedOwnership).Value.Type);
                Assert.Equal(300,ascended.Own[Program.Stats.Key("vaal_upgrade_minion_damage_+%_final")!.Value]);
                Assert.Equal(-70,ascended.Own[Program.Stats.Key("vaal_upgrade_minion_damage_taken_+%_final")!.Value]);
                clock.Time++; await Game.SummonsTick("test",f.Stream,f.Ciphers,f.Conn,clock.Time);
                Assert.Equal(300,ascended.Own[Program.Stats.Key("vaal_upgrade_minion_damage_+%_final")!.Value]);
                if (!absolution)
                    for (uint extra=941;extra<=942;extra++)
                    {
                        var next=Target(f,extra,MonsterRarity.Rare); Mark(f,next,source,clock.Time); Kill(f,extra,next,clock.Time);
                        await Game.HitSummonsTick("test",f.Stream,f.Ciphers,f.Conn,clock.Time);
                        var body=f.Conn.Summons.Fighters.Values.Single(g=>g.Definition.Kind=="DominatingRare");
                        for(int j=0;j<90;j++) f.Conn.VaalSouls.Gain();
                        Assert.True(Admit());
                        Assert.True(await Game.CastGemSkill("test",f.Stream,f.Ciphers,f.Conn,vaal.Id,(100,100),(120,100),body.Id));
                    }
                int count=f.Conn.Summons.Fighters.Count;
                Assert.Equal(absolution?1:3,count);
                uint life=ascended.Monster.Life; double oldUntil=f.Conn.Summons.Until[ascended.Id]; clock.Time++;
                for(int j=0;j<90;j++) f.Conn.VaalSouls.Gain();
                Assert.True(Admit());
                Assert.True(await Game.CastGemSkill("test",f.Stream,f.Ciphers,f.Conn,vaal.Id,(100,100),(120,100),ascended.Id));
                Assert.Equal(count,f.Conn.Summons.Fighters.Count); Assert.Same(ascended,f.Conn.Summons.Fighters[ascended.Id]);
                Assert.Equal(life,ascended.Monster.Life); Assert.True(f.Conn.Summons.Until[ascended.Id]>oldUntil);
                _ = Game.SentinelOwnershipPresentation(f.Conn);
                clock.Time+=35; await Game.SummonsTick("test",f.Stream,f.Ciphers,f.Conn,clock.Time);
                Assert.Empty(f.Conn.Summons.Fighters); Assert.NotNull(Game.SentinelOwnershipPresentation(f.Conn));
                Assert.Empty(f.Conn.Summons.PresentedOwnership); f.Conn.NextAttackAt=0;
                foreach (string support in new[] { "Spell Totem Support", "Trap Support", "Blastchain Mine Support" })
                {
                    f.Equip((absolution ? "Vaal Absolution" : "Vaal Domination",20),(support,1));
                    var host=Game.ItemsOf(f.Conn).Items(PlayerInventories.Weapon1).Single();
                    Game.ItemsOf(f.Conn).Change(PlayerInventories.Weapon1,host.Id,item=>item with { Links=[2] });
                    var candidate=Game.GemSkillEffects(f.Conn).Single(g=>Game.SkillsFor(f.Conn).EffectId(g.Effect)==vaalId);
                    var applied=Game.SupportedSkill(f.Conn,candidate.Id);
                    if (Game.IsTotemSkill(f.Conn,candidate.Id) || Game.PlacedKind(applied) is not (false,false))
                        Assert.Equal("Vaal minion skills through totems, traps or mines are not implemented",
                            Game.GemSkillInstanceDisabled(f.Conn,candidate.Effect,candidate.Id));
                }
            }
        }
        finally { Game.AreaUpdateContext.Value=outer; }
    }

}
