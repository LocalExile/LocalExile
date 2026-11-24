using GameServer.Core.Protocol.Instance;
using GameServer.Core.Projectiles;
using System.Reflection;

namespace GameServer.Gateway.Tests;

public sealed class HitSummonTests
{
    sealed class Clock : IPlayerUpdateSchedule
    {
        internal double Time;
        public double Now(Player player) => Time;
        public Task Wait(int milliseconds, CancellationToken stopping)
        { stopping.ThrowIfCancellationRequested(); Time += milliseconds / 1000d; return Task.CompletedTask; }
    }
    sealed class ZeroRandom : Random { public override int Next(int maxValue) => 0; }
    sealed class HighRandom : Random { public override int Next(int maxValue) => maxValue - 1; }
    static SpawnedMonster Target(MinionUtilityTests.Fixture f, uint id, MonsterRarity rarity = MonsterRarity.Normal) =>
        f.Player.Area.Monsters[id] = new("victim", new(100,100,0), 100, 3, 1000) { Hostile = true, Rarity = rarity };
    static void Mark(MinionUtilityTests.Fixture f, SpawnedMonster target, ushort source, double now, Random? random = null) =>
        Game.HitSummonsOnHitLocked(f.Player, target, Game.SupportedSkill(f.Player, source).Stats, source, now, random ?? new HighRandom());
    static void Kill(MinionUtilityTests.Fixture f, uint id, SpawnedMonster target, double now)
    {
        target.Life = 0; target.Dead = true;
        Game.CommitAcceptedKillLocked(f.Player, id, target, now, playerKill: false);
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
            var normal=Target(f,900); Mark(f,normal,source,1,new ZeroRandom()); Assert.Empty(f.Player.Summons.HitSummons);
            var rare=Target(f,901,MonsterRarity.Rare); Mark(f,rare,source,1,new HighRandom()); Assert.Empty(f.Player.Summons.HitSummons);
            Mark(f,rare,source,1,new ZeroRandom()); Assert.Single(f.Player.Summons.HitSummons);
            var boss=Target(f,902,MonsterRarity.Unique); Mark(f,boss,source,1,new ZeroRandom());
            await Game.HitSummonsTick(f.Player,1);
            Assert.Equal(2,f.Player.Summons.Fighters.Count); Assert.False(rare.CorpseConsumed); Assert.False(boss.CorpseConsumed);
            Kill(f,902,boss,1); Assert.Empty(f.Player.Summons.HitSummons);
            var shattered=Target(f,903); Mark(f,shattered,source,1); shattered.Ailments.Shatter(); Kill(f,903,shattered,1);
            await Game.HitSummonsTick(f.Player,1);
            Assert.Equal(2,f.Player.Summons.Fighters.Count); Assert.False(shattered.CorpseConsumed);
            var removed=Target(f,904); Mark(f,removed,source,1); Kill(f,904,removed,1);
            f.Equip(("Summon Skeletons",20));
            await Game.HitSummonsTick(f.Player,1);
            Assert.Equal(2,f.Player.Summons.Fighters.Count); Assert.False(removed.CorpseConsumed);
            source=f.Equip(("Absolution",20)).Single();
            var reused=Target(f,905); Mark(f,reused,source,1); Kill(f,905,reused,1); Target(f,905);
            await Game.HitSummonsTick(f.Player,1);
            Assert.Equal(2,f.Player.Summons.Fighters.Count); Assert.False(reused.CorpseConsumed);
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
            var host=Game.ItemsOf(f.Player).Items(PlayerInventories.Weapon1).Single();
            Game.ItemsOf(f.Player).Change(PlayerInventories.Weapon1,host.Id,item=>item with { Links=[2] });
            ushort source=Game.GemSkillEffects(f.Player).Single().Id;
            var rare=Target(f,920,MonsterRarity.Rare);
            Game.PhantasmsOnHitLocked(f.Player,rare,source,1,new HighRandom()); Assert.Empty(f.Player.Summons.HitSummons);
            Game.PhantasmsOnHitLocked(f.Player,rare,source,1,new ZeroRandom());
            await Game.HitSummonsTick(f.Player,1);
            var ghost=Assert.Single(f.Player.Summons.Fighters).Value;
            Assert.Equal("Phantasm",ghost.Definition.Kind); Assert.Equal(4,ghost.Monster.Level);
            Assert.Equal(16,f.Player.Summons.Until[ghost.Id]);
            Assert.Contains(ghost.Abilities,a=>a.Attack.Spell&&a.Attack.Damage.MaxTotal>0);
            ghost.Monster.Walk.Teleport(100,100,1); Game.GolemStepLocked(f.Player,ghost,1);
            Assert.True(ghost.Pending!.Ability.Attack.Spell);
            Game.PhantasmsOnHitLocked(f.Player,rare,source,1,new ZeroRandom(),ghost); Assert.Empty(f.Player.Summons.HitSummons);
            rare.Life=0; rare.Dead=true;
            Game.PhantasmsOnHitLocked(f.Player,rare,source,1,new ZeroRandom());
            Assert.Equal(2,f.Player.Summons.HitSummons.Count);
            f.Player.Summons.HitSummons.Clear(); rare.Life=1000; rare.Dead=false;
            for(int i=0;i<7;i++) Game.PhantasmsOnHitLocked(f.Player,rare,source,1,new ZeroRandom());
            await Game.HitSummonsTick(f.Player,1);
            Assert.Equal(5,f.Player.Summons.Fighters.Count);
            Game.PhantasmsOnHitLocked(f.Player,rare,source,1,new ZeroRandom());
            Game.ItemsOf(f.Player).Change(PlayerInventories.Weapon1,host.Id,item=>item with { Links=[1,1] });
            await Game.HitSummonsTick(f.Player,1);
            Assert.Equal(5,f.Player.Summons.Fighters.Count);
            await Game.SummonsTick(f.Player,1);
            Assert.Empty(f.Player.Summons.Fighters);
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
                var host = Game.ItemsOf(f.Player).Items(PlayerInventories.Weapon1).Single();
                Game.ItemsOf(f.Player).Change(PlayerInventories.Weapon1, host.Id, item => item with { Links = [(byte)item.Sockets.Count] });
                ushort source = Game.GemSkillEffects(f.Player).Single().Id;
                var target = Target(f, 940, MonsterRarity.Rare);
                target.Walk.Teleport(140, 100, 1);
                Game.PhantasmsOnHitLocked(f.Player, target, source, 1, new ZeroRandom());
                await Game.HitSummonsTick(f.Player, 1);
                var ghost = Assert.Single(f.Player.Summons.Fighters).Value;
                f.Player.Clock.Reset();
                ghost.Monster.Walk.Teleport(100, 100, 1);
                var ability = ghost.Abilities.First(a => a.Effect is 9427 or 9428);
                Assert.NotNull(ability.Projectile);
                typeof(Game).GetMethod("GolemAbilityContactLocked", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
                    .Invoke(null, [f.Player, ghost,
                        new GolemState.Action(ability, 940, target, (140, 100), 1, ghost.Monster.ActionGeneration), 1d]);
                var session = Game.ProjectilesFor(f.Player);
                session.AdvanceTo(ProjectileRuntime.Time(1));
                var shots = session.Runtime.Projectiles.ToArray();
                Assert.Equal(supported ? 5 : 1, shots.Length);
                Assert.All(shots, shot => Assert.True(shot.Use.Profile.Pierce < 0 || shot.Use.Profile.Pierce == int.MaxValue));
                if (!supported) baselineSpeed = Assert.Single(shots).Speed;
                else
                {
                    Assert.Equal(5, shots.Select(shot => shot.Direction).Distinct().Count());
                    int speed = ghost.Own[Stat.BaseProjectileSpeedPlusPct];
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
            var vaal=Game.GemSkillEffects(f.Player).Single(g=>Game.SkillsFor(f.Player).EffectId(g.Effect)=="VaalSummonSkeletons");
            Game.RefreshVaalSouls(f.Player);
            bool Admit()=>Game.TryBeginSkillCooldownUse(f.Player,vaal.Id,vaal.Effect,null,false,()=>f.Player.ActionCount++,out _);
            Assert.False(Admit());
            for(int i=0;i<50;i++) Assert.True(f.Player.VaalSouls.Gain());
            Assert.True(Admit());
            Assert.True(await Game.CastGemSkill(f.Player,vaal.Id,(100,100),(120,100)));
            Assert.Equal(0u,Assert.Single(f.Player.VaalSouls.Read()).Current);
            Assert.Equal(18,f.Player.Summons.Fighters.Count);
            foreach(var (kind,count) in new[]{("Melee",14),("Ranged",2),("Spellcaster",1),("General",1)})
                Assert.Equal(count,f.Player.Summons.Fighters.Values.Count(g=>g.Monster.Variety!.Id.Contains(kind,StringComparison.Ordinal)));
            var archer=f.Player.Summons.Fighters.Values.First(g=>g.Monster.Variety!.Id.Contains("Ranged",StringComparison.Ordinal));
            Assert.Contains(archer.Abilities,a=>a.Projectile is not null);
            var enemy=Target(f,930); archer.Monster.Walk.Teleport(500,500,clock.Time); enemy.Walk.Teleport(510,500,clock.Time);
            Game.GolemStepLocked(f.Player,archer,clock.Time);
            Assert.Equal(930u,archer.Target);
            clock.Time=22; await Game.SummonsTick(f.Player,clock.Time); Assert.Empty(f.Player.Summons.Fighters);

            // The native supports apply, but Vaal proxy use budgets are not implemented. Reject before payment.
            foreach (string support in new[] { "Spell Totem Support", "Trap Support", "Blastchain Mine Support" })
            {
                f.Equip(("Vaal Summon Skeletons",1),(support,1));
                var host = Game.ItemsOf(f.Player).Items(PlayerInventories.Weapon1).Single();
                Game.ItemsOf(f.Player).Change(PlayerInventories.Weapon1,host.Id,item=>item with { Links=[2] });
                vaal = Game.GemSkillEffects(f.Player).Single(g=>Game.SkillsFor(f.Player).EffectId(g.Effect)=="VaalSummonSkeletons");
                var supported = Game.SupportedSkill(f.Player,vaal.Id);
                Assert.True(Game.IsTotemSkill(f.Player,vaal.Id) || Game.PlacedKind(supported) is not (false,false));
                Assert.Equal("Vaal minion skills through totems, traps or mines are not implemented",
                    Game.GemSkillInstanceDisabled(f.Player,vaal.Effect,vaal.Id));
                var ordinary = Game.GemSkillEffects(f.Player).Single(g=>Game.SkillsFor(f.Player).EffectId(g.Effect)=="SummonSkeletons");
                Assert.Null(Game.ActorSkillInstanceDisabled(f.Player,ordinary.Effect,ordinary.Id));
                Game.RefreshVaalSouls(f.Player);
                while (Assert.Single(f.Player.VaalSouls.Read()).Current < 50) Assert.True(f.Player.VaalSouls.Gain());
                int action = f.Player.ActionCount, cooldowns = f.Player.SingleUseCooldowns.Count;
                var deadlines = f.Player.SkillReadyAt.ToArray();
                uint mana = f.Player.PlayerMana;
                double prevention = f.Player.VaalPreventionUntil;
                var admission = f.Player.VaalUse;
                Assert.False(Admit());
                Assert.False(await Game.CastGemSkill(f.Player,vaal.Id,(100,100),(120,100)));
                Assert.Equal(50u,Assert.Single(f.Player.VaalSouls.Read()).Current);
                Assert.Equal(action,f.Player.ActionCount);
                Assert.Equal(mana,f.Player.PlayerMana);
                Assert.Equal(prevention,f.Player.VaalPreventionUntil);
                Assert.Same(admission,f.Player.VaalUse);
                Assert.Equal(cooldowns,f.Player.SingleUseCooldowns.Count);
                Assert.Equal(deadlines,f.Player.SkillReadyAt.ToArray());
                Assert.Empty(f.Player.Summons.Fighters);
                Assert.Empty(f.Player.Totems.All);
                Assert.Empty(f.Player.Placed.All);
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
                var vaal = Game.GemSkillEffects(f.Player).Single(g => Game.SkillsFor(f.Player).EffectId(g.Effect) == vaalId);
                ushort source = Game.GemSkillEffects(f.Player).Single(g => Game.SkillsFor(f.Player).EffectId(g.Effect)
                    == (absolution ? "Absolution" : "DominatingBlow")).Id;
                Game.RefreshVaalSouls(f.Player); for (int j=0;j<90;j++) f.Player.VaalSouls.Gain();
                bool Admit() => Game.TryBeginSkillCooldownUse(f.Player, vaal.Id, vaal.Effect, null, false,
                    () => f.Player.ActionCount++, out _);
                Assert.False(Admit());
                var victim = Target(f,940,MonsterRarity.Rare); Mark(f,victim,source,clock.Time); Kill(f,940,victim,clock.Time);
                await Game.HitSummonsTick(f.Player,clock.Time);
                var normal = Assert.Single(f.Player.Summons.Fighters).Value;
                Assert.NotNull(Game.SentinelOwnershipPresentation(f.Player));
                var owned = Assert.Single(f.Player.Summons.PresentedOwnership).Value;
                Assert.Equal((ushort)(0xc000 | vaal.Id), owned.Source);
                var proxy=Game.PlayerSkills(f.Player).Single(s=>s.Id==owned.Source);
                var active=Game.PlayerSkills(f.Player).Single(s=>s.Id==vaal.Id);
                Assert.Equal(absolution?0:1, active.Stats!.Single(s=>s.Key==Stat.SkillBehaviourVariation).Value);
                Assert.Equal(((uint)0x1000, source), proxy.Parent!.Value);
                Assert.Equal(absolution ? 50 : 3, owned.Type);
                Assert.Null(Game.SentinelOwnershipPresentation(f.Player));
                normal.Monster.Life = normal.Monster.MaxLife / 2;
                Assert.True(Admit());
                Assert.True(await Game.CastGemSkill(f.Player,vaal.Id,(100,100),(120,100),normal.Id));
                var ascended = Assert.Single(f.Player.Summons.Fighters).Value;
                Assert.NotEqual(normal.Id,ascended.Id); Assert.Equal(vaalId,ascended.Definition.Kind);
                Assert.Equal(normal.Monster.Level,ascended.Monster.Level);
                Assert.Equal(source,ascended.Source); Assert.Same(normal.InheritedMods,ascended.InheritedMods);
                Assert.NotNull(Game.SentinelOwnershipPresentation(f.Player));
                Assert.Equal(absolution ? 51 : 4,Assert.Single(f.Player.Summons.PresentedOwnership).Value.Type);
                Assert.Equal(300,ascended.Own[Stat.VaalUpgradeMinionDamagePlusPctFinal]);
                Assert.Equal(-70,ascended.Own[Stat.VaalUpgradeMinionDamageTakenPlusPctFinal]);
                clock.Time++; await Game.SummonsTick(f.Player,clock.Time);
                Assert.Equal(300,ascended.Own[Stat.VaalUpgradeMinionDamagePlusPctFinal]);
                if (!absolution)
                    for (uint extra=941;extra<=942;extra++)
                    {
                        var next=Target(f,extra,MonsterRarity.Rare); Mark(f,next,source,clock.Time); Kill(f,extra,next,clock.Time);
                        await Game.HitSummonsTick(f.Player,clock.Time);
                        var body=f.Player.Summons.Fighters.Values.Single(g=>g.Definition.Kind=="DominatingRare");
                        for(int j=0;j<90;j++) f.Player.VaalSouls.Gain();
                        Assert.True(Admit());
                        Assert.True(await Game.CastGemSkill(f.Player,vaal.Id,(100,100),(120,100),body.Id));
                    }
                int count=f.Player.Summons.Fighters.Count;
                Assert.Equal(absolution?1:3,count);
                uint life=ascended.Monster.Life; double oldUntil=f.Player.Summons.Until[ascended.Id]; clock.Time++;
                for(int j=0;j<90;j++) f.Player.VaalSouls.Gain();
                Assert.True(Admit());
                Assert.True(await Game.CastGemSkill(f.Player,vaal.Id,(100,100),(120,100),ascended.Id));
                Assert.Equal(count,f.Player.Summons.Fighters.Count); Assert.Same(ascended,f.Player.Summons.Fighters[ascended.Id]);
                Assert.Equal(life,ascended.Monster.Life); Assert.True(f.Player.Summons.Until[ascended.Id]>oldUntil);
                _ = Game.SentinelOwnershipPresentation(f.Player);
                clock.Time+=35; await Game.SummonsTick(f.Player,clock.Time);
                Assert.Empty(f.Player.Summons.Fighters); Assert.NotNull(Game.SentinelOwnershipPresentation(f.Player));
                Assert.Empty(f.Player.Summons.PresentedOwnership); f.Player.NextAttackAt=0;
                foreach (string support in new[] { "Spell Totem Support", "Trap Support", "Blastchain Mine Support" })
                {
                    f.Equip((absolution ? "Vaal Absolution" : "Vaal Domination",20),(support,1));
                    var host=Game.ItemsOf(f.Player).Items(PlayerInventories.Weapon1).Single();
                    Game.ItemsOf(f.Player).Change(PlayerInventories.Weapon1,host.Id,item=>item with { Links=[2] });
                    var candidate=Game.GemSkillEffects(f.Player).Single(g=>Game.SkillsFor(f.Player).EffectId(g.Effect)==vaalId);
                    var applied=Game.SupportedSkill(f.Player,candidate.Id);
                    if (Game.IsTotemSkill(f.Player,candidate.Id) || Game.PlacedKind(applied) is not (false,false))
                        Assert.Equal("Vaal minion skills through totems, traps or mines are not implemented",
                            Game.GemSkillInstanceDisabled(f.Player,candidate.Effect,candidate.Id));
                }
            }
        }
        finally { Game.AreaUpdateContext.Value=outer; }
    }

    [Fact]
    public async Task Town_suppresses_persistent_summons_and_golems_without_wiping_saves()
    {
        using var f = new MinionUtilityTests.Fixture();
        var ids = f.Equip(("Raise Zombie", 20), ("Summon Stone Golem", 20));
        var zombieDef = Game.SummonData.Find("RaiseZombie")!;
        var savedZombie = new SummonedMinions.Saved("RaiseZombie", ids[0], zombieDef.Variety, 1);
        var savedGolem = new GolemState.Saved("SummonStoneGolem", ids[1], 1);
        CharacterSaves.ChangeModuleData(f.Player.Character.Name!, "Game.SummonedMinions",
            _ => System.Text.Json.JsonSerializer.Serialize(new[] { savedZombie }));
        CharacterSaves.ChangeModuleData(f.Player.Character.Name!, "Game.Golems",
            _ => System.Text.Json.JsonSerializer.Serialize(new[] { savedGolem }));

        // Entering Town (1_1_town, 0x235c): summons and golems do not spawn, and saved data is preserved.
        f.Player.Use(new AreaLayout(0x235c, "1_1_town", 1000, 1000, 100, 100, null, []));
        f.Player.Summons.Restored = false;
        f.Player.Golems.Restored = false;
        await Game.SummonsTick(f.Player, 0);
        await Game.GolemsTick(f.Player, 0);
        Assert.Empty(f.Player.Summons.Fighters);
        Assert.Empty(f.Player.Golems.All);
        Assert.False(f.Player.Summons.Restored);
        Assert.False(f.Player.Golems.Restored);
        Game.SaveSummons(f.Player);
        Game.SaveGolems(f.Player);
        Assert.Single(System.Text.Json.JsonSerializer.Deserialize<SummonedMinions.Saved[]>(
            CharacterSaves.Load(f.Player.Character.Name!)!.ModuleData!["Game.SummonedMinions"])!);
        Assert.Single(System.Text.Json.JsonSerializer.Deserialize<GolemState.Saved[]>(
            CharacterSaves.ModuleData(f.Player.Character.Name!, "Game.Golems")!)!);

        // Entering Hideout / combat area: saved persistent minions and golems restore cleanly.
        var hideout = HideoutCatalog.Default!;
        f.Player.Use(new AreaLayout(hideout.AreaHash16, hideout.AreaId, 1000, 1000, 100, 100, null, []));
        await Game.SummonsTick(f.Player, 1);
        await Game.GolemsTick(f.Player, 1);
        Assert.Single(f.Player.Summons.Fighters);
        Assert.Single(f.Player.Golems.All);
        Assert.True(f.Player.Summons.Restored);
        Assert.True(f.Player.Golems.Restored);
    }

    [Fact]
    public async Task Spectre_receives_spectral_buff_ownership_and_cosmetic_visual()
    {
        using var f = new MinionUtilityTests.Fixture();
        var clock = new Clock { Time = 1 }; var outer = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, clock);
        string charName = f.Player.Character.Name ?? "animate";
        uint gemHash = 0;
        try
        {
            var sources = f.Equip(("Raise Spectre", 20), ("Raise Zombie", 20));
            ushort spectreSource = sources[0], zombieSource = sources[1];
            gemHash = Game.GemSkillEffects(f.Player).Single(g => g.Id == spectreSource).Gem;
            var variety = Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie")!;
            f.Player.Area.Monsters[950] = new("corpse", new(100, 100, 0), 100, 3, 1000) { Hostile = true, Dead = true, Life = 0, Variety = variety };
            var stygian = Assert.IsType<GameServer.Core.Protocol.Instance.InstanceMicrotransactions.Record>(Game.Mtx.ByHash(230254224u)); // MicrotransactionStygianRaiseSpectreSkin
            CharacterSaves.SetSkillCosmetic(charName, gemHash, 0, new CharacterSaves.SavedCosmetic(stygian.Id, 0, stygian.MtxTypeHash));

            ushort buffInstance = f.Player.Area.Buffs.Next;
            uint spectreId = await Game.SummonFighter(f.Player, spectreSource, Game.SummonData.Find("RaiseSpectre")!, (100, 100), 950);
            Assert.NotEqual(0u, spectreId);
            var spectre = f.Player.Summons.Fighters[spectreId];
            Assert.NotEqual(0, spectre.SpectreBuff);
            f.Player.Area.Buffs.Publish();
            var applied = Assert.IsType<Buff>(f.Player.Area.Buffs.Find(spectreId, buffInstance));
            ushort stygianVisual = Assert.IsType<ushort>(applied.Visual);
            Assert.Equal("stygian_spectres", GameServer.Core.ClientTables.Table("BuffVisuals").Row((int)stygianVisual)?.Text("Id"));

            // Clearing the skill cosmetic restores default spectre_buff visual on live spectres.
            CharacterSaves.SetSkillCosmetic(charName, gemHash, 0, null);
            Game.RefreshSpectreCosmetics(f.Player, gemHash);
            f.Player.Area.Buffs.Publish();
            var reverted = Assert.IsType<Buff>(f.Player.Area.Buffs.Find(spectreId, buffInstance));
            Assert.Null(reverted.Visual);

            Assert.NotNull(Game.SentinelOwnershipPresentation(f.Player));
            var entry = Assert.Single(f.Player.Summons.PresentedOwnership).Value;
            Assert.Equal(spectreId, entry.Id);
            Assert.Equal(spectreSource, entry.Source);
            Assert.Equal((ushort)0, entry.Type); // MinionType.SpectreMinion == 0

            uint zombieId = await Game.SummonFighter(f.Player, zombieSource, Game.SummonData.Find("RaiseZombie")!, (102, 100), restoredVariety: variety.Id);
            Assert.NotEqual(0u, zombieId);
            Assert.Equal(0, f.Player.Summons.Fighters[zombieId].SpectreBuff);
            Assert.NotNull(Game.SentinelOwnershipPresentation(f.Player));
            Assert.Equal((ushort)7, f.Player.Summons.PresentedOwnership[zombieId].Type); // MinionType.ZombieMinion == 7

            Game.GolemDieLocked(f.Player, spectre, clock.Time);
            Assert.Equal(0, spectre.SpectreBuff);
            Assert.NotNull(Game.SentinelOwnershipPresentation(f.Player));
            Assert.False(f.Player.Summons.PresentedOwnership.ContainsKey(spectreId));
        }
        finally
        {
            if (gemHash != 0) CharacterSaves.SetSkillCosmetic(charName, gemHash, 0, null);
            Game.AreaUpdateContext.Value = outer;
        }
    }

    [Fact]
    public async Task Persistent_spectre_survives_player_death_and_populates_desecrate_bank()
    {
        using var f = new MinionUtilityTests.Fixture();
        var clock = new Clock { Time = 1 }; var outer = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, clock);
        try
        {
            var sources = f.Equip(("Raise Spectre", 20), ("Desecrate", 20));
            ushort spectreSource = sources[0], desecrateSource = sources[1];
            var revenant = Game.Monsters.Find("Metadata/Monsters/Revenant/RevenantBoss")!;
            f.Player.Area.Monsters[952] = new("corpse", new(100, 100, 0), 100, 3, 1000) { Hostile = true, Dead = true, Life = 0, Variety = revenant };
            uint spectreId = await Game.SummonFighter(f.Player, spectreSource, Game.SummonData.Find("RaiseSpectre")!, (100, 100), 952);
            Assert.NotEqual(0u, spectreId);
            Assert.Contains(revenant.Id, f.Player.Summons.SpectreBank);
            Game.SaveSummons(f.Player);

            // Simulate two consecutive deaths + revives: guard.NextIndex must reset so the 2nd revive also restores.
            for (int cycle = 0; cycle < 2; cycle++)
            {
                f.Player.PlayerLife = 0;
                await Game.SummonsTick(f.Player, 2.0 + cycle * 2.0);
                Assert.True(f.Player.Summons.LostPersistentOnDeath);
                Assert.Empty(f.Player.Summons.Fighters);
                Game.SaveSummons(f.Player);

                f.Player.PlayerLife = 10000;
                await Game.SummonsTick(f.Player, 3.0 + cycle * 2.0);
                Assert.False(f.Player.Summons.LostPersistentOnDeath);
                var restored = Assert.Single(f.Player.Summons.Fighters.Values);
                Assert.Equal(revenant.Id, restored.Monster.Variety!.Id);
                Assert.NotEqual(0, restored.SpectreBuff);
            }

            // Desecrate draws from persisted SpectreBank even after clearing active fighters/memory cache.
            f.Player.Summons.Fighters.Clear();
            f.Player.Area.Monsters.Clear();
            f.Player.Summons.SpectreBank.Clear();
            f.Player.Summons.SpectreBankLoaded = false;
            bool ok = await Game.Desecrate(f.Player, desecrateSource, (100, 100), () => true, triggered: true);
            Assert.True(ok);
            Assert.Contains(f.Player.Area.Monsters.Values, m => m.Dead && m.Variety?.Id == revenant.Id);
        }
        finally { Game.AreaUpdateContext.Value = outer; }
    }

}
