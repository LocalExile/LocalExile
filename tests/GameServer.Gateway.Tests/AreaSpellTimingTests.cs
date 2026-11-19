using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

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
    public async Task An_area_spell_lands_at_its_aoe_clip_contact()
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
        await VortexDetonatesSelectedBolts();
        await ShockNovaSamplesBothContacts();
        await ProcessionRepeatsAlongItsOriginalAim();
        WalkFacingAtAWaypoint();
    }

    static async Task ProcessionRepeatsAlongItsOriginalAim()
    {
        using var battle = new ArrowRainAndMirageTests.Battle("Shock Nova of Procession", "Assassin's Mark", "Fireball");
        var conn = battle.Conn;
        var session = battle.Session;
        conn.Use(conn.Layout with
        {
            Sight = (x, _) => x == 330 ? 0 : 5,
            Clearance = (x, _, size) => size == 0 && x != 329
        });
        conn.Walk.Teleport(100, 100, 0);
        conn.Monsters.Clear();
        battle.AdvanceBy(12);
        conn.Clock.Start();
        conn.PlayerStats[Program.Stats.Key("global_cannot_crit")!.Value] = 1;
        conn.PlayerStats[Program.Stats.Key("nova_spells_cast_at_marked_target")!.Value] = 1;
        var nova = Game.GemSkillEffects(conn).Single(s => s.Name == "Shock Nova of Procession");
        var mark = Game.GemSkillEffects(conn).Single(s => s.Name == "Assassin's Mark");
        var wire = Game.PlayerSkills(conn).Single(s => s.Id == nova.Id).Stats!;
        foreach (var (name, value) in new[] { ("skill_behaviour_variation", 1), ("spell_repeat_count", 2),
            ("total_skill_area_of_effect_radius_+%_including_final", -17), ("virtual_nova_spells_cast_at_marked_target", 1) })
            Assert.Contains((Program.Stats.Key(name)!.Value, value), wire);

        SpawnedMonster At(int x, int y = 100) =>
            conn.Monsters[(uint)(1000 * y + x)] = new("nova witness", new(x, y, 0), 0, 0, 100000) { Hostile = true };
        var marked = At(305);
        lock (conn.Monsters)
            Assert.True(Game.ApplyCurseLocked(conn, marked, mark.Id, Game.ProjectileWorldTime(conn), aura: false));
        var firstRing = At(421);
        var firstDisc = At(529);
        var secondRing = At(341);
        var secondDisc = At(347);
        var thirdDisc = At(357);
        var pastWall = At(358);
        var cursor = At(100, 240);
        double cast = Game.CastMs(conn, nova.Effect, nova.Id)!.Value / 1000d;
        long start = session.Runtime.Now;
        long contact = start + ProjectileRuntime.Time(cast * Game.Animations.SpellAreaRelease(
            Game.SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject));
        long interval = ProjectileRuntime.Time(cast), gap = ProjectileRuntime.Time(.1);
        var use = Game.LaunchProjectile(session.remote, battle.Stream, battle.Ciphers, conn, nova.Id,
            (100, 100), (100, 240), conn.ActionInterruptionGeneration);
        // The moving mark controls both first-cast hits. It supplied the repeat path's start at (305,100).
        marked.Walk.Teleport(400, 100, Game.ProjectileWorldTime(conn));
        session.AdvanceTo(contact - 1);
        Assert.Equal(firstRing.MaxLife, firstRing.Life);
        session.AdvanceTo(contact);
        Assert.True(firstRing.Life < firstRing.MaxLife);
        marked.Walk.Teleport(500, 100, Game.ProjectileWorldTime(conn));
        session.AdvanceTo(contact + gap);
        Assert.True(firstDisc.Life < firstDisc.MaxLife);
        Assert.Equal(secondRing.MaxLife, secondRing.Life);
        // Single-precision normalization of the 205-cell vector gives a step of 13. The next ray stops at 328.
        session.AdvanceTo(contact + interval);
        Assert.True(secondRing.Life < secondRing.MaxLife);
        Assert.Equal(secondDisc.MaxLife, secondDisc.Life);
        session.AdvanceTo(contact + interval + gap);
        Assert.True(secondDisc.Life < secondDisc.MaxLife);
        Assert.Equal(thirdDisc.MaxLife, thirdDisc.Life);
        session.AdvanceTo(contact + 2 * interval + gap);
        await use;
        Assert.True(thirdDisc.Life < thirdDisc.MaxLife);
        Assert.Equal(pastWall.MaxLife, pastWall.Life);
        Assert.Equal(cursor.MaxLife, cursor.Life);

        // A fresh actor faces north. Its second repeat reaches beyond the first disc.
        conn.PlayerStats.Remove(Program.Stats.Key("nova_spells_cast_at_marked_target")!.Value);
        var self = At(100);
        var north = At(100, 46);
        long windup = contact - start;
        contact = session.Runtime.Now + windup;
        use = Cast((100, 100));
        session.AdvanceTo(contact);
        Assert.Equal(self.MaxLife, self.Life);
        session.AdvanceTo(contact + gap);
        Assert.True(self.Life < self.MaxLife);
        Assert.Equal(north.MaxLife, north.Life);
        session.AdvanceTo(contact + 2 * interval + gap);
        await use;
        Assert.True(north.Life < north.MaxLife);

        // Identical ground requests retain different directions after an east or west cast.
        var east = At(152);
        var west = At(47, 98);
        foreach (var (aim, along, behind) in new[] { ((240, 100), east, west), ((20, 100), west, east) })
        {
            await Finish(aim);
            uint alongLife = along.Life, behindLife = behind.Life;
            await Finish((100, 100));
            Assert.True(along.Life < alongLife);
            Assert.Equal(behindLife, behind.Life);
        }

        // A paid Fireball turns to its aim. A refused Fireball, a stale action and a no-turn cast keep it.
        var fire = Game.GemSkillEffects(conn).Single(s => s.Name == "Fireball");
        conn.PlayerMana = 200;
        Assert.True(await Game.CastGemSkill(session.remote, battle.Stream, battle.Ciphers, conn, fire.Id,
            (100, 100), (240, 100), actionToken: conn.ActionCount));
        conn.PlayerMana = 0;
        Assert.False(await Game.CastGemSkill(session.remote, battle.Stream, battle.Ciphers, conn, fire.Id,
            (100, 100), (20, 100), actionToken: conn.ActionCount));
        await Game.LaunchProjectile(session.remote, battle.Stream, battle.Ciphers, conn, nova.Id,
            (100, 100), (20, 100), conn.ActionInterruptionGeneration, actionToken: conn.ActionCount - 1);
        conn.PlayerStats[Program.Stats.Key("action_do_not_face_target")!.Value] = 1;
        await Finish((20, 100));
        Assert.Equal(MathF.PI / 2, conn.Walk.Rotation(Game.ProjectileWorldTime(conn)));
        conn.PlayerStats.Remove(Program.Stats.Key("action_do_not_face_target")!.Value);
        conn.PlayerStats[Program.Stats.Key("action_should_face_away_from_target")!.Value] = 1;
        await Finish((240, 100));
        Assert.Equal(3 * MathF.PI / 2, conn.Walk.Rotation(Game.ProjectileWorldTime(conn)));

        Task Cast((int X, int Y) aim) => Game.LaunchProjectile(session.remote, battle.Stream, battle.Ciphers,
            conn, nova.Id, (100, 100), aim, conn.ActionInterruptionGeneration);

        async Task Finish((int X, int Y) aim)
        {
            var pending = Cast(aim);
            session.AdvanceTo(session.Runtime.Now + windup + 2 * interval + gap);
            await pending;
        }
    }

    static void WalkFacingAtAWaypoint()
    {
        var npc = new ClientPathfinder.Blocker(20, 0, 4);
        var walk = new PlayerWalk(0, 0, 37, (x, y) => x >= 0 && y >= 0 && x < 60 && y < 60, 60, 60, () => [npc]);
        walk.Order(40, 0, 0);
        var steps = walk.Steps(0, walk.ArrivesAt);
        var bends = steps.Where(s => walk.EndsPiece(s.At)).ToArray();
        Assert.True(bends.Length >= 2);
        var bend = bends[0];
        float incoming = Heading((0, 0), bend.Cell), outgoing = Heading(bend.Cell, bends[1].Cell);
        Assert.NotEqual(incoming, outgoing);
        var route = walk.Save();
        walk.Pause(0, .5);
        Assert.Equal(incoming, walk.Rotation(bend.At), 5);
        Assert.Equal(outgoing, walk.Rotation(bend.At + .5 + .000001), 5);
        walk.Restore(route);
        walk.StopAfter(bend.At);
        Assert.Equal(incoming, walk.Rotation(walk.ArrivesAt + 1), 5);
        walk.Restore(route);
        var next = steps.First(s => s.At > bend.At);
        walk.StopAt(next.At); // The next piece was set up even when its first step was blocked.
        Assert.Equal(outgoing, walk.Rotation(next.At), 5);
        walk.Teleport(10, 10, next.At);
        Assert.Equal(outgoing, walk.Rotation(next.At + 1), 5);

        static float Heading((int X, int Y) a, (int X, int Y) b)
        {
            double angle = Math.Atan2(b.X - a.X, a.Y - b.Y);
            return (float)(angle < 0 ? angle + Math.Tau : angle);
        }
    }

    static async Task ShockNovaSamplesBothContacts()
    {
        using var battle = new ArrowRainAndMirageTests.Battle("Shock Nova", "Assassin's Mark");
        var conn = battle.Conn;
        var session = battle.Session;
        conn.Monsters.Clear();
        battle.AdvanceBy(12);
        conn.Clock.Start();
        conn.PlayerStats[Program.Stats.Key("global_cannot_crit")!.Value] = 1;
        conn.PlayerStats[Program.Stats.Key("nova_spells_cast_at_marked_target")!.Value] = 1;
        var nova = Game.GemSkillEffects(conn).Single(s => s.Name == "Shock Nova");
        var mark = Game.GemSkillEffects(conn).Single(s => s.Name == "Assassin's Mark");
        long release = ProjectileRuntime.Time(Game.CastMs(conn, nova.Effect, nova.Id)!.Value / 1000d
            * Game.Animations.SpellAreaRelease(Game.SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject));
        long gap = ProjectileRuntime.QuantaPerSecond / 10;
        SpawnedMonster At(int x) =>
            conn.Monsters[(uint)x] = new("nova witness", new(x, 100, 0), 0, 0, 1100) { Hostile = true };
        Task Cast() => Game.LaunchProjectile(session.remote, battle.Stream, battle.Ciphers, conn, nova.Id,
            (100, 100), (500, 100), conn.ActionInterruptionGeneration);

        var marked = At(300);
        lock (conn.Monsters)
            Assert.True(Game.ApplyCurseLocked(conn, marked, mark.Id, Game.ProjectileWorldTime(conn), aura: false));
        var hole = At(311);
        var inner = At(312);
        var outer = At(328);
        var beyondRing = At(329);
        var discEdge = At(335);
        var beyondDisc = At(336);
        long contact = session.Runtime.Now + release;
        var cast = Cast();
        session.AdvanceTo(contact - 1);
        Assert.All(conn.Monsters.Values, m => Assert.Equal(m.MaxLife, m.Life));
        session.AdvanceTo(contact);
        Assert.True(inner.Life < inner.MaxLife && outer.Life < outer.MaxLife);
        Assert.True(inner.Ailments.Magnitude(MonsterElementalAilments.Kind.Shock, Game.ProjectileWorldTime(conn)) > 0);
        Assert.All(new[] { marked, hole, beyondRing, discEdge, beyondDisc }, m => Assert.Equal(m.MaxLife, m.Life));
        uint first = inner.Life;
        session.AdvanceTo(contact + gap - 1);
        Assert.Equal(first, inner.Life);
        Assert.Equal(hole.MaxLife, hole.Life);
        session.AdvanceTo(contact + gap);
        await cast;
        Assert.True(inner.Life < first);
        Assert.All(new[] { marked, hole, beyondRing, discEdge }, m => Assert.True(m.Life < m.MaxLife));
        Assert.Equal(0, hole.Ailments.Magnitude(MonsterElementalAilments.Kind.Shock, Game.ProjectileWorldTime(conn)));
        Assert.Equal(beyondDisc.MaxLife, beyondDisc.Life);

        // The disc samples the marked actor again. A target can leave its area or arrive after contact.
        contact = session.Runtime.Now + release;
        cast = Cast();
        session.AdvanceTo(contact);
        first = inner.Life;
        marked.Walk.Teleport(500, 100, Game.ProjectileWorldTime(conn));
        var arrival = At(535);
        session.AdvanceTo(contact + gap);
        await cast;
        Assert.Equal(first, inner.Life);
        Assert.True(arrival.Life < arrival.MaxLife);

        // Cursor targeting wins over the mark. Quadruple area doubles each radius before adding the target's size.
        marked.Walk.Teleport(300, 100, Game.ProjectileWorldTime(conn));
        conn.PlayerStats[Program.Stats.Key("nova_spells_cast_at_target_location")!.Value] = 1;
        conn.PlayerStats[Program.Stats.Key(SkillArea.BaseIncreased)!.Value] = 300;
        uint scale = Program.Stats.Key("total_skill_area_of_effect_radius_+%_including_final")!.Value;
        Assert.Contains((scale, 100), Game.PlayerSkills(conn).Single(s => s.Id == nova.Id).Stats!);
        var enlargedRing = At(554);
        var enlargedDisc = At(568);
        var outside = At(569);
        const uint shared = 0x08000055;
        conn.Monsters[shared] = new("shared", new(510, 100, 0), 0, 0, 100000) { HitAnimations = [new(0, 1)] };
        conn.Instance.World.MonsterOrigins[shared] = new(123, new(510, 100, 23, []), 0, 1, MonsterRarity.Normal);
        conn.PlayerStats[Program.Stats.Key("always_stun_enemies_that_are_on_full_life")!.Value] = 1;
        var observer = SpellTotemExecutionTests.Connection("Nova observer", conn.Instance);
        var observed = new List<byte[]>();
        observer.AwakeMonsters = [shared];
        observer.Outbox = (bytes, _) => { observed.Add(bytes); return Task.CompletedTask; };
        conn.Instance.PublishPlayer(new(observer.PeerId, 7, new("Observer", 1, 500, 100, 50, 40), 500, 100, 50, observer));
        uint beforeMark = marked.Life;
        contact = session.Runtime.Now + release;
        cast = Cast();
        session.AdvanceTo(contact);
        Assert.True(enlargedRing.Life < enlargedRing.MaxLife);
        Assert.Equal(enlargedDisc.MaxLife, enlargedDisc.Life);
        session.AdvanceTo(contact + gap);
        await cast;
        await battle.FlushOutput();
        Assert.True(enlargedDisc.Life < enlargedDisc.MaxLife);
        Assert.Equal(outside.MaxLife, outside.Life);
        Assert.Equal(beforeMark, marked.Life);
        Assert.True(conn.Monsters[shared].FlinchUntil > Game.ProjectileWorldTime(conn));
        var flinch = InstanceProtocol.ActorAnimation(shared, .35f, PlayerHitRecovery.TakeHitAnimation);
        Assert.Single(observed, bytes => bytes.SequenceEqual(flinch));
    }

    static async Task VortexDetonatesSelectedBolts()
    {
        using var battle = new ArrowRainAndMirageTests.Battle("Vortex of Projection", "Frostbolt", "Assassin's Mark");
        var conn = battle.Conn;
        var session = battle.Session;
        conn.Monsters.Clear();
        battle.AdvanceBy(12);
        conn.Clock.Start();
        var vortex = Game.GemSkillEffects(conn).Single(s => s.Name == "Vortex of Projection");
        var frostbolt = Game.GemSkillEffects(conn).Single(s => s.Name == "Frostbolt");
        long start = session.Runtime.Now;
        long contact = start + ProjectileRuntime.Time(Game.CastMs(conn, vortex.Effect, vortex.Id)!.Value / 1000d
            * Game.Animations.SpellAreaRelease(Game.SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject));
        long half = ProjectileRuntime.QuantaPerSecond / 2;
        var aim = (X: 300, Y: 100);

        ProjectileEntity Bolt(int x, double speed, double lifetime = 10)
        {
            var use = Game.CaptureGemProjectile(conn, session, frostbolt.Id, frostbolt, (x + 600, 100), new(0), from: (x, 100));
            long id = session.Release(use with { Profile = use.Profile with { Speed = speed, Lifetime = ProjectileRuntime.Time(lifetime) } });
            return session.Runtime.Projectiles.Single(p => p.UseId == id);
        }
        SpawnedMonster Witness(uint id, int x, int y) =>
            conn.Monsters[id] = new("vortex witness", new(x, y, 0), 0, 0, 100000) { Hostile = true };
        Task Cast() => Game.LaunchProjectile(session.remote, battle.Stream, battle.Ciphers, conn, vortex.Id,
            (100, 100), aim, conn.ActionInterruptionGeneration);

        var first = Bolt(281, .001);
        var second = Bolt(350, 30);
        var expired = Bolt(207, .001, (contact - start + half + 1d) / ProjectileRuntime.QuantaPerSecond);
        int laterX = (int)Math.Round(350 + second.Speed * (contact + half - start) / ProjectileRuntime.QuantaPerSecond);
        var one = Witness(800, 281, 126);
        var beyond = Witness(801, 281, 127);
        var two = Witness(802, laterX, 126);
        var oldCell = Witness(803, 350, 126);
        var lost = Witness(804, 207, 126);
        var self = Witness(805, 100, 122);
        var marked = Witness(806, 550, 100);
        var mark = Game.GemSkillEffects(conn).Single(s => s.Name == "Assassin's Mark");
        conn.PlayerStats[Program.Stats.Key("nova_spells_cast_at_marked_target")!.Value] = 1;
        void Mark(SpawnedMonster target)
        {
            lock (conn.Monsters)
                Assert.True(Game.ApplyCurseLocked(conn, target, mark.Id, Game.ProjectileWorldTime(conn), aura: false));
        }
        Mark(marked);
        var cast = Cast();
        session.AdvanceTo(contact - 1);
        Assert.Contains(first, session.Runtime.Projectiles);
        session.AdvanceTo(contact);
        Assert.DoesNotContain(first, session.Runtime.Projectiles);
        Assert.Contains(second, Game.FrostboltNovaTargets(conn, aim, 5));
        Assert.Empty(Game.FrostboltNovaTargets(conn, aim, 5, forVortex: true));
        Assert.True(one.Life < one.MaxLife);
        Assert.Equal(beyond.MaxLife, beyond.Life);
        Assert.Equal(two.MaxLife, two.Life);
        uint afterFirst = one.Life;
        Assert.Equal(marked.MaxLife, marked.Life); // The selected Frostbolt takes precedence over the mark.
        lock (conn.Monsters) marked.Curses.Clear();

        // A second use finds only reserved bolts. It falls back to the player while the first use still owes its bolts.
        var overlappingCast = Cast();
        session.AdvanceTo(contact + half - 1);
        Assert.Contains(second, session.Runtime.Projectiles);
        Assert.Equal(two.MaxLife, two.Life);
        session.AdvanceTo(contact + half);
        Assert.DoesNotContain(second, session.Runtime.Projectiles);
        Assert.True(two.Life < two.MaxLife);
        uint afterSecond = two.Life;
        Assert.Equal(oldCell.MaxLife, oldCell.Life);
        var replacement = Bolt(207, .001);
        session.AdvanceTo(contact + 4 * half + ProjectileRuntime.QuantaPerSecond);
        await Task.WhenAll(cast, overlappingCast);
        await battle.FlushOutput();
        Assert.DoesNotContain(expired, session.Runtime.Projectiles);
        Assert.Contains(replacement, session.Runtime.Projectiles);
        Assert.True(self.Life < self.MaxLife);
        Assert.Equal(lost.MaxLife, lost.Life);
        Assert.Equal(172u, afterFirst - one.Life);
        Assert.Equal(172u, afterSecond - two.Life);
        Assert.Equal(beyond.MaxLife, beyond.Life);

        // Without a Frostbolt the mark is resolved at release, even when the caster can aim novas at the cursor.
        battle.AdvanceBy(12);
        conn.PlayerStats[Program.Stats.Key("nova_spells_cast_at_target_location")!.Value] = 1;
        Mark(marked);
        var markedCast = Cast();
        var next = Witness(807, 700, 100);
        Mark(next);
        next.Walk.Teleport(800, 100, Game.ProjectileWorldTime(conn));
        var nearNewCell = Witness(808, 800, 110);
        battle.AdvanceBy(1);
        await markedCast;
        Assert.Equal(marked.MaxLife, marked.Life);
        Assert.True(next.Life < next.MaxLife);
        Assert.True(nearNewCell.Life < nearNewCell.MaxLife);

        // A full refresh in the same stat scope observes a removed recipient slot immediately.
        lock (conn.Monsters)
        {
            using var scope = new Game.PlayerStatScope(conn, fresh: true);
            uint idKey = Program.Stats.Key("marked_target_id")!.Value, hasKey = Program.Stats.Key("has_a_marked_target")!.Value;
            var active = Game.PlayerStatsWithCleared(conn);
            Assert.Contains((idKey, 807), active);
            Game.PlayerStatsUpdateAtGate(conn, active);
            next.Curses.Clear();
            var cleared = Game.PlayerStatsWithCleared(conn);
            Assert.Contains((idKey, 0), cleared);
            Assert.Contains((hasKey, 0), cleared);
        }
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
