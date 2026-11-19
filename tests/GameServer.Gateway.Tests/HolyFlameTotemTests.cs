using GameServer.Core.Protocol.Instance;
using GameServer.Core.Projectiles;
using System.Reflection;

namespace GameServer.Gateway.Tests;

public sealed class HolyFlameTotemTests
{
    [Fact]
    public async Task Native_holy_flame_totem_places_and_releases_visible_piercing_projectiles_from_the_proxy()
    {
        using var f=new MinionUtilityTests.Fixture();
        ushort source=f.Equip(("Holy Flame Totem",20)).Single();
        var gem=Game.GemSkillEffects(f.Conn).Single();
        Assert.True(Game.ProjectilePathAvailable(f.Conn,source,gem.Effect));
        Assert.NotNull(Game.TotemVariety(f.Conn,gem.Effect));
        Assert.False(Game.SpellTotemEffect(f.Conn,gem.Effect));
        f.Conn.Walk.Teleport(20,20,0);
        await Game.CreateTotem(f.Conn,source,gem.Effect,gem.Level,
            Game.TotemVariety(f.Conn,gem.Effect)!,(50,50));
        var totem=Assert.Single(f.Conn.Totems.All);
        Assert.True(Game.SpellTotemCurrent(f.Conn,totem));
        var session=Game.ProjectilesFor(f.Conn);
        try
        {
            var use=Game.CaptureGemProjectile(f.Conn,session,source,gem,(70,50),new(0),totem:totem);
            Assert.Equal(totem.Id,use.Emitter.Id);Assert.Equal(totem.At.X,use.Origin.X);Assert.Equal(totem.At.Y,use.Origin.Y);
            Assert.Equal(6,use.Profile.Count);Assert.Equal(-1,use.Profile.Pierce);
            Assert.Equal("Metadata/Projectiles/TotemHolyFireSpray",use.Profile.Id);
            Assert.True(Assert.IsType<ProjectileOffense>(use.Payload).Hit.MaxTotal>0);
            // The integrated Dying Sun path reaches the native totem, once, and disappears when the flask ends.
            var sun = UtilityFlasks.DuringEffectSources(new Dictionary<string, int>
                { ["local_flask_number_of_additional_projectiles_during_flask_effect"] = 2 }, 1.5);
            f.Conn.UtilityFlaskEffects.Start(new(123, 0, "Dying Sun", new(0, 0, "test", [], 10, 1, 10),
                1, null, sun, 0, 100));
            var flaskUse = Game.CaptureGemProjectile(f.Conn,session,source,gem,(70,50),new(0),totem:totem);
            Assert.Equal(9,flaskUse.Profile.Count);
            Assert.Equal(-1,flaskUse.Profile.Pierce);
            Assert.Equal(totem.Id,flaskUse.Emitter.Id);
            f.Conn.UtilityFlaskEffects.End(100,all:true,out _);
            Assert.Equal(6,Game.CaptureGemProjectile(f.Conn,session,source,gem,(70,50),new(0),totem:totem).Profile.Count);
            var visible=(ProjectileUse)typeof(Game).GetMethod("ClientDrawnVolley",BindingFlags.NonPublic|BindingFlags.Static)!
                .Invoke(null,[f.Conn,session,source,gem,use,(ushort)1,0u])!;
            var shot=Assert.IsType<ProjectileOffense>(visible.Payload);
            Assert.False(shot.ClientPredicted);Assert.NotNull(shot.Presentation);
            f.Conn.Monsters[900]=new("target",new(70,50,0),0,0,100000){Hostile=true};
            var bytes=new List<byte>();uint mana=f.Conn.PlayerMana;
            Game.TickSpellTotemsLocked(f.Conn,bytes);
            session.AdvanceTo(ProjectileRuntime.Time(totem.NextCast+1));
            Assert.True(f.Conn.Monsters[900].Life<100000);Assert.Equal(mana,f.Conn.PlayerMana);
        }
        finally {session.Invalidate();await session.OutputCompleted;}
    }

    [Fact]
    public void Native_admission_stays_bounded_and_retired_holy_flame_sources_stop_casting()
    {
        using var f=new MinionUtilityTests.Fixture();
        ushort source=f.Equip(("Holy Flame Totem",1)).Single();
        var gem=Game.SupportedSkill(f.Conn,source);
        var totem=new TotemState.Totem(777,source,(100,100),100,100,null)
            {Effect=gem.Effect,SourceGem=Game.GemSkillEffects(f.Conn).Single().Gem,SourceHost=gem.Host,SourceItem=gem.SourceGem};
        f.Conn.Totems.Place(totem,1,0);
        Assert.True(Game.SpellTotemCurrent(f.Conn,totem));
        var definition=Game.ResolveProjectileDefinition(gem.Effect)!;
        int Get(string name)=>gem.Stats.GetValueOrDefault(Program.Stats.Key(name)??0);
        Assert.Equal(3,definition.Parameters.Resolve(Get,600,false).Count);
        source=f.Equip(("Shockwave Totem",1)).Single();
        Assert.False(Game.NativeProjectileTotem(f.Conn,Game.SupportedSkill(f.Conn,source).Effect));
        Assert.False(Game.SpellTotemCurrent(f.Conn,totem));
        source=f.Equip(("Searing Bond",1)).Single();
        Assert.False(Game.NativeProjectileTotem(f.Conn,Game.SupportedSkill(f.Conn,source).Effect));
    }
}
