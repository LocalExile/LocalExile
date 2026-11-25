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
        var gem=Game.GemSkillEffects(f.Player).Single();
        Assert.True(Game.ProjectilePathAvailable(f.Player,source,gem.Effect));
        Assert.NotNull(Game.TotemVariety(f.Player,gem.Effect));
        Assert.False(Game.SpellTotemEffect(f.Player,gem.Effect));
        f.Player.Walk.Teleport(20,20,0);
        await Game.CreateTotem(f.Player,source,gem.Effect,gem.Level,
            Game.TotemVariety(f.Player,gem.Effect)!,(50,50));
        var totem=Assert.Single(f.Player.Totems.All);
        Assert.True(Game.SpellTotemCurrent(f.Player,totem));
        var session=Game.ProjectilesFor(f.Player);
        try
        {
            var use=Game.CaptureGemProjectile(f.Player,session,source,gem,(70,50),new(0),totem:totem);
            Assert.Equal(totem.Id,use.Emitter.Id);Assert.Equal(totem.At.X,use.Origin.X);Assert.Equal(totem.At.Y,use.Origin.Y);
            Assert.Equal(6,use.Profile.Count);Assert.Equal(-1,use.Profile.Pierce);
            Assert.Equal("Metadata/Projectiles/TotemHolyFireSpray",use.Profile.Id);
            Assert.True(Assert.IsType<ProjectileOffense>(use.Payload).Hit.MaxTotal>0);
            // The integrated Dying Sun path reaches the native totem, once, and disappears when the flask ends.
            var sun = UtilityFlasks.DuringEffectSources(new Dictionary<uint, int>
                { [Stat.LocalFlaskNumberOfAdditionalProjectilesDuringFlaskEffect] = 2 }, 1.5);
            f.Player.UtilityFlaskEffects.Start(new(123, 0, "Dying Sun", new(0, 0, "test", [], 10, 1, 10),
                1, null, sun, 0, 100));
            var flaskUse = Game.CaptureGemProjectile(f.Player,session,source,gem,(70,50),new(0),totem:totem);
            Assert.Equal(9,flaskUse.Profile.Count);
            Assert.Equal(-1,flaskUse.Profile.Pierce);
            Assert.Equal(totem.Id,flaskUse.Emitter.Id);
            f.Player.UtilityFlaskEffects.End(100,all:true,out _);
            Assert.Equal(6,Game.CaptureGemProjectile(f.Player,session,source,gem,(70,50),new(0),totem:totem).Profile.Count);
            var visible=(ProjectileUse)typeof(Game).GetMethod("ClientDrawnVolley",BindingFlags.NonPublic|BindingFlags.Static)!
                .Invoke(null,[f.Player,session,source,gem,use,(ushort)1,0u])!;
            var shot=Assert.IsType<ProjectileOffense>(visible.Payload);
            Assert.False(shot.ClientPredicted);Assert.NotNull(shot.Presentation);
            f.Player.Area.Monsters[900]=new("target",new(70,50,0),0,0,100000){Hostile=true};
            var bytes=new List<byte>();uint mana=f.Player.PlayerMana;
            Game.TickSpellTotemsLocked(f.Player,bytes);
            session.AdvanceTo(ProjectileRuntime.Time(totem.NextCast+1));
            Assert.True(f.Player.Area.Monsters[900].Life<100000);Assert.Equal(mana,f.Player.PlayerMana);

            var ground = f.Player.FlaskGrounds;
            Assert.False(ground.NeedsMonsterUpdates);
            var edge = (totem.At.X + 16, totem.At.Y);
            Assert.True(ground.OnConsecrated(true, edge, 0));
            ground.SetConsecrated(true, () => 100);
            Assert.Contains((Stat.ImmuneToCurses, 1), ground.PlayerStats());
            totem.DiedAt = 1;
            Assert.Single(ground.Expire(1));
            Assert.False(ground.OnConsecrated(true, edge, 1));
            Assert.False(ground.CurseImmune);
        }
        finally {session.Invalidate();await session.OutputCompleted;}
    }

    [Fact]
    public void Area_exit_retires_totem_objects_pending_placements_and_their_ground()
    {
        using var fixture = new MinionUtilityTests.Fixture();
        var player = fixture.Player;
        var area = player.Area;
        var totem = new TotemState.Totem(100, 259, (50, 50), 24, double.PositiveInfinity, null);
        player.Totems.Place(totem, 1, 0);
        player.SetObjectBlocker(totem.Id, 50, 50, 2, true);
        player.Totems.PendingPlacements.Add(101);
        player.FlaskGrounds.Add(new(FlaskGrounds.Kind.ConsecratedGround, 50, 50, 16, double.PositiveInfinity, 102)
            { SourceAlive = totem.Alive, GrantsCurseImmunity = true });
        player.FlaskGrounds.Add(new(FlaskGrounds.Kind.ChilledGround, 99, 99, 16, double.PositiveInfinity, 103));
        var previous = GameArea.Own(area);
        try
        {
            foreach (uint id in new uint[] { 100, 101, 102, 103 })
                area.Delta.Add(id, InstanceEntities.CreateObject(id, 0x1234, [0, 0]), kind: AreaDelta.Kind.Create);
            area.Delta.Take();

            Game.EndAreaTotems(player);
            Assert.Empty(player.Totems.All);
            Assert.Empty(player.Totems.PendingPlacements);
            Assert.Equal(103u, Assert.Single(player.FlaskGrounds.Grounds).Object);
            Assert.False(area.Objects.Has(100));
            Assert.False(area.Objects.Has(101));
            Assert.False(area.Objects.Has(102));
            Assert.True(area.Objects.Has(103));

            Game.EndAreaTotems(player);
            var records = area.Delta.Take();
            Assert.Equal(new uint[] { 100, 101, 102 }, records
                .Where(record => record.Kind == AreaDelta.Kind.Remove).Select(record => record.Entity).Order());
            var roster = InstanceEntities.PlayerMinionsChange(player.Id, [new(100, 0, 5)], false);
            Assert.Single(records, record => ReferenceEquals(record.Only, player)
                && record.Bytes.AsSpan().SequenceEqual(roster));
        }
        finally { GameArea.Own(previous); }
    }

    [Fact]
    public void Native_admission_stays_bounded_and_retired_holy_flame_sources_stop_casting()
    {
        using var f=new MinionUtilityTests.Fixture();
        ushort source=f.Equip(("Holy Flame Totem",1)).Single();
        var gem=Game.SupportedSkill(f.Player,source);
        var totem=new TotemState.Totem(777,source,(100,100),100,100,null)
            {Effect=gem.Effect,SourceGem=Game.GemSkillEffects(f.Player).Single().Gem,SourceHost=gem.Host,SourceItem=gem.SourceGem};
        f.Player.Totems.Place(totem,1,0);
        Assert.True(Game.SpellTotemCurrent(f.Player,totem));
        var definition=Game.ResolveProjectileDefinition(gem.Effect)!;
        int Get(uint name)=>gem.Stats.GetValueOrDefault(name);
        Assert.Equal(3,definition.Parameters.Resolve(Get,600,false).Count);
        source=f.Equip(("Shockwave Totem",1)).Single();
        Assert.False(Game.NativeProjectileTotem(f.Player,Game.SupportedSkill(f.Player,source).Effect));
        Assert.False(Game.SpellTotemCurrent(f.Player,totem));
        source=f.Equip(("Searing Bond",1)).Single();
        Assert.False(Game.NativeProjectileTotem(f.Player,Game.SupportedSkill(f.Player,source).Effect));
    }
}
