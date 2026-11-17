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
                typeof(Game).GetMethod("GolemAbilityContactLocked", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                    .Invoke(null, ["test", b.Stream, b.Ciphers, c, clone,
                        new GolemState.Action(rain, 200, b.Enemy, (120, 100), 0, clone.Monster.ActionGeneration), 1d]);
                Assert.Equal(11, clone.Pulses.Count);
                Assert.All(clone.Pulses, p => Assert.True(p.Next >= 1.35));
                clone.Monster.Mods = MonsterModSet.None with
                    { Effects = new(new Dictionary<string, int> { ["accuracy_rating"] = 1000000 }) };
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
                    typeof(Game).GetMethod("MinionAilmentsLocked", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                        .Invoke(null, [c, clone, attack, 999u, ailmentTarget, true, 1d]);
                    Assert.False(ailmentTarget.Poisons.Poisoned);
                    Assert.Equal(element == DamageType.Fire, ailmentTarget.Ignites.Burning);
                }
            }
        }
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
    public void Attack_coefficients_separate_weapon_base_from_added_damage(string skill, int basis, int added, double expected)
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
        Claw_and_dagger_projectiles_keep_the_released_hand_and_source();
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

