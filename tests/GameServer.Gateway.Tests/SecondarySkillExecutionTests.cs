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
        method.Invoke(null,[f.Conn,placed,1d,records]);
        Assert.Equal(900u,f.Conn.Monsters[901].Life);
        Assert.Equal(1000u,f.Conn.Monsters[900].Life);Assert.Equal(1000u,f.Conn.Monsters[902].Life);
        Assert.Equal(1000u,f.Conn.Monsters[903].Life);
    }
}
