using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ArrowRainAndMirageTests
{
    [Theory]
    [InlineData("Blink Arrow", true)]
    [InlineData("Mirror Arrow", false)]
    public async Task Arrow_clone_and_owner_land_at_their_respective_positions(string name, bool blink)
    {
        using var b = new Battle(name);
        var c = b.Conn;
        c.Clock.Start();
        var gem = Game.GemSkillEffects(c).Single();
        Assert.False(Game.LandsAtAim(c, gem.Effect));
        var origin = c.Walk.Position(Game.AreaTime(c));
        var aim = (origin.X + 24, origin.Y);
        Assert.True(Game.TryBeginSkillCooldownUse(c, gem.Id, gem.Effect, Game.CooldownForSkill(c, gem.Id), false,
            () => c.ActionCount++, out var paid));
        Assert.True(await Game.CastGemSkill("clone", b.Stream, b.Ciphers, c, gem.Id, origin, aim, cooldownUse: paid));
        var clone = Assert.Single(c.Summons.Fighters.Values);
        Assert.Equal(blink ? origin : aim, clone.Monster.Walk.Position(Game.AreaTime(c)));
        Assert.Equal(blink ? aim : origin, c.Walk.Position(Game.ProjectileWorldTime(c)));
    }

    sealed class Battle : IDisposable
    {
        readonly IGameLogic? previous = Program.GameLogicOrNull;
        readonly GameServer.Modding.IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        readonly SpellTotemExecutionTests.Wire wire = new();
        public System.Net.Sockets.NetworkStream Stream => wire.Stream;
        public GameServer.Core.Protocol.Login.ConnectionCiphers Ciphers => wire.Ciphers;
        public readonly ClientState Conn;
        public readonly Game.ProjectileSession Session;
        public readonly SpawnedMonster Enemy = new("target", new(120, 100, 0), 0, 0, 100000) { Hostile = true };
        public readonly SpawnedMonster Ally = new("friendly", new(120, 100, 0), 0, 0, 100000) { Hostile = false };

        public Battle(params string[] gems)
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
            Conn = SpellTotemExecutionTests.Connection("Rain" + Guid.NewGuid().ToString("N"));
            Conn.Clock.Stop();
            Conn.Walk.Teleport(100, 100, 0);
            Conn.PlayerStats[Program.Stats.Key("global_always_hit")!.Value] = 1;
            var items = Game.ItemsOf(Conn);
            var sockets = gems.Select(name => new ItemSocket(SocketColour.White, ItemCatalog.Create(Program.Items.FindGem(name)!.Value.Type))).ToArray();
            items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Crude Bow")!) with
            { Sockets = sockets, Links = [(byte)sockets.Length] });
            Conn.Monsters[200] = Enemy;
            Conn.Monsters[201] = Ally;
            Session = new Game.ProjectileSession("rain", wire.Stream, wire.Ciphers, Conn);
            Conn.ProjectileRuntimeState = Session;
        }

        public void Dispose()
        {
            Session.Invalidate();
            Conn.Closed = true;
            Session.OutputCompleted.GetAwaiter().GetResult();
            lifetime.Stop(_ => { });
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previous!);
            PlayerInventories.Forget(Conn.Character!);
            wire.Dispose();
        }
    }

    [Fact]
    public void Rain_lands_after_release_and_toxic_pods_damage_only_enemies()
    {
        var aim = new ProjectileVector(120, 100);
        var points = Game.RainLandings("RainOfArrows", aim, 18, 24, [aim + new ProjectileVector(2, 0)], new Random(3));
        Assert.Equal(18, points.Count);
        Assert.Equal(aim, points[0]);
        Assert.Equal(aim + new ProjectileVector(2, 0), points[1]);
        Assert.All(Game.RainLandings("BlastRain", aim, 6, 17, [], new Random(1)), p => Assert.InRange((p - aim).Length, 0, 20));
        using var b = new Battle("Toxic Rain");
        var gem = Game.GemSkillEffects(b.Conn).Single();
        Game.ReleaseArrowRain(b.Session, gem.Id, gem, (120, 100), new(1), 0);
        Assert.Equal(100000u, b.Enemy.Life);
        b.Session.AdvanceTo(ProjectileRuntime.Time(.4));
        // Put the target under a known pod to verify the fractional DoT and the terminal burst.
        var use = Game.CaptureGemProjectile(b.Conn, b.Session, gem.Id, gem, (120, 100), new(1));
        b.Session.PlaceToxicPod(use, aim, 18, 1, 100, 5, 30, null);
        Assert.InRange(Game.ToxicWalkFactor(b.Enemy), .7, .95);
        Assert.Equal(1, Game.ToxicWalkFactor(b.Ally));
        uint landed = b.Enemy.Life;
        b.Session.AdvanceTo(ProjectileRuntime.Time(.95));
        Assert.True(b.Enemy.Life < landed);
        uint ticking = b.Enemy.Life;
        b.Session.AdvanceTo(ProjectileRuntime.Time(1.6));
        Assert.True(b.Enemy.Life < ticking);
        Assert.Equal(1, Game.ToxicWalkFactor(b.Enemy));
        Assert.Equal(100000u, b.Ally.Life);
    }

    [Fact]
    public void Supported_ice_arrow_hit_grants_a_temporary_autonomous_attack()
    {
        using var b = new Battle("Ice Shot", "Mirage Archer Support");
        var gem = Game.GemSkillEffects(b.Conn).Single();
        var use = Game.CaptureGemProjectile(b.Conn, b.Session, gem.Id, gem, (120, 100), new(1));
        var owner = Assert.IsType<ProjectileOffense>(use.Payload);
        Assert.NotNull(owner.MirageSource);
        b.Session.Schedule(0, () => true, () => use with { Payload = owner with { AlwaysHits = true } });
        b.Session.AdvanceTo(ProjectileRuntime.Time(.4));
        uint initial = b.Enemy.Life;
        Assert.True(initial < b.Enemy.MaxLife);
        b.Session.AdvanceTo(ProjectileRuntime.Time(4.8));
        Assert.True(b.Enemy.Life < initial);
        uint expired = b.Enemy.Life;
        b.Session.AdvanceTo(ProjectileRuntime.Time(9));
        Assert.Equal(expired, b.Enemy.Life);
        Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
    }

    [Fact]
    public void Toxic_pods_use_native_linked_multipliers_and_the_actual_casters_restrictions()
    {
        static (long Damage, int More, int Mirage) Run(bool concentrated, bool mirage = false,
            bool linked = true, string? prohibition = null)
        {
            using var b = concentrated
                ? new Battle("Toxic Rain", "Mirage Archer Support", "Concentrated Effect Support")
                : new Battle("Toxic Rain", "Mirage Archer Support");
            var items = Game.ItemsOf(b.Conn);
            if (!linked)
                items.Change(PlayerInventories.Weapon1, items.Items(PlayerInventories.Weapon1).Single().Id,
                    item => item with { Links = [2, 1] });
            // Co-locate every pod and keep fractional damage visible without relying on random spread or hit rolls.
            b.Conn.PlayerStats[Program.Stats.Key("active_skill_base_radius_+")!.Value] = -24;
            b.Conn.PlayerStats[Program.Stats.Key("damage_over_time_+%")!.Value] = 9900;
            if (prohibition is not null) b.Conn.PlayerStats[Program.Stats.Key(prohibition)!.Value] = 1;
            var gem = Game.GemSkillEffects(b.Conn).Single();
            var own = Game.SupportedSkill(b.Conn, gem.Id).Stats;
            int more = own.GetValueOrDefault(Program.Stats.Key("support_area_concentrate_area_damage_+%_final")!.Value);
            int penalty = own[Program.Stats.Key("support_mirage_archer_damage_+%_final")!.Value];
            Game.ReleaseArrowRain(b.Session, gem.Id, gem, (120, 100), new(1), 0, mirage);
            b.Session.AdvanceTo(ProjectileRuntime.Time(.4));
            uint landed = b.Enemy.Life;
            b.Session.AdvanceTo(ProjectileRuntime.Time(.95));
            Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
            Assert.Equal(1000u, b.Conn.PlayerMana);
            return (landed - b.Enemy.Life, more, penalty);
        }

        var baseline = Run(false);
        var supported = Run(true);
        Assert.True(baseline.Damage > 50);
        Assert.True(supported.More > 0);
        double expected = baseline.Damage * (1 + supported.More / 100d);
        Assert.InRange(supported.Damage, Math.Floor(expected) - 3, Math.Ceiling(expected) + 3);
        Assert.Equal(baseline.Damage, Run(true, linked: false).Damage);
        var proxy = Run(true, mirage: true);
        Assert.True(proxy.Mirage < 0);
        expected = supported.Damage * (1 + proxy.Mirage / 100d);
        Assert.InRange(proxy.Damage, Math.Floor(expected) - 3, Math.Ceiling(expected) + 3);
        foreach (string ownerOnly in new[] { "keystone_ancestral_bond", "deal_no_damage_yourself" })
        {
            Assert.Equal(0, Run(true, prohibition: ownerOnly).Damage);
            Assert.Equal(proxy.Damage, Run(true, mirage: true, prohibition: ownerOnly).Damage);
        }
        foreach (string prohibited in new[] { "base_deal_no_chaos_damage", "base_deal_no_damage" })
        {
            Assert.Equal(0, Run(true, prohibition: prohibited).Damage);
            Assert.Equal(0, Run(true, mirage: true, prohibition: prohibited).Damage);
        }
    }

    [Theory]
    [InlineData("Split Arrow", "Crude Bow")]
    [InlineData("Power Siphon", "Driftwood Wand")]
    public void Barrage_support_paces_attacks_using_the_resolved_projectile_count(string skill, string weapon)
    {
        using var b = new Battle(skill, "Barrage Support");
        var items = Game.ItemsOf(b.Conn);
        var bow = items.Items(PlayerInventories.Weapon1).Single();
        items.Change(PlayerInventories.Weapon1, bow.Id, item => ItemCatalog.Create(Program.Items.Find(weapon)!) with
        { Sockets = item.Sockets, Links = item.Links });
        var gem = Game.GemSkillEffects(b.Conn).Single();
        uint penaltyKey = Program.Stats.Key("support_barrage_attack_time_+%_per_projectile_fired")!.Value;
        uint countKey = Program.Stats.Key("total_number_of_projectiles_to_fire")!.Value;
        items.Change(PlayerInventories.Weapon1, bow.Id, item => item with { Links = [1, 1] });
        double baseline = Game.SupportedAttackSeconds(b.Conn, gem.Id);
        int increased = Game.SupportedMainHandAttackSpeedPct(b.Conn, gem.Id);
        float nativeSpeed = (increased + 100) * (float)Game.Supports.AttackSpeedMultiplier(gem.Effect, gem.Level);
        int baselineSpeed = (int)MathF.Round(nativeSpeed - 100, MidpointRounding.AwayFromZero);
        Assert.Equal(0, Game.SupportedSkill(b.Conn, gem.Id).Stats.GetValueOrDefault(penaltyKey));
        items.Change(PlayerInventories.Weapon1, bow.Id, item => item with { Links = [2] });
        int initialCount = 0;
        foreach (int extra in new[] { 0, 2 })
        {
            b.Conn.PlayerStats[Program.Stats.Key("number_of_additional_projectiles")!.Value] = extra;
            var stats = Game.SupportedStats(b.Conn, gem.Id);
            int penalty = stats[penaltyKey];
            Assert.True(penalty > 0);
            Game.ApplyProjectilePanelStats(gem.Effect, stats);
            int count = stats[countKey];
            Assert.True(count > extra + 1);
            if (extra == 0) initialCount = count;
            Assert.Equal(initialCount + extra, count);
            int speed = (int)MathF.Round(nativeSpeed / (1 + penalty * count / 100f) - 100,
                MidpointRounding.AwayFromZero);
            double expected = baseline * (baselineSpeed + 100) / (speed + 100);
            Assert.Equal(expected, Game.SupportedAttackSeconds(b.Conn, gem.Id), 6);
        }
        items.Change(PlayerInventories.Weapon1, bow.Id, item => item with { Links = [1, 1] });
        Assert.Equal(baseline, Game.SupportedAttackSeconds(b.Conn, gem.Id), 6);
    }

    [Fact]
    public void Mirage_attacks_bypass_only_owner_restrictions_and_end_with_their_source()
    {
        foreach (var (ownerOnly, prohibited) in new (string, string[])[]
        {
            ("keystone_ancestral_bond", []), ("deal_no_damage_yourself", []),
            ("keystone_ancestral_bond", ["base_deal_no_physical_damage", "base_deal_no_cold_damage"]),
            ("deal_no_damage_yourself", ["base_deal_no_damage"]),
        })
        {
            using var b = new Battle("Ice Shot", "Mirage Archer Support");
            b.Enemy.Walk.Teleport(170, 100, 0);
            uint ownerKey = Program.Stats.Key(ownerOnly)!.Value;
            uint increase = Program.Stats.Key("projectile_damage_+%")!.Value;
            b.Conn.PlayerStats[ownerKey] = 1;
            b.Conn.PlayerStats[increase] = 37;
            foreach (string name in prohibited) b.Conn.PlayerStats[Program.Stats.Key(name)!.Value] = 1;
            var gem = Game.GemSkillEffects(b.Conn).Single();
            var use = Game.CaptureGemProjectile(b.Conn, b.Session, gem.Id, gem, (170, 100), new(1));
            var owner = Assert.IsType<ProjectileOffense>(use.Payload);
            b.Session.Schedule(0, () => true, () => use);
            b.Session.AdvanceTo(ProjectileRuntime.Time(.4));
            Assert.Equal(b.Enemy.MaxLife, b.Enemy.Life);
            ProjectileOffense? proxy = null;
            for (int tenth = 5; tenth <= 48 && proxy is null; tenth++)
            {
                b.Session.AdvanceTo(ProjectileRuntime.Time(tenth / 10d));
                proxy = b.Session.Runtime.Projectiles.Select(p => p.Use.Payload).OfType<ProjectileOffense>()
                    .FirstOrDefault(p => p.ActualCaster is not null);
            }
            Assert.NotNull(proxy);
            var caster = Assert.IsAssignableFrom<IReadOnlyDictionary<uint, int>>(proxy!.ActualCaster);
            Assert.Equal(0, caster.GetValueOrDefault(ownerKey));
            Assert.Equal(37, caster[increase]);
            foreach (string name in prohibited) Assert.Equal(1, caster[Program.Stats.Key(name)!.Value]);
            Assert.Null(proxy.MirageSource);
            Assert.Null(proxy.CriticalTrigger);
            int penalty = owner.SkillStats[Program.Stats.Key("support_mirage_archer_damage_+%_final")!.Value];
            Assert.Equal(owner.Hit.MaxTotal * (1 + penalty / 100d), proxy.Hit.MaxTotal, 6);
            b.Session.AdvanceTo(ProjectileRuntime.Time(4.8));
            if (prohibited.Length == 0) Assert.True(b.Enemy.Life < b.Enemy.MaxLife);
            else Assert.Equal(b.Enemy.MaxLife, b.Enemy.Life);
            uint expired = b.Enemy.Life;
            b.Session.AdvanceTo(ProjectileRuntime.Time(9));
            Assert.Equal(expired, b.Enemy.Life);
            Assert.Equal(1, Game.CharacterPlayerStats(b.Conn)[ownerKey]);
            Assert.Equal(1000u, b.Conn.PlayerMana);
            Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
        }
        foreach (bool replace in new[] { false, true })
        {
            using var b = new Battle("Ice Shot", "Mirage Archer Support");
            b.Conn.PlayerStats[Program.Stats.Key("keystone_ancestral_bond")!.Value] = 1;
            var gem = Game.GemSkillEffects(b.Conn).Single();
            b.Session.Schedule(0, () => true, () => Game.CaptureGemProjectile(b.Conn, b.Session,
                gem.Id, gem, (120, 100), new(1)));
            b.Session.AdvanceTo(ProjectileRuntime.Time(.4));
            var items = Game.ItemsOf(b.Conn);
            var host = items.Items(PlayerInventories.Weapon1).Single();
            items.Change(PlayerInventories.Weapon1, host.Id, item => replace ? item with
            {
                Sockets = [new(SocketColour.White, ItemCatalog.Create(Program.Items.FindGem("Ice Shot")!.Value.Type)), item.Sockets[1]],
            } : item with { Links = [1, 1] });
            Assert.Equal(gem.Id, Game.GemSkillEffects(b.Conn).Single().Id);
            b.Session.AdvanceTo(ProjectileRuntime.Time(9));
            Assert.Equal(b.Enemy.MaxLife, b.Enemy.Life);
        }
    }
}

