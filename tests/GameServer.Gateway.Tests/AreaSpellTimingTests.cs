using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

// When an area spell's hits land: at the class's spell_aoe clip contact (the client plays it for the IceNova action's
// Animation.dat row 33, SpellAreaOfEffectCold), then as Ice Nova's ring reaches each victim.
public sealed class AreaSpellTimingTests
{
    const string Json = """
            { "Metadata/Characters/Int/Int.ao": { "animations": {
                "spell_projectile_wand": [1066, 599], "spell_aoe_wand": [766, 266] },
                "indices": { "spell_projectile_wand": 25, "spell_aoe_wand": 33 },
                "cancels": { "spell_projectile_wand": 754, "spell_aoe_wand": null } },
              "Metadata/Characters/StrInt/StrInt.ao": { "animations": {
                "spell_projectile_unarmed": [900, 500], "spellaoe_unarmed": [833, 366] } },
              "Metadata/Characters/Dex/Dex.ao": { "animations": { "spell_projectile_unarmed": [1000, 400] } } }
        """;

    [Fact]
    public void An_area_spell_lands_at_its_aoe_clip_contact()
    {
        var contacts = AnimationContactsTables.From(Json);
        Assert.Equal(266.0 / 766, contacts.SpellAreaRelease("Metadata/Characters/Int/Int.ao"), 6);
        Assert.Equal(366.0 / 833, contacts.SpellAreaRelease("Metadata/Characters/StrInt/StrInt.ao"), 6);
        // No area clip: the projectile release; no such rig: the default.
        Assert.Equal(0.4, contacts.SpellAreaRelease("Metadata/Characters/Dex/Dex.ao"), 6);
        Assert.Equal(AnimationContacts.DefaultSpellRelease, contacts.SpellAreaRelease("Metadata/None.ao"));
        var wand = new WeaponProfile { Wand = true };
        var window = contacts.SpellMoveWindow("Metadata/Characters/Int/Int.ao", wand,
            CastAnimationKind.Projectile, 0, .65, false)!.Value;
        Assert.True(window.AllowsMove(.1));
        Assert.False(window.AllowsMove(.2));
        Assert.False(window.AllowsMove(.45));
        Assert.True(window.AllowsMove(.46));
        Assert.False(window.AllowsMove(.1, earlyCancellation: false));
        Assert.False(contacts.SpellMoveWindow("Metadata/Characters/Int/Int.ao", wand,
            CastAnimationKind.Projectile, 0, .65, true)!.Value.AllowsMove(.1));
        Assert.Equal(.65 * 266 / 766, contacts.SpellMoveWindow("Metadata/Characters/Int/Int.ao", wand,
            CastAnimationKind.Area, 0, .65, false)!.Value.LateFrom, 6);
        Assert.Null(contacts.SpellMoveWindow("Metadata/None.ao", wand, CastAnimationKind.Projectile, 0, .65, false));
    }

    [Fact]
    public async Task The_ring_reaches_each_victim_in_proportion_to_its_distance()
    {
        Assert.Equal(0, IceNovaRing.Reach(0, 30, 0));
        Assert.Equal(0.1, IceNovaRing.Reach(15, 30, 0), 6);
        Assert.Equal(0.2, IceNovaRing.Reach(30, 30, 0), 6);
        Assert.Equal(0.25, IceNovaRing.Reach(30, 30, 3), 6);
        Assert.Equal(0.2, IceNovaRing.Reach(31, 30, 0), 6);
        Assert.Equal(0.1, Game.FrostboltNovaDelay(1, 0, 0), 6);
        Assert.Equal(0.5, Game.FrostboltNovaDelay(0, 1, 0), 6);
        Assert.Equal(0.65, Game.FrostboltNovaDelay(1, 1, 3), 6);

        using var battle = new ArrowRainAndMirageTests.Battle("Ice Nova of Frostbolts", "Frostbolt", "Fireball");
        var conn = battle.Conn;
        conn.Monsters.Clear();
        var nova = Game.GemSkillEffects(conn).Single(s => s.Name == "Ice Nova of Frostbolts");
        var aim = (X: 300, Y: 300);
        Assert.Equal((100, 100), Game.SpellCentre(conn, nova.Effect, (100, 100), aim));

        ProjectileEntity Release(string name, (int X, int Y) at, uint owner, long? lifetime = null)
        {
            var gem = Game.GemSkillEffects(conn).Single(s => s.Name == name);
            var use = Game.CaptureGemProjectile(conn, battle.Session, gem.Id, gem, (at.X + 60, at.Y), new(0), from: at);
            if (lifetime is long end) use = use with { Profile = use.Profile with { Lifetime = end } };
            var actor = new ProjectileActorKey(owner, conn.ProjectileEpoch);
            long id = battle.Session.Runtime.Release(use with { Owner = actor, Emitter = actor });
            return battle.Session.Runtime.Projectiles.Single(p => p.UseId == id);
        }
        Release("Fireball", aim, Program.PlayerId);
        Release("Frostbolt", aim, 200);
        Release("Frostbolt", (361, 380), Program.PlayerId);
        var edge = Release("Frostbolt", (360, 380), Program.PlayerId);
        Assert.Same(edge, Assert.Single(Game.FrostboltNovaTargets(conn, aim, 2)));
        var second = Release("Frostbolt", (320, 300), Program.PlayerId);
        var first = Release("Frostbolt", (310, 300), Program.PlayerId);
        Assert.Equal([first, second], Game.FrostboltNovaTargets(conn, aim, 2));

        // Advance across the second ring's start AND its Frostbolt's expiry in one update. The started ring keeps
        // the cell the moving projectile occupied at +100 ms. Its witness stands clear of both projectile paths.
        battle.AdvanceBy(12);
        conn.Clock.Start();
        double cast = Game.CastMs(conn, nova.Effect, nova.Id)!.Value / 1000d;
        double contact = cast * Game.Animations.SpellAreaRelease(Game.SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject);
        Release("Frostbolt", (280, 300), Program.PlayerId);
        var brief = Release("Frostbolt", (342, 300), Program.PlayerId, ProjectileRuntime.Time(contact + .12));
        int ringX = (int)Math.Round(342 + brief.Speed * (contact + .1));
        var witness = new SpawnedMonster("second ring", new(ringX, 315, 0), 0, 0, 10000) { Hostile = true };
        conn.Monsters[900] = witness;
        var late = new SpawnedMonster("later arrival", new(ringX, 315, 0), 0, 0, 10000) { Hostile = true };
        battle.Session.Runtime.Schedule(battle.Session.Runtime.Now + ProjectileRuntime.Time(contact + .11),
            () => conn.Monsters[901] = late);
        var castJob = Game.LaunchProjectile(battle.Session.remote, battle.Stream, battle.Ciphers, conn, nova.Id,
            (100, 100), aim, conn.ActionInterruptionGeneration);
        battle.AdvanceBy(contact + .4);
        await castJob;
        await battle.FlushOutput();
        Assert.DoesNotContain(brief, battle.Session.Runtime.Projectiles);
        Assert.True(witness.Life < witness.MaxLife);
        Assert.Equal(late.MaxLife, late.Life);
    }
}
