using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ProjectileSkillProfileTests
{
    public ProjectileSkillProfileTests() => TestSaves.Ensure();
    static Func<string, int> Stats(params (string Name, int Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;
    static ProjectileProfile Profile(string effect, params (string Name, int Value)[] values) =>
        GatewayProjectileSkillProfiles.Resolve(effect,
            effect == "Fireball" ? "Metadata/Projectiles/Fireball" : "Metadata/Projectiles/WandProjectile",
            Stats(values), 100, effect != "Fireball") with { Radius = 1 };
    static ProjectileEvent[] Areas(ProjectileRuntimeProbe w) => w.Events.Where(e => e.Kind == ProjectileEventKind.Area).ToArray();

    [Theory]
    [InlineData("KineticBlast", 4, 20)]
    [InlineData("KineticBlastAltX", 3, 28)]
    [InlineData("KineticBlastAltY", 4, 20)]
    public void KB_uses_fixed_scaled_circle_and_thirtieth_second_area_slots(string effect, int count, int placement)
    {
        var w = new ProjectileRuntimeProbe();
        w.Release(Profile(effect, ("skill_area_of_effect_+%", 300)) with { Range = 20 });
        w.Advance(.2 - 1d / ProjectileRuntime.QuantaPerSecond); Assert.Empty(Areas(w));
        w.Advance(.2); Assert.Single(Areas(w));
        w.Advance(.2 + 1d / 30 - 1d / ProjectileRuntime.QuantaPerSecond); Assert.Single(Areas(w));
        w.Advance(.2 + 1d / 30); Assert.Equal(2, Areas(w).Length);
        w.Advance(1);
        var areas = Areas(w); Assert.Equal(count, areas.Length);
        Assert.Equal(Enumerable.Range(0, count).Select(i => ProjectileRuntime.Time(.2) + i * 4000), areas.Select(e => e.At));
        Assert.All(areas, e => Assert.Equal(placement * 2, (e.Position - new ProjectileVector(30, 10)).Length, 7));
        double previous = Math.Atan2(areas[0].Position.Y - 10, areas[0].Position.X - 30), turn = 0;
        foreach (var area in areas.Skip(1))
        {
            double angle = Math.Atan2(area.Position.Y - 10, area.Position.X - 30);
            double delta = (angle - previous + Math.Tau) % Math.Tau;
            Assert.InRange(delta, double.Epsilon, 2 * Math.Tau / count); turn += delta; previous = angle;
        }
        Assert.True(turn < Math.Tau);
    }

    [Theory]
    [InlineData("Fireball")]
    [InlineData("KineticBlast")]
    public void Lone_target_admits_one_direct_collision_and_no_denied_sibling_blast(string effect)
    {
        var w = new ProjectileRuntimeProbe(); w.Target(1, 40, 10);
        w.Release(Profile(effect, ("number_of_additional_projectiles", 4)) with { SpreadRadians = 0 }); w.Advance(.6);
        Assert.Single(w.Attempts, h => h.Target.Id == 1);
        Assert.Equal(effect == "Fireball" ? 1 : 4, Areas(w).Length);
        Assert.Equal(4, w.Runtime.Projectiles.Count);
    }

    [Fact]
    public void KB_natural_stop_triggers_one_group_before_return_then_one_at_return_completion()
    {
        var w = new ProjectileRuntimeProbe(); var owner = new ProjectileActorKey(1000);
        w.Targets.Add(new(owner, 1, _ => new(10, 10), 0));
        w.Runtime.Release(new(Profile("KineticBlast", ("projectiles_return", 1)) with { Range = 20 },
            owner, owner, 1, new(10, 10), new(110, 10)) { ReturnAnchor = owner });
        w.Advance(.2);
        Assert.Single(Areas(w)); Assert.Equal(1, Assert.Single(w.Runtime.Projectiles).Phase);
        w.Advance(.4); Assert.Equal(4, Areas(w).Length);
        w.Advance(2.2);
        Assert.Equal(8, Areas(w).Length); Assert.Empty(w.Runtime.Projectiles);
    }

    [Theory]
    [InlineData("Fireball")]
    [InlineData("KineticBlast")]
    public void Returning_siblings_share_a_new_direct_quota_and_keep_independent_areas(string effect)
    {
        var w = new ProjectileRuntimeProbe(); w.Target(1, 40, 10); w.Target(2, 0, 10);
        var owner = new ProjectileActorKey(1000); w.Targets.Add(new(owner, 1, _ => new(10, 10), 0));
        var p = Profile(effect, ("projectiles_return", 1), ("number_of_additional_projectiles", 1),
            ("returning_projectiles_always_pierce", 1)) with { SpreadRadians = 0 };
        w.Runtime.Release(new(p, owner, owner, 1, new(10, 10), new(110, 10)) { ReturnAnchor = owner });
        w.Advance(5);
        Assert.Single(w.Attempts, h => h.Phase == 0 && h.Stage == "direct" && h.Target.Id == 1);
        Assert.Single(w.Attempts, h => h.Phase == 1 && h.Stage == "direct" && h.Target.Id == 1);
        Assert.Single(w.Attempts, h => h.Phase == 1 && h.Stage == "direct" && h.Target.Id == 2);
        Assert.True(Areas(w).Length >= 2);
    }

    [Theory]
    [InlineData("Fireball")]
    [InlineData("KineticBlast")]
    [InlineData("BurningArrow")]
    public void Simultaneous_siblings_pass_denied_victim_and_hit_the_next(string effect)
    {
        var w = new ProjectileRuntimeProbe(); w.Target(1, 40, 10); w.Target(2, 80, 10);
        w.Release(Profile(effect, ("number_of_additional_projectiles", 1)) with { SpreadRadians = 0 }); w.Advance(1.5);
        Assert.Single(w.Attempts, h => h.Target.Id == 1 && (h.Stage == "direct" || h.Stage == "fireball-explosion"));
        Assert.Single(w.Attempts, h => h.Target.Id == 2 && (h.Stage == "direct" || h.Stage == "fireball-explosion"));
    }

    [Fact]
    public void Fireball_pierces_directly_then_terminal_area_hits_victim_once_and_neighbor()
    {
        var w = new ProjectileRuntimeProbe(); w.Target(1, 40, 10); w.Target(2, 80, 10); w.Target(3, 80, 18);
        w.Release(Profile("Fireball", ("projectile_base_number_of_targets_to_pierce", 1))); w.Advance(1);
        Assert.Equal("direct", Assert.Single(w.Attempts, h => h.Target.Id == 1).Stage);
        var terminal = Assert.Single(w.Attempts, h => h.Target.Id == 2);
        Assert.Equal("fireball-explosion", terminal.Stage); Assert.True(terminal.IsArea);
        Assert.True(Assert.Single(w.Attempts, h => h.Target.Id == 3).IsArea);
        Assert.Single(Areas(w));
    }

    [Fact]
    public void Fireball_return_defers_blast_and_damage_until_terminal_return_contact()
    {
        var w = new ProjectileRuntimeProbe(); w.Target(1, 80, 10); w.Target(2, 80, 18);
        w.Target(3, 0, 10); w.Target(4, 0, 18);
        var owner = new ProjectileActorKey(1000); w.Targets.Add(new(owner, 1, _ => new(10, 10), 0));
        w.Runtime.Release(new(Profile("Fireball", ("projectiles_return", 1)), owner, owner, 1, new(10,10), new(110,10)) { ReturnAnchor = owner });
        w.Advance(.75);
        Assert.Empty(Areas(w)); Assert.DoesNotContain(w.Attempts, h => h.Target.Id == 2);
        Assert.Equal("direct", Assert.Single(w.Attempts).Stage);
        w.Advance(2);
        Assert.Single(Areas(w));
        Assert.True(Assert.Single(w.Attempts, h => h.Target.Id == 3).IsArea);
        Assert.True(Assert.Single(w.Attempts, h => h.Target.Id == 4).IsArea);
        Assert.Equal(ProjectileEndReason.Enemy, Assert.Single(w.Events, e => e.Kind == ProjectileEventKind.End).Reason);
    }

    [Fact]
    public void Neighboring_independent_fireball_blasts_overlap_the_same_target()
    {
        var w = new ProjectileRuntimeProbe(); w.Target(1, 40, 8); w.Target(2, 40, 12); w.Target(3, 45, 10);
        w.Release(Profile("Fireball") with { Count = 2, SpreadRadians = .14, AimSpreadDistance = 0, Radius = 1.5 });
        w.Advance(1);
        Assert.Equal(2, Areas(w).Length);
        Assert.Equal(2, w.Attempts.Count(h => h.Target.Id == 3 && h.IsArea));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fireball_terminal_terrain_and_empty_range_both_explode(bool wall)
    {
        var w = new ProjectileRuntimeProbe(); if (wall) w.Blocked = (x, _) => x == 60;
        w.Release(Profile("Fireball") with { Range = 80 }); w.Advance(1);
        Assert.Single(Areas(w));
        Assert.Equal(wall ? ProjectileEndReason.Terrain : ProjectileEndReason.Distance,
            Assert.Single(w.Events, e => e.Kind == ProjectileEventKind.End).Reason);
    }

    [Theory]
    [InlineData("Fireball", false)]
    [InlineData("KineticBlast", false)]
    [InlineData("Fireball", true)]
    [InlineData("KineticBlast", true)]
    public void Finite_pierce_hands_off_to_fork_or_chain(string effect, bool fork)
    {
        var w = new ProjectileRuntimeProbe(); w.Target(1, 30, 10); w.Target(2, 60, 10); w.Target(3, 85, 25);
        w.Release(Profile(effect, ("projectile_base_number_of_targets_to_pierce", 1),
            (fork ? "projectiles_fork" : "number_of_chains", 1)));
        w.Advance(1.5);
        Assert.Contains(w.Events, e => fork ? e.Kind == ProjectileEventKind.Spawn && e.Projectile.ParentId != 0
            : e.Kind == ProjectileEventKind.Redirect);
    }

    [Theory]
    [InlineData("Fireball")]
    [InlineData("KineticBlast")]
    public void Cannot_pierce_overrides_a_finite_grant(string effect)
    {
        var w = new ProjectileRuntimeProbe(); w.Target(1, 30, 10); w.Target(2, 80, 10);
        w.Release(Profile(effect, ("projectile_base_number_of_targets_to_pierce", 5), ("cannot_pierce", 1))); w.Advance(1.5);
        Assert.DoesNotContain(w.Attempts, h => h.Target.Id == 2);
        Assert.Equal(ProjectileEndReason.Enemy, Assert.Single(w.Events, e => e.Kind == ProjectileEventKind.End).Reason);
    }

    [Theory]
    [InlineData("Fireball", "spells_number_of_additional_projectiles")]
    [InlineData("KineticBlast", "attacks_number_of_additional_projectiles")]
    public void Split_conversion_consumes_scoped_quantity_once(string effect, string scoped)
    {
        var p = Profile(effect, ("number_of_additional_projectiles", 2), (scoped, 2),
            ("modifiers_to_number_of_projectiles_instead_apply_to_splitting", 1));
        Assert.Equal(1, p.Count);
        var w = new ProjectileRuntimeProbe(); w.Target(1, 30, 10);
        for (uint i = 2; i <= 5; i++) w.Target(i, 60, i * 10);
        w.Release(p); w.Advance(.21);
        Assert.Equal(4, w.Events.Count(e => e.Kind == ProjectileEventKind.Spawn && e.Projectile.ParentId != 0));
    }

    [Theory]
    [InlineData("KineticBlast", 4, 14, 20, .6)]
    [InlineData("KineticBlastAltX", 3, 19, 28, 1)]
    [InlineData("KineticBlastAltY", 4, 14, 20, .6)]
    public void KB_profiles_emit_separate_scaled_sector_clusters(string effect, int count, int blast, int placement, double multiplier)
    {
        var p = Profile(effect); var w = new ProjectileRuntimeProbe(); w.Target(1, 40, 10);
        w.Release(p); w.Advance(.6);
        Assert.Equal(count, Areas(w).Length);
        var impact = Assert.Single(w.Events, e => e.Kind == ProjectileEventKind.End).Position;
        Assert.All(Areas(w), a => { Assert.Equal(blast, a.Radius); Assert.Equal(placement, (a.Position - impact).Length, 7); });
        Assert.Single(w.Attempts); Assert.Equal("direct", w.Attempts[0].Stage);
        Assert.Equal(multiplier, p.ContactArea!.Multiplier);
        Assert.True(p.ContactArea.SectorPlacement); Assert.True(p.ContactArea.RespectTerrain);
        Assert.True(p.ContactArea.ExcludeContactTarget);
    }

    [Fact]
    public void KB_area_increase_scales_blast_and_placement_separately_once()
    {
        var p = Profile("KineticBlast", ("skill_area_of_effect_+%", 300), ("active_skill_base_area_of_effect_radius", 14),
            ("active_skill_base_secondary_area_of_effect_radius", 20), ("cluster_burst_spawn_amount", 6),
            ("active_skill_area_damage_+%_final", -25));
        Assert.Equal(28, p.ContactArea!.Radius); Assert.Equal(40, p.ContactArea.Offset);
        Assert.Equal(6, p.ContactArea.Count); Assert.Equal(.75, p.ContactArea.Multiplier);
    }

    [Fact]
    public void Clustering_converts_all_quantity_and_rejects_Fury_Valve_split()
    {
        var p = Profile("KineticBlastAltX", ("number_of_additional_projectiles", 4), ("attacks_number_of_additional_projectiles", 2),
            ("modifiers_to_number_of_projectiles_instead_apply_to_splitting", 1), ("projectile_number_to_split", 4));
        Assert.Equal(1, p.Count); Assert.Equal(9, p.ContactArea!.Count); Assert.True(p.CannotSplit);
        var w = new ProjectileRuntimeProbe(); w.Target(1, 40, 10); w.Target(2, 70, 20);
        w.Release(p); w.Advance(.9);
        Assert.Equal(9, Areas(w).Length); Assert.Single(w.Events, e => e.Kind == ProjectileEventKind.Spawn);
    }

    [Fact]
    public void KB_triggers_each_pierced_impact_and_final_stop_without_duplicate_terminal_group()
    {
        var w = new ProjectileRuntimeProbe(); w.Target(1, 30, 10); w.Target(2, 80, 10);
        w.Release(Profile("KineticBlast", ("projectile_base_number_of_targets_to_pierce", 1))); w.Advance(1);
        Assert.Equal(8, Areas(w).Length);
        Assert.Equal(2, w.Attempts.Count(h => h.Stage == "direct"));
    }

    [Fact]
    public void KB_terrain_skips_blocked_sectors_and_empty_range_emits_one_group()
    {
        var empty = new ProjectileRuntimeProbe(); empty.Release(Profile("KineticBlast") with { Range = 30 }); empty.Advance(1);
        Assert.Equal(4, Areas(empty).Length);
        var wall = new ProjectileRuntimeProbe { Blocked = (x, _) => x >= 60 };
        wall.Release(Profile("KineticBlast")); wall.Advance(1);
        Assert.InRange(Areas(wall).Length, 1, 3); Assert.All(Areas(wall), e => Assert.True(e.Position.X < 60));
    }

    [Theory]
    [InlineData("KineticBlast", 4)]
    [InlineData("KineticBlastAltX", 3)]
    [InlineData("KineticBlastAltY", 4)]
    public void Terrain_omits_centres_and_compacts_the_surviving_KB_sequence(string effect, int count)
    {
        var wall = new ProjectileRuntimeProbe { Blocked = (x, _) => x >= 60 };
        wall.Release(Profile(effect)); wall.Advance(1);
        var impact = Assert.Single(wall.Events, e => e.Kind == ProjectileEventKind.End);
        var open = new ProjectileRuntimeProbe();
        open.Release(Profile(effect) with { Range = impact.Position.X - 10 }); open.Advance(1);
        Assert.Equal(count, Areas(open).Length); Assert.InRange(Areas(wall).Length, 1, count - 1);
        var surviving = Areas(open).Where(e => e.Position.X < 60).ToArray();
        Assert.Equal(surviving.Length, Areas(wall).Length);
        foreach (var (actual, expected) in Areas(wall).Zip(surviving))
        {
            Assert.Equal(expected.Position.X, actual.Position.X, 7); Assert.Equal(expected.Position.Y, actual.Position.Y, 7);
        }
        Assert.Equal(Enumerable.Range(0, surviving.Length).Select(i => impact.At + i * 4000), Areas(wall).Select(e => e.At));
    }

    [Theory]
    [InlineData("KineticBlast", 4)]
    [InlineData("KineticBlastAltX", 3)]
    [InlineData("KineticBlastAltY", 4)]
    public void KB_contact_group_excludes_its_large_victim_but_all_blasts_can_hit_a_neighbor(string effect, int count)
    {
        var w = new ProjectileRuntimeProbe(); w.Targets.Add(new(new(1), 2, _ => new(40, 10), 60));
        w.Target(2, 40, 100);
        w.Release(Profile(effect, ("active_skill_base_area_of_effect_radius", 200))); w.Advance(.25);
        Assert.Equal(count, Areas(w).Length);
        Assert.Equal("direct", Assert.Single(w.Attempts, h => h.Target.Id == 1).Stage);
        Assert.Equal(count, w.Attempts.Count(h => h.Target.Id == 2 && h.IsArea));
    }

    [Theory]
    [InlineData("KineticBlast", 4)]
    [InlineData("KineticBlastAltX", 3)]
    [InlineData("KineticBlastAltY", 4)]
    public void KB_pierced_contact_groups_only_exclude_their_own_victims(string effect, int count)
    {
        var w = new ProjectileRuntimeProbe(); w.Target(1, 40, 10); w.Target(2, 50, 10);
        w.Release(Profile(effect, ("projectile_base_number_of_targets_to_pierce", 1),
            ("active_skill_base_area_of_effect_radius", 100))); w.Advance(.8);
        Assert.Equal(count * 2, Areas(w).Length);
        foreach (uint id in new uint[] { 1, 2 })
        {
            Assert.Single(w.Attempts, h => h.Target.Id == id && !h.IsArea);
            Assert.Equal(count, w.Attempts.Count(h => h.Target.Id == id && h.IsArea));
        }
    }

    [Theory]
    [InlineData("KineticBlast", 4, false)]
    [InlineData("KineticBlast", 4, true)]
    [InlineData("KineticBlastAltX", 3, false)]
    [InlineData("KineticBlastAltX", 3, true)]
    [InlineData("KineticBlastAltY", 4, false)]
    [InlineData("KineticBlastAltY", 4, true)]
    public void KB_wall_or_range_group_can_hit_an_earlier_pierced_victim(string effect, int count, bool wall)
    {
        var w = new ProjectileRuntimeProbe(); w.Target(1, 40, 10);
        if (wall) w.Blocked = (x, _) => x >= 100;
        w.Release(Profile(effect, ("projectile_base_number_of_targets_to_pierce", 1),
            ("active_skill_base_area_of_effect_radius", 100)) with { Range = 90 }); w.Advance(1.5);
        int terminalBlasts = Areas(w).Length - count;
        Assert.InRange(terminalBlasts, 1, count);
        Assert.Single(w.Attempts, h => h.Target.Id == 1 && !h.IsArea);
        Assert.Equal(terminalBlasts, w.Attempts.Count(h => h.Target.Id == 1 && h.IsArea));
        Assert.Equal(wall ? ProjectileEndReason.Terrain : ProjectileEndReason.Distance,
            Assert.Single(w.Events, e => e.Kind == ProjectileEventKind.End).Reason);
    }

    [Fact]
    public void Catalog_uses_common_defaults_including_base_count_spread_and_named_return_restrictions()
    {
        var p = Program.CatalogProjectileDefinition(Program.Skills.EffectRow("BurningArrow"))!
            .Parameters.Resolve(Stats(("base_number_of_projectiles", 2), ("attacks_number_of_additional_projectiles", 2)), 100);
        Assert.Equal(4, p.Count); Assert.Equal(40, p.AimSpreadDistance); Assert.Equal(ProjectileHitScope.Emission, p.HitRule.Scope);
        Assert.Null(p.OutboundHitLimit); Assert.Null(p.ReturnHitLimit); Assert.False(p.IgnoreTerrainOnReturn);
        Assert.True(Profile("Spark", ("projectiles_return", 1)).CannotReturn);
        Assert.False(Profile("FreezingPulse").ReturnOnDistance);
    }
}

public partial class OwnAuraGatewayTests
{
    [Theory]
    [InlineData("Fireball", "Metadata/Effects/Spells/fireball/explode_new2.ao")]
    [InlineData("KineticBlast", "Metadata/Effects/Spells/cluster_burst/mini.ao")]
    [InlineData("KineticBlastAltX", "Metadata/Effects/Spells/cluster_burst/mini.ao")]
    [InlineData("KineticBlastAltY", "Metadata/Effects/Spells/cluster_burst/mini.ao")]
    public async Task Skill_input_commits_direct_and_cluster_damage_before_native_output(string effect, string art)
    {
        bool fireball = effect == "Fireball";
        using var f = new Fixture(fireball ? "Fireball" : "KineticBlast");
        if (!fireball)
        {
            var type = Program.Items.Find("Metadata/Items/Gems/SkillGemKineticBlast")!;
            var variant = Program.Items.Gem(type)!.Variants.First(v => v.Effect == Program.Skills.EffectRow(effect));
            f.Items.Change(PlayerInventories.Weapon1, f.Host.Id, item => item with
                { Sockets = [item.Sockets[0] with { Gem = item.Sockets[0].Gem! with { GemVariant = variant.Index } }] });
        }
        // Replacing a gem's effect deliberately retires its old skill id. Use the newly assigned native id.
        var active = ((IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>)
            Call("GemSkillEffects", f.Conn.Character!)!).Single(g => g.Effect == f.Effect(effect));
        int skillIndex = Program.GemSkillIdPool.Value.ToList().IndexOf(active.Id);
        Assert.True(skillIndex >= 0);
        f.Conn.PlayerStats[Program.Stats.Key("global_cannot_crit")!.Value] = 1;
        foreach (string boundary in new[] { "minimum", "maximum" })
            f.Conn.PlayerStats[Program.Stats.Key((fireball ? "spell_" : "attack_") + boundary + "_added_chaos_damage")!.Value] = 100;
        f.Conn.Pools = Program.PoolsFor(f.Conn, 100) with { Accuracy = int.MaxValue };
        var direct = new SpawnedMonster("direct skill target", new(45, 32, 0), 0, 2, 10000);
        var nearby = new SpawnedMonster("off-path skill target", new(45, fireball ? 40 : 48, 0), 0, 2, 10000);
        f.Conn.Monsters.Add(42, direct); f.Conn.Monsters.Add(43, nearby);
        double maximum = Enum.GetValues<DamageType>().Sum(t => ((DamagePacket)Call("SupportedHit", f.Conn, active.Id)!)[t].Max);
        // Drive the real production session manually; a wall-clock pump could expire brief native effects
        // while this test deliberately blocks output under a busy full-suite scheduler.
        f.Conn.ProjectileRuntime = new Program.ProjectileSession("skill-input", f.Stream, f.Ciphers, f.Conn);
        await f.Gate.WaitAsync();
        bool gateOwned = true;
        try
        {
            using (Program.ProjectileMutation(f.Conn, advance: false))
            {
                Assert.True(f.Use("ground", skillIndex).IsCompletedSuccessfully);
                var session = Assert.IsType<Program.ProjectileSession>(f.Conn.ProjectileRuntime);
                for (int i = 0; i < 120 && (direct.Life == 10000 || nearby.Life == 10000); i++)
                    session.AdvanceTo(session.Runtime.Now + ProjectileRuntime.TickQuanta);
                Assert.True(direct.Life < 10000);
                Assert.True(nearby.Life < 10000, "At least one independent area must hit an off-path neighbour.");
                Assert.InRange((double)(10000 - direct.Life), 1d, Math.Ceiling(maximum));
            }
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Conn.ProjectileRuntime!.Enqueue(() => { drained.SetResult(); return Task.CompletedTask; });
            gateOwned = false; f.Gate.Release();
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
            byte[] sentinel = new LoginWriter().U16(0x7ffe).U32(0x12345678).ToArray();
            await Program.Send("skill-input", f.Stream, f.Ciphers, sentinel.ToArray(), null);
            async Task<byte[]> ReadThroughSentinel()
            {
                var bytes = new List<byte>();
                while (true)
                {
                    bytes.AddRange(await f.Drain());
                    if (bytes.Count >= sentinel.Length && bytes.TakeLast(sentinel.Length).SequenceEqual(sentinel))
                        return bytes.ToArray();
                }
            }
            var output = await ReadThroughSentinel().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(output.AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes(art)) >= 0);
        }
        finally
        {
            f.Conn.Closed = true; f.Conn.ProjectileRuntime?.Invalidate();
            if (gateOwned) { gateOwned = false; f.Gate.Release(); }
            if (f.Conn.ProjectileRuntime is { } session) await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
