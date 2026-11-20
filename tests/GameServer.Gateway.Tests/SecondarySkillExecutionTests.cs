using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class SecondarySkillExecutionTests
{
    static ushort Equip(MinionUtilityTests.Fixture f, params string[] names)
    {
        f.Equip(names.Select(n => (n,4)).ToArray());
        var host=Game.ItemsOf(f.Conn).Items(PlayerInventories.Weapon1).Single();
        Game.ItemsOf(f.Conn).Change(PlayerInventories.Weapon1,host.Id,i=>i with {Links=[(byte)names.Length]});
        return Game.GemSkillEffects(f.Conn).Single().Id;
    }
    static void Set(ClientState conn,string stat,int value) => conn.PlayerStats[Program.Stats.Key(stat)!.Value]=value;

    [Fact]
    public void Bear_trap_resolves_its_secondary_base_and_generic_added_damage_without_attack_or_spell_modifiers()
    {
        using var f=new MinionUtilityTests.Fixture();
        ushort source=Equip(f,"Bear Trap");
        var baseline=Game.SupportedHitFor(f.Conn,source,false);
        Assert.Equal(35,baseline.MinTotal);Assert.Equal(49,baseline.MaxTotal);
        Set(f.Conn,"spell_damage_+%",1000);Set(f.Conn,"attack_damage_+%",1000);
        Set(f.Conn,"spell_minimum_added_fire_damage",1000);Set(f.Conn,"spell_maximum_added_fire_damage",1000);
        Set(f.Conn,"attack_minimum_added_fire_damage",1000);Set(f.Conn,"attack_maximum_added_fire_damage",1000);
        var unaffected=Game.SupportedHitFor(f.Conn,source,false);
        Assert.Equal(baseline.MinTotal,unaffected.MinTotal);Assert.Equal(baseline.MaxTotal,unaffected.MaxTotal);
        Set(f.Conn,"physical_damage_+%",100);
        var physical=Game.SupportedHitFor(f.Conn,source,false);
        Assert.Equal(70,physical.MinTotal);Assert.Equal(98,physical.MaxTotal);
        Set(f.Conn,"global_minimum_added_cold_damage",10);Set(f.Conn,"global_maximum_added_cold_damage",10);
        var added=Game.SupportedHitFor(f.Conn,source,false);
        Assert.Equal(30,added[DamageType.Cold].Min);Assert.Equal(30,added[DamageType.Cold].Max);
        Set(f.Conn,"global_minimum_added_cold_damage",0);Set(f.Conn,"global_maximum_added_cold_damage",0);
        Set(f.Conn,"physical_damage_+%",0);
        source=Equip(f,"Bear Trap","Added Cold Damage Support");
        var linked=Game.SupportedHitFor(f.Conn,source,false);
        Assert.True(linked[DamageType.Cold].Min>0);
        var gem=Game.SupportedSkill(f.Conn,source);
        int Own(string name)=>gem.Stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
        Assert.Equal(Own("global_minimum_added_cold_damage")*3,linked[DamageType.Cold].Min);
        var input=Game.SupportedHitInputsFor(f.Conn,source)!;
        var panel=Game.SecondaryPanelRows(f.Conn,input.Get,input.Caster,gem,Program.Skills.SecondaryHitDamage(gem.Effect,gem.Level));
        Assert.Equal((int)(linked[DamageType.Cold].Min),panel.Single(r=>r.Stat=="secondary_minimum_cold_damage").Value);
        uint coldRow=Program.Stats.Key("secondary_minimum_cold_damage")!.Value;
        var native=Game.PlayerSkills(f.Conn).Single(s=>s.Id==source);
        Assert.Equal((int)linked[DamageType.Cold].Min,native.Stats!.Single(s=>s.Key==coldRow).Value);
        foreach(string name in new[]{"Fire Trap","Stormblast Mine"})
        { source=Equip(f,name);Assert.True(Game.SupportedHitFor(f.Conn,source,false).MinTotal>0); }
    }

    [Fact]
    public void Bear_trap_hits_one_nearby_enemy_without_splashing_other_enemies_or_allies()
    {
        using var f=new MinionUtilityTests.Fixture();
        ushort source=Equip(f,"Bear Trap");var gem=Game.SupportedSkill(f.Conn,source);
        var placed=new PlacedSkills.Placed(777,source,gem.Effect,(100,100),false,0,10,
            DamagePacket.Of(DamageType.Physical,100,100),gem.Stats,0,0,false,0);
        foreach(var (id,x,hostile) in new[]{(900u,104,true),(901u,102,true),(902u,103,false),(903u,140,true)})
            f.Conn.Monsters[id]=new("victim",new(x,100,0),0,0,1000){Hostile=hostile};
        var method=typeof(Game).GetMethod("PlacedDamageLocked",BindingFlags.Static|BindingFlags.NonPublic)!;
        var records=Activator.CreateInstance(method.GetParameters()[^1].ParameterType,
            BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[f.Conn,null],null);
        method.Invoke(null,["test",f.Stream,f.Ciphers,f.Conn,placed,1d,records]);
        Assert.Equal(900u,f.Conn.Monsters[901].Life);
        Assert.Equal(1000u,f.Conn.Monsters[900].Life);Assert.Equal(1000u,f.Conn.Monsters[902].Life);
        Assert.Equal(1000u,f.Conn.Monsters[903].Life);
        var victim=f.Conn.Monsters[901];
        Assert.True(victim.Walk.MovementBlocked(2.49));
        Assert.False(victim.Walk.MovementBlocked(2.5));
        Assert.Equal(1,Game.MonsterActionFactor(victim,1.1));
        Assert.False(victim.Invulnerable);
        victim.Walk.Order(120,100,1.5);
        Assert.Equal((102,100),victim.Walk.Destination);
        Assert.Equal(.2,Game.BearTrapWalkFactor(victim,2.5),8);
        Assert.Equal(.6,Game.BearTrapWalkFactor(victim,4),8);
        Assert.Equal(1,Game.BearTrapWalkFactor(victim,5.5));
        Assert.Equal(5,Game.BearTrapRootSeconds(1000,1000));
        Assert.Equal(.15,Game.BearTrapRootSeconds(10,1000),8);
        var packet=DamagePacket.Of(DamageType.Fire,100,100);
        victim.TakenFromPlayer=(20,0,0);
        Assert.Equal(120,Game.MonsterTakesMitigated(victim,packet,false,2));
        Assert.Equal(145,Game.MonsterTakesTrapOrMineHit(victim,packet,false,2));
        Assert.Equal(120,Game.MonsterTakesTrapOrMineHit(victim,packet,false,5.5));
        // The trap debuff adds to the target's cold damage taken on trap or mine hits.
        victim.Mods = MonsterModSet.None with { Effects = new(new Dictionary<string, int> { ["cold_damage_taken_+%"] = 10 }) };
        var cold = DamagePacket.Of(DamageType.Cold, 100, 100);
        Assert.Equal(130, Game.MonsterTakesMitigated(victim, cold, false, 2));
        Assert.Equal(155, Game.MonsterTakesTrapOrMineHit(victim, cold, false, 2));
        Assert.Equal(130, Game.MonsterTakesTrapOrMineHit(victim, cold, false, 5.5));
        victim.Ignites.Add(1, 1, 6);
        Game.InflictedLocked(f.Conn, victim, Game.AilmentType.Ignite, 7);
        Game.KeepPerAilmentTypeTakenLocked(f.Conn, victim, 10);
        Assert.Equal(130, Game.MonsterTakesMitigated(victim, packet, false, 2));
        Assert.Equal(155, Game.MonsterTakesTrapOrMineHit(victim, packet, false, 2));
        Assert.Equal(130, Game.MonsterTakesTrapOrMineHit(victim, packet, false, 5.5));
        var buffs=Game.MonsterAilmentChangesLocked(f.Conn,1.1);
        Assert.Contains(buffs,p=>p.Log=="Bear Trap debuff");
        // The client walks by movement_velocity_+%, so the slow is sent there.
        Assert.Contains(buffs,p=>p.Log.EndsWith("movement_velocity_+% = -80 (movement modifiers)"));
        Assert.DoesNotContain(Game.MonsterAilmentChangesLocked(f.Conn,1.2),p=>p.Log=="Bear Trap debuff");
        Assert.Contains(Game.MonsterAilmentChangesLocked(f.Conn,2.5),p=>p.Log=="Bear Trap debuff changes");
        Assert.Contains(Game.MonsterAilmentChangesLocked(f.Conn,5.5),p=>p.Log=="Bear Trap debuff changes");
        Assert.Equal(1,victim.Ailments.WalkFactor);
        var immune=new SpawnedMonster("movement floor",new(100,100,20),0,0,1000)
            {EncounterStats=new Dictionary<string,int>{{"movement_speed_cannot_be_reduced_below_base",1}}};
        Game.BearTrapOnHitLocked(immune,100,1,_=>0,Own);
        Assert.False(immune.Walk.MovementBlocked(1.1));
        Assert.Equal(1,Game.BearTrapWalkFactor(immune,1.1));
        int Own(string name)=>gem.Stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
    }
}
