using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modules.Survivors;

namespace GameServer.Gateway.Tests;

public class ArrowRainAndMirageTests
{
    [Theory]
    [InlineData("Blink Arrow", true)]
    [InlineData("Mirror Arrow", false)]
    public async Task Arrow_clone_and_owner_land_at_their_respective_positions(string name, bool blink)
    {
        foreach (string variant in new[] { name, name + " of Bombarding Clones", name + " of Prismatic Clones" })
        {
            using var b = new Battle(variant);
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
            if (variant.Contains("Bombarding", StringComparison.Ordinal))
            {
                var rain = Assert.Single(clone.Abilities);
                Assert.Equal(9511, rain.Effect); Assert.Null(rain.Projectile);
                typeof(Game).GetMethod("GolemAbilityContactLocked", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                    .Invoke(null, ["test", b.Stream, b.Ciphers, c, clone,
                        new GolemState.Action(rain, 200, b.Enemy, (120, 100), 0, clone.Monster.ActionGeneration), 1d]);
                Assert.Equal(11, clone.Pulses.Count);
                Assert.All(clone.Pulses, p => Assert.True(p.Next >= 1.35));
                clone.Gear["accuracy_rating"] = 1000000;
                uint life = b.Enemy.Life;
                Game.GolemStepLocked("test", b.Stream, b.Ciphers, c, clone, 1.36);
                Assert.True(b.Enemy.Life < life);
                Assert.Equal(100000u, b.Ally.Life);
            }
            if (variant.Contains("Prismatic", StringComparison.Ordinal))
            {
                Assert.Equal(306, Assert.Single(clone.Abilities).Effect);
                foreach (string type in new[] { "fire", "chaos" })
                    foreach (string bound in new[] { "minimum", "maximum" })
                        clone.Gear[$"global_{bound}_added_{type}_damage"] = 100;
                clone.Own[Program.Stats.Key("base_chance_to_poison_on_hit_%")!.Value] = 100;
                foreach (var element in new[] { DamageType.Fire, DamageType.Cold, DamageType.Lightning })
                {
                    var attack = Assert.Single(Game.GolemAbilities(c, clone.Monster.Variety!, clone.Monster.Level, clone, clone.Player, element));
                    Assert.True(attack.Attack.Damage.MaxTotal > 0);
                    Assert.All(attack.Attack.Damage.Components, p => Assert.Equal(element, p.Type));
                    var ailmentTarget = new SpawnedMonster("ailment target", new(120, 100, 0), 100, 3, 100000) { Hostile = true };
                    typeof(Game).GetMethod("MinionAilmentsLocked", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                        .Invoke(null, [c, clone, attack, 999u, ailmentTarget, true, 1d]);
                    Assert.False(ailmentTarget.Poisons.Poisoned);
                    Assert.Equal(element == DamageType.Fire, ailmentTarget.Ignites.Burning);
                }
            }
        }
    }

    internal sealed class Battle : IDisposable
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

        public void AdvanceBy(double seconds) => Session.AdvanceTo(ProjectileRuntime.Time(
            Math.Max(Conn.Clock.Elapsed.TotalSeconds, Game.ProjectileWorldTime(Conn)) + seconds));

        public async Task FlushOutput()
        {
            Assert.True(Session.Active, $"closed={Conn.Closed}, rules={Game.CurrentGameRules}, retired={Conn.RageRetired}");
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Session.Enqueue(() => { done.SetResult(); return Task.CompletedTask; });
            var completed = await Task.WhenAny(done.Task, Session.OutputCompleted);
            Assert.Same(done.Task, completed);
            await done.Task;
        }

        public Battle(params string[] gems)
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
            Conn = SpellTotemExecutionTests.Connection("Rain" + Guid.NewGuid().ToString("N"));
            Conn.Clock.Stop();
            Conn.Walk.Teleport(100, 100, 0);
            Conn.PlayerStats[Program.Stats.Key("global_always_hit")!.Value] = 1;
            var items = Game.ItemsOf(Conn);
            var sockets = gems.Select(name =>
            {
                var gem = Program.Items.FindGem(name)!.Value;
                return new ItemSocket(SocketColour.White, ItemCatalog.Create(gem.Type) with { GemVariant = gem.Variant.Index });
            }).ToArray();
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
    public async Task Rain_lands_after_release_and_toxic_pods_damage_only_enemies()
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
        await VortexSharesGroundAcrossCasters();
    }

    static async Task VortexSharesGroundAcrossCasters()
    {
        using var b = new Battle("Vortex");
        using var wire = new SpellTotemExecutionTests.Wire();
        var peer = SpellTotemExecutionTests.Connection("Vortex" + Guid.NewGuid().ToString("N"), b.Conn.Instance);
        var other = new Game.ProjectileSession("vortex-peer", wire.Stream, wire.Ciphers, peer);
        peer.ProjectileRuntimeState = other;
        try
        {
            var at = (X: 120, Y: 100);
            long start = ProjectileRuntime.Time(16);
            b.Session.AdvanceTo(start);
            other.AdvanceTo(start);
            var dying = b.Conn.Monsters[202] = new("credit", new(120, 100, 0), 0, 0, 20) { Hostile = true };
            var walker = b.Conn.Monsters[203] = new("crosses", new(117, 100, 8), 0, 0, 1000) { Hostile = true };
            walker.Walk.Order(125, 100, 16);
            b.Session.PlaceVortexGround(at, 1, 1.375, 32, _ => 0, 0);
            other.PlaceVortexGround(at, 1, .375, 64, name => name == "virtual_chill_effect_+%" ? 100 : 0, 0);
            Assert.Equal(20, b.Enemy.Ailments.Magnitude(MonsterElementalAilments.Kind.Chill, 16));
            b.Session.AdvanceTo(start + ProjectileRuntime.Time(.4));
            Assert.True(dying.Dead);
            Assert.Same(peer, dying.LastHitBy);
            Assert.Equal(10, b.Enemy.Ailments.Magnitude(MonsterElementalAilments.Kind.Chill, 16.4));
            b.Session.AdvanceTo(start + ProjectileRuntime.Time(1.375));
            Assert.Equal(56u, b.Enemy.MaxLife - b.Enemy.Life);
            Assert.Equal(16u, walker.MaxLife - walker.Life);
            Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
            Assert.Equal(0, b.Enemy.Ailments.Magnitude(MonsterElementalAilments.Kind.Chill, 17.375));

            // Removing the timer's caster leaves the other player's ground on the same instance clock.
            other.AdvanceTo(b.Session.Runtime.Now);
            var variety = Game.Monsters.Find("Cannibal")!;
            var boundary = b.Conn.Monsters[204] = new("lethal before exit", new(120, 100, 0), 0, 0, 4)
                { Hostile = true, Variety = variety, NoRewards = true };
            b.Conn.Instance.World.MonsterOrigins[204] = new(InstanceEntities.ObjectTypeHash(variety.Id),
                new MonsterObject(120, 100, 4, variety.ComponentsOrBase, variety.AnimatedObject), (ushort)variety.Row, 1, MonsterRarity.Normal);
            peer.PlayerSent = true;
            var pose = new PlayerObject(peer.Character!, 0, 100, 100, peer.PlayerLife, peer.PlayerMana);
            peer.Instance.PublishPlayer(new(peer.PeerId, 1, pose, 100, 100, peer.PlayerLife, peer));
            other.PlaceVortexGround(at, 1, .625, 32, _ => 0, 0);
            b.Session.PlaceVortexGround(at, 1, 2, 96, _ => 0, 0);
            b.Session.AdvanceTo(start + ProjectileRuntime.Time(1.4375));
            Assert.True(b.Conn.TryBeginAreaChange(new(2, 1, false), out _));
            await Game.FinishVortexGround(b.Conn);
            Assert.True(boundary.Dead);
            Assert.True(peer.KillShares.TryDequeue(out _));
            await Game.FinishVortexGround(b.Conn);
            Assert.Empty(peer.KillShares);
            Assert.Same(b.Conn, boundary.LastHitBy);
            other.AdvanceTo(start + ProjectileRuntime.Time(2));
            Assert.Equal(80u, b.Enemy.MaxLife - b.Enemy.Life);

            // The new caster can observe the old driver's field expiring before that driver's next pump.
            b.Conn.ResumeAfterFailedTransfer();
            var next = new Game.ProjectileSession("vortex-return", b.Stream, b.Ciphers, b.Conn);
            b.Conn.ProjectileRuntimeState = next;
            try
            {
                other.PlaceVortexGround(at, 1, .0625, 32, _ => 0, 0);
                next.AdvanceTo(start + ProjectileRuntime.Time(2.125));
                next.PlaceVortexGround(at, 1, .5, 64, _ => 0, 0);
                other.Invalidate();
                next.AdvanceTo(start + ProjectileRuntime.Time(3));
                Assert.Equal(114u, b.Enemy.MaxLife - b.Enemy.Life);
            }
            finally
            {
                next.Invalidate();
                next.OutputCompleted.GetAwaiter().GetResult();
            }
        }
        finally
        {
            other.Invalidate();
            peer.Closed = true;
            await Game.FinishVortexGround(peer);
            await other.OutputCompleted;
            PlayerInventories.Forget(peer.Character!);
        }
    }


    [Fact]
    public async Task Supported_ice_arrow_hit_grants_a_temporary_autonomous_attack()
    {
        var packets = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
        var outgoing = Program.Outgoing;
        Program.Outgoing = (ciphers, bytes) => { packets.Enqueue(bytes.ToArray()); outgoing?.Invoke(ciphers, bytes); };
        try
        {
            using var b = new Battle("Ice Shot", "Mirage Archer Support");
            var gem = Game.GemSkillEffects(b.Conn).Single();
            var use = Game.CaptureGemProjectile(b.Conn, b.Session, gem.Id, gem, (120, 100), new(1));
            var owner = Assert.IsType<ProjectileOffense>(use.Payload);
            Assert.NotNull(owner.MirageSource);
            b.Conn.Clock.Start();
            b.Session.Schedule(0, () => true, () => use with { Payload = owner with { AlwaysHits = true } });
            b.Session.AdvanceTo(ProjectileRuntime.Time(.4));
            await b.FlushOutput();
            var visual = Assert.Single(b.Conn.MinionOrigins);
            Assert.StartsWith("Metadata/Monsters/Mirage/", b.Conn.Monsters[visual.Key].Variety!.Id);
            Assert.Equal(Program.PlayerId, visual.Value.Object.Parent);
            Assert.Equal("Head_Attachment", visual.Value.Object.Bone);
            Assert.True(b.Conn.Monsters[visual.Key].Invulnerable);
            Assert.True(b.Conn.Monsters[visual.Key].NoRewards);
            Assert.Null(b.Conn.Monsters[visual.Key].Attack);
            byte[] shown = Assert.Single(packets, p => p.Length >= 16 && p[0] == 1 && p[1] == 0xc1 && p[14] == 3 && p[15] == 0xf9);
            Assert.Equal(31, shown.Length);
            Assert.Equal(new byte[] { 0, 0, 0, 1, 0, 0, 0, 0 }, shown[^8..]);
            byte[] display = Assert.Single(packets, p => p.Length >= 16 && p[0] == 1 && p[1] == 0xc1 && p[14] == 6 && p[15] == 0xd4);
            Assert.Equal(41, display.Length);
            Assert.Equal(new byte[] { 3, 0x22 }, display[23..25]);
            Assert.Equal(new byte[] { 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, display[^16..]);
            using var observerWire = new SpellTotemExecutionTests.Wire();
            var observer = new ClientState(b.Conn.Layout, 1, 1) { Instance = b.Conn.Instance, PlayerSent = true, PlayerLife = 1000 };
            b.Conn.PlayerSent = true;
            b.Conn.Instance.Presence.PublishPlayer(new(b.Conn.PeerId, 0, null!, 100, 100, 1000, b.Conn));
            var actor = b.Conn.Monsters[visual.Key];
            b.Conn.PublishedMinions = [new(visual.Key, visual.Value, actor, visual.Value.Object.GridX,
                visual.Value.Object.GridY, actor.Walk.Destination, actor.Life)];
            await Game.SyncPeerMinions("mirage-observer", observerWire.Stream, observerWire.Ciphers, observer);
            uint otherId = observer.MirroredObjects[(b.Conn.PeerId, visual.Key)];
            byte[] peerCreate = InstanceEntities.CreateObject(otherId, visual.Value.Type,
                (visual.Value.Object with { Parent = b.Conn.PeerId }).Blob());
            Assert.Contains(packets, p => Convert.ToHexString(p).Contains(Convert.ToHexString(peerCreate), StringComparison.Ordinal));
            byte[] peerDisplay = display.ToArray();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(peerDisplay.AsSpan(2), otherId);
            Assert.Contains(packets, p => Convert.ToHexString(p).Contains(Convert.ToHexString(peerDisplay), StringComparison.Ordinal));
            uint initial = b.Enemy.Life;
            Assert.True(initial < b.Enemy.MaxLife);
            b.Session.Schedule(ProjectileRuntime.Time(1), () => true, () => use);
            b.Session.AdvanceTo(ProjectileRuntime.Time(1.4));
            await b.FlushOutput();
            Assert.Equal(visual.Key, Assert.Single(b.Conn.MinionOrigins).Key);
            ushort playerSequence = b.Conn.NextActionSequence();
            packets.Clear();
            b.Session.AdvanceTo(ProjectileRuntime.Time(4.8));
            await b.FlushOutput();
            Assert.Equal((ushort)(playerSequence + 1), b.Conn.NextActionSequence());
            var attacks = packets.Where(p => p.Length >= 18 && p[0] == 1 && p[1] == 0xab).ToArray();
            Assert.NotEmpty(attacks);
            Assert.All(attacks, p =>
            {
                Assert.Equal(visual.Key, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(2)));
                Assert.True(p.AsSpan().StartsWith(InstanceProtocol.ActionStarted(visual.Key,
                    100, 100, 120, 100, gem.Id, 0xffff, 0x0408)));
            });
            Assert.True(b.Enemy.Life < initial);
            Assert.Single(b.Conn.MinionOrigins);
            b.Session.AdvanceTo(ProjectileRuntime.Time(6));
            await b.FlushOutput();
            Assert.Empty(b.Conn.MinionOrigins);
            Assert.DoesNotContain(visual.Key, b.Conn.Monsters.Keys);
            Assert.Contains(packets, p => p.SequenceEqual(InstanceEntities.RemoveBuff(Program.PlayerId,
                (ushort)(shown[12] << 8 | shown[13]))));
            b.Conn.PublishedMinions = [];
            await Game.SyncPeerMinions("mirage-observer", observerWire.Stream, observerWire.Ciphers, observer);
            Assert.Empty(observer.SeenMinions);
            uint expired = b.Enemy.Life;
            b.Session.AdvanceTo(ProjectileRuntime.Time(9));
            Assert.Equal(expired, b.Enemy.Life);
            Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
            b.Session.Schedule(ProjectileRuntime.Time(9), () => true, () => use);
            b.Session.AdvanceTo(ProjectileRuntime.Time(9.4));
            await b.FlushOutput();
            Assert.Single(b.Conn.MinionOrigins);
            b.Conn.PlayerLife = 0;
            b.Session.AdvanceTo(ProjectileRuntime.Time(12));
            await b.FlushOutput();
            Assert.Empty(b.Conn.MinionOrigins);
            b.Conn.PlayerLife = 1000;
            b.Session.Schedule(ProjectileRuntime.Time(12), () => true, () => use);
            b.Session.AdvanceTo(ProjectileRuntime.Time(12.4));
            await b.FlushOutput();
            var items = Game.ItemsOf(b.Conn);
            var bow = items.Items(PlayerInventories.Weapon1).Single();
            items.Change(PlayerInventories.Weapon1, bow.Id, item => item with { Links = [1, 1] });
            b.Session.AdvanceTo(ProjectileRuntime.Time(15));
            await b.FlushOutput();
            Assert.Empty(b.Conn.MinionOrigins);
            items.Change(PlayerInventories.Weapon1, bow.Id, item => item with { Links = [2] });
            b.Session.Schedule(ProjectileRuntime.Time(15), () => true, () => use);
            b.Session.AdvanceTo(ProjectileRuntime.Time(15.4));
            await b.FlushOutput();
            uint retiring = Assert.Single(b.Conn.MinionOrigins).Key;
            b.Session.Invalidate();
            Assert.Empty(b.Conn.MinionOrigins);
            Assert.Contains(retiring, b.Conn.RetiredCombatObjects);
            Assert.Contains(b.Conn.RetiredCombatBuffs, p => p.Actor == Program.PlayerId);
        }
        finally { Program.Outgoing = outgoing; }
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
    [InlineData("Frenzy", 11610, 11600, 17410)]
    [InlineData("Tornado Shot", 10110, 10100, 15160)]
    [InlineData("Rain of Arrows", 4860, 4900, 7310)]
    [InlineData("Blast Rain", 3030, 3000, 4530)]
    [InlineData("Toxic Rain", 5120, 5100, 7670)]
    [InlineData("Split Arrow", 12120, 12120, 18180)]
    public async Task Attack_coefficients_separate_weapon_base_from_added_damage(string skill, int basis, int added, double expected)
    {
        using var b = new Battle(skill);
        var items = Game.ItemsOf(b.Conn);
        var host = items.Items(PlayerInventories.Weapon1).Single();
        items.Change(PlayerInventories.Weapon1, host.Id, item => item with
        {
            Sockets = [item.Sockets[0] with { Gem = item.Sockets[0].Gem! with
            { GemExperience = Program.Items.Gem(Program.Items.ByHash(item.Sockets[0].Gem!.TypeHash)!)!.ExperienceFor(2) } }]
        });
        var gem = Game.GemSkillEffects(b.Conn).Single();
        Assert.Equal(2, gem.Level);
        foreach (string bound in new[] { "minimum", "maximum" })
            b.Conn.PlayerStats[Program.Stats.Key($"attack_{bound}_added_physical_damage")!.Value] = 5000;
        var weapon = new SkillCatalog.Unarmed(1000, 10000, 10000, 80) { Bow = true };
        // Independently calculated from the level-2 table offsets: e.g. Frenzy 1610 / 1600 gives
        // 10000 * 1.161 + 5000 * 1.16 = 17410, rather than scaling the whole 15000 by 1.16.
        var inputs = Game.SupportedHitInputsFor(b.Conn, gem.Id, weapon: weapon)!;
        Assert.Equal(expected, inputs.Raw[DamageType.Physical].Min, 6);
        Assert.Equal(expected, inputs.Raw[DamageType.Physical].Max, 6);
        Assert.Equal((basis, added), Game.SkillsFor(b.Conn).DamageCoefficients(gem.Effect, gem.Level));
        Assert.Equal((10000, 10000), Game.SkillsFor(b.Conn).DamageCoefficients(-1, 2));
        int tornado = Game.SkillsFor(b.Conn).EffectRow("TornadoShot");
        Assert.Equal((10000, 10000), Game.SkillsFor(b.Conn).DamageCoefficients(tornado, 1));

        var additions = new Dictionary<string, int>
        {
            ["attack_minimum_added_physical_damage"] = 5000, ["attack_maximum_added_physical_damage"] = 5000,
            ["global_minimum_added_cold_damage"] = 300, ["global_maximum_added_cold_damage"] = 300,
            ["attack_minimum_added_fire_damage_with_bow"] = 400, ["attack_maximum_added_fire_damage_with_bow"] = 400,
            ["attack_minimum_added_fire_damage_with_bows"] = 300, ["attack_maximum_added_fire_damage_with_bows"] = 300,
            ["maximum_mana"] = 1000, [DamageStats.MaxManaAddedLightning] = 6,
            ["maximum_life"] = 1000, ["added_fire_damage_to_attacks_equal_to_%_maximum_life"] = 21,
        };
        int Get(string name) => additions.GetValueOrDefault(name);
        var local = DamagePacket.Of(DamageType.Physical, 10000, 10000).Add(DamageType.Cold, new(1000, 1000));
        foreach (var localWeapon in new[]
        {
            weapon with { LocalDamage = local, LocalAdded = DamagePacket.Of(DamageType.Cold, 9999, 9999) },
            weapon with { LocalAdded = DamagePacket.Of(DamageType.Cold, 1000, 1000) },
        })
        {
            var packet = SupportedAttackDamage.Raw(localWeapon, Get, new(0), basis, added);
            Assert.Equal(expected, packet[DamageType.Physical].Min, 6);
            Assert.Equal((1000L * basis + 300L * added) / 10000d, packet[DamageType.Cold].Min, 6);
            Assert.Equal(910L * added / 10000d, packet[DamageType.Fire].Min, 6);
            Assert.Equal(60L * added / 10000d, packet[DamageType.Lightning].Min, 6);
            var ordinary = SupportedAttackDamage.Raw(localWeapon, Get, new(0));
            Assert.Equal(15000, ordinary[DamageType.Physical].Min);
            Assert.Equal(1300, ordinary[DamageType.Cold].Min);
            Assert.Equal(5000L * added / 10000d,
                SupportedAttackDamage.Raw(localWeapon, Get, new(0), 0, added)[DamageType.Physical].Min, 6);
        }
        // Attack coefficients never scale a spell's own base, and attack/pool/bow additions stay out of spells.
        var spell = SpellDamage.Packet(DamagePacket.Of(DamageType.Cold, 100, 100), Get, addedEffectiveness: 3);
        Assert.Equal(1000, spell[DamageType.Cold].Min);
        Assert.Equal(0, spell[DamageType.Physical].Min);
        Assert.Equal(0, spell[DamageType.Fire].Min);
        Assert.Equal(0, spell[DamageType.Lightning].Min);

        if (skill != "Rain of Arrows") return;
        // An equipped local affix builds a fixed 10000 physical weapon through ItemWeapons, not a substituted hit.
        var plain = Game.WeaponFor(b.Conn);
        var affix = Program.Items.Mods.ById("LocalAddedPhysicalDamage1")!;
        items.Change(PlayerInventories.Weapon1, host.Id, item => item with
        { Mods = [new ItemMod(ModSection.Explicit, affix.Hash, [10000 - plain.MinDamage, 10000 - plain.MaxDamage])] });
        Assert.Equal(10000, Game.WeaponFor(b.Conn).LocalDamage![DamageType.Physical].Min);
        Assert.Equal(10000, Game.WeaponFor(b.Conn).LocalDamage![DamageType.Physical].Max);
        b.Conn.PlayerStats[Program.Stats.Key("global_cannot_crit")!.Value] = 1;
        b.Conn.PlayerStats[Program.Stats.Key("area_damage_+%")!.Value] = 100;
        b.Conn.PlayerStats[Program.Stats.Key("number_of_additional_projectiles")!.Value] = -1000;
        var use = Game.CaptureGemProjectile(b.Conn, b.Session, gem.Id, gem, (120, 100), new(0));
        var shot = Assert.IsType<ProjectileOffense>(use.Payload);
        Assert.Equal(7310, shot.Hit.MinTotal, 6);
        Assert.Equal(7310, shot.Hit.MaxTotal, 6);
        Assert.Equal(14620, shot.AreaHit!.MinTotal, 6);
        var panel = Game.PlayerSkills(b.Conn).Single(s => s.Id == gem.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal(14620, panel[Program.Stats.Key("main_hand_minimum_physical_damage")!.Value]);
        Assert.Equal(14620, panel[Program.Stats.Key("main_hand_maximum_physical_damage")!.Value]);
        Assert.Equal(1462000, panel[Program.Stats.Key("display_hundred_times_damage_per_skill_use")!.Value]);
        Game.ReleaseArrowRain(b.Session, gem.Id, gem, (120, 100), new(0), 0);
        foreach (string bound in new[] { "minimum", "maximum" })
            b.Conn.PlayerStats[Program.Stats.Key($"attack_{bound}_added_physical_damage")!.Value] = 0;
        items.Change(PlayerInventories.Weapon1, host.Id, item => item with { Mods = [] });
        Assert.True(Game.SupportedHitFor(b.Conn, gem.Id, suppress: true).MaxTotal < 14620);
        b.Session.AdvanceTo(ProjectileRuntime.Time(.4));
        Assert.Equal(100000u - 14620, b.Enemy.Life);
        Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
        Weapon_cold_per_dexterity_stays_with_its_source();
        Spellblade_takes_weapon_damage_without_global_attack_inputs();
        Spellblade_requires_an_active_one_hand_melee_weapon();
        Spellblade_transfers_each_active_weapon_before_spell_scaling();
        Accuracy_boots_use_resolved_skill_and_hand_inputs();
        Fan_of_Blades_reaches_projectile_count_and_attack_damage();
        Different_weapon_mastery_reaches_shared_damage();
        Physical_dot_taken_combines_real_curse_and_debuff_increases();
        Maim_settles_elapsed_bleeding_before_changing_target_damage();
        Monster_elemental_modifiers_reach_damage_over_time();
        Righteous_fire_uses_player_damage_modifiers();
        await Righteous_fire_uses_its_current_burning_damage_support();
        await Righteous_fire_combines_its_admitted_supports();
        await Righteous_fire_settles_its_timed_supports_between_skill_payments();
        Claw_and_dagger_projectiles_keep_the_released_hand_and_source();
    }

    static void Maim_settles_elapsed_bleeding_before_changing_target_damage()
    {
        static uint Key(string name) => Program.Stats.Key(name)!.Value;
        foreach (bool projectile in new[] { false, true })
        foreach (bool previousMaim in new[] { false, true })
        {
            using var b = new Battle("Rain of Arrows", "Maim Support");
            var c = b.Conn;
            var m = b.Enemy;
            var gem = Game.GemSkillEffects(c).Single();
            var own = Game.SupportedSkill(c, gem.Id);
            Assert.True(own.RequirementsMet);
            Assert.Equal(30, own.Stats[Key("maim_on_hit_%")]);
            Assert.Equal(10, own.Stats[Key("support_maimed_enemies_physical_damage_taken_+%")]);
            void Apply(double at) => Game.HitDebuffsOnHitLocked(m, n => n == "maim_on_hit_%" ? 100
                : Program.Stats.Key(n) is uint key ? own.Stats.GetValueOrDefault(key) : 0,
                spell: false, critical: false, now: at);
            if (previousMaim) Apply(0); // Its expiry coincides with Rupture's at t=4.
            int dps = projectile ? 101 : 100; // Keep the fractional landing away from an integer damage boundary.
            Assert.True(m.Bleeds.Add(1, dps, 10)); // Controlled final DPS isolates target changes.
            m.Ruptures.Add(1, 25);
            m.Bleeds.SetExpiryRate(1, Game.BleedingExpiryRate(m, 1));
            double contact = 5;
            if (projectile)
            {
                // The native support supplies30; guarantee only the chance, retaining its real target10.
                c.PlayerStats[Key("maim_on_hit_%")] = 70;
                c.PlayerStats[Key("global_cannot_crit")] = 1;
                b.Session.AdvanceTo(ProjectileRuntime.Time(5));
                Game.ReleaseArrowRain(b.Session, gem.Id, gem, (120, 100), new(0), 5);
                b.Session.AdvanceTo(ProjectileRuntime.Time(5.35)); // Only the first, centered landing.
                Assert.True(m.Life < m.MaxLife);
                contact = m.MaimedUntil - Game.MaimSeconds;
                Assert.Equal(5.35, contact, 6);
                Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
                Assert.Equal(double.NegativeInfinity, b.Ally.MaimedUntil);
            }
            else
            {
                Apply(contact);
                Assert.Null(Game.BleedOnHitLocked(c, 200, m, new(0, 0, 0, 0, 0, 0), critical: false, now: contact));
            }
            Assert.Equal(10, Game.MaimedPhysicalTaken(m, contact));
            Assert.Empty(m.Ruptures.Active);
            // Only the old Maim can affect the three ruptured seconds; t=4 to contact has no Maim.
            double earned = 3 * dps * 1.25 * (previousMaim ? 1.1 : 1) + (contact - 4) * dps;
            Assert.Equal((int)Math.Floor(earned), m.Bleeds.Advance(contact, 1)); // First control: 475, not 522.

            // The new Maim still increases subsequent bleed damage, through the normal life-update tick.
            const double nextTick = 6.125;
            b.Session.AdvanceTo(ProjectileRuntime.Time(nextTick));
            uint before = m.Life, playerLife = c.PlayerLife;
            Game.BleedMonsters("maim-bleed-order", b.Stream, b.Ciphers, c).GetAwaiter().GetResult();
            Assert.Equal((uint)(Math.Floor(earned + (nextTick - contact) * dps * 1.1) - Math.Floor(earned)), before - m.Life);
            Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
            Assert.Equal(playerLife, c.PlayerLife);
        }
    }

    static void Physical_dot_taken_combines_real_curse_and_debuff_increases()
    {
        static uint Key(string name) => Program.Stats.Key(name)!.Value;
        using var b = new Battle("Vulnerability", "Puncture", "Maim Support");
        var c = b.Conn;
        var m = b.Enemy;
        var gem = Game.GemSkillEffects(c).Single(g => g.Name == "Vulnerability");
        var own = Game.SupportedSkill(c, gem.Id);
        Assert.True(own.RequirementsMet);
        Assert.Equal(15, own.Stats[Key("physical_damage_taken_+%")]);
        Assert.False(Game.ApplyCurseLocked(c, b.Ally, gem.Id, 0, aura: false));
        Assert.True(Game.ApplyCurseLocked(c, m, gem.Id, 0, aura: false));
        Assert.Equal(15, m.Effects["physical_damage_taken_+%"]);
        Assert.Equal(0, m.Mods.Effects["physical_damage_taken_+%"]);
        var curse = Assert.Single(m.Curses.All.Values);
        Assert.Equal((ushort)79, curse.Row);
        Assert.Equal(8, curse.Until);
        m.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Shock, 4, 0, _ => 0, _ => 0, stated: 20);
        Assert.Equal(20, m.Ailments.Magnitude(MonsterElementalAilments.Kind.Shock, 1));
        Assert.True(m.Bleeds.Add(0, 100, 5)); // Controlled final DPS isolates target scaling from attack rolls.
        void Tick(double to, uint expected)
        {
            uint before = m.Life, playerLife = c.PlayerLife;
            b.Session.AdvanceTo(ProjectileRuntime.Time(to));
            Game.BleedMonsters("physical-dot", b.Stream, b.Ciphers, c).GetAwaiter().GetResult();
            Assert.Equal(expected, before - m.Life);
            Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
            Assert.Equal(playerLife, c.PlayerLife);
        }
        Tick(1, 135); // Before the fix this actual bleed tick dealt 138.
        m.Curses.Remove(c.PeerId, "Vulnerability");
        Assert.Equal(0, m.Effects["physical_damage_taken_+%"]);
        Tick(1.5, 60);
        Assert.True(Game.ApplyCurseLocked(c, m, gem.Id, 1.5, aura: false));
        Assert.Equal(15, m.Effects["physical_damage_taken_+%"]);

        var attack = Game.GemSkillEffects(c).Single(g => g.Name == "Puncture");
        var supported = Game.SupportedSkill(c, attack.Id);
        Assert.Equal(30, supported.Stats[Key("maim_on_hit_%")]);
        Assert.Equal(10, supported.Stats[Key("support_maimed_enemies_physical_damage_taken_+%")]);
        Assert.Equal(10, supported.Stats[Key("support_maim_chance_physical_damage_+%_final")]);
        // Guarantee only the chance; the admitted support supplies the target magnitude through the real hit handler.
        Game.HitDebuffsOnHitLocked(m, n => n == "maim_on_hit_%" ? 100
            : Program.Stats.Key(n) is uint key ? supported.Stats.GetValueOrDefault(key) : 0,
            spell: false, critical: false, now: 1.5);
        Assert.Equal(10, m.MaimedPhysicalTakenPct);
        Assert.Equal(5.5, m.MaimedUntil);
        Tick(2.5, 145); // Curse15 + shock20 + Maim10; support's source-side MORE is not another target factor.
        foreach (var type in new[] { DamageType.Fire, DamageType.Cold, DamageType.Lightning, DamageType.Chaos })
            Assert.Equal(1.2, Game.MonsterDotTaken(m, type, 2.5), 6);
        void Factor(double at, double expected) => Assert.Equal(expected, Game.MonsterDotTaken(m, DamageType.Physical, at), 6);
        m.PridePhysicalMore = 25;
        Factor(2.5, 1.8125); // (1 + .15 + .2 + .1) * 1.25, not an increased-taken contribution.
        m.TakenFromPlayer = (All: 0, Elemental: 100, Physical: 50);
        m.IntimidatedUntil = m.UnnervedUntil = 20;
        m.EncounterStats = new Dictionary<string, int> { ["rare_monster_mod_spell_damage_taken_+%_final"] = -90 };
        Factor(2.5, 1.8125); // Elemental auras, Flesh and Stone and the hit-only debuffs do not alter physical DoT.
        Factor(4, 1.5625); // Shock expired, Maim and the curse remain.
        Factor(5.5, 1.4375); // Maim expired; the curse remains.
        m.Curses.Expire(9.5);
        Assert.Empty(m.Curses.All);
        Factor(9.5, 1.25);
        m.PridePhysicalMore = 0;
        m.TakenFromPlayer = default;
        m.EncounterStats = null;

        // New applications replace warm recipient stats and honor actual curse-effect scaling.
        c.PlayerStats[Key("curse_effect_+%")] = 50;
        Assert.True(Game.ApplyCurseLocked(c, m, gem.Id, 10, aura: false));
        Assert.Equal(22, m.Effects["physical_damage_taken_+%"]); // Truncated once by the existing curse consumer.
        m.TakenFromPlayer = (All: 20, Elemental: 0, Physical: 0);
        Factor(10, 1.42);
        m.Mods = new([], [], new MonsterModEffects(new Dictionary<string, int> { ["curse_effect_on_self_+%"] = -50 }));
        Assert.True(Game.ApplyCurseLocked(c, m, gem.Id, 10, aura: false));
        Assert.Equal(15, m.Effects["physical_damage_taken_+%"]);
        Factor(10, 1.35);
        c.PlayerStats.Remove(Key("curse_effect_+%"));
        Assert.True(Game.ApplyCurseLocked(c, m, gem.Id, 10, aura: false));
        Assert.Equal(7, m.Effects["physical_damage_taken_+%"]);
        Factor(10, 1.27);
        m.Curses.Clear();
        m.Mods = new([], [], new MonsterModEffects(new Dictionary<string, int> { ["hexproof"] = 1 }));
        Assert.False(Game.ApplyCurseLocked(c, m, gem.Id, 10, aura: false));
        Factor(10, 1.2);
        m.Mods = MonsterModSet.None;
        m.TakenFromPlayer = default;

        void Taken(params (string Name, int Value)[] stats) =>
            m.EncounterStats = stats.ToDictionary(s => s.Name, s => s.Value);
        Taken(("base_damage_taken_+%", 50), ("physical_damage_taken_+%", -100));
        Factor(10, .5); // Clamp the combined bucket, not the typed contribution first.
        Taken(("base_damage_taken_+%", 20), ("physical_damage_taken_+%", -150));
        Factor(10, 0);
        Assert.True(Game.TryMaimOnHitLocked(m, 100, 25, 10, new Random(17)));
        Taken(("base_damage_taken_+%", 20), ("physical_damage_taken_+%", 15));
        Factor(10, 1.6);
        Factor(14, 1.35);
        Taken(("base_damage_taken_+%", 20), ("physical_damage_taken_+%", 15),
            ("map_monster_damage_taken_+%_final_from_atlas_keystone", -40));
        Factor(14, .81);
        Taken(("physical_damage_taken_+%", 500), ("base_cannot_be_damaged", 1));
        Factor(14, 0);
    }

    static void Monster_elemental_modifiers_reach_damage_over_time()
    {
        // Precompiled Mods bind to this exact public signature, not to optional-argument source syntax.
        Assert.NotNull(typeof(Game).GetMethod(nameof(Game.MonsterDamageTaken),
            [typeof(SpawnedMonster), typeof(bool), typeof(double), typeof(bool), typeof(bool), typeof(bool)]));
        using var b = new Battle("Righteous Fire");
        var c = b.Conn;
        var m = b.Enemy;
        c.Pools = Game.PoolsFor(c, 100);
        m.Walk.Teleport(110, 100, 0);
        b.Ally.Walk.Teleport(110, 100, 0);
        var fire = IzaroEffects.Of(IzaroStatues.Mechanic, 1, returning: false).Single(e => e.Element == 0);
        Assert.Equal("ToggleStatuesNormal_Fire", fire.Id);
        Assert.Equal((ushort)559, fire.Buff);
        Assert.Equal(-20, fire.Stats["fire_damage_taken_+%"]);
        m.EncounterStats = IzaroEffects.Sum([fire]);
        Assert.Equal(-20, m.Effects["fire_damage_taken_+%"]);
        var gem = Assert.Single(Game.GemSkillEffects(c));
        Game.ActivateRighteousFire("elemental-dot", b.Stream, b.Ciphers, c, gem.Id,
            gem.Effect, gem.Level, gem.Quality, gem.Gem).GetAwaiter().GetResult();
        var rf = Assert.IsType<RighteousFireState>(c.RighteousFire);
        b.AdvanceBy(1);
        double baseDps = (c.PlayerMaxLife + c.Pools.EnergyShield) * .7;
        void Burn(double factor)
        {
            m.Life = m.MaxLife;
            m.Burn = 0;
            uint playerLife = c.PlayerLife;
            Game.BurnAround("elemental-dot", b.Stream, b.Ciphers, c, rf, .5).GetAwaiter().GetResult();
            Assert.Equal((int)Math.Floor(baseDps * factor * .5), (int)(m.MaxLife - m.Life));
            Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
            Assert.Equal(playerLife, c.PlayerLife);
            Assert.DoesNotContain(201u, rf.Auras.Keys);
        }
        Burn(.8); // Real Normal statue: the original failure was 435 instead of 348.
        m.EncounterStats = null;
        Burn(1);
        m.EncounterStats = IzaroEffects.Sum([fire]);
        Burn(.8);

        DamageType[] elements = [DamageType.Fire, DamageType.Cold, DamageType.Lightning];
        string[] names = ["fire", "cold", "lightning"];
        foreach (int tier in new[] { 1, 2, 3, 4 })
        foreach (bool returning in new[] { false, true })
        foreach (var effect in IzaroEffects.Of(IzaroStatues.Mechanic, tier, returning))
        {
            Assert.Equal(-20, effect.Stats[names[effect.Element] + "_damage_taken_+%"]);
            m.EncounterStats = IzaroEffects.Sum([effect]);
            foreach (var type in elements)
                Assert.Equal(type == elements[effect.Element] ? .8 : 1, Game.MonsterDotTaken(m, type), 6);
            Assert.Equal(1, Game.MonsterDotTaken(m, DamageType.Physical));
            Assert.Equal(1, Game.MonsterDotTaken(m, DamageType.Chaos));
            Burn(effect.Element == 0 ? .8 : 1);
        }

        void Taken(params (string Name, int Value)[] stats) =>
            m.EncounterStats = stats.ToDictionary(s => s.Name, s => s.Value);
        // Typed and general increases share one bucket, including signed reductions and the final zero clamp.
        Taken(("base_damage_taken_+%", 30), ("fire_damage_taken_+%", 20),
            ("cold_damage_taken_+%", -10), ("lightning_damage_taken_+%", -50));
        Assert.Equal(1.5, Game.MonsterDotTaken(m, DamageType.Fire), 6);
        Assert.Equal(1.2, Game.MonsterDotTaken(m, DamageType.Cold), 6);
        Assert.Equal(.8, Game.MonsterDotTaken(m, DamageType.Lightning), 6);
        Assert.Equal(1.3, Game.MonsterDotTaken(m, DamageType.Physical), 6);
        Assert.Equal(1.3, Game.MonsterDotTaken(m, DamageType.Chaos), 6);
        Burn(1.5);
        Taken(("base_damage_taken_+%", 50), ("fire_damage_taken_+%", -100));
        Burn(.5);
        Taken(("base_damage_taken_+%", 20), ("fire_damage_taken_+%", -150));
        Burn(0);

        Taken(("base_damage_taken_+%", 20), ("fire_damage_taken_+%", -20));
        m.TakenFromPlayer = (All: 10, Elemental: 30, Physical: 0);
        Assert.Equal(1.4, Game.MonsterDotTaken(m, DamageType.Fire, 0), 6);
        m.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Shock, 4, 0, _ => 0, _ => 0, stated: 20);
        Assert.Equal(1.6, Game.MonsterDotTaken(m, DamageType.Fire, 0), 6);
        Assert.Equal(1.4, Game.MonsterDotTaken(m, DamageType.Fire, 4), 6);
        m.TakenFromPlayer = default;
        // Use a time after the controlled shock expires for the remaining factor checks.
        void Factor(double expected) => Assert.Equal(expected, Game.MonsterDotTaken(m, DamageType.Fire, 4), 6);
        Taken(("fire_damage_taken_+%", -20), ("base_fire_damage_resistance_%", 50));
        Factor(.4);
        Taken(("fire_damage_taken_+%", -20), ("base_fire_damage_resistance_%", 200));
        Factor(.2);
        Taken(("fire_damage_taken_+%", -20), ("base_fire_damage_resistance_%", 200),
            ("additional_maximum_all_elemental_resistances_%", 10));
        Factor(.12);
        Taken(("fire_damage_taken_+%", -20), ("base_fire_damage_resistance_%", -20));
        Factor(.96);
        Taken(("fire_damage_taken_+%", -20), ("rare_monster_mod_spell_damage_taken_+%_final", -90));
        Factor(.8); // RF and ignites are not spell hits.
        Taken(("fire_damage_taken_+%", -20), ("map_monster_damage_taken_+%_final_from_atlas_keystone", -40));
        Factor(.48);
        Taken(("fire_damage_taken_+%", 500), ("base_cannot_be_damaged", 1));
        Factor(0);

        // The shared consumer also feeds ignites; changing the target does not replace the applied ignite.
        m.EncounterStats = IzaroEffects.Sum([fire]);
        Assert.True(m.Ignites.Add(4, 100, 4));
        Assert.Equal(80, m.Ignites.Advance(5, Game.MonsterDotTaken(m, DamageType.Fire, 5)));
        m.EncounterStats = null;
        Assert.Equal(100, m.Ignites.Advance(6, Game.MonsterDotTaken(m, DamageType.Fire, 6)));
        m.EncounterStats = IzaroEffects.Sum([fire]);
        Assert.Equal(80, m.Ignites.Advance(7, Game.MonsterDotTaken(m, DamageType.Fire, 7)));
    }

    sealed class ProjectileClock : IPlayerUpdateSchedule
    {
        public readonly Queue<Func<Task>> Steps = [];
        public void OnWait(Action step) => Steps.Enqueue(() =>
        {
            step();
            return Task.CompletedTask;
        });
        public double ResourceLag;
        public double Now(ClientState conn) => Game.ProjectileWorldTime(conn);
        public double ResourceNow(ClientState conn) => Now(conn) - ResourceLag;
        public Task Wait(int milliseconds, CancellationToken stopping) => Steps.Dequeue()();
    }

    static async Task SwapRighteousFireSocket(Battle b, uint host, int index, string name, byte quality = 0)
    {
        var items = Game.ItemsOf(b.Conn);
        var gem = Program.Items.FindGem(name)!.Value;
        items.Add(PlayerInventories.Cursor, 0, 0, ItemCatalog.Create(gem.Type) with { GemVariant = gem.Variant.Index, Quality = quality });
        await Game.SocketGem("rf-links", b.Stream, b.Ciphers, b.Conn,
            new(PlayerInventories.InventoryId(PlayerInventories.Weapon1), host, (uint)index));
        items.Remove(PlayerInventories.Cursor, Assert.Single(items.Items(PlayerInventories.Cursor)).Id);
    }

    static async Task Righteous_fire_settles_its_timed_supports_between_skill_payments()
    {
        using var b = new Battle("Righteous Fire", "Lifetap Support", "Inspiration Support", "Tornado Shot", "Lifetap Support");
        var c = b.Conn;
        var items = Game.ItemsOf(c);
        var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
        items.Change(PlayerInventories.Weapon1, host.Id, item => item with { ItemLevel = 80, Links = [3, 2] });
        c.Pools = Game.PoolsFor(c, 100);
        b.Enemy.Walk.Teleport(105, 100, 0);
        b.Ally.Walk.Teleport(105, 100, 0);
        var gems = Game.GemSkillEffects(c);
        var gem = gems.Single(g => g.Name == "Righteous Fire");
        var payer = gems.Single(g => g.Name == "Tornado Shot");
        var own = Game.SupportedSkill(c, gem.Id).Stats;
        Assert.Equal(10, own[Program.Stats.Key(Lifetap.DamageStat)!.Value]);
        Assert.Equal(3, own[Program.Stats.Key("elemental_damage_+%_final_per_righteous_charge")!.Value]);
        c.Clock.Start();
        await Game.OnUseObject("rf-time", b.Stream, b.Ciphers, c, new(Program.PlayerId, gem.Id, 1, 0), c.Clock.Elapsed.TotalSeconds);
        c.Clock.Stop();
        var rf = Assert.IsType<RighteousFireState>(c.RighteousFire);
        double origin = Math.Ceiling(Math.Max(c.Clock.Elapsed.TotalSeconds, Game.ProjectileWorldTime(c))) + 1;
        void At(double time) => b.Session.AdvanceTo(ProjectileRuntime.Time(origin + time));
        void Pay(uint mana, uint life)
        {
            Assert.True(Game.PayMana(c, payer.Id, out uint paidMana, out uint paidLife));
            Assert.Equal((mana, life), (paidMana, paidLife));
        }
        async Task Tick(double end, double factor)
        {
            At(end);
            await AssertRighteousFireTick(b, rf, factor);
        }
        Task Swap(int slot, string name) => SwapRighteousFireSocket(b, host.Id, slot, name);
        var previous = Game.AreaUpdateContext.Value;
        var clock = new ProjectileClock();
        var updates = new PlayerUpdatesState(Game.CombatLifetime!, clock);
        Game.AreaUpdateContext.Value = updates;
        try
        {
            await Tick(1, 1);
            At(2);
            Pay(0, 27);
            Assert.Equal(origin + 6, c.Lifetap.Until);
            await Tick(2.25, 1.05); // First half of this interval preceded the life payment.
            await Tick(2.75, 1.1);

            await Swap(4, "Inspiration Support");
            At(3.125);
            Pay(8, 0);
            At(3.375);
            Pay(8, 0);
            Assert.Equal(2, c.SupportGems.InspirationCharges);
            At(3.4375);
            Assert.True(await Game.RelightRighteousFire("rf-time", b.Stream, b.Ciphers, c, "skill art", rf, gem.Gem));
            rf = Assert.IsType<RighteousFireState>(c.RighteousFire);
            await Tick(3.5, 1.1 * (.25 + .5 * 1.03 + .25 * 1.06));

            At(5);
            await Swap(4, "Lifetap Support");
            Pay(0, 27);
            Assert.Equal(origin + 9, c.Lifetap.Until);
            await Tick(6.25, 1.1 * 1.06); // A refresh spans the former deadline.
            await Tick(9.25, 1.05 * 1.06);
            await Tick(10, 1.06);

            await Swap(4, "Inspiration Support");
            for (int i = 0; i < 13; i++) Pay(8, 0);
            Assert.Equal((5, 120L), (c.SupportGems.InspirationCharges, c.SupportGems.InspirationSpent));
            At(10.25);
            Pay(8, 0); // The 122-Mana threshold clears all five charges.
            Assert.Equal((0, 0L), (c.SupportGems.InspirationCharges, c.SupportGems.InspirationSpent));
            await Tick(10.5, 1.075);

            At(11);
            Pay(8, 0);
            At(11.25);
            await Swap(2, "Arcane Surge Support");
            await Tick(11.5, 1.015);
            At(11.75);
            await Swap(2, "Inspiration Support");
            await Tick(12, 1.015);
            Assert.Same(rf, c.RighteousFire);

            At(21.25);
            c.PlayerSent = true;
            await Game.SendSupportGemBuffs("rf-time", b.Stream, b.Ciphers, c);
            Assert.Equal(0, c.SupportGems.InspirationCharges);
            await Tick(21.25, 1.015); // The player tick published expiry before settling damage.

            At(22);
            Pay(8, 0);
            At(32.25);
            Pay(8, 0); // No presentation tick since the previous charges expired.
            Assert.Equal(1, c.SupportGems.InspirationCharges);
            await Tick(32.5, 1.015);

            At(33);
            var retired = rf;
            await Swap(0, "Righteous Fire");
            Assert.Null(c.RighteousFire);
            gem = Game.GemSkillEffects(c).Single(g => g.Name == "Righteous Fire");
            await Game.ActivateRighteousFire("rf-time", b.Stream, b.Ciphers, c, gem.Id, gem.Effect, gem.Level, gem.Quality, gem.Gem);
            rf = Assert.IsType<RighteousFireState>(c.RighteousFire);
            uint beforeStale = b.Enemy.Life;
            await Game.BurnAround("rf-time", b.Stream, b.Ciphers, c, retired, .5);
            Assert.Equal(beforeStale, b.Enemy.Life);
            Assert.Same(rf, c.RighteousFire);
            await Tick(33.25, .5 * 1.03); // Only the new source's quarter second, with its existing Inspiration charge.
            await Tick(33.75, 1.03);

            // A payment arrives after the player tick sampled [43, 43.5] but before its queued publications finish.
            At(43);
            clock.OnWait(() => At(43.5));
            clock.OnWait(updates.Stop.Cancel);
            uint laterMana = 0;
            using var paidDuringTick = GameEvents.Tick.Add("Game", (actor, elapsed) =>
            {
                At(43.75);
                Game.PayMana(actor, payer.Id, out laterMana, out _);
                At(44);
                return Task.CompletedTask;
            });
            c.Wire = ("rf-time", b.Stream, b.Ciphers);
            c.PlayerUpdatesState = updates;
            Assert.True(c.PlayerUpdatesWorker.TryStart(c, updates.Rules, null, updates, out updates.Generation));
            b.Enemy.Burn = 0;
            uint beforeTick = b.Enemy.Life;
            c.Clock.Start();
            await Game.RunPlayerTick("rf-time", b.Stream, b.Ciphers, c, updates);
            c.Clock.Stop();
            Assert.Empty(clock.Steps);
            Assert.Equal(8u, laterMana);
            Assert.Equal(RighteousFireHalfSecondDamage(c, rf, 1), (int)(beforeTick - b.Enemy.Life));
            await Tick(44.25, 1.03);
        }
        finally
        {
            Game.AreaUpdateContext.Value = previous;
            if (ReferenceEquals(c.PlayerUpdatesState, updates)) c.RetirePlayerUpdates();
            updates.Stop.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Righteous_fire_burns_only_between_activation_and_source_retirement(bool recovery)
    {
        await RighteousFireSocketIntervals(recovery);
        foreach (string move in new[] { "pickup", "place", "onto", "weapon swap", "promotion", "stash", "vendor",
            "loses qualification", "cascading qualification", "keeps qualification", "gem qualification" })
        {
            var (atWrite, atTick, running, lease, rate) = await RighteousFireHostInterval(recovery, move);
            bool retained = move is "promotion" or "keeps qualification";
            Assert.Equal((move, (int)(rate * (retained ? .5 : .75)), (int)(rate * (retained ? 1 : .75)), retained, retained),
                (move, atWrite, atTick, running, lease));
        }
    }

    static async Task RighteousFireSocketIntervals(bool recovery)
    {
        using var b = new Battle("Righteous Fire", "Faster Attacks Support");
        var c = b.Conn;
        c.Pools = Game.PoolsFor(c, 100);
        c.PlayerLife = c.PlayerMaxLife;
        Assert.Equal(0u, c.Pools.EnergyShield);
        b.Enemy.Walk.Teleport(105, 100, 0);
        var gem = Assert.Single(Game.GemSkillEffects(c));
        var items = Game.ItemsOf(c);
        var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
        items.Change(PlayerInventories.Weapon1, host.Id, item => item with { ItemLevel = 80 });
        var socket = new InstanceItems.SocketGem(PlayerInventories.InventoryId(PlayerInventories.Weapon1), host.Id, 0);
        var clock = new ProjectileClock();
        var updates = new PlayerUpdatesState(Game.CombatLifetime!, clock);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = updates;
        try
        {
            double origin = Math.Ceiling(Math.Max(c.Clock.Elapsed.TotalSeconds, Game.ProjectileWorldTime(c))) + 1;
            void At(double time) => b.Session.AdvanceTo(ProjectileRuntime.Time(origin + time));
            Task Ignite() => Game.OnUseObject("rf-start", b.Stream, b.Ciphers, c,
                new(Program.PlayerId, gem.Id, 1, 0), c.Clock.Elapsed.TotalSeconds);
            uint startingLife = recovery ? c.PlayerLife : b.Enemy.Life;
            var late = new SpawnedMonster("late target", new(105, 100, 0), 0, 0, 100000);
            async Task RemoveFirst()
            {
                // This monster enters just after the 1.5 sample, in time for the last 1/16 second.
                c.Monsters[202] = late;
                var doomed = new SpawnedMonster("former occupant", new(105, 100, 0), 0, 0, 1);
                if (!recovery)
                {
                    doomed.Contagion.Apply(clock.Now(c), new(5, 10, 18));
                    c.Monsters[203] = doomed;
                }
                At(1.5625);
                clock.ResourceLag = recovery ? .03125 : 0;
                uint lifeBeforeRemoval = c.PlayerLife;
                var old = Assert.IsType<RighteousFireState>(c.RighteousFire);
                var gate = Program.SendGates.GetValue(b.Ciphers, _ => new(1, 1));
                Assert.True(gate.Wait(0));
                Task replacing;
                try
                {
                    replacing = SwapRighteousFireSocket(b, host.Id, 0, "Righteous Fire");
                    Assert.False(replacing.IsCompleted);
                    Assert.Null(c.RighteousFire);
                    Assert.False(c.GemSourceLifetimes.IsCurrent(old.SourceLifetime!));
                    if (recovery) Assert.True(c.PlayerLife < lifeBeforeRemoval);
                    else
                    {
                        Assert.Equal((uint)(c.PlayerMaxLife * old.DamagePctPerMinute / 6000d * .0625), late.MaxLife - late.Life);
                        Assert.True(doomed.Dead);
                        c.Monsters[203] = new("replacement", new(200, 100, 0), 0, 0, 200);
                    }
                }
                finally { gate.Release(); }
                await replacing;
                if (recovery) c.EnergyShield.SetMaximum(125, refill: true);
                Assert.True(late.Contagion.Idle);
                clock.ResourceLag = 0;
                await Game.BurnAround("rf-start", b.Stream, b.Ciphers, c, old, .5);
                late.Walk.Teleport(200, 100, 0);
            }
            var losses = new List<int>();
            void Observe()
            {
                if (!recovery && losses.Count == 0)
                    Assert.DoesNotContain(200u, Assert.IsType<RighteousFireState>(c.RighteousFire).Auras.Keys);
                losses.Add((int)(startingLife - (recovery ? c.PlayerLife : b.Enemy.Life)));
            }

            // The player loop samples 0.5, then admits RF at 0.75 while publishing that tick.
            // Recovery samples under the actor lock and first sees the new owner at 1.0.
            At(0);
            bool activatedInPlayerTick = false;
            using var afterSample = GameEvents.Tick.Add("Game", async (actor, elapsed) =>
            {
                if (!ReferenceEquals(actor, c)) return;
                if (!activatedInPlayerTick)
                {
                    activatedInPlayerTick = true;
                    At(.75);
                    await Ignite();
                }
                else if (clock.Now(c) == origin + 1.5)
                {
                    Observe();
                    await RemoveFirst();
                }
            });
            clock.OnWait(() => At(.5));
            clock.Steps.Enqueue(async () =>
            {
                Observe();
                if (recovery)
                {
                    At(.75);
                    await Ignite();
                }
                At(1);
            });
            clock.Steps.Enqueue(async () =>
            {
                Observe();
                At(1.25);
                var original = Assert.IsType<RighteousFireState>(c.RighteousFire);
                var gate = Program.SendGates.GetValue(b.Ciphers, _ => new(1, 1));
                Assert.True(gate.Wait(0));
                Task<bool> restyle;
                RighteousFireState? duringOutput;
                bool waiting;
                try
                {
                    restyle = Game.RelightRighteousFire("rf-start", b.Stream, b.Ciphers, c,
                        "restyled by the skill pick", original, gem.Gem);
                    waiting = !restyle.IsCompleted;
                    duringOutput = c.RighteousFire;
                }
                finally { gate.Release(); }
                Assert.True(await restyle);
                Assert.True(waiting);
                var restyled = Assert.IsType<RighteousFireState>(c.RighteousFire);
                Assert.Same(duringOutput, restyled);
                Assert.NotSame(original, restyled);
                Assert.Same(original.SourceLifetime, restyled.SourceLifetime);
                At(1.5);
            });
            clock.Steps.Enqueue(async () =>
            {
                if (recovery) { Observe(); await RemoveFirst(); }
                At(1.625);
            });
            clock.Steps.Enqueue(async () =>
            {
                Observe();
                if (recovery)
                {
                    Assert.Equal(125u, c.EnergyShield.Current);
                    c.EnergyShield.SetMaximum(0);
                }
                await SwapRighteousFireSocket(b, host.Id, 1, "Burning Damage Support");
                await Game.ActivateRighteousFire("rf-start", b.Stream, b.Ciphers, c, gem.Id,
                    gem.Effect, gem.Level, gem.Quality, Assert.Single(Game.GemSkillEffects(c)).Gem);
                var linked = Assert.IsType<RighteousFireState>(c.RighteousFire);
                uint burning = Program.Stats.Key("support_burning_damage_+%_final")!.Value;
                Assert.Equal(20, Game.SupportedSkill(c, gem.Id).Stats[burning]);
                At(1.6875);
                var held = items.Add(PlayerInventories.Cursor, 0, 0,
                    Assert.Single(items.Items(PlayerInventories.Weapon1)).Item.Sockets[0].Gem!);
                await Game.UnsocketGem("rf-start", b.Stream, b.Ciphers, c, socket);
                Assert.Same(linked, c.RighteousFire);
                Assert.Equal(held.Id, Assert.Single(items.Items(PlayerInventories.Cursor)).Id);
                items.Remove(PlayerInventories.Cursor, held.Id);
                At(1.75);
                await Game.UnsocketGem("rf-start", b.Stream, b.Ciphers, c, socket);
                Assert.Null(c.RighteousFire);
                Assert.False(c.GemSourceLifetimes.IsCurrent(linked.SourceLifetime!));
                await Game.SocketGem("rf-start", b.Stream, b.Ciphers, c, socket);
                await SwapRighteousFireSocket(b, host.Id, 1, "Faster Attacks Support");
                await Game.ActivateRighteousFire("rf-start", b.Stream, b.Ciphers, c, gem.Id,
                    gem.Effect, gem.Level, gem.Quality, Assert.Single(Game.GemSkillEffects(c)).Gem);
                var unlinked = Assert.IsType<RighteousFireState>(c.RighteousFire);
                Assert.NotSame(linked.SourceLifetime, unlinked.SourceLifetime);
                Assert.DoesNotContain(burning, Game.SupportedSkill(c, gem.Id).Stats.Keys);
                At(1.8125);
            });
            clock.OnWait(() => { Observe(); updates.Stop.Cancel(); });
            c.Wire = ("rf-start", b.Stream, b.Ciphers);
            c.PlayerUpdatesState = updates;
            Assert.True(c.PlayerUpdatesWorker.TryStart(c, updates.Rules, null, updates, out updates.Generation));
            c.Clock.Start();
            if (recovery) await Game.RunResourceRecovery("rf-start", b.Stream, b.Ciphers, c, updates);
            else await Game.RunPlayerTick("rf-start", b.Stream, b.Ciphers, c, updates);
            var rf = Assert.IsType<RighteousFireState>(c.RighteousFire);
            Assert.Empty(clock.Steps);
            Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
            double rate = recovery
                ? DamageModel.Mitigate(c.PlayerMaxLife * rf.BurnPctPerMinute / 6000d,
                    Game.Resistance(c, DamageType.Fire), Game.MaxResistance(c, DamageType.Fire))
                : c.PlayerMaxLife * rf.DamagePctPerMinute / 6000d;
            double linkedRate = recovery ? rate : rate * 1.2;
            double firstBurn = recovery ? .78125 : .8125;
            Assert.Equal(new[] { 0, (int)(rate * .25), (int)(rate * .75), (int)(rate * firstBurn),
                (int)(rate * firstBurn + linkedRate * .125 + rate * .0625) }, losses);
        }
        finally
        {
            c.Clock.Stop();
            Game.AreaUpdateContext.Value = previous;
            if (ReferenceEquals(c.PlayerUpdatesState, updates)) c.RetirePlayerUpdates();
            updates.Stop.Dispose();
        }
    }

    static uint EquipRighteousFireAttributeItems(ClientState c, PlayerInventories items, ItemCatalog.BaseType wand, string move)
    {
        c.PlayerStats.Remove(Program.Stats.Key("virtual_intelligence_for_requirements")!.Value);
        var intelligence = Program.Items.Mods.ById("Intelligence8")!;
        int bare = Game.CurrentGemRequirements(c).Intelligence;
        Assert.InRange(Program.Items.Requirements(wand).Int, bare + 1, bare + intelligence.RangeAt(0).Max);

        InventoryItem Equip(byte row, string type, params string[] modifiers)
        {
            var affixes = modifiers.Select(id => Program.Items.Mods.ById(id)!)
                .Select(mod => new ItemMod(ModSection.Explicit, mod.Hash, [mod.RangeAt(0).Max])).ToArray();
            return items.Add(row, 0, 0, ItemCatalog.Create(Program.Items.Find(type)!) with
                { Rarity = affixes.Length == 1 ? ItemRarity.Magic : ItemRarity.Rare, Mods = affixes });
        }
        if (move == "cascading qualification")
        {
            c.PlayerStats.Remove(Program.Stats.Key("virtual_dexterity_for_requirements")!.Value);
            var boots = Program.Items.Find("Goathide Boots")!;
            int dex = Game.CurrentGemRequirements(c).Dexterity;
            Assert.InRange(Program.Items.Requirements(boots).Dex, dex + 1, dex + Program.Items.Mods.ById("Dexterity8")!.RangeAt(0).Max);
            Equip(PlayerInventories.Boots, boots.Name, "Intelligence8");
            return Equip(PlayerInventories.Gloves, "Iron Gauntlets", "Dexterity8").Id;
        }
        if (move == "keeps qualification")
        {
            Equip(PlayerInventories.Helmet, "Iron Hat", "Intelligence8");
            return Equip(PlayerInventories.Gloves, "Iron Gauntlets", "Intelligence8").Id;
        }
        return Equip(PlayerInventories.Gloves, "Iron Gauntlets", "Intelligence8", "FireResist3").Id;
    }

    static async Task<(int AtWrite, int AtTick, bool Running, bool Lease, double Rate)> RighteousFireHostInterval(
        bool recovery, string move)
    {
        using var b = new Battle("Righteous Fire", "Burning Damage Support");
        var c = b.Conn;
        var items = Game.ItemsOf(c);
        var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
        bool attributeMove = move is "loses qualification" or "cascading qualification" or "keeps qualification" or "gem qualification";
        using var exemption = move == "gem qualification"
            ? GameEvents.RequirementExemptions.Add("Game", actor => ReferenceEquals(actor, c) ? [host.Id] : []) : null;
        var wandType = Program.Items.Find(attributeMove ? "Carved Wand" : "Driftwood Wand")!;
        var wand = ItemCatalog.Create(wandType);
        var burningWand = wand with { Sockets = host.Item.Sockets, Links = [2], ItemLevel = 80 };
        if (move == "gem qualification")
        {
            var socket = burningWand.Sockets[0];
            var fire = socket.Gem!;
            var type = Program.Items.Gem(Program.Items.ByHash(fire.TypeHash)!)!;
            burningWand = burningWand with { Sockets = [socket with { Gem = fire with { GemExperience = type.ExperienceFor(8) } },
                burningWand.Sockets[1]] };
        }
        items.Change(PlayerInventories.Weapon1, host.Id, _ => burningWand);
        var replacement = wand with { Sockets = [burningWand.Sockets[0]], Links = [1] };
        uint picked = attributeMove ? EquipRighteousFireAttributeItems(c, items, wandType, move) : host.Id;
        if (move == "promotion")
        {
            items.Remove(PlayerInventories.Weapon1, host.Id);
            items.Put(PlayerInventories.Offhand1, host with { Item = burningWand });
            picked = items.Add(PlayerInventories.Weapon1, 0, 0, wand).Id;
        }
        if (move == "weapon swap") items.Add(PlayerInventories.Weapon2, 0, 0, replacement);
        if (move is "place" or "onto") items.Add(PlayerInventories.Cursor, 0, 0, replacement);
        if (move == "vendor")
        {
            var wisdom = Program.Items.Find("Scroll of Wisdom")!;
            items.Add(PlayerInventories.MainInventory, 0, 0, ItemCatalog.Create(wisdom));
            var seller = Game.Npcs.Value.Find("Metadata/NPC/Epilogue/LillyHideout")!;
            var offer = new InventoryItem(41, 0, 0, replacement with { Price = [(wisdom.Hash, 1)] });
            c.Shop = new(new(seller, 100, 100), new() { [offer.Id] = (NpcShops.FirstTabId, offer) });
        }
        c.Pools = Game.PoolsFor(c, 100);
        c.PlayerLife = c.PlayerMaxLife;
        b.Enemy.Walk.Teleport(105, 100, 0);
        var gem = Assert.Single(Game.GemSkillEffects(c));
        var clock = new ProjectileClock();
        var updates = new PlayerUpdatesState(Game.CombatLifetime!, clock);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = updates;
        try
        {
            const string name = "rf-host";
            uint inv = PlayerInventories.InventoryId(attributeMove ? PlayerInventories.Gloves : PlayerInventories.Weapon1);
            double start = Math.Ceiling(Math.Max(c.Clock.Elapsed.TotalSeconds, clock.Now(c))) + 1;
            void At(double time) => b.Session.AdvanceTo(ProjectileRuntime.Time(start + time));
            Task Pick() => Game.PickUpItem(name, b.Stream, b.Ciphers, c, new(inv, picked, 0, 0, 0));
            Task Swap(byte set) => Game.OnClientMessages(name, b.Stream, b.Ciphers,
                [(InstanceProtocol.SwapWeaponsId, new byte[] { 0, (byte)InstanceProtocol.SwapWeaponsId, set })], [], c);
            At(0);
            c.Clock.Start();
            await Game.ActivateRighteousFire(name, b.Stream, b.Ciphers, c, gem.Id, gem.Effect, gem.Level, gem.Quality, gem.Gem);
            var rf = Assert.IsType<RighteousFireState>(c.RighteousFire);
            Assert.Equal(host.Id, rf.SourceLifetime!.Key.Host);
            double rate = recovery
                ? DamageModel.Mitigate(c.PlayerMaxLife * rf.BurnPctPerMinute / 6000d,
                    Game.Resistance(c, DamageType.Fire), Game.MaxResistance(c, DamageType.Fire))
                : c.PlayerMaxLife * rf.DamagePctPerMinute / 6000d * 1.2;
            uint before = recovery ? c.PlayerLife : b.Enemy.Life;
            int Loss() => (int)(before - (recovery ? c.PlayerLife : b.Enemy.Life));
            int atWrite = 0;
            bool running = false, lease = false;
            clock.OnWait(() => At(.5));
            clock.Steps.Enqueue(async () =>
            {
                Assert.Equal((int)(rate * .5), Loss());
                At(.625);
                if (move == "pickup" || attributeMove)
                {
                    var held = items.Add(PlayerInventories.Cursor, 0, 0, wand);
                    await Pick();
                    Assert.Same(rf, c.RighteousFire);
                    Assert.Equal((int)(rate * .5), Loss());
                    items.Remove(PlayerInventories.Cursor, held.Id);
                }
                if (move == "onto")
                {
                    await Game.HeldOntoItem(name, b.Stream, b.Ciphers, c, new(inv, uint.MaxValue, 0, 0, 0), "");
                    Assert.Same(rf, c.RighteousFire);
                    Assert.Equal((int)(rate * .5), Loss());
                }
                if (move == "stash")
                {
                    Assert.Null(ItemMoves.InventoryToStashFrom(Game.StashOf(c), items, 0, host.Id,
                        PlayerInventories.MainInventory, Game.ShapeOf, out _));
                    Assert.Equal(PlayerInventories.Weapon1, items.RowOf(host.Id));
                }
                if (move == "weapon swap")
                {
                    await Swap(0);
                    Assert.Same(rf, c.RighteousFire);
                    Assert.Equal((int)(rate * .5), Loss());
                }
                At(.75);
                var gate = Program.SendGates.GetValue(b.Ciphers, _ => new(1, 1));
                Assert.True(gate.Wait(0));
                Task operation;
                try
                {
                    operation = move switch
                    {
                        _ when attributeMove => Pick(),
                        "pickup" or "promotion" => Pick(),
                        "place" => Game.PlaceItem(name, b.Stream, b.Ciphers, c, new(inv, 0, 0, 0, 0, 0, 0)),
                        "onto" => Game.HeldOntoItem(name, b.Stream, b.Ciphers, c, new(inv, host.Id, 0, 0, 0), ""),
                        "weapon swap" => Swap(1),
                        "stash" => Game.CtrlClickToStash(name, b.Stream, b.Ciphers, c, 0, host.Id, name),
                        "vendor" => VendorsFeature.BuyFromShop(name, b.Stream, b.Ciphers, c, new(41, inv, 0, 0, 0, 0, 0)),
                        _ => throw new ArgumentOutOfRangeException(nameof(move)),
                    };
                    Assert.False(operation.IsCompleted);
                    atWrite = Loss();
                    running = ReferenceEquals(rf, c.RighteousFire);
                    lease = c.GemSourceLifetimes.IsCurrent(rf.SourceLifetime);
                }
                finally { gate.Release(); }
                await operation;
                if (attributeMove)
                {
                    Assert.Equal(host.Id, Assert.Single(items.Items(PlayerInventories.Weapon1)).Id);
                    bool eligible = move == "keeps qualification";
                    Assert.Equal(!eligible && move != "gem qualification", Game.IsInactive(c, host.Id));
                    Assert.Equal(eligible, Game.SupportedSkill(c, gem.Id).RequirementsMet);
                    if (move == "cascading qualification")
                        Assert.True(Game.IsInactive(c, Assert.Single(items.Items(PlayerInventories.Boots)).Id));
                    if (!eligible)
                    {
                        At(.875);
                        await Game.PlaceItem(name, b.Stream, b.Ciphers, c, new(inv, 0, 0, 0, 0, 0, 0));
                        Assert.True(Game.SupportedSkill(c, gem.Id).RequirementsMet);
                        Assert.Null(c.RighteousFire);
                        Assert.False(c.GemSourceLifetimes.IsCurrent(rf.SourceLifetime));
                    }
                }
                else if (move == "promotion") Assert.Equal(host.Id, Assert.Single(items.Items(PlayerInventories.Weapon1)).Id);
                else Assert.DoesNotContain(items.EquipmentSnapshot(), entry => entry.Item.Id == host.Id);
                if (move is "place" or "onto" or "weapon swap" or "vendor")
                    Assert.Equal(gem.Id, Assert.Single(Game.GemSkillEffects(c)).Id);
                At(1);
            });
            clock.OnWait(() => updates.Stop.Cancel());
            c.Wire = (name, b.Stream, b.Ciphers);
            c.PlayerUpdatesState = updates;
            Assert.True(c.PlayerUpdatesWorker.TryStart(c, updates.Rules, null, updates, out updates.Generation));
            if (recovery) await Game.RunResourceRecovery(name, b.Stream, b.Ciphers, c, updates);
            else await Game.RunPlayerTick(name, b.Stream, b.Ciphers, c, updates);
            Assert.Empty(clock.Steps);
            return (atWrite, Loss(), running, lease, rate);
        }
        finally
        {
            c.Clock.Stop();
            Game.AreaUpdateContext.Value = previous;
            if (ReferenceEquals(c.PlayerUpdatesState, updates)) c.RetirePlayerUpdates();
            updates.Stop.Dispose();
        }
    }

    static int RighteousFireHalfSecondDamage(ClientState c, RighteousFireState rf, double factor)
    {
        double basis = (c.PlayerMaxLife * (double)rf.DamagePctPerMinute
            + c.Pools.EnergyShield * (double)rf.EnergyShieldDamagePctPerMinute) / 6000;
        return (int)Math.Floor(basis * factor * .5);
    }

    static async Task AssertRighteousFireTick(Battle b, RighteousFireState rf, double factor)
    {
        var c = b.Conn;
        b.Enemy.Burn = 0;
        uint before = b.Enemy.Life, playerLife = c.PlayerLife;
        await Game.BurnAround("rf-link", b.Stream, b.Ciphers, c, rf, .5);
        Assert.Equal(RighteousFireHalfSecondDamage(c, rf, factor), (int)(before - b.Enemy.Life));
        Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
        Assert.Equal(playerLife, c.PlayerLife);
    }

    static async Task Righteous_fire_combines_its_admitted_supports()
    {
        static uint Key(string stat) => Program.Stats.Key(stat)!.Value;
        using var b = new Battle("Righteous Fire", "Swift Affliction Support", "Elemental Focus Support",
            "Concentrated Effect Support", "Efficacy Support", "Less Duration Support");
        var c = b.Conn;
        var items = Game.ItemsOf(c);
        var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
        items.Change(PlayerInventories.Weapon1, host.Id, item => item with
        {
            ItemLevel = 80,
            Sockets = item.Sockets.Select((socket, i) => i == 0 ? socket
                : socket with { Gem = socket.Gem! with { Quality = 20 } }).ToArray(),
        });
        c.Pools = Game.PoolsFor(c, 100);
        c.PlayerStats[Key("fire_damage_+%")] = 25;
        b.Enemy.Walk.Teleport(105, 100, 0);
        b.Ally.Walk.Teleport(105, 100, 0);
        var gem = Assert.Single(Game.GemSkillEffects(c));
        Game.SupportedGem Current() => Game.SupportedSkill(c, gem.Id);
        var start = Current();
        Assert.Equal(new[] { "SupportElementalFocus", "SupportConcentratedEffect", "SupportEfficacy" },
            start.Supports.Select(s => s.Effect.Id));
        Assert.Equal((20, 25, 15), (start.Stats[Key("support_gem_elemental_damage_+%_final")],
            start.Stats[Key("support_area_concentrate_area_damage_+%_final")],
            start.Stats[Key("support_efficacy_damage_over_time_+%_final")]));
        Assert.Equal((10, 10, 10), (start.Stats[Key("elemental_damage_+%")],
            start.Stats[Key("area_damage_+%")], start.Stats[Key("damage_over_time_+%")]));
        Assert.NotEqual(0, start.Stats[Key("cannot_inflict_status_ailments")]);
        c.Clock.Start(); // A paused instance refuses skill use.
        await Game.OnUseObject("rf-links", b.Stream, b.Ciphers, c, new(Program.PlayerId, gem.Id, 1, 0), c.Clock.Elapsed.TotalSeconds);
        c.Clock.Stop();
        var rf = Assert.IsType<RighteousFireState>(c.RighteousFire);
        b.AdvanceBy(1);
        Task Tick(double factor) => AssertRighteousFireTick(b, rf, factor);
        async Task Replace(int index, string name, byte quality = 0)
        {
            await SwapRighteousFireSocket(b, host.Id, index, name, quality);
            Assert.Same(rf, c.RighteousFire);
        }
        await Tick(1.55 * 1.2 * 1.25 * 1.15);
        await Replace(2, "Lifetap Support");
        var duration = Current();
        Assert.Contains(duration.Supports, s => s.Effect.Id == "SupportSwiftAffliction");
        Assert.Contains(duration.Supports, s => s.Effect.Id == "SupportLessDuration");
        Assert.Equal((25, 10), (duration.Stats[Key("support_rapid_decay_damage_over_time_+%_final")],
            duration.Stats[Key("support_reduced_duration_damage_+%_final")]));
        Assert.Equal(20, duration.Stats[Key("damage_over_time_+%")]);
        Assert.False(Lifetap.ActiveAt(c, Game.ProjectileWorldTime(c)));
        await Tick(1.55 * 1.25 * 1.15 * 1.25 * 1.1);

        foreach (var prohibition in new[] { "Brutality Support", "Void Manipulation Support" })
        {
            await Replace(2, prohibition);
            var forbidden = Current();
            Assert.NotEqual(0, forbidden.Stats[Key("deal_no_elemental_damage")]);
            Assert.DoesNotContain(forbidden.Supports, s => s.Effect.Id is "SupportSwiftAffliction" or "SupportLessDuration");
            await Tick(0);
        }
        await Replace(2, "Arcane Surge Support", 20);
        Assert.Equal(10, Current().Stats[Key("spell_damage_+%")]);
        Game.GainArcaneSurgeLocked(c, Game.ProjectileWorldTime(c), 4000, 20, 30);
        Assert.True(Game.ArcaneSurgeActive(c, Game.ProjectileWorldTime(c)));
        await Tick(1.55 * 1.25 * 1.15 * 1.25 * 1.1);

        // A trigger support can be attached after activation, while the same physical RF is still burning.
        foreach (var (name, stat, value, factor) in new[]
        {
            ("Cast when Damage Taken Support", "cast_on_damage_taken_damage_+%_final", -65, .35),
            ("Cast on Ward Break Support", "cast_on_ward_break_damage_+%_final", -30, 1d), // Spell damage only.
        })
        {
            await Replace(2, name);
            Assert.Equal(value, Current().Stats[Key(stat)]);
            Assert.NotNull(Game.TriggerOnlyReason(Current()));
            await Tick(1.45 * 1.25 * 1.15 * factor);
        }
        await Replace(2, "Elemental Focus Support", 20);
        await Tick(1.55 * 1.2 * 1.25 * 1.15);
        Assert.Equal((5400, 4200), (rf.BurnPctPerMinute, rf.EnergyShieldBurnPctPerMinute));
    }

    static async Task Righteous_fire_uses_its_current_burning_damage_support()
    {
        using var b = new Battle("Righteous Fire", "Burning Damage Support");
        var c = b.Conn;
        c.Pools = Game.PoolsFor(c, 100);
        b.Enemy.Walk.Teleport(110, 100, 0);
        var items = Game.ItemsOf(c);
        var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
        var gem = Assert.Single(Game.GemSkillEffects(c));
        var supported = Game.SupportedSkill(c, gem.Id);
        Assert.Contains(supported.Supports, s => s.Effect.Id == "SupportBurningDamage");
        uint burning = Program.Stats.Key("support_burning_damage_+%_final")!.Value;
        uint increased = Program.Stats.Key("burn_damage_+%")!.Value;
        b.Ally.Walk.Teleport(110, 100, 0);
        Assert.Equal(20, supported.Stats[burning]);
        await Game.ActivateRighteousFire("rf-link", b.Stream, b.Ciphers, c, gem.Id,
            gem.Effect, gem.Level, gem.Quality, gem.Gem);
        var rf = Assert.IsType<RighteousFireState>(c.RighteousFire);
        b.AdvanceBy(1);
        var owner = Assert.IsType<GemSourceLifetimes.SourceLifetime>(rf.SourceLifetime);
        Task Tick(double factor) => AssertRighteousFireTick(b, rf, factor);
        await Tick(1.2);
        var socket = new InstanceItems.SocketGem(PlayerInventories.InventoryId(PlayerInventories.Weapon1), host.Id, 1);
        await Game.UnsocketGem("rf-link", b.Stream, b.Ciphers, c, socket);
        Assert.Same(rf, c.RighteousFire);
        Assert.True(c.GemSourceLifetimes.IsCurrent(owner));
        Assert.DoesNotContain(burning, Game.SupportedSkill(c, gem.Id).Stats.Keys);
        await Tick(1);
        c.PlayerStats[Program.Stats.Key("fire_damage_+%")!.Value] = 25;
        c.PlayerStats[burning] = 80; // Only a support attached to this skill supplies the more multiplier.
        await Tick(1.25);
        var held = Assert.Single(items.Items(PlayerInventories.Cursor));
        items.Change(PlayerInventories.Cursor, held.Id, item => item with { Quality = 20 });
        await Game.SocketGem("rf-link", b.Stream, b.Ciphers, c, socket);
        Assert.Same(rf, c.RighteousFire);
        var quality = Game.SupportedSkill(c, gem.Id).Stats;
        Assert.Equal((20, 10), (quality[burning], quality[increased]));
        await Tick(1.35 * 1.2);
        var highLevel = Program.Items.FindGem("Burning Damage Support")!.Value;
        items.Add(PlayerInventories.Cursor, 0, 0,
            ItemCatalog.Create(highLevel.Type) with { GemVariant = highLevel.Variant.Index, Quality = 20,
                GemExperience = Program.Items.Gem(highLevel.Type)!.ExperienceFor(20) });
        await Game.SocketGem("rf-link", b.Stream, b.Ciphers, c, socket);
        Assert.Same(rf, c.RighteousFire);
        Assert.True(c.GemSourceLifetimes.IsCurrent(owner));
        var swapped = Game.SupportedSkill(c, gem.Id).Stats;
        Assert.Equal((34, 10), (swapped[burning], swapped[increased]));
        await Tick(1.35 * 1.34);
        c.PlayerStats[Program.Stats.Key("base_deal_no_fire_damage")!.Value] = 1;
        await Tick(0);
        c.PlayerStats.Remove(Program.Stats.Key("base_deal_no_fire_damage")!.Value);
        items.Remove(PlayerInventories.Cursor, Assert.Single(items.Items(PlayerInventories.Cursor)).Id);
        Assert.Equal((5400, 4200), (rf.BurnPctPerMinute, rf.EnergyShieldBurnPctPerMinute));

        var replacement = Program.Items.FindGem("Righteous Fire")!.Value;
        items.Add(PlayerInventories.Cursor, 0, 0,
            ItemCatalog.Create(replacement.Type) with { GemVariant = replacement.Variant.Index });
        await Game.SocketGem("rf-link", b.Stream, b.Ciphers, c, socket with { Socket = 0 });
        Assert.True(owner.Retired);
        Assert.Null(c.RighteousFire);
        Assert.Equal(gem.Id, Assert.Single(Game.GemSkillEffects(c)).Id);
        await Tick(0);
    }

    static void Righteous_fire_uses_player_damage_modifiers()
    {
        static uint Key(string name) => Program.Stats.Key(name)!.Value;
        using var b = new Battle("Righteous Fire");
        var c = b.Conn;
        var items = Game.ItemsOf(c);
        var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
        items.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
            { Sockets = host.Item.Sockets, Links = [1] });
        items.Remove(PlayerInventories.Weapon1, host.Id);
        b.Enemy.Walk.Teleport(110, 100, 0);
        b.Ally.Walk.Teleport(110, 100, 0);
        c.Pools = Game.PoolsFor(c, 100);
        var gem = Assert.Single(Game.GemSkillEffects(c));
        Game.ActivateRighteousFire("rf-increases", b.Stream, b.Ciphers, c, gem.Id,
            gem.Effect, gem.Level, gem.Quality, gem.Gem).GetAwaiter().GetResult();
        var rf = Assert.IsType<RighteousFireState>(c.RighteousFire);
        b.AdvanceBy(1);
        Assert.Equal(4200, rf.DamagePctPerMinute);
        Assert.Equal(4200, rf.EnergyShieldDamagePctPerMinute);
        Assert.Equal(5400, rf.BurnPctPerMinute);
        void Burn(double increase, double factor = 1)
        {
            c.Pools = Game.PoolsFor(c, 100);
            double basis = (c.PlayerMaxLife * (double)rf.DamagePctPerMinute
                + c.Pools.EnergyShield * (double)rf.EnergyShieldDamagePctPerMinute) / 6000;
            uint playerLife = c.PlayerLife;
            b.Enemy.Life = b.Enemy.MaxLife;
            b.Enemy.Burn = 0;
            Game.BurnAround("rf-increases", b.Stream, b.Ciphers, c, rf, .5).GetAwaiter().GetResult();
            Assert.Equal((int)Math.Floor(basis * Math.Max(0, 100 + increase) / 100 * factor * .5),
                (int)(b.Enemy.MaxLife - b.Enemy.Life));
            Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
            Assert.Equal(playerLife, c.PlayerLife);
        }
        Burn(0);
        items.Add(6, 0, 0, new UniqueCatalog(Program.Items).Create("Le Heup of All", new Random(17))!);
        int ringDamage = Game.CharacterPlayerStats(c)[Key("damage_+%")];
        Assert.InRange(ringDamage, 10, 30);
        Burn(ringDamage);
        // A warmed snapshot must observe removing and restoring a real global source.
        var ring = Assert.Single(items.Items(6));
        items.Remove(6, ring.Id);
        Burn(0);
        items.Add(6, 0, 0, ring.Item);
        Burn(ringDamage);
        Assert.True(c.Pools.EnergyShield > 0);
        c.PlayerLife = Math.Max(2, c.PlayerLife / 2);
        Burn(ringDamage); // Current Life is not the base of the burn.

        var wandType = Program.Items.Find("Driftwood Wand")!;
        var plainWand = ItemCatalog.Create(wandType) with { ItemLevel = 86, Rarity = ItemRarity.Rare };
        var suffix = Program.Items.Mods.ById("GlobalDamageOverTimeMultiplier1h1")!;
        Assert.Contains(new ItemRolls(Program.Items).Pool(wandType, plainWand), p => p.Mod.Id == suffix.Id);
        var suffixRoll = suffix.Roll(ModSection.Explicit, new Random(17));
        int wandMultiplier = suffixRoll.Values[0];
        Assert.InRange(wandMultiplier, 7, 11);
        items.Add(PlayerInventories.Weapon1, 0, 0, plainWand with { Mods = [suffixRoll] });
        Assert.Equal(wandMultiplier, Game.CharacterPlayerStats(c)[Key("dot_multiplier_+")]);
        Burn(ringDamage, factor: (100 + wandMultiplier) / 100d);
        items.Remove(PlayerInventories.Weapon1, Assert.Single(items.Items(PlayerInventories.Weapon1)).Id);

        var uniques = new UniqueCatalog(Program.Items);
        var blade = uniques.Create("Fleshrender", new Random(17))!;
        int genericMultiplier = blade.Mods.Single(m => Program.Items.Mods.ByHash(m.Hash)!.Id
            == "GlobalDamageOverTimeMultiplierUnique__1").Values[0];
        Assert.InRange(genericMultiplier, 20, 40);
        items.Add(PlayerInventories.Weapon1, 0, 0, blade);
        Assert.Equal(genericMultiplier, Game.CharacterPlayerStats(c)[Key("dot_multiplier_+")]);
        Burn(ringDamage, factor: (100 + genericMultiplier) / 100d);

        var blackflame = uniques.Create("Blackflame", new Random(17))!;
        int fireMultiplier = blackflame.Mods.Single(m => Program.Items.Mods.ByHash(m.Hash)!.Id
            == "FireDamageOverTimeMultiplierUnique__3").Values[0];
        Assert.InRange(fireMultiplier, 8, 12);
        items.Add(7, 0, 0, blackflame);
        Assert.Equal(fireMultiplier, Game.CharacterPlayerStats(c)[Key("fire_dot_multiplier_+")]);
        double both = (100 + genericMultiplier + fireMultiplier) / 100d;
        Burn(ringDamage, factor: both); // Multipliers add to each other, separately from the ring's increase.
        c.PlayerStats[Key("fire_damage_+%")] = 30;
        Burn(ringDamage + 30, factor: both);
        c.PlayerStats.Remove(Key("fire_damage_+%"));
        foreach (string stat in new[] { "ignite_dot_multiplier_+", "ailment_dot_multiplier_+",
            "critical_ailment_dot_multiplier_+", "critical_ignite_dot_multiplier_+", "physical_dot_multiplier_+",
            "chaos_dot_multiplier_+", "cold_dot_multiplier_+" })
            c.PlayerStats[Key(stat)] = 500;
        Burn(ringDamage, factor: both);
        c.PlayerStats[Key("dot_multiplier_+")] = 7;
        Burn(ringDamage, factor: (100 + 7 + fireMultiplier) / 100d); // Explicit final override replaces the item input.
        c.PlayerStats[Key("dot_multiplier_+")] = -150;
        Burn(ringDamage, factor: 0);
        c.PlayerStats.Remove(Key("dot_multiplier_+"));
        c.PlayerStats[Key("fire_dot_multiplier_+")] = -10;
        Burn(ringDamage, factor: (100 + genericMultiplier - 10) / 100d);
        c.PlayerStats.Remove(Key("fire_dot_multiplier_+"));

        // Current equipment, including a real weapon-set swap, refreshes the live RF tick.
        c.PlayerStats[Key("virtual_dexterity_for_requirements")] = 0;
        Assert.Equal(0, Game.CharacterPlayerStats(c).GetValueOrDefault(Key("dot_multiplier_+")));
        Burn(ringDamage, factor: (100 + fireMultiplier) / 100d);
        c.PlayerStats[Key("virtual_dexterity_for_requirements")] = 1000;
        Burn(ringDamage, factor: both);
        items.SwapWeaponSets();
        Burn(ringDamage, factor: (100 + fireMultiplier) / 100d);
        items.SwapWeaponSets();
        Burn(ringDamage, factor: both);
        c.PlayerStats[Key("base_deal_no_fire_damage")] = 1;
        Burn(ringDamage, factor: 0);
        c.PlayerStats.Remove(Key("base_deal_no_fire_damage"));
        c.PlayerStats[Key("delirium_aura_damage_over_time_+%_final")] = 30;
        Burn(ringDamage, factor: both * 1.3);
        c.PlayerStats.Remove(Key("delirium_aura_damage_over_time_+%_final"));
        b.Enemy.Mods = MonsterModSet.None with
            { Effects = new(new Dictionary<string, int> { ["base_fire_damage_resistance_%"] = 50 }) };
        Burn(ringDamage, factor: both * .5); // Blackflame's ignite-only chaos rule does not change RF's damage type.
        Assert.False(b.Enemy.Ignites.Burning);
        b.Enemy.Mods = MonsterModSet.None;
        items.Remove(7, Assert.Single(items.Items(7)).Id);
        Burn(ringDamage, factor: (100 + genericMultiplier) / 100d);
        items.Remove(PlayerInventories.Weapon1, Assert.Single(items.Items(PlayerInventories.Weapon1)).Id);
        Burn(ringDamage);

        // The same generic bucket includes the real different-weapon mastery, once and additively with the ring.
        items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Nailed Fist")!));
        items.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
        const ushort node = 8872;
        var mastery = Program.Passives.EffectsFor(node).Single(e => e.Hash == 46493);
        Assert.Contains((Key("damage_+%_while_wielding_two_different_weapon_types"), 60), mastery.Stats);
        c.Passives.Add(node);
        c.MasteryEffects = new Dictionary<ushort, ushort>(c.MasteryEffects) { [node] = mastery.Hash };
        Burn(ringDamage + 60);
        c.Passives.Remove(node);
        c.MasteryEffects = new Dictionary<ushort, ushort>();
        Burn(ringDamage);

        // Each eligible increase joins one additive bucket; spell, hit and ailment-only sources stay out.
        foreach (string stat in new[] { "fire_damage_+%", "elemental_damage_+%", "damage_over_time_+%",
            "fire_damage_over_time_+%", "burn_damage_+%", "area_damage_+%", "righteous_fire_damage_+%" })
        {
            c.PlayerStats[Key(stat)] = 25;
            Burn(ringDamage + 25);
            c.PlayerStats.Remove(Key(stat));
        }
        c.PlayerStats[Key("fire_damage_+%")] = 30;
        c.PlayerStats[Key("area_damage_+%")] = 40;
        c.PlayerStats[Key("damage_over_time_+%")] = -20;
        Burn(ringDamage + 50);
        foreach (string stat in new[] { "spell_damage_+%", "attack_damage_+%", "projectile_damage_+%",
            "melee_damage_+%", "ignite_damage_+%", "base_ailment_damage_+%", "weapon_elemental_damage_+%" })
            c.PlayerStats[Key(stat)] = 500;
        Burn(ringDamage + 50);
        c.PlayerStats[Key("damage_over_time_+%")] = -500;
        Burn(ringDamage - 430);
        c.PlayerStats.Remove(Key("fire_damage_+%"));
        c.PlayerStats.Remove(Key("area_damage_+%"));
        c.PlayerStats.Remove(Key("damage_over_time_+%"));

        foreach (string stat in new[] { "base_deal_no_damage", "deal_no_elemental_damage",
            "deal_no_non_chaos_damage", "keystone_ancestral_bond", "deal_no_damage_yourself" })
        {
            c.PlayerStats[Key(stat)] = 1;
            Burn(ringDamage, factor: 0);
            c.PlayerStats.Remove(Key(stat));
            Burn(ringDamage);
        }
        c.PlayerStats[StatKeys.SpellSkillsDealNoDamage] = 1;
        Burn(ringDamage, factor: 0);
        c.PlayerStats.Remove(StatKeys.SpellSkillsDealNoDamage);
        c.PlayerStats[Key("delirium_aura_damage_over_time_+%_final")] = 30;
        Burn(ringDamage, factor: 1.3);
        c.PlayerStats.Remove(Key("delirium_aura_damage_over_time_+%_final"));
        b.Enemy.Mods = MonsterModSet.None with
            { Effects = new(new Dictionary<string, int> { ["base_fire_damage_resistance_%"] = 50 }) };
        Assert.Equal(.5, Game.MonsterDotTaken(b.Enemy, DamageType.Fire));
        c.PlayerStats[Key("base_reduce_enemy_fire_resistance_%")] = 100;
        Burn(ringDamage, factor: .5); // Resistance applies; penetration does not apply to a DoT.
        // Fractional ticks share the existing carry rather than rounding away each small interval.
        b.Enemy.Life = b.Enemy.MaxLife;
        b.Enemy.Burn = 0;
        double dps = (c.PlayerMaxLife + c.Pools.EnergyShield) * .7 * (100 + ringDamage) / 100 * .5;
        for (int i = 0; i < 17; i++)
            Game.BurnAround("rf-increases", b.Stream, b.Ciphers, c, rf, .00037).GetAwaiter().GetResult();
        double total = dps * .00037 * 17;
        Assert.Equal((int)Math.Floor(total), (int)(b.Enemy.MaxLife - b.Enemy.Life));
        Assert.Equal(total - Math.Floor(total), b.Enemy.Burn, 6);
        b.Enemy.Walk.Teleport(150, 100, 0);
        Burn(ringDamage, factor: 0);
        Assert.DoesNotContain(200u, rf.Auras.Keys);
        Assert.DoesNotContain(201u, rf.Auras.Keys);
    }

    static void Different_weapon_mastery_reaches_shared_damage()
    {
        static uint Key(string name) => Program.Stats.Key(name)!.Value;
        var uniques = new UniqueCatalog(Program.Items);
        Item Make(string name) => name is "Varunastra" or "Le Heup of All"
            ? uniques.Create(name, new Random(17))! : ItemCatalog.Create(Program.Items.Find(name)!);
        using var b = new Battle("Spectral Throw", "Fireball", "Bear Trap", "Essence Drain");
        var c = b.Conn;
        c.PlayerStats[Key("global_cannot_crit")] = 1;
        c.PlayerStats[Key("spell_damage_+%")] = 0; // keep the wand's unrelated implicit out of the chaos control
        c.PlayerStats[Key("attack_minimum_added_chaos_damage")] = 10;
        c.PlayerStats[Key("attack_maximum_added_chaos_damage")] = 10;
        c.PlayerStats[Key("spell_minimum_added_chaos_damage")] = 10;
        c.PlayerStats[Key("spell_maximum_added_chaos_damage")] = 10;
        var items = Game.ItemsOf(c);
        var main = items.Items(PlayerInventories.Weapon1).Single();
        items.Add(PlayerInventories.BodyArmour, 0, 0, Make("Simple Robe") with
            { Sockets = main.Item.Sockets, Links = [1, 1, 1, 1] });
        items.Add(6, 0, 0, Make("Le Heup of All"));
        void Hands(string? mainName, string? offName)
        {
            foreach (var (row, name) in new (byte, string?)[]
                { (PlayerInventories.Weapon1, mainName), (PlayerInventories.Offhand1, offName) })
            {
                if (items.Items(row) is [var held]) items.Remove(row, held.Id);
                if (name is not null) items.Add(row, 0, 0, Make(name));
            }
            c.Pools = Game.PoolsFor(c, 100);
        }
        const ushort node = 8872;
        var effect = Program.Passives.EffectsFor(node).Single(e => e.Id == "DualWield3");
        Assert.Equal((ushort)46493, effect.Hash);
        Assert.Contains((Key("damage_+%_while_wielding_two_different_weapon_types"), 60), effect.Stats);
        void Select(bool enabled)
        {
            var masteries = new Dictionary<ushort, ushort>(c.MasteryEffects);
            if (enabled) { c.Passives.Add(node); masteries[node] = effect.Hash; }
            else { c.Passives.Remove(node); masteries.Remove(node); }
            c.MasteryEffects = masteries;
            c.Pools = Game.PoolsFor(c, 100);
        }
        Dictionary<uint, int> Stats() => Game.FinalPlayerStats(c).ToDictionary(s => s.Item1, s => s.Item2);
        Hands("Nailed Fist", "Rusted Sword");
        int ringDamage = Stats().GetValueOrDefault(Key("damage_+%"));
        Assert.InRange(ringDamage, 10, 30);
        var attack = Game.GemSkillEffects(c).Single(g => g.Name == "Spectral Throw");
        var spell = Game.GemSkillEffects(c).Single(g => g.Name == "Fireball");
        var secondary = Game.GemSkillEffects(c).Single(g => g.Name == "Bear Trap");
        var drain = Game.GemSkillEffects(c).Single(g => g.Name == "Essence Drain");
        var drainBase = Assert.IsType<MonsterEssenceDrain.Cast>(Game.EssenceDrainCast(c, drain.Id, drain.Effect));
        Assert.True(drainBase.DamagePerSecond > 0);
        var secondaryBase = Game.SupportedHitFor(c, secondary.Id, suppress: false);
        Assert.True(secondaryBase[DamageType.Physical].Min > 0);
        var poisonBase = Game.SupportedAilmentRows(c, attack.Id, "main_hand").ToDictionary(r => r.Stat, r => r.Value);
        var igniteBase = Game.SupportedAilmentRows(c, spell.Id, "spell").ToDictionary(r => r.Stat, r => r.Value);
        Assert.Contains(poisonBase, r => r.Key.Contains("poison") && r.Value > 0);
        Assert.Contains(igniteBase, r => r.Key.Contains("ignite") && r.Value > 0);
        Select(true);
        Assert.Equal(60, Stats()[Key("damage_+%_while_wielding_two_different_weapon_types")]);
        double factor = (100 + ringDamage + 60) / 100.0;
        Assert.Equal(10 * factor, Game.SupportedHitFor(c, attack.Id, suppress: false)[DamageType.Chaos].Min, 6);
        Assert.Equal(30 * factor, Game.SupportedProjectileHit(c, spell.Id, area: false)[DamageType.Chaos].Min, 6);
        double ratio = (100 + ringDamage + 60.0) / (100 + ringDamage);
        Assert.Equal(drainBase.DamagePerSecond * ratio,
            Game.EssenceDrainCast(c, drain.Id, drain.Effect)!.DamagePerSecond, 6);
        Assert.Equal(secondaryBase[DamageType.Physical].Min * ratio,
            Game.SupportedHitFor(c, secondary.Id, suppress: false)[DamageType.Physical].Min, 6);
        foreach (var (skill, hand, baseline) in new[] { (attack.Id, "main_hand", poisonBase), (spell.Id, "spell", igniteBase) })
            foreach (var (stat, value) in Game.SupportedAilmentRows(c, skill, hand))
                // Ailment panel values are whole damage per minute, rounded only when published.
                Assert.InRange(value, (int)Math.Floor(baseline[stat] * ratio) - 1, (int)Math.Ceiling(baseline[stat] * ratio) + 1);
        var panel = Game.PlayerSkills(c).Single(s => s.Id == attack.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal((int)(10 * factor), panel[Key("main_hand_minimum_chaos_damage")]);

        foreach (var (mainName, offName, qualifies) in new (string?, string?, bool)[]
        {
            ("Nailed Fist", "Rusted Sword", true), ("Rusted Sword", "Nailed Fist", true),
            ("Nailed Fist", "Nailed Fist", false), ("Rusted Sword", "Rusted Spike", false),
            ("Rusted Spike", "Rusted Sword", false), ("Glass Shank", "Carving Knife", true),
            ("Carving Knife", "Glass Shank", true), ("Carving Knife", "Carving Knife", false),
            ("Driftwood Club", "Driftwood Sceptre", true), ("Driftwood Sceptre", "Driftwood Club", true),
            ("Driftwood Sceptre", "Driftwood Sceptre", false), ("Driftwood Wand", "Driftwood Wand", false),
            ("Rusted Sword", "Splintered Tower Shield", false), ("Corroded Blade", null, false),
            ("Rusted Sword", null, false), (null, null, false),
            ("Varunastra", null, true), ("Varunastra", "Splintered Tower Shield", true),
            ("Varunastra", "Rusted Sword", true), ("Rusted Sword", "Varunastra", true), ("Varunastra", "Varunastra", true),
        })
        {
            Hands(mainName, offName);
            int expected = ringDamage + (qualifies ? 60 : 0);
            Assert.Equal(expected, Stats().GetValueOrDefault(Key("damage_+%")));
            Assert.Equal(expected, Stats().GetValueOrDefault(Key("damage_+%"))); // warm snapshot, no repeated addition
            Assert.Equal(30 * (100 + expected) / 100.0,
                Game.SupportedProjectileHit(c, spell.Id, area: false)[DamageType.Chaos].Min, 6);
        }
        Hands(null, null);
        c.PlayerStats[Key("unarmed_counts_as_dual_wielding")] = 1;
        Assert.Equal(1, Stats()[Key("is_dual_wielding")]);
        Assert.Equal(ringDamage, Stats().GetValueOrDefault(Key("damage_+%")));
        c.PlayerStats.Remove(Key("unarmed_counts_as_dual_wielding"));

        Hands("Rusted Sword", "Imperial Claw");
        Assert.Equal(ringDamage + 60, Stats()[Key("damage_+%")]);
        c.PlayerStats[Key("virtual_dexterity_for_requirements")] = 100;
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Null(Game.ActiveOffHand(c));
        Assert.Equal(ringDamage, Stats().GetValueOrDefault(Key("damage_+%")));
        c.PlayerStats[Key("virtual_dexterity_for_requirements")] = 1000;
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(ringDamage + 60, Stats()[Key("damage_+%")]);
        Hands("Rusted Sword", "Splintered Tower Shield");
        items.Add(14, 0, 0, Make("Varunastra"));
        Assert.Equal(ringDamage, Stats().GetValueOrDefault(Key("damage_+%")));
        items.SwapWeaponSets();
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(ringDamage + 60, Stats()[Key("damage_+%")]);
        items.SwapWeaponSets();
        Hands("Nailed Fist", "Rusted Sword");
        Select(false);
        Assert.Equal(0, Stats().GetValueOrDefault(Key("damage_+%_while_wielding_two_different_weapon_types")));
        Assert.Equal(ringDamage, Stats().GetValueOrDefault(Key("damage_+%")));
        Select(true);
        c.PlayerStats[Key("damage_+%")] = 7;
        Assert.Equal(7, Stats()[Key("damage_+%")]);
        c.PlayerStats.Remove(Key("damage_+%"));
        Assert.Equal(ringDamage + 60, Stats()[Key("damage_+%")]);

        items.Change(14, items.Items(14).Single().Id, _ => Make("Rusted Sword"));
        var released = Game.CaptureGemProjectile(c, b.Session, spell.Id, spell, (120, 100), new(0));
        var shot = Assert.IsType<ProjectileOffense>(released.Payload);
        Assert.Equal(30 * factor, shot.Hit[DamageType.Chaos].Min, 6);
        var drainUse = Game.CaptureGemProjectile(c, b.Session, drain.Id, drain, (120, 100), new(0));
        var drainShot = Assert.IsType<ProjectileOffense>(drainUse.Payload);
        var releasedDrain = Assert.IsType<MonsterEssenceDrain.Cast>(drainShot.EssenceDrain);
        Assert.Equal(drainBase.DamagePerSecond * ratio, releasedDrain.DamagePerSecond, 6);
        b.Session.Runtime.Release(drainUse);
        items.SwapWeaponSets();
        Select(false);
        Assert.Equal(30 * (100 + ringDamage) / 100.0,
            Game.SupportedProjectileHit(c, spell.Id, area: false)[DamageType.Chaos].Min, 6);
        Assert.Equal(30 * factor, shot.Hit[DamageType.Chaos].Min, 6);
        Assert.Equal(drainBase.DamagePerSecond, Game.EssenceDrainCast(c, drain.Id, drain.Effect)!.DamagePerSecond, 6);
        b.Session.AdvanceTo(ProjectileRuntime.Time(.4));
        Assert.Same(releasedDrain, b.Enemy.EssenceDrain.Active);
        Assert.Equal(drainBase.DamagePerSecond * ratio, b.Enemy.EssenceDrain.Active!.DamagePerSecond, 6);
        uint minimum = (uint)drainShot.Hit.Components.Sum(part => Math.Floor(part.Range.Min));
        uint maximum = (uint)drainShot.Hit.Components.Sum(part => Math.Floor(part.Range.Max))
            + (uint)Math.Ceiling(releasedDrain.DamagePerSecond * .4);
        Assert.InRange(b.Enemy.MaxLife - b.Enemy.Life, minimum, maximum);
        Assert.True(b.Ally.EssenceDrain.Idle);
        Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
    }

    static void Fan_of_Blades_reaches_projectile_count_and_attack_damage()
    {
        static uint Key(string name) => Program.Stats.Key(name)!.Value;
        static Item Make(string name) => name == "Varunastra"
            ? new UniqueCatalog(Program.Items).Create(name, new Random(17))!
            : ItemCatalog.Create(Program.Items.Find(name)!);
        using var b = new Battle("Spectral Throw", "Fireball", "Double Strike", "Lightning Strike", "Kinetic Blast", "Ice Shot");
        var c = b.Conn;
        c.PlayerStats[Key("global_cannot_crit")] = 1;
        c.PlayerStats[Key("base_deal_no_physical_damage")] = 1;
        c.PlayerStats[Key("attack_minimum_added_lightning_damage")] = 10;
        c.PlayerStats[Key("attack_maximum_added_lightning_damage")] = 10;
        c.PlayerStats[Key("attack_minimum_added_chaos_damage")] = 10;
        c.PlayerStats[Key("attack_maximum_added_chaos_damage")] = 10;
        c.PlayerStats[Key("damage_+%")] = 100;
        c.PlayerStats[Key("spell_damage_+%")] = 0;
        var items = Game.ItemsOf(c);
        var hand = items.Items(PlayerInventories.Weapon1).Single();
        var activeType = Program.Items.ByHash(hand.Item.Sockets[0].Gem!.TypeHash)!;
        items.Add(PlayerInventories.BodyArmour, 0, 0, Make("Simple Robe") with
        {
            Sockets = [hand.Item.Sockets[0] with { Gem = hand.Item.Sockets[0].Gem! with
                { GemExperience = Program.Items.Gem(activeType)!.ExperienceFor(2) } }, .. hand.Item.Sockets.Skip(1)],
            Links = [1, 1, 1, 1, 1, 1],
        });
        items.Change(PlayerInventories.Weapon1, hand.Id, _ => Make("Nailed Fist"));
        var gem = Game.GemSkillEffects(c).Single(g => g.Name == "Spectral Throw");
        var fireball = Game.GemSkillEffects(c).Single(g => g.Name == "Fireball");
        var melee = Game.GemSkillEffects(c).Single(g => g.Name == "Double Strike");
        var hybrid = Game.GemSkillEffects(c).Single(g => g.Name == "Lightning Strike");
        var survivorsCatalog = SurvivorsSkills.SurvivorsCatalogFor(c);
        SurvivorsSkills.SurvivorsComputedCast SurvivorsCast(string name)
        {
            int index = survivorsCatalog.ToList().FindIndex(skill => skill.Name == name);
            Assert.True(index >= 0);
            var run = new SurvivorsRun();
            run.Learn(index);
            return Assert.IsType<SurvivorsSkills.SurvivorsComputedCast>(SurvivorsSkills.SurvivorsComputeCast(
                c, run, survivorsCatalog[index], 1, index, new([], [], 1, 20, 40)));
        }
        var type = Program.Items.Find("Large Cluster Jewel")!;
        var theme = ClusterJewels.FindTheme(type.Cluster!, "affliction_dagger_and_claw_damage")!;
        var fan = Program.Items.Mods.ById("AfflictionNotableFanofBlades_")!;
        var plain = ClusterJewels.Create(type, theme, 8, 2)!;
        Assert.Contains(new ItemRolls(Program.Items).Pool(type, plain), p => p.Mod.Id == fan.Id);
        var cluster = ClusterJewels.Create(type, theme, 8, 2, [fan])!;
        var jewel = items.Add(PlayerInventories.PassiveJewels, 20, 0, cluster)!;
        c.Passives.Add(Program.Passives.JewelNodeForSlot(20)!.Value);
        var layout = ClusterJewelLayout.For(20, cluster)!;
        ushort notable = (ushort)(layout.Base + Assert.Single(layout.NotableOrders).Key);
        c.Pools = Game.PoolsFor(c, 100);
        var spellBase = Game.SupportedProjectileHit(c, fireball.Id, area: false);
        var meleeBase = Game.SupportedHitFor(c, melee.Id, suppress: false);
        var hybridMeleeBase = Game.SupportedHitFor(c, hybrid.Id, suppress: false);
        var hybridProjectileBase = Game.SupportedHitFor(c, hybrid.Id, suppress: false, projectile: true);
        var ailmentBase = Game.SupportedAilmentRows(c, gem.Id, "main_hand");
        Assert.Contains(ailmentBase, row => row.Stat.Contains("poison") && row.Value > 0);
        var survivorsBase = SurvivorsCast("Spectral Throw");
        var survivorsSpellBase = SurvivorsCast("Fireball");
        c.ClusterPassives.Add(notable);
        var stats = Game.FinalPlayerStats(c).ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Equal(1, stats[Key("attack_skill_additional_num_projectiles_while_wielding_claws_daggers")]);
        Assert.Equal(20, stats[Key("projectile_attack_damage_+%_with_claw_or_dagger")]);
        Assert.Equal(2, gem.Level);
        var use = Game.CaptureGemProjectile(c, b.Session, gem.Id, gem, (120, 100), new(0));
        var shot = Assert.IsType<ProjectileOffense>(use.Payload);
        // Level-two Spectral Throw: 10 * 102.6% * (100% + 100% + 20%), with one additional projectile.
        Assert.Equal((2, new DamageRange(22.572, 22.572)), (use.Profile.Count, shot.Hit[DamageType.Lightning]));
        Assert.Equal(spellBase.Components, Game.SupportedProjectileHit(c, fireball.Id, area: false).Components);
        Assert.Equal(meleeBase.Components, Game.SupportedHitFor(c, melee.Id, suppress: false).Components);
        Assert.Equal(hybridMeleeBase.Components, Game.SupportedHitFor(c, hybrid.Id, suppress: false).Components);
        Assert.Equal(hybridProjectileBase[DamageType.Lightning].Min * 1.1,
            Game.SupportedHitFor(c, hybrid.Id, suppress: false, projectile: true)[DamageType.Lightning].Min, 6);
        Assert.Equal(ailmentBase, Game.SupportedAilmentRows(c, gem.Id, "main_hand"));
        var survivors = SurvivorsCast("Spectral Throw");
        Assert.Equal(survivorsBase.Count + 1, survivors.Count);
        Assert.Equal(survivorsBase.Shot!.Hit[DamageType.Lightning].Min * 1.1,
            survivors.Shot!.Hit[DamageType.Lightning].Min, 6);
        Assert.Equal(survivorsBase.Shot.AreaHit![DamageType.Lightning].Min * 1.1,
            survivors.Shot.AreaHit![DamageType.Lightning].Min, 6);
        var survivorsSpell = SurvivorsCast("Fireball");
        Assert.Equal(survivorsSpellBase.Count, survivorsSpell.Count);
        Assert.Equal(survivorsSpellBase.Shot!.Hit.Components, survivorsSpell.Shot!.Hit.Components);

        foreach (var (mainName, offName, count, mainDamage, offDamage) in new (string, string?, int, double, double)[]
        {
            ("Nailed Fist", null, 2, 22.572, 0), ("Glass Shank", null, 2, 22.572, 0),
            ("Carving Knife", null, 2, 22.572, 0), ("Varunastra", null, 2, 22.572, 0),
            ("Rusted Sword", null, 1, 20.52, 0),
            ("Rusted Sword", "Nailed Fist", 2, 20.52, 22.572), ("Nailed Fist", "Rusted Sword", 2, 22.572, 20.52),
            ("Rusted Sword", "Varunastra", 2, 20.52, 22.572), ("Varunastra", "Rusted Sword", 2, 22.572, 20.52),
            ("Glass Shank", "Carving Knife", 2, 22.572, 22.572), ("Varunastra", "Varunastra", 2, 22.572, 22.572),
        })
        {
            items.Change(PlayerInventories.Weapon1, hand.Id, _ => Make(mainName));
            if (items.Items(PlayerInventories.Offhand1) is [var off]) items.Remove(PlayerInventories.Offhand1, off.Id);
            if (offName is not null) items.Add(PlayerInventories.Offhand1, 0, 0, Make(offName));
            var captured = Game.CaptureGemProjectile(c, b.Session, gem.Id, gem, (120, 100), new(0));
            Assert.Equal(count, captured.Profile.Count);
            Assert.Equal(mainDamage, Assert.IsType<ProjectileOffense>(captured.Payload).Hit[DamageType.Lightning].Min, 6);
            var panel = Game.PlayerSkills(c).Single(s => s.Id == gem.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
            Assert.Equal(count, panel[Key("total_number_of_projectiles_to_fire")]);
            Assert.Equal((int)mainDamage, panel[Key("main_hand_minimum_lightning_damage")]);
            if (offName is not null)
                Assert.Equal(offDamage, Game.SupportedOffHandHit(c, gem.Id, Game.OffhandWeaponFor(c)!)[DamageType.Lightning].Min, 6);
            Assert.Equal(1, Game.CaptureGemProjectile(c, b.Session, fireball.Id, fireball, (120, 100), new(0)).Profile.Count);
        }
        items.Remove(PlayerInventories.Offhand1, items.Items(PlayerInventories.Offhand1).Single().Id);
        foreach (var (weapon, skill) in new[] { ("Driftwood Wand", "Kinetic Blast"), ("Crude Bow", "Ice Shot") })
        {
            items.Change(PlayerInventories.Weapon1, hand.Id, _ => Make(weapon));
            var active = Game.GemSkillEffects(c).Single(g => g.Name == skill);
            var captured = Game.CaptureGemProjectile(c, b.Session, active.Id, active, (120, 100), new(0));
            Assert.Equal(1, captured.Profile.Count);
            var inputs = Game.SupportedHitInputsFor(c, active.Id)!;
            Assert.Equal(inputs.Raw[DamageType.Lightning].Min * 2,
                Assert.IsType<ProjectileOffense>(captured.Payload).Hit[DamageType.Lightning].Min, 6);
        }

        // The notable and body-socketed gems stay active when only the claw's Dexterity requirement fails.
        items.Change(PlayerInventories.Weapon1, hand.Id, _ => Make("Imperial Claw"));
        c.PlayerStats[Key("virtual_dexterity_for_requirements")] = 100;
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Null(Game.ActiveMainHand(c));
        stats = Game.FinalPlayerStats(c).ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Equal(1, stats[Key("attack_skill_additional_num_projectiles_while_wielding_claws_daggers")]);
        var inactivePanel = Game.PlayerSkills(c).Single(s => s.Id == gem.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal(1, inactivePanel[Key("total_number_of_projectiles_to_fire")]);
        c.PlayerStats[Key("virtual_dexterity_for_requirements")] = 1000;
        c.Pools = Game.PoolsFor(c, 100);
        items.Change(PlayerInventories.Weapon1, hand.Id, _ => Make("Nailed Fist"));
        c.ClusterPassives.Remove(notable);
        var refunded = Game.CaptureGemProjectile(c, b.Session, gem.Id, gem, (120, 100), new(0));
        Assert.Equal((1, new DamageRange(20.52, 20.52)),
            (refunded.Profile.Count, Assert.IsType<ProjectileOffense>(refunded.Payload).Hit[DamageType.Lightning]));
        c.ClusterPassives.Add(notable);
        c.PlayerStats[Key("number_of_additional_projectiles")] = 1;
        var released = Game.CaptureGemProjectile(c, b.Session, gem.Id, gem, (120, 100), new(0));
        var releasedShot = Assert.IsType<ProjectileOffense>(released.Payload);
        Assert.Equal((3, new DamageRange(22.572, 22.572)), (released.Profile.Count, releasedShot.Hit[DamageType.Lightning]));
        b.Session.Runtime.Release(released);
        items.Add(14, 0, 0, Make("Rusted Sword"));
        items.SwapWeaponSets();
        items.Remove(PlayerInventories.PassiveJewels, jewel.Id);
        c.PlayerStats.Remove(Key("number_of_additional_projectiles"));
        var removed = Game.CaptureGemProjectile(c, b.Session, gem.Id, gem, (120, 100), new(0));
        Assert.Equal((1, new DamageRange(20.52, 20.52)),
            (removed.Profile.Count, Assert.IsType<ProjectileOffense>(removed.Payload).Hit[DamageType.Lightning]));
        Assert.Equal((3, new DamageRange(22.572, 22.572)), (released.Profile.Count, releasedShot.Hit[DamageType.Lightning]));
        b.Session.AdvanceTo(ProjectileRuntime.Time(.4));
        // Damage rolls floor each type independently; this only checks at least one admitted projectile hit.
        uint minimum = (uint)releasedShot.Hit.Components.Sum(part => Math.Floor(part.Range.Min));
        uint maximum = (uint)releasedShot.Hit.Components.Sum(part => Math.Floor(part.Range.Max));
        Assert.InRange(b.Enemy.MaxLife - b.Enemy.Life, minimum, maximum * 3);
        Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
    }

    static void Claw_and_dagger_projectiles_keep_the_released_hand_and_source()
    {
        static uint Key(string name) => Program.Stats.Key(name)!.Value;
        static Item Weapon(string name)
        {
            if (name != "Varunastra") return ItemCatalog.Create(Program.Items.Find(name)!);
            var uniques = new UniqueCatalog(Program.Items);
            return Assert.IsType<Item>(uniques.Create(uniques.Find(name)!, new Random(17)));
        }
        foreach (var (name, itemClass, other, mod, maximum, ineligible) in new[]
        {
            ("Nailed Fist", "Claw", "Glass Shank", "AbyssAddedLightningDamageWithClawsJewel1__", 19, "Imperial Claw"),
            ("Carving Knife", "Rune Dagger", "Nailed Fist", "AbyssAddedLightningDamageWithDaggersJewel1", 20, "Demon Dagger"),
            ("Varunastra", "One Hand Sword", "Rusted Sword", "AbyssAddedLightningDamageWithClawsJewel1__", 19, "Varunastra"),
        })
        {
            using var b = new Battle("Spectral Throw", "Fireball", "Spellblade Support", "Shield Charge");
            var c = b.Conn;
            c.PlayerStats[Key("global_cannot_crit")] = 1;
            c.PlayerStats[Key("base_deal_no_physical_damage")] = 1;
            c.PlayerStats[Key("lightning_damage_+%")] = 100;
            var items = Game.ItemsOf(c);
            var host = items.Items(PlayerInventories.Weapon1).Single();
            var activeType = Program.Items.ByHash(host.Item.Sockets[0].Gem!.TypeHash)!;
            var body = items.Add(PlayerInventories.BodyArmour, 0, 0, Weapon("Simple Robe") with
            {
                ItemLevel = 100,
                Sockets = [host.Item.Sockets[0] with { Gem = host.Item.Sockets[0].Gem! with
                    { GemExperience = Program.Items.Gem(activeType)!.ExperienceFor(2) } }, .. host.Item.Sockets.Skip(1)],
                Links = [1, 2, 1],
            })!;
            items.Change(PlayerInventories.Weapon1, host.Id, _ => Weapon(name));
            items.Add(PlayerInventories.Offhand1, 0, 0, Weapon(other));
            items.Add(PlayerInventories.Weapon2, 0, 0, Weapon("Rusted Sword"));
            var jewel = Weapon("Murderous Eye Jewel") with
            {
                Rarity = ItemRarity.Magic,
                Mods = [new(ModSection.Explicit, Program.Items.Mods.ById(mod)!.Hash, [1, maximum])],
            };
            var belt = items.Add(PlayerInventories.Belt, 0, 0, Weapon("Stygian Vise") with
            { Sockets = [new(SocketColour.Abyss, jewel)] })!;
            var attack = Game.GemSkillEffects(c).Single(g => g.Name == "Spectral Throw");
            var spell = Game.GemSkillEffects(c).Single(g => g.Name == "Fireball");
            Assert.Null(Game.WeaponRestricted(c, attack.Effect));
            Assert.Equal(itemClass, Game.WeaponFor(c).ItemClass);
            Assert.Equal((10_260, 10_260), Game.SkillsFor(c).DamageCoefficients(attack.Effect, attack.Level));
            // Native level-two Spectral Throw has base/added offsets260:102.6% before increased lightning.
            var raw = new DamageRange(1.026, maximum * 1026 / 1000d);
            var hit = new DamageRange(2.052, maximum * 2052 / 1000d);
            Assert.Equal(raw, Game.SupportedHitInputsFor(c, attack.Id)!.Raw[DamageType.Lightning]);
            Assert.Equal(0, Game.SupportedHitInputsFor(c, attack.Id, weapon: Game.OffhandWeaponFor(c))!.Raw[DamageType.Lightning].Max);
            Assert.Equal(hit, Game.SupportedProjectileHit(c, attack.Id, area: false)[DamageType.Lightning]);
            var panel = Game.PlayerSkills(c).Single(s => s.Id == attack.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
            Assert.Equal((int)hit.Min, panel[Key("main_hand_minimum_lightning_damage")]);
            Assert.Equal((int)hit.Max, panel[Key("main_hand_maximum_lightning_damage")]);
            Assert.Equal(0, panel.GetValueOrDefault(Key("off_hand_maximum_lightning_damage")));
            var off = items.Items(PlayerInventories.Offhand1).Single();
            items.Change(PlayerInventories.Offhand1, off.Id, _ => Weapon(name));
            Assert.Equal(hit, Game.SupportedOffHandHit(c, attack.Id, Game.OffhandWeaponFor(c)!)[DamageType.Lightning]);
            var both = Game.PlayerSkills(c).Single(s => s.Id == attack.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
            Assert.Equal((int)hit.Max, both[Key("off_hand_maximum_lightning_damage")]);
            // The real shield override must not inherit the eligible weapon still held in the main hand.
            items.Change(PlayerInventories.Offhand1, off.Id, _ => Weapon("Splintered Tower Shield"));
            var charge = Game.GemSkillEffects(c).Single(g => g.Name == "Shield Charge");
            Assert.Null(Game.WeaponRestricted(c, charge.Effect));
            var shield = Assert.IsType<SkillCatalog.Unarmed>(Game.ShieldWeapon(c, charge.Id));
            Assert.Null(shield.ItemClass);
            Assert.Equal(0, Game.SupportedHitInputsFor(c, charge.Id, weapon: shield)!.Raw[DamageType.Lightning].Max);
            var shieldPanel = Game.PlayerSkills(c).Single(s => s.Id == charge.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
            Assert.Equal(0, shieldPanel.GetValueOrDefault(Key("main_hand_maximum_lightning_damage")));
            items.Change(PlayerInventories.Offhand1, off.Id, _ => Weapon(other));
            uint spellblade = Key("support_spell_weapon_damage_gain_%_of_one_hand_melee_weapon_damage_as_added_spell_damage");
            Assert.Equal(40, Game.SupportedSkill(c, spell.Id).Stats[spellblade]);
            Assert.Equal(0, Game.SupportedHitInputsFor(c, spell.Id)!.Raw[DamageType.Lightning].Max);
            items.Change(PlayerInventories.BodyArmour, body.Id, item => item with { Links = [1, 1, 1, 1] });
            Assert.Equal(0, Game.SupportedHitInputsFor(c, spell.Id)!.Raw[DamageType.Lightning].Max);
            items.Change(PlayerInventories.BodyArmour, body.Id, item => item with { Links = [1, 2, 1] });

            // The jewel and body-socketed skill remain active when a high-level weapon becomes unusable.
            items.Change(PlayerInventories.Weapon1, host.Id, _ => Weapon(ineligible));
            c.Experience = CharacterStats.ExperienceFor(50);
            c.Pools = Game.PoolsFor(c, 50);
            Assert.Null(Game.ActiveMainHand(c));
            Assert.Equal(2, Game.GemSkillEffects(c).Single(g => g.Id == attack.Id).Level);
            Assert.Equal(0, Game.SupportedHitInputsFor(c, attack.Id)!.Raw[DamageType.Lightning].Max);
            c.Experience = CharacterStats.ExperienceFor(100);
            c.Pools = Game.PoolsFor(c, 100);
            items.Change(PlayerInventories.Weapon1, host.Id, _ => Weapon(name));
            Assert.Equal(raw, Game.SupportedHitInputsFor(c, attack.Id)!.Raw[DamageType.Lightning]);

            var use = Game.CaptureGemProjectile(c, b.Session, attack.Id, attack, (120, 100), new(0));
            var shot = Assert.IsType<ProjectileOffense>(use.Payload);
            Assert.Equal(hit, shot.Hit[DamageType.Lightning]);
            Assert.Equal(1, use.Profile.Count);
            b.Session.Runtime.Release(use);
            items.SwapWeaponSets();
            Assert.Equal("One Hand Sword", Game.WeaponFor(c).ItemClass);
            Assert.Equal(attack.Id, Game.GemSkillEffects(c).Single(g => g.Name == "Spectral Throw").Id);
            Assert.Equal(0, Game.SupportedHitInputsFor(c, attack.Id)!.Raw[DamageType.Lightning].Max);
            items.Remove(PlayerInventories.Belt, belt.Id);
            Assert.Equal(hit, shot.Hit[DamageType.Lightning]);
            // Advance only through the outgoing leg, before the existing Spectral Throw return begins.
            b.Session.AdvanceTo(ProjectileRuntime.Time(.4));
            Assert.InRange(b.Enemy.MaxLife - b.Enemy.Life, (uint)hit.Min, (uint)hit.Max);
            Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
        }
    }

    static void Weapon_cold_per_dexterity_stays_with_its_source()
    {
        const string minimum = "attacks_with_this_weapon_minimum_added_cold_damage_per_10_dexterity";
        const string maximum = "attacks_with_this_weapon_maximum_added_cold_damage_per_10_dexterity";
        var affix = Program.Items.Mods.ById("AddedColdDamagePerDexterityUber1")!;
        Assert.Equal(new[] { minimum, maximum }, affix.Stats.Select(s => s.Id));
        Assert.All(affix.Stats, s => Assert.False(s.Local));
        var source = new ItemMod(ModSection.Explicit, affix.Hash, [2, 4]);
        using (var b = new Battle("Rain of Arrows"))
        {
            var c = b.Conn;
            var items = Game.ItemsOf(c);
            var host = items.Items(PlayerInventories.Weapon1).Single();
            items.Change(PlayerInventories.Weapon1, host.Id, item => item with
            {
                Mods = [source],
                Sockets = [item.Sockets[0] with { Gem = item.Sockets[0].Gem! with
                { GemExperience = Program.Items.Gem(Program.Items.ByHash(item.Sockets[0].Gem!.TypeHash)!)!.ExperienceFor(2) } }]
            });
            var gem = Game.GemSkillEffects(c).Single();
            uint dexterity = Program.Stats.Key("dexterity")!.Value;
            foreach (var (dex, groups) in new[] { (9, 0), (10, 1), (19, 1), (20, 2), (1000, 100) })
            {
                c.PlayerStats[dexterity] = dex;
                Assert.Equal(dex, Game.CharacterPlayerStats(c)[dexterity]);
                var raw = Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Cold];
                Assert.Equal(groups * 2 * .49, raw.Min, 6);
                Assert.Equal(groups * 4 * .49, raw.Max, 6);
            }
            var weapon = Game.WeaponFor(c);
            Assert.Equal(0, weapon.LocalDamage![DamageType.Cold].Max);
            var character = Game.CharacterPlayerStats(c);
            int Get(string name) => Program.Stats.Key(name) is uint key ? character.GetValueOrDefault(key) : 0;
            Assert.Equal(new DamageRange(200, 400), SupportedAttackDamage.Raw(weapon, Get, new(0))[DamageType.Cold]);
            Assert.Equal(200, character[Program.Stats.Key("main_hand_minimum_cold_damage")!.Value]);
            Assert.Equal(400, character[Program.Stats.Key("main_hand_maximum_cold_damage")!.Value]);
            // A globally summed raw pair or an inactive set cannot supply another copy of this weapon's grant.
            c.PlayerStats[Program.Stats.Key(minimum)!.Value] = 999;
            c.PlayerStats[Program.Stats.Key(maximum)!.Value] = 999;
            var inactive = items.Add(PlayerInventories.Weapon2, 0, 0,
                ItemCatalog.Create(Program.Items.Find("Crude Bow")!) with { Mods = [source] });
            Assert.NotNull(inactive);
            Assert.Equal(98, Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Cold].Min, 6);
            c.PlayerStats[Program.Stats.Key("global_cannot_crit")!.Value] = 1;
            c.PlayerStats[Program.Stats.Key("base_deal_no_physical_damage")!.Value] = 1;
            c.PlayerStats[Program.Stats.Key("area_damage_+%")!.Value] = 100;
            c.PlayerStats[Program.Stats.Key("number_of_additional_projectiles")!.Value] = -1000;
            var use = Game.CaptureGemProjectile(c, b.Session, gem.Id, gem, (120, 100), new(0));
            var shot = Assert.IsType<ProjectileOffense>(use.Payload);
            Assert.Equal(new DamageRange(98, 196), shot.Hit[DamageType.Cold]);
            Assert.Equal(new DamageRange(196, 392), shot.AreaHit![DamageType.Cold]);
            var panel = Game.PlayerSkills(c).Single(s => s.Id == gem.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
            Assert.Equal(196, panel[Program.Stats.Key("main_hand_minimum_cold_damage")!.Value]);
            Assert.Equal(392, panel[Program.Stats.Key("main_hand_maximum_cold_damage")!.Value]);
            Game.ReleaseArrowRain(b.Session, gem.Id, gem, (120, 100), new(0), 0);
            items.Change(PlayerInventories.Weapon1, host.Id, item => item with { Mods = [] });
            c.PlayerStats[dexterity] = 0;
            Assert.Equal(0, Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Cold].Max);
            b.Session.AdvanceTo(ProjectileRuntime.Time(.4));
            Assert.InRange(100000u - b.Enemy.Life, 196u, 392u);
            Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
            items.Change(PlayerInventories.Weapon1, host.Id, item => item with { Mods = [source] });
            c.Experience = CharacterStats.ExperienceFor(1);
            Game.CharacterStatSources(c, level: 1);
            Assert.Null(Game.ActiveMainHand(c));
            Assert.Equal(0, Game.WeaponFor(c).LocalSources?.GetValueOrDefault(minimum) ?? 0);
            c.Experience = CharacterStats.ExperienceFor(100);
            Game.CharacterStatSources(c, level: 100);
            Assert.Equal(2, Game.WeaponFor(c).LocalSources!.GetValueOrDefault(minimum));
        }
        using (var b = new Battle("Double Strike", "Fireball", "Spellblade Support"))
        {
            var c = b.Conn;
            c.PlayerStats[Program.Stats.Key("dexterity")!.Value] = 1000;
            var items = Game.ItemsOf(c);
            var host = items.Items(PlayerInventories.Weapon1).Single();
            items.Change(PlayerInventories.Weapon1, host.Id, item => ItemCatalog.Create(Program.Items.Find("Rusted Sword")!) with
            { Sockets = item.Sockets, Links = item.Links, Mods = [source] });
            var off = items.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find("Rusted Sword")!))!;
            var attack = Game.GemSkillEffects(c).Single(g => Game.SkillsFor(c).IsAttack(g.Effect));
            var spell = Game.GemSkillEffects(c).Single(g => Game.SkillsFor(c).IsSpell(g.Effect));
            uint spellblade = Program.Stats.Key("support_spell_weapon_damage_gain_%_of_one_hand_melee_weapon_damage_as_added_spell_damage")!.Value;
            Assert.True(Game.SupportedSkill(c, spell.Id).Stats.GetValueOrDefault(spellblade) > 0);
            Assert.True(Game.SupportedHitInputsFor(c, attack.Id)!.Raw[DamageType.Cold].Max > 0);
            Assert.Equal(0, Game.SupportedHitInputsFor(c, attack.Id, weapon: Game.OffhandWeaponFor(c))!.Raw[DamageType.Cold].Max);
            Assert.Equal(0, Game.SupportedHitInputsFor(c, spell.Id)!.Raw[DamageType.Cold].Max);
            items.Change(PlayerInventories.Weapon1, host.Id, item => item with { Mods = [] });
            items.Change(PlayerInventories.Offhand1, off.Id, item => item with { Mods = [source] });
            Assert.Equal(0, Game.SupportedHitInputsFor(c, attack.Id)!.Raw[DamageType.Cold].Max);
            Assert.True(Game.SupportedHitInputsFor(c, attack.Id, weapon: Game.OffhandWeaponFor(c))!.Raw[DamageType.Cold].Max > 0);
            Assert.Equal(0, Game.SupportedHitInputsFor(c, spell.Id)!.Raw[DamageType.Cold].Max);
        }
    }

    static void Spellblade_takes_weapon_damage_without_global_attack_inputs()
    {
        using var b = new Battle("Fireball", "Spellblade Support");
        var c = b.Conn;
        static uint Key(string name) => Program.Stats.Key(name)!.Value;
        c.PlayerStats[Key("global_cannot_crit")] = 1;
        c.PlayerStats[Key("base_deal_no_physical_damage")] = 1;
        c.PlayerStats[Key("base_deal_no_fire_damage")] = 1;
        var items = Game.ItemsOf(c);
        var host = items.Items(PlayerInventories.Weapon1).Single();
        var local = Program.Items.Mods.ById("LocalAddedLightningDamage1")!;
        items.Change(PlayerInventories.Weapon1, host.Id, item => ItemCatalog.Create(Program.Items.Find("Rusted Sword")!) with
        {
            Rarity = ItemRarity.Magic, Sockets = item.Sockets, Links = item.Links,
            Mods = [new(ModSection.Explicit, local.Hash, [1, 5])],
        });
        var gem = Game.GemSkillEffects(c).Single();
        Assert.Equal(40, Game.SupportedSkill(c, gem.Id).Stats[Key(
            "support_spell_weapon_damage_gain_%_of_one_hand_melee_weapon_damage_as_added_spell_damage")]);
        Assert.Equal(new DamageRange(1, 5), Game.WeaponFor(c).LocalDamage![DamageType.Lightning]);
        var localContribution = Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning];
        Assert.True(localContribution.Max > 0);
        int PanelMaximum() => Game.PlayerSkills(c).Single(s => s.Id == gem.Id).Stats!
            .Single(s => s.Key == Key("main_hand_maximum_lightning_damage")).Value;
        int localPanel = PanelMaximum();

        // This real ring prefix adds lightning to attacks, not to the weapon or to Spellblade's spell.
        var attackOnly = Program.Items.Mods.ById("AddedLightningDamage1")!;
        Assert.All(attackOnly.Stats, stat => Assert.False(stat.Local));
        var ring = items.Add(6, 0, 0, ItemCatalog.Create(Program.Items.Find("Coral Ring")!) with
        { Rarity = ItemRarity.Magic, Mods = [new(ModSection.Explicit, attackOnly.Hash, [1, 5])] })!;
        var character = Game.CharacterPlayerStats(c);
        Assert.Equal(1, character[Key("attack_minimum_added_lightning_damage")]);
        Assert.Equal(5, character[Key("attack_maximum_added_lightning_damage")]);
        Assert.Equal(new DamageRange(2, 10), Game.AttackHit(Game.WeaponFor(c), character, melee: true, accuracy: new(Game.PlayerAccuracy(c, character)))[DamageType.Lightning]);
        Assert.Equal(localContribution, Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning]);
        Assert.Equal(localPanel, PanelMaximum());
        items.Remove(6, ring.Id);

        // Circle of Regret's generic global lightning already enters an unsupported spell once.
        var generic = new UniqueCatalog(Program.Items).Create("Circle of Regret", new Random(17))!;
        var circle = items.Add(6, 0, 0, generic)!;
        items.Change(PlayerInventories.Weapon1, host.Id, item => item with { Links = [1, 1] });
        var globalContribution = Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning];
        Assert.True(globalContribution.Max > 0);
        items.Change(PlayerInventories.Weapon1, host.Id, item => item with { Links = [2] });
        var expected = localContribution.Add(globalContribution);
        Assert.Equal(expected, Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning]);
        Assert.Equal((int)expected.Max, PanelMaximum());
        Assert.Equal(new DamageRange(1, 5), Game.WeaponFor(c).LocalDamage![DamageType.Lightning]);

        var use = Game.CaptureGemProjectile(c, b.Session, gem.Id, gem, (120, 100), new(0));
        var shot = Assert.IsType<ProjectileOffense>(use.Payload);
        Assert.Equal(expected, shot.Hit[DamageType.Lightning]);
        Assert.Equal(expected, shot.AreaHit![DamageType.Lightning]);
        Assert.Equal(expected.Min, shot.Hit.MinTotal);
        Assert.Equal(expected.Max, shot.Hit.MaxTotal);
        b.Session.Runtime.Release(use);
        items.Remove(6, circle.Id);
        items.Change(PlayerInventories.Weapon1, host.Id, item => item with { Links = [1, 1] });
        Assert.Equal(0, Game.SupportedHit(c, gem.Id)[DamageType.Lightning].Max);
        b.Session.AdvanceTo(ProjectileRuntime.Time(1));
        Assert.InRange(b.Enemy.MaxLife - b.Enemy.Life, (uint)expected.Min, (uint)expected.Max);
        Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
        Assert.Equal(expected, shot.Hit[DamageType.Lightning]);
    }

    static void Spellblade_requires_an_active_one_hand_melee_weapon()
    {
        using var b = new Battle("Fireball", "Spellblade Support");
        var c = b.Conn;
        static uint Key(string name) => Program.Stats.Key(name)!.Value;
        c.PlayerStats[Key("global_cannot_crit")] = 1;
        c.PlayerStats[Key("base_deal_no_physical_damage")] = 1;
        c.PlayerStats[Key("base_deal_no_fire_damage")] = 1;
        var items = Game.ItemsOf(c);
        var host = items.Items(PlayerInventories.Weapon1).Single();
        // Keep the spell and support active independently of the equipped weapon and its requirements.
        items.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
        { Sockets = host.Item.Sockets, Links = host.Item.Links });
        items.Remove(PlayerInventories.Weapon1, host.Id);
        var gem = Game.GemSkillEffects(c).Single();
        DamageRange Lightning() => Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning];
        Assert.Equal(0, Lightning().Max);

        var local = Program.Items.Mods.ById("LocalAddedLightningDamage1")!;
        Item Weapon(string name) => ItemCatalog.Create(Program.Items.Find(name)!) with
        { Rarity = ItemRarity.Magic, Mods = [new(ModSection.Explicit, local.Hash, [1, 5])] };
        var main = items.Add(PlayerInventories.Weapon1, 0, 0, Weapon("Rusted Sword"))!;
        var transferred = Lightning();
        Assert.True(transferred.Max > 0);
        foreach (var (name, eligible) in new[]
        {
            ("Nailed Fist", true), ("Glass Shank", true), ("Carving Knife", true), ("Rusted Sword", true),
            ("Rusted Spike", true), ("Rusted Hatchet", true), ("Driftwood Club", true), ("Driftwood Sceptre", true),
            ("Corroded Blade", false), ("Stone Axe", false), ("Driftwood Maul", false),
            ("Gnarled Branch", false), ("Iron Staff", false), ("Driftwood Wand", false), ("Crude Bow", false),
        })
        {
            items.Change(PlayerInventories.Weapon1, main.Id, _ => Weapon(name));
            Assert.Equal(gem.Id, Game.GemSkillEffects(c).Single().Id);
            Assert.Equal(new DamageRange(1, 5), Game.WeaponFor(c).LocalDamage![DamageType.Lightning]);
            var expected = eligible ? transferred : DamageRange.Zero;
            Assert.True(expected == Lightning(), $"{name}: expected {expected}, actual {Lightning()}");
            var panel = Game.PlayerSkills(c).Single(s => s.Id == gem.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
            Assert.Equal((int)expected.Max, panel.GetValueOrDefault(Key("main_hand_maximum_lightning_damage")));
        }

        items.Change(PlayerInventories.Weapon1, main.Id, _ => Weapon("Rusted Sword"));
        var shield = items.Add(PlayerInventories.Offhand1, 0, 0,
            ItemCatalog.Create(Program.Items.Find("Splintered Tower Shield")!))!;
        Assert.Equal(transferred, Lightning());
        items.Remove(PlayerInventories.Offhand1, shield.Id);
        items.Add(PlayerInventories.Weapon2, 0, 0, Weapon("Gnarled Branch"));
        var use = Game.CaptureGemProjectile(c, b.Session, gem.Id, gem, (120, 100), new(0));
        var shot = Assert.IsType<ProjectileOffense>(use.Payload);
        Assert.Equal(transferred, shot.Hit[DamageType.Lightning]);
        Assert.Equal(transferred, shot.AreaHit![DamageType.Lightning]);
        b.Session.Runtime.Release(use);
        items.SwapWeaponSets();
        Assert.Equal(0, Lightning().Max);
        Assert.Equal(gem.Id, Game.GemSkillEffects(c).Single().Id);
        b.Session.AdvanceTo(ProjectileRuntime.Time(1));
        Assert.InRange(b.Enemy.MaxLife - b.Enemy.Life, (uint)transferred.Min, (uint)transferred.Max);
        Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
        Assert.Equal(transferred, shot.Hit[DamageType.Lightning]);
        items.SwapWeaponSets();
        Assert.Equal(transferred, Lightning());

        items.Change(PlayerInventories.Weapon1, main.Id, _ => Weapon("Jewelled Foil"));
        c.Experience = CharacterStats.ExperienceFor(50);
        c.Pools = Game.PoolsFor(c, 50);
        Assert.Null(Game.ActiveMainHand(c));
        Assert.Equal(40, Game.SupportedSkill(c, gem.Id).Stats[Key(
            "support_spell_weapon_damage_gain_%_of_one_hand_melee_weapon_damage_as_added_spell_damage")]);
        Assert.Equal(0, Lightning().Max);
        c.Experience = CharacterStats.ExperienceFor(100);
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(transferred, Lightning());
    }

    static void Spellblade_transfers_each_active_weapon_before_spell_scaling()
    {
        using var b = new Battle("Fireball", "Spellblade Support");
        var c = b.Conn;
        static uint Key(string name) => Program.Stats.Key(name)!.Value;
        c.PlayerStats[Key("global_cannot_crit")] = 1;
        c.PlayerStats[Key("base_deal_no_physical_damage")] = 1;
        c.PlayerStats[Key("base_deal_no_fire_damage")] = 1;
        var items = Game.ItemsOf(c);
        var host = items.Items(PlayerInventories.Weapon1).Single();
        var body = items.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
        { Sockets = host.Item.Sockets, Links = host.Item.Links })!;
        items.Remove(PlayerInventories.Weapon1, host.Id);
        var gem = Game.GemSkillEffects(c).Single();
        Item Weapon(string name, string mod, int min, int max) => ItemCatalog.Create(Program.Items.Find(name)!) with
        {
            ItemLevel = 100, Rarity = ItemRarity.Magic,
            Mods = [new(ModSection.Explicit, Program.Items.Mods.ById(mod)!.Hash, [min, max])],
        };
        items.Add(PlayerInventories.Weapon1, 0, 0, Weapon("Rusted Sword", "LocalAddedColdDamage2", 7, 14));
        var off = items.Add(PlayerInventories.Offhand1, 0, 0,
            Weapon("Rusted Sword", "LocalAddedLightningDamage2", 2, 25))!;
        DamageRange Cold() => Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Cold];
        // 40% support * 60% per hand: floor each endpoint, then apply Fireball's 300% effectiveness.
        Assert.Equal(new DamageRange(0, 18), Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning]);
        Assert.Equal(new DamageRange(3, 9), Cold());
        items.Change(PlayerInventories.Offhand1, off.Id,
            _ => Weapon("Rusted Sword", "LocalAddedColdDamage2", 9, 16));
        // Separate floors: (1+2, 3+3) * 3. Combining hands before rounding would produce 9-21.
        Assert.Equal(new DamageRange(9, 18), Cold());
        void SupportLevel(int level) => items.Change(PlayerInventories.BodyArmour, body.Id, item =>
        {
            var sockets = item.Sockets.ToArray();
            var support = sockets[1].Gem!;
            sockets[1] = sockets[1] with { Gem = support with
            { GemExperience = Program.Items.Gem(Program.Items.ByHash(support.TypeHash)!)!.ExperienceFor(level) } };
            return item with { Sockets = sockets };
        });
        SupportLevel(20);
        Assert.Equal(135, Game.SupportedSkill(c, gem.Id).Stats[Key(
            "support_spell_weapon_damage_gain_%_of_one_hand_melee_weapon_damage_as_added_spell_damage")]);
        Assert.Equal(new DamageRange(36, 69), Cold());
        SupportLevel(1);

        items.Remove(PlayerInventories.Offhand1, off.Id);
        Assert.Equal(new DamageRange(6, 15), Cold());
        var shield = items.Add(PlayerInventories.Offhand1, 0, 0,
            ItemCatalog.Create(Program.Items.Find("Splintered Tower Shield")!))!;
        Assert.Equal(new DamageRange(6, 15), Cold());
        items.Remove(PlayerInventories.Offhand1, shield.Id);
        off = items.Add(PlayerInventories.Offhand1, 0, 0,
            Weapon("Jewelled Foil", "LocalAddedColdDamage2", 9, 16))!;
        c.Experience = CharacterStats.ExperienceFor(50);
        c.Pools = Game.PoolsFor(c, 50);
        Assert.Null(Game.ActiveOffHand(c));
        Assert.Equal(40, Game.SupportedSkill(c, gem.Id).Stats[Key(
            "support_spell_weapon_damage_gain_%_of_one_hand_melee_weapon_damage_as_added_spell_damage")]);
        Assert.Equal(new DamageRange(6, 15), Cold());
        c.Experience = CharacterStats.ExperienceFor(100);
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(new DamageRange(9, 18), Cold());

        items.Add(PlayerInventories.Weapon2, 0, 0, ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
        items.Add(PlayerInventories.Offhand2, 0, 0, ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
        c.PlayerStats[Key("cold_damage_+%")] = 100;
        var expected = new DamageRange(18, 36);
        var panel = Game.PlayerSkills(c).Single(s => s.Id == gem.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal(18, panel[Key("main_hand_minimum_cold_damage")]);
        Assert.Equal(36, panel[Key("main_hand_maximum_cold_damage")]);
        var use = Game.CaptureGemProjectile(c, b.Session, gem.Id, gem, (120, 100), new(0));
        var shot = Assert.IsType<ProjectileOffense>(use.Payload);
        Assert.Equal(expected, shot.Hit[DamageType.Cold]);
        Assert.Equal(expected, shot.AreaHit![DamageType.Cold]);
        b.Session.Runtime.Release(use);
        items.SwapWeaponSets();
        Assert.Equal(gem.Id, Game.GemSkillEffects(c).Single().Id);
        Assert.Equal(0, Cold().Max);
        b.Session.AdvanceTo(ProjectileRuntime.Time(1));
        Assert.InRange(b.Enemy.MaxLife - b.Enemy.Life, 18u, 36u);
        Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
        Assert.Equal(expected, shot.Hit[DamageType.Cold]);
    }

    static void Accuracy_boots_use_resolved_skill_and_hand_inputs()
    {
        using var b = new Battle("Kinetic Blast", "Additional Accuracy Support", "Fireball", "Spellblade Support");
        var c = b.Conn;
        static uint Key(string name) => Program.Stats.Key(name)!.Value;
        c.PlayerStats[Key("global_cannot_crit")] = 1;
        c.PlayerStats[Key("base_deal_no_physical_damage")] = 1;
        c.PlayerStats[Key("spell_damage_+%")] = 0;
        c.PlayerStats[Key("accuracy_rating_+%")] = 100;
        c.PlayerStats[Key("attack_speed_+%_per_200_accuracy_rating")] = 1;
        var items = Game.ItemsOf(c);
        var host = items.Items(PlayerInventories.Weapon1).Single();
        var sockets = host.Item.Sockets.ToArray();
        foreach (int index in new[] { 0, 1 })
        {
            var socketGem = sockets[index].Gem!;
            sockets[index] = sockets[index] with { Gem = socketGem with
            { GemExperience = Program.Items.Gem(Program.Items.ByHash(socketGem.TypeHash)!)!.ExperienceFor(2) } };
        }
        var body = items.Add(PlayerInventories.BodyArmour, 0, 0, ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
        { ItemLevel = 100, Sockets = sockets, Links = [2, 2] })!;
        items.Change(PlayerInventories.Weapon1, host.Id, _ => ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!));
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(456, Game.PlayerAccuracy(c));
        Assert.Equal(3, c.Pools.AttackSpeedPct); // This existing stat grants speed per complete 150 accuracy.
        Item Boots(string name, string mod) => ItemCatalog.Create(Program.Items.Find(name)!) with
        { ItemLevel = 100, Mods = [new(ModSection.Implicit, Program.Items.Mods.ById(mod)!.Hash, [-25, 1, 6])] };
        var boots = items.Add(PlayerInventories.Boots, 0, 0,
            Boots("Cloudwhisper Boots", "AddedLightningDamagePerAccuracyReducedAccuracyImplicitR1_"))!;
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(342, Game.PlayerAccuracy(c));
        Assert.Equal(2, c.Pools.AttackSpeedPct);
        c.PlayerStats.Remove(Key("attack_speed_+%_per_200_accuracy_rating"));
        c.Pools = Game.PoolsFor(c, 100);
        var gem = Game.GemSkillEffects(c).Single(g => g.Name == "Kinetic Blast");
        var spell = Game.GemSkillEffects(c).Single(g => g.Name == "Fireball");
        Assert.Equal(2, gem.Level);
        Assert.Equal(100, Game.SupportedSkill(c, gem.Id).Stats[Key("accuracy_rating")]);
        Assert.Equal(492, Game.SupportedAccuracy(c, gem.Id).Rating); // (228 + 100) * 2 * .75.
        Assert.Equal(new DamageRange(2.82, 16.92), Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning]);
        Assert.Equal(0, Game.SupportedHitInputsFor(c, spell.Id)!.Raw[DamageType.Lightning].Max);
        // An eligible Spellblade weapon still does not transfer this global attack-only grant.
        items.Change(PlayerInventories.Weapon1, host.Id, _ => ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(0, Game.SupportedHitInputsFor(c, spell.Id)!.Raw[DamageType.Lightning].Max);
        items.Change(PlayerInventories.Weapon1, host.Id, _ => ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!));
        items.Change(PlayerInventories.BodyArmour, body.Id, item => item with
        { Sockets = [item.Sockets[0], item.Sockets[1] with { Gem = item.Sockets[1].Gem! with { Quality = 20 } }, .. item.Sockets.Skip(2)] });
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(20, Game.SupportedSkill(c, gem.Id).Stats[Key("accuracy_rating_+%")]);
        Assert.Equal(541, Game.SupportedAccuracy(c, gem.Id).Rating); // floor(328 * 2.2 * .75).
        var player = Game.CharacterPlayerStats(c);
        int Get(string name) => Program.Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        foreach (var (rating, expected) in new[]
        {
            (199, new DamageRange(0, 0)), (200, new DamageRange(1, 6)),
            (399, new DamageRange(1, 6)), (400, new DamageRange(2, 12)),
        })
        {
            // Target modifiers and cannot-evade do not supply extra rating to the grant.
            var accuracy = new AttackAccuracyProfile(rating, MoreVsUnique: 100, MoreInCloseRange: 100, CannotBeEvaded: true);
            Assert.Equal(expected, SupportedAttackDamage.Raw(Game.WeaponFor(c), Get, accuracy)[DamageType.Lightning]);
            Assert.Equal(expected.Scale(1.5), SupportedAttackDamage.Raw(Game.WeaponFor(c), Get, accuracy, 20_000, 15_000)[DamageType.Lightning]);
        }
        Item AccurateWand() => ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with
        {
            ItemLevel = 100, Rarity = ItemRarity.Magic,
            Mods = [new(ModSection.Explicit, Program.Items.Mods.ById("LocalIncreasedAccuracy5")!.Hash, [153])],
        };
        items.Add(PlayerInventories.Weapon2, 0, 0, AccurateWand());
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(541, Game.SupportedAccuracy(c, gem.Id).Rating);
        items.Add(PlayerInventories.Offhand1, 0, 0, AccurateWand());
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(541, Game.SupportedAccuracy(c, gem.Id).Rating);
        var off = Game.OffhandWeaponFor(c)!;
        Assert.Equal(793, Game.WeaponAccuracyFor(c, off, Game.CharacterPlayerStats(c), Game.SupportedSkill(c, gem.Id).Stats));
        // An off-hand swing (main's hand alternation) keeps the boots' less-accuracy factor.
        Assert.Equal(793, Game.SupportedAccuracy(c, gem.Id, AttackHand.Off).Rating);
        Assert.Equal(571, Game.HandAccuracy(c, AttackHand.Off, Game.CharacterPlayerStats(c)));   // floor((228 + 153) * 2 * .75)
        Assert.Equal(new DamageRange(4.23, 25.38), Game.SupportedHitInputsFor(c, gem.Id, weapon: off)!.Raw[DamageType.Lightning]);
        var panel = Game.PlayerSkills(c).Single(s => s.Id == gem.Id).Stats!.ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal(541, panel[Key("main_hand_accuracy_rating")]);
        Assert.Equal(793, panel[Key("off_hand_accuracy_rating")]);
        Assert.Equal(16, panel[Key("main_hand_maximum_lightning_damage")]);
        Assert.Equal(25, panel[Key("off_hand_maximum_lightning_damage")]);
        items.Change(PlayerInventories.Boots, boots.Id,
            _ => Boots("Stormrider Boots", "AddedLightningDamagePerAccuracyReducedAccuracyImplicitR3"));
        c.Experience = CharacterStats.ExperienceFor(50);
        c.Pools = Game.PoolsFor(c, 50);
        Assert.True(Game.IsInactive(c, boots.Id));
        Assert.Equal(0, Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning].Max);
        c.Experience = CharacterStats.ExperienceFor(100);
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(new DamageRange(2.82, 16.92), Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning]);
        c.PlayerStats[Key("lightning_damage_+%")] = 100;
        var expectedHit = new DamageRange(5.64, 33.84);
        var use = Game.CaptureGemProjectile(c, b.Session, gem.Id, gem, (120, 100), new(0));
        var shot = Assert.IsType<ProjectileOffense>(use.Payload);
        Assert.Equal(541, shot.AccuracyProfile!.Value.Rating);
        Assert.Equal(expectedHit, shot.Hit[DamageType.Lightning]);
        Assert.Equal(expectedHit, shot.AreaHit![DamageType.Lightning]);
        b.Session.Runtime.Release(use);
        items.SwapWeaponSets();
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(gem.Id, Game.GemSkillEffects(c).Single(g => g.Name == "Kinetic Blast").Id);
        Assert.Equal(793, Game.SupportedAccuracy(c, gem.Id).Rating);
        Assert.Equal(new DamageRange(4.23, 25.38), Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning]);
        // Preserve the fraction until both more multipliers are applied: floor(381 * .75 * .7) = 200, not 199.
        c.PlayerStats.Remove(Key("accuracy_rating_+%"));
        items.Change(PlayerInventories.BodyArmour, body.Id, item => item with { Links = [1, 1, 2] });
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(285, Game.PlayerAccuracy(c));
        // The fixture's clock is paused; seed the timed effect without exercising live-action admission.
        var effects = Game.ResourceEffectsOf(c);
        Assert.True(Game.ResourceEffectsCurrent(c, effects));
        Assert.True(effects.PlayerBlind.Inflict(Game.ResourceEffectsTime(c), 10, effectPercent: 50, immune: false));
        Assert.Equal(200, Game.PlayerAccuracy(c));
        Assert.Equal(200, Game.SupportedAccuracy(c, gem.Id).Rating);
        Assert.Equal(new DamageRange(1.41, 8.46), Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning]);
        items.Remove(PlayerInventories.Boots, boots.Id);
        c.Pools = Game.PoolsFor(c, 100);
        Assert.Equal(0, Game.SupportedHitInputsFor(c, gem.Id)!.Raw[DamageType.Lightning].Max);
        b.Session.AdvanceTo(ProjectileRuntime.Time(1));
        Assert.InRange(b.Enemy.MaxLife - b.Enemy.Life, 5u, 33u);
        Assert.Equal(b.Ally.MaxLife, b.Ally.Life);
        Assert.Equal(expectedHit, shot.Hit[DamageType.Lightning]);
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

